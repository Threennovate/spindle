using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using Spindle.Api.Auth;
using Spindle.Api.Common;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Mcp;

/// <summary>
/// Spindle's MCP server at <c>/mcp</c> (Streamable HTTP, stateless) and the OAuth server that lets agents in. Its
/// tokens are JWTs like the apps', but for the MCP server's URL as their audience: the API refuses them, and the MCP
/// server refuses the apps'. Each request also checks that the agent's device session is still signed in, so signing
/// it out under Account stops it at once rather than when its token lapses.
/// </summary>
internal static class McpServerSetup
{
    public static IServiceCollection AddSpindleMcp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication().AddJwtBearer(McpOAuth.Scheme);
        services.AddOptions<JwtBearerOptions>(McpOAuth.Scheme)
            .Configure<IOptions<AuthOptions>, IOptions<AppOptions>>((bearer, auth, app) =>
            {
                bearer.MapInboundClaims = false;
                var validation = auth.Value.CreateValidationParameters();
                validation.ValidAudience = McpOAuth.Resource(app.Value);
                bearer.TokenValidationParameters = validation;
                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var services = context.HttpContext.RequestServices;
                        var sessionId = context.Principal?.GetSessionId();
                        var signedIn = sessionId is { } id
                            && await services.GetRequiredService<AppDbContext>().AgentConnections.AnyAsync(c => c.SessionId == id, context.HttpContext.RequestAborted)
                            && await services.GetRequiredService<RefreshTokenService>().IsActiveAsync(id, context.HttpContext.RequestAborted);
                        if (!signedIn)
                        {
                            context.Fail("The agent was signed out.");
                        }
                    },
                    // Tells the agent where to sign in (RFC 9728, section 5.1).
                    OnChallenge = context =>
                    {
                        context.HandleResponse();
                        var app = context.HttpContext.RequestServices.GetRequiredService<IOptions<AppOptions>>().Value;
                        var error = context.AuthenticateFailure is null ? "" : ", error=\"invalid_token\"";
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate =
                            $"Bearer resource_metadata=\"{McpOAuth.ResourceMetadataUrl(app)}\", scope=\"{string.Join(' ', McpOAuth.Scopes)}\"{error}";
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder().AddPolicy(McpOAuth.Policy, policy => policy
            .AddAuthenticationSchemes(McpOAuth.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(AccessTokenIssuer.WorkspaceClaim));

        services.AddCors(options => options.AddPolicy(McpOAuth.CorsPolicy, cors => cors
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("WWW-Authenticate", "Mcp-Session-Id", "Mcp-Protocol-Version")));

        var registrations = configuration.GetValue("RateLimits:OAuthRegistrationsPerHour", 30);
        services.AddRateLimiter(options => options.AddPolicy(McpOAuth.RegistrationRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = registrations, Window = TimeSpan.FromHours(1) })));

        services.AddHttpClient<ClientMetadataDocuments>(ClientMetadataDocuments.Configure)
            .ConfigurePrimaryHttpMessageHandler(ClientMetadataDocuments.CreateHandler);
        services.AddScoped<OAuthClients>();
        services.AddScoped<AgentEvidence>();
        services.AddScoped<McpTools>();

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "spindle", Title = "Spindle", Version = "1.0.0" };
                options.ServerInstructions = McpTools.Instructions;
            })
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ModelContextProtocol.Protocol.ListToolsResult { Tools = McpTools.Tools }))
            .WithCallToolHandler((context, cancellationToken) =>
            {
                var http = context.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext!;
                return http.RequestServices.GetRequiredService<McpTools>().CallAsync(http.User, context.Params!, cancellationToken);
            });

        return services;
    }

    public static IEndpointRouteBuilder MapSpindleMcp(this IEndpointRouteBuilder app)
    {
        app.MapOAuthEndpoints();
        app.MapMcp(McpOAuth.McpPath).RequireAuthorization(McpOAuth.Policy).RequireCors(McpOAuth.CorsPolicy).ExcludeFromDescription();
        return app;
    }
}
