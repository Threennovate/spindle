using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.AgentActivity;
using Spindle.Api.Auth;
using Spindle.Api.Common;
using Spindle.Api.Operator;
using Spindle.Api.Subscriptions;
using Spindle.Domain.Organizations;
using Spindle.Domain.Subscriptions;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Me;

/// <param name="EmailConfirmed">Whether the person has confirmed that the email address is theirs.</param>
public sealed record MeResponse(
    Guid Id,
    string Email,
    bool EmailConfirmed,
    string FullName,
    string TimeZone,
    PreferencesResponse Preferences,
    Guid? DefaultWorkspaceId,
    IReadOnlyList<OrganizationSummary> Organizations,
    IReadOnlyList<WorkspaceSummary> Workspaces)
{
    /// <summary>Whether the Mac app uploads agent activity. Not required, so apps can read servers from before it.</summary>
    public AgentActivitySettingsResponse? AgentActivity { get; init; }

    /// <summary>Whether the person runs this server and may open the operator pages. Missing means no.</summary>
    public bool? IsOperator { get; init; }

    /// <summary>Whether the web shows the person their first steps, until they put them away. Missing means no.</summary>
    public bool? FirstSteps { get; init; }

    /// <summary>
    /// Whether the account has a password. One made with Google or Apple has none until the person sets one from a
    /// password reset email. Missing means yes, as on servers from before it.
    /// </summary>
    public bool? HasPassword { get; init; }
}

public sealed record OrganizationSummary(Guid Id, string Name, OrganizationRole Role)
{
    /// <summary>
    /// Whether the organization's data can be changed, or only read because its trial or plan has ended. Always full on
    /// a self-hosted server. Not required, so apps can read servers from before it: missing means full.
    /// </summary>
    public OrganizationAccess? Access { get; init; }

    /// <summary>When the organization's trial ends, while it is on one, on the hosted service.</summary>
    public DateTimeOffset? TrialEndsAt { get; init; }
}

public sealed record WorkspaceSummary(Guid Id, Guid OrganizationId, string Name, WorkspaceRole Role);

/// <summary>How the apps show time to this person, on every device.</summary>
/// <param name="WeekStart">The first day of the week: 0 for Sunday to 6 for Saturday.</param>
/// <param name="DateFormat">One of <c>d MMM yyyy</c>, <c>dd.MM.yyyy</c>, <c>dd/MM/yyyy</c>, <c>MM/dd/yyyy</c>, <c>yyyy-MM-dd</c>.</param>
/// <param name="TimeFormat"><c>24h</c> or <c>12h</c>.</param>
/// <param name="DurationFormat"><c>hms</c> (1:30:00), <c>hm</c> (1:30) or <c>decimal</c> (1.50 h).</param>
public sealed record PreferencesResponse(
    int WeekStart,
    string DateFormat,
    string TimeFormat,
    string DurationFormat,
    bool PomodoroEnabled,
    int PomodoroMinutes,
    int PomodoroBreakMinutes,
    bool PomodoroAutoStop)
{
    public static PreferencesResponse From(AppUser user) => new(
        user.WeekStart,
        user.DateFormat,
        user.TimeFormat,
        user.DurationFormat,
        user.PomodoroEnabled,
        user.PomodoroMinutes,
        user.PomodoroBreakMinutes,
        user.PomodoroAutoStop);
}

public sealed record ProfileRequest
{
    [Required]
    [StringLength(AppUser.FullNameMaxLength)]
    public string FullName { get; init; } = string.Empty;

    /// <summary>IANA time zone, e.g. <c>Europe/Zagreb</c>. Days in the apps and reports are counted in it.</summary>
    [Required]
    [IanaTimeZone]
    public string TimeZone { get; init; } = string.Empty;
}

public sealed record PreferencesRequest
{
    [Range(0, 6)]
    public int WeekStart { get; init; } = 1;

    [Required]
    public string DateFormat { get; init; } = UserPreferences.DefaultDateFormat;

    [Required]
    public string TimeFormat { get; init; } = UserPreferences.DefaultTimeFormat;

    [Required]
    public string DurationFormat { get; init; } = UserPreferences.DefaultDurationFormat;

    public bool PomodoroEnabled { get; init; }

    [Range(5, 180)]
    public int PomodoroMinutes { get; init; } = 25;

    [Range(1, 60)]
    public int PomodoroBreakMinutes { get; init; } = 5;

    public bool PomodoroAutoStop { get; init; }
}

public sealed record FirstStepsRequest
{
    /// <summary>Puts the first steps away for good, on every device, or brings them back.</summary>
    public bool Hidden { get; init; }
}

