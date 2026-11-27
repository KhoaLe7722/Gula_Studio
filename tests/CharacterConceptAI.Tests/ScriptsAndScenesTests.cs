using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Projects;
using CharacterConceptAI.Shared.Contracts.Scripts;

namespace CharacterConceptAI.Tests;

public class ScriptsAndScenesTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;

    public ScriptsAndScenesTests(TestApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, string UserId, string Email)> CreateAndLoginUserAsync(string displayName)
    {
        var adminClient = _factory.CreateClient();
        var adminLoginResp = await adminClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(
            Email: "admin@conceptai.local",
            Password: "Admin@Secured2026"
        ));
        Assert.Equal(HttpStatusCode.OK, adminLoginResp.StatusCode);
        var adminAuth = await adminLoginResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminAuth!["accessToken"].ToString());

        var email = $"user_{Guid.NewGuid():N}@conceptai.local";
        var password = "User@Secured2026";
        var createResp = await adminClient.PostAsJsonAsync("/api/v1/admin/accounts", new CreateAccountRequest(
            Email: email,
            DisplayName: displayName,
            Password: password,
            Roles: ["Designer"]
        ));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var account = await createResp.Content.ReadFromJsonAsync<AccountDto>();

        var userClient = _factory.CreateClient();
        var userLoginResp = await userClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, userLoginResp.StatusCode);
        var userAuth = await userLoginResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userAuth!["accessToken"].ToString());

        return (userClient, account!.Id, email);
    }

    [Fact]
    public async Task ScriptCRUD_AndConcurrencyRowVersion_WorksCorrectly()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Script Writer");

        // 1. Create project
        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Scripting Chronicles", "Game", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        // 2. Create script
        var createScriptResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/scripts", new CreateScriptRequest(
            Title: "Hồi 1: Lời Nguyền Cổ Xưa",
            Summary: "Mở đầu câu chuyện về vương quốc bóng đêm"
        ));
        Assert.Equal(HttpStatusCode.Created, createScriptResp.StatusCode);
        var script = await createScriptResp.Content.ReadFromJsonAsync<ScriptDto>();
        Assert.NotNull(script);
        Assert.Equal("Hồi 1: Lời Nguyền Cổ Xưa", script.Title);
        Assert.Equal("Active", script.Status);

        // Fetch detail to get current RowVersion
        var detailResp = await client.GetAsync($"/api/v1/projects/{proj.Id}/scripts/{script.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
        var fetched = await detailResp.Content.ReadFromJsonAsync<ScriptDto>();
        var activeVersion = fetched!.RowVersion;
        Assert.NotNull(activeVersion);

        // 3. Update script successfully
        var updateResp = await client.PutAsJsonAsync($"/api/v1/projects/{proj.Id}/scripts/{script.Id}", new UpdateScriptRequest(
            Title: "Hồi 1: Lời Nguyền Cổ Xưa (Đã chỉnh sửa)",
            Summary: "Tóm tắt được cập nhật",
            Version: activeVersion
        ));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updated = await updateResp.Content.ReadFromJsonAsync<ScriptDto>();
        Assert.NotNull(updated);
        Assert.NotEqual(activeVersion, updated.RowVersion);

        // 4. Update with stale RowVersion -> 409 Conflict
        var staleResp = await client.PutAsJsonAsync($"/api/v1/projects/{proj.Id}/scripts/{script.Id}", new UpdateScriptRequest(
            Title: "Stale Override Attempt",
            Summary: "Overwriting with stale token",
            Version: activeVersion
        ));
        Assert.Equal(HttpStatusCode.Conflict, staleResp.StatusCode);
    }

    [Fact]
    public async Task ArchivedScript_RejectsSceneCreation_RestoresSuccessfully()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Archival Script Lead");

        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Archival Script Project", "Comic", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var scriptResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/scripts", new CreateScriptRequest("Bản Thảo Cũ", "Sẽ lưu trữ"));
        var script = await scriptResp.Content.ReadFromJsonAsync<ScriptDto>();

        // Archive script
        var archiveResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/scripts/{script!.Id}/archive", new { });
        Assert.Equal(HttpStatusCode.OK, archiveResp.StatusCode);

        // Attempt to create scene in archived script -> 422 Unprocessable Entity
        var illegalSceneResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/scripts/{script.Id}/scenes", new CreateSceneRequest(
            Title: "Cảnh 1",
            Setting: "Khu rừng đen",
            Content: "Không thể thêm vào kịch bản đã lưu trữ"
        ));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, illegalSceneResp.StatusCode);

        // Restore script
        var restoreResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/scripts/{script.Id}/restore", new { });
        Assert.Equal(HttpStatusCode.OK, restoreResp.StatusCode);

        // Now scene creation succeeds -> 201 Created
        var legalSceneResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/scripts/{script.Id}/scenes", new CreateSceneRequest(
            Title: "Cảnh 1: Khởi hành",
            Setting: "Cổng thành",
            Content: "Luna bắt đầu hành trình"
        ));
        Assert.Equal(HttpStatusCode.Created, legalSceneResp.StatusCode);
    }

    [Fact]
    public async Task Scenes_OrderingAndAtomicReordering_WorksSuccessfully()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Scene Director");

        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Cinematic Scenes", "Animation", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var scriptResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/scripts", new CreateScriptRequest("Chương 2", "Kịch bản phân cảnh"));
        var script = await scriptResp.Content.ReadFromJsonAsync<ScriptDto>();

        // Create 3 scenes
        var s1Resp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/scripts/{script!.Id}/scenes", new CreateSceneRequest("Cảnh A: Gặp gỡ", "Quán rượu", "Hai người gặp nhau", 1));
        var s2Resp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/scripts/{script.Id}/scenes", new CreateSceneRequest("Cảnh B: Giao đấu", "Đấu trường", "Giao đấu ác liệt", 2));
        var s3Resp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/scripts/{script.Id}/scenes", new CreateSceneRequest("Cảnh C: Kết thúc", "Lâu đài", "Chiến thắng trở về", 3));

        var s1 = await s1Resp.Content.ReadFromJsonAsync<SceneDto>();
        var s2 = await s2Resp.Content.ReadFromJsonAsync<SceneDto>();
        var s3 = await s3Resp.Content.ReadFromJsonAsync<SceneDto>();

        // Reorder scenes atomically (Swap s1 and s3)
        var reorderResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/scripts/{script.Id}/scenes/reorder", new ReorderScenesRequest([
            new SceneOrderItem(s3!.Id, 1),
            new SceneOrderItem(s2!.Id, 2),
            new SceneOrderItem(s1!.Id, 3)
        ]));
        Assert.Equal(HttpStatusCode.OK, reorderResp.StatusCode);

        // Fetch ordered scenes
        var listResp = await client.GetAsync($"/api/v1/projects/{proj.Id}/scripts/{script.Id}/scenes");
        var scenes = await listResp.Content.ReadFromJsonAsync<List<SceneDto>>();
        Assert.NotNull(scenes);
        Assert.Equal(3, scenes.Count);
        Assert.Equal(s3.Id, scenes[0].Id);
        Assert.Equal(1, scenes[0].OrderIndex);
        Assert.Equal(s2.Id, scenes[1].Id);
        Assert.Equal(2, scenes[1].OrderIndex);
        Assert.Equal(s1.Id, scenes[2].Id);
        Assert.Equal(3, scenes[2].OrderIndex);
    }

    [Fact]
    public async Task CrossProjectCharacter_CannotBeLinkedToScene_DeniesWith422()
    {
        var (clientA, _, _) = await CreateAndLoginUserAsync("Project A Creator");
        var (clientB, _, _) = await CreateAndLoginUserAsync("Project B Creator");

        // 1. User A creates Project A with Script & Scene & Character A
        var projAResp = await clientA.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Project Alpha", "Game", null));
        var projA = await projAResp.Content.ReadFromJsonAsync<ProjectDto>();

        var charAResp = await clientA.PostAsJsonAsync($"/api/v1/projects/{projA!.Id}/characters", new CreateCharacterRequest("Hero A", "Protagonist", null, null));
        var charA = await charAResp.Content.ReadFromJsonAsync<CharacterDto>();

        var scriptAResp = await clientA.PostAsJsonAsync($"/api/v1/projects/{projA.Id}/scripts", new CreateScriptRequest("Script Alpha", null));
        var scriptA = await scriptAResp.Content.ReadFromJsonAsync<ScriptDto>();

        // 2. User B creates Project B with Character B
        var projBResp = await clientB.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Project Beta", "Comic", null));
        var projB = await projBResp.Content.ReadFromJsonAsync<ProjectDto>();

        var charBResp = await clientB.PostAsJsonAsync($"/api/v1/projects/{projB!.Id}/characters", new CreateCharacterRequest("Villain B", "Antagonist", null, null));
        var charB = await charBResp.Content.ReadFromJsonAsync<CharacterDto>();

        // 3. User A attempts to create a scene linking Character B (cross-project attack!) -> 422 Unprocessable Entity
        var crossCreateResp = await clientA.PostAsJsonAsync($"/api/v1/projects/{projA.Id}/scripts/{scriptA!.Id}/scenes", new CreateSceneRequest(
            Title: "Illegal Cross Scene",
            Setting: null,
            Content: null,
            CharacterIds: [charB!.Id]
        ));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossCreateResp.StatusCode);

        // 4. User A creates a valid scene with Character A (same project) -> 201 Created
        var validSceneResp = await clientA.PostAsJsonAsync($"/api/v1/projects/{projA.Id}/scripts/{scriptA.Id}/scenes", new CreateSceneRequest(
            Title: "Valid Scene Alpha",
            Setting: null,
            Content: null,
            CharacterIds: [charA!.Id]
        ));
        Assert.Equal(HttpStatusCode.Created, validSceneResp.StatusCode);
        var validScene = await validSceneResp.Content.ReadFromJsonAsync<SceneDto>();
        Assert.NotNull(validScene);
        Assert.Contains(charA.Id, validScene.LinkedCharacterIds);

        // 5. User A attempts to update linked characters with Character B -> 422 Unprocessable Entity
        var crossUpdateResp = await clientA.PutAsJsonAsync(
            $"/api/v1/projects/{projA.Id}/scripts/{scriptA.Id}/scenes/{validScene.Id}/characters",
            new UpdateSceneCharactersRequest([charB.Id])
        );
        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossUpdateResp.StatusCode);
    }
}
