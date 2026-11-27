using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Common;
using CharacterConceptAI.Shared.Contracts.Projects;

namespace CharacterConceptAI.Tests;

public class AuthAndSecurityTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;

    public AuthAndSecurityTests(TestApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<string> LoginAsAdminAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(
            Email: "admin@conceptai.local",
            Password: "Admin@Secured2026"
        ));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var auth = await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(auth);
        return auth["accessToken"].ToString()!;
    }

    [Fact]
    public async Task API001_UnauthenticatedRequest_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task API002_AnonymousRegistrationAttempt_IsBlocked_Returns404()
    {
        var client = _factory.CreateClient();

        // Check root register
        var r1 = await client.PostAsJsonAsync("/register", new { email = "hacker@test.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.NotFound, r1.StatusCode);

        // Check auth register
        var r2 = await client.PostAsJsonAsync("/api/v1/auth/register", new { email = "hacker@test.com", password = "Password123!" });
        Assert.Equal(HttpStatusCode.NotFound, r2.StatusCode);
    }

    [Fact]
    public async Task AdminAccounts_OnlyAccessibleBySystemAdmin()
    {
        var client = _factory.CreateClient();

        // 1. Anonymous attempt -> 401
        var anonResp = await client.GetAsync("/api/v1/admin/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // 2. Admin logs in -> 200
        var adminToken = await LoginAsAdminAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var listResp = await client.GetAsync("/api/v1/admin/accounts");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var paged = await listResp.Content.ReadFromJsonAsync<PagedResult<AccountDto>>();
        Assert.NotNull(paged);
        Assert.NotEmpty(paged.Items);

        // 3. Admin creates a new Designer account
        var designerEmail = $"designer_{Guid.NewGuid():N}@conceptai.local";
        var createResp = await client.PostAsJsonAsync("/api/v1/admin/accounts", new CreateAccountRequest(
            Email: designerEmail,
            DisplayName: "Concept Designer Alpha",
            Password: "Designer@123456",
            Roles: ["Designer"]
        ));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var newAccount = await createResp.Content.ReadFromJsonAsync<AccountDto>();
        Assert.NotNull(newAccount);

        // 4. Non-admin user tries to access Admin accounts -> 403 Forbidden
        var designerClient = _factory.CreateClient();
        var designerLogin = await designerClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(
            Email: designerEmail,
            Password: "Designer@123456"
        ));
        Assert.Equal(HttpStatusCode.OK, designerLogin.StatusCode);
        var designerAuth = await designerLogin.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var designerToken = designerAuth!["accessToken"].ToString()!;

        designerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", designerToken);
        var forbiddenResp = await designerClient.GetAsync("/api/v1/admin/accounts");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);
    }

    [Fact]
    public async Task API012_DisabledUser_CannotLogin_OrAccessEndpoints()
    {
        var client = _factory.CreateClient();
        var adminToken = await LoginAsAdminAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // 1. Create a user
        var testEmail = $"user_disable_{Guid.NewGuid():N}@test.local";
        var createResp = await client.PostAsJsonAsync("/api/v1/admin/accounts", new CreateAccountRequest(
            Email: testEmail,
            DisplayName: "To Be Disabled",
            Password: "User@12345678"
        ));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var user = await createResp.Content.ReadFromJsonAsync<AccountDto>();
        Assert.NotNull(user);

        // 2. Disable user
        var disableResp = await client.PatchAsJsonAsync($"/api/v1/admin/accounts/{user.Id}/status", new UpdateAccountStatusRequest(false));
        Assert.Equal(HttpStatusCode.OK, disableResp.StatusCode);

        // 3. User tries to log in -> 403 Forbidden
        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(testEmail, "User@12345678"));
        Assert.Equal(HttpStatusCode.Forbidden, loginResp.StatusCode);
    }

    [Fact]
    public async Task API013_RefreshToken_WorksCorrectly_AndRejectsInvalidToken()
    {
        var client = _factory.CreateClient();
        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(
            Email: "admin@conceptai.local",
            Password: "Admin@Secured2026"
        ));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        var auth = await loginResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var refreshToken = auth!["refreshToken"].ToString()!;

        // Refresh with valid token
        var refreshResp = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshTokenRequest(refreshToken));
        Assert.Equal(HttpStatusCode.OK, refreshResp.StatusCode);
        var newAuth = await refreshResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(newAuth);
        Assert.NotEmpty(newAuth["accessToken"].ToString()!);

        // Refresh with forged token -> 401
        var badResp = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshTokenRequest("forged_fake_token"));
        Assert.Equal(HttpStatusCode.Unauthorized, badResp.StatusCode);
    }

    [Fact]
    public async Task API003_IDOR_CrossProjectConcealment_Returns404()
    {
        var client = _factory.CreateClient();
        var adminToken = await LoginAsAdminAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // Admin creates Project A
        var projAResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Project Alpha", "Game", null));
        var projA = await projAResp.Content.ReadFromJsonAsync<ProjectDto>();

        // Create user B who is NOT in Project A
        var userBEmail = $"user_b_{Guid.NewGuid():N}@test.local";
        await client.PostAsJsonAsync("/api/v1/admin/accounts", new CreateAccountRequest(userBEmail, "User B", "Password@123456"));

        var userBClient = _factory.CreateClient();
        var loginB = await userBClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(userBEmail, "Password@123456"));
        var authB = await loginB.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        userBClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authB!["accessToken"].ToString());

        // User B tries to view characters of Project A -> 404 Not Found (IDOR concealed!)
        var idorResp = await userBClient.GetAsync($"/api/v1/projects/{projA!.Id}/characters");
        Assert.Equal(HttpStatusCode.NotFound, idorResp.StatusCode);
    }

    [Fact]
    public async Task API004_Viewer_CannotCreateCharacters_Returns403()
    {
        var client = _factory.CreateClient();
        var adminToken = await LoginAsAdminAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // Admin creates project
        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Project Restricted", "Comic", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        // Create Viewer user
        var viewerEmail = $"viewer_{Guid.NewGuid():N}@test.local";
        var createViewer = await client.PostAsJsonAsync("/api/v1/admin/accounts", new CreateAccountRequest(viewerEmail, "Viewer V", "Viewer@123456"));
        var viewerAccount = await createViewer.Content.ReadFromJsonAsync<AccountDto>();

        // Add Viewer to project via DbContext
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CharacterConceptAI.Server.Data.AppDbContext>();
            db.ProjectMembers.Add(new CharacterConceptAI.Server.Data.Entities.ProjectMember
            {
                ProjectId = proj!.Id,
                UserId = viewerAccount!.Id,
                Role = "Viewer"
            });
            await db.SaveChangesAsync();
        }

        // Viewer logs in
        var viewerClient = _factory.CreateClient();
        var loginViewer = await viewerClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(viewerEmail, "Viewer@123456"));
        var authViewer = await loginViewer.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        viewerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authViewer!["accessToken"].ToString());

        // Viewer can read project characters -> 200 OK
        var readResp = await viewerClient.GetAsync($"/api/v1/projects/{proj!.Id}/characters");
        Assert.Equal(HttpStatusCode.OK, readResp.StatusCode);

        // Viewer tries to create character -> 403 Forbidden!
        var createCharResp = await viewerClient.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters", new CreateCharacterRequest(
            Name: "Illegal Char",
            Role: "Villain",
            Description: null,
            InitialAttributes: null
        ));
        Assert.Equal(HttpStatusCode.Forbidden, createCharResp.StatusCode);
    }

    [Fact]
    public async Task API016_ProprietaryBearerToken_AccessesMeEndpoint_WithoutJwtParser()
    {
        var client = _factory.CreateClient();
        var adminToken = await LoginAsAdminAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // GET /me verifies built-in bearer token protector works end-to-end
        var meResp = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);
        var me = await meResp.Content.ReadFromJsonAsync<UserInfoResponse>();
        Assert.NotNull(me);
        Assert.Equal("admin@conceptai.local", me.Email);
        Assert.Contains("SystemAdmin", me.GlobalRoles);
        Assert.True(me.IsActive);
    }

    [Fact]
    public async Task Admin_CanResetPassword_And_CannotSelfDisable()
    {
        var client = _factory.CreateClient();
        var adminToken = await LoginAsAdminAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // 1. Create a user
        var userEmail = $"user_pwd_{Guid.NewGuid():N}@test.local";
        var createResp = await client.PostAsJsonAsync("/api/v1/admin/accounts", new CreateAccountRequest(
            Email: userEmail,
            DisplayName: "Password Target",
            Password: "OldPassword@123"
        ));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var user = await createResp.Content.ReadFromJsonAsync<AccountDto>();
        Assert.NotNull(user);

        // 2. Admin resets password
        var resetResp = await client.PostAsJsonAsync($"/api/v1/admin/accounts/{user.Id}/reset-password", new ResetPasswordRequest("NewSecret@456789"));
        Assert.Equal(HttpStatusCode.OK, resetResp.StatusCode);

        // 3. User logs in with old password -> 401 Unauthorized
        var userClient = _factory.CreateClient();
        var oldLogin = await userClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(userEmail, "OldPassword@123"));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        // 4. User logs in with new password -> 200 OK
        var newLogin = await userClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(userEmail, "NewSecret@456789"));
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);

        // 5. Admin tries to self-disable -> 400 Bad Request
        var meResp = await client.GetAsync("/api/v1/auth/me");
        var me = await meResp.Content.ReadFromJsonAsync<UserInfoResponse>();
        var selfDisableResp = await client.PatchAsJsonAsync($"/api/v1/admin/accounts/{me!.Id}/status", new UpdateAccountStatusRequest(false));
        Assert.Equal(HttpStatusCode.BadRequest, selfDisableResp.StatusCode);
    }
}