internal static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/me").WithTags("Me").RequireAuthorization().ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("", GetMeAsync).WithName("getMe");
        group.MapPut("/profile", UpdateProfileAsync).WithName("updateProfile");
        group.MapPut("/preferences", UpdatePreferencesAsync).WithName("updatePreferences");
        group.MapPut("/first-steps", UpdateFirstStepsAsync).WithName("updateFirstSteps");

        return routes;
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> GetMeAsync(
        ClaimsPrincipal principal,
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        Operators operators,
        CancellationToken cancellationToken)
    {
        var user = await FindAsync(db, principal, cancellationToken);
        return user is null ? TypedResults.Unauthorized() : TypedResults.Ok(await ToResponseAsync(db, subscriptions, operators, user, cancellationToken));
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> UpdateProfileAsync(
        ProfileRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        Operators operators,
        CancellationToken cancellationToken)
    {
        var user = await FindAsync(db, principal, cancellationToken, tracking: true);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        user.FullName = request.FullName.Trim();
        user.TimeZone = request.TimeZone;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ToResponseAsync(db, subscriptions, operators, user, cancellationToken));
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult, ValidationProblem>> UpdatePreferencesAsync(
        PreferencesRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        Operators operators,
        CancellationToken cancellationToken)
    {
        var invalid = new Dictionary<string, string[]>();
        Check(invalid, "dateFormat", request.DateFormat, UserPreferences.DateFormats);
        Check(invalid, "timeFormat", request.TimeFormat, UserPreferences.TimeFormats);
        Check(invalid, "durationFormat", request.DurationFormat, UserPreferences.DurationFormats);
        if (invalid.Count > 0)
        {
            return Problems.Validation(invalid);
        }

        var user = await FindAsync(db, principal, cancellationToken, tracking: true);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        user.WeekStart = request.WeekStart;
        user.DateFormat = request.DateFormat;
        user.TimeFormat = request.TimeFormat;
        user.DurationFormat = request.DurationFormat;
        user.PomodoroEnabled = request.PomodoroEnabled;
        user.PomodoroMinutes = request.PomodoroMinutes;
        user.PomodoroBreakMinutes = request.PomodoroBreakMinutes;
        user.PomodoroAutoStop = request.PomodoroAutoStop;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ToResponseAsync(db, subscriptions, operators, user, cancellationToken));
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> UpdateFirstStepsAsync(
        FirstStepsRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        Operators operators,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var user = await FindAsync(db, principal, cancellationToken, tracking: true);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        user.FirstStepsHiddenAt = request.Hidden ? user.FirstStepsHiddenAt ?? timeProvider.GetUtcNow() : null;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await ToResponseAsync(db, subscriptions, operators, user, cancellationToken));
    }

    /// <summary>The signed-in person, unless the account was deleted while the access token was still valid.</summary>
    public static Task<AppUser?> FindAsync(AppDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken, bool tracking = false)
    {
        var userId = principal.GetUserId();
        var users = tracking ? db.Users : db.Users.AsNoTracking();
        return users.SingleOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, cancellationToken);
    }

    private static void Check(Dictionary<string, string[]> errors, string field, string value, string[] allowed)
    {
        if (!allowed.Contains(value))
        {
            errors[field] = [$"Choose one of: {string.Join(", ", allowed)}."];
        }
    }

    private static async Task<MeResponse> ToResponseAsync(
        AppDbContext db,
        OrganizationSubscriptions subscriptions,
        Operators operators,
        AppUser user,
        CancellationToken cancellationToken)
    {
        var organizations = await (
            from member in db.OrganizationMembers
            join organization in db.Organizations on member.OrganizationId equals organization.Id
            where member.UserId == user.Id
            orderby organization.Name
            select new OrganizationSummary(organization.Id, organization.Name, member.Role))
            .ToListAsync(cancellationToken);
        var standings = await subscriptions.StandingsAsync([.. organizations.Select(o => o.Id)], cancellationToken);
        organizations = [.. organizations.Select(o => o with
        {
            Access = standings[o.Id].Access,
            TrialEndsAt = standings[o.Id].TrialEndsAt,
        })];

        var workspaces = await (
            from member in db.WorkspaceMembers
            join workspace in db.Workspaces on member.WorkspaceId equals workspace.Id
            where member.UserId == user.Id
            orderby workspace.Name
            select new WorkspaceSummary(workspace.Id, workspace.OrganizationId, workspace.Name, member.Role))
            .ToListAsync(cancellationToken);

        return new MeResponse(
            user.Id,
            user.Email!,
            user.EmailConfirmed,
            user.FullName,
            user.TimeZone,
            PreferencesResponse.From(user),
            user.DefaultWorkspaceId,
            organizations,
            workspaces)
        {
            AgentActivity = AgentActivitySettingsResponse.From(user),
            IsOperator = operators.Includes(user),
            FirstSteps = user.FirstStepsHiddenAt is null,
            HasPassword = user.PasswordHash is not null,
        };
    }
}
