using CharacterConceptAI.Shared.Contracts.Generations;

namespace CharacterConceptAI.Server.AI;

public interface ILoraCatalogService
{
    LoraCatalogResponse ListAvailable();
    bool TryValidateLora(string fileName, out string normalizedFileName);
    string? ResolveLorasFolder();
}

public sealed class LoraCatalogService : ILoraCatalogService
{
    private readonly IConfiguration _config;

    private sealed record CuratedLoraMeta(
        string DisplayNameVi,
        string DescriptionVi,
        string Category,
        double DefaultStrength,
        bool IsRecommended,
        int SortOrder);

    private static readonly Dictionary<string, CuratedLoraMeta> CuratedMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        ["z_image_detailer.safetensors"] = new(
            "Siêu chi tiết da & vải",
            "Tăng lỗ chân lông thật, sợi tóc tơ, thớ vải và độ tương phản vi mô — loại bỏ cảm giác da nhựa AI.",
            "detail",
            0.40,
            IsRecommended: true,
            SortOrder: 1),
        ["z_image_turbo_realism.safetensors"] = new(
            "Chân thực Studio Pro",
            "Tối ưu ánh sáng chụp chân dung thật, độ sâu màu da và bố cục đời thực cao cấp.",
            "realism",
            0.55,
            IsRecommended: true,
            SortOrder: 2),
        ["z_image_cinema_logc4.safetensors"] = new(
            "Điện ảnh LogC4",
            "Dải sáng động rộng phong cách máy quay điện ảnh ARRI LogC4, chuyển sắc tự nhiên.",
            "cinema",
            0.55,
            IsRecommended: false,
            SortOrder: 3),
        ["z_image_anime_style.safetensors"] = new(
            "Anime Cao cấp",
            "Nét vẽ anime sắc nét, ánh sáng trong trẻo và màu sắc đậm chất minh họa chuyên nghiệp.",
            "anime",
            0.65,
            IsRecommended: false,
            SortOrder: 4),
        ["z_image_classic_painting.safetensors"] = new(
            "Hội họa cổ điển",
            "Nét cọ sơn dầu giàu chiều sâu, ánh sáng cổ điển đậm chất tranh nghệ thuật.",
            "art",
            0.60,
            IsRecommended: false,
            SortOrder: 5),
    };

    public LoraCatalogService(IConfiguration config)
    {
        _config = config;
    }

    public string? ResolveLorasFolder()
    {
        var configured = _config["ComfyUI:LorasPath"];
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return Path.GetFullPath(configured);

        var candidates = new List<string>();
        var envHome = Environment.GetEnvironmentVariable("GULA_STUDIO_HOME");
        if (!string.IsNullOrWhiteSpace(envHome))
            candidates.Add(Path.Combine(envHome.Trim(), "ComfyUI", "models", "loras"));

        var localAppConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CharacterConceptAI", "workspace-root.txt");
        try
        {
            if (File.Exists(localAppConfig))
                candidates.Add(Path.Combine(File.ReadAllText(localAppConfig).Trim(), "ComfyUI", "models", "loras"));
        }
        catch { }

        var current = AppContext.BaseDirectory;
        for (var depth = 0; depth < 7 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            candidates.Add(Path.Combine(current, "ComfyUI", "models", "loras"));
            current = Directory.GetParent(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName;
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GulaStudio", "ComfyUI", "models", "loras"));

        return candidates.FirstOrDefault(Directory.Exists);
    }

    public LoraCatalogResponse ListAvailable()
    {
        var folder = ResolveLorasFolder();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new LoraCatalogResponse([], folder ?? string.Empty);

        var items = new List<(int Order, LoraCatalogItemDto Dto)>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.safetensors", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1024) continue;

            var name = info.Name;
            if (CuratedMetadata.TryGetValue(name, out var meta))
            {
                items.Add((meta.SortOrder, new LoraCatalogItemDto(
                    FileName: name,
                    DisplayNameVi: meta.DisplayNameVi,
                    DescriptionVi: meta.DescriptionVi,
                    Category: meta.Category,
                    DefaultStrength: meta.DefaultStrength,
                    SizeBytes: info.Length,
                    IsRecommended: meta.IsRecommended)));
            }
            else
            {
                var cleanTitle = Path.GetFileNameWithoutExtension(name).Replace('_', ' ').Replace('-', ' ');
                items.Add((100, new LoraCatalogItemDto(
                    FileName: name,
                    DisplayNameVi: cleanTitle,
                    DescriptionVi: "LoRA tùy chỉnh trong thư mục ComfyUI/models/loras.",
                    Category: "custom",
                    DefaultStrength: 0.55,
                    SizeBytes: info.Length,
                    IsRecommended: false)));
            }
        }

        var ordered = items
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Dto.DisplayNameVi, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Dto)
            .ToList();

        return new LoraCatalogResponse(ordered, folder);
    }

    public bool TryValidateLora(string fileName, out string normalizedFileName)
    {
        normalizedFileName = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var trimmed = fileName.Trim();
        if (trimmed != Path.GetFileName(trimmed) ||
            !trimmed.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("..", StringComparison.Ordinal))
            return false;

        var folder = ResolveLorasFolder();
        if (string.IsNullOrWhiteSpace(folder)) return false;

        var full = Path.GetFullPath(Path.Combine(folder, trimmed));
        if (!full.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(full)) return false;

        normalizedFileName = Path.GetFileName(full);
        return true;
    }
}
