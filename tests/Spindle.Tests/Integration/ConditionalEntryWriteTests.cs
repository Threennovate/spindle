using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Spindle.Api.TimeEntries;

namespace Spindle.Tests.Integration;

/// <summary>Writes that carry the version the client saw (<c>If-Match</c>) fail instead of overwriting a newer change.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ConditionalEntryWriteTests(PostgresApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    [Fact]
    public async Task A_write_with_the_version_seen_goes_through_and_gets_a_new_version()
    {
        var user = await TestUser.RegisterAsync(factory);
        var entry = await CreateAsync(user, "Draft");

        var response = await SendAsync(user, HttpMethod.Put, $"/time-entries/{entry.Id}", entry.Version, Body(entry, "Final"));

        var saved = await TestUser.ReadAsync<TimeEntryResponse>(response);
        Assert.Equal("Final", saved.Description);
        Assert.NotEqual(entry.Version, saved.Version);
        Assert.Equal(saved.Version, (await user.GetAsync<TimeEntryResponse>($"/time-entries/{entry.Id}")).Version);
    }

    [Fact]
    public async Task The_version_may_be_sent_bare_as_well_as_an_entity_tag()
    {
        var user = await TestUser.RegisterAsync(factory);
        var entry = await CreateAsync(user, "Draft");
        var request = new HttpRequestMessage(HttpMethod.Put, user.In($"/time-entries/{entry.Id}")) { Content = JsonContent.Create(Body(entry, "Final")) };
        // As the Swift client sends it: its OpenAPI runtime would percent-encode the quotes.
        request.Headers.TryAddWithoutValidation("If-Match", entry.Version);

        var saved = await TestUser.ReadAsync<TimeEntryResponse>(await user.Client.SendAsync(request, Ct));

        Assert.Equal("Final", saved.Description);
        var stale = await SendAsync(user, HttpMethod.Delete, $"/time-entries/{entry.Id}", entry.Version);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task A_write_with_an_old_version_is_refused_with_the_entry_as_it_is_now()
    {
        var user = await TestUser.RegisterAsync(factory);
        var entry = await CreateAsync(user, "Draft");
        var elsewhere = await TestUser.ReadAsync<TimeEntryResponse>(await user.PutAsync($"/time-entries/{entry.Id}", Body(entry, "Changed elsewhere")));

        var response = await SendAsync(user, HttpMethod.Put, $"/time-entries/{entry.Id}", entry.Version, Body(entry, "Changed offline"));

        var problem = await ReadChangedAsync(response);
        Assert.Equal("Changed elsewhere", problem.Current.Description);
        Assert.Equal(elsewhere.Version, problem.Current.Version);
        Assert.Equal("Changed elsewhere", (await user.GetAsync<TimeEntryResponse>($"/time-entries/{entry.Id}")).Description);
    }

    [Fact]
    public async Task A_write_without_a_condition_overwrites_as_before()
    {
        var user = await TestUser.RegisterAsync(factory);
        var entry = await CreateAsync(user, "Draft");
        await user.PutAsync($"/time-entries/{entry.Id}", Body(entry, "Changed elsewhere"));

        var response = await user.PutAsync($"/time-entries/{entry.Id}", Body(entry, "Last write"));

        Assert.Equal("Last write", (await TestUser.ReadAsync<TimeEntryResponse>(response)).Description);
        Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync($"/time-entries/{entry.Id}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_an_entry_changed_since_is_refused_and_keeps_it()
    {
        var user = await TestUser.RegisterAsync(factory);
        var entry = await CreateAsync(user, "Draft");
        var elsewhere = await TestUser.ReadAsync<TimeEntryResponse>(await user.PutAsync($"/time-entries/{entry.Id}", Body(entry, "Changed elsewhere")));

        var stale = await SendAsync(user, HttpMethod.Delete, $"/time-entries/{entry.Id}", entry.Version);

        Assert.Equal("Changed elsewhere", (await ReadChangedAsync(stale)).Current.Description);
        Assert.Equal(entry.Id, (await user.GetAsync<TimeEntryResponse>($"/time-entries/{entry.Id}")).Id);
        var current = await SendAsync(user, HttpMethod.Delete, $"/time-entries/{entry.Id}", elsewhere.Version);
        Assert.Equal(HttpStatusCode.NoContent, current.StatusCode);
    }

    [Fact]
    public async Task Stopping_with_an_old_version_is_refused_unless_it_already_stopped_there()
    {
        var user = await TestUser.RegisterAsync(factory);
        var timer = await user.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Timer", start = Now.AddMinutes(-30) });
        var at = Now.AddMinutes(-5);
        await user.PutAsync($"/time-entries/{timer.Id}", new { description = "Renamed elsewhere", start = timer.Start });

        var stale = await SendAsync(user, HttpMethod.Post, $"/time-entries/{timer.Id}/stop", timer.Version, new { stop = at });
        var current = (await ReadChangedAsync(stale)).Current;
        Assert.Null(current.Stop);

        var stopped = await TestUser.ReadAsync<TimeEntryResponse>(
            await SendAsync(user, HttpMethod.Post, $"/time-entries/{timer.Id}/stop", current.Version, new { stop = at }));
        // The same stop again after a lost answer, with the version it was sent with.
        var again = await SendAsync(user, HttpMethod.Post, $"/time-entries/{timer.Id}/stop", current.Version, new { stop = at });

        Assert.Equal(stopped.Stop, (await TestUser.ReadAsync<TimeEntryResponse>(again)).Stop);
        Assert.Equal("Renamed elsewhere", stopped.Description);
    }

    [Fact]
    public async Task Of_two_writes_with_the_same_version_at_the_same_moment_one_goes_through()
    {
        var user = await TestUser.RegisterAsync(factory);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var entry = await CreateAsync(user, $"Draft {attempt}");

            var responses = await Task.WhenAll(
                SendAsync(user, HttpMethod.Put, $"/time-entries/{entry.Id}", entry.Version, Body(entry, "One")),
                SendAsync(user, HttpMethod.Put, $"/time-entries/{entry.Id}", entry.Version, Body(entry, "Two")),
                SendAsync(user, HttpMethod.Delete, $"/time-entries/{entry.Id}", entry.Version));

            Assert.Single(responses, r => r.IsSuccessStatusCode);
            Assert.All(responses.Where(r => !r.IsSuccessStatusCode), r =>
                Assert.Contains(r.StatusCode, new[] { HttpStatusCode.PreconditionFailed, HttpStatusCode.NotFound }));
        }
    }

    private static Task<TimeEntryResponse> CreateAsync(TestUser user, string description) =>
        user.CreateAsync<TimeEntryResponse>("/time-entries", new { description, start = Now.AddHours(-2), stop = Now.AddHours(-1) });

    private static object Body(TimeEntryResponse entry, string description) =>
        new { description, start = entry.Start, stop = entry.Stop };

    private static Task<HttpResponseMessage> SendAsync(TestUser user, HttpMethod method, string path, string version, object? body = null)
    {
        var request = new HttpRequestMessage(method, user.In(path)) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        return user.Client.SendAsync(request, Ct);
    }

    private static async Task<TimeEntryChangedProblem> ReadChangedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<TimeEntryChangedProblem>(TestJson.Options, Ct))!;
        Assert.Equal("urn:spindle:problem:entry-changed", problem.Type);
        return problem;
    }
}
