using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Common;
using CharacterConceptAI.Shared.Contracts.Concepts;
using CharacterConceptAI.Shared.Contracts.Generations;
using CharacterConceptAI.Shared.Contracts.Projects;
using CharacterConceptAI.Shared.Contracts.Scripts;

namespace CharacterConceptAI.Desktop.Smoke;

/// <summary>In-memory fixture only. No request ever reaches a socket, SQL, or ComfyUI.</summary>
internal sealed class SimulatedApiHandler : HttpMessageHandler
{
    internal static readonly Guid ProjectId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    internal static readonly Guid CharacterId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid ScriptId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid SceneId = Guid.Parse("80000000-0000-0000-0000-000000000001");
    internal static readonly Guid OffPageCharacterId = Guid.Parse("20000000-0000-0000-0000-000000000099");
    private static readonly DateTimeOffset FixtureDate = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    internal List<string> Requests { get; } = [];
    internal List<string> UnhandledRequests { get; } = [];
    private static readonly Guid UpscaleJobId = Guid.Parse("70000000-0000-0000-0000-000000000002");
    private static readonly Guid UpscaleAssetId = Guid.Parse("70000000-0000-0000-0000-000000000003");
    internal bool FullQualityRequested { get; private set; }
    internal bool Offline { get; set; }
    internal bool Forbidden { get; set; }
    private IReadOnlyList<Guid> _sceneCharacterIds = [CharacterId, OffPageCharacterId];
    internal IReadOnlyList<Guid>? SubmittedSceneCharacterIds { get; private set; }
    internal SceneDto Scene => new(SceneId, ScriptId, 0, "Cuộc gặp gỡ dưới bầu trời sao",
        "SIMULATED · Sân ga cũ, đêm muộn", "SIMULATED · Linh Nguyệt mở tấm bản đồ và bắt đầu câu chuyện.",
        _sceneCharacterIds, FixtureDate, FixtureDate, "AAAAAAAABAA=");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal ProjectDto[] Projects { get; } =
    [
        new(ProjectId, "Biên niên sử Ánh Trăng", "Game", "SIMULATED · Thế giới huyền ảo, nhân vật và những câu chuyện đang hình thành.", "Active", "smoke-user", FixtureDate, FixtureDate.AddHours(3), "AAAAAAAABAA="),
        new(Guid.Parse("10000000-0000-0000-0000-000000000002"), "Thành phố sau cơn mưa", "Manga", "SIMULATED · Một hành trình trưởng thành trong thành phố ven biển.", "Active", "smoke-user", FixtureDate, FixtureDate.AddHours(2), "AAAAAAAABAE="),
        new(Guid.Parse("10000000-0000-0000-0000-000000000003"), "Người giữ rừng", "Anime", "SIMULATED · Nghiên cứu tạo hình cho nhóm nhân vật chính.", "Active", "smoke-user", FixtureDate, FixtureDate.AddHours(1), "AAAAAAAABAI="),
        new(Guid.Parse("10000000-0000-0000-0000-000000000004"), "Những ngày cũ", "Novel", "SIMULATED · Tài liệu tham khảo đã được lưu trữ.", "Archived", "smoke-user", FixtureDate, FixtureDate, "AAAAAAAABAM=")
    ];

    internal CreateCharacterRequest? CreatedCharacterRequest { get; private set; }
    internal bool RejectCharacterCreate { get; set; }
    private CharacterDto? _createdCharacter;

