using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Spindle.Core;
using Spindle.Platform;
using Spindle.Windows.Views;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

public sealed class AuthModeTests
{
    [Fact]
    public async Task DefaultSignInHidesRegistrationAndServerUntilChosenAndRegistrationSubmitsAcceptedTerms()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var window = Window(fixture);
            window.Show();
            Assert.Contains(fixture.Api.ServerUri.Authority, ErrorText(window));
            Assert.DoesNotContain(Controls<TextBox>(window), x => x.Name is "FullName" or "Server");
            Assert.DoesNotContain(Controls<Button>(window), x => x.Name == "Register");
            await ClickNamed(window, "RegisterMode");
            Assert.DoesNotContain(Controls<Button>(window), x => x.Name == "SignIn");
            Text(window, "FullName").Text = "  New User  ";
            Text(window, "Email").Text = "  new@example.com  ";
            Text(window, "Password").Text = "safe-password";
            await ClickNamed(window, "Register");
            Assert.Empty(fixture.AuthRequests);
            Assert.Contains("Agree to the terms", ErrorText(window));
            Controls<CheckBox>(window).Single(x => x.Name == "Terms").IsChecked = true;
            await Preview(window, "register");
            await ClickNamed(window, "Register");
            Assert.True(fixture.Api.IsAuthenticated);
            var sent = Assert.Single(fixture.AuthRequests);
            Assert.Equal("/api/auth/register", sent.Path);
            var request = JsonSerializer.Deserialize<RegisterRequest>(sent.Body, SpindleApiClient.JsonOptions)!;
            Assert.Equal("New User", request.FullName);
            Assert.Equal("new@example.com", request.Email);
            Assert.True(request.AcceptTerms);
            Assert.False(string.IsNullOrWhiteSpace(request.TimeZone));
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ServerModeRejectsInsecureServerAndReturnsToSignInAfterValidSelection()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var window = Window(fixture);
            window.Show();
            await ClickNamed(window, "ServerMode");
            Assert.DoesNotContain(Controls<TextBox>(window), x => x.Name is "Email" or "Password");
            Text(window, "Server").Text = "http://example.com";
            await ClickNamed(window, "SaveServer");
            Assert.Contains("Use HTTPS", ErrorText(window));
            Assert.False(fixture.Api.IsAuthenticated);
            Text(window, "Server").Text = fixture.Api.ServerUri.Authority;
            await Preview(window, "server");
            await ClickNamed(window, "SaveServer");
            Assert.Contains(Controls<Button>(window), x => x.Name == "SignIn");
            Assert.DoesNotContain(Controls<TextBox>(window), x => x.Name == "Server");
            Assert.Contains(fixture.Api.ServerUri.Authority, ErrorText(window));
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ForgotPasswordSendsEmailWithoutAuthenticationAndOffersReturnToSignIn()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var window = Window(fixture);
            window.Show();
            await ClickNamed(window, "ForgotMode");
            Assert.DoesNotContain(Controls<TextBox>(window), x => x.Name is "Password" or "FullName");
            Text(window, "Email").Text = "  test@example.com  ";
            await Preview(window, "forgot-password");
            await ClickNamed(window, "SendReset");
            Assert.Contains("Check your inbox", ErrorText(window));
            Assert.False(fixture.Api.IsAuthenticated);
            var sent = Assert.Single(fixture.AuthRequests);
            Assert.Equal("/api/auth/forgot-password", sent.Path);
            using var body = JsonDocument.Parse(sent.Body);
            Assert.Equal("test@example.com", body.RootElement.GetProperty("email").GetString());
            await ClickNamed(window, "BackToSignIn");
            Assert.Contains(Controls<Button>(window), x => x.Name == "SignIn");
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ContinueWithGoogleSignsInThroughTheBrowserAndLandsWhereRegistrationDoes()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture { Server = new(true, true, "https://example.com/terms", "https://example.com/privacy", [new("google", "Google"), new("apple", "Apple")]) };
            var window = Window(fixture);
            Uri? opened = null;
            window.OpenBrowser = uri => opened = uri;
            window.Show();
            await WaitFor(() => Controls<Button>(window).Any(x => x.Name == "ContinueGoogle"));
            Assert.Equal(["ContinueGoogle", "ContinueApple"], Controls<Button>(window).Select(x => x.Name).Where(x => x?.StartsWith("Continue") == true));
            var agreement = Controls<TextBlock>(window).Single(x => x.Name == "Agreement");
            Assert.StartsWith("By continuing, you agree to the", string.Concat(agreement.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(x => x.Text)));
            Assert.Contains("or with email", ErrorText(window));
            await Preview(window, "sign-in-providers");
            await ClickNamed(window, "RegisterMode");
            Assert.Contains(Controls<Button>(window), x => x.Name == "ContinueApple");
            await Preview(window, "register-providers");

