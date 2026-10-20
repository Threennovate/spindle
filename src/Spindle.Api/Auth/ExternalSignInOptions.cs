namespace Spindle.Api.Auth;

/// <summary>
/// "Continue with Google": an OAuth client of type "Web application" in a Google Cloud project, whose authorized
/// redirect URI is <c>{App:BaseUrl}/api/auth/external/google/callback</c>. Without both values the button isn't offered.
/// </summary>
public sealed class GoogleSignInOptions
{
    public const string SectionName = "Auth:Google";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>
/// "Continue with Apple": a Services ID with Sign in with Apple, whose return URL is
/// <c>{App:BaseUrl}/api/auth/external/apple/callback</c>, and a Sign in with Apple key. Without all four values the
/// button isn't offered.
/// </summary>
public sealed class AppleSignInOptions
{
    public const string SectionName = "Auth:Apple";

    /// <summary>The Services ID, e.g. <c>com.spindletrack.signin</c>. Apple calls it the client ID.</summary>
    public string? ServicesId { get; set; }

    /// <summary>The team that owns the Services ID and the key, e.g. <c>9ARDA688G5</c>.</summary>
    public string? TeamId { get; set; }

    /// <summary>The ID of the Sign in with Apple key.</summary>
    public string? KeyId { get; set; }

    /// <summary>
    /// The contents of the key's .p8 file. Line breaks may be written as <c>\n</c>, for settings that take one line.
    /// </summary>
    public string? PrivateKey { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServicesId) && !string.IsNullOrWhiteSpace(TeamId)
        && !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(PrivateKey);
}
