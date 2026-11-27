using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Shared.Contracts.Projects;
using CharacterConceptAI.Shared.Contracts.Scripts;
using CharacterConceptAI.Shared.Contracts.Concepts;

namespace CharacterConceptAI.Desktop.Services;

public class ApiClient
{
    private readonly HttpClient _http;
    private readonly HttpClient _longRunningHttp;
    private string? _token;

    public string BaseUrl { get; set; } = "http://127.0.0.1:5000";
    public bool IsLoopbackBaseUrl => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;
    public Func<CancellationToken, Task<bool>>? RefreshSessionAsync { get; set; }
    public Func<CancellationToken, Task<bool>>? EnsureLocalServerAsync { get; set; }
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _recoverLock = new(1, 1);
    private long _lastRecoveredTicks;
    private readonly object _tokenLock = new();
    private readonly object _mediaCacheLock = new();
    private readonly Dictionary<(Guid ProjectId, Guid AssetId), LinkedListNode<CachedMediaEntry>> _mediaCacheMap = new();
    private readonly LinkedList<CachedMediaEntry> _mediaCacheLru = new();
    private long _mediaCacheBytes;
    private const int MaxMediaCacheEntries = 32;
    private const long MaxMediaCacheBytes = 64L * 1024 * 1024;

    private sealed record CachedMediaEntry((Guid ProjectId, Guid AssetId) Key, byte[] Bytes, string? ETag);
    private readonly AsyncLocal<string?> _asyncErrorMessage = new();
    private readonly AsyncLocal<System.Net.HttpStatusCode?> _asyncStatusCode = new();
    private string _lastGlobalError = string.Empty;
    private System.Net.HttpStatusCode? _lastGlobalStatus;

    public string LastErrorMessage
    {
        get => _asyncErrorMessage.Value ?? _lastGlobalError;
        set { _asyncErrorMessage.Value = value; _lastGlobalError = value; }
    }
    public System.Net.HttpStatusCode? LastStatusCode
    {
        get => _asyncStatusCode.Value ?? _lastGlobalStatus;
        private set { _asyncStatusCode.Value = value; _lastGlobalStatus = value; }
    }
    public event Action<System.Net.HttpStatusCode>? RequestRejected;

    public ApiClient(HttpClient http, HttpClient? longRunningHttp = null)
    {
        _http = http;
        _longRunningHttp = longRunningHttp ?? http;
    }

    public async Task<bool> IsServerHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var quickCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quickCts.CancelAfter(TimeSpan.FromMilliseconds(IsLoopbackBaseUrl ? 400 : 2000));
            using var resp = await _http.GetAsync($"{BaseUrl}/api/v1/health", quickCts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> WaitUntilHealthyAsync(TimeSpan maxWait, CancellationToken ct = default)
    {
        var deadline = Environment.TickCount64 + (long)maxWait.TotalMilliseconds;
        while (Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
        {
            if (await IsServerHealthyAsync(ct)) return true;
            await Task.Delay(IsLoopbackBaseUrl ? 200 : 500, ct);
        }
        return false;
    }

    public async Task<HttpResponseMessage> SendWithRetryAsync(Func<Task<HttpResponseMessage>> sendFunc, CancellationToken ct = default)
    {
        var requestToken = Volatile.Read(ref _token);
        var startedTicks = Environment.TickCount64;
        HttpResponseMessage resp;
        try
        {
            resp = await sendFunc();
        }
        catch (HttpRequestException) when (EnsureLocalServerAsync is not null && IsLoopbackBaseUrl)
        {
            // Prevent thundering-herd recovery when multiple concurrent thumbnails fail together
            bool recovered;
            await _recoverLock.WaitAsync(ct);
            try
            {
                if (Volatile.Read(ref _lastRecoveredTicks) >= startedTicks)
                {
                    recovered = true;
                }
                else
                {
                    recovered = await EnsureLocalServerAsync(ct);
                    if (recovered) Volatile.Write(ref _lastRecoveredTicks, Environment.TickCount64);
                }
            }
            finally { _recoverLock.Release(); }

            if (!recovered) throw;
            resp = await sendFunc();
        }

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && RefreshSessionAsync is not null)
        {
            await _refreshLock.WaitAsync(ct);
            try
            {
                var currentToken = Volatile.Read(ref _token);
                if ((currentToken is not null && currentToken != requestToken) || await RefreshSessionAsync(ct))
                {
                    resp.Dispose();
                    resp = await sendFunc();
                }
            }
            finally { _refreshLock.Release(); }
        }
        LastStatusCode = resp.StatusCode;
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotModified)
            await CaptureErrorAsync(resp, ct);
        else _asyncErrorMessage.Value = string.Empty;
        return resp;
    }

