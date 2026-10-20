using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Push;

/// <summary>
/// People whose entries just changed, so their iPhones' Live Activities may need to start, change or end. The device
/// session that made the change is named: it shows the change itself.
/// </summary>
internal sealed class LiveActivityChanges
{
    private readonly Channel<(Guid UserId, Guid? Origin)> changes =
        Channel.CreateUnbounded<(Guid, Guid?)>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<(Guid UserId, Guid? Origin)> Reader => changes.Reader;

    public void Changed(Guid userId, Guid? origin) => changes.Writer.TryWrite((userId, origin));
}

/// <summary>Sends the Live Activity pushes for <see cref="LiveActivityChanges"/>, a moment after the change.</summary>
internal sealed class LiveActivityPushService(
    LiveActivityChanges changes,
    IServiceScopeFactory scopes,
    IOptions<PushOptions> options,
    TimeProvider timeProvider,
    ILogger<LiveActivityPushService> logger) : BackgroundService
{
    /// <summary>Changes that come together, such as stopping one timer and starting another, become one push.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var first in changes.Reader.ReadAllAsync(stoppingToken))
        {
            await Task.Delay(Settle, timeProvider, stoppingToken);
            // A person's changes from different devices in one go leave out none of them.
            var people = new Dictionary<Guid, Guid?> { [first.UserId] = first.Origin };
            while (changes.Reader.TryRead(out var next))
            {
                people[next.UserId] = people.TryGetValue(next.UserId, out var origin) && origin != next.Origin ? null : next.Origin;
            }

            if (!options.Value.Apns.IsConfigured)
            {
                continue;
            }

            await using var scope = scopes.CreateAsyncScope();
            var pushes = scope.ServiceProvider.GetRequiredService<LiveActivityPushes>();
            foreach (var (userId, origin) in people)
            {
                try
                {
                    await pushes.SendAsync(userId, origin, stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Could not update the Live Activities of user {UserId}", userId);
                }
            }
        }
    }
}

/// <summary>
/// Brings each of a person's iPhones in line with their running timer: changes its Live Activity with the entry, and
/// ends it when the timer stops.
/// </summary>
internal sealed class LiveActivityPushes(AppDbContext db, ApnsClient apns, TimeProvider timeProvider)
{
    /// <summary>What <see cref="PushDevice.LastSent"/> holds once the activity was told to end.</summary>
    private const string Ended = "ended";

    /// <summary>A Live Activity push may be 4 KB; the activity shows a line of the description anyway.</summary>
    private const int DescriptionLength = 200;

    private static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task SendAsync(Guid userId, Guid? origin, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        // A device signed out, or whose session lapsed, hears nothing more.
        await db.PushDevices
            .Where(d => d.UserId == userId && !db.RefreshTokens.Any(t => t.FamilyId == d.SessionId && t.RevokedAt == null && t.ExpiresAt > now))
            .ExecuteDeleteAsync(cancellationToken);
        var devices = await db.PushDevices.Where(d => d.UserId == userId).ToListAsync(cancellationToken);
        if (devices.Count == 0)
        {
            return;
        }

        var state = await StateAsync(userId, cancellationToken);
        var sent = state is null ? Ended : Hash(state);
        foreach (var device in devices)
        {
            if (device.SessionId == origin)
            {
                // It changed its own activity; what it was sent before no longer says what it shows.
                device.LastSent = null;
                continue;
            }

            if (device.LastSent == sent)
            {
                continue;
            }

            await BringInLineAsync(device, state, now, cancellationToken);
            device.LastSent = sent;
            device.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Changes the device's activity with its entry, or ends it once that entry no longer runs. A timer started
    /// elsewhere isn't pushed: starting an activity by push asks for an alert, which may sound, so the app shows it
    /// when next opened.
    /// </summary>
    private async Task BringInLineAsync(PushDevice device, JsonObject? state, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (device.LiveActivityToken is not { } token)
        {
            return;
        }

        var entryId = state is null ? (Guid?)null : Guid.Parse(state["entryId"]!.GetValue<string>());
        if (state is not null && device.LiveActivityEntryId == entryId)
        {
            if (await apns.SendLiveActivityAsync(token, device.Sandbox, Payload("update", now, state), cancellationToken) == ApnsResult.Gone)
            {
                device.LiveActivityToken = null;
                device.LiveActivityEntryId = null;
            }

            return;
        }

        var end = Payload("end", now, state ?? LastState(device.LiveActivityEntryId));
        end["aps"]!["dismissal-date"] = now.ToUnixTimeSeconds();
        await apns.SendLiveActivityAsync(token, device.Sandbox, end, cancellationToken);
        device.LiveActivityToken = null;
        device.LiveActivityEntryId = null;
    }

    private static JsonObject Payload(string @event, DateTimeOffset now, JsonObject state) => new()
    {
        ["aps"] = new JsonObject
        {
            ["timestamp"] = now.ToUnixTimeSeconds(),
            ["event"] = @event,
            ["content-state"] = state.DeepClone(),
        },
    };

    /// <summary>An ending needs a content state; the entry is gone or stopped, so any that decodes will do.</summary>
    private static JsonObject LastState(Guid? entryId) => new()
    {
        ["entryId"] = entryId?.ToString() ?? "",
        ["description"] = "",
        ["start"] = 0.0,
    };

    /// <summary>
    /// The running timer as the app's <c>TimerActivityAttributes.ContentState</c>, as LiveActivityController makes it:
    /// a split entry shows its projects together, in the color of its main project. The start is in seconds since 2001,
    /// as Swift encodes a date by default; ActivityKit decodes the state that way.
    /// </summary>
    private async Task<JsonObject?> StateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var running = await db.TimeEntries.AsNoTracking().SingleOrDefaultAsync(e => e.UserId == userId && e.Stop == null, cancellationToken);
        if (running is null)
        {
            return null;
        }

        var parts = running.IsSplit ? running.Parts.Select(p => p.ProjectId).ToList() : [];
        var projectIds = parts.Append(running.ProjectId).OfType<Guid>().Distinct().ToList();
        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.WorkspaceId == running.WorkspaceId && projectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var project = running.ProjectId is { } projectId ? projects.GetValueOrDefault(projectId) : null;
        var name = running.IsSplit
            ? string.Join(" + ", parts.Select(id => id is { } partId && projects.TryGetValue(partId, out var part) ? part.Name : "No project"))
            : project?.Name;

        var state = new JsonObject
        {
            ["entryId"] = running.Id.ToString(),
            ["description"] = running.Description.Length > DescriptionLength ? running.Description[..DescriptionLength] : running.Description,
            ["start"] = (running.Start - ReferenceDate).TotalSeconds,
        };
        if (name is not null)
        {
            state["projectName"] = name;
        }

        if (project is not null)
        {
            state["projectColor"] = project.Color;
        }

        return state;
    }

    private static string Hash(JsonObject state) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToJsonString())));
}
