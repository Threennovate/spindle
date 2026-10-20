namespace Spindle.Infrastructure.Identity;

/// <summary>
/// The outcome of signing in with Google or Apple, waiting for the browser or app that started it. Its code travels in
/// a redirect, so it lives for a minute, is used once, and only the client holding the secret that matches
/// <see cref="BindingHash"/> can redeem it.
/// </summary>
public sealed class ExternalSignInCode
{
    public const int ErrorMaxLength = 500;
    public const int ReturnPathMaxLength = 2000;

    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The SHA-256 of the client's secret, base64url: the browser's cookie or the app's PKCE verifier.</summary>
    public string BindingHash { get; set; } = string.Empty;

    /// <summary>The account signed in, when it worked.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Why it didn't work, in words for the person.</summary>
    public string? Error { get; set; }

    /// <summary>Whether signing in created the account.</summary>
    public bool NewAccount { get; set; }

    /// <summary>Where the web app goes afterwards, a path on this server.</summary>
    public string? ReturnPath { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
