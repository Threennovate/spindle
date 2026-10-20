using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Threading;
using Spindle.Core;

namespace Spindle.Windows.Views;

/// <summary>Signing in, creating an account, resetting a password and choosing a server, as on the Mac.</summary>
public sealed partial class AuthView : UserControl
{
    private const string CloudHost = "app.spindletrack.com";
    private readonly Func<string, Uri> parseServer;
    private readonly Func<Uri, SpindleApiClient> client;
    private readonly Func<Uri, LoginRequest, Task> signIn;
    private readonly Func<Uri, RegisterRequest, Task> register;
    private readonly Func<Uri, SignInProvider, CancellationToken, Task> continueWith;
    private readonly TextBox server = new() { Name = "Server", PlaceholderText = "spindle.example.com" };
    private readonly TextBox email = new() { Name = "Email" };
    private readonly TextBox password = new() { Name = "Password", PasswordChar = '●' };
    private readonly TextBox name = new() { Name = "FullName" };
    private readonly CheckBox terms = new() { Name = "Terms" };
    private readonly RadioButton cloud = new() { Content = "Spindle Cloud" };
    private readonly RadioButton own = new() { Content = "Your own server" };
    private readonly ContentControl form = new();
    private readonly StackPanel footer = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    private string mode = "signin";
    private string? error;
    private ServerInfo info = new(false, true, "terms", "privacy");
    private Uri selectedServer;
    private (SignInProvider Provider, CancellationTokenSource Cancel)? browser;