    private async Task CaptureErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        LastStatusCode = response.StatusCode;
        LastErrorMessage = response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.",
            System.Net.HttpStatusCode.Forbidden => "Bạn không có quyền thực hiện thao tác này.",
            System.Net.HttpStatusCode.Conflict => "Dữ liệu đã thay đổi. Hãy tải lại trước khi lưu.",
            System.Net.HttpStatusCode.NotFound => "Không tìm thấy dữ liệu hoặc bạn không còn quyền truy cập.",
            _ => $"Máy chủ từ chối yêu cầu (HTTP {(int)response.StatusCode})."
        };
        try
        {
            using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (body.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == System.Text.Json.JsonValueKind.String)
                LastErrorMessage = detail.GetString() ?? LastErrorMessage;
        }
        catch (System.Text.Json.JsonException) { }
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            RequestRejected?.Invoke(response.StatusCode);
    }

    public void SetToken(string token)
    {
        lock (_tokenLock)
        {
            Volatile.Write(ref _token, token);
            var authorization = new AuthenticationHeaderValue("Bearer", token);
            _http.DefaultRequestHeaders.Authorization = authorization;
            _longRunningHttp.DefaultRequestHeaders.Authorization = authorization;
        }
    }

    public void ClearToken()
    {
        lock (_tokenLock)
        {
            Volatile.Write(ref _token, null);
            _http.DefaultRequestHeaders.Authorization = null;
            _longRunningHttp.DefaultRequestHeaders.Authorization = null;
        }
        ClearMediaCache();
    }

    public void ClearMediaCache()
    {
        lock (_mediaCacheLock)
        {
            _mediaCacheMap.Clear();
            _mediaCacheLru.Clear();
            _mediaCacheBytes = 0;
        }
        lock (_poseCacheLock)
        {
            _poseThumbCacheMap.Clear();
            _poseThumbCacheLru.Clear();
            _poseThumbCacheBytes = 0;
        }
    }

    public async Task<LoginResponse?> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        ClearToken();
        LastErrorMessage = string.Empty;
        using var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/auth/login", new LoginRequest(email, password), ct);
        if (!resp.IsSuccessStatusCode)
        {
            LastErrorMessage = $"Đăng nhập thất bại (HTTP {(int)resp.StatusCode}).";
            return null;
        }
        var tokenResult = await resp.Content.ReadFromJsonAsync<BearerTokenResponse>(cancellationToken: ct);
        if (tokenResult is null) return null;

        SetToken(tokenResult.AccessToken);
        var me = await GetMeAsync(ct);
        if (me is null || !me.IsActive)
        {
            ClearToken();
            LastErrorMessage = "Không thể xác minh phiên đăng nhập. Vui lòng thử lại.";
            return null;
        }
        return new LoginResponse(
            AccessToken: tokenResult.AccessToken,
            RefreshToken: tokenResult.RefreshToken,
            ExpiresIn: tokenResult.ExpiresIn,
            TokenType: tokenResult.TokenType,
            User: me
        );
    }

    public async Task<LoginResponse?> LocalAutoLoginAsync(CancellationToken ct = default)
    {
        ClearToken();
        LastErrorMessage = string.Empty;
        using var resp = await _http.PostAsync($"{BaseUrl}/api/v1/auth/local-session", null, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var tokenResult = await resp.Content.ReadFromJsonAsync<BearerTokenResponse>(cancellationToken: ct);
        if (tokenResult is null) return null;

        SetToken(tokenResult.AccessToken);
        var me = await GetMeAsync(ct);
        if (me is null || !me.IsActive)
        {
            ClearToken();
            return null;
        }
        return new LoginResponse(
            AccessToken: tokenResult.AccessToken,
            RefreshToken: tokenResult.RefreshToken,
            ExpiresIn: tokenResult.ExpiresIn,
            TokenType: tokenResult.TokenType,
            User: me
        );
    }

    public async Task<BearerTokenResponse?> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/auth/refresh", new RefreshTokenRequest(refreshToken), ct);
        if (!resp.IsSuccessStatusCode) return null;
        var result = await resp.Content.ReadFromJsonAsync<BearerTokenResponse>(cancellationToken: ct);
        if (result is not null)
        {
            SetToken(result.AccessToken);
        }
        return result;
    }

    public async Task<UserInfoResponse?> GetMeAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{BaseUrl}/api/v1/auth/me", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<UserInfoResponse>(cancellationToken: ct);
    }

    public async Task<GenerationCapabilitiesDto?> GetGenerationCapabilitiesAsync(CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/generation-capabilities", ct), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<GenerationCapabilitiesDto>(cancellationToken: ct) : null;
    }

    public async Task<CharacterConceptAI.Shared.Contracts.Common.PagedResult<ProjectDto>?> GetProjectsPagedAsync(int page = 1, int pageSize = 20, string? query = null, string? status = null, CancellationToken ct = default, string? sort = null)
    {
        var url = $"{BaseUrl}/api/v1/projects?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(query)) url += $"&query={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(status)) url += $"&status={Uri.EscapeDataString(status)}";
        if (!string.IsNullOrWhiteSpace(sort)) url += $"&sort={Uri.EscapeDataString(sort)}";
        using var resp = await SendWithRetryAsync(() => _http.GetAsync(url, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CharacterConceptAI.Shared.Contracts.Common.PagedResult<ProjectDto>>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<ProjectDto>> GetProjectsAsync(CancellationToken ct = default)
    {
        var paged = await GetProjectsPagedAsync(1, 100, ct: ct);
        return paged?.Items ?? [];
    }

    public async Task<ProjectDto?> GetProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}", ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ProjectDto>(cancellationToken: ct);
    }

    public async Task<ProjectDto?> CreateProjectAsync(string name, string type, string? description, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects", new CreateProjectRequest(name, type, description), ct), ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<ProjectDto>(cancellationToken: ct);
    }

    public async Task<ProjectDto?> UpdateProjectAsync(Guid projectId, UpdateProjectRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PutAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ProjectDto>(cancellationToken: ct);
    }

    public async Task<bool> ArchiveProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/archive", null, ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RestoreProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/restore", null, ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<ProjectMemberDto>?> GetProjectMembersAsync(Guid projectId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/members", ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<List<ProjectMemberDto>>(cancellationToken: ct);
    }

    public async Task<ProjectMemberDto?> AddProjectMemberAsync(Guid projectId, AddProjectMemberRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/members", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ProjectMemberDto>(cancellationToken: ct);
    }

    public async Task<bool> UpdateProjectMemberRoleAsync(Guid projectId, string targetUserId, string newRole, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PatchAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/members/{targetUserId}", new UpdateProjectMemberRoleRequest(newRole), ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RemoveProjectMemberAsync(Guid projectId, string targetUserId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.DeleteAsync($"{BaseUrl}/api/v1/projects/{projectId}/members/{targetUserId}", ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> TransferOwnershipAsync(Guid projectId, string newOwnerUserId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/transfer-ownership", new TransferOwnershipRequest(newOwnerUserId), ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<CharacterDto>> GetCharactersAsync(Guid projectId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters", ct), ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<CharacterDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<CharacterConceptAI.Shared.Contracts.Common.PagedResult<CharacterDto>?> GetCharactersPagedAsync(
        Guid projectId, int page = 1, int pageSize = 20, string? query = null, string? status = null, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/v1/projects/{projectId}/characters/paged?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(query)) url += $"&query={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(status)) url += $"&status={Uri.EscapeDataString(status)}";
        using var resp = await SendWithRetryAsync(() => _http.GetAsync(url, ct), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<CharacterConceptAI.Shared.Contracts.Common.PagedResult<CharacterDto>>(cancellationToken: ct) : null;
    }

    public async Task<CharacterConceptAI.Shared.Contracts.Common.PagedResult<CharacterConceptDto>?> GetProjectConceptsPagedAsync(
        Guid projectId, string? status = null, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/v1/projects/{projectId}/concepts?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(status)) url += $"&status={Uri.EscapeDataString(status)}";
        using var resp = await SendWithRetryAsync(() => _http.GetAsync(url, ct), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<CharacterConceptAI.Shared.Contracts.Common.PagedResult<CharacterConceptDto>>(cancellationToken: ct) : null;
    }

    public async Task<CharacterDto?> CreateCharacterAsync(Guid projectId, string name, string role, string? description, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters", new CreateCharacterRequest(name, role, description, null), ct), ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<CharacterDto>(cancellationToken: ct);
    }

    public async Task<CharacterDto?> GetCharacterAsync(Guid projectId, Guid characterId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}", ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CharacterDto>(cancellationToken: ct);
    }

    public async Task<CharacterDto?> UpdateCharacterAsync(Guid projectId, Guid characterId, UpdateCharacterRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PutAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CharacterDto>(cancellationToken: ct);
    }

    public async Task<bool> ArchiveCharacterAsync(Guid projectId, Guid characterId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/archive", null, ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RestoreCharacterAsync(Guid projectId, Guid characterId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/restore", null, ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<AttributeDefinitionDto>> GetAttributeDefinitionsAsync(Guid projectId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/attribute-definitions", ct), ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<AttributeDefinitionDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<AttributeDefinitionDto?> CreateAttributeDefinitionAsync(Guid projectId, CreateAttributeDefinitionRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/attribute-definitions", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AttributeDefinitionDto>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<CharacterAttributeValueDto>> GetCharacterAttributesAsync(Guid projectId, Guid characterId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/attributes", ct), ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<CharacterAttributeValueDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<IReadOnlyList<CharacterAttributeValueDto>?> SetCharacterAttributesAsync(Guid projectId, Guid characterId, SetCharacterAttributesRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PutAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/attributes", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<List<CharacterAttributeValueDto>>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<CharacterVariantDto>> GetCharacterVariantsAsync(Guid projectId, Guid characterId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/variants", ct), ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<CharacterVariantDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<CharacterVariantDto?> CreateVariantAsync(Guid projectId, Guid characterId, CreateVariantRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/variants", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CharacterVariantDto>(cancellationToken: ct);
    }

    public async Task<CharacterVariantDto?> SetDefaultVariantAsync(Guid projectId, Guid characterId, Guid variantId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/variants/{variantId}/set-default", null, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CharacterVariantDto>(cancellationToken: ct);
    }

    public async Task<CharacterVariantDto?> SetVariantAttributesAsync(Guid projectId, Guid characterId, Guid variantId, SetVariantAttributesRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PutAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/variants/{variantId}/attributes", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CharacterVariantDto>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<CharacterRevisionDto>> GetCharacterRevisionsAsync(Guid projectId, Guid characterId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/revisions", ct), ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<CharacterRevisionDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<CharacterRevisionDiffDto?> GetRevisionDiffAsync(Guid projectId, Guid characterId, Guid fromRevId, Guid toRevId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/revisions/diff?from={fromRevId}&to={toRevId}", ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CharacterRevisionDiffDto>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<CharacterTemplateDto>> GetTemplatesAsync(Guid projectId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/character-templates", ct), ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<CharacterTemplateDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<CharacterTemplateDto?> CreateTemplateAsync(Guid projectId, CreateTemplateRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/character-templates", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CharacterTemplateDto>(cancellationToken: ct);
    }

    public async Task<ApplyTemplatePreviewDto?> PreviewApplyTemplateAsync(Guid projectId, Guid characterId, ApplyTemplateRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/apply-template/preview", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ApplyTemplatePreviewDto>(cancellationToken: ct);
    }

    public async Task<CharacterDto?> ApplyTemplateAsync(Guid projectId, Guid characterId, ApplyTemplateRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/apply-template", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CharacterDto>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<CharacterRelationshipDto>> GetRelationshipsAsync(Guid projectId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/relationships", ct), ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<CharacterRelationshipDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<CharacterRelationshipDto?> CreateRelationshipAsync(Guid projectId, CreateCharacterRelationshipRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/relationships", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CharacterRelationshipDto>(cancellationToken: ct);
    }

    public async Task<bool> DeleteRelationshipAsync(Guid projectId, Guid relationshipId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.DeleteAsync($"{BaseUrl}/api/v1/projects/{projectId}/relationships/{relationshipId}", ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<Guid?> SubmitGenerationAsync(
        Guid projectId,
        Guid characterId,
        Guid? variantId,
        PromptMode promptMode,
        string rawPrompt,
        string? finalPrompt,
        int width,
        int height,
        int requestedCount = 1,
        string? promptTemplateVersion = "1.0",
        Guid? referenceConceptVersionId = null,
        double? denoise = null,
        CancellationToken ct = default,
        long? seed = null,
        bool fullQuality = false,
        string? depthPoseId = null,
        double? depthStrength = null,
        Guid? faceReferenceConceptVersionId = null,
        double? faceSwapStrength = null,
        double? faceRestoreStrength = null,
        IReadOnlyList<Guid>? additionalFaceReferenceConceptVersionIds = null,
        string? outfitId = null,
        Guid? secondaryFaceReferenceConceptVersionId = null,
        double? secondaryFaceSwapStrength = null,
        double? secondaryFaceRestoreStrength = null,
        IReadOnlyList<Guid>? secondaryAdditionalFaceReferenceConceptVersionIds = null,
        IReadOnlyList<LoraSelectionDto>? loras = null)
    {
        var requestId = Guid.NewGuid().ToString();
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/generations", new CreateGenerationRequest(
            CharacterId: characterId,
            VariantId: variantId,
            PromptMode: promptMode,
            RawPrompt: rawPrompt,
            FinalPrompt: finalPrompt,
            Width: width,
            Height: height,
            RequestedCount: requestedCount,
            Seed: seed,
            IdempotencyKey: requestId,
            PromptTemplateVersion: promptTemplateVersion,
            ReferenceConceptVersionId: referenceConceptVersionId,
            Denoise: denoise,
            FullQuality: fullQuality,
            DepthPoseId: depthPoseId,
            DepthStrength: depthStrength,
            FaceReferenceConceptVersionId: faceReferenceConceptVersionId,
            FaceSwapStrength: faceSwapStrength,
            FaceRestoreStrength: faceRestoreStrength,
            AdditionalFaceReferenceConceptVersionIds: additionalFaceReferenceConceptVersionIds,
            OutfitId: outfitId,
            SecondaryFaceReferenceConceptVersionId: secondaryFaceReferenceConceptVersionId,
            SecondaryFaceSwapStrength: secondaryFaceSwapStrength,
            SecondaryFaceRestoreStrength: secondaryFaceRestoreStrength,
            SecondaryAdditionalFaceReferenceConceptVersionIds: secondaryAdditionalFaceReferenceConceptVersionIds,
            Loras: loras
        ), ct), ct);

        if (!resp.IsSuccessStatusCode) return null;

        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>(cancellationToken: ct);
        if (body is not null && body.TryGetValue("id", out var idObj) && Guid.TryParse(idObj.ToString(), out var jobId))
        {
            return jobId;
        }
        return null;
    }

    public async Task<GenerationJobDto?> GetJobStatusAsync(Guid projectId, Guid jobId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/generations/{jobId}", ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<GenerationJobDto>(cancellationToken: ct);
    }

    public async Task<PromptPreviewDto?> PreviewPromptAsync(Guid projectId, Guid characterId, PromptPreviewRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/prompt-preview", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<PromptPreviewDto>(cancellationToken: ct);
    }

    public async Task<CharacterConceptAI.Shared.Contracts.Common.PagedResult<GenerationJobDto>?> GetGenerationsPagedAsync(Guid projectId, string? status = null, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/v1/projects/{projectId}/generations?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(status)) url += $"&status={Uri.EscapeDataString(status)}";
        using var resp = await SendWithRetryAsync(() => _http.GetAsync(url, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CharacterConceptAI.Shared.Contracts.Common.PagedResult<GenerationJobDto>>(cancellationToken: ct);
    }

    public async Task<bool> CancelGenerationAsync(Guid projectId, Guid jobId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/generations/{jobId}/cancel", null, ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RetryGenerationAsync(Guid projectId, Guid jobId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/generations/{jobId}/retry", null, ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<byte[]?> DownloadImageBytesAsync(Guid projectId, Guid assetId, CancellationToken ct = default)
    {
        var key = (projectId, assetId);
        CachedMediaEntry? cached = null;
        lock (_mediaCacheLock)
        {
            if (_mediaCacheMap.TryGetValue(key, out var node))
            {
                _mediaCacheLru.Remove(node);
                _mediaCacheLru.AddFirst(node);
                cached = node.Value;
            }
        }

        using var resp = await SendWithRetryAsync(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/v1/projects/{projectId}/media/{assetId}");
            if (!string.IsNullOrWhiteSpace(cached?.ETag))
            {
                req.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
            }
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.NotModified && cached is not null)
        {
            return cached.Bytes;
        }

        if (!resp.IsSuccessStatusCode)
        {
            if (cached is not null)
            {
                lock (_mediaCacheLock)
                {
                    if (_mediaCacheMap.Remove(key, out var stale))
                    {
                        _mediaCacheLru.Remove(stale);
                        _mediaCacheBytes -= stale.Value.Bytes.Length;
                    }
                }
            }
            return null;
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var etag = resp.Headers.ETag?.ToString();

        if (bytes.Length <= MaxMediaCacheBytes / 2)
        {
            lock (_mediaCacheLock)
            {
                if (_mediaCacheMap.Remove(key, out var existing))
                {
                    _mediaCacheLru.Remove(existing);
                    _mediaCacheBytes -= existing.Value.Bytes.Length;
                }

                var entry = new CachedMediaEntry(key, bytes, etag);
                var node = _mediaCacheLru.AddFirst(entry);
                _mediaCacheMap[key] = node;
                _mediaCacheBytes += bytes.Length;

                while (_mediaCacheMap.Count > MaxMediaCacheEntries || _mediaCacheBytes > MaxMediaCacheBytes)
                {
                    var last = _mediaCacheLru.Last;
                    if (last is null) break;
                    _mediaCacheLru.RemoveLast();
                    _mediaCacheMap.Remove(last.Value.Key);
                    _mediaCacheBytes -= last.Value.Bytes.Length;
                }
            }
        }

        return bytes;
    }

    public async Task<CharacterConceptAI.Shared.Contracts.Common.PagedResult<AccountDto>?> GetAccountsAsync(int page = 1, int pageSize = 20, string? query = null, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/v1/admin/accounts?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(query)) url += $"&query={Uri.EscapeDataString(query)}";
        using var resp = await SendWithRetryAsync(() => _http.GetAsync(url, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CharacterConceptAI.Shared.Contracts.Common.PagedResult<AccountDto>>(cancellationToken: ct);
    }

    public async Task<AccountDto?> CreateAccountAsync(CreateAccountRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/admin/accounts", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AccountDto>(cancellationToken: ct);
    }

    public async Task<bool> UpdateAccountStatusAsync(string userId, bool isActive, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PatchAsJsonAsync($"{BaseUrl}/api/v1/admin/accounts/{userId}/status", new UpdateAccountStatusRequest(isActive), ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> ResetPasswordAsync(string userId, string newPassword, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/admin/accounts/{userId}/reset-password", new ResetPasswordRequest(newPassword), ct), ct);
        return resp.IsSuccessStatusCode;
    }

    // Scripts & Scenes
    public async Task<IReadOnlyList<ScriptDto>> GetScriptsAsync(Guid projectId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/scripts", ct), ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<ScriptDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<ScriptDto?> CreateScriptAsync(Guid projectId, CreateScriptRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/scripts", req, ct), ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<ScriptDto>(cancellationToken: ct);
    }

    public async Task<ScriptDto?> GetScriptAsync(Guid projectId, Guid scriptId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/scripts/{scriptId}", ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ScriptDto>(cancellationToken: ct);
    }

    public async Task<ScriptDto?> UpdateScriptAsync(Guid projectId, Guid scriptId, UpdateScriptRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PutAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/scripts/{scriptId}", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ScriptDto>(cancellationToken: ct);
    }

    public async Task<bool> ArchiveScriptAsync(Guid projectId, Guid scriptId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/scripts/{scriptId}/archive", null, ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RestoreScriptAsync(Guid projectId, Guid scriptId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/scripts/{scriptId}/restore", null, ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<SceneDto>> GetScenesAsync(Guid projectId, Guid scriptId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/scripts/{scriptId}/scenes", ct), ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<SceneDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<SceneDto?> CreateSceneAsync(Guid projectId, Guid scriptId, CreateSceneRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/scripts/{scriptId}/scenes", req, ct), ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        return await resp.Content.ReadFromJsonAsync<SceneDto>(cancellationToken: ct);
    }

    public async Task<SceneDto?> GetSceneAsync(Guid projectId, Guid scriptId, Guid sceneId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/scripts/{scriptId}/scenes/{sceneId}", ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SceneDto>(cancellationToken: ct);
    }

    public async Task<SceneDto?> UpdateSceneAsync(Guid projectId, Guid scriptId, Guid sceneId, UpdateSceneRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PutAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/scripts/{scriptId}/scenes/{sceneId}", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SceneDto>(cancellationToken: ct);
    }

    public async Task<bool> DeleteSceneAsync(Guid projectId, Guid scriptId, Guid sceneId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.DeleteAsync($"{BaseUrl}/api/v1/projects/{projectId}/scripts/{scriptId}/scenes/{sceneId}", ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> ReorderScenesAsync(Guid projectId, Guid scriptId, ReorderScenesRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/scripts/{scriptId}/scenes/reorder", req, ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateSceneCharactersAsync(Guid projectId, Guid scriptId, Guid sceneId, UpdateSceneCharactersRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PutAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/scripts/{scriptId}/scenes/{sceneId}/characters", req, ct), ct);
        return resp.IsSuccessStatusCode;
    }

    // Concept Art & Approvals (P08)
    public async Task<List<CharacterConceptDto>> GetCharacterConceptsAsync(Guid projectId, Guid characterId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/concepts", ct), ct);
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<CharacterConceptDto>>(cancellationToken: ct) ?? [];
    }

    public async Task<ConceptDetailDto?> GetConceptDetailAsync(Guid projectId, Guid conceptId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/concepts/{conceptId}", ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ConceptDetailDto>(cancellationToken: ct);
    }

    public async Task<CharacterConceptDto?> CreateConceptAsync(Guid projectId, Guid characterId, CreateConceptRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/characters/{characterId}/concepts", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CharacterConceptDto>(cancellationToken: ct);
    }

    public async Task<ConceptVersionDto?> CreateConceptVersionAsync(Guid projectId, Guid conceptId, CreateConceptVersionRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/concepts/{conceptId}/versions", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ConceptVersionDto>(cancellationToken: ct);
    }

    public async Task<bool> SubmitConceptVersionAsync(Guid projectId, Guid conceptId, Guid versionId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/concepts/{conceptId}/versions/{versionId}/submit", null, ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> ReviewConceptVersionAsync(Guid projectId, Guid conceptId, Guid versionId, ReviewConceptVersionRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PostAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/concepts/{conceptId}/versions/{versionId}/review", req, ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetRepresentativeVersionAsync(Guid projectId, Guid conceptId, SetRepresentativeVersionRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.PatchAsJsonAsync($"{BaseUrl}/api/v1/projects/{projectId}/concepts/{conceptId}/representative", req, ct), ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<ConceptComparisonDto?> CompareConceptVersionsAsync(Guid projectId, Guid conceptId, Guid left, Guid right, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/projects/{projectId}/concepts/{conceptId}/versions/compare?left={left}&right={right}", ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ConceptComparisonDto>(cancellationToken: ct);
    }

    public async Task<EnhancePromptResponse?> EnhancePromptAsync(EnhancePromptRequest req, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _longRunningHttp.PostAsJsonAsync($"{BaseUrl}/api/v1/studio/enhance-prompt", req, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<EnhancePromptResponse>(cancellationToken: ct);
    }

    public async Task<UploadReferenceResponse?> UploadReferenceImageAsync(
        Guid projectId, Guid characterId, byte[] imageBytes, string fileName, CancellationToken ct = default)
    {
        var ext = System.IO.Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        var contentType = ext is "jpg" or "jpeg" ? "image/jpeg" : "image/png";
        var safeFileName = string.IsNullOrWhiteSpace(fileName) ? "reference.png" : System.IO.Path.GetFileName(fileName);

        using var resp = await SendWithRetryAsync(async () =>
        {
            using var form = new MultipartFormDataContent();
            using var content = new ByteArrayContent(imageBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            form.Add(content, "image", safeFileName);
            return await _http.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/studio/upload-reference?characterId={characterId}", form, ct);
        }, ct);

        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<UploadReferenceResponse>(cancellationToken: ct);
    }

    public async Task<byte[]?> UpscaleAssetAsync(Guid projectId, Guid assetId, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _longRunningHttp.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/media/{assetId}/upscale", null, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Bytes)>> _poseThumbCacheMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, byte[] Bytes)> _poseThumbCacheLru = new();
    private long _poseThumbCacheBytes;
    private const int MaxPoseThumbCacheEntries = 400;
    private const long MaxPoseThumbCacheBytes = 24L * 1024 * 1024;
    private readonly object _poseCacheLock = new();

    public async Task<PoseGalleryResponse?> GetPoseGalleryAsync(
        bool includeNsfw = false,
        string? system = null,
        string? category = null,
        string? angle = null,
        string? query = null,
        int page = 1,
        int pageSize = 24,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/v1/studio/pose-gallery?includeNsfw={(includeNsfw ? "true" : "false")}&page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(system)) url += $"&system={Uri.EscapeDataString(system)}";
        if (!string.IsNullOrWhiteSpace(category)) url += $"&category={Uri.EscapeDataString(category)}";
        if (!string.IsNullOrWhiteSpace(angle)) url += $"&angle={Uri.EscapeDataString(angle)}";
        if (!string.IsNullOrWhiteSpace(query)) url += $"&q={Uri.EscapeDataString(query)}";
        using var resp = await SendWithRetryAsync(() => _http.GetAsync(url, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<PoseGalleryResponse>(cancellationToken: ct);
    }

    public async Task<byte[]?> DownloadPoseGalleryImageBytesAsync(
        string poseId,
        string kind = "thumb",
        string mode = "mannequin",
        CancellationToken ct = default)
    {
        var cacheKey = $"{poseId}|{kind}|{mode}";
        lock (_poseCacheLock)
        {
            if (_poseThumbCacheMap.TryGetValue(cacheKey, out var node))
            {
                _poseThumbCacheLru.Remove(node);
                _poseThumbCacheLru.AddFirst(node);
                return node.Value.Bytes;
            }
        }

        var url = $"{BaseUrl}/api/v1/studio/pose-gallery/image/{Uri.EscapeDataString(poseId)}?kind={Uri.EscapeDataString(kind)}&mode={Uri.EscapeDataString(mode)}";
        using var resp = await SendWithRetryAsync(() => _http.GetAsync(url, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        if (bytes.Length > 0 && bytes.Length < 400_000)
        {
            lock (_poseCacheLock)
            {
                if (_poseThumbCacheMap.TryGetValue(cacheKey, out var existing))
                {
                    _poseThumbCacheBytes -= existing.Value.Bytes.Length;
                    _poseThumbCacheLru.Remove(existing);
                    _poseThumbCacheMap.Remove(cacheKey);
                }
                while ((_poseThumbCacheMap.Count >= MaxPoseThumbCacheEntries || _poseThumbCacheBytes + bytes.Length > MaxPoseThumbCacheBytes)
                       && _poseThumbCacheLru.Last is { } oldest)
                {
                    _poseThumbCacheBytes -= oldest.Value.Bytes.Length;
                    _poseThumbCacheMap.Remove(oldest.Value.Key);
                    _poseThumbCacheLru.RemoveLast();
                }
                var newNode = new LinkedListNode<(string Key, byte[] Bytes)>((cacheKey, bytes));
                _poseThumbCacheLru.AddFirst(newNode);
                _poseThumbCacheMap[cacheKey] = newNode;
                _poseThumbCacheBytes += bytes.Length;
            }
        }
        return bytes;
    }

    public async Task<OutfitGalleryResponse?> GetOutfitGalleryAsync(
        bool includeNsfw = false,
        string? gender = null,
        string? collection = null,
        string? category = null,
        string? query = null,
        int page = 1,
        int pageSize = 24,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/v1/studio/outfit-gallery?includeNsfw={(includeNsfw ? "true" : "false")}&page={page}&pageSize={pageSize}";
        if (!string.IsNullOrWhiteSpace(gender)) url += $"&gender={Uri.EscapeDataString(gender)}";
        if (!string.IsNullOrWhiteSpace(collection)) url += $"&collection={Uri.EscapeDataString(collection)}";
        if (!string.IsNullOrWhiteSpace(category)) url += $"&category={Uri.EscapeDataString(category)}";
        if (!string.IsNullOrWhiteSpace(query)) url += $"&q={Uri.EscapeDataString(query)}";
        using var resp = await SendWithRetryAsync(() => _http.GetAsync(url, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<OutfitGalleryResponse>(cancellationToken: ct);
    }

    public async Task<byte[]?> DownloadOutfitGalleryImageBytesAsync(
        string outfitId,
        string kind = "thumb",
        string view = "front",
        CancellationToken ct = default)
    {
        var cacheKey = $"outfit|{outfitId}|{kind}|{view}";
        lock (_poseCacheLock)
        {
            if (_poseThumbCacheMap.TryGetValue(cacheKey, out var node))
            {
                _poseThumbCacheLru.Remove(node);
                _poseThumbCacheLru.AddFirst(node);
                return node.Value.Bytes;
            }
        }

        var url = $"{BaseUrl}/api/v1/studio/outfit-gallery/image/{Uri.EscapeDataString(outfitId)}?kind={Uri.EscapeDataString(kind)}&view={Uri.EscapeDataString(view)}";
        using var resp = await SendWithRetryAsync(() => _http.GetAsync(url, ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        if (bytes.Length > 0 && bytes.Length < 400_000)
        {
            lock (_poseCacheLock)
            {
                if (_poseThumbCacheMap.TryGetValue(cacheKey, out var existing))
                {
                    _poseThumbCacheBytes -= existing.Value.Bytes.Length;
                    _poseThumbCacheLru.Remove(existing);
                    _poseThumbCacheMap.Remove(cacheKey);
                }
                while ((_poseThumbCacheMap.Count >= MaxPoseThumbCacheEntries || _poseThumbCacheBytes + bytes.Length > MaxPoseThumbCacheBytes)
                       && _poseThumbCacheLru.Last is { } oldest)
                {
                    _poseThumbCacheBytes -= oldest.Value.Bytes.Length;
                    _poseThumbCacheMap.Remove(oldest.Value.Key);
                    _poseThumbCacheLru.RemoveLast();
                }
                var newNode = new LinkedListNode<(string Key, byte[] Bytes)>((cacheKey, bytes));
                _poseThumbCacheLru.AddFirst(newNode);
                _poseThumbCacheMap[cacheKey] = newNode;
                _poseThumbCacheBytes += bytes.Length;
            }
        }
        return bytes;
    }

    public async Task<LoraCatalogResponse?> GetLoraCatalogAsync(CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() => _http.GetAsync($"{BaseUrl}/api/v1/studio/loras", ct), ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<LoraCatalogResponse>(cancellationToken: ct);
    }

    public async Task<byte[]?> RemoveBackgroundAsync(Guid projectId, byte[] imageBytes, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(async () =>
        {
            using var form = new MultipartFormDataContent();
            using var content = new ByteArrayContent(imageBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(content, "image", "source.png");
            return await _longRunningHttp.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/studio/remove-background", form, ct);
        }, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]?> HiresUpscale2KAsync(
        Guid projectId,
        byte[] imageBytes,
        string? prompt = null,
        bool useTiledHires = true,
        double denoise = 0.24,
        string? upscaler = null,
        IReadOnlyList<LoraSelectionDto>? loras = null,
        CancellationToken ct = default)
    {
        var lorasJson = loras is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(loras) : string.Empty;
        using var resp = await SendWithRetryAsync(async () =>
        {
            using var form = new MultipartFormDataContent();
            using var content = new ByteArrayContent(imageBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(content, "image", "source.png");
            if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
            form.Add(new StringContent(useTiledHires ? "true" : "false"), "useTiledHires");
            form.Add(new StringContent(denoise.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), "denoise");
            if (!string.IsNullOrWhiteSpace(upscaler)) form.Add(new StringContent(upscaler), "upscaler");
            if (!string.IsNullOrWhiteSpace(lorasJson)) form.Add(new StringContent(lorasJson), "lorasJson");
            return await _longRunningHttp.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/studio/hires-upscale", form, ct);
        }, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]?> InpaintAsync(
        Guid projectId,
        byte[] imageBytes,
        byte[] maskBytes,
        string prompt,
        double controlStrength = 0.90,
        int maskDilate = 6,
        int featherPx = 14,
        long? seed = null,
        IReadOnlyList<LoraSelectionDto>? loras = null,
        CancellationToken ct = default)
    {
        var lorasJson = loras is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(loras) : string.Empty;
        using var resp = await SendWithRetryAsync(async () =>
        {
            using var form = new MultipartFormDataContent();
            using var imgContent = new ByteArrayContent(imageBytes);
            imgContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(imgContent, "image", "source.png");

            using var maskContent = new ByteArrayContent(maskBytes);
            maskContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(maskContent, "mask", "mask.png");

            form.Add(new StringContent(prompt ?? string.Empty), "prompt");
            form.Add(new StringContent(controlStrength.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), "controlStrength");
            form.Add(new StringContent(maskDilate.ToString()), "maskDilate");
            form.Add(new StringContent(featherPx.ToString()), "featherPx");
            if (seed.HasValue) form.Add(new StringContent(seed.Value.ToString()), "seed");
            if (!string.IsNullOrWhiteSpace(lorasJson)) form.Add(new StringContent(lorasJson), "lorasJson");
            return await _longRunningHttp.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/studio/inpaint", form, ct);
        }, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]?> OutpaintAsync(
        Guid projectId,
        byte[] imageBytes,
        string prompt,
        string targetPreset = "16:9",
        int padLeft = 0,
        int padRight = 0,
        int padTop = 0,
        int padBottom = 0,
        double controlStrength = 0.90,
        long? seed = null,
        IReadOnlyList<LoraSelectionDto>? loras = null,
        CancellationToken ct = default)
    {
        var lorasJson = loras is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(loras) : string.Empty;
        using var resp = await SendWithRetryAsync(async () =>
        {
            using var form = new MultipartFormDataContent();
            using var imgContent = new ByteArrayContent(imageBytes);
            imgContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(imgContent, "image", "source.png");

            form.Add(new StringContent(prompt ?? string.Empty), "prompt");
            form.Add(new StringContent(targetPreset), "targetPreset");
            form.Add(new StringContent(padLeft.ToString()), "padLeft");
            form.Add(new StringContent(padRight.ToString()), "padRight");
            form.Add(new StringContent(padTop.ToString()), "padTop");
            form.Add(new StringContent(padBottom.ToString()), "padBottom");
            form.Add(new StringContent(controlStrength.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)), "controlStrength");
            if (seed.HasValue) form.Add(new StringContent(seed.Value.ToString()), "seed");
            if (!string.IsNullOrWhiteSpace(lorasJson)) form.Add(new StringContent(lorasJson), "lorasJson");
            return await _longRunningHttp.PostAsync($"{BaseUrl}/api/v1/projects/{projectId}/studio/outpaint", form, ct);
        }, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<bool> WarmupStudioGpuAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _longRunningHttp.PostAsync($"{BaseUrl}/api/v1/studio/warmup", content: null, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
