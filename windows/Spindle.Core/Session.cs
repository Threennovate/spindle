namespace Spindle.Core;

public sealed record StoredSession(string RefreshToken);

/// <summary>Store refresh tokens with operating-system credential protection, scoped to the API server.</summary>
public interface ISessionStore
{
    Task<StoredSession?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(StoredSession session, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
