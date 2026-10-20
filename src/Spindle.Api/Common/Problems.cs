using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Spindle.Domain.Common;

namespace Spindle.Api.Common;

internal static class Problems
{
    public static ValidationProblem Validation(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    public static ValidationProblem Validation(IDictionary<string, string[]> errors) => TypedResults.ValidationProblem(errors);
}

/// <summary>Turns a broken business rule into a 400 with the message next to the offending field.</summary>
internal sealed class DomainValidationExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DomainValidationException error)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new HttpValidationProblemDetails(new Dictionary<string, string[]> { [error.Field] = [error.Message] })
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more validation errors occurred.",
            },
        });
    }
}

internal static class DbUpdateExceptionExtensions
{
    /// <summary>A unique index rejected the write, e.g. a duplicate name. Optionally only for the named index.</summary>
    public static bool IsUniqueViolation(this DbUpdateException exception, string? indexName = null) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && (indexName is null || postgres.ConstraintName == indexName);

    /// <summary>Saves, or returns false when a unique index rejects the write.</summary>
    public static async Task<bool> TrySaveChangesAsync(this DbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return false;
        }
    }
}
