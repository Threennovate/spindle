using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spindle.Api.Auth;
using Spindle.Api.Me;
using Spindle.Api.Projects;
using Spindle.Api.Reports;
using Spindle.Api.Team;
using Spindle.Api.TimeEntries;

namespace Spindle.Tests.Integration;

/// <summary>A person's own account: profile, preferences, password, devices, data export and deletion.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AccountTests(PostgresApiFactory factory)
{
    private const string Password = "correct horse battery";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task People_change_their_profile_and_preferences()
    {
        var user = await TestUser.RegisterAsync(factory);

        var profile = await PutAsync<MeResponse>(user, "/api/me/profile", new { fullName = "  Ana Anić ", timeZone = "America/New_York" });
        var preferences = await PutAsync<MeResponse>(user, "/api/me/preferences", new
        {
            weekStart = 0, dateFormat = "dd.MM.yyyy", timeFormat = "12h", durationFormat = "decimal",
            pomodoroEnabled = true, pomodoroMinutes = 50, pomodoroBreakMinutes = 10, pomodoroAutoStop = true,
        });
        var badZone = await user.Client.PutAsJsonAsync("/api/me/profile", new { fullName = "Ana", timeZone = "Mars/Olympus" }, Ct);
        var badFormats = await user.Client.PutAsJsonAsync("/api/me/preferences", new { weekStart = 7, dateFormat = "dd-MM", timeFormat = "24h", durationFormat = "hms" }, Ct);

        Assert.Equal(("Ana Anić", "America/New_York"), (profile.FullName, profile.TimeZone));
        Assert.Equal(new PreferencesResponse(0, "dd.MM.yyyy", "12h", "decimal", true, 50, 10, true), preferences.Preferences);
        Assert.Equal(new PreferencesResponse(0, "dd.MM.yyyy", "12h", "decimal", true, 50, 10, true), (await user.MeAsync()).Preferences);
        Assert.Equal(["timeZone"], await TestUser.ErrorFieldsAsync(badZone));
        Assert.Contains("weekStart", await TestUser.ErrorFieldsAsync(badFormats));
        Assert.Equal(["dateFormat"], await TestUser.ErrorFieldsAsync(
            await user.Client.PutAsJsonAsync("/api/me/preferences", new { weekStart = 1, dateFormat = "dd-MM", timeFormat = "24h", durationFormat = "hms" }, Ct)));
    }

    [Fact]
    public async Task New_accounts_start_with_sensible_preferences()
    {
        var user = await TestUser.RegisterAsync(factory);

        var me = await user.MeAsync();

        Assert.Equal(new PreferencesResponse(1, "d MMM yyyy", "24h", "hms", false, 25, 5, false), me.Preferences);
        Assert.False(me.EmailConfirmed);
    }

    [Fact]
    public async Task Changing_the_password_signs_out_every_other_device()
    {
        var user = await TestUser.RegisterAsync(factory);
        var laptop = await LoginAsync(user.Email, Password, "Laptop");
        var phone = await LoginAsync(user.Email, Password, "Phone");
        using var onLaptop = Authorized(laptop.AccessToken);

        var wrong = await onLaptop.PostAsJsonAsync("/api/me/password", new { currentPassword = "not it at all", newPassword = "a brand new secret" }, Ct);
        var tooShort = await onLaptop.PostAsJsonAsync("/api/me/password", new { currentPassword = Password, newPassword = "short" }, Ct);
        var changed = await onLaptop.PostAsJsonAsync("/api/me/password", new { currentPassword = Password, newPassword = "a brand new secret" }, Ct);

        Assert.Equal(["currentPassword"], await TestUser.ErrorFieldsAsync(wrong));
        Assert.Equal(["newPassword"], await TestUser.ErrorFieldsAsync(tooShort));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(laptop.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(phone.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password }, Ct)).StatusCode);
        await LoginAsync(user.Email, "a brand new secret", "Tablet");
    }

    [Fact]
    public async Task People_see_their_devices_and_sign_them_out()
    {
        var user = await TestUser.RegisterAsync(factory);
        var laptop = await LoginAsync(user.Email, Password, "Laptop");
        var phone = await LoginAsync(user.Email, Password, "Phone");
        using var onLaptop = Authorized(laptop.AccessToken);

        var sessions = (await onLaptop.GetFromJsonAsync<List<SessionResponse>>("/api/me/sessions", TestJson.Options, Ct))!;
        var phoneSession = sessions.Single(s => s.DeviceName == "Phone");
        var signedOut = await onLaptop.DeleteAsync($"/api/me/sessions/{phoneSession.Id}", Ct);
        var again = await onLaptop.DeleteAsync($"/api/me/sessions/{phoneSession.Id}", Ct);
        var someoneElses = await user.Client.DeleteAsync($"/api/me/sessions/{Guid.NewGuid()}", Ct);
        var afterwards = await onLaptop.GetFromJsonAsync<List<SessionResponse>>("/api/me/sessions", TestJson.Options, Ct);

        Assert.Equal(3, sessions.Count);
        Assert.Equal(("Laptop", true), (sessions[0].DeviceName, sessions[0].IsCurrent));
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, someoneElses.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(phone.RefreshToken)).StatusCode);
        Assert.DoesNotContain(afterwards!, s => s.DeviceName == "Phone");

        (await onLaptop.PostAsync("/api/me/sessions/revoke-others", null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(["Laptop"], (await onLaptop.GetFromJsonAsync<List<SessionResponse>>("/api/me/sessions", TestJson.Options, Ct))!.Select(s => s.DeviceName));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(laptop.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Web_sign_ins_are_named_after_the_browser()
    {
        var user = await TestUser.RegisterAsync(factory);
        var browser = factory.CreateClient();
        browser.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15");
        (await browser.PostAsJsonAsync("/api/auth/web/login", new { email = user.Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        var sessions = await user.Client.GetFromJsonAsync<List<SessionResponse>>("/api/me/sessions", TestJson.Options, Ct);

        Assert.Contains(sessions!, s => s.DeviceName == "Safari on macOS");
    }

    [Fact]
    public async Task People_download_everything_they_tracked()
    {
        var user = await TestUser.RegisterAsync(factory);
        var project = await user.CreateAsync<ProjectResponse>("/projects", new { name = "Website", color = "#0B83D9" });
        await user.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Homepage", projectId = project.Id, start = DateTimeOffset.UtcNow.AddHours(-2), stop = DateTimeOffset.UtcNow.AddHours(-1) });

        var response = await user.Client.GetAsync("/api/me/export", Ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var workspace = json.RootElement.GetProperty("workspaces")[0];
        var entry = workspace.GetProperty("timeEntries")[0];

        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("spindle-data-", response.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal(user.Email, json.RootElement.GetProperty("profile").GetProperty("email").GetString());
        Assert.Equal("admin", workspace.GetProperty("role").GetString());
        Assert.Equal(("Homepage", "Website"), (entry.GetProperty("description").GetString(), entry.GetProperty("project").GetString()));
    }

    [Fact]
    public async Task Deleting_an_account_keeps_shared_time_and_forgets_the_person()
    {
        var admin = await TestUser.RegisterAsync(factory);
        var member = await factory.AddMemberAsync(admin, "Ivo Ivić");
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Design", start, stop = start.AddHours(2) });
        await member.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Still running", start = DateTimeOffset.UtcNow.AddMinutes(-5) });

        var wrongPassword = await member.Client.PostAsJsonAsync("/api/me/delete", new { password = "not my password" }, Ct);
        var deleted = await member.Client.PostAsJsonAsync("/api/me/delete", new { password = Password }, Ct);
        var report = await admin.CreateAsync<SummaryReport>("/reports/summary", new
        {
            from = DateOnly.FromDateTime(start.UtcDateTime).AddDays(-1), to = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), groupBy = "user",
        });
        var members = await admin.GetAsync<List<MemberResponse>>("/members");

        Assert.Equal(["password"], await TestUser.ErrorFieldsAsync(wrongPassword));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.Client.GetAsync("/api/me", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.Client.GetAsync(member.In("/time-entries/current"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = member.Email, password = Password }, Ct)).StatusCode);
        Assert.DoesNotContain(members, m => m.UserId == member.UserId);
        var group = Assert.Single(report.Groups);
        Assert.Equal((AccountDeletion.DeletedName, 2, "Deleted user"), (group.Name, report.Totals.EntryCount, group.Name));

        // The address is free again.
        await TestUser.RegisterAsync(factory, member.Email);
    }

    [Fact]
    public async Task The_only_admin_of_a_shared_workspace_hands_it_over_first()
    {
        var owner = await TestUser.RegisterAsync(factory);
        var colleague = await factory.AddMemberAsync(owner);

        var blocked = await owner.Client.PostAsJsonAsync("/api/me/delete", new { password = Password }, Ct);
        (await owner.PutAsync($"/members/{colleague.UserId}", new { role = "admin" })).EnsureSuccessStatusCode();
        await owner.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Setup", start = DateTimeOffset.UtcNow.AddHours(-3), stop = DateTimeOffset.UtcNow.AddHours(-2) });
        var deleted = await owner.Client.PostAsJsonAsync("/api/me/delete", new { password = Password }, Ct);
        var colleagueMe = await colleague.MeAsync();

        Assert.Equal(["account"], await TestUser.ErrorFieldsAsync(blocked));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        // The workspace carries on under the colleague, who now owns the organization.
        Assert.Equal(owner.WorkspaceId, Assert.Single(colleagueMe.Workspaces).Id);
        Assert.Equal(Spindle.Domain.Organizations.OrganizationRole.Owner, Assert.Single(colleagueMe.Organizations).Role);
        Assert.Equal(1, (await colleague.CreateAsync<SummaryReport>("/reports/summary", new { from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), to = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1) })).Totals.EntryCount);
    }

    [Fact]
    public async Task A_workspace_nobody_else_uses_goes_with_the_account()
    {
        var user = await TestUser.RegisterAsync(factory);
        await user.CreateAsync<ProjectResponse>("/projects", new { name = "Mine", color = "#0B83D9" });
        await user.CreateAsync<TimeEntryResponse>("/time-entries", new { description = "Mine", start = DateTimeOffset.UtcNow.AddHours(-2), stop = DateTimeOffset.UtcNow.AddHours(-1) });

        (await user.Client.PostAsJsonAsync("/api/me/delete", new { password = Password }, Ct)).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
        Assert.False(db.Workspaces.IgnoreQueryFilters().Any(w => w.Id == user.WorkspaceId));
        Assert.False(db.TimeEntries.IgnoreQueryFilters().Any(e => e.WorkspaceId == user.WorkspaceId));
        Assert.False(db.Projects.IgnoreQueryFilters().Any(p => p.WorkspaceId == user.WorkspaceId));
        Assert.False(db.Organizations.IgnoreQueryFilters().Any(o => db.OrganizationMembers.IgnoreQueryFilters().Any(m => m.OrganizationId == o.Id && m.UserId == user.UserId)));
        var forgotten = db.Users.Single(u => u.Id == user.UserId);
        Assert.Equal((AccountDeletion.DeletedName, (string?)null), (forgotten.FullName, forgotten.PasswordHash));
        Assert.EndsWith("@deleted.invalid", forgotten.Email);
    }

    private async Task<AuthTokensResponse> LoginAsync(string email, string password, string deviceName)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password, deviceName }, Ct);
        return (await TestUser.ReadAsync<AuthTokensResponse>(response))!;
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken }, Ct);

    private HttpClient Authorized(string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static async Task<T> PutAsync<T>(TestUser user, string path, object body) =>
        await TestUser.ReadAsync<T>(await user.Client.PutAsJsonAsync(path, body, Ct));
}
