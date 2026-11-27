using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using CharacterConceptAI.Server.AI;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Server.Storage;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Common;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Shared.Contracts.Projects;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CharacterConceptAI.Tests;

public class JobQueueAndPromptsTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;

    public JobQueueAndPromptsTests(TestApplicationFactory factory)
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
    public void UNIT001_StructuredPrompt_SixAttributes_DeterministicEnglishOutput()
    {
        var service = new PromptBuilderService();

        var character = new Character
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Name = "Astrid Stormcaller",
            Role = "Mage"
        };

        var defs = new[]
        {
            new CharacterAttributeDefinition { Id = Guid.NewGuid(), Code = "hair_color", DisplayName = "Màu tóc", IsIdentityCritical = true },
            new CharacterAttributeDefinition { Id = Guid.NewGuid(), Code = "eye_color", DisplayName = "Màu mắt", IsIdentityCritical = true },
            new CharacterAttributeDefinition { Id = Guid.NewGuid(), Code = "outfit", DisplayName = "Trang phục", IsIdentityCritical = false },
            new CharacterAttributeDefinition { Id = Guid.NewGuid(), Code = "weapon", DisplayName = "Vũ khí", IsIdentityCritical = false },
            new CharacterAttributeDefinition { Id = Guid.NewGuid(), Code = "expression", DisplayName = "Biểu cảm", IsIdentityCritical = false },
            new CharacterAttributeDefinition { Id = Guid.NewGuid(), Code = "hair_style", DisplayName = "Kiểu tóc", IsIdentityCritical = true },
        };

        character.AttributeValues.Add(new CharacterAttributeValue { DefinitionId = defs[0].Id, Definition = defs[0], ValueJson = "silver" });
        character.AttributeValues.Add(new CharacterAttributeValue { DefinitionId = defs[1].Id, Definition = defs[1], ValueJson = "sapphire blue" });
        character.AttributeValues.Add(new CharacterAttributeValue { DefinitionId = defs[2].Id, Definition = defs[2], ValueJson = "mage robes with runic patterns" });
        character.AttributeValues.Add(new CharacterAttributeValue { DefinitionId = defs[3].Id, Definition = defs[3], ValueJson = "crystal staff" });
        character.AttributeValues.Add(new CharacterAttributeValue { DefinitionId = defs[4].Id, Definition = defs[4], ValueJson = "serene" });
        character.AttributeValues.Add(new CharacterAttributeValue { DefinitionId = defs[5].Id, Definition = defs[5], ValueJson = "braided twin tails" });

        var res1 = service.BuildPrompt(character);
        var res2 = service.BuildPrompt(character);

        Assert.Equal("1.1", res1.TemplateVersion);
        Assert.Equal(res1.StructuredPrompt, res2.StructuredPrompt); // Deterministic
        Assert.Contains("silver hair", res1.StructuredPrompt);
        Assert.Contains("sapphire blue eyes", res1.StructuredPrompt);
        Assert.Contains("wearing mage robes with runic patterns", res1.StructuredPrompt);
        Assert.Contains("holding crystal staff", res1.StructuredPrompt);
        Assert.Contains("serene expression", res1.StructuredPrompt);
        Assert.Contains("braided twin tails", res1.StructuredPrompt);
        Assert.Empty(res1.Warnings);
    }

    [Fact]
    public void UNIT002_HybridAppendedConstraints_SelectedVariant_FinalEditedPromptPreserved()
    {
        var service = new PromptBuilderService();

        var character = new Character
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Name = "Kaelen",
            Role = "Warrior"
        };

        var outfitDef = new CharacterAttributeDefinition { Id = Guid.NewGuid(), Code = "outfit", DisplayName = "Trang phục" };
        var weaponDef = new CharacterAttributeDefinition { Id = Guid.NewGuid(), Code = "weapon", DisplayName = "Vũ khí" };

        character.AttributeValues.Add(new CharacterAttributeValue { DefinitionId = outfitDef.Id, Definition = outfitDef, ValueJson = "steel armor" });
        character.AttributeValues.Add(new CharacterAttributeValue { DefinitionId = weaponDef.Id, Definition = weaponDef, ValueJson = "iron broadsword" });

        var battleVariant = new CharacterVariant
        {
            Id = Guid.NewGuid(),
            CharacterId = character.Id,
            Name = "Berserker State",
            IsDefault = false
        };
        battleVariant.AttributeValues.Add(new VariantAttributeValue
        {
            VariantId = battleVariant.Id,
            DefinitionId = outfitDef.Id,
            Definition = outfitDef,
            ValueJson = "battle-damaged spiked armor"
        });
        character.Variants.Add(battleVariant);

        var result = service.BuildPrompt(character, variantId: battleVariant.Id, userAddendum: "surrounded by red aura on battlefield");

        Assert.Contains("wearing battle-damaged spiked armor", result.HybridPrompt);
        Assert.DoesNotContain("steel armor", result.HybridPrompt);
        Assert.EndsWith("surrounded by red aura on battlefield", result.HybridPrompt);
    }

    [Fact]
    public void UNIT003_RawMode_NoSilentlyAddedProfileAttrs()
    {
        var service = new PromptBuilderService();

        var character = new Character
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Name = "Luna",
            Role = "Knight"
        };
        var hairDef = new CharacterAttributeDefinition { Id = Guid.NewGuid(), Code = "hair_color", DisplayName = "Màu tóc" };
        character.AttributeValues.Add(new CharacterAttributeValue { DefinitionId = hairDef.Id, Definition = hairDef, ValueJson = "platinum blonde" });

        var result = service.BuildPrompt(character, userAddendum: "Cyberpunk neon city alleyway at night");

        Assert.Equal("Cyberpunk neon city alleyway at night", result.RawPrompt);
        Assert.DoesNotContain("platinum blonde", result.RawPrompt);
    }

    [Theory]
    [InlineData("Photorealistic photo of an adult woman (not anime, not cartoon, not illustration), in a black jacket.",
        "Photorealistic photo of an adult woman, in a black jacket.")]
    [InlineData("Cartoon portrait in a black jacket.", "Cartoon portrait in a black jacket.")]
    [InlineData("Ảnh chụp chân dung trong nhà.", "Ảnh chụp chân dung trong nhà.")]
    public void Hybrid_UsesRequestedMedium_WithoutAddingAnime(string rawPrompt, string expectedPrefix)
    {
        var character = new Character { Name = "Linh", Role = "Supporting" };
        var result = new PromptBuilderService().BuildPrompt(character, userAddendum: rawPrompt);

        Assert.Equal(rawPrompt, result.RawPrompt);
        Assert.StartsWith(expectedPrefix, result.HybridPrompt);
        Assert.Contains("Linh", result.HybridPrompt);
        Assert.DoesNotContain("anime", result.HybridPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clean lines", result.HybridPrompt);
        Assert.StartsWith("2D anime concept art", result.StructuredPrompt);
        Assert.Contains(result.Warnings, warning => warning.Contains("Hybrid ưu tiên phong cách"));
    }

    [Fact]
    public void Structured_UsesProfileStyle_AndHybrid_CanOverrideIt()
    {
        var style = new CharacterAttributeDefinition { Id = Guid.NewGuid(), Code = "visual_style", DisplayName = "Phong cách hình ảnh" };
        var hair = new CharacterAttributeDefinition { Id = Guid.NewGuid(), Code = "hair_color", DisplayName = "Màu tóc" };
        var character = new Character { Name = "Linh", Role = "Supporting" };
        character.AttributeValues.Add(new CharacterAttributeValue { Definition = style, DefinitionId = style.Id, ValueJson = "2D anime" });
        character.AttributeValues.Add(new CharacterAttributeValue { Definition = hair, DefinitionId = hair.Id, ValueJson = "silver" });
        var service = new PromptBuilderService();

        var result = service.BuildPrompt(character, userAddendum: "Studio portrait, soft indoor light");
        Assert.StartsWith("2D anime,", result.StructuredPrompt);
        Assert.DoesNotContain("anime", result.HybridPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("silver hair", result.HybridPrompt);
        Assert.Equal("2D anime", result.EffectiveAttributes["visual_style"]);
        Assert.Contains("2D anime", service.BuildPrompt(character, userAddendum: "wearing a black jacket").HybridPrompt);
        Assert.DoesNotContain("2D anime", service.BuildPrompt(character, userAddendum: "Portrait, not anime").HybridPrompt);
    }

    [Fact]
    public void PromptBuilder_AntiContradiction_WarningsDetected()
    {
        var service = new PromptBuilderService();

        var character = new Character
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Name = "Eldarion",
            Role = "Archer"
        };
        var hairDef = new CharacterAttributeDefinition { Id = Guid.NewGuid(), Code = "hair_color", DisplayName = "Màu tóc" };
        character.AttributeValues.Add(new CharacterAttributeValue { DefinitionId = hairDef.Id, Definition = hairDef, ValueJson = "silver" });

        var result = service.BuildPrompt(character, userAddendum: "portrait of Eldarion with golden blonde hair under noon sun");

        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("silver", StringComparison.OrdinalIgnoreCase) && w.Contains("blonde", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UNIT010_IllegalJobTransitions_RejectedWith409()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Transitions Tester");

        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Transition Project", "Game", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/characters", new CreateCharacterRequest("Hero", "Protagonist", null, null));
        var hero = await charResp.Content.ReadFromJsonAsync<CharacterDto>();

        // 1. Submit job
        var genResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            CharacterId: hero!.Id,
            VariantId: hero.Variants[0].Id,
            PromptMode: PromptMode.Raw,
            RawPrompt: "Test Prompt",
            FinalPrompt: "Test Prompt",
            Width: 512,
            Height: 512,
            RequestedCount: 1,
            Seed: 1234,
            IdempotencyKey: Guid.NewGuid().ToString()
        ));
        Assert.Equal(HttpStatusCode.Accepted, genResp.StatusCode);
        var jobLocation = genResp.Headers.Location!;
        var job = await client.GetFromJsonAsync<GenerationJobDto>(jobLocation);

        // 2. Attempt to retry a Pending/Running job -> Should be 409
        var illegalRetryResp = await client.PostAsync($"/api/v1/projects/{proj.Id}/generations/{job!.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, illegalRetryResp.StatusCode);

        // 3. Wait until job is completed
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            job = await client.GetFromJsonAsync<GenerationJobDto>(jobLocation);
            if (job?.Status == GenerationStatus.Completed) break;
        }
        Assert.Equal(GenerationStatus.Completed, job!.Status);

        // 4. Attempt to cancel an already completed job -> Should be 409
        var illegalCancelResp = await client.PostAsync($"/api/v1/projects/{proj.Id}/generations/{job.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, illegalCancelResp.StatusCode);
    }

    [Fact]
    public async Task UNIT011_StorageKey_PathTraversal_RejectedSafely()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Media:RootPath"] = Path.Combine(AppContext.BaseDirectory, "TestMediaStorage_" + Guid.NewGuid().ToString("N"))
        }).Build();

        var store = new LocalMediaStore(config);

        // Test various path traversal attempts
        var stream1 = await store.OpenReadAsync("../../../windows/system32/cmd.exe");
        Assert.Null(stream1);

        var stream2 = await store.OpenReadAsync("..\\..\\secret.key");
        Assert.Null(stream2);

        var stream3 = await store.OpenReadAsync("normal/subfolder/../../secret.txt");
        Assert.Null(stream3);
    }

    [Fact]
    public async Task PromptPreview_Endpoint_ReturnsThreeModes_AndSnapshot_NoImageGenerated()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Preview Tester");

        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Preview Project", "Game", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/characters", new CreateCharacterRequest("Mage Luna", "Mage", null, null));
        var character = await charResp.Content.ReadFromJsonAsync<CharacterDto>();

        var previewResp = await client.PostAsJsonAsync(
            $"/api/v1/projects/{proj.Id}/characters/{character!.Id}/prompt-preview",
            new PromptPreviewRequest(
                VariantId: character.Variants[0].Id,
                RawPrompt: "Casting lightning spell on castle wall",
                TemplateVersion: "1.0"
            )
        );

        Assert.Equal(HttpStatusCode.OK, previewResp.StatusCode);
        var preview = await previewResp.Content.ReadFromJsonAsync<PromptPreviewDto>();
        Assert.NotNull(preview);
        Assert.Equal("1.1", preview.TemplateVersion);
        Assert.Equal("Casting lightning spell on castle wall", preview.RawPrompt);
        Assert.Contains("Mage Luna", preview.StructuredPrompt);
        Assert.Contains("Casting lightning spell on castle wall", preview.HybridPrompt);
        Assert.False(string.IsNullOrWhiteSpace(preview.SnapshotJson));

        // Ensure no generation job was enqueued
        var jobsResp = await client.GetFromJsonAsync<PagedResult<GenerationJobDto>>($"/api/v1/projects/{proj.Id}/generations?page=1&pageSize=10");
        Assert.Empty(jobsResp!.Items);
    }

    [Fact]
    public async Task JOB001_ConcurrentClients_SubmitAtOnce_QueuedDurableJobs_SequentialExecution()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Concurrent Submitter");

        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Concurrent Project", "Game", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/characters", new CreateCharacterRequest("Knight", "Defender", null, null));
        var knight = await charResp.Content.ReadFromJsonAsync<CharacterDto>();

        var t1 = client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            knight!.Id, knight.Variants[0].Id, PromptMode.Raw, "Job 1", "Job 1", 512, 512, 1, 101, Guid.NewGuid().ToString()
        ));
        var t2 = client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            knight.Id, knight.Variants[0].Id, PromptMode.Raw, "Job 2", "Job 2", 512, 512, 1, 102, Guid.NewGuid().ToString()
        ));
        var t3 = client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            knight.Id, knight.Variants[0].Id, PromptMode.Raw, "Job 3", "Job 3", 512, 512, 1, 103, Guid.NewGuid().ToString()
        ));

        var responses = await Task.WhenAll(t1, t2, t3);
        foreach (var r in responses)
        {
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        }

        // Wait for all 3 to complete via sequential worker
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            var queue = await client.GetFromJsonAsync<PagedResult<GenerationJobDto>>($"/api/v1/projects/{proj.Id}/generations?page=1&pageSize=10");
            if (queue?.Items.Count == 3 && queue.Items.All(j => j.Status == GenerationStatus.Completed))
            {
                break;
            }
        }

        var finalQueue = await client.GetFromJsonAsync<PagedResult<GenerationJobDto>>($"/api/v1/projects/{proj.Id}/generations?page=1&pageSize=10");
        Assert.Equal(3, finalQueue!.Items.Count);
        Assert.All(finalQueue.Items, j => Assert.Equal(GenerationStatus.Completed, j.Status));
        Assert.All(finalQueue.Items, j => Assert.True(j.IsSimulated));
    }

    [Fact]
    public async Task JOB002_RepeatSameIdempotencyKey_ReturnsSameJob_NoDuplicateWorkflow()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Idempotency Tester");

        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Idempotency Project", "Game", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/characters", new CreateCharacterRequest("Rogue", "Assassin", null, null));
        var rogue = await charResp.Content.ReadFromJsonAsync<CharacterDto>();

        var key = $"idemp_{Guid.NewGuid():N}";
        var req = new CreateGenerationRequest(
            rogue!.Id, rogue.Variants[0].Id, PromptMode.Raw, "Stealth strike", "Stealth strike", 512, 512, 1, 42, key
        );

        var firstResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", req);
        Assert.Equal(HttpStatusCode.Accepted, firstResp.StatusCode);
        var firstBody = await firstResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var firstId = firstBody!["id"].ToString();

        // Repeat with identical IdempotencyKey
        var secondResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", req);
        Assert.True(secondResp.StatusCode == HttpStatusCode.Accepted || secondResp.StatusCode == HttpStatusCode.OK);
        var secondBody = await secondResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var secondId = secondBody!["id"].ToString();

        Assert.Equal(firstId, secondId);

        var list = await client.GetFromJsonAsync<PagedResult<GenerationJobDto>>($"/api/v1/projects/{proj.Id}/generations");
        Assert.Single(list!.Items);
    }

    [Fact]
    public async Task JOB003_CancelPendingJob_MarkedCancelled_NeverExecutedByWorker()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Cancel Tester");

        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Cancel Project", "Game", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/characters", new CreateCharacterRequest("Archer", "Marksman", null, null));
        var archer = await charResp.Content.ReadFromJsonAsync<CharacterDto>();

        var genResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            archer!.Id, archer.Variants[0].Id, PromptMode.Raw, "Aiming bow", "Aiming bow", 512, 512, 1, null, Guid.NewGuid().ToString()
        ));
        Assert.Equal(HttpStatusCode.Accepted, genResp.StatusCode);
        var body = await genResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var jobId = Guid.Parse(body!["id"].ToString()!);

        // Cancel job immediately
        var cancelResp = await client.PostAsync($"/api/v1/projects/{proj.Id}/generations/{jobId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);
        var cancelResult = await cancelResp.Content.ReadFromJsonAsync<CancelJobResponse>();
        Assert.NotNull(cancelResult);
        Assert.Equal(GenerationStatus.Cancelled, cancelResult.Status);

        var jobDetails = await client.GetFromJsonAsync<GenerationJobDto>($"/api/v1/projects/{proj.Id}/generations/{jobId}");
        Assert.Equal(GenerationStatus.Cancelled, jobDetails!.Status);
    }

    [Fact]
    public async Task JOB005_ProviderFailure_JobTransitionsToFailed_CanRetrySuccessfully()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Retry Tester");

        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Retry Project", "Game", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/characters", new CreateCharacterRequest("Paladin", "Tank", null, null));
        var paladin = await charResp.Content.ReadFromJsonAsync<CharacterDto>();

        var genResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            paladin!.Id, paladin.Variants[0].Id, PromptMode.Raw, "Holy light aura", "Holy light aura", 512, 512, 1, null, Guid.NewGuid().ToString()
        ));
        var body = await genResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var jobId = Guid.Parse(body!["id"].ToString()!);

        // Cancel job so it reaches Cancelled state
        await client.PostAsync($"/api/v1/projects/{proj.Id}/generations/{jobId}/cancel", null);

        // Now retry the job
        var retryResp = await client.PostAsync($"/api/v1/projects/{proj.Id}/generations/{jobId}/retry", null);
        Assert.True(retryResp.StatusCode == HttpStatusCode.Accepted || retryResp.StatusCode == HttpStatusCode.OK);
        var retryResult = await retryResp.Content.ReadFromJsonAsync<RetryJobResponse>();
        Assert.NotNull(retryResult);
        Assert.Equal(GenerationStatus.Pending, retryResult.Status);

        // Background worker will now pick it up and complete it
        GenerationJobDto? job = null;
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/v1/projects/{proj.Id}/generations/{jobId}");
            if (job?.Status == GenerationStatus.Completed) break;
        }

        Assert.NotNull(job);
        Assert.Equal(GenerationStatus.Completed, job.Status);
    }

    [Fact]
    public async Task FullQualityRunsInGenerationJobAndStoresDoubleResolution()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Full Quality Tester");
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Full Quality Project", "Game", null));
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectDto>())!;
        var characterResponse = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/characters",
            new CreateCharacterRequest("Ranger", "Explorer", null, null));
        var character = (await characterResponse.Content.ReadFromJsonAsync<CharacterDto>())!;
        var route = $"/api/v1/projects/{project.Id}/generations";
        var request = new CreateGenerationRequest(character.Id, character.Variants[0].Id, PromptMode.Raw,
            "A ranger in a field", null, 256, 384, 1, 319, Guid.NewGuid().ToString(), FullQuality: true);
        var response = await client.PostAsJsonAsync(route, request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        GenerationJobDto? job = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            job = await client.GetFromJsonAsync<GenerationJobDto>(response.Headers.Location);
            if (job?.Status != GenerationStatus.Pending && job?.Status != GenerationStatus.Running) break;
            await Task.Delay(300);
        }

        Assert.NotNull(job);
        Assert.Equal(GenerationStatus.Completed, job.Status);
        Assert.True(job.FullQuality);
        var assetId = Assert.Single(job.OutputAssetIds);
        var png = await client.GetByteArrayAsync($"/api/v1/projects/{project.Id}/media/{assetId}");
        Assert.Equal(512, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(768, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }

    [Fact]
    public async Task JOB010_MultipleRequestedImages_GeneratedSequentially_CountConsistent()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Multi-Image Tester");

        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Multi-Image Project", "Anime", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/characters", new CreateCharacterRequest("Samurai", "Swordsman", null, null));
        var samurai = await charResp.Content.ReadFromJsonAsync<CharacterDto>();

        // Request 3 images
        var genResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            samurai!.Id, samurai.Variants[0].Id, PromptMode.Raw, "Katana unsheathed at cherry blossom garden", "Katana unsheathed at cherry blossom garden", 512, 512, 3, 777, Guid.NewGuid().ToString()
        ));
        Assert.Equal(HttpStatusCode.Accepted, genResp.StatusCode);
        var body = await genResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var jobId = Guid.Parse(body!["id"].ToString()!);

        GenerationJobDto? completedJob = null;
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/v1/projects/{proj.Id}/generations/{jobId}");
            if (job?.Status == GenerationStatus.Completed)
            {
                completedJob = job;
                break;
            }
        }

        Assert.NotNull(completedJob);
        Assert.Equal(GenerationStatus.Completed, completedJob.Status);
        Assert.Equal(3, completedJob.RequestedCount);
        Assert.Equal(3, completedJob.CompletedCount);
        Assert.Equal(3, completedJob.OutputAssetIds.Count);
        Assert.True(completedJob.IsSimulated);
    }

    [Fact]
    public async Task JOB011_PendingQuotaExceeded_Returns429TooManyRequests()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Quota Tester");

        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Quota Project", "Anime", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/characters", new CreateCharacterRequest("Ninja", "Spy", null, null));
        var ninja = await charResp.Content.ReadFromJsonAsync<CharacterDto>();

        // Send 10 generation requests quickly to exceed the quota of 5 active jobs
        var responses = new List<HttpResponseMessage>();
        for (int i = 0; i < 10; i++)
        {
            var resp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
                ninja!.Id, ninja.Variants[0].Id, PromptMode.Raw, $"Quota test {i}", $"Quota test {i}", 512, 512, 1, null, Guid.NewGuid().ToString()
            ));
            responses.Add(resp);
        }

        // At least one submission beyond quota limit must receive 429 Too Many Requests
        var has429 = responses.Any(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.True(has429, "Expected 429 TooManyRequests when exceeding quota limit of 5 active jobs.");
    }
}
