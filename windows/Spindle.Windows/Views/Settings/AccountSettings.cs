using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Spindle.Core;
using Spindle.Windows.Model;
using Spindle.Windows.Views.Shell;
using static Spindle.Windows.Views.SettingsView;

namespace Spindle.Windows.Views;

/// <summary>The Pomodoro, which is the account's, the same on every device.</summary>
public sealed class PomodoroSettings : UserControl
{
    public PomodoroSettings(MainWindow owner)
    {
        var current = owner.Me?.Preferences ?? new PreferencesResponse();
        var enabled = new ToggleSwitch { IsChecked = current.PomodoroEnabled, OnContent = "On", OffContent = "Off", Name = "PomodoroEnabled" };
        var focus = new NumericUpDown { Minimum = 5, Maximum = 180, Increment = 5, Value = Math.Max(5, current.PomodoroMinutes), Width = 140, FormatString = "0" };
        var rest = new NumericUpDown { Minimum = 1, Maximum = 60, Increment = 1, Value = Math.Max(1, current.PomodoroBreakMinutes), Width = 140, FormatString = "0" };
        var autoStop = new ToggleSwitch { IsChecked = current.PomodoroAutoStop, OnContent = "On", OffContent = "Off" };
        var save = Ui.Button("Save", async () =>
        {
            var me = await owner.Api!.GetMeAsync();
            var p = me.Preferences;
            await owner.Api.UpdatePreferencesAsync(new PreferencesRequest
            {
                WeekStart = p.WeekStart, DateFormat = p.DateFormat, TimeFormat = p.TimeFormat, DurationFormat = p.DurationFormat,
                PomodoroEnabled = enabled.IsChecked == true, PomodoroMinutes = (int)(focus.Value ?? 25), PomodoroBreakMinutes = (int)(rest.Value ?? 5), PomodoroAutoStop = autoStop.IsChecked == true,
            });
            await owner.ReloadAccountAsync();
            owner.Notify("Pomodoro saved", NoticeKind.Success);
        }, owner.Notify, "primary").Named("SavePomodoro");
        save.HorizontalAlignment = HorizontalAlignment.Left;
        Content = Ui.Stack(0,
            Section("Pomodoro",
                Card("IconTimer", "Use the Pomodoro", "Work in focused intervals with short breaks. You get a reminder when each interval ends.", enabled),
                Card(null, "Focus", "Minutes in each interval.", focus),
                Card(null, "Break", "Minutes between intervals.", rest),
                Card(null, "Stop the timer when a focus interval ends", null, autoStop),
                Footer("These settings are your account's, the same on every device.")),
            save.Margin(0, 16, 0, 0));
    }
}

/// <summary>The account: profile, how dates and times are shown, password, devices and data.</summary>
public sealed class AccountSettings : UserControl
{
    private readonly MainWindow owner;
    private readonly StackPanel sessions = new() { Spacing = 4 };

    public AccountSettings(MainWindow owner)
    {
        this.owner = owner;
        var me = owner.Me!;
        var api = owner.Api!;

        var name = new TextBox { Text = me.FullName, Width = 280, Name = "ProfileName" };
        var zones = TimeZoneInfo.GetSystemTimeZones().Select(z => Iana(z.Id)).Append(me.TimeZone).Distinct().OrderBy(z => z).ToArray();
        var local = Iana(TimeZoneInfo.Local.Id);
        if (!zones.Contains(local)) zones = [.. zones, local];
        var zone = new ComboBox { ItemsSource = zones.Select(z => z.Replace('_', ' ')).ToArray(), SelectedIndex = Array.IndexOf(zones, me.TimeZone), Width = 280 };
        var useLocal = Ui.Button($"Use this PC's time zone ({local.Replace('_', ' ')})", () => zone.SelectedIndex = Array.IndexOf(zones, local), "link");
        useLocal.IsVisible = !SameRules(local, me.TimeZone);
        var saveProfile = Ui.Button("Save", async () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidOperationException("Enter your name.");
            await api.UpdateProfileAsync(new ProfileRequest { FullName = name.Text.Trim(), TimeZone = zones[Math.Max(0, zone.SelectedIndex)] });
            await owner.ReloadAccountAsync();
            owner.Notify("Profile saved", NoticeKind.Success);
        }, owner.Notify, "primary");

