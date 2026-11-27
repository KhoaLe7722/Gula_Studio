using System;
using System.Threading;
using System.Threading.Tasks;
using CharacterConceptAI.Shared.Contracts.Auth;

namespace CharacterConceptAI.Desktop.Services;

public class AuthSessionService
{
    private readonly ApiClient _api;
    private readonly object _lock = new();

    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public UserInfoResponse? CurrentUser { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken) && CurrentUser != null;
    public bool IsAdmin => CurrentUser?.GlobalRoles.Contains("SystemAdmin") == true;

    public event Action? SessionChanged;

    public AuthSessionService(ApiClient api)
    {
        _api = api;
        _api.RefreshSessionAsync = RefreshAsync;
    }

    public async Task<bool> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var result = await _api.LoginAsync(email, password, ct);
        if (result is null)
        {
            Logout();
            return false;
        }

        lock (_lock)
        {
            AccessToken = result.AccessToken;
            RefreshToken = result.RefreshToken;
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn);
            CurrentUser = result.User;
        }

        SessionChanged?.Invoke();
        return true;
    }

    public async Task<bool> TryLocalAutoLoginAsync(CancellationToken ct = default)
    {
        var result = await _api.LocalAutoLoginAsync(ct);
        if (result is null) return false;

        lock (_lock)
        {
            AccessToken = result.AccessToken;
            RefreshToken = result.RefreshToken;
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn);
            CurrentUser = result.User;
        }

        SessionChanged?.Invoke();
        return true;
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(RefreshToken))
        {
            var refreshed = await _api.RefreshTokenAsync(RefreshToken, ct);
            if (refreshed is not null)
            {
                lock (_lock)
                {
                    AccessToken = refreshed.AccessToken;
                    RefreshToken = refreshed.RefreshToken;
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn);
                }

                var user = await _api.GetMeAsync(ct);
                if (user is { IsActive: true })
                {
                    lock (_lock) { CurrentUser = user; }
                    SessionChanged?.Invoke();
                    return true;
                }
            }
        }

        // In personal single-PC local mode, seamlessly re-establish the loopback session instead of logging out.
        if (_api.IsLoopbackBaseUrl && await TryLocalAutoLoginAsync(ct))
            return true;

        Logout();
        return false;
    }

    public void Logout()
    {
        lock (_lock)
        {
            AccessToken = null;
            RefreshToken = null;
            ExpiresAt = null;
            CurrentUser = null;
        }

        _api.ClearToken();
        SessionChanged?.Invoke();
    }
}
