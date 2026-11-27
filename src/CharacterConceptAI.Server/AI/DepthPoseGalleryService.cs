using System.Text.Json;
using System.Text.RegularExpressions;
using CharacterConceptAI.Shared.Contracts.Generations;

namespace CharacterConceptAI.Server.AI;

public interface IDepthPoseGalleryService
{
    bool IsAvailable { get; }
    void Prewarm();
    PoseGalleryResponse Query(bool includeNsfw, string? system, string? category, string? angle, string? query, int page, int pageSize);
    bool TryResolveImageFile(string poseId, string kind, string mode, out string fullPath, out string contentType);
    Task<byte[]?> TryReadDepthImageBytesAsync(string poseId, CancellationToken ct = default);
    bool PoseExists(string poseId);
    string BuildSynchronizedPrompt(string poseId, string basePrompt);
}

public sealed class DepthPoseGalleryService : IDepthPoseGalleryService
{
    private readonly string _rootDir;
    private readonly string _thumbsRoot;
    private readonly string _imgRoot;
    private readonly ILogger<DepthPoseGalleryService> _logger;
    private readonly object _loadLock = new();
    private bool _loaded;
    private List<PoseEntry> _entries = [];
    private List<PoseEntry> _sfwEntries = [];
    private Dictionary<string, PoseEntry> _byId = new(StringComparer.OrdinalIgnoreCase);
    private List<PoseGroupInternal> _groups = [];
    private List<PoseGroupInternal> _sfwGroups = [];
    private List<PoseGalleryFilterOption> _allSystemOptions = [];
    private List<PoseGalleryFilterOption> _sfwSystemOptions = [];
    private List<PoseGalleryFilterOption> _allAngleOptions = [];
    private List<PoseGalleryFilterOption> _sfwAngleOptions = [];

    private static readonly string[] AngleOrder = ["front", "diag", "side", "low", "high"];