        var p = me.Preferences;
        var week = Choice([(0, "Sunday"), (1, "Monday"), (2, "Tuesday"), (3, "Wednesday"), (4, "Thursday"), (5, "Friday"), (6, "Saturday")], p.WeekStart, _ => { });
        var sample = new DateTime(2026, 9, 28);
        var dateFormats = new[] { "d MMM yyyy", "dd.MM.yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd" };
        var date = Choice(dateFormats.Select(f => (f, sample.ToString(f, BritishEnglish.Culture))).ToArray(), p.DateFormat, _ => { });
        var clock = Choice([("24h", "24-hour (13:30)"), ("12h", "12-hour (1:30 PM)")], p.TimeFormat == "12h" ? "12h" : "24h", _ => { });
        var durations = Choice([("hms", "1:30:00"), ("hm", "1:30"), ("decimal", "1.50 h")], p.DurationFormat is { Length: > 0 } format ? format : "hms", _ => { });
        var savePreferences = Ui.Button("Save", async () =>
        {
            var latest = (await api.GetMeAsync()).Preferences;
            string[] times = ["24h", "12h"];
            string[] kinds = ["hms", "hm", "decimal"];
            await api.UpdatePreferencesAsync(new PreferencesRequest
            {
                WeekStart = week.SelectedIndex, DateFormat = dateFormats[Math.Max(0, date.SelectedIndex)], TimeFormat = times[Math.Max(0, clock.SelectedIndex)],
                DurationFormat = kinds[Math.Max(0, durations.SelectedIndex)], PomodoroEnabled = latest.PomodoroEnabled, PomodoroMinutes = latest.PomodoroMinutes,
                PomodoroBreakMinutes = latest.PomodoroBreakMinutes, PomodoroAutoStop = latest.PomodoroAutoStop,
            });
            await owner.ReloadAccountAsync();
            owner.Notify("Preferences saved", NoticeKind.Success);
        }, owner.Notify, "primary");

        var current = new TextBox { PasswordChar = '●', Width = 240 };
        var next = new TextBox { PasswordChar = '●', Width = 240 };
        var repeat = new TextBox { PasswordChar = '●', Width = 240 };
        var change = Ui.Button("Change password", async () =>
        {
            if ((next.Text?.Length ?? 0) < 8) throw new InvalidOperationException("Use at least 8 characters.");
            if (next.Text != repeat.Text) throw new InvalidOperationException("The new passwords don't match.");
            await api.ChangePasswordAsync(new ChangePasswordRequest { CurrentPassword = current.Text ?? "", NewPassword = next.Text! });
            current.Text = next.Text = repeat.Text = "";
            owner.Notify("Password changed. Your other devices are signed out.", NoticeKind.Success);
        }, owner.Notify, "primary");
        // An account made with Google or Apple has no password to change; the reset email sets its first one.
        var passwordSection = me.HasPassword == false
            ? Section("Password",
                Card("IconShield", "No password yet", "You sign in with Google or Apple, so your account has no password. Add one to also sign in with your email address.",
                    Ui.Button("Email me a link to set a password", async () =>
                    {
                        await api.ForgotPasswordAsync(me.Email);
                        owner.Notify($"We sent a link to {me.Email}. Open it to choose your password.", NoticeKind.Success);
                    }, owner.Notify).Named("SetPasswordLink")))
            : Section("Password",
                Card("IconShield", "Current password", null, current),
                Card(null, "New password", "At least 8 characters; a few words make a strong one.", next),
                Card(null, "Repeat the new password", null, repeat),
                Footer("Changing it signs you out on every other device."),
                Right(change));

