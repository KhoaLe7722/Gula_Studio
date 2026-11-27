using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CharacterConceptAI.Server.AI;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Server.Storage;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Common;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Shared.Contracts.Projects;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CharacterConceptAI.Tests;

public class SecurityRegressionTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;
    public SecurityRegressionTests(TestApplicationFactory factory) => _factory = factory;

    private static async Task<BearerTokenResponse> LoginAsync(HttpClient client, string email, string password)
    {
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = await login.Content.ReadFromJsonAsync<BearerTokenResponse>();
        Assert.NotNull(tokens);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return tokens;
    }

    private async Task<ApplicationUser> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"regression_{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Regression", IsActive = true };
        Assert.True((await manager.CreateAsync(user, "Regression@2026")).Succeeded);
        return user;
    }

    [Fact]
    public async Task SystemAdmin_WithoutProjectMembership_CannotReadPrivateProject()
    {
        var user = await CreateUserAsync();
        var owner = _factory.CreateClient();
        await LoginAsync(owner, user.Email!, "Regression@2026");
        var created = await owner.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Private project", "Game", null));
        var project = (await created.Content.ReadFromJsonAsync<ProjectDto>())!;
        var admin = _factory.CreateClient();
        await LoginAsync(admin, "admin@conceptai.local", "Admin@Secured2026");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/projects/{project.Id}")).StatusCode);
        var list = await admin.GetFromJsonAsync<PagedResult<ProjectDto>>("/api/v1/projects?pageSize=100");
        Assert.DoesNotContain(list!.Items, p => p.Id == project.Id);
    }

    [Fact]
    public async Task DisabledAccount_RejectsExistingAccessAndRefresh_AfterReenable()
    {
        var user = await CreateUserAsync();
        var client = _factory.CreateClient();
        var old = await LoginAsync(client, user.Email!, "Regression@2026");
        var admin = _factory.CreateClient();
        await LoginAsync(admin, "admin@conceptai.local", "Admin@Secured2026");
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/v1/admin/accounts/{user.Id}/status", new UpdateAccountStatusRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/v1/admin/accounts/{user.Id}/status", new UpdateAccountStatusRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/accounts")).StatusCode);
        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshTokenRequest(old.RefreshToken));
        Assert.False(refresh.IsSuccessStatusCode);
    }

    [Fact]
    public async Task HybridFinalPrompt_ExactlyEqualToRaw_RemainsUsersEditedText()
    {
        var client = _factory.CreateClient();
        await LoginAsync(client, "admin@conceptai.local", "Admin@Secured2026");
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Prompt provenance", "Game", null));
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectDto>())!;
        var characterResponse = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/characters", new CreateCharacterRequest("Mei Lin", "Knight", null, null));
        var character = (await characterResponse.Content.ReadFromJsonAsync<CharacterDto>())!;
        const string edited = "An intentionally exact prompt";
        var response = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/generations", new CreateGenerationRequest(
            character.Id, null, PromptMode.Hybrid, edited, edited, 512, 512, 1, 12, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = await client.GetFromJsonAsync<GenerationJobDto>(response.Headers.Location!);
        Assert.Equal(edited, job!.FinalPrompt);
        var preview = new PromptBuilderService().BuildPrompt(new Character { Name = "Mei Lin", Role = "Knight" }, userAddendum: "in a garden");
        Assert.DoesNotContain("East Asian woman", preview.HybridPrompt);
        Assert.DoesNotContain("glasses", preview.HybridPrompt);
    }

    [Fact]
    public async Task UnqualifiedRealImg2Img_IsUnavailableAndCannotBeEnqueued()
    {
        await using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IImageGenerationClient>(new UnqualifiedProvider())));
        var client = factory.CreateClient();
        await LoginAsync(client, "admin@conceptai.local", "Admin@Secured2026");
        var capability = await client.GetFromJsonAsync<GenerationCapabilitiesDto>("/api/v1/generation-capabilities");
        Assert.False(capability!.Img2ImgAvailable);
        Assert.Equal("REAL_AI_UNVERIFIED", capability.Img2ImgStatus);
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Gate", "Game", null));
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectDto>())!;
        var characterResponse = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/characters", new CreateCharacterRequest("Hero", "Knight", null, null));
        var character = (await characterResponse.Content.ReadFromJsonAsync<CharacterDto>())!;
        var request = new CreateGenerationRequest(character.Id, null, PromptMode.Raw, "Test", null, 512, 512, 1, null, null, ReferenceConceptVersionId: Guid.NewGuid());
        var response = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/generations", request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("IMG2IMG_UNQUALIFIED", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MediaStore_RejectsRootedKeysUnknownFormatsAndOversizedBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "CharacterConceptAI_Tests", Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Media:RootPath"] = root,
            ["Media:MaxImageBytes"] = "32"
        }).Build();
        var store = new LocalMediaStore(config);
        Assert.Null(await store.OpenReadAsync("C:/Windows/win.ini"));
        Assert.Null(await store.OpenReadAsync("/etc/passwd"));
        Assert.Throws<ArgumentException>(() => store.GetFullPath("../secret"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Guid.NewGuid(), new MemoryStream(new byte[40]), "png"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Guid.NewGuid(), new MemoryStream(new byte[24]), "png"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Guid.NewGuid(), new MemoryStream(new byte[24]), "../../txt"));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task MediaStreaming_SupportsETagNotModified_WhilePreservingSecurityHeaders()
    {
        var client = _factory.CreateClient();
        await LoginAsync(client, "admin@conceptai.local", "Admin@Secured2026");

        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("ETag Project", "Anime", null));
        var project = (await projectResponse.Content.ReadFromJsonAsync<ProjectDto>())!;
        var charResponse = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/characters", new CreateCharacterRequest("Hana", "Mage", null, null));
        var character = (await charResponse.Content.ReadFromJsonAsync<CharacterDto>())!;

        var genResponse = await client.PostAsJsonAsync($"/api/v1/projects/{project.Id}/generations", new CreateGenerationRequest(
            character.Id, null, PromptMode.Raw, "Anime mage portrait", null, 512, 512, 1, 42, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Accepted, genResponse.StatusCode);

        GenerationJobDto? completed = null;
        for (var i = 0; i < 40; i++)
        {
            var job = await client.GetFromJsonAsync<GenerationJobDto>(genResponse.Headers.Location!);
            if (job?.Status == GenerationStatus.Completed && job.OutputAssetIds.Count > 0)
            {
                completed = job;
                break;
            }
            await Task.Delay(150);
        }
        Assert.NotNull(completed);
        var assetId = completed!.OutputAssetIds[0];
        var mediaUrl = $"/api/v1/projects/{project.Id}/media/{assetId}";

        using var firstResp = await client.GetAsync(mediaUrl);
        Assert.Equal(HttpStatusCode.OK, firstResp.StatusCode);
        Assert.NotNull(firstResp.Headers.ETag);
        Assert.Contains("no-store", firstResp.Headers.CacheControl?.ToString() ?? string.Empty);

        using var conditionalReq = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
        conditionalReq.Headers.TryAddWithoutValidation("If-None-Match", firstResp.Headers.ETag!.ToString());
        using var secondResp = await client.SendAsync(conditionalReq);
        Assert.Equal(HttpStatusCode.NotModified, secondResp.StatusCode);
        Assert.Contains("no-store", secondResp.Headers.CacheControl?.ToString() ?? string.Empty);

        // Unauthorized client must still be rejected even with a valid ETag
        var anonClient = _factory.CreateClient();
        using var anonReq = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
        anonReq.Headers.TryAddWithoutValidation("If-None-Match", firstResp.Headers.ETag!.ToString());
        using var anonResp = await anonClient.SendAsync(anonReq);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);
    }

    private sealed class UnqualifiedProvider : IImageGenerationClient
    {
        public bool IsSimulated => false;
        public Task<GenerationExecutionResult> GenerateAsync(GenerationExecutionRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("The unqualified workflow must never reach a real provider.");
    }
}