    internal CharacterDto[] Characters { get; } =
    [
        new(CharacterId, ProjectId, "Linh Nguyệt", "Protagonist", "SIMULATED · Người vẽ bản đồ sao, luôn mang theo cuốn sổ nhỏ và những điều chưa nói.", "Active",
            [new(Guid.Parse("40000000-0000-0000-0000-000000000001"), "hair_color", "Màu tóc", "Text", "\"Bạc ánh tím\"", true),
             new(Guid.Parse("40000000-0000-0000-0000-000000000002"), "age", "Tuổi", "Number", "24")],
            [new(Guid.Parse("50000000-0000-0000-0000-000000000001"), CharacterId, "Trang phục thám hiểm", "SIMULATED · Bản mặc định", true, "Active", "AAAAAAAACAA=")], "AAAAAAAADAA="),
        new(Guid.Parse("20000000-0000-0000-0000-000000000002"), ProjectId, "An Phong", "Supporting", "SIMULATED · Người bạn đồng hành trên chuyến tàu đến vùng đất phía Bắc.", "Active", [], [], "AAAAAAAADAE="),
        new(Guid.Parse("20000000-0000-0000-0000-000000000003"), ProjectId, "Hạ Vũ", "Antagonist", "SIMULATED · Người bảo vệ cánh cổng cổ với một ký ức bị lãng quên.", "Draft", [], [], "AAAAAAAADAI=")
    ];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add($"{request.Method} {path}");
        if (Offline) throw new HttpRequestException("SIMULATED offline fixture.");
        if (Forbidden && path.Contains("/projects/", StringComparison.Ordinal))
            return Task.FromResult(Json(new { detail = "SIMULATED · Quyền truy cập dự án đã bị thu hồi." }, HttpStatusCode.Forbidden));
        if (request.Method == HttpMethod.Put && path.EndsWith($"/scenes/{SceneId}/characters", StringComparison.Ordinal))
        {
            // JSON content is buffered by the in-memory HttpClient fixture; no I/O is performed.
            var payload = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var update = JsonSerializer.Deserialize<UpdateSceneCharactersRequest>(payload, JsonOptions)!;
            SubmittedSceneCharacterIds = _sceneCharacterIds = update.CharacterIds.ToArray();
            return Task.FromResult(Json(new { simulated = true }));
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/characters", StringComparison.Ordinal))
        {
            if (RejectCharacterCreate)
                return Task.FromResult(Json(new { detail = "SIMULATED · Không lưu được nhân vật." }, HttpStatusCode.UnprocessableEntity));
            var payload = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var create = JsonSerializer.Deserialize<CreateCharacterRequest>(payload, JsonOptions)!;
            CreatedCharacterRequest = create;
            _createdCharacter = new CharacterDto(Guid.NewGuid(), ProjectId, create.Name, create.Role,
                create.Description, "Active", [], [], "AAAAAAAADAM=");
            return Task.FromResult(Json(_createdCharacter, HttpStatusCode.Created));
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/generations", StringComparison.Ordinal))
        {
            var payload = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            FullQualityRequested = JsonSerializer.Deserialize<CreateGenerationRequest>(payload, JsonOptions)!.FullQuality;
            return Task.FromResult(Json(new { id = UpscaleJobId }, HttpStatusCode.Accepted));
        }
        if (path.EndsWith($"/media/{UpscaleAssetId}", StringComparison.Ordinal))
            return Task.FromResult(Image(FullQualityRequested ? 4 : 2));

        object? body = null;
        if (path.EndsWith("/auth/login", StringComparison.Ordinal) || path.EndsWith("/auth/refresh", StringComparison.Ordinal))
            body = new BearerTokenResponse("Bearer", "SIMULATED-NOT-A-REAL-ACCESS-TOKEN", 3600, "SIMULATED-NOT-A-REAL-REFRESH-TOKEN");
        else if (path.EndsWith("/auth/me", StringComparison.Ordinal))
            body = new UserInfoResponse("smoke-user", "smoke@example.invalid", "Nhà thiết kế thử nghiệm", true, ["SystemAdmin"]);
        else if (path.EndsWith("/generation-capabilities", StringComparison.Ordinal))
            body = new GenerationCapabilitiesDto("UI fixture", true, false, "SIMULATED · Workflow ảnh tham chiếu chưa được nghiệm thu trên GPU.", FullQualityAvailable: true);
        else if (path.EndsWith("/admin/accounts", StringComparison.Ordinal))
            body = Page(new[] { new AccountDto("smoke-user", "smoke@example.invalid", "Nhà thiết kế thử nghiệm", true, FixtureDate, ["SystemAdmin"]) });
        else if (path == "/api/v1/projects")
            body = Page(Projects);
        else if (path == "/api/v1/health") body = new { status = "Healthy", provenance = "SIMULATED" };
        else if (path.EndsWith("/members", StringComparison.Ordinal))
            body = new[] { new ProjectMemberDto(ProjectId, "smoke-user", "Nhà thiết kế thử nghiệm", "smoke@example.invalid", "Owner", FixtureDate),
                new ProjectMemberDto(ProjectId, "smoke-designer", "Minh Anh", "designer@example.invalid", "Designer", FixtureDate) };
        else if (path.EndsWith("/characters/paged", StringComparison.Ordinal))
        {
            var query = request.RequestUri.Query.TrimStart('?').Split('&')
                .Select(pair => pair.Split('=', 2)).FirstOrDefault(pair => pair[0] == "query");
            var search = query?.Length == 2 ? Uri.UnescapeDataString(query[1]) : string.Empty;
            var available = _createdCharacter == null ? Characters : [.. Characters, _createdCharacter];
            body = Page(available.Where(character => string.IsNullOrWhiteSpace(search)
                || character.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || character.Role.Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToArray());
        }
        else if (path.EndsWith("/characters", StringComparison.Ordinal)) body = Characters;
        else if (path.EndsWith("/attribute-definitions", StringComparison.Ordinal))
            body = Characters[0].Attributes.Select(a => new AttributeDefinitionDto(a.DefinitionId, ProjectId, a.Code, a.DisplayName, a.DataType, null, false, a.IsIdentityCritical)).ToArray();
        else if (path.EndsWith("/character-templates", StringComparison.Ordinal)) body = Array.Empty<CharacterTemplateDto>();
        else if (path.EndsWith("/relationships", StringComparison.Ordinal)) body = Array.Empty<CharacterRelationshipDto>();
        else if (path.EndsWith("/revisions", StringComparison.Ordinal))
            body = new[] { new CharacterRevisionDto(Guid.Parse("60000000-0000-0000-0000-000000000001"), CharacterId, null, 1, "{}", null, "smoke-user", FixtureDate, "SIMULATED · Tạo hồ sơ ban đầu") };
        else if (path.EndsWith("/concepts", StringComparison.Ordinal))
            body = path.Contains("/characters/", StringComparison.Ordinal)
                ? Array.Empty<CharacterConceptDto>() : Page(Array.Empty<CharacterConceptDto>());
        else if (path.EndsWith("/variants", StringComparison.Ordinal)) body = Characters[0].Variants;
        else if (path.EndsWith("/attributes", StringComparison.Ordinal)) body = Characters[0].Attributes;
        else if (path.EndsWith("/prompt-preview", StringComparison.Ordinal))
            body = new PromptPreviewDto("SIMULATED · Áo khoác thám hiểm", "SIMULATED · Linh Nguyệt, tóc bạc ánh tím", "SIMULATED · Linh Nguyệt, áo khoác thám hiểm", [], "1.0", "{}");
        else if (path.EndsWith($"/generations/{UpscaleJobId}", StringComparison.Ordinal))
            body = new GenerationJobDto(UpscaleJobId, ProjectId, CharacterId, null, "smoke-user", GenerationStatus.Completed,
                PromptMode.Raw, "SIMULATED", 2, 2, 1, 1, 42, null, true, FixtureDate, null, null, null, [UpscaleAssetId], FullQuality: FullQualityRequested);
        else if (path.EndsWith("/generations", StringComparison.Ordinal))
            body = Page(new[] { new GenerationJobDto(Guid.Parse("70000000-0000-0000-0000-000000000001"), ProjectId, CharacterId, null, "smoke-user", GenerationStatus.Pending,
                PromptMode.Hybrid, "SIMULATED · Fixture chỉ kiểm tra giao diện, không sinh ảnh.", 1024, 1024, 1, 0, 42, null, true, FixtureDate, null, null, null, []) });
        else if (path.EndsWith("/scenes", StringComparison.Ordinal))
            body = new[] { Scene };
        else if (path.EndsWith($"/scenes/{SceneId}", StringComparison.Ordinal)) body = Scene;
        else if (path.EndsWith("/scripts", StringComparison.Ordinal))
            body = new[] { new ScriptDto(ScriptId, ProjectId, "Chương 1 · Bản đồ của những vì sao", "SIMULATED · Bối cảnh và hành trình mở đầu.", "Active", FixtureDate, FixtureDate, "AAAAAAAABAA=", 1) };
        else if (path.EndsWith($"/characters/{CharacterId}", StringComparison.Ordinal)) body = Characters[0];
        else if (path == "/api/v1/studio/pose-gallery")
        {
            var sampleItem = new PoseGalleryItemDto(
                "full_single_P001_front", "full_standard:P001", "P001",
                "full_standard", "Toàn thân · Chuẩn", "standing", "Đứng",
                "Dáng đứng chuẩn · P001", "标准站姿", "front", "Chính diện", false,
                "/api/v1/studio/pose-gallery/image/full_single_P001_front?kind=thumb&mode=mannequin",
                "/api/v1/studio/pose-gallery/image/full_single_P001_front?kind=thumb&mode=depth");
            body = new PoseGalleryResponse(
                TotalPoses: 765, TotalGroups: 153, FilteredGroups: 1, Page: 1, PageSize: 18,
                IncludeNsfw: false, IsAvailable: true,
                Systems: [new("full_standard", "Toàn thân · Chuẩn", 73)],
                Categories: [new("standing", "Đứng", 40)],
                Angles: [new("front", "Chính diện", 153)],
                Groups: [new PoseGalleryGroupDto("full_standard:P001", "P001", "full_standard", "Toàn thân · Chuẩn", "standing", "Đứng", "Dáng đứng chuẩn · P001", false, [sampleItem])]);
        }
        else if (path.StartsWith("/api/v1/studio/pose-gallery/image/", StringComparison.Ordinal))
            return Task.FromResult(Image(64));
        else if (path == "/api/v1/studio/outfit-gallery")
        {
            var maleSuit = new OutfitGalleryItemDto(
                "shisei-C195", "C195", "shisei_modern", "Hiện đại · Đa phong cách (CC0)", "shisei_fm", "Vest · Lễ phục",
                "Vest · Suit · Vest nam 3 mảnh (xám than)",
                "男士三件套西装（炭灰）",
                "a men's charcoal grey three-piece tailored suit with white shirt and burgundy tie",
                ["Nam", "Vest · Suit", "4 góc"], false, 97, "★ Chuẩn nhất",
                "/api/v1/studio/outfit-gallery/image/shisei-C195?kind=thumb&view=front",
                [
                    new OutfitGalleryViewDto("front", "Chính diện", "/api/v1/studio/outfit-gallery/image/shisei-C195?kind=thumb&view=front"),
                    new OutfitGalleryViewDto("diag", "Nghiêng 3/4", "/api/v1/studio/outfit-gallery/image/shisei-C195?kind=thumb&view=diag"),
                    new OutfitGalleryViewDto("side", "Góc ngang", "/api/v1/studio/outfit-gallery/image/shisei-C195?kind=thumb&view=side"),
                    new OutfitGalleryViewDto("back", "Phía sau", "/api/v1/studio/outfit-gallery/image/shisei-C195?kind=thumb&view=back")
                ], "male", "Nam");
            var shiseiItem = new OutfitGalleryItemDto(
                "shisei-C012", "C012", "shisei_fantasy", "Giả tưởng & Cổ trang (CC0)", "shisei_hc", "Cổ trang Trung Hoa",
                "Đời Đường · Váy Tề Hung Nhu Quần (Đỏ lựu)",
                "唐 · 齐胸襦裙（石榴红）",
                "a Tang-dynasty Chinese women's qixiong ruqun in pomegranate-red silk",
                ["Nữ", "Nhà Đường", "4 góc"], false, 97, "★ Chuẩn nhất",
                "/api/v1/studio/outfit-gallery/image/shisei-C012?kind=thumb&view=front",
                [
                    new OutfitGalleryViewDto("front", "Chính diện", "/api/v1/studio/outfit-gallery/image/shisei-C012?kind=thumb&view=front"),
                    new OutfitGalleryViewDto("diag", "Nghiêng 3/4", "/api/v1/studio/outfit-gallery/image/shisei-C012?kind=thumb&view=diag"),
                    new OutfitGalleryViewDto("side", "Góc ngang", "/api/v1/studio/outfit-gallery/image/shisei-C012?kind=thumb&view=side"),
                    new OutfitGalleryViewDto("back", "Phía sau", "/api/v1/studio/outfit-gallery/image/shisei-C012?kind=thumb&view=back")
                ], "female", "Nữ");
            var brokenItem = new OutfitGalleryItemDto(
                "PTY-0003", "PTY-0003", "modern", "Thời trang Nữ 4 mùa", "party", "Dự tiệc · Lễ hội",
                "Găng tay satin xanh bạc hà · Giày cao gót",
                "薄荷绿缎面手套・高跟鞋",
                "薄荷绿缎面手套、高跟鞋 (mint satin gloves heels)",
                ["Xanh bạc hà", "Găng tay", "Giày cao gót"], false, 48, "Thiếu mô tả / Dễ lệch",
                "/api/v1/studio/outfit-gallery/image/PTY-0003?kind=thumb&view=front",
                [new OutfitGalleryViewDto("front", "Chính diện", "/api/v1/studio/outfit-gallery/image/PTY-0003?kind=thumb&view=front")],
                "female", "Nữ");
            body = new OutfitGalleryResponse(
                TotalOutfits: 1472, FilteredOutfits: 3, Page: 1, PageSize: 18,
                IncludeNsfw: false, IsAvailable: true,
                Collections: [new("shisei_modern", "Hiện đại · Đa phong cách (CC0)", 170), new("modern", "Thời trang Nữ 4 mùa", 1068), new("shisei_fantasy", "Giả tưởng & Cổ trang (CC0)", 234)],
                Categories: [new("shisei_fm", "Vest · Lễ phục", 30), new("shisei_hc", "Cổ trang Trung Hoa", 60), new("party", "Dự tiệc · Lễ hội", 48)],
                Items: [maleSuit, shiseiItem, brokenItem],
                Genders: [new("male", "Nam", 154), new("unisex", "Unisex", 69), new("female", "Nữ", 1249)]);
        }
        else if (path.StartsWith("/api/v1/studio/outfit-gallery/image/", StringComparison.Ordinal))
            return Task.FromResult(Image(64));
        else if (path.EndsWith($"/projects/{ProjectId}", StringComparison.Ordinal)) body = Projects[0];

        if (body is null)
        {
            UnhandledRequests.Add($"{request.Method} {path}");
            return Task.FromResult(Json(new { detail = "SIMULATED fixture endpoint is not implemented." }, HttpStatusCode.NotFound));
        }
        return Task.FromResult(Json(body));
    }

    private static HttpResponseMessage Image(int width)
    {
        var bitmap = BitmapSource.Create(width, width, 96, 96, PixelFormats.Bgr24, null, new byte[width * width * 3], width * 3);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var content = new ByteArrayContent(stream.ToArray());
        content.Headers.ContentType = new("image/png");
        return new(HttpStatusCode.OK) { Content = content };
    }

    private static PagedResult<T> Page<T>(IReadOnlyList<T> items) => new() { Items = items, Page = 1, PageSize = 24, TotalCount = items.Count };
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = JsonContent.Create(value, value.GetType(), options: JsonOptions) };
}
