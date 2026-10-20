using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Spindle.Api.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    /// <summary>HMAC key for signing access tokens. At least 32 characters; never commit it.</summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    [Range(typeof(TimeSpan), "1.00:00:00", "365.00:00:00")]
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How long a just-rotated refresh token may be retried by a client that never got the response.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:05:00")]
    public TimeSpan RefreshTokenReuseGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether anyone can create an account. With <c>false</c>, people only join through invitations, which keeps
    /// strangers off a self-hosted server.
    /// </summary>
    public bool AllowRegistration { get; set; } = true;

    public SymmetricSecurityKey CreateSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey));

    public TokenValidationParameters CreateValidationParameters() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = CreateSigningKey(),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        NameClaimType = JwtRegisteredClaimNames.Sub,
        ClockSkew = TimeSpan.FromSeconds(30),
    };
}