        Content = Ui.Stack(0,
            Section("Profile",
                Card("IconPersonCircle", "Email", null, Ui.Text(me.Email + (me.EmailConfirmed ? "" : " · not confirmed"), "secondary")),
                Card(null, "Full name", "Your name is how teammates see you.", name),
                Card(null, "Time zone", "Days and times are counted in your time zone.", zone, useLocal.IsVisible ? useLocal : null),
                Right(saveProfile)),
            Section("Dates and times",
                Card("IconCalendarLtr", "First day of the week", null, week),
                Card(null, "Date format", null, date),
                Card(null, "Time format", null, clock),
                Card(null, "Duration format", null, durations),
                Footer("How dates, times and durations are shown to you, on every device."),
                Right(savePreferences)),
            passwordSection,
            Section("Where you're signed in", sessions, Footer("Sign out of a device you no longer use or don't recognize.")),
            Section("Passkeys and your data",
                Card("IconOpen", "Passkeys", "Add or remove passkeys in your account on the web.", Ui.Button("Open on the web", () => owner.OpenWeb("account"))),
                Card("IconArrowDownload", "Export or delete your account", "A copy of your profile and every time entry, or deleting the account, on the web.", Ui.Button("Open on the web", () => owner.OpenWeb("account")))));
        AttachedToVisualTree += async (_, _) => await LoadSessionsAsync();
    }

    private static Control Right(Button button)
    {
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.Margin = new Thickness(0, 8, 0, 0);
        return button;
    }

    private async Task LoadSessionsAsync()
    {
        try
        {
            var list = await owner.Api!.GetAsync<DeviceSession[]>("/api/me/sessions");
            sessions.Children.Clear();
            foreach (var session in list.OrderByDescending(s => s.IsCurrent).ThenByDescending(s => s.LastActiveAt))
            {
                Control? action = session.IsCurrent ? null : Ui.Button("Sign out", async () =>
                {
                    await owner.Api!.DeleteAsync("/api/me/sessions/" + session.Id);
                    await LoadSessionsAsync();
                }, owner.Notify);
                sessions.Children.Add(Card("IconDesktop", session.DeviceName ?? "Unknown device", session.IsCurrent ? "This PC" : "Last active " + Relative(session.LastActiveAt), action));
            }
            if (list.Length > 1)
                sessions.Children.Add(Right(Ui.Button("Sign out everywhere else", async () =>
                {
                    await owner.Api!.PostAsync("/api/me/sessions/revoke-others");
                    owner.Notify("Signed out everywhere else", NoticeKind.Success);
                    await LoadSessionsAsync();
                }, owner.Notify)));
        }
        catch (Exception)
        {
            sessions.Children.Clear();
            sessions.Children.Add(Footer("Couldn't load your devices."));
        }
    }

    internal static string Relative(DateTimeOffset when)
    {
        var ago = DateTimeOffset.UtcNow - when;
        if (ago.TotalMinutes < 1) return "just now";
        if (ago.TotalHours < 1) return $"{(int)ago.TotalMinutes} minutes ago";
        if (ago.TotalDays < 1) return $"{(int)ago.TotalHours} hours ago";
        return ago.TotalDays < 2 ? "yesterday" : $"{(int)ago.TotalDays} days ago";
    }

    /// <summary>The IANA name of a zone; for a Windows zone, the one of this PC's region, e.g. Europe/Zagreb in Croatia.</summary>
    private static string Iana(string id)
    {
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _)) return id;
        var region = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, region, out var regional) ? regional : TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;
    }

    private static bool SameRules(string first, string second)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(first).HasSameRules(TimeZoneInfo.FindSystemTimeZoneById(second));
        }
        catch (Exception)
        {
            return first == second;
        }
    }

    public sealed record DeviceSession
    {
        public Guid Id { get; init; }
        public string? DeviceName { get; init; }
        public DateTimeOffset LastActiveAt { get; init; }
        public bool IsCurrent { get; init; }
    }
}
