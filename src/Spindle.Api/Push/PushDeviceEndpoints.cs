using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Spindle.Api.Auth;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Push;

/// <summary>
/// Where APNs reaches this iPhone's Live Activity. The app sends it whenever a token changes; a token left out is
/// forgotten, e.g. once Live Activities are turned off.
/// </summary>
/// <param name="Sandbox">Whether the app was built for Apple's sandbox, as builds from Xcode are.</param>
/// <param name="LiveActivityToken">Updates and ends the Live Activity the device shows, of <paramref name="LiveActivityEntryId"/>.</param>
public sealed record PushDeviceRequest(
    bool Sandbox,
    [property: MaxLength(PushDevice.TokenMaxLength), RegularExpression("^[0-9a-fA-F]+$")] string? LiveActivityToken,
    Guid? LiveActivityEntryId);

internal static class PushDeviceEndpoints
{
    public static IEndpointRouteBuilder MapPushDeviceEndpoints(this IEndpointRouteBuilder api)
    {
        var device = api.MapGroup("/me/push-device").WithTags("Account").RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        device.MapPut("", RegisterAsync).WithName("registerPushDevice");

        return api;
    }

    /// <summary>
    /// Registers this device session's push tokens. A server without APNs settings keeps nothing, and says so with
    /// <c>false</c>, so the app knows not to expect pushes.
    /// </summary>
    private static async Task<Results<Ok<bool>, BadRequest>> RegisterAsync(
        PushDeviceRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        IOptions<PushOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (principal.GetSessionId() is not { } sessionId)
        {
            return TypedResults.BadRequest();
        }

        if (!options.Value.Apns.IsConfigured)
        {
            return TypedResults.Ok(false);
        }

        var userId = principal.GetUserId();
        var device = await db.PushDevices.SingleOrDefaultAsync(d => d.SessionId == sessionId, cancellationToken);
        if (device is null)
        {
            device = new PushDevice { SessionId = sessionId, UserId = userId, UpdatedAt = timeProvider.GetUtcNow() };
            db.PushDevices.Add(device);
        }

        var entryId = request.LiveActivityToken is null ? null : request.LiveActivityEntryId;
        if (device.LiveActivityToken != request.LiveActivityToken || device.LiveActivityEntryId != entryId)
        {
            // What the activity shows now is the device's own doing, not something sent.
            device.LastSent = null;
        }

        device.Sandbox = request.Sandbox;
        device.LiveActivityToken = request.LiveActivityToken;
        device.LiveActivityEntryId = entryId;
        device.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(true);
    }
}
