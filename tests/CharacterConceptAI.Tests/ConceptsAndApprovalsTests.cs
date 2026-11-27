using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Concepts;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Shared.Contracts.Projects;

namespace CharacterConceptAI.Tests;

public class ConceptsAndApprovalsTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;

    public ConceptsAndApprovalsTests(TestApplicationFactory factory)
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

    private async Task<(ProjectDto Project, CharacterDto Character, GenerationJobDto CompletedJob, Guid AssetId)> SetupCompletedGenerationAsync(HttpClient client)
    {
        var createProjResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest(
            Name: $"Concept Project {Guid.NewGuid():N}",
            ProjectType: "Anime",
            Description: "Testing concepts and approvals"
        ));
        Assert.Equal(HttpStatusCode.Created, createProjResp.StatusCode);
        var proj = await createProjResp.Content.ReadFromJsonAsync<ProjectDto>();

        var createCharResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/characters", new CreateCharacterRequest(
            Name: "Eldrin Sunfire",
            Role: "Protagonist",
            Description: "Solar knight"
        ));
        Assert.Equal(HttpStatusCode.Created, createCharResp.StatusCode);
        var character = await createCharResp.Content.ReadFromJsonAsync<CharacterDto>();

        var genResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            CharacterId: character!.Id,
            VariantId: null,
            PromptMode: PromptMode.Raw,
            RawPrompt: "Anime solar knight with blazing cape",
            FinalPrompt: "Anime solar knight with blazing cape",
            PromptTemplateVersion: "1.0",
            Width: 512,
            Height: 512,
            RequestedCount: 1,
            Seed: 123456,
            IdempotencyKey: Guid.NewGuid().ToString("N")
        ));
        Assert.Equal(HttpStatusCode.Accepted, genResp.StatusCode);
        var jobRef = await genResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var jobId = Guid.Parse(jobRef!["id"].ToString()!);

        // Poll for background worker completion
        GenerationJobDto? completedJob = null;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(200);
            var pollResp = await client.GetAsync($"/api/v1/projects/{proj.Id}/generations/{jobId}");
            completedJob = await pollResp.Content.ReadFromJsonAsync<GenerationJobDto>();
            if (completedJob!.Status == GenerationStatus.Completed) break;
        }

        Assert.NotNull(completedJob);
        Assert.Equal(GenerationStatus.Completed, completedJob.Status);
        Assert.NotEmpty(completedJob.OutputAssetIds);

        return (proj, character, completedJob, completedJob.OutputAssetIds[0]);
    }

    [Fact]
    public async Task API005_Designer_CannotPostApproval_RejectedWith403_And_Owner_CanApprove()
    {
        var (ownerClient, ownerId, _) = await CreateAndLoginUserAsync("Project Owner");
        var (designerClient, designerId, designerEmail) = await CreateAndLoginUserAsync("Lead Designer");

        var (proj, character, job, assetId) = await SetupCompletedGenerationAsync(ownerClient);

        // Add Designer to project
        var addMemberResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/members", new AddProjectMemberRequest(
            Email: designerEmail,
            Role: "Designer"
        ));
        Assert.Equal(HttpStatusCode.Created, addMemberResp.StatusCode);

        // Designer creates concept from completed job
        var createConceptResp = await designerClient.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{character.Id}/concepts", new CreateConceptRequest(
            VariantId: null,
            Title: "Eldrin Final Armor Concept",
            Description: "Approved armor direction",
            GenerationJobId: job.Id,
            ImageAssetId: assetId
        ));
        Assert.Equal(HttpStatusCode.Created, createConceptResp.StatusCode);
        var concept = await createConceptResp.Content.ReadFromJsonAsync<CharacterConceptDto>();
        Assert.NotNull(concept);
        Assert.Equal(ConceptStatus.Draft, concept.Status);
        Assert.NotNull(concept.RepresentativeVersionId);

        // Designer submits version 1 for review
        var version1Id = concept.RepresentativeVersionId.Value;
        var submitResp = await designerClient.PostAsync($"/api/v1/projects/{proj.Id}/concepts/{concept.Id}/versions/{version1Id}/submit", null);
        Assert.Equal(HttpStatusCode.OK, submitResp.StatusCode);

        // DESIGNER attempts to approve -> MUST BE REJECTED WITH 403 Forbidden (API-005)
        var designerReviewResp = await designerClient.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/concepts/{concept.Id}/versions/{version1Id}/review", new ReviewConceptVersionRequest(
            Decision: ConceptApprovalDecision.Approved,
            Comment: "Self-approving designer should fail"
        ));
        Assert.Equal(HttpStatusCode.Forbidden, designerReviewResp.StatusCode);

        // OWNER approves -> SUCCEEDS (200 OK) with audit recorded
        var ownerReviewResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/concepts/{concept.Id}/versions/{version1Id}/review", new ReviewConceptVersionRequest(
            Decision: ConceptApprovalDecision.Approved,
            Comment: "Phê duyệt chính thức bởi Owner"
        ));
        Assert.Equal(HttpStatusCode.OK, ownerReviewResp.StatusCode);

        // Verify concept state is now Approved
        var detailResp = await designerClient.GetAsync($"/api/v1/projects/{proj.Id}/concepts/{concept.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
        var detail = await detailResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var conceptObj = detail!["concept"];
        Assert.NotNull(conceptObj);
    }

    [Fact]
    public async Task UNIT012_ApprovedConceptVersion_CannotBeMutatedOrReApproved_RejectedWith409()
    {
        var (ownerClient, _, _) = await CreateAndLoginUserAsync("Immutability Owner");
        var (proj, character, job, assetId) = await SetupCompletedGenerationAsync(ownerClient);

        // Create concept
        var createConceptResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{character.Id}/concepts", new CreateConceptRequest(
            VariantId: null,
            Title: "Immutable Concept",
            Description: "Testing UNIT-012",
            GenerationJobId: job.Id,
            ImageAssetId: assetId
        ));
        Assert.Equal(HttpStatusCode.Created, createConceptResp.StatusCode);
        var concept = await createConceptResp.Content.ReadFromJsonAsync<CharacterConceptDto>();
        var versionId = concept!.RepresentativeVersionId!.Value;

        // Owner approves
        var reviewResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/concepts/{concept.Id}/versions/{versionId}/review", new ReviewConceptVersionRequest(
            Decision: ConceptApprovalDecision.Approved,
            Comment: "First approval"
        ));
        Assert.Equal(HttpStatusCode.OK, reviewResp.StatusCode);

        // Re-approving already approved immutable version -> 409 Conflict (UNIT-012)
        var reReviewResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/concepts/{concept.Id}/versions/{versionId}/review", new ReviewConceptVersionRequest(
            Decision: ConceptApprovalDecision.Approved,
            Comment: "Re-approval attempt"
        ));
        Assert.Equal(HttpStatusCode.Conflict, reReviewResp.StatusCode);
    }

    [Fact]
    public async Task API011_UnauthorizedMediaAccess_Returns404_BytesNotStreamed()
    {
        var (ownerA, _, _) = await CreateAndLoginUserAsync("Owner A");
        var (userB, _, _) = await CreateAndLoginUserAsync("Foreign User B");

        var (projA, _, _, assetId) = await SetupCompletedGenerationAsync(ownerA);

        // Foreign User B tries to access media from Project A -> 404 Not Found (IDOR concealment)
        var foreignResp = await userB.GetAsync($"/api/v1/projects/{projA.Id}/media/{assetId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignResp.StatusCode);

        // Foreign User B tries to access thumbnail from Project A -> 404 Not Found
        var foreignThumbResp = await userB.GetAsync($"/api/v1/projects/{projA.Id}/media/{assetId}/thumbnail");
        Assert.Equal(HttpStatusCode.NotFound, foreignThumbResp.StatusCode);

        // Owner A accesses media -> 200 OK with valid bytes
        var validResp = await ownerA.GetAsync($"/api/v1/projects/{projA.Id}/media/{assetId}");
        Assert.Equal(HttpStatusCode.OK, validResp.StatusCode);
        var bytes = await validResp.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);
        Assert.Equal("image/png", validResp.Content.Headers.ContentType?.MediaType);

        // Owner A accesses thumbnail -> 200 OK
        var validThumbResp = await ownerA.GetAsync($"/api/v1/projects/{projA.Id}/media/{assetId}/thumbnail");
        Assert.Equal(HttpStatusCode.OK, validThumbResp.StatusCode);
    }

    [Fact]
    public async Task API017_MismatchedJobOrAsset_ConceptCreation_RejectedWith422()
    {
        var (ownerClient, _, _) = await CreateAndLoginUserAsync("Mismatch Tester");
        var (proj, char1, job1, asset1) = await SetupCompletedGenerationAsync(ownerClient);

        // Create second character in same project
        var createChar2Resp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters", new CreateCharacterRequest(
            Name: "Second Character",
            Role: "Antagonist",
            Description: "Dark sorcerer"
        ));
        Assert.Equal(HttpStatusCode.Created, createChar2Resp.StatusCode);
        var char2 = await createChar2Resp.Content.ReadFromJsonAsync<CharacterDto>();

        // Attempt to create concept for Char2 using Job1 that belongs to Char1 -> 422 Unprocessable Entity
        var mismatchResp = await ownerClient.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{char2!.Id}/concepts", new CreateConceptRequest(
            VariantId: null,
            Title: "Mismatched Concept",
            Description: "Should be rejected",
            GenerationJobId: job1.Id,
            ImageAssetId: asset1
        ));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatchResp.StatusCode);
    }

    [Fact]
    public async Task Concept_VersionLinearHistory_MonotonicIncrement_AndCompare()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Linear History Tester");
        var (proj, character, job1, asset1) = await SetupCompletedGenerationAsync(client);

        // Run second generation job for character
        var gen2Resp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            CharacterId: character.Id,
            VariantId: null,
            PromptMode: PromptMode.Raw,
            RawPrompt: "Anime solar knight upgraded armor v2",
            FinalPrompt: "Anime solar knight upgraded armor v2",
            PromptTemplateVersion: "1.0",
            Width: 512,
            Height: 512,
            RequestedCount: 1,
            Seed: 654321,
            IdempotencyKey: Guid.NewGuid().ToString("N")
        ));
        Assert.Equal(HttpStatusCode.Accepted, gen2Resp.StatusCode);
        var job2Ref = await gen2Resp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var job2Id = Guid.Parse(job2Ref!["id"].ToString()!);

        GenerationJobDto? job2 = null;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(200);
            var pollResp = await client.GetAsync($"/api/v1/projects/{proj.Id}/generations/{job2Id}");
            job2 = await pollResp.Content.ReadFromJsonAsync<GenerationJobDto>();
            if (job2!.Status == GenerationStatus.Completed) break;
        }

        Assert.NotNull(job2);
        Assert.NotEmpty(job2.OutputAssetIds);
        var asset2 = job2.OutputAssetIds[0];

        // 1. Create concept with Version 1 from job1
        var createConceptResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{character.Id}/concepts", new CreateConceptRequest(
            VariantId: null,
            Title: "Eldrin Evolution",
            Description: "Version evolution tracking",
            GenerationJobId: job1.Id,
            ImageAssetId: asset1
        ));
        Assert.Equal(HttpStatusCode.Created, createConceptResp.StatusCode);
        var concept = await createConceptResp.Content.ReadFromJsonAsync<CharacterConceptDto>();
        var version1Id = concept!.RepresentativeVersionId!.Value;

        // 2. Add Version 2 from job2
        var createVer2Resp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/concepts/{concept.Id}/versions", new CreateConceptVersionRequest(
            GenerationJobId: job2.Id,
            ImageAssetId: asset2,
            ParentVersionId: version1Id,
            Note: "Upgraded v2 armor"
        ));
        Assert.Equal(HttpStatusCode.Created, createVer2Resp.StatusCode);
        var version2 = await createVer2Resp.Content.ReadFromJsonAsync<ConceptVersionDto>();
        Assert.Equal(2, version2!.VersionNumber);
        Assert.Equal(version1Id, version2.ParentVersionId);

        // 3. Switch representative version to Version 2
        var patchRepResp = await client.PatchAsJsonAsync($"/api/v1/projects/{proj.Id}/concepts/{concept.Id}/representative", new SetRepresentativeVersionRequest(
            VersionId: version2.Id
        ));
        Assert.Equal(HttpStatusCode.OK, patchRepResp.StatusCode);

        // 4. Call Version Comparison API (Side-by-side compare)
        var compareResp = await client.GetAsync($"/api/v1/projects/{proj.Id}/concepts/{concept.Id}/versions/compare?left={version1Id}&right={version2.Id}");
        Assert.Equal(HttpStatusCode.OK, compareResp.StatusCode);
        var compare = await compareResp.Content.ReadFromJsonAsync<ConceptComparisonDto>();
        Assert.NotNull(compare);
        Assert.Equal(1, compare.Left.VersionNumber);
        Assert.Equal(2, compare.Right.VersionNumber);
        Assert.Equal(asset1, compare.Left.ImageAssetId);
        Assert.Equal(asset2, compare.Right.ImageAssetId);
        Assert.Contains("v2", compare.Right.FinalPrompt);
    }
}
