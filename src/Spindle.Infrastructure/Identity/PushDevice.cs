namespace Spindle.Infrastructure.Identity;

/// <summary>
/// Where Apple's push service reaches one signed-in iPhone, so a timer changed or stopped elsewhere shows in its Live
/// Activity. One per device session (<see cref="SessionId"/>, the refresh token family): signing the device out ends
/// it. Plain rows deleted for good, like the tokens themselves.
/// </summary>
public sealed class PushDevice
{
    /// <summary>APNs tokens are hex, 64 characters today; Apple says not to rely on the length.</summary>
    public const int TokenMaxLength = 200;

    public required Guid SessionId { get; init; }

    public required Guid UserId { get; init; }

    /// <summary>Whether the app was built for Apple's sandbox (from Xcode) rather than production (TestFlight, App Store).</summary>
    public bool Sandbox { get; set; }

    /// <summary>The entry the device's Live Activity shows.</summary>
    public Guid? LiveActivityEntryId { get; set; }

    /// <summary>Updates and ends the Live Activity of <see cref="LiveActivityEntryId"/>.</summary>
    public string? LiveActivityToken { get; set; }

    /// <summary>A hash of what the Live Activity was last sent, so an edit to another entry sends nothing.</summary>
    public string? LastSent { get; set; }

    public required DateTimeOffset UpdatedAt { get; set; }
}
