using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Billing;
using Spindle.Domain.Billing;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Workspaces;

/// <param name="DefaultHourlyRate">The rate as last set. Shown to admins only.</param>
public sealed record WorkspaceSettingsResponse(
    string Name,
    string Currency,
    decimal? DefaultHourlyRate,
    bool OnlyAdminsCreateProjects,
    bool OnlyAdminsCreateTags,
    DateOnly? LockedBefore,
    bool RequireDescription,
    bool RequireProject,
    bool RequireTask,
    bool RequireTag)
{
    public static WorkspaceSettingsResponse From(Workspace workspace, decimal? defaultHourlyRate) => new(
        workspace.Name,
        workspace.Currency,
        defaultHourlyRate,
        workspace.OnlyAdminsCreateProjects,
        workspace.OnlyAdminsCreateTags,
        workspace.LockedBefore,
        workspace.RequireDescription,
        workspace.RequireProject,
        workspace.RequireTask,
        workspace.RequireTag);
}

public sealed record WorkspaceSettingsRequest
{
    [Required]
    [StringLength(Workspace.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    /// <summary>ISO 4217 code, e.g. <c>EUR</c>.</summary>
    [Required]
    [RegularExpression("^[A-Z]{3}$")]
    public string Currency { get; init; } = "EUR";

    [Range(0, 1_000_000)]
    public decimal? DefaultHourlyRate { get; init; }

    /// <summary>
    /// The first day a changed default rate applies to; earlier time keeps the rate it had. Left out, the current
    /// rate is corrected for all the time it covers.
    /// </summary>
    public DateOnly? DefaultHourlyRateFrom { get; init; }

    public bool OnlyAdminsCreateProjects { get; init; }

    public bool OnlyAdminsCreateTags { get; init; }

    /// <summary>Members cannot change time on days before this date.</summary>
    public DateOnly? LockedBefore { get; init; }

    public bool RequireDescription { get; init; }

    public bool RequireProject { get; init; }

    public bool RequireTask { get; init; }

    public bool RequireTag { get; init; }
}

internal static class WorkspaceSettingsEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceSettingsEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/settings").WithTags("Workspaces");

        // Everyone reads them: the apps need to know what is locked and required, and what members may create.
        group.MapGet("", GetAsync).WithName("getWorkspaceSettings");
        group.MapPut("", UpdateAsync).WithName("updateWorkspaceSettings").RequireWorkspaceAdmin();

        return workspace;
    }

    private static async Task<Ok<WorkspaceSettingsResponse>> GetAsync(
        CurrentWorkspace current,
        AppDbContext db,
        RateBook rates,
        CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == current.Id, cancellationToken);
        var rate = current.IsAdmin ? (await rates.CardAsync(cancellationToken)).Current(RateScope.WorkspaceDefault) : null;
        return TypedResults.Ok(WorkspaceSettingsResponse.From(workspace, rate));
    }

    private static async Task<Ok<WorkspaceSettingsResponse>> UpdateAsync(
        WorkspaceSettingsRequest request,
        CurrentWorkspace current,
        AppDbContext db,
        RateBook rates,
        CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.SingleAsync(w => w.Id == current.Id, cancellationToken);
        await rates.SetAsync(
            RateScope.WorkspaceDefault,
            request.DefaultHourlyRate,
            request.DefaultHourlyRateFrom,
            "defaultHourlyRate",
            "defaultHourlyRateFrom",
            cancellationToken);
        workspace.UpdateSettings(new WorkspaceSettings(
            request.Name,
            request.Currency,
            request.OnlyAdminsCreateProjects,
            request.OnlyAdminsCreateTags,
            request.LockedBefore,
            request.RequireDescription,
            request.RequireProject,
            request.RequireTask,
            request.RequireTag));
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(WorkspaceSettingsResponse.From(workspace, (await rates.CardAsync(cancellationToken)).Current(RateScope.WorkspaceDefault)));
    }
}
