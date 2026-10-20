namespace Spindle.Core;

/// <summary>What a server says about itself before anyone signs in. Its sign-in providers are in display order; none means no buttons.</summary>
public sealed record ServerInfo(bool Hosted, bool RegistrationOpen, string? TermsUrl, string? PrivacyUrl, SignInProvider[]? SignInProviders = null);
public sealed partial class SpindleApiClient
{
    public async Task<ServerInfo> GetServerInfoAsync(CancellationToken ct = default)
    { using var response = await SendRawAsync(HttpMethod.Get, "/api/server", null, null, ct); await EnsureSuccessAsync(response, ct); return await ReadAsync<ServerInfo>(response, ct); }
    public async Task ForgotPasswordAsync(string email, CancellationToken ct = default)
    { using var response = await SendRawAsync(HttpMethod.Post, "/api/auth/forgot-password", new { Email = email }, null, ct); await EnsureSuccessAsync(response, ct); }

    /// <summary>Trades the code the browser brought back for a session, kept as a password sign-in's is. True when it made a new account.</summary>
    public async Task<bool> ExchangeExternalSignInAsync(ExternalExchangeRequest request, CancellationToken ct = default)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            using var response = await SendRawAsync(HttpMethod.Post, "/api/auth/external/exchange", request, null, ct);
            await EnsureSuccessAsync(response, ct);
            var result = await ReadAsync<ExternalSignInResponse>(response, ct);
            await AcceptTokensAsync(new AuthTokensResponse
            {
                AccessToken = result.AccessToken, AccessTokenExpiresAt = result.AccessTokenExpiresAt,
                RefreshToken = result.RefreshToken, RefreshTokenExpiresAt = result.RefreshTokenExpiresAt,
            }, ct);
            return result.NewAccount;
        }
        finally { _sessionGate.Release(); }
    }
}