    /// <param name="continueWith">Signs in with Google or Apple in the browser; cancelled when the person chooses Cancel here.</param>
    public AuthView(Uri initialServer, Func<string, Uri> parseServer, Func<Uri, SpindleApiClient> client,
        Func<Uri, LoginRequest, Task> signIn, Func<Uri, RegisterRequest, Task> register,
        Func<Uri, SignInProvider, CancellationToken, Task> continueWith)
    {
        selectedServer = initialServer;
        this.parseServer = parseServer;
        this.client = client;
        this.signIn = signIn;
        this.register = register;
        this.continueWith = continueWith;
        server.Text = selectedServer.Host == CloudHost ? "" : selectedServer.Authority;
        cloud.IsChecked = selectedServer.Host == CloudHost;
        own.IsChecked = !cloud.IsChecked;
        cloud.IsCheckedChanged += (_, _) => Render();
        password.KeyDown += async (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter || mode != "signin") return;
            e.Handled = true;
            try
            {
                await SignInAsync();
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
            }
        };
        var stack = Ui.Stack(24, Ui.Logo(36), form, footer);
        stack.Width = 400;
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Margin = new Thickness(0, 56, 0, 40);
        Content = new ScrollViewer { Content = stack };
        Render();
        AttachedToVisualTree += async (_, _) =>
        {
            try
            {
                info = await client(selectedServer).GetServerInfoAsync();
                Render();
            }
            catch (Exception)
            {
                // Signing in stays available when the server can't be reached yet.
            }
        };
    }

    private static void Detach(Control control)
    {
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        else if (control.Parent is ContentControl holder) holder.Content = null;
        else if (control.Parent is Decorator decorator) decorator.Child = null;
    }

    private Button ModeLink(string text, string next, string controlName) =>
        Ui.Button(text, () =>
        {
            mode = next;
            error = null;
            Render();
        }, "link").Named(controlName);

    private void Render()
    {
        foreach (var control in new Control[] { server, email, password, name, terms, cloud, own }) Detach(control);
        var title = mode switch
        {
            "register" => "Create your account",
            "forgot" => "Forgot your password?",
            "sent" => "Check your inbox",
            "server" => "Choose a server",
            "browser" => "Continue in your browser",
            _ => "Sign in",
        };
        var description = mode switch
        {
            "register" => "Start tracking your time in less than a minute.",
            "forgot" => "Enter the email you sign in with. We'll send you a link to choose a new password.",
            "sent" => $"If an account uses {email.Text?.Trim()}, we sent it a link to choose a new password.",
            "server" => "Spindle Cloud, or a Spindle server that you or your team run.",
            "browser" => $"Finish signing in with {browser?.Provider.DisplayName} in the browser window that opened. Spindle continues here when you're done.",
            _ => "Welcome back. Sign in to continue tracking your time.",
        };
        var content = Ui.Stack(20, Ui.Stack(6, Ui.Text(title, "subtitle"), Ui.Wrapped(description, "secondary")));
        if (error is not null)
            content.Children.Add(new Border { Child = Ui.Columns("Auto,*", 10, Ui.Icon("IconWarningFilled", "critical"), Ui.Wrapped(error).Named("AuthError")) }.Classed("infobar", "critical"));
        Button submit;
        switch (mode)
        {
            case "register":
                AddProviders(content);
                content.Children.Add(new FormField("Full name", name));
                content.Children.Add(new FormField("Email", email));
                content.Children.Add(new FormField("Password", password, "At least 8 characters."));
                if (info.TermsUrl is not null) content.Children.Add(Terms());
                submit = Ui.Button("Create account", RegisterAsync, Fail, "primary").Named("Register");
                break;
            case "forgot":
                content.Children.Add(new FormField("Email", email));
                submit = Ui.Button("Send reset link", async () =>
                {
                    if (string.IsNullOrWhiteSpace(email.Text)) throw new InvalidOperationException("Enter your email address.");
                    await client(selectedServer).ForgotPasswordAsync(email.Text.Trim());
                    mode = "sent";
                    error = null;
                    Render();
                }, Fail, "primary").Named("SendReset");
                break;
            case "sent":
                submit = Ui.Button("Back to sign in", () =>
                {
                    mode = "signin";
                    Render();
                }, "primary").Named("BackToSignIn");
                break;
            case "server":
                content.Children.Add(Ui.Stack(8, cloud, own));
                if (own.IsChecked == true)
                {
                    content.Children.Add(new FormField("Address", server, "The address you open Spindle at in a browser, e.g. spindle.example.com."));
                    content.Children.Add(Ui.Wrapped("Passkeys work only with Spindle Cloud. On your own server, sign in with your email and password.", "caption", "secondary"));
                }
                submit = Ui.Button("Use this server", SelectServerAsync, Fail, "primary").Named("SaveServer");
                break;
            case "browser":
                submit = Ui.Button("Cancel", () => browser?.Cancel.Cancel()).Named("CancelBrowser");
                break;
            default:
                AddProviders(content);
                content.Children.Add(new FormField("Email", email));
                content.Children.Add(new FormField("Password", password));
                var forgot = ModeLink("Forgot your password?", "forgot", "ForgotMode");
                forgot.HorizontalAlignment = HorizontalAlignment.Right;
                forgot.Margin = new Thickness(0, -12, 0, 0);
                content.Children.Add(forgot);
                submit = Ui.Button("Sign in", SignInAsync, Fail, "primary").Named("SignIn");
                break;
        }
        submit.HorizontalAlignment = HorizontalAlignment.Stretch;
        submit.HorizontalContentAlignment = HorizontalAlignment.Center;
        submit.MinHeight = 36;
        content.Children.Add(submit);
        var card = Ui.Card(content);
        card.Padding = new Thickness(28);
        card.CornerRadius = new CornerRadius(12);
        card.Res(Border.BackgroundProperty, "SolidCardBackgroundBrush");
        card.BoxShadow = Avalonia.Media.BoxShadows.Parse("0 4 12 0 #10000000");
        form.Content = card;
        footer.Children.Clear();
        if (mode == "signin" && info.RegistrationOpen) footer.Children.Add(Centered(Ui.Text("New to Spindle?", "secondary"), ModeLink("Create an account", "register", "RegisterMode")));
        else if (mode == "register") footer.Children.Add(Centered(Ui.Text("Already have an account?", "secondary"), ModeLink("Sign in", "signin", "BackToSignIn")));
        else if (mode is "forgot" or "server") footer.Children.Add(Centered(ModeLink("Back to sign in", "signin", "BackToSignIn")));
        if (mode is "signin" or "register")
            footer.Children.Add(Centered(Ui.Text(selectedServer.Host == CloudHost ? "Spindle Cloud" : selectedServer.Authority, "caption", "secondary").Named("ServerName"), Ui.Text("·", "caption", "secondary"), ModeLink("Change server", "server", "ServerMode")));
        Dispatcher.UIThread.Post(() => (mode switch { "register" => name, "server" => own.IsChecked == true ? server : null, "signin" or "forgot" => email, _ => null })?.Focus());
    }

    private static StackPanel Centered(params Control[] children)
    {
        var row = Ui.Row(6, children);
        row.HorizontalAlignment = HorizontalAlignment.Center;
        foreach (var child in children) child.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }

    private Control Terms()
    {
        var text = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        text.Inlines!.Add(new Run("I agree to the "));
        text.Inlines.Add(new InlineUIContainer(Ui.Button("terms of service", () => Open(info.TermsUrl!), "link")));
        text.Inlines.Add(new Run(" and the "));
        text.Inlines.Add(new InlineUIContainer(Ui.Button("privacy policy", () => Open(info.PrivacyUrl ?? "privacy"), "link")));
        text.Inlines.Add(new Run("."));
        return Ui.Columns("Auto,*", 4, terms, text.Centered());
    }

    /// <summary>"Continue with Google" and "Continue with Apple" above the email form, for signing in and creating an account alike.</summary>
    private void AddProviders(Panel content)
    {
        if (info.SignInProviders is not { Length: > 0 } providers) return;
        var buttons = Ui.Stack(8);
        foreach (var provider in providers)
        {
            var label = $"Continue with {provider.DisplayName}";
            var row = Ui.Row(10, Ui.ProviderMark(provider.Name), Ui.Text(label).Centered());
            var button = Ui.Button(row, () => ContinueAsync(provider), Fail).Named("Continue" + Capitalized(provider.Name)).Spoken(label);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.MinHeight = 36;
            buttons.Children.Add(button);
        }
        if (info.TermsUrl is not null) buttons.Children.Add(Agreement());
        content.Children.Add(buttons);
        var line = () => new Border().Classed("divider").Centered();
        content.Children.Add(Ui.Columns("*,Auto,*", 12, line(), Ui.Text("or with email", "caption", "secondary"), line()).Named("OrWithEmail"));
    }

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Continuing is agreeing, as on the web: the server records it with the new account.</summary>
    private Control Agreement()
    {
        var text = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, TextAlignment = Avalonia.Media.TextAlignment.Center }.Classed("caption", "secondary");
        text.Inlines!.Add(new Run("By continuing, you agree to the "));
        text.Inlines.Add(new InlineUIContainer(Ui.Button("terms of service", () => Open(info.TermsUrl!), "link")));
        if (info.PrivacyUrl is { } privacy)
        {
            text.Inlines.Add(new Run(" and "));
            text.Inlines.Add(new InlineUIContainer(Ui.Button("privacy policy", () => Open(privacy), "link")));
        }
        text.Inlines.Add(new Run("."));
        return text.Named("Agreement");
    }

    private async Task ContinueAsync(SignInProvider provider)
    {
        var returnTo = mode;
        using var cancel = new CancellationTokenSource();
        browser = (provider, cancel);
        mode = "browser";
        error = null;
        Render();
        try
        {
            await continueWith(selectedServer, provider, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The person chose Cancel; the listener has stopped and there's nothing to say.
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"Signing in with {provider.DisplayName} took too long. Try again.");
        }
        finally
        {
            browser = null;
            if (mode == "browser")
            {
                mode = returnTo;
                Render();
            }
        }
    }

    private async Task SignInAsync()
    {
        var address = email.Text?.Trim() ?? "";
        if (address.Length == 0) throw new InvalidOperationException("Enter your email address.");
        if (!EmailPattern().IsMatch(address)) throw new InvalidOperationException("Enter a valid email address.");
        if (string.IsNullOrEmpty(password.Text)) throw new InvalidOperationException("Enter your password.");
        await signIn(selectedServer, new LoginRequest { Email = address, Password = password.Text });
        password.Text = "";
    }

    private async Task RegisterAsync()
    {
        if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidOperationException("Enter your name.");
        var address = email.Text?.Trim() ?? "";
        if (address.Length == 0) throw new InvalidOperationException("Enter your email address.");
        if (!EmailPattern().IsMatch(address)) throw new InvalidOperationException("Enter a valid email address.");
        if ((password.Text?.Length ?? 0) < 8) throw new InvalidOperationException("Use at least 8 characters.");
        if (info.TermsUrl is not null && terms.IsChecked != true) throw new InvalidOperationException("Agree to the terms of service and privacy policy to create an account.");
        await register(selectedServer, new RegisterRequest { FullName = name.Text!.Trim(), Email = address, Password = password.Text!, AcceptTerms = terms.IsChecked == true });
        password.Text = "";
    }

    private async Task SelectServerAsync()
    {
        var candidate = parseServer(cloud.IsChecked == true ? "https://" + CloudHost : server.Text ?? "");
        try
        {
            info = await client(candidate).GetServerInfoAsync();
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException($"No Spindle server answered at {candidate.Host}. Check the address, and that the server is running.");
        }
        selectedServer = candidate;
        mode = "signin";
        error = null;
        Render();
    }

    private void Open(string address)
    {
        var uri = Uri.TryCreate(address, UriKind.Absolute, out var absolute) ? absolute : new Uri(selectedServer, address);
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void Fail(string message)
    {
        error = message;
        Render();
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
