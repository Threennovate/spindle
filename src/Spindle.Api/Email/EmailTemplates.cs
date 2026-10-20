using System.Net;
using Spindle.Infrastructure.Email;

namespace Spindle.Api.Email;

/// <summary>
/// The emails the app sends. Plain HTML with inline styles, as most mail clients ignore stylesheets. The Spindle lockup is
/// a PNG the web app serves (email-logo.png), since mail clients don't show SVG; the button is marigold with midnight text.
/// </summary>
internal static class EmailTemplates
{
    public static EmailMessage Invitation(string to, string inviterName, string workspaceName, string link) => Build(
        to,
        subject: $"{inviterName} invited you to {workspaceName} on Spindle",
        heading: $"Join {workspaceName}",
        paragraph: $"{inviterName} invited you to track time in the {workspaceName} workspace on Spindle.",
        buttonText: "Accept invitation",
        link: link,
        footnote: "The invitation is valid for 14 days. If you were not expecting it, you can ignore this email.");

    public static EmailMessage ConfirmEmail(string to, string fullName, string link) => Build(
        to,
        subject: "Confirm your email for Spindle",
        heading: $"Welcome, {fullName}",
        paragraph: "Confirm that this is your email address, so you can reset your password if you ever forget it.",
        buttonText: "Confirm email",
        link: link,
        footnote: "The link works for a day. If you did not create an account on Spindle, you can ignore this email.");

    public static EmailMessage PasswordReset(string to, string link) => Build(
        to,
        subject: "Reset your Spindle password",
        heading: "Reset your password",
        paragraph: "Someone, hopefully you, asked to reset the password of your Spindle account. Choose a new one here.",
        buttonText: "Choose a new password",
        link: link,
        footnote: "The link works for a day and only once. If you did not ask for it, you can ignore this email; your password stays as it is.");

    public static EmailMessage TrialEnding(string to, string organizationName, int daysLeft, string link)
    {
        var when = daysLeft <= 1 ? "within a day" : $"in {daysLeft} days";
        return Build(
            to,
            subject: $"The Spindle trial of {organizationName} ends {when}",
            heading: $"Your trial ends {when}",
            paragraph: $"The trial of {organizationName} on Spindle ends {when}. After that, everyone in it can still see, "
                + "report and export their time, but nothing can be tracked or changed until you choose a plan.",
            buttonText: "See the plan",
            link: link,
            footnote: TrialFootnote(organizationName));
    }

    public static EmailMessage TrialEnded(string to, string organizationName, string link) => Build(
        to,
        subject: $"The Spindle trial of {organizationName} has ended",
        heading: "Your trial has ended",
        paragraph: $"{organizationName} is now read-only on Spindle. Nothing was deleted: everyone in it can still see, "
            + "report and export their time. Choose a plan to track time again.",
        buttonText: "Choose a plan",
        link: link,
        footnote: TrialFootnote(organizationName));

    private static string TrialFootnote(string organizationName) =>
        $"You get this email because you own or manage {organizationName} on Spindle.";

    private static EmailMessage Build(string to, string subject, string heading, string paragraph, string buttonText, string link, string footnote)
    {
        var text = $"{heading}\n\n{paragraph}\n\n{buttonText}: {link}\n\n{footnote}\n";
        Func<string, string> e = value => WebUtility.HtmlEncode(value);
        // The link always points at the app, so its origin also serves the logo.
        var logo = $"{new Uri(link).GetLeftPart(UriPartial.Authority)}/email-logo.png";
        var html = $"""
            <!doctype html>
            <html><body style="margin:0;padding:24px;background:#f7f6f3;font-family:-apple-system,Segoe UI,Helvetica,Arial,sans-serif;color:#0e1322">
              <table role="presentation" width="100%" style="max-width:480px;margin:0 auto;background:#ffffff;border-radius:12px;padding:32px">
                <tr><td>
                  <p style="margin:0 0 24px"><img src="{e(logo)}" width="99" height="27" alt="Spindle" style="display:block;border:0"></p>
                  <h1 style="margin:0 0 12px;font-size:20px">{e(heading)}</h1>
                  <p style="margin:0 0 24px;font-size:15px;line-height:1.5">{e(paragraph)}</p>
                  <a href="{e(link)}" style="display:inline-block;background:#f0b21b;color:#0e1322;text-decoration:none;padding:10px 18px;border-radius:10px;font-weight:600">{e(buttonText)}</a>
                  <p style="margin:24px 0 0;font-size:13px;line-height:1.5;color:#71717a">{e(footnote)}</p>
                  <p style="margin:12px 0 0;font-size:12px;color:#a1a1aa;word-break:break-all">{e(link)}</p>
                </td></tr>
              </table>
            </body></html>
            """;
        return new EmailMessage(to, subject, text, html);
    }
}
