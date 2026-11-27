using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Concepts;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Shared.Contracts.Projects;

namespace CharacterConceptAI.Tests;

public class MultiClientLanFlowIntegrationTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;

    public MultiClientLanFlowIntegrationTests(TestApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, string UserId, string Email)> CreateAuthenticatedClientAsync(string email, string password, string displayName)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var existing = await userManager.FindByEmailAsync(email);
            if (existing == null)
            {
                var newUser = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    DisplayName = displayName,
                    IsActive = true,
                    EmailConfirmed = true
                };
                var createResult = await userManager.CreateAsync(newUser, password);
                if (!createResult.Succeeded)
                {
                    throw new InvalidOperationException($"Failed to create user {email}: {string.Join(", ", createResult.Errors.Select(e => e.Description))}");
                }
            }
        }

        var client = _factory.CreateClient();
        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);

        var loginResult = await loginResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(loginResult);
        var token = loginResult["accessToken"].ToString();
        Assert.False(string.IsNullOrEmpty(token));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var meResp = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);
        var me = await meResp.Content.ReadFromJsonAsync<UserInfoResponse>();
        Assert.NotNull(me);

        return (client, me.Id, email);
    }

    [Fact]
    public async Task TwoClientSimulation_OwnerAndDesigner_Collaboration_ConcurrentJobs_And_ApprovalSecurity()
    {
        // Setup two distinct client sessions simulating two workstations on LAN
        var (clientOwner, ownerId, ownerEmail) = await CreateAuthenticatedClientAsync(
            "owner.lan@studio.local", "OwnerPass123!", "Lead Art Director (PC-01)");

        var (clientDesigner, designerId, designerEmail) = await CreateAuthenticatedClientAsync(
            "designer.lan@studio.local", "DesignerPass123!", "Junior Concept Artist (PC-02)");

        // 1. Owner (PC-01) creates a shared collaborative project
        var projectResp = await clientOwner.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest(
            Name: "LAN Anime Studio Masterpiece",
            ProjectType: "Anime Series",
            Description: "Collaborative project across PC-01 (Owner) and PC-02 (Designer)"
        ));
        Assert.Equal(HttpStatusCode.Created, projectResp.StatusCode);
        var project = await projectResp.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(project);
        var projectId = project.Id;

        // 2. Designer (PC-02) does not have access initially
        var designerInitialAccessResp = await clientDesigner.GetAsync($"/api/v1/projects/{projectId}");
        Assert.Equal(HttpStatusCode.NotFound, designerInitialAccessResp.StatusCode);

        // 3. Owner (PC-01) adds Designer (PC-02) to the project with role "Designer"
        var addMemberResp = await clientOwner.PostAsJsonAsync($"/api/v1/projects/{projectId}/members", new AddProjectMemberRequest(
            Email: designerEmail,
            Role: "Designer"
        ));
        Assert.Equal(HttpStatusCode.Created, addMemberResp.StatusCode);

        // 4. Designer (PC-02) now has access to the project
        var designerMemberAccessResp = await clientDesigner.GetAsync($"/api/v1/projects/{projectId}");
        Assert.Equal(HttpStatusCode.OK, designerMemberAccessResp.StatusCode);

        // 5. Designer (PC-02) creates a character
        var charResp = await clientDesigner.PostAsJsonAsync($"/api/v1/projects/{projectId}/characters", new CreateCharacterRequest(
            Name: "Kagura Tsuki",
            Role: "Ninja Assassin",
            Description: "Twin daggers shadow operative",
            InitialAttributes: null
        ));
        Assert.Equal(HttpStatusCode.Created, charResp.StatusCode);
        var character = await charResp.Content.ReadFromJsonAsync<CharacterDto>();
        Assert.NotNull(character);
        var charId = character.Id;

        // 6. Concurrency Test: Both Owner (PC-01) and Designer (PC-02) submit generation requests simultaneously
        var ownerJobTask = clientOwner.PostAsJsonAsync($"/api/v1/projects/{projectId}/generations", new CreateGenerationRequest(
            CharacterId: charId,
            VariantId: null,
            PromptMode: PromptMode.Raw,
            RawPrompt: "Owner direction: Dramatic moonlit background",
            FinalPrompt: "Dramatic moonlit background anime wallpaper",
            PromptTemplateVersion: "1.0",
            Width: 512,
            Height: 512,
            RequestedCount: 1,
            Seed: 101010,
            IdempotencyKey: Guid.NewGuid().ToString("N")
        ));

        var designerJobTask = clientDesigner.PostAsJsonAsync($"/api/v1/projects/{projectId}/generations", new CreateGenerationRequest(
            CharacterId: charId,
            VariantId: null,
            PromptMode: PromptMode.Raw,
            RawPrompt: "Designer iteration: Character close-up action pose",
            FinalPrompt: "Character close-up action pose with twin daggers",
            PromptTemplateVersion: "1.0",
            Width: 512,
            Height: 512,
            RequestedCount: 1,
            Seed: 202020,
            IdempotencyKey: Guid.NewGuid().ToString("N")
        ));

        var jobResponses = await Task.WhenAll(ownerJobTask, designerJobTask);
        Assert.Equal(HttpStatusCode.Accepted, jobResponses[0].StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, jobResponses[1].StatusCode);

        var ownerJobUrl = jobResponses[0].Headers.Location!;
        var designerJobUrl = jobResponses[1].Headers.Location!;

        // Poll both jobs concurrently until completion
        async Task<GenerationJobDto> PollJobAsync(HttpClient c, Uri url)
        {
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(400);
                var r = await c.GetAsync(url);
                if (r.IsSuccessStatusCode)
                {
                    var j = await r.Content.ReadFromJsonAsync<GenerationJobDto>();
                    if (j?.Status == GenerationStatus.Completed) return j;
                }
            }
            throw new TimeoutException($"Job {url} timed out.");
        }

        var completedJobs = await Task.WhenAll(
            PollJobAsync(clientOwner, ownerJobUrl),
            PollJobAsync(clientDesigner, designerJobUrl)
        );

        var ownerJob = completedJobs[0];
        var designerJob = completedJobs[1];
        Assert.Equal(GenerationStatus.Completed, ownerJob.Status);
        Assert.Equal(GenerationStatus.Completed, designerJob.Status);
        Assert.NotEmpty(designerJob.OutputAssetIds);

        var selectedAssetId = designerJob.OutputAssetIds[0];

        // 7. Designer (PC-02) submits a Concept and Version from the generated asset
        var createConceptResp = await clientDesigner.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/characters/{charId}/concepts",
            new CreateConceptRequest(
                VariantId: null,
                Title: "Kagura Action Stance v1",
                Description: "Submitted by Designer for Lead approval",
                GenerationJobId: designerJob.Id,
                ImageAssetId: selectedAssetId
            ));
        Assert.Equal(HttpStatusCode.Created, createConceptResp.StatusCode);
        var concept = await createConceptResp.Content.ReadFromJsonAsync<CharacterConceptDto>();
        Assert.NotNull(concept);
        Assert.NotNull(concept.RepresentativeVersionId);
        var conceptVersionId = concept.RepresentativeVersionId.Value;

        // Designer submits version for review
        var submitResp = await clientDesigner.PostAsync(
            $"/api/v1/projects/{projectId}/concepts/{concept.Id}/versions/{conceptVersionId}/submit", null);
        Assert.Equal(HttpStatusCode.OK, submitResp.StatusCode);

        // 8. SECURITY BOUNDARY VERIFICATION: Designer (PC-02) tries to approve their own concept version
        // Expected: 403 Forbidden because only Owner can approve
        var unauthorizedApproveResp = await clientDesigner.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/concepts/{concept.Id}/versions/{conceptVersionId}/review",
            new ReviewConceptVersionRequest(
                Decision: ConceptApprovalDecision.Approved,
                Comment: "Designer self-approving"
            ));
        Assert.Equal(HttpStatusCode.Forbidden, unauthorizedApproveResp.StatusCode);

        // 9. Owner (PC-01) legitimately approves the concept version
        var authorizedApproveResp = await clientOwner.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/concepts/{concept.Id}/versions/{conceptVersionId}/review",
            new ReviewConceptVersionRequest(
                Decision: ConceptApprovalDecision.Approved,
                Comment: "Approved by Lead Art Director. Looks excellent for production!"
            ));
        Assert.Equal(HttpStatusCode.OK, authorizedApproveResp.StatusCode);

        // 10. Designer (PC-02) fetches concept detail and verifies status is Approved
        var designerDetailResp = await clientDesigner.GetAsync($"/api/v1/projects/{projectId}/concepts/{concept.Id}");
        Assert.Equal(HttpStatusCode.OK, designerDetailResp.StatusCode);
        var detail = await designerDetailResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(detail);
        Assert.True(detail.ContainsKey("concept"));

        // 11. IMMUTABILITY VERIFICATION: Attempting to re-approve an approved version yields 409 Conflict (UNIT-012)
        var reApproveResp = await clientOwner.PostAsJsonAsync(
            $"/api/v1/projects/{projectId}/concepts/{concept.Id}/versions/{conceptVersionId}/review",
            new ReviewConceptVersionRequest(
                Decision: ConceptApprovalDecision.Approved,
                Comment: "Attempt re-approval"
            ));
        Assert.Equal(HttpStatusCode.Conflict, reApproveResp.StatusCode);
    }
}
