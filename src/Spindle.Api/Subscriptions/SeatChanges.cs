using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Spindle.Api.Subscriptions;

/// <summary>
/// Organizations whose seats may have just changed: someone joined, was removed, or deleted their account. The plan
/// is brought in line in the background, so nobody waits for the provider; the hourly reconciliation still catches
/// anything lost, e.g. in a restart.
/// </summary>
internal sealed class SeatChanges
{
    private readonly Channel<Guid> changes = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<Guid> Reader => changes.Reader;

    public void Changed(Guid organizationId) => changes.Writer.TryWrite(organizationId);
}

/// <summary>Brings the plans of organizations in <see cref="SeatChanges"/> in line with their seats, a moment after they change.</summary>
internal sealed class SeatChangeService(
    SeatChanges changes,
    IServiceScopeFactory scopes,
    IOptions<SubscriptionOptions> subscriptions,
    TimeProvider timeProvider,
    ILogger<SeatChangeService> logger) : BackgroundService
{
    /// <summary>
    /// How long to wait after a change before telling the provider: the change is saved by then, and several that come
    /// together, such as a team accepting their invitations, become one.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var first in changes.Reader.ReadAllAsync(stoppingToken))
        {
            await Task.Delay(Settle, timeProvider, stoppingToken);
            HashSet<Guid> organizations = [first];
            while (changes.Reader.TryRead(out var next))
            {
                organizations.Add(next);
            }

            if (!subscriptions.Value.Enabled || !subscriptions.Value.Paddle.IsConfigured)
            {
                continue;
            }

            await using var scope = scopes.CreateAsyncScope();
            var billing = scope.ServiceProvider.GetRequiredService<SubscriptionBilling>();
            foreach (var organizationId in organizations)
            {
                try
                {
                    await billing.ReconcileOrganizationAsync(organizationId, stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Could not bring the plan of organization {OrganizationId} in line with its seats yet", organizationId);
                }
            }
        }
    }
}
