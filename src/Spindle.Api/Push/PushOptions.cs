namespace Spindle.Api.Push;

/// <summary>
/// Apple's push service, which shows a timer changed or stopped elsewhere in the iPhone's Live Activity. Spindle Cloud
/// only: it needs an APNs key of the team that signs the iPhone app. Without it, devices aren't registered and
/// nothing is sent; the apps still show the timer when opened.
/// </summary>
public sealed class PushOptions
{
    public const string SectionName = "Push";

    public ApnsOptions Apns { get; set; } = new();
}

public sealed class ApnsOptions
{
    /// <summary>The key's ID, from the Keys page of the Apple Developer account.</summary>
    public string? KeyId { get; set; }

    /// <summary>The ID of the team the key and the app belong to.</summary>
    public string? TeamId { get; set; }

    /// <summary>
    /// The key itself: the text of the downloaded <c>.p8</c> file, from <c>-----BEGIN PRIVATE KEY-----</c> on. Line breaks
    /// may be written as <c>\n</c>, for an environment variable on one line. A secret.
    /// </summary>
    public string? PrivateKey { get; set; }

    /// <summary>The iPhone app's bundle ID.</summary>
    public string BundleId { get; set; } = "com.spindletrack.iphone";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(TeamId) && !string.IsNullOrWhiteSpace(PrivateKey);
}
