using Microsoft.AspNetCore.Identity;

namespace Spindle.Infrastructure.Identity;

public sealed class AppUser : IdentityUser<Guid>
{
    public const int FullNameMaxLength = 100;
    public const int TimeZoneMaxLength = 64;
    public const int TermsVersionMaxLength = 50;

    public string FullName { get; set; } = string.Empty;

    /// <summary>IANA time zone ID, e.g. <c>Europe/Zagreb</c>. Reports group days in this zone.</summary>
    public string TimeZone { get; set; } = string.Empty;

    public Guid? DefaultWorkspaceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the person agreed to the server's terms of service, on a server that has them.</summary>
    public DateTimeOffset? TermsAcceptedAt { get; set; }

    /// <summary>Which version of the terms they agreed to, as the server named it then.</summary>
    public string? TermsVersion { get; set; }

    /// <summary>
    /// When the person deleted their account. The row stays, emptied of personal data, so the time they tracked in
    /// shared workspaces keeps its place in the team's reports.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>The first day of the week, 0 for Sunday to 6 for Saturday, as in JavaScript and .NET.</summary>
    public int WeekStart { get; set; } = 1;

    /// <summary>How dates are written, e.g. <c>dd.MM.yyyy</c>; one of <see cref="UserPreferences.DateFormats"/>.</summary>
    public string DateFormat { get; set; } = UserPreferences.DefaultDateFormat;

    /// <summary><c>24h</c> or <c>12h</c>.</summary>
    public string TimeFormat { get; set; } = UserPreferences.DefaultTimeFormat;

    /// <summary>How durations are written: <c>hms</c> (1:30:00), <c>hm</c> (1:30) or <c>decimal</c> (1.50 h).</summary>
    public string DurationFormat { get; set; } = UserPreferences.DefaultDurationFormat;

    public bool PomodoroEnabled { get; set; }

    public int PomodoroMinutes { get; set; } = 25;

    public int PomodoroBreakMinutes { get; set; } = 5;

    /// <summary>Whether the timer stops by itself when a Pomodoro interval ends.</summary>
    public bool PomodoroAutoStop { get; set; }

    /// <summary>
    /// Whether the Mac app may upload agent activity (prompt times, repositories, branches), so the web can suggest
    /// splits too. Off by default; turning it off deletes what was uploaded.
    /// </summary>
    public bool AgentActivitySync { get; set; }

    /// <summary>Whether uploaded agent activity includes session titles, which can be the first line of a prompt.</summary>
    public bool AgentActivityTitles { get; set; }

    /// <summary>When agent activity sync was last turned on. A Mac that uploaded before then uploads everything again.</summary>
    public DateTimeOffset? AgentActivitySyncedSince { get; set; }

    /// <summary>
    /// When the person put away the first steps the web shows after signing up. Accounts from before first steps have
    /// it set, so only new people see them.
    /// </summary>
    public DateTimeOffset? FirstStepsHiddenAt { get; set; }
}

/// <summary>The choices people have for how the apps show dates, times and durations.</summary>
public static class UserPreferences
{
    public const string DefaultDateFormat = "d MMM yyyy";
    public const string DefaultTimeFormat = "24h";
    public const string DefaultDurationFormat = "hms";

    public static readonly string[] DateFormats = ["d MMM yyyy", "dd.MM.yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd"];
    public static readonly string[] TimeFormats = ["24h", "12h"];
    public static readonly string[] DurationFormats = ["hms", "hm", "decimal"];
}
