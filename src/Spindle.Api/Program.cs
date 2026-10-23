using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Spindle.Api.AgentActivity;
using Spindle.Api.Auth;
using Spindle.Api.Billing;
using Spindle.Api.Clients;
using Spindle.Api.Common;
using Spindle.Api.Import;
using Spindle.Api.Mcp;
using Spindle.Api.Me;
using Spindle.Api.Operator;
using Spindle.Api.Organizations;
using Spindle.Api.Projects;
using Spindle.Api.Push;
using Spindle.Api.Reports;
using Spindle.Api.Subscriptions;
using Spindle.Api.Sync;
using Spindle.Api.Tags;
using Spindle.Api.Team;
using Spindle.Api.TimeEntries;
using Spindle.Api.Web;
using Spindle.Api.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Spindle.Infrastructure;
using Spindle.Infrastructure.Persistence;

// The build generates openapi/spindle.json by running this program without any secrets,
// so required configuration is only enforced at startup when the app really runs.
var isGeneratingOpenApiDocument = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration, validateOnStart: !isGeneratingOpenApiDocument);
builder.Services.AddAuth(builder.Configuration, validateOnStart: !isGeneratingOpenApiDocument);
builder.Services.AddReverseProxy(builder.Configuration);
builder.Services.AddSpindleMcp(builder.Configuration);

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ValidationErrorNaming.Apply);
builder.Services.AddExceptionHandler<DomainValidationExceptionHandler>();
builder.Services.AddValidation();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

    // Numbers must be JSON numbers. The web default also accepts strings, which makes every integer
    // in the OpenAPI document "integer or string" and the generated clients awkward.
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Spindle API";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
    options.AddOperationTransformer<RouteParametersTransformer>();
    options.AddSchemaTransformer<StringEnumSchemaTransformer>();
});
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

var appOptions = builder.Services.AddOptions<AppOptions>()
    .Bind(builder.Configuration.GetSection(AppOptions.SectionName))
    .ValidateDataAnnotations();
var subscriptionOptions = builder.Services.AddOptions<SubscriptionOptions>()
    .Bind(builder.Configuration.GetSection(SubscriptionOptions.SectionName))
    .ValidateDataAnnotations();
if (!isGeneratingOpenApiDocument)
{
    appOptions.ValidateOnStart();
    subscriptionOptions.ValidateOnStart();
}

builder.Services.AddScoped<CurrentWorkspace>();
builder.Services.AddScoped<WorkspacePolicy>();
builder.Services.AddScoped<TimeEntryRules>();
builder.Services.AddScoped<TimeEntryReferences>();
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<RateBook>();
builder.Services.AddScoped<ReportLoader>();
builder.Services.AddScoped<AccountDeletion>();
builder.Services.AddScoped<SplitSuggestions>();
builder.Services.AddScoped<AccountEmails>();
builder.Services.AddScoped<ImportPlanner>();
builder.Services.AddScoped<OrganizationSubscriptions>();
builder.Services.AddScoped<NewWorkspaces>();
builder.Services.AddScoped<TrialReminders>();
builder.Services.AddHostedService<TrialReminderService>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<ISubscriptionProvider, PaddleProvider>((services, client) =>
    PaddleProvider.Configure(client, services.GetRequiredService<IOptions<SubscriptionOptions>>().Value.Paddle));
builder.Services.AddScoped<SubscriptionBilling>();
builder.Services.AddHostedService<SubscriptionSyncService>();
builder.Services.AddSingleton<SeatChanges>();
builder.Services.AddHostedService<SeatChangeService>();
builder.Services.Configure<OperatorOptions>(builder.Configuration.GetSection(OperatorOptions.SectionName));
builder.Services.AddScoped<Operators>();
builder.Services.AddScoped<CurrentOperator>();

