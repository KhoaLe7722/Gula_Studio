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

public class ReferenceImg2ImgTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;

    public ReferenceImg2ImgTests(TestApplicationFactory factory)
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

    private async Task<(ProjectDto Project, CharacterDto Character, ConceptVersionDto Version1, Guid AssetId)> SetupConceptWithVersionAsync(HttpClient client)
    {
        var createProjResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest(
            Name: $"Img2Img Test Project {Guid.NewGuid():N}",
            ProjectType: "Anime",
            Description: "Testing P09 Img2Img workflow"
        ));
        Assert.Equal(HttpStatusCode.Created, createProjResp.StatusCode);
        var proj = await createProjResp.Content.ReadFromJsonAsync<ProjectDto>();

        var createCharResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/characters", new CreateCharacterRequest(
            Name: "Kaelen Shadowblade",
            Role: "Rogue",
            Description: "Shadow assassin"
        ));
        Assert.Equal(HttpStatusCode.Created, createCharResp.StatusCode);
        var character = await createCharResp.Content.ReadFromJsonAsync<CharacterDto>();

        // T2I base generation
        var genResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            CharacterId: character!.Id,
            VariantId: null,
            PromptMode: PromptMode.Raw,
            RawPrompt: "Anime shadow rogue with silver daggers",
            FinalPrompt: "Anime shadow rogue with silver daggers",
            PromptTemplateVersion: "1.0",
            Width: 512,
            Height: 512,
            RequestedCount: 1,
            Seed: 112233,
            IdempotencyKey: Guid.NewGuid().ToString("N")
        ));
        Assert.Equal(HttpStatusCode.Accepted, genResp.StatusCode);
        var jobRef = await genResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var jobId = Guid.Parse(jobRef!["id"].ToString()!);

        // Poll for T2I completion
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
        var assetId = completedJob.OutputAssetIds[0];

        // Create concept from completed job
        var createConceptResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{character.Id}/concepts", new CreateConceptRequest(
            VariantId: null,
            Title: "Kaelen Stealth Suit",
            Description: "Base design",
            GenerationJobId: completedJob.Id,
            ImageAssetId: assetId
        ));
        Assert.Equal(HttpStatusCode.Created, createConceptResp.StatusCode);
        var concept = await createConceptResp.Content.ReadFromJsonAsync<CharacterConceptDto>();

        // Get concept detail with versions
        var detailResp = await client.GetAsync($"/api/v1/projects/{proj.Id}/concepts/{concept!.Id}");
        var detail = await detailResp.Content.ReadFromJsonAsync<ConceptDetailDto>();
        Assert.NotNull(detail);
        Assert.NotEmpty(detail.Versions);

        return (proj, character, detail.Versions[0], assetId);
    }

    [Fact]
    public async Task U08_ReferenceImg2Img_JobEnqueued_WithDenoiseAndReferenceVersion_AndCompletedSuccessfully()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Img2Img Creator");
        var (proj, character, ver1, assetId) = await SetupConceptWithVersionAsync(client);

        // Submit Img2Img request consuming ver1 as reference with denoise 0.55
        var i2iResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            CharacterId: character.Id,
            VariantId: null,
            PromptMode: PromptMode.Raw,
            RawPrompt: "Anime shadow rogue, smiling expression, glowing purple cape",
            FinalPrompt: "Anime shadow rogue, smiling expression, glowing purple cape",
            PromptTemplateVersion: "1.0",
            Width: 512,
            Height: 512,
            RequestedCount: 1,
            Seed: 998877,
            IdempotencyKey: Guid.NewGuid().ToString("N"),
            ReferenceConceptVersionId: ver1.Id,
            Denoise: 0.55
        ));
        Assert.Equal(HttpStatusCode.Accepted, i2iResp.StatusCode);
        var jobRef = await i2iResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var i2iJobId = Guid.Parse(jobRef!["id"].ToString()!);

        // Poll for Img2Img job completion
        GenerationJobDto? completedJob = null;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(200);
            var pollResp = await client.GetAsync($"/api/v1/projects/{proj.Id}/generations/{i2iJobId}");
            completedJob = await pollResp.Content.ReadFromJsonAsync<GenerationJobDto>();
            if (completedJob!.Status == GenerationStatus.Completed) break;
        }

        Assert.NotNull(completedJob);
        Assert.Equal(GenerationStatus.Completed, completedJob.Status);
        Assert.Equal("i2i_reference", completedJob.WorkflowKey);
        Assert.Equal(ver1.Id, completedJob.ReferenceConceptVersionId);
        Assert.Equal(0.55, completedJob.Denoise);
        Assert.NotEmpty(completedJob.OutputAssetIds);

        // Derive Version 2 from the completed Img2Img job
        var createVer2Resp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/concepts/{ver1.ConceptId}/versions", new CreateConceptVersionRequest(
            GenerationJobId: completedJob.Id,
            ImageAssetId: completedJob.OutputAssetIds[0],
            ParentVersionId: ver1.Id,
            Note: "Img2Img iteration with denoise 0.55"
        ));
        Assert.Equal(HttpStatusCode.Created, createVer2Resp.StatusCode);
        var ver2 = await createVer2Resp.Content.ReadFromJsonAsync<ConceptVersionDto>();
        Assert.NotNull(ver2);
        Assert.Equal(2, ver2.VersionNumber);
        Assert.Equal(ver1.Id, ver2.ParentVersionId);
    }

    [Fact]
    public async Task API_CrossProject_ReferenceVersion_Access_RejectedWith404()
    {
        var (clientA, _, _) = await CreateAndLoginUserAsync("User A");
        var (clientB, _, _) = await CreateAndLoginUserAsync("User B");

        var (projA, charA, verA, _) = await SetupConceptWithVersionAsync(clientA);

        // User B creates their own project and character
        var createProjBResp = await clientB.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest(
            Name: "User B Project",
            ProjectType: "Anime",
            Description: "Project B"
        ));
        Assert.Equal(HttpStatusCode.Created, createProjBResp.StatusCode);
        var projB = await createProjBResp.Content.ReadFromJsonAsync<ProjectDto>();

        var createCharBResp = await clientB.PostAsJsonAsync($"/api/v1/projects/{projB!.Id}/characters", new CreateCharacterRequest(
            Name: "Character B",
            Role: "Mage",
            Description: "Mage B"
        ));
        Assert.Equal(HttpStatusCode.Created, createCharBResp.StatusCode);
        var charB = await createCharBResp.Content.ReadFromJsonAsync<CharacterDto>();

        // User B tries to use verA from Project A as reference in Project B -> 404 Not Found (IDOR concealment)
        var crossResp = await clientB.PostAsJsonAsync($"/api/v1/projects/{projB.Id}/generations", new CreateGenerationRequest(
            CharacterId: charB!.Id,
            VariantId: null,
            PromptMode: PromptMode.Raw,
            RawPrompt: "Cross-project theft attempt",
            FinalPrompt: "Cross-project theft attempt",
            PromptTemplateVersion: "1.0",
            Width: 512,
            Height: 512,
            RequestedCount: 1,
            Seed: 123456,
            IdempotencyKey: Guid.NewGuid().ToString("N"),
            ReferenceConceptVersionId: verA.Id,
            Denoise: 0.6
        ));
        Assert.Equal(HttpStatusCode.NotFound, crossResp.StatusCode);
    }

    [Fact]
    public async Task API_MismatchedCharacter_ReferenceVersion_RejectedWith422()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Mismatch Tester");
        var (proj, char1, ver1, _) = await SetupConceptWithVersionAsync(client);

        // Create second character in same project
        var createChar2Resp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters", new CreateCharacterRequest(
            Name: "Different Character",
            Role: "Warrior",
            Description: "Heavy tank"
        ));
        Assert.Equal(HttpStatusCode.Created, createChar2Resp.StatusCode);
        var char2 = await createChar2Resp.Content.ReadFromJsonAsync<CharacterDto>();

        // Request Img2Img for char2 using ver1 which belongs to char1 -> 422 Unprocessable Entity
        var mismatchResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            CharacterId: char2!.Id,
            VariantId: null,
            PromptMode: PromptMode.Raw,
            RawPrompt: "Mismatched character reference test",
            FinalPrompt: "Mismatched character reference test",
            PromptTemplateVersion: "1.0",
            Width: 512,
            Height: 512,
            RequestedCount: 1,
            Seed: 554433,
            IdempotencyKey: Guid.NewGuid().ToString("N"),
            ReferenceConceptVersionId: ver1.Id,
            Denoise: 0.6
        ));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatchResp.StatusCode);
    }

    [Fact]
    public async Task I2I_ClampsDenoiseRange_Properly()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Clamp Tester");
        var (proj, character, ver1, _) = await SetupConceptWithVersionAsync(client);

        // High denoise 1.8 -> clamped to 1.0
        var highDenoiseResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            CharacterId: character.Id,
            VariantId: null,
            PromptMode: PromptMode.Raw,
            RawPrompt: "High denoise test",
            FinalPrompt: "High denoise test",
            PromptTemplateVersion: "1.0",
            Width: 512,
            Height: 512,
            RequestedCount: 1,
            Seed: 771122,
            IdempotencyKey: Guid.NewGuid().ToString("N"),
            ReferenceConceptVersionId: ver1.Id,
            Denoise: 1.8
        ));
        Assert.Equal(HttpStatusCode.Accepted, highDenoiseResp.StatusCode);
        var highJobRef = await highDenoiseResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var highJobId = Guid.Parse(highJobRef!["id"].ToString()!);

        var getHighJob = await client.GetAsync($"/api/v1/projects/{proj.Id}/generations/{highJobId}");
        var highJobDto = await getHighJob.Content.ReadFromJsonAsync<GenerationJobDto>();
        Assert.Equal(1.0, highJobDto!.Denoise);

        // Low denoise -0.5 -> clamped to 0.05
        var lowDenoiseResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            CharacterId: character.Id,
            VariantId: null,
            PromptMode: PromptMode.Raw,
            RawPrompt: "Low denoise test",
            FinalPrompt: "Low denoise test",
            PromptTemplateVersion: "1.0",
            Width: 512,
            Height: 512,
            RequestedCount: 1,
            Seed: 882233,
            IdempotencyKey: Guid.NewGuid().ToString("N"),
            ReferenceConceptVersionId: ver1.Id,
            Denoise: -0.5
        ));
        Assert.Equal(HttpStatusCode.Accepted, lowDenoiseResp.StatusCode);
        var lowJobRef = await lowDenoiseResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var lowJobId = Guid.Parse(lowJobRef!["id"].ToString()!);

        var getLowJob = await client.GetAsync($"/api/v1/projects/{proj.Id}/generations/{lowJobId}");
        var lowJobDto = await getLowJob.Content.ReadFromJsonAsync<GenerationJobDto>();
        Assert.Equal(0.05, lowJobDto!.Denoise);
    }
}
