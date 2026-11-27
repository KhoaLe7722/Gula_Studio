using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CharacterConceptAI.Server.AI;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Shared.Contracts.Projects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CharacterConceptAI.Tests;

public class StudioProTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;

    public StudioProTests(TestApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void UNIT_PromptEnhancer_StripsBuzzwords_PreservesQuotes_AndAddsZCaptionerDetails()
    {
        var req = new EnhancePromptRequest(
            Prompt: "cô gái uống cà phê với biển \"8K Coffee Shop\", 8K, masterpiece, best quality",
            StylePreset: "poster",
            OverlayText: "CÀ PHÊ SÀI GÒN",
            OverlayTextStyle: "neon",
            OverlayTextPosition: "top",
            Width: 896,
            Height: 1152);

        var res = PromptEnhancerService.EnhanceWithZCaptionerRules(req);

        Assert.Contains("\"8K Coffee Shop\"", res.EnhancedPrompt);
        Assert.Contains("\"CÀ PHÊ SÀI GÒN\"", res.EnhancedPrompt);
        Assert.DoesNotContain("masterpiece", res.EnhancedPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("best quality", res.EnhancedPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("máy ảnh", res.EnhancedPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ống kính", res.EnhancedPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kim loại", res.EnhancedPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("cô gái uống cà phê", res.EnhancedPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("neon", res.EnhancedPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(res.EnhancedPrompt.Length > req.Prompt.Length);
    }

    [Fact]
    public async Task UNIT_ComfyUiClient_Img2Img_InjectsBoundedScaleAndDynamicShift()
    {
        string? capturedPromptJson = null;
        var mockPng = CreateMinimalValidPng(64, 64);

        var handler = new SimpleMockHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/upload/image")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { name = "uploaded_ref.png" }))
                };
            }
            if (req.RequestUri?.AbsolutePath == "/prompt")
            {
                capturedPromptJson = req.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { prompt_id = "i2i-test-id" }))
                };
            }
            if (req.RequestUri?.AbsolutePath == "/history/i2i-test-id")
            {
                var history = new Dictionary<string, object>
                {
                    ["i2i-test-id"] = new
                    {
                        status = new { status_str = "success", completed = true },
                        outputs = new Dictionary<string, object>
                        {
                            ["9"] = new { images = new[] { new { filename = "out.png", subfolder = "", type = "output" } } }
                        }
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(history)) };
            }
            if (req.RequestUri?.AbsolutePath == "/view")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(mockPng) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ComfyUI:BaseUrl"] = "http://127.0.0.1:8188",
            ["ComfyUI:TimeoutSeconds"] = "10"
        }).Build();

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8188") };
        var client = new ComfyUiClient(http, config, NullLogger<ComfyUiClient>.Instance);

        var result = await client.GenerateAsync(new GenerationExecutionRequest(
            JobId: Guid.NewGuid(),
            Prompt: "Cyberpunk restyle",
            Width: 832,
            Height: 1216,
            Seed: 777,
            Denoise: 0.62,
            ReferenceImageBytes: mockPng));

        Assert.True(result.Success);
        Assert.NotNull(capturedPromptJson);
        var graph = JsonNode.Parse(capturedPromptJson)?["prompt"];
        Assert.NotNull(graph);
        Assert.Equal("uploaded_ref.png", graph["10"]?["inputs"]?["image"]?.ToString());
        Assert.Equal(832, graph["14"]?["inputs"]?["width"]?.GetValue<int>());
        Assert.Equal(1216, graph["14"]?["inputs"]?["height"]?.GetValue<int>());
        Assert.Equal(ComfyUiClient.ComputeDynamicShift(832, 1216), graph["11"]?["inputs"]?["shift"]?.GetValue<double>());
        Assert.Equal(0.62, graph["3"]?["inputs"]?["denoise"]?.GetValue<double>() ?? 0, 2);
    }

    [Fact]
    public async Task INTEGRATION_StudioEnhancementEndpoints_EnhanceAndUploadReference_WorkEndToEnd()
    {
        var client = await CreateAuthenticatedClientAsync();

        // 1. Enhance prompt endpoint
        var enhanceResp = await client.PostAsJsonAsync("/api/v1/studio/enhance-prompt",
            new EnhancePromptRequest("nữ kiếm sĩ dưới mưa", "anime", "KIẾM SĨ", "gold3d", "top", 832, 1216));
        Assert.Equal(HttpStatusCode.OK, enhanceResp.StatusCode);
        var enhanced = await enhanceResp.Content.ReadFromJsonAsync<EnhancePromptResponse>();
        Assert.NotNull(enhanced);
        Assert.Contains("\"KIẾM SĨ\"", enhanced.EnhancedPrompt);

        // 2. Upload reference image for a project & character
        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("StudioPro Test", "Concept", "Test"));
        projResp.EnsureSuccessStatusCode();
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(proj);

        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters",
            new CreateCharacterRequest("Heroine", "Protagonist", "Test"));
        charResp.EnsureSuccessStatusCode();
        var character = await charResp.Content.ReadFromJsonAsync<CharacterDto>();
        Assert.NotNull(character);

        var pngBytes = CreateMinimalValidPng(128, 128);
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(pngBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(fileContent, "image", "sample.png");

        var uploadResp = await client.PostAsync($"/api/v1/projects/{proj.Id}/studio/upload-reference?characterId={character.Id}", form);
        Assert.Equal(HttpStatusCode.OK, uploadResp.StatusCode);
        var uploaded = await uploadResp.Content.ReadFromJsonAsync<UploadReferenceResponse>();
        Assert.NotNull(uploaded);
        Assert.NotEqual(Guid.Empty, uploaded.AssetId);
        Assert.NotEqual(Guid.Empty, uploaded.ConceptVersionId);
        Assert.Equal(128, uploaded.Width);
        Assert.Equal(128, uploaded.Height);

        // 3. Submit Img2Img generation referencing the uploaded ConceptVersionId
        var genResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            CharacterId: character.Id,
            VariantId: null,
            PromptMode: PromptMode.Raw,
            RawPrompt: "Cyberpunk lighting",
            FinalPrompt: null,
            Width: 512,
            Height: 512,
            RequestedCount: 2,
            Seed: 100,
            IdempotencyKey: null,
            ReferenceConceptVersionId: uploaded.ConceptVersionId,
            Denoise: 0.65));
        Assert.Equal(HttpStatusCode.Accepted, genResp.StatusCode);
    }

    [Fact]
    public async Task UNIT_ComfyUiClient_DepthPoseAndFaceIdentity_InjectsUnion21AndFaceSwapNodes()
    {
        string? capturedPromptJson = null;
        var uploadedFiles = new List<string>();
        var mockPng = CreateMinimalValidPng(64, 64);

        var handler = new SimpleMockHandler(req =>
        {
            if (req.RequestUri?.AbsolutePath == "/upload/image")
            {
                var idx = uploadedFiles.Count + 1;
                var name = $"uploaded_{idx}.png";
                uploadedFiles.Add(name);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { name }))
                };
            }
            if (req.RequestUri?.AbsolutePath == "/prompt")
            {
                capturedPromptJson = req.Content?.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { prompt_id = "depth-face-id" }))
                };
            }
            if (req.RequestUri?.AbsolutePath == "/history/depth-face-id")
            {
                var history = new Dictionary<string, object>
                {
                    ["depth-face-id"] = new
                    {
                        status = new { status_str = "success", completed = true },
                        outputs = new Dictionary<string, object>
                        {
                            ["81"] = new { cca_face_identity_status = new[] { "swapped" } },
                            ["9"] = new { images = new[] { new { filename = "out.png", subfolder = "", type = "output" } } }
                        }
                    }
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(history)) };
            }
            if (req.RequestUri?.AbsolutePath == "/view")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(mockPng) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ComfyUI:BaseUrl"] = "http://127.0.0.1:8188",
            ["ComfyUI:TimeoutSeconds"] = "10"
        }).Build();

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8188") };
        var client = new ComfyUiClient(http, config, NullLogger<ComfyUiClient>.Instance);

        var result = await client.GenerateAsync(new GenerationExecutionRequest(
            JobId: Guid.NewGuid(),
            Prompt: "Story scene with locked multi-angle face and depth pose",
            Width: 896,
            Height: 1152,
            Seed: 4242,
            DepthPoseImageBytes: mockPng,
            DepthStrength: 0.85,
            FaceReferenceImageBytes: mockPng,
            FaceSwapStrength: 0.92,
            FaceRestoreStrength: 0.78,
            AdditionalFaceReferenceImageBytes: [mockPng, mockPng]));

        Assert.True(result.Success);
        Assert.Equal(4, uploadedFiles.Count);
        Assert.NotNull(capturedPromptJson);

        var graph = JsonNode.Parse(capturedPromptJson)?["prompt"];
        Assert.NotNull(graph);
        Assert.Equal("uploaded_1.png", graph["50"]?["inputs"]?["image"]?.ToString());
        Assert.Equal("Z-Image-Turbo-Fun-Controlnet-Union-2.1-lite-2602-8steps.safetensors", graph["42"]?["inputs"]?["name"]?.ToString());
        Assert.Equal("CcaDepthLetterboxResize", graph["51"]?["class_type"]?.ToString());
        Assert.Equal(896, graph["51"]?["inputs"]?["width"]?.GetValue<int>());
        Assert.Equal(1152, graph["51"]?["inputs"]?["height"]?.GetValue<int>());
        Assert.Equal("CcaZImageFunControlnetScheduled", graph["43"]?["class_type"]?.ToString());
        Assert.Equal(0.85, graph["43"]?["inputs"]?["strength"]?.GetValue<double>() ?? 0, 2);
        Assert.InRange(graph["43"]?["inputs"]?["end_percent"]?.GetValue<double>() ?? 1.0, 0.64, 0.74);
        Assert.Equal("uploaded_2.png", graph["80"]?["inputs"]?["image"]?.ToString());
        Assert.Equal("uploaded_3.png", graph["82"]?["inputs"]?["image"]?.ToString());
        Assert.Equal("uploaded_4.png", graph["83"]?["inputs"]?["image"]?.ToString());
        Assert.Equal("CcaFaceIdentitySwap", graph["81"]?["class_type"]?.ToString());
        Assert.Equal("82", graph["81"]?["inputs"]?["source_face_2"]?[0]?.ToString());
        Assert.Equal("83", graph["81"]?["inputs"]?["source_face_3"]?[0]?.ToString());
        Assert.Equal(0.65, graph["81"]?["inputs"]?["yaw_weight_strength"]?.GetValue<double>() ?? 0, 2);
        Assert.Equal(0.92, graph["81"]?["inputs"]?["strength"]?.GetValue<double>() ?? 0, 2);
        Assert.Equal(0.78, graph["81"]?["inputs"]?["restore_strength"]?.GetValue<double>() ?? 0, 2);
        Assert.Equal("81", graph["9"]?["inputs"]?["images"]?[0]?.ToString());
    }

    [Fact]
    public async Task INTEGRATION_PoseGalleryAndStoryGeneration_EndToEnd()
    {
        var client = await CreateAuthenticatedClientAsync();

        // 1. Pose Gallery endpoint (SFW default + NSFW unlock + path traversal defense)
        var sfwResp = await client.GetAsync("/api/v1/studio/pose-gallery?page=1&pageSize=12");
        Assert.Equal(HttpStatusCode.OK, sfwResp.StatusCode);
        var sfw = await sfwResp.Content.ReadFromJsonAsync<PoseGalleryResponse>();
        Assert.NotNull(sfw);
        Assert.False(sfw.IncludeNsfw);
        Assert.All(sfw.Groups, g => Assert.False(g.IsNsfw));

        if (sfw.IsAvailable && sfw.Groups.Count > 0)
        {
            Assert.True(sfw.TotalPoses > 500);
            // Verify top-to-bottom quality ranking (highest reliability poses appear first)
            for (var i = 1; i < sfw.Groups.Count; i++)
                Assert.True(sfw.Groups[i - 1].QualityScore >= sfw.Groups[i].QualityScore);
            Assert.True(sfw.Groups[0].QualityScore >= 92);

            var nsfwResp = await client.GetFromJsonAsync<PoseGalleryResponse>("/api/v1/studio/pose-gallery?includeNsfw=true&page=1&pageSize=12");
            Assert.NotNull(nsfwResp);
            Assert.True(nsfwResp.TotalPoses > sfw.TotalPoses);
            // Couple poses are inherently hardest and must rank last in systems
            Assert.Equal("couple", nsfwResp.Systems.Last().Key);

            var firstPoseId = sfw.Groups[0].Angles[0].Id;
            var imgResp = await client.GetAsync($"/api/v1/studio/pose-gallery/image/{Uri.EscapeDataString(firstPoseId)}?kind=thumb&mode=mannequin");
            Assert.Equal(HttpStatusCode.OK, imgResp.StatusCode);
            var depthResp = await client.GetAsync($"/api/v1/studio/pose-gallery/image/{Uri.EscapeDataString(firstPoseId)}?kind=full&mode=depth");
            Assert.Equal(HttpStatusCode.OK, depthResp.StatusCode);
        }

        var traversalResp = await client.GetAsync("/api/v1/studio/pose-gallery/image/..%2F..%2FREADME.md?kind=full&mode=mannequin");
        Assert.Equal(HttpStatusCode.NotFound, traversalResp.StatusCode);

        // 2. Story Generation with DepthPoseId + FaceReferenceConceptVersionId persists snapshot metadata cleanly
        var proj = await (await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Story Test", "Anime", "Test"))).Content.ReadFromJsonAsync<ProjectDto>();
        Assert.NotNull(proj);
        var character = await (await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters", new CreateCharacterRequest("Linh", "Protagonist", "DNA"))).Content.ReadFromJsonAsync<CharacterDto>();
        Assert.NotNull(character);

        var pngBytes = CreateMinimalValidPng(128, 128);
        async Task<UploadReferenceResponse> UploadFaceAsync(string name)
        {
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(pngBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(fileContent, "image", name);
            var uploaded = await (await client.PostAsync($"/api/v1/projects/{proj.Id}/studio/upload-reference?characterId={character.Id}", form)).Content.ReadFromJsonAsync<UploadReferenceResponse>();
            Assert.NotNull(uploaded);
            return uploaded;
        }
        var faceRef = await UploadFaceAsync("face_front.png");
        var faceLeft = await UploadFaceAsync("face_left34.png");
        var faceRight = await UploadFaceAsync("face_right34.png");

        var genResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/generations", new CreateGenerationRequest(
            CharacterId: character.Id,
            VariantId: null,
            PromptMode: PromptMode.Raw,
            RawPrompt: "A photorealistic close-up in Tokyo rain",
            FinalPrompt: null,
            Width: 896,
            Height: 1152,
            RequestedCount: 1,
            Seed: 12345,
            IdempotencyKey: null,
            DepthPoseId: "full_single_P001_front",
            DepthStrength: 0.92,
            FaceReferenceConceptVersionId: faceRef.ConceptVersionId,
            FaceSwapStrength: 0.90,
            FaceRestoreStrength: 0.75,
            AdditionalFaceReferenceConceptVersionIds: [faceLeft.ConceptVersionId, faceRight.ConceptVersionId]));
        Assert.Equal(HttpStatusCode.Accepted, genResp.StatusCode);

        var accepted = await genResp.Content.ReadFromJsonAsync<JsonObject>();
        var jobId = Guid.Parse(accepted!["id"]!.ToString());
        var job = await client.GetFromJsonAsync<GenerationJobDto>($"/api/v1/projects/{proj.Id}/generations/{jobId}");
        Assert.NotNull(job);
        Assert.Equal("depth_pose_union21", job.WorkflowKey);
        Assert.Equal("full_single_P001_front", job.DepthPoseId);
        if (sfw.IsAvailable)
        {
            Assert.Contains("full-body shot", job.FinalPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("close-up", job.FinalPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("pose:", job.FinalPrompt, StringComparison.OrdinalIgnoreCase);

            // Verify posture conflict healing on sitting, special yoga bridge, and couple poses
            var gallerySvc = new DepthPoseGalleryService(new ConfigurationBuilder().Build(), NullLogger<DepthPoseGalleryService>.Instance);
            var healedSit = gallerySvc.BuildSynchronizedPrompt("full_single_P005_front", "A woman standing in a sunlit studio");
            Assert.Contains("sitting pose", healedSit, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("standing", healedSit, StringComparison.OrdinalIgnoreCase);

            var healedBridge = gallerySvc.BuildSynchronizedPrompt("full_sexy_P066_front", "A woman in sportswear");
            Assert.Contains("yoga", healedBridge, StringComparison.OrdinalIgnoreCase);

            var healedCouple = gallerySvc.BuildSynchronizedPrompt("pair60_P009_front", "A young couple");
            Assert.Contains("two distinct people", healedCouple, StringComparison.OrdinalIgnoreCase);

            var healedP072 = gallerySvc.BuildSynchronizedPrompt("upper80v2_P072_front", "A young woman indoors");
            Assert.Contains("both hands firmly holding and pressing inward", healedP072, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(faceRef.ConceptVersionId, job.FaceReferenceConceptVersionId);
        Assert.NotNull(job.AdditionalFaceReferenceConceptVersionIds);
        Assert.Equal(2, job.AdditionalFaceReferenceConceptVersionIds.Count);
        Assert.Equal(faceLeft.ConceptVersionId, job.AdditionalFaceReferenceConceptVersionIds[0]);
        Assert.Equal(faceRight.ConceptVersionId, job.AdditionalFaceReferenceConceptVersionIds[1]);
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

    private static byte[] CreateMinimalValidPng(int width, int height)
    {
        // Minimal valid PNG header matching LocalMediaStore IHDR validation
        var bytes = new byte[33];
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        bytes[4] = 0x0D; bytes[5] = 0x0A; bytes[6] = 0x1A; bytes[7] = 0x0A;
        bytes[11] = 13;
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        bytes[16] = (byte)(width >> 24); bytes[17] = (byte)(width >> 16); bytes[18] = (byte)(width >> 8); bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24); bytes[21] = (byte)(height >> 16); bytes[22] = (byte)(height >> 8); bytes[23] = (byte)height;
        bytes[24] = 8; bytes[25] = 6;
        return bytes;
    }

    private sealed class SimpleMockHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(fn(request));
    }
}