            Tap(window, "Continue with Google");
            await WaitFor(() => opened is not null);
            Assert.Contains("Continue in your browser", ErrorText(window));
            await Preview(window, "sign-in-browser");
            var query = Query(opened!);
            Assert.Equal("/api/auth/external/google/start", opened!.AbsolutePath);
            Assert.Equal("true", query["acceptTerms"]);
            Assert.False(string.IsNullOrWhiteSpace(query["timeZone"]));
            using var browser = new HttpClient();
            var page = await browser.GetStringAsync(query["redirectUri"] + "?code=abc", TestContext.Current.CancellationToken);
            Assert.Contains("go back to Spindle", page);
            await WaitFor(() => Controls<TextBox>(window).Any(x => x.Name is "TimerDescription" or "RunningDescription"));
            var sent = Assert.Single(fixture.AuthRequests);
            Assert.Equal("/api/auth/external/exchange", sent.Path);
            using var body = JsonDocument.Parse(sent.Body);
            Assert.Equal("abc", body.RootElement.GetProperty("code").GetString());
            Assert.StartsWith("Spindle for Windows", body.RootElement.GetProperty("deviceName").GetString());
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CancellingTheBrowserSignInReturnsToTheFormAndStopsListening()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture { Server = new(false, true, null, null, [new("google", "Google")]) };
            var window = Window(fixture);
            Uri? opened = null;
            window.OpenBrowser = uri => opened = uri;
            window.Show();
            await WaitFor(() => Controls<Button>(window).Any(x => x.Name == "ContinueGoogle"));
            // Without terms there's nothing to agree to.
            Assert.DoesNotContain(Controls<Control>(window), x => x.Name == "Agreement");
            Tap(window, "Continue with Google");
            await WaitFor(() => opened is not null);
            await ClickNamed(window, "CancelBrowser");
            await WaitFor(() => Controls<Button>(window).Any(x => x.Name == "SignIn"));
            Assert.DoesNotContain(Controls<Control>(window), x => x.Name == "AuthError");
            Assert.False(fixture.Api.IsAuthenticated);
            using var browser = new HttpClient();
            await Assert.ThrowsAsync<HttpRequestException>(() => browser.GetAsync(Query(opened!)["redirectUri"] + "?code=late", TestContext.Current.CancellationToken));
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ServersWithoutProvidersShowOnlyTheEmailForm()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var window = Window(fixture);
            window.Show();
            await WaitFor(() => Controls<Button>(window).Any(x => x.Name == "SignIn"));
            await Task.Delay(50);
            Assert.DoesNotContain(Controls<Control>(window), x => x.Name is "ContinueGoogle" or "OrWithEmail" or "Agreement");
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AnAccountWithoutAPasswordIsOfferedALinkToSetOneInsteadOfChangingIt()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture { HasPassword = false };
            var window = await SignedIn(fixture);
            await Click(window, "Settings");
            var settings = Controls<SettingsView>(window).Single();
            settings.Show(3);
            await WaitFor(() => Controls<Button>(settings).Any(x => x.Name == "SetPasswordLink"));
            Assert.DoesNotContain(Controls<Button>(settings), x => Label(x) == "Change password");
            await Preview(window, "settings-account-no-password");
            await ClickNamed(settings, "SetPasswordLink");
            var sent = fixture.AuthRequests.Last();
            Assert.Equal("/api/auth/forgot-password", sent.Path);
            using var body = JsonDocument.Parse(sent.Body);
            Assert.Equal("ana@example.com", body.RootElement.GetProperty("email").GetString());
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    private static MainWindow Window(ApiFixture fixture) => new(fixture.Api,
        new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")), fixture.DataDirectory);
    private static TextBox Text(Control window, string name) => Controls<TextBox>(window).Single(x => x.Name == name);
}
