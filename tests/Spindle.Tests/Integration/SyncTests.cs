using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Spindle.Api.Projects;
using Spindle.Api.Sync;
using Spindle.Api.TimeEntries;

namespace Spindle.Tests.Integration;

/// <summary>The apps hear about changes made elsewhere: on another device, or by a teammate.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SyncTests(PostgresApiFactory factory)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_persons_devices_hear_about_their_entries()
    {
        var user = await TestUser.RegisterAsync(factory);
        await using var laptop = await ConnectAsync(user);

        await user.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Started on the phone", start = DateTimeOffset.UtcNow.AddMinutes(-1) });

        Assert.Equal(new SyncChange("entries", user.WorkspaceId, user.UserId), await laptop.NextAsync(c => c.Topic == "entries"));
    }

    [Fact]
    public async Task Watchers_of_a_workspace_hear_about_its_changes()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        var stranger = await TestUser.RegisterAsync(factory);
        await using var adminApp = await ConnectAsync(admin);
        await using var strangerApp = await ConnectAsync(stranger);

        Assert.True(await adminApp.Connection.InvokeAsync<bool>("Watch", admin.WorkspaceId, Ct));
        Assert.False(await strangerApp.Connection.InvokeAsync<bool>("Watch", admin.WorkspaceId, Ct));
        await member.CreateAsync<ProjectResponse>("/projects", new { name = "Website", color = "#0B83D9" });
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Design", start = DateTimeOffset.UtcNow.AddHours(-1), stop = DateTimeOffset.UtcNow });

        Assert.Equal(new SyncChange("catalog", admin.WorkspaceId, null), await adminApp.NextAsync(c => c.Topic == "catalog"));
        Assert.Equal(new SyncChange("entries", admin.WorkspaceId, member.UserId), await adminApp.NextAsync(c => c.Topic == "entries"));
        Assert.Empty(await strangerApp.AllWithinAsync(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task Only_the_persons_own_devices_hear_about_their_agents_proposals()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        await using var memberApp = await ConnectAsync(member);
        await using var adminApp = await ConnectAsync(admin);
        Assert.True(await adminApp.Connection.InvokeAsync<bool>("Watch", admin.WorkspaceId, Ct));

        (await member.PostAsync("/agent-proposals/entries", new { entries = new[] { new { start = "2026-09-14 09:00", stop = "2026-09-14 10:00" } } }))
            .EnsureSuccessStatusCode();

        Assert.Equal(new SyncChange("agentProposals", admin.WorkspaceId, member.UserId), await memberApp.NextAsync(c => c.Topic == "agentProposals"));
        Assert.DoesNotContain(await adminApp.AllWithinAsync(TimeSpan.FromMilliseconds(500)), c => c.Topic == "agentProposals");
    }

    [Fact]
    public async Task People_hear_when_their_membership_changes()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin);
        await using var memberApp = await ConnectAsync(member);

        (await admin.PutAsync($"/members/{member.UserId}", new { role = "admin" })).EnsureSuccessStatusCode();

        Assert.Equal(new SyncChange("me", null, member.UserId), await memberApp.NextAsync(c => c.Topic == "me"));
    }

    [Fact]
    public async Task The_device_that_made_a_change_is_not_told_about_it()
    {
        var user = await TestUser.RegisterAsync(factory);
        await using var laptop = await ConnectAsync(user);
        await using var phone = await ConnectAsync(user);

        var request = new HttpRequestMessage(HttpMethod.Post, user.In("/time-entries"))
        {
            Content = JsonContent.Create(new { description = "From the laptop", start = DateTimeOffset.UtcNow.AddMinutes(-1) }),
        };
        request.Headers.Add(SyncInterceptor.ConnectionHeader, laptop.Connection.ConnectionId);
        (await user.Client.SendAsync(request, Ct)).EnsureSuccessStatusCode();

        Assert.Equal("entries", (await phone.NextAsync(c => c.Topic == "entries")).Topic);
        Assert.DoesNotContain(await laptop.AllWithinAsync(TimeSpan.FromMilliseconds(500)), c => c.Topic == "entries");
    }

    [Fact]
    public async Task Only_signed_in_people_connect()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, SyncHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => connection.StartAsync(Ct));
    }

    private async Task<App> ConnectAsync(TestUser user)
    {
        var token = user.Client.DefaultRequestHeaders.Authorization!.Parameter;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, SyncHub.Path.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult(token);
            })
            .Build();
        var app = new App(connection);
        connection.On<SyncChange>(nameof(ISyncClient.Changed), change => app.Received.Writer.TryWrite(change));
        await connection.StartAsync(Ct);
        return app;
    }

    /// <summary>A connected app, and what it was told.</summary>
    private sealed class App(HubConnection connection) : IAsyncDisposable
    {
        public HubConnection Connection { get; } = connection;

        public Channel<SyncChange> Received { get; } = Channel.CreateUnbounded<SyncChange>();

        public async Task<SyncChange> NextAsync(Func<SyncChange, bool> match)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(Wait);
            while (true)
            {
                var change = await Received.Reader.ReadAsync(timeout.Token);
                if (match(change))
                {
                    return change;
                }
            }
        }

        public async Task<List<SyncChange>> AllWithinAsync(TimeSpan period)
        {
            await Task.Delay(period, Ct);
            var all = new List<SyncChange>();
            while (Received.Reader.TryRead(out var change))
            {
                all.Add(change);
            }

            return all;
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