builder.Services.Configure<PushOptions>(builder.Configuration.GetSection(PushOptions.SectionName));
builder.Services.AddSingleton<ApnsProviderToken>();
builder.Services.AddHttpClient<ApnsClient>(ApnsClient.Configure);
builder.Services.AddScoped<LiveActivityPushes>();
builder.Services.AddSingleton<LiveActivityChanges>();
builder.Services.AddHostedService<LiveActivityPushService>();

builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ISaveChangesInterceptor, SyncInterceptor>();

// Emails about accounts go to addresses that anyone can type in, so each network address gets a few per quarter hour.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var passkeyPermits = builder.Configuration.GetValue("RateLimits:PasskeyRequestsPerMinute", 60);
    options.AddPolicy(PasskeyEndpoints.RateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = passkeyPermits, Window = TimeSpan.FromMinutes(1) }));
    var externalPermits = builder.Configuration.GetValue("RateLimits:ExternalSignInRequestsPerMinute", 60);
    options.AddPolicy(ExternalSignInEndpoints.RateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = externalPermits, Window = TimeSpan.FromMinutes(1) }));
    // Timesheet links are opened by anyone who has one, so each network address gets a page a second or so.
    var sharedPermits = builder.Configuration.GetValue("RateLimits:SharedReportRequestsPerMinute", 60);
    options.AddPolicy(SharedReportEndpoints.RateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = sharedPermits, Window = TimeSpan.FromMinutes(1) }));
    var permits = builder.Configuration.GetValue("RateLimits:AccountEmailsPerQuarterHour", 5);
    options.AddPolicy(PasswordEndpoints.EmailRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(15) }));
});

var app = builder.Build();

// A single application instance applies migrations before it accepts requests.
if (!isGeneratingOpenApiDocument && app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    // Said before connecting, so a log that ends here shows the app started but couldn't reach the database.
    app.Logger.LogInformation("Bringing the database up to date");
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    app.Logger.LogInformation("The database is up to date");
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSecurityHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHsts();
}

// It reads App:BaseUrl, which generating the OpenAPI document at build time doesn't have.
if (!isGeneratingOpenApiDocument)
{
    app.UsePreviousHostRedirect();
}

app.UseWebAppStaticFiles();
app.UseDownloads(app.Configuration["App:DownloadsPath"], app.Environment.ContentRootPath, app.Logger);
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health");
app.MapAppleAssociation();
app.MapDownloadLinks();
app.MapHub<SyncHub>(SyncHub.Path);
app.MapSpindleMcp();

var api = app.MapGroup("/api");
api.MapAuthEndpoints();
api.MapPasswordEndpoints();
api.MapWebAuthEndpoints();
api.MapPasskeyEndpoints();
api.MapExternalSignInEndpoints();
api.MapMeEndpoints();
api.MapAccountEndpoints();
api.MapPushDeviceEndpoints();
api.MapCurrentTimeEntryEndpoint();
api.MapAgentActivityEndpoints();
api.MapWorkspaceEndpoints();
api.MapOrganizationEndpoints();
api.MapPublicInvitationEndpoints();
api.MapPublicSharedReportEndpoints();
api.MapSubscriptionEndpoints();
api.MapOperatorEndpoints();

var workspace = api.MapWorkspaceGroup();
workspace.MapTimeEntryEndpoints();
workspace.MapSplitSuggestionEndpoints();
workspace.MapAgentProposalEndpoints();
workspace.MapProjectEndpoints();
workspace.MapClientEndpoints();
workspace.MapTagEndpoints();
workspace.MapMemberEndpoints();
workspace.MapInvitationEndpoints();
workspace.MapGroupEndpoints();
workspace.MapProjectMemberEndpoints();
workspace.MapWorkspaceSettingsEndpoints();
workspace.MapRateEndpoints();
workspace.MapBillingEndpoints();
workspace.MapAgreementEndpoints();
workspace.MapProfitabilityEndpoints();
workspace.MapReportEndpoints();
workspace.MapSavedReportEndpoints();
workspace.MapSharedReportEndpoints();
workspace.MapImportEndpoints();

app.MapWebAppFallback();

app.Run();
