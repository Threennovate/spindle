using System.Text.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Spindle.Api.Common;

/// <summary>
/// Marks enums, which the API writes as camelCase strings, as string schemas. The document generator lists their values
/// but leaves the type out, and clients that go by the type, such as the Swift one, then see an untyped value instead
/// of an enum.
/// </summary>
internal sealed class StringEnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (schema.Type is null && schema.Enum is { Count: > 0 } values
            && values.All(value => value is null || value.GetValueKind() is JsonValueKind.String or JsonValueKind.Null)
            && values.Any(value => value?.GetValueKind() == JsonValueKind.String))
        {
            schema.Type = JsonSchemaType.String;
            // An enum that is nullable somewhere gets null among its values. Where it may be null, the property says so
            // already (oneOf null), and the null value would make the enum untyped for the clients again.
            for (var i = values.Count - 1; i >= 0; i--)
            {
                if (values[i] is null || values[i]!.GetValueKind() == JsonValueKind.Null)
                {
                    values.RemoveAt(i);
                }
            }
        }
        return Task.CompletedTask;
    }
}