    private static readonly Dictionary<string, int> SystemReliabilityRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["upper"] = 1,
        ["full_standard"] = 2,
        ["full_sexy"] = 3,
        ["couple"] = 4,
    };

    private static readonly Dictionary<string, int> CategoryReliabilityRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cute"] = 1,
        ["standing"] = 2,
        ["glamour"] = 3,
        ["twisting"] = 4,
        ["side_sit"] = 5,
        ["kneeling"] = 6,
        ["leaning_forward"] = 7,
        ["sitting"] = 8,
        ["leaning_back"] = 9,
        ["cleavage"] = 10,
        ["crouching"] = 11,
        ["all_fours"] = 12,
        ["lying"] = 13,
        ["special"] = 14,
        ["couple_close_stand"] = 20,
        ["upright"] = 21,
        ["couple_face_sit"] = 22,
        ["seated"] = 23,
        ["couple_back_sit"] = 24,
        ["couple_close_sit"] = 25,
        ["side_lying"] = 26,
        ["face_touch"] = 27,
        ["close_body"] = 28,
        ["couple_classic"] = 29,
        ["couple_back"] = 30,
        ["couple_mount"] = 31,
    };

    private static readonly Dictionary<string, (string Key, string LabelVi, bool IsNsfw)> SystemMap = new()
    {
        ["全身单人・标准系"] = ("full_standard", "Toàn thân · Chuẩn", false),
        ["上半身单人"] = ("upper", "Nửa thân trên", false),
        ["全身单人・性感系"] = ("full_sexy", "Toàn thân · Gợi cảm (18+)", true),
        ["双人缠绵"] = ("couple", "Cặp đôi (18+)", true),
    };

    private static readonly Dictionary<string, (string Key, string LabelVi)> AngleMap = new()
    {
        ["正面"] = ("front", "Chính diện"),
        ["斜侧"] = ("diag", "Nghiêng 3/4"),
        ["侧面"] = ("side", "Góc ngang"),
        ["俯视"] = ("high", "Từ trên xuống"),
        ["仰视"] = ("low", "Ngước lên"),
    };

    private static readonly Dictionary<string, (string Key, string LabelVi)> CategoryMap = new()
    {
        ["站姿"] = ("standing", "Đứng"),
        ["坐姿"] = ("sitting", "Ngồi"),
        ["坐位"] = ("seated", "Tư thế ngồi"),
        ["立位"] = ("upright", "Đứng thẳng"),
        ["蹲姿"] = ("crouching", "Ngồi xổm"),
        ["跪立"] = ("kneeling", "Quỳ"),
        ["跪立・四肢着地"] = ("all_fours", "Quỳ chống tay"),
        ["趴卧"] = ("lying", "Nằm sấp / nghiêng"),
        ["侧卧位"] = ("side_lying", "Nằm nghiêng"),
        ["侧坐・女孩坐"] = ("side_sit", "Ngồi nghiêng"),
        ["前倾"] = ("leaning_forward", "Nghiêng về trước"),
        ["后仰・挺背"] = ("leaning_back", "Ngả lưng"),
        ["扭身"] = ("twisting", "Xoay người / ngoái nhìn"),
        ["可爱系"] = ("cute", "Dễ thương / năng động"),
        ["贴脸系"] = ("face_touch", "Chạm mặt / cận cảnh"),
        ["贴身"] = ("close_body", "Tương tác gần"),
        ["性感・大胆系"] = ("glamour", "Quyến rũ (18+)"),
        ["挤胸・强调事业线系"] = ("cleavage", "Gợi cảm cận thân (18+)"),
        ["特殊"] = ("special", "Đặc biệt / sáng tạo"),
        ["正常位（传教士）"] = ("couple_classic", "Cặp đôi · Cơ bản (18+)"),
        ["后入位"] = ("couple_back", "Cặp đôi · Phía sau (18+)"),
        ["骑乘位"] = ("couple_mount", "Cặp đôi · Ngồi trên (18+)"),
        ["面对面坐位"] = ("couple_face_sit", "Cặp đôi · Đối mặt (18+)"),
        ["背向坐位"] = ("couple_back_sit", "Cặp đôi · Tựa lưng (18+)"),
        ["坐位贴身"] = ("couple_close_sit", "Cặp đôi · Ngồi sát (18+)"),
        ["立位贴身"] = ("couple_close_stand", "Cặp đôi · Đứng sát (18+)"),
    };

    private static readonly (string Zh, string Vi)[] PhraseReplacements =
    [
        ("S形站姿", "Đứng dáng chữ S"),
        ("单膝立起", "chống 1 gối"),
        ("双膝立起", "chống 2 gối"),
        ("单手撑地", "một tay chống sàn"),
        ("双手撑地", "hai tay chống sàn"),
        ("双手抱膝", "hai tay ôm gối"),
        ("双手叉腰", "chống nạnh hai tay"),
        ("单手叉腰", "chống nạnh một tay"),
        ("双手举高", "giơ cao hai tay"),
        ("单手举高", "giơ cao một tay"),
        ("双手背在身后", "hai tay chắp sau lưng"),
        ("双手放在脑后", "hai tay đặt sau gáy"),
        ("双手合十", "chắp hai tay"),
        ("双臂交叉", "khoanh tay"),
        ("双手托腮", "chống cằm hai tay"),
        ("单手托腮", "chống cằm một tay"),
        ("指尖轻触嘴唇", "ngón tay chạm môi"),
        ("手放嘴唇", "tay chạm môi"),
        ("撩头发", "vuốt tóc"),
        ("拨弄头发", "nghịch tóc"),
        ("回头看", "ngoái nhìn lại"),
        ("回眸", "ngoái nhìn"),
        ("后仰坐", "Ngồi ngả người"),
        ("正坐", "Ngồi ngay ngắn"),
        ("盘腿坐", "Ngồi khoanh chân"),
        ("侧坐", "Ngồi nghiêng"),
        ("翘二郎腿", "ngồi bắt chéo chân"),
        ("交叉腿", "bắt chéo chân"),
        ("伸懒腰", "vươn vai"),
        ("前倾站姿", "Đứng nghiêng người về trước"),
        ("靠墙站姿", "Đứng tựa tường"),
        ("行走姿势", "Dáng bước đi"),
        ("奔跑姿势", "Dáng chạy"),
        ("跳跃姿势", "Dáng nhảy"),
        ("站姿", "Dáng đứng"),
        ("坐姿", "Dáng ngồi"),
        ("蹲姿", "Dáng ngồi xổm"),
        ("跪姿", "Dáng quỳ"),
        ("躺姿", "Dáng nằm"),
        ("趴卧", "Nằm sấp"),
        ("侧卧", "Nằm nghiêng"),
        ("仰卧", "Nằm ngửa"),
        ("胯部侧顶", "hông đẩy lệch"),
        ("挺胸", "ưỡn ngực"),
        ("收腰", "thon eo"),
        ("托脸", "nâng cằm"),
        ("比耶", "giơ tay chữ V"),
        ("比心", "thả tim"),
        ("挥手", "vẫy tay"),
        ("敬礼", "giơ tay chào"),
        ("抱胸", "khoanh tay trước ngực"),
        ("伸手", "vươn tay"),
        ("单腿站立", "đứng một chân"),
        ("踮脚", "kiễng chân"),
        ("抬头", "ngẩng đầu"),
        ("低头", "cúi đầu"),
        ("歪头", "nghiêng đầu"),
        ("微笑", "mỉm cười"),
        ("正面", "chính diện"),
        ("侧面", "góc ngang"),
        ("背面", "từ phía sau"),
        ("・", " · ")
    ];

    public DepthPoseGalleryService(IConfiguration config, ILogger<DepthPoseGalleryService> logger)
    {
        _logger = logger;
        var configured = config["PoseGallery:RootPath"];
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            _rootDir = Path.GetFullPath(configured);
        }
        else
        {
            var candidates = new List<string>();
            var envHome = Environment.GetEnvironmentVariable("GULA_STUDIO_HOME");
            if (!string.IsNullOrWhiteSpace(envHome))
                candidates.Add(Path.Combine(envHome.Trim(), "NEWTOY", "ai-pose-gallery-zh"));

            var current = AppContext.BaseDirectory;
            for (var depth = 0; depth < 7 && !string.IsNullOrWhiteSpace(current); depth++)
            {
                candidates.Add(Path.Combine(current, "NEWTOY", "ai-pose-gallery-zh"));
                current = Directory.GetParent(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
            }
            candidates.Add(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "NEWTOY", "ai-pose-gallery-zh")));
            _rootDir = candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, "data", "pose-zh.json"))) ?? string.Empty;
        }
        _thumbsRoot = string.IsNullOrEmpty(_rootDir) ? string.Empty : Path.GetFullPath(Path.Combine(_rootDir, "thumbs")) + Path.DirectorySeparatorChar;
        _imgRoot = string.IsNullOrEmpty(_rootDir) ? string.Empty : Path.GetFullPath(Path.Combine(_rootDir, "img")) + Path.DirectorySeparatorChar;
    }

    public void Prewarm() => EnsureLoaded();

    public bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return _entries.Count > 0;
        }
    }

    public bool PoseExists(string poseId)
    {
        if (string.IsNullOrWhiteSpace(poseId)) return false;
        EnsureLoaded();
        return _byId.ContainsKey(poseId.Trim());
    }

    public PoseGalleryResponse Query(bool includeNsfw, string? system, string? category, string? angle, string? query, int page, int pageSize)
    {
        EnsureLoaded();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 12, 120);
        var q = query?.Trim() ?? string.Empty;
        var sysFilter = system?.Trim() ?? string.Empty;
        var catFilter = category?.Trim() ?? string.Empty;
        var angleFilter = angle?.Trim() ?? string.Empty;

        var visibleEntries = includeNsfw ? _entries : _sfwEntries;
        var visibleGroups = includeNsfw ? _groups : _sfwGroups;
        var systemOptions = includeNsfw ? _allSystemOptions : _sfwSystemOptions;
        var angleOptions = includeNsfw ? _allAngleOptions : _sfwAngleOptions;

        var catBase = string.IsNullOrWhiteSpace(sysFilter)
            ? visibleGroups
            : visibleGroups.Where(g => g.SystemKey.Equals(sysFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var categoryOptions = catBase
            .GroupBy(g => (g.CategoryKey, g.CategoryLabelVi))
            .Select(g => new PoseGalleryFilterOption(g.Key.CategoryKey, g.Key.CategoryLabelVi, g.Count()))
            .OrderBy(o => CategoryReliabilityRank.TryGetValue(o.Key, out var r) ? r : 99)
            .ThenByDescending(o => o.Count)
            .ToList();

        var filtered = visibleGroups.Where(g =>
        {
            if (!string.IsNullOrWhiteSpace(sysFilter) && !g.SystemKey.Equals(sysFilter, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(catFilter) && !g.CategoryKey.Equals(catFilter, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrWhiteSpace(angleFilter) && !g.Angles.Any(a => a.AngleKey.Equals(angleFilter, StringComparison.OrdinalIgnoreCase)))
                return false;
            if (q.Length > 0 && !g.SearchBlob.Contains(q, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }).ToList();

        var skipLong = (long)(page - 1) * pageSize;
        var paged = skipLong >= filtered.Count
            ? []
            : filtered
                .Skip((int)skipLong)
                .Take(pageSize)
                .Select(g => new PoseGalleryGroupDto(
                    g.GroupKey,
                    g.PoseCode,
                    g.SystemKey,
                    g.SystemLabelVi,
                    g.CategoryKey,
                    g.CategoryLabelVi,
                    g.NameVi,
                    g.IsNsfw,
                    g.Angles.Select(ToDto).ToList(),
                    g.QualityScore,
                    g.QualityBadge))
                .ToList();

        return new PoseGalleryResponse(
            TotalPoses: visibleEntries.Count,
            TotalGroups: visibleGroups.Count,
            FilteredGroups: filtered.Count,
            Page: page,
            PageSize: pageSize,
            IncludeNsfw: includeNsfw,
            IsAvailable: _entries.Count > 0,
            Systems: systemOptions,
            Categories: categoryOptions,
            Angles: angleOptions,
            Groups: paged);
    }

    public bool TryResolveImageFile(string poseId, string kind, string mode, out string fullPath, out string contentType)
    {
        fullPath = string.Empty;
        contentType = "application/octet-stream";
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(poseId) || !_byId.TryGetValue(poseId.Trim(), out var entry))
            return false;

        var useDepth = string.Equals(mode, "depth", StringComparison.OrdinalIgnoreCase);
        var useThumb = !string.Equals(kind, "full", StringComparison.OrdinalIgnoreCase);
        var relPath = useDepth
            ? (useThumb ? entry.DepthThumbRel : entry.DepthFullRel)
            : (useThumb ? entry.ThumbRel : entry.FullRel);

        if (string.IsNullOrWhiteSpace(relPath) || string.IsNullOrEmpty(_rootDir)) return false;
        var candidate = Path.GetFullPath(Path.Combine(_rootDir, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(_thumbsRoot, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(_imgRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!File.Exists(candidate)) return false;
        fullPath = candidate;
        contentType = Path.GetExtension(candidate).ToLowerInvariant() switch
        {
            ".webp" => "image/webp",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
        return true;
    }

    private static readonly Regex CloseUpConflictRegex = new(
        @"\b(?:extreme\s+close[- ]?up|close[- ]?up|headshot|macro\s+portrait)\b|chân dung cận mặt|cận cảnh khuôn mặt|cận cảnh",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex FullBodyConflictRegex = new(
        @"\b(?:full[- ]?body\s+shot|full[- ]?body|head\s+to\s+toe)\b|ảnh\s+toàn\s+thân|toàn\s+thân",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex StandingConflictRegex = new(
        @"\b(?:standing\s+up|standing\s+pose|standing)\b|đang\s+đứng|tư\s+thế\s+đứng",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex SittingLyingConflictRegex = new(
        @"\b(?:sitting\s+down|sitting|lying\s+down|lying)\b|đang\s+ngồi|đang\s+nằm",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public string BuildSynchronizedPrompt(string poseId, string basePrompt)
    {
        var prompt = basePrompt?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(poseId)) return prompt;
        EnsureLoaded();
        if (!_byId.TryGetValue(poseId.Trim(), out var entry))
            return prompt;

        // 1. Heal framing conflicts between text prompt and 3D depth map (e.g. "close-up" prompt on a full-body pose)
        var isFullBody = entry.SystemKey is "full_standard" or "full_sexy" or "couple";
        var isUpper = entry.SystemKey == "upper";
        if (isFullBody && !string.IsNullOrEmpty(prompt))
            prompt = CloseUpConflictRegex.Replace(prompt, "full-body shot");
        else if (isUpper && !string.IsNullOrEmpty(prompt))
            prompt = FullBodyConflictRegex.Replace(prompt, "upper-body shot");

        // 2. Heal posture conflicts (e.g. "standing" prompt fighting a sitting/lying/kneeling 3D depth silhouette)
        var rawName = entry.NameOriginal ?? string.Empty;
        var postureCue = entry.CategoryKey switch
        {
            "standing" or "upright" => "standing pose",
            "couple_close_stand" => "two people standing hugging closely",
            "sitting" or "seated" or "side_sit" => "sitting pose on the floor",
            "couple_face_sit" => "two people sitting face to face",
            "couple_back_sit" => "two people sitting together, one hugging from behind",
            "couple_close_sit" => "two people sitting closely together",
            "crouching" => "crouching squatting pose",
            "kneeling" => "kneeling pose on the floor",
            "all_fours" => "kneeling on hands and knees on the floor",
            "couple_back" => "two people, kneeling on hands and knees from behind",
            "lying" or "side_lying" => rawName.Contains("仰卧", StringComparison.Ordinal)
                ? "lying on back on the floor"
                : rawName.Contains("侧卧", StringComparison.Ordinal)
                    ? "lying on side on the floor"
                    : "lying face-down on the floor",
            "couple_classic" => "two people lying close together on bed or floor",
            "couple_mount" => "two people, one lying and one sitting upright on top",
            "leaning_forward" => "standing leaning forward pose",
            "leaning_back" => "standing leaning back arched pose",
            "twisting" => "standing twisting torso pose looking back",
            "cute" => "upper-body portrait, playful expressive hand gesture",
            "glamour" => "upper-body portrait, confident glamorous pose",
            "cleavage" => "upper-body portrait, leaning slightly forward",
            "face_touch" => entry.SystemKey == "couple" ? "two people lying closely face to face" : "upper-body portrait, hand gently touching face",
            "close_body" => "two people posing closely together",
            "special" => (rawName.Contains("蹲", StringComparison.Ordinal) || rawName.Contains("跪", StringComparison.Ordinal))
                ? "deep crouching or kneeling flexible pose on the floor"
                : "athletic flexible yoga bridge or stretching pose on a mat on the floor",
            _ => string.Empty
        };
        if (!string.IsNullOrEmpty(prompt))
        {
            if (entry.CategoryKey is "sitting" or "seated" or "side_sit" or "crouching" or "kneeling" or "all_fours" or "lying" or "side_lying" or "special" or "couple_classic" or "couple_mount" or "couple_face_sit" or "couple_back_sit" or "couple_close_sit")
                prompt = StandingConflictRegex.Replace(prompt, postureCue);
            else if (entry.CategoryKey is "standing" or "upright" or "couple_close_stand")
                prompt = SittingLyingConflictRegex.Replace(prompt, postureCue);
        }

        // 3. Build concise native pose + posture + camera angle anchor (Qwen3-4B inside Z-Image understands EN/ZH/VI natively)
        var framingCue = entry.SystemKey switch
        {
            "upper" => "upper-body shot of one person",
            "couple" => "full-body shot of two distinct people (a man and a woman, two separate clearly visible heads and bodies)",
            _ => "full-body shot of one person"
        };
        var angleCue = entry.AngleKey switch
        {
            "diag" => "three-quarter angle view",
            "side" => "side profile view",
            "high" => "high-angle shot looking down",
            "low" => "low-angle shot looking up",
            _ => "front view"
        };

        var poseDesc = !string.IsNullOrWhiteSpace(entry.CategoryOriginal) && !string.IsNullOrWhiteSpace(entry.NameOriginal)
            ? $"{entry.CategoryOriginal} ({entry.NameOriginal})"
            : !string.IsNullOrWhiteSpace(entry.NameOriginal) ? entry.NameOriginal : entry.NameVi;

        var handActionCue = BuildEnglishHandActionCue(rawName);
        var posturePart = string.IsNullOrEmpty(postureCue) ? string.Empty : $", {postureCue}";
        var actionPart = string.IsNullOrEmpty(handActionCue) ? string.Empty : $", {handActionCue}";
        var prefix = $"{framingCue}{posturePart}{actionPart}, pose: {poseDesc}, camera angle: {angleCue}";
        if (string.IsNullOrWhiteSpace(prompt)) return prefix;
        if (prompt.Contains(poseDesc, StringComparison.OrdinalIgnoreCase)) return prompt;
        return $"{prefix}, {prompt}";
    }

    private static string BuildEnglishHandActionCue(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;

        // Priority hand-on-chest / hand-on-body contact actions so long English prompts never overwrite visible hands/fingers
        if (rawName.Contains("向内挤", StringComparison.Ordinal) || rawName.Contains("从两侧挤", StringComparison.Ordinal) || rawName.Contains("上胸向内挤", StringComparison.Ordinal))
            return "both hands firmly holding and pressing inward against the sides of her breasts with all fingers clearly visible";
        if (rawName.Contains("双手捧胸", StringComparison.Ordinal) || rawName.Contains("自下托起", StringComparison.Ordinal) || rawName.Contains("交叉托起", StringComparison.Ordinal))
            return "both hands cupping and lifting her breasts from underneath with all fingers clearly visible";
        if (rawName.Contains("双手揉胸", StringComparison.Ordinal))
            return "both hands resting on and holding her breasts with all fingers clearly visible";
        if (rawName.Contains("单手揉胸", StringComparison.Ordinal) || rawName.Contains("单手挤胸", StringComparison.Ordinal))
            return "one hand resting on and holding her breast with fingers clearly visible";
        if (rawName.Contains("双手抚胸", StringComparison.Ordinal) || rawName.Contains("探入胸前", StringComparison.Ordinal))
            return "both hands resting on her chest with all fingers clearly visible";
        if (rawName.Contains("单手抚胸", StringComparison.Ordinal) || rawName.Contains("单手托胸", StringComparison.Ordinal) || rawName.Contains("单臂托起胸部", StringComparison.Ordinal))
            return "one hand gently holding her breast with fingers clearly visible";
        if (rawName.Contains("胸下缘", StringComparison.Ordinal))
            return "fingers resting and tracing along the underside of her breasts";
        if (rawName.Contains("遮掩", StringComparison.Ordinal) || rawName.Contains("遮蔽", StringComparison.Ordinal))
            return "hands covering her chest with palms and fingers clearly visible";
        if (rawName.Contains("抱胸", StringComparison.Ordinal) || rawName.Contains("环抱托胸", StringComparison.Ordinal) || rawName.Contains("手肘挤胸", StringComparison.Ordinal))
            return "arms crossed beneath and pressing her chest together";
        if (rawName.Contains("褪下肩带", StringComparison.Ordinal))
            return "hands pulling down her shoulder strap";

        // Expressive upper-body & full-body hand gestures
        if (rawName.Contains("比心", StringComparison.Ordinal))
            return "hands and fingers forming a heart shape";
        if (rawName.Contains("比双V", StringComparison.Ordinal) || rawName.Contains("辣妹V", StringComparison.Ordinal) || rawName.Contains("比耶", StringComparison.Ordinal))
            return "making a V peace sign gesture with fingers";
        if (rawName.Contains("托腮", StringComparison.Ordinal) || rawName.Contains("捧脸", StringComparison.Ordinal) || rawName.Contains("贴脸", StringComparison.Ordinal))
            return "hands gently cupping her cheeks and face";
        if (rawName.Contains("抵唇", StringComparison.Ordinal) || rawName.Contains("含于唇边", StringComparison.Ordinal) || rawName.Contains("掩口", StringComparison.Ordinal) || rawName.Contains("轻触嘴唇", StringComparison.Ordinal))
            return "fingers gently touching near her lips";
        if (rawName.Contains("戳脸", StringComparison.Ordinal))
            return "index finger gently poking her cheek";
        if (rawName.Contains("遮单眼", StringComparison.Ordinal))
            return "one hand playfully covering one eye";
        if (rawName.Contains("飞吻", StringComparison.Ordinal))
            return "hand raised near lips blowing a kiss";
        if (rawName.Contains("猫爪", StringComparison.Ordinal))
            return "both hands curled like cat paws near cheeks";
        if (rawName.Contains("兔耳", StringComparison.Ordinal))
            return "both hands raised like bunny ears above head";
        if (rawName.Contains("合十", StringComparison.Ordinal))
            return "both hands clasped together in front of chest";
        if (rawName.Contains("取景框", StringComparison.Ordinal))
            return "both hands forming a rectangular finger frame in front of face";
        if (rawName.Contains("敬礼", StringComparison.Ordinal))
            return "one hand raised in a salute near forehead";
        if (rawName.Contains("指镜头", StringComparison.Ordinal))
            return "one index finger pointing toward camera";
        if (rawName.Contains("抱后脑", StringComparison.Ordinal) || rawName.Contains("抱头", StringComparison.Ordinal) || rawName.Contains("脑后", StringComparison.Ordinal))
            return "both hands raised behind her head";
        if (rawName.Contains("撩发", StringComparison.Ordinal) || rawName.Contains("抚发", StringComparison.Ordinal) || rawName.Contains("挽至耳后", StringComparison.Ordinal))
            return "hand gently brushing through her hair";
        if (rawName.Contains("掐腰", StringComparison.Ordinal) || rawName.Contains("叉腰", StringComparison.Ordinal) || rawName.Contains("扶腰", StringComparison.Ordinal))
            return "hands resting on her waist and hips";
        if (rawName.Contains("触地", StringComparison.Ordinal) || rawName.Contains("撑地", StringComparison.Ordinal) || rawName.Contains("前撑", StringComparison.Ordinal))
            return "hands planted firmly on the ground";
        if (rawName.Contains("扶膝", StringComparison.Ordinal) || rawName.Contains("抱膝", StringComparison.Ordinal))
            return "hands resting on her knees";
        if (rawName.Contains("放大腿内侧", StringComparison.Ordinal))
            return "hands resting on her inner thighs";

        return string.Empty;
    }

    public async Task<byte[]?> TryReadDepthImageBytesAsync(string poseId, CancellationToken ct = default)
    {
        if (!TryResolveImageFile(poseId, kind: "full", mode: "depth", out var fullPath, out _))
            return null;
        return await File.ReadAllBytesAsync(fullPath, ct);
    }

    private void EnsureLoaded()
    {
        if (Volatile.Read(ref _loaded)) return;
        lock (_loadLock)
        {
            if (Volatile.Read(ref _loaded)) return;
            try
            {
                if (string.IsNullOrWhiteSpace(_rootDir)) return;
                var jsonPath = Path.Combine(_rootDir, "data", "pose-zh.json");
                if (!File.Exists(jsonPath)) return;

                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var entries = new List<PoseEntry>();
                var byId = new Dictionary<string, PoseEntry>(StringComparer.OrdinalIgnoreCase);

                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var id = GetStr(el, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var sysRaw = GetStr(el, "体系");
                    var catRaw = GetStr(el, "分类");
                    var angleRaw = GetStr(el, "角度");
                    var code = GetStr(el, "姿势编号");
                    var nameRaw = GetStr(el, "姿势名称");
                    var thumbRel = GetStr(el, "缩略图").TrimStart('/');
                    var fullRel = GetStr(el, "原图").TrimStart('/');

                    var (sysKey, sysVi, isNsfw) = SystemMap.TryGetValue(sysRaw, out var sm)
                        ? sm
                        : ("other", sysRaw, false);
                    var (catKey, catVi) = CategoryMap.TryGetValue(catRaw, out var cm)
                        ? cm
                        : ("other", catRaw);
                    var (angleKey, angleVi) = AngleMap.TryGetValue(angleRaw, out var am)
                        ? am
                        : ("front", angleRaw);

                    var stem = Path.GetFileNameWithoutExtension(fullRel);
                    var depthThumbRel = $"thumbs/depth/{stem}.webp";
                    var depthFullRel = $"img/depth/{stem}.webp";
                    var nameVi = TranslatePoseName(nameRaw, catVi, code);
                    var groupKey = $"{sysKey}:{code}";
                    var quality = el.TryGetProperty("quality", out var qEl) && qEl.ValueKind == JsonValueKind.Number
                        ? Math.Clamp(qEl.GetInt32(), 40, 99)
                        : ComputeFallbackQuality(sysKey, catKey);

                    var entry = new PoseEntry(
                        Id: id,
                        GroupKey: groupKey,
                        PoseCode: code,
                        SystemKey: sysKey,
                        SystemLabelVi: sysVi,
                        CategoryKey: catKey,
                        CategoryLabelVi: catVi,
                        CategoryOriginal: catRaw,
                        NameVi: nameVi,
                        NameOriginal: nameRaw,
                        AngleKey: angleKey,
                        AngleLabelVi: angleVi,
                        IsNsfw: isNsfw,
                        ThumbRel: thumbRel,
                        FullRel: fullRel,
                        DepthThumbRel: depthThumbRel,
                        DepthFullRel: depthFullRel,
                        QualityScore: quality);

                    entries.Add(entry);
                    byId[id] = entry;
                }

                var groups = entries
                    .GroupBy(e => e.GroupKey)
                    .Select(g =>
                    {
                        var ordered = g.OrderBy(a =>
                        {
                            var idx = Array.IndexOf(AngleOrder, a.AngleKey);
                            return idx < 0 ? 99 : idx;
                        }).ThenByDescending(a => a.QualityScore).ToList();
                        var first = ordered[0];
                        var topQ = ordered.Max(a => a.QualityScore);
                        var avgQ = ordered.Average(a => a.QualityScore);
                        var completenessAdj = ordered.Count >= 5 ? 0.0 : -1.5 * (5 - ordered.Count);
                        var groupQuality = Math.Clamp((int)Math.Round(0.65 * topQ + 0.35 * avgQ + completenessAdj), 40, 99);
                        var badge = groupQuality >= 92 ? "★ Chuẩn nhất"
                            : groupQuality >= 82 ? "Ổn định"
                            : "Dáng khó";
                        var searchBlob = $"{first.PoseCode} {first.NameVi} {first.NameOriginal} {first.CategoryLabelVi} {first.SystemLabelVi} {badge} {string.Join(' ', ordered.Select(a => $"{a.Id} {a.AngleLabelVi}"))}";
                        return new PoseGroupInternal(
                            first.GroupKey, first.PoseCode, first.SystemKey, first.SystemLabelVi,
                            first.CategoryKey, first.CategoryLabelVi, first.NameVi, first.IsNsfw,
                            groupQuality, badge, searchBlob, ordered);
                    })
                    .OrderByDescending(g => g.QualityScore)
                    .ThenBy(g => SystemReliabilityRank.TryGetValue(g.SystemKey, out var sr) ? sr : 99)
                    .ThenBy(g => CategoryReliabilityRank.TryGetValue(g.CategoryKey, out var cr) ? cr : 99)
                    .ThenBy(g => g.PoseCode, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                static List<PoseGalleryFilterOption> BuildSystems(List<PoseGroupInternal> gs) => gs
                    .GroupBy(g => (g.SystemKey, g.SystemLabelVi, g.IsNsfw))
                    .Select(g => new PoseGalleryFilterOption(g.Key.SystemKey, g.Key.SystemLabelVi, g.Count(), g.Key.IsNsfw))
                    .OrderBy(o => SystemReliabilityRank.TryGetValue(o.Key, out var r) ? r : 99)
                    .ThenByDescending(o => o.Count)
                    .ToList();

                static List<PoseGalleryFilterOption> BuildAngles(List<PoseEntry> es) => es
                    .GroupBy(e => (e.AngleKey, e.AngleLabelVi))
                    .Select(g => new PoseGalleryFilterOption(g.Key.AngleKey, g.Key.AngleLabelVi, g.Count()))
                    .OrderBy(o => Array.IndexOf(AngleOrder, o.Key))
                    .ToList();

                var sfwEntries = entries.Where(e => !e.IsNsfw).ToList();
                var sfwGroups = groups.Where(g => !g.IsNsfw).ToList();

                _entries = entries;
                _sfwEntries = sfwEntries;
                _byId = byId;
                _groups = groups;
                _sfwGroups = sfwGroups;
                _allSystemOptions = BuildSystems(groups);
                _sfwSystemOptions = BuildSystems(sfwGroups);
                _allAngleOptions = BuildAngles(entries);
                _sfwAngleOptions = BuildAngles(sfwEntries);
                _logger.LogInformation("Đã nạp kho {Count} tư thế Depth ({Groups} nhóm) từ {Root}.", entries.Count, groups.Count, _rootDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể nạp pose-zh.json từ {Root}.", _rootDir);
            }
            finally
            {
                // Publish _loaded strictly AFTER immutable collections are assigned (fixes DCL race)
                Volatile.Write(ref _loaded, true);
            }
        }
    }

    private static string GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() ?? string.Empty : string.Empty;

    private static string TranslatePoseName(string rawName, string categoryVi, string code)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return $"{categoryVi} {code}".Trim();
        var s = rawName;
        foreach (var (zh, vi) in PhraseReplacements)
            s = s.Replace(zh, vi, StringComparison.Ordinal);

        // If significant CJK remains, prepend the clean Vietnamese category + code so the UI stays friendly and legible.
        var cjkCount = s.Count(c => c is >= '\u4E00' and <= '\u9FFF');
        if (cjkCount == 0) return s.Trim();
        return $"{categoryVi} · {code} ({s.Trim()})";
    }

    private static PoseGalleryItemDto ToDto(PoseEntry e) => new(
        e.Id,
        e.GroupKey,
        e.PoseCode,
        e.SystemKey,
        e.SystemLabelVi,
        e.CategoryKey,
        e.CategoryLabelVi,
        e.NameVi,
        e.NameOriginal,
        e.AngleKey,
        e.AngleLabelVi,
        e.IsNsfw,
        $"/api/v1/studio/pose-gallery/image/{Uri.EscapeDataString(e.Id)}?kind=thumb&mode=mannequin",
        $"/api/v1/studio/pose-gallery/image/{Uri.EscapeDataString(e.Id)}?kind=thumb&mode=depth");

    private static int ComputeFallbackQuality(string sysKey, string catKey) => sysKey switch
    {
        "couple" => 60,
        _ => catKey switch
        {
            "cute" or "standing" or "glamour" or "twisting" or "side_sit" or "kneeling" => 95,
            "sitting" or "leaning_forward" or "leaning_back" or "cleavage" or "crouching" => 91,
            "all_fours" or "lying" => 84,
            "special" => 76,
            _ => 86
        }
    };

    private sealed record PoseEntry(
        string Id,
        string GroupKey,
        string PoseCode,
        string SystemKey,
        string SystemLabelVi,
        string CategoryKey,
        string CategoryLabelVi,
        string CategoryOriginal,
        string NameVi,
        string NameOriginal,
        string AngleKey,
        string AngleLabelVi,
        bool IsNsfw,
        string ThumbRel,
        string FullRel,
        string DepthThumbRel,
        string DepthFullRel,
        int QualityScore);

    private sealed record PoseGroupInternal(
        string GroupKey,
        string PoseCode,
        string SystemKey,
        string SystemLabelVi,
        string CategoryKey,
        string CategoryLabelVi,
        string NameVi,
        bool IsNsfw,
        int QualityScore,
        string QualityBadge,
        string SearchBlob,
        List<PoseEntry> Angles);
}
