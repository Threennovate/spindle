using System.Text.Json;

namespace Spindle.Api.Common;

/// <summary>
/// Makes validation error keys match the JSON property names clients send, e.g. <c>timeZone</c> rather than
/// the C# name <c>TimeZone</c>, so clients can show each error next to its field.
/// </summary>
internal static class ValidationErrorNaming
{
    public static void Apply(ProblemDetailsContext context)
    {
        if (context.ProblemDetails is not HttpValidationProblemDetails { Errors.Count: > 0 } problem)
        {
            return;
        }

        var errors = problem.Errors
            .GroupBy(error => ToJsonPath(error.Key), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.SelectMany(error => error.Value).ToArray());

        problem.Errors.Clear();
        foreach (var (key, messages) in errors)
        {
            problem.Errors[key] = messages;
        }
    }

    // "Items[0].StartedAt" -> "items[0].startedAt"
    private static string ToJsonPath(string key) =>
        string.Join('.', key.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
}
