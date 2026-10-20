using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Auth;

internal static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddAuth(this IServiceCollection services, IConfiguration configuration, bool validateOnStart)
    {
        var authOptions = services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .ValidateDataAnnotations();

        if (validateOnStart)
        {
            authOptions.ValidateOnStart();
        }

        services
            .AddIdentityCore<AppUser>(options =>
            {
                // The user name is the email address, which EmailAddress validation already covers.
                options.User.AllowedUserNameCharacters = string.Empty;
                options.User.RequireUniqueEmail = true;

                // Length over composition rules, as NIST SP 800-63B recommends.
                options.Password.RequiredLength = PasswordRules.MinLength;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        // Links in password reset and confirmation emails work for a day. A reset link also stops working once the
        // password changes, since the token includes the account's security stamp.
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromDays(1));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((bearer, auth) =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = auth.Value.CreateValidationParameters();
                bearer.Events = new JwtBearerEvents
                {
                    // Browsers cannot set headers on a WebSocket, so the sync hub takes the token from the query.
                    OnMessageReceived = context =>
                    {
                        if (context.Request.Path.StartsWithSegments(Sync.SyncHub.Path) && context.Request.Query["access_token"] is { Count: > 0 } token)
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorization();

        services.AddOptions<IdentityPasskeyOptions>().Configure<IOptions<Common.AppOptions>>((options, app) =>
        {
            var origin = new Uri(app.Value.BaseUrl);
            options.ServerDomain = origin.Host;
            options.ResidentKeyRequirement = "required";
            options.UserVerificationRequirement = "required";
            options.AuthenticatorTimeout = PasskeyChallenges.Lifetime;
            options.ValidateOrigin = context => ValueTask.FromResult(
                !context.CrossOrigin && string.Equals(context.Origin, origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal));
        });

        services.Configure<GoogleSignInOptions>(configuration.GetSection(GoogleSignInOptions.SectionName));
        services.Configure<AppleSignInOptions>(configuration.GetSection(AppleSignInOptions.SectionName));
        services.AddHttpClient(OidcSignInProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
        // Google first, as most people have an account there; the apps show the buttons in this order.
        services.AddSingleton<IExternalSignInProvider, GoogleSignIn>();
        services.AddSingleton<IExternalSignInProvider, AppleSignIn>();
        services.AddScoped<ExternalAccounts>();

        services.AddSingleton<AccessTokenIssuer>();
        services.AddScoped<PasskeyChallenges>();
        services.AddScoped<RefreshTokenService>();
        services.AddScoped<AuthService>();

        return services;
    }
}
