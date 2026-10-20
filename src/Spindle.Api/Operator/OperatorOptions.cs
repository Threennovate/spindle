namespace Spindle.Api.Operator;

public sealed class OperatorOptions
{
    public const string SectionName = "Operators";

    /// <summary>
    /// The people who run this server: they see its organizations, without anyone's tracked data, and change
    /// subscriptions by hand. Set in configuration rather than in the app, so nobody can make themselves one. An
    /// address counts only once its owner has confirmed it.
    /// </summary>
    public string[] Emails { get; set; } = [];
}
