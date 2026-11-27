using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Common;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Shared.Contracts.Projects;

namespace CharacterConceptAI.Tests;

public class ProjectAndTeamTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;

    public ProjectAndTeamTests(TestApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, string UserId, string Email)> CreateAndLoginUserAsync(string displayName, string? role = null)
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
            Roles: role != null ? [role] : null
        ));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var account = await createResp.Content.ReadFromJsonAsync<AccountDto>();
        Assert.NotNull(account);

        var userClient = _factory.CreateClient();
        var userLoginResp = await userClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, userLoginResp.StatusCode);
        var userAuth = await userLoginResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userAuth!["accessToken"].ToString());

        return (userClient, account.Id, email);
    }

    [Fact]
    public async Task API006_Owner_AddsMember_AndChangesRole_Successfully()
    {
        var (ownerClient, ownerId, ownerEmail) = await CreateAndLoginUserAsync("Project Lead Owner");
        var (designerClient, designerId, designerEmail) = await CreateAndLoginUserAsync("Concept Artist Designer");

        // 1. Owner creates project
        var createProjResp = await ownerClient.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest(
            Name: "Team Collaboration Project",
            ProjectType: "Comic",
            Description: "Testing member invitations and roles"
        ));
        Assert.Equal(HttpStatusCode.Created, createProjResp.StatusCode);
        var project = await createProjResp.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(project);

        // 2. Owner adds Designer to project
        var addMemberResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{project.Id}/members", new AddProjectMemberRequest(
            Email: designerEmail,
            Role: "Designer"
        ));
        Assert.Equal(HttpStatusCode.Created, addMemberResp.StatusCode);
        var memberDto = await addMemberResp.Content.ReadFromJsonAsync<ProjectMemberDto>();
        Assert.NotNull(memberDto);
        Assert.Equal(designerId, memberDto.UserId);
        Assert.Equal("Designer", memberDto.Role);

        // Duplicate member addition should be rejected with 409 Conflict
        var dupResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{project.Id}/members", new AddProjectMemberRequest(designerEmail, "Designer"));
        Assert.Equal(HttpStatusCode.Conflict, dupResp.StatusCode);

        // 3. Designer can now access project characters
        var charListResp = await designerClient.GetAsync($"/api/v1/projects/{project.Id}/characters");
        Assert.Equal(HttpStatusCode.OK, charListResp.StatusCode);

        // 4. Owner promotes Designer to Owner
        var promoteResp = await ownerClient.PatchAsJsonAsync($"/api/v1/projects/{project.Id}/members/{designerId}", new UpdateProjectMemberRoleRequest("Owner"));
        Assert.Equal(HttpStatusCode.OK, promoteResp.StatusCode);

        // Verify members list shows 2 Owners
        var membersResp = await ownerClient.GetAsync($"/api/v1/projects/{project.Id}/members");
        Assert.Equal(HttpStatusCode.OK, membersResp.StatusCode);
        var members = await membersResp.Content.ReadFromJsonAsync<List<ProjectMemberDto>>();
        Assert.NotNull(members);
        Assert.Equal(2, members.Count(m => m.Role == "Owner"));
    }

    [Fact]
    public async Task API007_LastOwnerInvariant_PreventsDemotionAndRemoval_AllowsTransfer()
    {
        var (ownerClient, ownerId, _) = await CreateAndLoginUserAsync("Sole Owner");
        var (viewerClient, viewerId, viewerEmail) = await CreateAndLoginUserAsync("Project Viewer");

        // 1. Create project (actor is the only Owner)
        var createProjResp = await ownerClient.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest(
            Name: "Protected Ownership Project",
            ProjectType: "Game",
            Description: "Testing Last Owner Invariant"
        ));
        Assert.Equal(HttpStatusCode.Created, createProjResp.StatusCode);
        var project = await createProjResp.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(project);

        // Add a Viewer
        await ownerClient.PostAsJsonAsync($"/api/v1/projects/{project.Id}/members", new AddProjectMemberRequest(viewerEmail, "Viewer"));

        // 2. Attempt to demote sole Owner -> 409 Conflict
        var demoteResp = await ownerClient.PatchAsJsonAsync($"/api/v1/projects/{project.Id}/members/{ownerId}", new UpdateProjectMemberRoleRequest("Designer"));
        Assert.Equal(HttpStatusCode.Conflict, demoteResp.StatusCode);

        // 3. Attempt to remove sole Owner -> 409 Conflict
        var removeResp = await ownerClient.DeleteAsync($"/api/v1/projects/{project.Id}/members/{ownerId}");
        Assert.Equal(HttpStatusCode.Conflict, removeResp.StatusCode);

        // 4. Transfer ownership to Viewer
        var transferResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{project.Id}/transfer-ownership", new TransferOwnershipRequest(viewerId));
        Assert.Equal(HttpStatusCode.OK, transferResp.StatusCode);

        // Verify Viewer is now Owner, and original Owner was demoted to Designer
        var membersResp = await ownerClient.GetAsync($"/api/v1/projects/{project.Id}/members");
        var members = await membersResp.Content.ReadFromJsonAsync<List<ProjectMemberDto>>();
        Assert.NotNull(members);

        var newOwnerMember = members.FirstOrDefault(m => m.UserId == viewerId);
        Assert.NotNull(newOwnerMember);
        Assert.Equal("Owner", newOwnerMember.Role);

        var oldOwnerMember = members.FirstOrDefault(m => m.UserId == ownerId);
        Assert.NotNull(oldOwnerMember);
        Assert.Equal("Designer", oldOwnerMember.Role);

        // Now the old owner (who is now Designer) CAN be removed by the new Owner
        var deleteOldOwnerResp = await viewerClient.DeleteAsync($"/api/v1/projects/{project.Id}/members/{ownerId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteOldOwnerResp.StatusCode);
    }

    [Fact]
    public async Task API009_StaleRowVersion_Update_Returns409Conflict()
    {
        var (ownerClient, _, _) = await CreateAndLoginUserAsync("Concurrency Tester");

        // 1. Create project
        var createProjResp = await ownerClient.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest(
            Name: "Concurrency Baseline",
            ProjectType: "Animation",
            Description: "Initial version"
        ));
        Assert.Equal(HttpStatusCode.Created, createProjResp.StatusCode);
        var initial = await createProjResp.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(initial);

        // Fetch project detail to get active RowVersion
        var detailResp = await ownerClient.GetAsync($"/api/v1/projects/{initial.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
        var fetched = await detailResp.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(fetched);
        var originalRowVersion = fetched.RowVersion;
        Assert.NotNull(originalRowVersion);

        // 2. User 1 updates project successfully (bumping rowversion in SQL Server)
        var firstUpdateResp = await ownerClient.PutAsJsonAsync($"/api/v1/projects/{initial.Id}", new UpdateProjectRequest(
            Name: "Concurrency Baseline - Step 1",
            ProjectType: "Animation",
            Description: "Modified by user 1",
            Version: originalRowVersion
        ));
        Assert.Equal(HttpStatusCode.OK, firstUpdateResp.StatusCode);
        var updated = await firstUpdateResp.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(updated);
        Assert.NotEqual(originalRowVersion, updated.RowVersion);

        // 3. User 2 (or a concurrent tab) attempts to update using the stale original RowVersion -> 409 Conflict
        var staleUpdateResp = await ownerClient.PutAsJsonAsync($"/api/v1/projects/{initial.Id}", new UpdateProjectRequest(
            Name: "Concurrency Overwrite Attempt",
            ProjectType: "Animation",
            Description: "Stale update attempt",
            Version: originalRowVersion
        ));
        Assert.Equal(HttpStatusCode.Conflict, staleUpdateResp.StatusCode);
    }

    [Fact]
    public async Task API010_ArchivedProject_RejectsNewCharactersAndGenerations_RestoresSuccessfully()
    {
        var (ownerClient, _, _) = await CreateAndLoginUserAsync("Archival Tester");

        // 1. Create project and character
        var createProjResp = await ownerClient.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest(
            Name: "Project To Be Archived",
            ProjectType: "Game",
            Description: "Testing archival locking"
        ));
        Assert.Equal(HttpStatusCode.Created, createProjResp.StatusCode);
        var project = await createProjResp.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(project);

        var createCharResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{project.Id}/characters", new CreateCharacterRequest(
            Name: "Active Character",
            Role: "Protagonist",
            Description: "Created before archiving",
            InitialAttributes: null
        ));
        Assert.Equal(HttpStatusCode.Created, createCharResp.StatusCode);
        var character = await createCharResp.Content.ReadFromJsonAsync<CharacterDto>();
        Assert.NotNull(character);

        // 2. Archive project
        var archiveResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{project.Id}/archive", new { });
        Assert.Equal(HttpStatusCode.OK, archiveResp.StatusCode);

        // Verify status is Archived
        var getArchivedResp = await ownerClient.GetAsync($"/api/v1/projects/{project.Id}");
        var archivedProject = await getArchivedResp.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.Equal("Archived", archivedProject!.Status);

        // 3. Attempt to create new character in archived project -> 422 Unprocessable Entity
        var illegalCharResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{project.Id}/characters", new CreateCharacterRequest(
            Name: "Illegal Archived Character",
            Role: "Sidekick",
            Description: null,
            InitialAttributes: null
        ));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, illegalCharResp.StatusCode);

        // 4. Attempt to create generation in archived project -> 422 Unprocessable Entity
        var illegalGenResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{project.Id}/generations", new CreateGenerationRequest(
            CharacterId: character.Id,
            VariantId: null,
            PromptMode: PromptMode.Hybrid,
            RawPrompt: "Test prompt on archived",
            FinalPrompt: "Test prompt on archived",
            Width: 512,
            Height: 512,
            RequestedCount: 1,
            Seed: 12345,
            IdempotencyKey: Guid.NewGuid().ToString()
        ));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, illegalGenResp.StatusCode);

        // 5. Restore project -> 200 OK
        var restoreResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{project.Id}/restore", new { });
        Assert.Equal(HttpStatusCode.OK, restoreResp.StatusCode);

        // 6. Character creation succeeds again
        var legalCharResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{project.Id}/characters", new CreateCharacterRequest(
            Name: "Restored Character",
            Role: "Protagonist",
            Description: "Created after restoring",
            InitialAttributes: null
        ));
        Assert.Equal(HttpStatusCode.Created, legalCharResp.StatusCode);
    }

    [Fact]
    public async Task ProjectListing_OnlyReturnsMemberProjects_WithPagination()
    {
        var (user1Client, user1Id, _) = await CreateAndLoginUserAsync("Isolated User 1");
        var (user2Client, user2Id, _) = await CreateAndLoginUserAsync("Isolated User 2");

        // User 1 creates 3 projects
        for (int i = 1; i <= 3; i++)
        {
            await user1Client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest($"U1 Project {i}", "Game", null));
        }

        // User 2 creates 2 projects
        for (int i = 1; i <= 2; i++)
        {
            await user2Client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest($"U2 Project {i}", "Comic", null));
        }

        // User 1 queries projects -> should see exactly 3
        var u1Resp = await user1Client.GetAsync("/api/v1/projects?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, u1Resp.StatusCode);
        var u1Projects = await u1Resp.Content.ReadFromJsonAsync<PagedResult<ProjectDto>>();
        Assert.NotNull(u1Projects);
        Assert.Equal(3, u1Projects.TotalCount);
        Assert.All(u1Projects.Items, p => Assert.StartsWith("U1 Project", p.Name));

        // User 2 queries projects -> should see exactly 2
        var u2Resp = await user2Client.GetAsync("/api/v1/projects?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, u2Resp.StatusCode);
        var u2Projects = await u2Resp.Content.ReadFromJsonAsync<PagedResult<ProjectDto>>();
        Assert.NotNull(u2Projects);
        Assert.Equal(2, u2Projects.TotalCount);
        Assert.All(u2Projects.Items, p => Assert.StartsWith("U2 Project", p.Name));
    }
}
