using System.ComponentModel.DataAnnotations;
using Spindle.Infrastructure.Identity;

namespace Spindle.Api.Common;

public sealed class AppOptions : IValidatableObject
{
    public const string SectionName = "App";

    /// <summary>Where the web app is served, e.g. <c>https://track.example.com</c>. Used for links in emails.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// A folder served at <c>/downloads</c>: the Mac and Windows apps and their update feeds, and the iPhone app with its install page.
    /// A release copies files into it without deploying the site again, and a deploy leaves it alone. A relative path
    /// starts at the site's folder, e.g. <c>downloads</c> inside it. Without it, there are no downloads.
    /// </summary>
    public string? DownloadsPath { get; set; }

    /// <summary>
    /// The terms of service, e.g. on the landing page of the hosted service. With it, nobody can create an account
    /// without agreeing to them; without it, as on most self-hosted servers, there is nothing to agree to.
    /// </summary>
    [Url]
    public string? TermsUrl { get; set; }

    /// <summary>The privacy policy, linked next to the terms when people create an account.</summary>
    [Url]
    public string? PrivacyUrl { get; set; }

    /// <summary>
    /// The name of the current terms, e.g. <c>2026-10-01</c>, kept with every account that agrees to them. Required
    /// with <see cref="TermsUrl"/>; change it whenever the terms change.
    /// </summary>
    [StringLength(AppUser.TermsVersionMaxLength)]
    public string? TermsVersion { get; set; }

    /// <summary>
    /// Where people who use this server can get its source code, linked from the web app as the AGPL asks. A server
    /// that runs a modified version points it at that version's source.
    /// </summary>
    [Required]
    [Url]
    public string SourceUrl { get; set; } = "https://github.com/Threennovate/spindle";

    /// <summary>Whether new accounts must agree to terms of service.</summary>
    public bool HasTerms => !string.IsNullOrWhiteSpace(TermsUrl);

    /// <summary>
    /// Domains the app was served at before <see cref="BaseUrl"/>, e.g. after a move. Browsers that open one go to the
    /// same page on the new domain; the API and the downloads keep answering there for apps that haven't updated.
    /// </summary>
    public string[] PreviousHosts { get; set; } = [];

    public string Link(string path) => BaseUrl.TrimEnd('/') + path;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (HasTerms && string.IsNullOrWhiteSpace(TermsVersion))
        {
            yield return new ValidationResult("Name the version of the terms in App:TermsVersion.", [nameof(TermsVersion)]);
        }
    }
}
