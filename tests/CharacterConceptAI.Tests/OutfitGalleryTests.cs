using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CharacterConceptAI.Server.AI;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Shared.Contracts.Projects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CharacterConceptAI.Tests;

public class OutfitGalleryTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;

    public OutfitGalleryTests(TestApplicationFactory factory)
    {
        _factory = factory;
    }

    private static OutfitGalleryService CreateService()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("GULA_POSE_GALLERY_ROOT"),
            Path.Combine(Environment.GetEnvironmentVariable("GULA_STUDIO_HOME") ?? string.Empty, "NEWTOY", "ai-pose-gallery-zh"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "NEWTOY", "ai-pose-gallery-zh"))
        };
        var root = candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) ?? string.Empty;
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PoseGallery:RootPath"] = root
            })
            .Build();
        return new OutfitGalleryService(cfg, NullLogger<OutfitGalleryService>.Instance);
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var admin = _factory.CreateClient();
        var loginAdmin = await admin.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("admin@conceptai.local", "Admin@Secured2026"));
        loginAdmin.EnsureSuccessStatusCode();
        var token = await loginAdmin.Content.ReadFromJsonAsync<BearerTokenResponse>();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return admin;
    }

    [Fact]
    public void UNIT_OutfitGallery_LoadsMaleUnisexFemaleCatalog_RanksAccurateFirstAndErrorProneLast()
    {
        var svc = CreateService();
        if (!svc.IsAvailable) return; // Skip gracefully if local dataset absent on CI

        var sfw = svc.Query(includeNsfw: false, gender: null, collection: null, category: null, query: null, page: 1, pageSize: 120);
        Assert.True(sfw.IsAvailable);
        Assert.True(sfw.TotalOutfits >= 1400);
        Assert.All(sfw.Items, it => Assert.False(it.IsNsfw));
        Assert.NotNull(sfw.Genders);

        // Verify substantial Male & Unisex catalog (CC0 Shisei + MIT Men's Lookbook)
        var maleOnly = svc.Query(includeNsfw: false, gender: "male", collection: null, category: null, query: null, page: 1, pageSize: 60);
        Assert.True(maleOnly.FilteredOutfits >= 800, $"Expected >=800 male SFW outfits, got {maleOnly.FilteredOutfits}");
        Assert.All(maleOnly.Items, it => Assert.Equal("male", it.GenderKey));
        Assert.True(svc.OutfitExists("MEN-0001"));
        Assert.True(svc.TryResolveImageFile("MEN-0001", "thumb", "front", out var menPath, out var menMime));
        Assert.Equal("image/webp", menMime);
        Assert.True(File.Exists(menPath));

        var unisexOnly = svc.Query(includeNsfw: false, gender: "unisex", collection: null, category: null, query: null, page: 1, pageSize: 60);
        Assert.True(unisexOnly.FilteredOutfits >= 65, $"Expected >=65 unisex SFW outfits, got {unisexOnly.FilteredOutfits}");
        Assert.All(unisexOnly.Items, it => Assert.Equal("unisex", it.GenderKey));

        // Verify strict descending quality order: top-ranked items first, error-prone/incomplete pushed to the end
        for (var i = 1; i < sfw.Items.Count; i++)
            Assert.True(sfw.Items[i - 1].QualityScore >= sfw.Items[i].QualityScore);
        Assert.True(sfw.Items[0].QualityScore >= 95);
        Assert.Equal("★ Chuẩn nhất", sfw.Items[0].QualityBadge);

        // Empirically verified winners (Men's 3-Piece Suit C195, Tang C012, Autumn AUT-0001) must score >= 95
        var c195 = Assert.Single(svc.Query(false, "male", null, null, "shisei-C195", 1, 12).Items, it => it.Id == "shisei-C195");
        var c012 = Assert.Single(svc.Query(false, null, null, null, "shisei-C012", 1, 12).Items, it => it.Id == "shisei-C012");
        var aut1 = Assert.Single(svc.Query(false, null, null, null, "AUT-0001", 1, 12).Items, it => it.Id == "AUT-0001");
        Assert.True(c195.QualityScore >= 95);
        Assert.True(c012.QualityScore >= 95);
        Assert.True(aut1.QualityScore >= 95);

        // Empirically verified double-color collision (WIN-0043 silver+grey bleed) must rank below clean outfits
        var win43 = Assert.Single(svc.Query(false, null, null, null, "WIN-0043", 1, 12).Items, it => it.Id == "WIN-0043");
        Assert.True(win43.QualityScore < aut1.QualityScore);

        // Empirically verified incomplete metadata (PTY-0003 missing dress) must sink to the bottom tier (<70)
        var pty3 = Assert.Single(svc.Query(false, null, null, null, "PTY-0003", 1, 12).Items, it => it.Id == "PTY-0003");
        Assert.True(pty3.QualityScore < 70);

        // NSFW toggle unlocks lingerie & sexy categories
        var nsfw = svc.Query(includeNsfw: true, gender: null, collection: null, category: null, query: null, page: 1, pageSize: 24);
        Assert.True(nsfw.TotalOutfits > sfw.TotalOutfits);
    }

    [Fact]
    public void UNIT_OutfitGallery_PromptSync_HealsConflictingClothesWithoutLosingCharacterDna()
    {
        var svc = CreateService();
        if (!svc.IsAvailable) return;

        var dnaPrompt = "cô gái Việt Nam 22 tuổi tóc đen dài ngang vai, mắt nâu to trong trẻo, mặc áo khoác cardigan màu kem bên ngoài váy sơ mi trắng cổ điển, đứng ở quán cà phê nắng sáng";
        var synced = svc.BuildSynchronizedPrompt("AUT-0001", dnaPrompt);

        Assert.Contains("tóc đen dài ngang vai", synced);
        Assert.Contains("mắt nâu to trong trẻo", synced);
        Assert.Contains("quán cà phê nắng sáng", synced);
        Assert.DoesNotContain("cardigan màu kem", synced);
        Assert.Contains("burgundy blouse", synced, StringComparison.OrdinalIgnoreCase);

        // Empty prompt with male outfit defaults to a stylish young man
        var maleEmpty = svc.BuildSynchronizedPrompt("shisei-C195", "");
        Assert.Contains("young man", maleEmpty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UNIT_OutfitGallery_ResolveImageFile_BlocksPathTraversalAndSupportsShiseiViews()
    {
        var svc = CreateService();
        if (!svc.IsAvailable) return;

        Assert.False(svc.TryResolveImageFile("../../appsettings.json", "thumb", "front", out _, out _));
        Assert.True(svc.TryResolveImageFile("AUT-0001", "thumb", "front", out var modernPath, out var mime1));
        Assert.Equal("image/webp", mime1);
        Assert.True(File.Exists(modernPath));

        Assert.True(svc.TryResolveImageFile("shisei-C195", "thumb", "back", out var backPath, out var mime2));
        Assert.Equal("image/webp", mime2);
        Assert.Contains("_back.webp", backPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task INTEGRATION_OutfitGallery_EndpointsAndGenderFilterWorkEndToEnd()
    {
        var client = await CreateAuthenticatedClientAsync();

        var resp = await client.GetAsync("/api/v1/studio/outfit-gallery?includeNsfw=false&gender=male&page=1&pageSize=12");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var data = await resp.Content.ReadFromJsonAsync<OutfitGalleryResponse>();
        Assert.NotNull(data);
        if (data!.IsAvailable && data.Items.Count > 0)
        {
            Assert.All(data.Items, it => Assert.Equal("male", it.GenderKey));
            var imgResp = await client.GetAsync(data.Items[0].ThumbUrl);
            Assert.Equal(HttpStatusCode.OK, imgResp.StatusCode);
            Assert.Equal("nosniff", imgResp.Headers.GetValues("X-Content-Type-Options").FirstOrDefault());

            var traversalResp = await client.GetAsync("/api/v1/studio/outfit-gallery/image/..%2F..%2FREADME.md?kind=thumb");
            Assert.Equal(HttpStatusCode.NotFound, traversalResp.StatusCode);

            // End-to-end generation with Men's Suit OutfitId injects exact outfit prompt and heals conflicting clothes
            var proj = await (await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Outfit Male Test", "Anime", "Test"))).Content.ReadFromJsonAsync<ProjectDto>();
            Assert.NotNull(proj);
            var character = await (await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/characters", new CreateCharacterRequest("Minh", "Protagonist", "DNA"))).Content.ReadFromJsonAsync<CharacterDto>();
            Assert.NotNull(character);

            var genResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
                CharacterId: character!.Id,
                VariantId: null,
                PromptMode: PromptMode.Raw,
                RawPrompt: "chàng trai trẻ tóc đen gọn gàng, mặc áo thun trắng, đứng giữa đại lộ buổi tối",
                FinalPrompt: null,
                Width: 896,
                Height: 1152,
                RequestedCount: 1,
                Seed: 42,
                IdempotencyKey: null,
                OutfitId: "shisei-C195"));
            Assert.Equal(HttpStatusCode.Accepted, genResp.StatusCode);

            var accepted = await genResp.Content.ReadFromJsonAsync<JsonObject>();
            var jobId = Guid.Parse(accepted!["id"]!.ToString());
            var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/v1/projects/{proj.Id}/generations/{jobId}");
            Assert.NotNull(job);
            Assert.Contains("suit", job!.FinalPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("áo thun trắng", job.FinalPrompt, StringComparison.OrdinalIgnoreCase);
        }
    }
}
