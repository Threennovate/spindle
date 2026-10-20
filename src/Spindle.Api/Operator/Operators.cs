using Microsoft.Extensions.Options;
using Spindle.Api.Me;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Operator;

internal sealed class Operators(IOptions<OperatorOptions> options)
{
    public bool Includes(AppUser user) =>
        user.EmailConfirmed
        && user.Email is { } email
        && options.Value.Emails.Any(o => string.Equals(o.Trim(), email, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The operator making the current request, once <see cref="OperatorFilter"/> has let them through.</summary>
internal sealed class CurrentOperator
{
    private AppUser? user;

    public AppUser User => user ?? throw new InvalidOperationException("The endpoint is not behind OperatorFilter.");

    public void Set(AppUser value) => user = value;
}

/// <summary>Lets only operators through. To anyone else these endpoints don't exist.</summary>
internal sealed class OperatorFilter(AppDbContext db, Operators operators, CurrentOperator current) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var user = await MeEndpoints.FindAsync(db, http.User, http.RequestAborted);
        if (user is null || !operators.Includes(user))
        {
            return TypedResults.NotFound();
        }

        current.Set(user);
        return await next(context);
    }
}
