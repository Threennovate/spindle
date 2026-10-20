using System.Text.RegularExpressions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Spindle.Api.Common;

/// <summary>
/// Documents route parameters that a filter reads instead of the handler, such as <c>{workspaceId}</c>, which
/// <see cref="Workspaces.WorkspaceAccessFilter"/> checks for every endpoint in a workspace. Without this, generated
/// clients would not know to fill them in.
/// </summary>
internal sealed partial class RouteParametersTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var template = context.Description.ActionDescriptor.AttributeRouteInfo?.Template ?? context.Description.RelativePath ?? string.Empty;
        var documented = operation.Parameters?.Where(p => p.In == ParameterLocation.Path).Select(p => p.Name).ToHashSet() ?? [];

        var missing = RouteParameter().Matches(template)
            .Where(match => !documented.Contains(match.Groups["name"].Value))
            .Select(match => new OpenApiParameter
            {
                Name = match.Groups["name"].Value,
                In = ParameterLocation.Path,
                Required = true,
                Schema = match.Groups["constraint"].Value == "guid"
                    ? new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" }
                    : new OpenApiSchema { Type = JsonSchemaType.String },
            })
            .ToList();

        if (missing.Count > 0)
        {
            operation.Parameters ??= [];
            foreach (var parameter in missing)
            {
                operation.Parameters.Insert(0, parameter);
            }
        }

        return Task.CompletedTask;
    }

    [GeneratedRegex(@"\{(?<name>\w+)(:(?<constraint>\w+))?\}")]
    private static partial Regex RouteParameter();
}
