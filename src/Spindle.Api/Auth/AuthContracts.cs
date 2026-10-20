using System.ComponentModel.DataAnnotations;
using Spindle.Api.Common;
using Spindle.Infrastructure.Identity;

namespace Spindle.Api.Auth;

internal static class PasswordRules
{
    public const int MinLength = 8;
    public const int MaxLength = 128;
}

public sealed record RegisterRequest
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [StringLength(PasswordRules.MaxLength, MinimumLength = PasswordRules.MinLength)]
    public string Password { get; init; } = string.Empty;

    [Required]
    [StringLength(AppUser.FullNameMaxLength)]
    public string FullName { get; init; } = string.Empty;

    /// <summary>IANA time zone of the device, e.g. <c>Europe/Zagreb</c>.</summary>
    [Required]
    [IanaTimeZone]
    public string TimeZone { get; init; } = string.Empty;

    /// <summary>Shown in the list of signed-in devices, e.g. "MacBook Pro".</summary>
    [StringLength(RefreshToken.DeviceNameMaxLength)]
    public string? DeviceName { get; init; }

    /// <summary>
    /// The token from an invitation link. The account then joins the inviting workspace instead of getting a
    /// workspace of its own, and the email must be the invited one.
    /// </summary>
    [StringLength(100)]
    public string? InvitationToken { get; init; }

    /// <summary>
    /// That the person agrees to the server's terms of service and privacy policy. Required when the server has
    /// terms (<c>termsUrl</c> in <c>GET /api/server</c>).
    /// </summary>
    public bool AcceptTerms { get; init; }
}

public sealed record LoginRequest
{
    [Required]
    [EmailAddress]
    [StringLength(256)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [StringLength(PasswordRules.MaxLength)]
    public string Password { get; init; } = string.Empty;

    [StringLength(RefreshToken.DeviceNameMaxLength)]
    public string? DeviceName { get; init; }
}

public sealed record RefreshRequest
{
    [Required]
    public string RefreshToken { get; init; } = string.Empty;
}

public sealed record LogoutRequest
{
    [Required]
    public string RefreshToken { get; init; } = string.Empty;
}

public sealed record AuthTokensResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);
