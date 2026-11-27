using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CharacterConceptAI.Desktop.Services;

public enum SetupInstallKind
{
    DirectFiles,
    ComfyUiRuntime,
    GulaCustomNodes,
    LlamaServerAndQwenGguf
}

public sealed record SetupFileSpec(
    string DisplayName,
    string DownloadUrl,
    string RelativePath,
    long MinValidBytes = 1024);

public sealed partial class SetupPackageItem : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string NameZh { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string TierBadge { get; init; } = "Khuyên dùng";
    public string Description { get; init; } = string.Empty;
    public string DescriptionEn { get; init; } = string.Empty;
    public string DescriptionZh { get; init; } = string.Empty;
    public string SizeLabel { get; init; } = string.Empty;
    public long EstimatedBytes { get; init; }
    public bool IsEssential { get; init; }
    public bool IsRecommended { get; init; }
    public SetupInstallKind Kind { get; init; } = SetupInstallKind.DirectFiles;
    public IReadOnlyList<SetupFileSpec> Files { get; init; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionButtonLabel))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionButtonLabel))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeText))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isInstalling;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _detailStatus = "Chưa kiểm tra";

    [ObservableProperty]
    private string _speedLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool CanInstall => !IsInstalling;
    public string LocalizedName => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En when !string.IsNullOrWhiteSpace(NameEn) => NameEn,
        Infrastructure.Settings.AppLanguage.Zh when !string.IsNullOrWhiteSpace(NameZh) => NameZh,
        _ => Name
    };
    public string LocalizedDescription => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En when !string.IsNullOrWhiteSpace(DescriptionEn) => DescriptionEn,
        Infrastructure.Settings.AppLanguage.Zh when !string.IsNullOrWhiteSpace(DescriptionZh) => DescriptionZh,
        _ => Description
    };
    public string LocalizedTierBadge => IsEssential
        ? Infrastructure.Settings.LocalizationService.Instance.Get("Str.Setup.TierEssential", TierBadge)
        : IsRecommended
            ? Infrastructure.Settings.LocalizationService.Instance.Get("Str.Setup.TierRecommended", TierBadge)
            : Infrastructure.Settings.LocalizationService.Instance.Get("Str.Setup.TierOptional", TierBadge);
    public string StatusBadgeText => IsInstalling
        ? Infrastructure.Settings.LocalizationService.Instance.Get("Str.Setup.BadgeInstalling", "Đang cài...")
        : IsInstalled
            ? Infrastructure.Settings.LocalizationService.Instance.Get("Str.Setup.BadgeInstalled", "Đã cài đặt ✓")
            : Infrastructure.Settings.LocalizationService.Instance.Get("Str.Setup.BadgeMissing", "Chưa tải");
    public string ActionButtonLabel => IsInstalling
        ? Infrastructure.Settings.LocalizationService.Instance.Get("Str.Setup.BtnDownloading", "Đang tải...")
        : IsInstalled
            ? Infrastructure.Settings.LocalizationService.Instance.Get("Str.Setup.BtnReinstall", "Cài lại")
            : Infrastructure.Settings.LocalizationService.Instance.Get("Str.Setup.BtnInstall", "Tải & Cài đặt");

    public void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(LocalizedName));
        OnPropertyChanged(nameof(LocalizedDescription));
        OnPropertyChanged(nameof(LocalizedTierBadge));
        OnPropertyChanged(nameof(StatusBadgeText));
        OnPropertyChanged(nameof(ActionButtonLabel));
    }
}

public sealed record EnvironmentDiagnostics(
    string GpuLabel,
    bool HasNvidiaGpu,
    string SqlStatusLabel,
    bool HasSqlServer,
    string PythonLabel,
    bool HasPython,
    string FreeDiskLabel,
    double FreeDiskGb);

/// <summary>
/// Production in-app dependency &amp; AI model downloader/installer for Gula Studio.
/// Supports HTTP Range resume (.part files), live MB/s progress, disk checks, ComfyUI portable/git setup,
/// bundled custom nodes + pip packages installation, llama-server CUDA/CPU extraction, and Whisper/HuggingFace models.
/// </summary>
public sealed class DependencySetupService : IDisposable
{
    private readonly HttpClient _http;
    private bool _disposed;

    public DependencySetupService()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        };
        _http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GulaStudio-SetupManager/1.0");
    }

    public static IReadOnlyList<SetupPackageItem> CreateCatalog() =>
    [
        new SetupPackageItem
        {
            Id = "comfyui-runtime",
            Name = "ComfyUI Portable (Môi trường AI GPU NVIDIA)",
            NameEn = "ComfyUI Portable (Windows NVIDIA CUDA Runtime)",
            NameZh = "ComfyUI Portable（Windows NVIDIA CUDA 运行环境）",
            Category = "1. Môi trường lõi",
            TierBadge = "Bắt buộc",
            Description = "Tự động tải bản ComfyUI Portable mới nhất cho Windows + PyTorch CUDA và tạo sẵn run_comfyui.bat tối ưu 6GB VRAM.",
            DescriptionEn = "Automatically downloads the latest ComfyUI Portable build for Windows + PyTorch CUDA and generates a 6 GB VRAM-tuned run_comfyui.bat launcher.",
            DescriptionZh = "自动下载最新 Windows 版 ComfyUI Portable 与 PyTorch CUDA，并生成针对 6 GB 显存优化的 run_comfyui.bat 启动脚本。",
            SizeLabel = "~1.95 GB",
            EstimatedBytes = 2_090_000_000L,
            IsEssential = true,
            IsRecommended = true,
            Kind = SetupInstallKind.ComfyUiRuntime,
            Files = [new("ComfyUI main.py", "https://api.github.com/repos/Comfy-Org/ComfyUI/releases/latest", @"ComfyUI\main.py", 128)]
        },
        new SetupPackageItem
        {
            Id = "gula-custom-nodes",
            Name = "4 Gói Custom Nodes AI của Gula Studio",
            NameEn = "4 Bundled Gula Studio Custom Nodes + Python Deps",
            NameZh = "4 套 Gula Studio 内置自定义节点 + Python 依赖",
            Category = "1. Môi trường lõi",
            TierBadge = "Bắt buộc",
            Description = "Cài đặt tự động cca_depth_control, cca_face, cca_face_identity, cca_studio_pro cùng thư viện Python (onnxruntime, opencv, scikit-image).",
            DescriptionEn = "Installs cca_depth_control, cca_face, cca_face_identity, and cca_studio_pro alongside required Python packages (onnxruntime, opencv, scikit-image).",
            DescriptionZh = "自动安装 cca_depth_control、cca_face、cca_face_identity、cca_studio_pro 节点及相关 Python 依赖包。",
            SizeLabel = "~80 KB + pip",
            EstimatedBytes = 50_000_000L,
            IsEssential = true,
            IsRecommended = true,
            Kind = SetupInstallKind.GulaCustomNodes,
            Files =
            [
                new("cca_depth_control", "bundled://cca_depth_control", @"ComfyUI\custom_nodes\cca_depth_control\__init__.py", 200),
                new("cca_face", "bundled://cca_face", @"ComfyUI\custom_nodes\cca_face\__init__.py", 200),
                new("cca_face_identity", "bundled://cca_face_identity", @"ComfyUI\custom_nodes\cca_face_identity\__init__.py", 200),
                new("cca_studio_pro", "bundled://cca_studio_pro", @"ComfyUI\custom_nodes\cca_studio_pro\__init__.py", 200),
            ]
        },
        new SetupPackageItem
        {
            Id = "core-zimage-diffusion",
            Name = "Z-Image-Turbo INT8 ConvRot (Mô hình sinh ảnh chính)",
            NameEn = "Z-Image-Turbo INT8 ConvRot (Core Diffusion Model)",
            NameZh = "Z-Image-Turbo INT8 ConvRot（核心生图模型）",
            Category = "2. Bộ mô hình lõi Z-Image-Turbo",
            TierBadge = "Bắt buộc",
            Description = "Trọng số lượng tử hóa INT8 ConvRot tối ưu chạy mượt trên RTX 4050/3060 6GB VRAM với độ sắc nét như bản gốc.",
            DescriptionEn = "Quantized INT8 ConvRot diffusion weights tuned for smooth high-fidelity rendering on RTX 4050/3060 6 GB VRAM GPUs.",
            DescriptionZh = "INT8 ConvRot 量化生图权重，专为 RTX 4050/3060 6 GB 显存优化，保持原版画质。",
            SizeLabel = "5.78 GB",
            EstimatedBytes = 6_201_000_000L,
            IsEssential = true,
            IsRecommended = true,
            Files =
            [
                new("z_image_turbo_int8_convrot.safetensors",
                    "https://huggingface.co/Comfy-Org/z_image_turbo/resolve/main/split_files/diffusion_models/z_image_turbo_int8_convrot.safetensors",
                    @"ComfyUI\models\diffusion_models\z_image_turbo_int8_convrot.safetensors",
                    1_000_000_000L)
            ]
        },
        new SetupPackageItem
        {
            Id = "core-qwen-encoder",
            Name = "Qwen3-4B FP8 Text Encoder (Hiểu prompt Tiếng Việt & Anh)",
            NameEn = "Qwen3-4B FP8 Text Encoder (Multilingual Prompting)",
            NameZh = "Qwen3-4B FP8 文本编码器（多语言提示词理解）",
            Category = "2. Bộ mô hình lõi Z-Image-Turbo",
            TierBadge = "Bắt buộc",
            Description = "Bộ mã hóa ngôn ngữ Qwen3-4B FP8 giúp hiểu chính xác mô tả tiếng Việt, tiếng Anh và dựng chữ chuẩn trong ảnh.",
            DescriptionEn = "Qwen3-4B FP8 language encoder for accurate English, Vietnamese, and Chinese prompt comprehension and verbatim poster text rendering.",
            DescriptionZh = "Qwen3-4B FP8 文本编码器，精准理解中/英/越提示词并支持画面文字渲染。",
            SizeLabel = "5.25 GB",
            EstimatedBytes = 5_632_000_000L,
            IsEssential = true,
            IsRecommended = true,
            Files =
            [
                new("qwen_3_4b_fp8_mixed.safetensors",
                    "https://huggingface.co/Comfy-Org/z_image_turbo/resolve/main/split_files/text_encoders/qwen_3_4b_fp8_mixed.safetensors",
                    @"ComfyUI\models\text_encoders\qwen_3_4b_fp8_mixed.safetensors",
                    1_000_000_000L)
            ]
        },
        new SetupPackageItem
        {
            Id = "core-vae",
            Name = "Autoencoder VAE (ae.safetensors)",
            NameEn = "Autoencoder VAE (ae.safetensors)",
            NameZh = "图像解码器 VAE (ae.safetensors)",
            Category = "2. Bộ mô hình lõi Z-Image-Turbo",
            TierBadge = "Bắt buộc",
            Description = "Bộ giải mã hình ảnh VAE chuẩn cho Z-Image-Turbo, cho màu sắc sống động và chi tiết sắc nét.",
            DescriptionEn = "Official Z-Image-Turbo image VAE decoder for rich tonal color accuracy and fine edge reconstruction.",
            DescriptionZh = "Z-Image-Turbo 标准图像解码器，还原细腻色彩与锐利细节。",
            SizeLabel = "320 MB",
            EstimatedBytes = 335_000_000L,
            IsEssential = true,
            IsRecommended = true,
            Files =
            [
                new("ae.safetensors",
                    "https://huggingface.co/Comfy-Org/z_image_turbo/resolve/main/split_files/vae/ae.safetensors",
                    @"ComfyUI\models\vae\ae.safetensors",
                    100_000_000L)
            ]
        },
        new SetupPackageItem
        {
            Id = "controlnet-union21",
            Name = "ControlNet Union 2.1 Lite (Dáng 3D · Inpainting · Outpainting)",
            NameEn = "ControlNet Union 2.1 Lite (3D Pose · Inpainting · Outpainting)",
            NameZh = "ControlNet Union 2.1 Lite（3D 姿势 · 局部重绘 · 扩图）",
            Category = "3. Điều khiển nâng cao & Hậu kỳ Studio Pro",
            TierBadge = "Khuyên dùng",
            Description = "Điều khiển tư thế 3D theo bản đồ chiều sâu, chỉnh sửa cục bộ (Inpainting) và mở rộng khung hình (Outpainting) chỉ trong 8 bước.",
            DescriptionEn = "8-step multi-modal guidance for 3D Depth pose control, Magic Brush inpainting, and seamless canvas outpainting.",
            DescriptionZh = "8 步多功能控制模型，支持 3D 深度姿势引导、魔法画笔局部重绘与无痕扩图。",
            SizeLabel = "1.92 GB",
            EstimatedBytes = 2_017_000_000L,
            IsEssential = false,
            IsRecommended = true,
            Files =
            [
                new("Z-Image-Turbo-Fun-Controlnet-Union-2.1-lite-2602-8steps.safetensors",
                    "https://huggingface.co/alibaba-pai/Z-Image-Turbo-Fun-Controlnet-Union-2.1/resolve/main/Z-Image-Turbo-Fun-Controlnet-Union-2.1-lite-2602-8steps.safetensors",
                    @"ComfyUI\models\model_patches\Z-Image-Turbo-Fun-Controlnet-Union-2.1-lite-2602-8steps.safetensors",
                    500_000_000L)
            ]
        },
        new SetupPackageItem
        {
            Id = "face-identity-pack",
            Name = "Khóa Danh Tính Khuôn Mặt 0 VRAM (SCRFD · ArcFace · HyperSwap · GFPGAN)",
            NameEn = "0-VRAM CPU Face Identity Pack (SCRFD · ArcFace · HyperSwap · GFPGAN)",
            NameZh = "0 显存 CPU 人脸锁定包（SCRFD · ArcFace · HyperSwap · GFPGAN）",
            Category = "3. Điều khiển nâng cao & Hậu kỳ Studio Pro",
            TierBadge = "Khuyên dùng",
            Description = "Trọn bộ 5 mô hình ONNX chạy hoàn toàn trên CPU giúp đồng nhất gương mặt nhân vật qua nhiều phân cảnh mà không tốn VRAM.",
            DescriptionEn = "Complete 5-model ONNX Runtime CPU suite preserving multi-angle facial identity across storyboard scenes at 0 MB GPU VRAM.",
            DescriptionZh = "全套 5 个纯 CPU ONNX 模型，在 0 显存占用下实现跨分镜多角度人脸一致性。",
            SizeLabel = "892 MB",
            EstimatedBytes = 935_000_000L,
            IsEssential = false,
            IsRecommended = true,
            Files =
            [
                new("det_10g.onnx (SCRFD)",
                    "https://huggingface.co/public-data/insightface/resolve/main/models/buffalo_l/det_10g.onnx",
                    @"ComfyUI\models\cca_face_identity\det_10g.onnx",
                    5_000_000L),
                new("w600k_r50.onnx (ArcFace)",
                    "https://huggingface.co/public-data/insightface/resolve/main/models/buffalo_l/w600k_r50.onnx",
                    @"ComfyUI\models\cca_face_identity\w600k_r50.onnx",
                    50_000_000L),
                new("hyperswap_1c_256.onnx (HyperSwap 256)",
                    "https://huggingface.co/facefusion/models-3.3.0/resolve/main/hyperswap_1c_256.onnx",
                    @"ComfyUI\models\cca_face_identity\hyperswap_1c_256.onnx",
                    100_000_000L),
                new("GFPGANv1.4.onnx (Face Restore)",
                    "https://huggingface.co/facefusion/models-3.0.0/resolve/main/gfpgan_1.4.onnx",
                    @"ComfyUI\models\cca_face_identity\GFPGANv1.4.onnx",
                    100_000_000L),
                new("face_detection_yunet_2023mar.onnx (YuNet)",
                    "https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx",
                    @"ComfyUI\custom_nodes\cca_face\face_detection_yunet_2023mar.onnx",
                    100_000L)
            ]
        },
        new SetupPackageItem
        {
            Id = "studio-pro-rmbg",
            Name = "Tách Nền Bria RMBG-1.4 ONNX (PNG Trong Suốt 0 VRAM)",
            NameEn = "Bria RMBG-1.4 ONNX Background Removal (0-VRAM Alpha PNG)",
            NameZh = "Bria RMBG-1.4 ONNX 透明抠图（0 显存 PNG）",
            Category = "3. Điều khiển nâng cao & Hậu kỳ Studio Pro",
            TierBadge = "Khuyên dùng",
            Description = "Tách nền alpha chính xác từng sợi tóc trên CPU, xuất PNG trong suốt phục vụ ghép poster & thiết kế nhân vật.",
            DescriptionEn = "Hair-strand-accurate alpha matting on CPU for instant transparent PNG character cutouts.",
            DescriptionZh = "纯 CPU 发丝级透明通道抠图，一键导出透明底 PNG 角色素材。",
            SizeLabel = "168 MB",
            EstimatedBytes = 176_000_000L,
            IsEssential = false,
            IsRecommended = true,
            Files =
            [
                new("rmbg_1_4.onnx",
                    "https://huggingface.co/briaai/RMBG-1.4/resolve/main/onnx/model.onnx",
                    @"ComfyUI\models\background_removal\rmbg_1_4.onnx",
                    50_000_000L)
            ]
        },
        new SetupPackageItem
        {
            Id = "studio-pro-upscalers",
            Name = "Bộ Siêu Nét 2× / 4× (RealESRGAN · 4x-UltraSharp · NMKD-Siax)",
            NameEn = "Super-Resolution 2× / 4× Pack (RealESRGAN · UltraSharp · NMKD-Siax)",
            NameZh = "2× / 4× 超清放大包（RealESRGAN · UltraSharp · NMKD-Siax）",
            Category = "3. Điều khiển nâng cao & Hậu kỳ Studio Pro",
            TierBadge = "Khuyên dùng",
            Description = "Trọn bộ 3 mô hình phóng đại độ phân giải cao dùng cho Tiled Hires-Fix 2K và chế độ Siêu nét.",
            DescriptionEn = "3 high-fidelity upscalers powering 2K Tiled Hires-Fix and one-click 2×/4× super-resolution.",
            DescriptionZh = "包含 3 款高清超分模型，驱动 2K 分块高清修复与快速超分放大。",
            SizeLabel = "192 MB",
            EstimatedBytes = 201_000_000L,
            IsEssential = false,
            IsRecommended = true,
            Files =
            [
                new("RealESRGAN_x2plus.pth",
                    "https://huggingface.co/dtarnow/UPscaler/resolve/main/RealESRGAN_x2plus.pth",
                    @"ComfyUI\models\upscale_models\RealESRGAN_x2plus.pth",
                    30_000_000L),
                new("4x-UltraSharp.pth",
                    "https://huggingface.co/Kim2091/UltraSharp/resolve/main/4x-UltraSharp.pth",
                    @"ComfyUI\models\upscale_models\4x-UltraSharp.pth",
                    30_000_000L),
                new("4x_NMKD-Siax_200k.pth",
                    "https://huggingface.co/uwg/upscaler/resolve/main/ESRGAN/4x_NMKD-Siax_200k.pth",
                    @"ComfyUI\models\upscale_models\4x_NMKD-Siax_200k.pth",
                    30_000_000L)
            ]
        },
        new SetupPackageItem
        {
            Id = "voice-whisper",
            Name = "Whisper Large-v3 Turbo (Nói tiếng Việt thành Prompt)",
            NameEn = "Whisper Large-v3 Turbo (Offline Voice-to-Prompt STT)",
            NameZh = "Whisper Large-v3 Turbo（离线语音转提示词）",
            Category = "4. Trợ lý Giọng nói & Nâng cấp Prompt (Tùy chọn)",
            TierBadge = "Tùy chọn",
            Description = "Nhận diện giọng nói ngoại tuyến siêu nhanh (~0.8s) trực tiếp qua micro bằng Whisper.net Vulkan/CPU.",
            DescriptionEn = "Ultra-fast offline microphone transcription (~0.8s) via Whisper.net Vulkan/CPU with automatic VRAM release.",
            DescriptionZh = "基于 Whisper.net Vulkan/CPU 的超快离线语音转写（约 0.8 秒），空闲自动释放显存。",
            SizeLabel = "547 MB",
            EstimatedBytes = 574_000_000L,
            IsEssential = false,
            IsRecommended = false,
            Files =
            [
                new("ggml-large-v3-turbo-q5_0.bin",
                    "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin",
                    @"SpeechToText\models\ggml-large-v3-turbo-q5_0.bin",
                    200_000_000L)
            ]
        },
        new SetupPackageItem
        {
            Id = "prompt-enhancer-pack",
            Name = "Trợ lý Mở rộng Prompt AI Cục bộ (Qwen3-4B GGUF + llama-server)",
            NameEn = "Local CoT Prompt Enhancer (Qwen3-4B GGUF + llama-server)",
            NameZh = "本地 CoT 提示词增强助手（Qwen3-4B GGUF + llama-server）",
            Category = "4. Trợ lý Giọng nói & Nâng cấp Prompt (Tùy chọn)",
            TierBadge = "Tùy chọn",
            Description = "Tự động tải llama-server Windows mới nhất và Qwen3-4B Q4_K_M giúp viết lại câu lệnh điện ảnh chi tiết theo chuẩn Z-Image CoT.",
            DescriptionEn = "Downloads Windows llama-server and Qwen3-4B Q4_K_M GGUF for rich cinematic Z-Image CoT prompt expansion offline.",
            DescriptionZh = "自动下载 Windows 版 llama-server 与 Qwen3-4B Q4_K_M 模型，离线扩写电影级提示词。",
            SizeLabel = "~2.7 GB",
            EstimatedBytes = 2_890_000_000L,
            IsEssential = false,
            IsRecommended = false,
            Kind = SetupInstallKind.LlamaServerAndQwenGguf,
            Files =
            [
                new("llama-server.exe", "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=5", @"PromptEnhancer\bin\llama-server.exe", 4_096L),
                new("Qwen3-4B-Q4_K_M.gguf",
                    "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf",
                    @"PromptEnhancer\models\Qwen3-4B-Q4_K_M.gguf",
                    1_000_000_000L)
            ]
        }
    ];

    public void RefreshInstallStates(IEnumerable<SetupPackageItem> items, string workspaceRoot)
    {
        var lang = Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage;
        foreach (var item in items)
        {
            if (item.IsInstalling) continue;
            var presentCount = 0;
            long totalDiskBytes = 0;
            foreach (var f in item.Files)
            {
                var full = ResolveSafePath(workspaceRoot, f.RelativePath);
                if (File.Exists(full))
                {
                    var len = new FileInfo(full).Length;
                    if (len >= f.MinValidBytes)
                    {
                        presentCount++;
                        totalDiskBytes += len;
                    }
                }
            }

            var allPresent = item.Files.Count > 0 && presentCount == item.Files.Count;
            item.IsInstalled = allPresent;
            item.ProgressPercent = allPresent ? 100 : (item.Files.Count > 0 ? Math.Round(100.0 * presentCount / item.Files.Count, 1) : 0);
            item.SpeedLabel = string.Empty;
            if (allPresent)
            {
                item.DetailStatus = lang switch
                {
                    Infrastructure.Settings.AppLanguage.En => item.Kind == SetupInstallKind.GulaCustomNodes
                        ? $"Installed {presentCount}/{item.Files.Count} custom node packs"
                        : item.Kind == SetupInstallKind.ComfyUiRuntime
                            ? "ComfyUI runtime ready in workspace"
                            : $"Installed ({FormatBytes(totalDiskBytes)})",
                    Infrastructure.Settings.AppLanguage.Zh => item.Kind == SetupInstallKind.GulaCustomNodes
                        ? $"已安装 {presentCount}/{item.Files.Count} 套自定义节点"
                        : item.Kind == SetupInstallKind.ComfyUiRuntime
                            ? "工作目录中的 ComfyUI 已就绪"
                            : $"已安装（{FormatBytes(totalDiskBytes)}）",
                    _ => item.Kind == SetupInstallKind.GulaCustomNodes
                        ? $"Đã cài đủ {presentCount}/{item.Files.Count} gói custom node"
                        : item.Kind == SetupInstallKind.ComfyUiRuntime
                            ? "Đã sẵn sàng ComfyUI tại thư mục làm việc"
                            : $"Đã cài đủ ({FormatBytes(totalDiskBytes)})"
                };
            }
            else if (presentCount > 0)
            {
                item.DetailStatus = lang switch
                {
                    Infrastructure.Settings.AppLanguage.En => $"{presentCount}/{item.Files.Count} files present — click Download & Install to complete",
                    Infrastructure.Settings.AppLanguage.Zh => $"已有 {presentCount}/{item.Files.Count} 个文件 — 点击下载并安装补齐",
                    _ => $"Mới có {presentCount}/{item.Files.Count} tệp — bấm Tải & Cài đặt để bổ sung"
                };
            }
            else
            {
                item.DetailStatus = lang switch
                {
                    Infrastructure.Settings.AppLanguage.En => $"Not installed · Requires {item.SizeLabel}",
                    Infrastructure.Settings.AppLanguage.Zh => $"未安装 · 需要下载 {item.SizeLabel}",
                    _ => $"Chưa cài đặt · Cần tải {item.SizeLabel}"
                };
            }
        }
    }

    public async Task<EnvironmentDiagnostics> ProbeEnvironmentAsync(string workspaceRoot, CancellationToken ct = default)
    {
        var gpuTask = ProbeNvidiaGpuAsync(ct);
        var sqlTask = ProbeSqlServerAsync(ct);
        var pyTask = Task.Run(() => ProbePython(workspaceRoot), ct);
        var diskTask = Task.Run(() => ProbeFreeDisk(workspaceRoot), ct);

        await Task.WhenAll(gpuTask, sqlTask, pyTask, diskTask);
        var (gpuLabel, hasGpu) = gpuTask.Result;
        var (sqlLabel, hasSql) = sqlTask.Result;
        var (pyLabel, hasPy) = pyTask.Result;
        var (diskLabel, freeGb) = diskTask.Result;

        return new EnvironmentDiagnostics(gpuLabel, hasGpu, sqlLabel, hasSql, pyLabel, hasPy, diskLabel, freeGb);
    }

    public async Task InstallPackageAsync(SetupPackageItem item, string workspaceRoot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new InvalidOperationException("Chưa chọn thư mục cài đặt.");

        Directory.CreateDirectory(workspaceRoot);
        item.ErrorMessage = string.Empty;
        item.IsInstalling = true;
        item.ProgressPercent = 1;
        item.SpeedLabel = string.Empty;

        try
        {
            switch (item.Kind)
            {
                case SetupInstallKind.ComfyUiRuntime:
                    await InstallComfyUiRuntimeAsync(item, workspaceRoot, ct);
                    break;
                case SetupInstallKind.GulaCustomNodes:
                    await InstallGulaCustomNodesAsync(item, workspaceRoot, ct);
                    break;
                case SetupInstallKind.LlamaServerAndQwenGguf:
                    await InstallLlamaServerAndModelAsync(item, workspaceRoot, ct);
                    break;
                default:
                    await DownloadFilesSequentiallyAsync(item, workspaceRoot, ct);
                    break;
            }

            if (item.Id == "voice-whisper")
                VoiceInputService.InvalidateModelCache();

            item.IsInstalled = true;
            item.ProgressPercent = 100;
            item.SpeedLabel = string.Empty;
            item.DetailStatus = "Cài đặt hoàn tất ✓";
        }
        catch (OperationCanceledException)
        {
            item.DetailStatus = "Đã tạm dừng tải (giữ tệp .part để tiếp tục lần sau).";
            item.SpeedLabel = string.Empty;
            throw;
        }
        catch (Exception ex)
        {
            item.ErrorMessage = ex.Message;
            item.DetailStatus = $"Lỗi: {ex.Message}";
            item.SpeedLabel = string.Empty;
            throw;
        }
        finally
        {
            item.IsInstalling = false;
        }
    }

    private async Task DownloadFilesSequentiallyAsync(SetupPackageItem item, string workspaceRoot, CancellationToken ct)
    {
        EnsureSufficientDiskSpace(workspaceRoot, item.EstimatedBytes);
        var total = item.Files.Count;
        for (var idx = 0; idx < total; idx++)
        {
            var spec = item.Files[idx];
            var targetPath = ResolveSafePath(workspaceRoot, spec.RelativePath);
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length >= spec.MinValidBytes)
            {
                item.ProgressPercent = Math.Round((idx + 1) * 100.0 / total, 1);
                continue;
            }

            var filePrefix = total > 1 ? $"[{idx + 1}/{total}] {spec.DisplayName}" : spec.DisplayName;
            await DownloadFileResumableAsync(
                spec.DownloadUrl,
                targetPath,
                (pct, downloaded, totalBytes, speedBps) =>
                {
                    var overall = ((idx + pct / 100.0) / total) * 100.0;
                    item.ProgressPercent = Math.Round(Math.Clamp(overall, 0, 100), 1);
                    item.DetailStatus = totalBytes > 0
                        ? $"Đang tải {filePrefix}: {FormatBytes(downloaded)} / {FormatBytes(totalBytes)}"
                        : $"Đang tải {filePrefix}: {FormatBytes(downloaded)}";
                    item.SpeedLabel = speedBps > 0 ? $"{FormatBytes((long)speedBps)}/s" : string.Empty;
                },
                ct);
        }
    }

    private async Task InstallComfyUiRuntimeAsync(SetupPackageItem item, string workspaceRoot, CancellationToken ct)
    {
        var comfyMain = Path.Combine(workspaceRoot, "ComfyUI", "main.py");
        if (File.Exists(comfyMain))
        {
            EnsurePortableComfyLauncher(workspaceRoot);
            return;
        }

        EnsureSufficientDiskSpace(workspaceRoot, 5_500_000_000L);
        item.DetailStatus = "Đang tìm bản ComfyUI Portable mới nhất từ GitHub Releases...";
        var (assetName, downloadUrl) = await ResolveLatestComfyUiPortableAssetAsync(ct);

        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            var tempArchive = Path.Combine(workspaceRoot, "_downloads", assetName);
            await DownloadFileResumableAsync(
                downloadUrl,
                tempArchive,
                (pct, dl, total, speed) =>
                {
                    item.ProgressPercent = Math.Round(pct * 0.80, 1);
                    item.DetailStatus = total > 0
                        ? $"Đang tải {assetName}: {FormatBytes(dl)} / {FormatBytes(total)}"
                        : $"Đang tải {assetName}: {FormatBytes(dl)}";
                    item.SpeedLabel = speed > 0 ? $"{FormatBytes((long)speed)}/s" : string.Empty;
                },
                ct);

            item.SpeedLabel = string.Empty;
            item.DetailStatus = "Đang giải nén ComfyUI Portable (vui lòng đợi 1–2 phút)...";
            item.ProgressPercent = 84;
            await ExtractArchiveAsync(tempArchive, workspaceRoot, ct);

            // Flatten ComfyUI_windows_portable/ComfyUI -> <workspace>/ComfyUI if nested
            var nestedRoot = Path.Combine(workspaceRoot, "ComfyUI_windows_portable");
            if (Directory.Exists(nestedRoot))
            {
                var nestedComfy = Path.Combine(nestedRoot, "ComfyUI");
                var targetComfy = Path.Combine(workspaceRoot, "ComfyUI");
                if (Directory.Exists(nestedComfy) && !Directory.Exists(targetComfy))
                    Directory.Move(nestedComfy, targetComfy);

                var nestedPy = Path.Combine(nestedRoot, "python_embeded");
                var targetPy = Path.Combine(workspaceRoot, "python_embeded");
                if (Directory.Exists(nestedPy) && !Directory.Exists(targetPy))
                    Directory.Move(nestedPy, targetPy);
            }

            try { File.Delete(tempArchive); } catch { }
        }
        else
        {
            // Fallback: git clone ComfyUI + pip install requirements
            item.DetailStatus = "Đang tải mã nguồn ComfyUI qua Git...";
            item.ProgressPercent = 25;
            var targetComfy = Path.Combine(workspaceRoot, "ComfyUI");
            var (exit, output) = await RunProcessCaptureAsync("git", $"clone --depth 1 https://github.com/comfyanonymous/ComfyUI.git \"{targetComfy}\"", workspaceRoot, ct);
            if (exit != 0)
                throw new InvalidOperationException($"Không thể tải ComfyUI qua Git ({output.Trim()}).");

            var pyExe = GulaWorkspaceLocator.FindComfyPythonExecutable(workspaceRoot);
            if (!string.IsNullOrWhiteSpace(pyExe))
            {
                item.DetailStatus = "Đang cài đặt thư viện Python cho ComfyUI...";
                item.ProgressPercent = 70;
                await RunProcessCaptureAsync(pyExe, $"-m pip install -r \"{Path.Combine(targetComfy, "requirements.txt")}\"", targetComfy, ct);
            }
        }

        if (!File.Exists(comfyMain))
            throw new InvalidOperationException("Giải nén xong nhưng chưa tìm thấy ComfyUI\\main.py. Kiểm tra 7-Zip / tar trên Windows.");

        item.ProgressPercent = 96;
        item.DetailStatus = "Đang tạo run_comfyui.bat tối ưu 6GB+ VRAM...";
        EnsurePortableComfyLauncher(workspaceRoot);
    }

    private async Task InstallGulaCustomNodesAsync(SetupPackageItem item, string workspaceRoot, CancellationToken ct)
    {
        var sourceDir = GulaWorkspaceLocator.FindBundledCustomNodesSource();
        if (string.IsNullOrWhiteSpace(sourceDir))
            throw new DirectoryNotFoundException("Không tìm thấy thư mục BundledCustomNodes đi kèm ứng dụng.");

        var comfyCustomNodes = Path.Combine(workspaceRoot, "ComfyUI", "custom_nodes");
        Directory.CreateDirectory(comfyCustomNodes);

        var nodeNames = new[] { "cca_depth_control", "cca_face", "cca_face_identity", "cca_studio_pro" };
        for (var i = 0; i < nodeNames.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var name = nodeNames[i];
            var srcNode = Path.Combine(sourceDir, name);
            var dstNode = Path.Combine(comfyCustomNodes, name);
            if (!Directory.Exists(srcNode)) continue;

            Directory.CreateDirectory(dstNode);
            foreach (var file in Directory.GetFiles(srcNode, "*", SearchOption.TopDirectoryOnly))
            {
                var destFile = Path.Combine(dstNode, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }
            item.ProgressPercent = 15 + (i + 1) * 12;
            item.DetailStatus = $"Đã chép gói node {name}...";
        }

        var pyExe = GulaWorkspaceLocator.FindComfyPythonExecutable(workspaceRoot);
        var reqFile = Path.Combine(sourceDir, "requirements.txt");
        if (!string.IsNullOrWhiteSpace(pyExe) && File.Exists(reqFile))
        {
            item.ProgressPercent = 68;
            item.DetailStatus = "Đang cài đặt thư viện onnxruntime, opencv-python, scikit-image...";
            var (exit, output) = await RunProcessCaptureAsync(pyExe, $"-m pip install --no-warn-script-location -r \"{reqFile}\"", workspaceRoot, ct);
            if (exit != 0)
            {
                // Keep custom nodes installed, report concise pip warning if offline
                item.DetailStatus = $"Đã chép 4 gói node (lưu ý pip: kiểm tra Python/mạng nếu thiếu onnxruntime).";
            }
        }
    }

    private async Task InstallLlamaServerAndModelAsync(SetupPackageItem item, string workspaceRoot, CancellationToken ct)
    {
        EnsureSufficientDiskSpace(workspaceRoot, item.EstimatedBytes);

        var binDir = Path.Combine(workspaceRoot, "PromptEnhancer", "bin");
        var modelsDir = Path.Combine(workspaceRoot, "PromptEnhancer", "models");
        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(modelsDir);

        var llamaExe = Path.Combine(binDir, "llama-server.exe");
        if (!File.Exists(llamaExe))
        {
            item.DetailStatus = "Đang tìm bản phát hành llama-server mới nhất trên GitHub...";
            item.ProgressPercent = 3;
            var (_, hasGpu) = await ProbeNvidiaGpuAsync(ct);
            var (mainZipName, mainZipUrl, cudartName, cudartUrl) = await ResolveLatestLlamaCppAssetsAsync(hasGpu, ct);

            if (!string.IsNullOrWhiteSpace(mainZipUrl))
            {
                var dlDir = Path.Combine(workspaceRoot, "_downloads");
                Directory.CreateDirectory(dlDir);

                var mainZip = Path.Combine(dlDir, mainZipName);
                await DownloadFileResumableAsync(mainZipUrl, mainZip, (pct, dl, total, speed) =>
                {
                    item.ProgressPercent = Math.Round(5 + pct * 0.22, 1);
                    item.DetailStatus = $"Đang tải {mainZipName}: {FormatBytes(dl)} / {FormatBytes(total)}";
                    item.SpeedLabel = speed > 0 ? $"{FormatBytes((long)speed)}/s" : string.Empty;
                }, ct);

                item.SpeedLabel = string.Empty;
                item.DetailStatus = "Đang giải nén llama-server...";
                ZipFile.ExtractToDirectory(mainZip, binDir, overwriteFiles: true);
                try { File.Delete(mainZip); } catch { }

                if (hasGpu && !string.IsNullOrWhiteSpace(cudartUrl))
                {
                    var cudartZip = Path.Combine(dlDir, cudartName);
                    await DownloadFileResumableAsync(cudartUrl, cudartZip, (pct, dl, total, speed) =>
                    {
                        item.ProgressPercent = Math.Round(28 + pct * 0.14, 1);
                        item.DetailStatus = $"Đang tải CUDA runtime ({cudartName}): {FormatBytes(dl)} / {FormatBytes(total)}";
                        item.SpeedLabel = speed > 0 ? $"{FormatBytes((long)speed)}/s" : string.Empty;
                    }, ct);
                    item.SpeedLabel = string.Empty;
                    ZipFile.ExtractToDirectory(cudartZip, binDir, overwriteFiles: true);
                    try { File.Delete(cudartZip); } catch { }
                }
            }
        }

        // Download Qwen3-4B Q4_K_M GGUF
        var modelSpec = item.Files[1];
        var modelDest = ResolveSafePath(workspaceRoot, modelSpec.RelativePath);
        if (!File.Exists(modelDest) || new FileInfo(modelDest).Length < modelSpec.MinValidBytes)
        {
            await DownloadFileResumableAsync(modelSpec.DownloadUrl, modelDest, (pct, dl, total, speed) =>
            {
                item.ProgressPercent = Math.Round(44 + pct * 0.54, 1);
                item.DetailStatus = total > 0
                    ? $"Đang tải Qwen3-4B-Q4_K_M.gguf: {FormatBytes(dl)} / {FormatBytes(total)}"
                    : $"Đang tải Qwen3-4B-Q4_K_M.gguf: {FormatBytes(dl)}";
                item.SpeedLabel = speed > 0 ? $"{FormatBytes((long)speed)}/s" : string.Empty;
            }, ct);
        }

        // Write portable launcher bat
        var batPath = Path.Combine(workspaceRoot, "PromptEnhancer", "run_prompt_enhancer.bat");
        var batContent =
            "@echo off\r\n" +
            "title Gula Studio Prompt Enhancer (Qwen3-4B)\r\n" +
            "cd /d \"%~dp0bin\"\r\n" +
            "llama-server.exe --models-dir \"%~dp0models\" --host 127.0.0.1 --port 11435 --ctx-size 2048 --n-gpu-layers 99 --cache-type-k q8_0 --cache-type-v q8_0 --sleep-idle-seconds 15 --parallel 1\r\n";
        await File.WriteAllTextAsync(batPath, batContent, ct);
    }

    private async Task DownloadFileResumableAsync(
        string url,
        string destinationPath,
        Action<double, long, long, double> onProgress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var partPath = destinationPath + ".part";
        long existingBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingBytes > 0)
            req.Headers.Range = new RangeHeaderValue(existingBytes, null);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Already completely downloaded in .part
            File.Move(partPath, destinationPath, overwrite: true);
            onProgress(100, existingBytes, existingBytes, 0);
            return;
        }

        resp.EnsureSuccessStatusCode();

        var isResuming = resp.StatusCode == HttpStatusCode.PartialContent && existingBytes > 0;
        if (!isResuming) existingBytes = 0;

        var contentLength = resp.Content.Headers.ContentLength ?? -1;
        var totalBytes = contentLength > 0 ? existingBytes + contentLength : -1;

        await using (var remoteStream = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fileStream = new FileStream(
            partPath,
            isResuming ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 131072,
            useAsync: true))
        {
            var buffer = new byte[131072];
            long downloaded = existingBytes;
            long windowBytes = 0;
            var sw = Stopwatch.StartNew();
            var lastUiMs = 0L;
            var lastSpeedMs = 0L;
            double currentSpeed = 0;

            int read;
            while ((read = await remoteStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                windowBytes += read;

                var nowMs = sw.ElapsedMilliseconds;
                if (nowMs - lastSpeedMs >= 500)
                {
                    currentSpeed = windowBytes / ((nowMs - lastSpeedMs) / 1000.0);
                    windowBytes = 0;
                    lastSpeedMs = nowMs;
                }

                if (nowMs - lastUiMs >= 120)
                {
                    lastUiMs = nowMs;
                    var pct = totalBytes > 0 ? Math.Clamp(downloaded * 100.0 / totalBytes, 0, 99.9) : 50;
                    onProgress(pct, downloaded, totalBytes, currentSpeed);
                }
            }
            await fileStream.FlushAsync(ct);
        }

        File.Move(partPath, destinationPath, overwrite: true);
        var finalLen = new FileInfo(destinationPath).Length;
        onProgress(100, finalLen, finalLen, 0);
    }

    private async Task<(string Name, string Url)> ResolveLatestComfyUiPortableAssetAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("https://api.github.com/repos/Comfy-Org/ComfyUI/releases/latest", ct);
            if (!resp.IsSuccessStatusCode) return (string.Empty, string.Empty);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? string.Empty;
                    if (name.Equals("ComfyUI_windows_portable_nvidia.7z", StringComparison.OrdinalIgnoreCase) ||
                        (name.StartsWith("ComfyUI_windows_portable_nvidia", StringComparison.OrdinalIgnoreCase) &&
                         name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) &&
                         !name.Contains("arm64", StringComparison.OrdinalIgnoreCase)))
                    {
                        var url = a.GetProperty("browser_download_url").GetString() ?? string.Empty;
                        return (name, url);
                    }
                }
            }
        }
        catch { }
        return (string.Empty, string.Empty);
    }

    private async Task<(string MainName, string MainUrl, string CudartName, string CudartUrl)> ResolveLatestLlamaCppAssetsAsync(bool preferCuda, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=5", ct);
            if (!resp.IsSuccessStatusCode) return (string.Empty, string.Empty, string.Empty, string.Empty);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (!rel.TryGetProperty("assets", out var assets)) continue;
                string mainName = string.Empty, mainUrl = string.Empty, cudartName = string.Empty, cudartUrl = string.Empty;
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? string.Empty;
                    var url = a.GetProperty("browser_download_url").GetString() ?? string.Empty;
                    if (preferCuda && name.Contains("bin-win-cuda-12.4-x64.zip", StringComparison.OrdinalIgnoreCase) && name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase))
                    {
                        mainName = name; mainUrl = url;
                    }
                    else if (preferCuda && name.StartsWith("cudart-llama-bin-win-cuda-12.4-x64", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        cudartName = name; cudartUrl = url;
                    }
                    else if (!preferCuda && name.Contains("bin-win-cpu-x64.zip", StringComparison.OrdinalIgnoreCase) && name.StartsWith("llama-", StringComparison.OrdinalIgnoreCase))
                    {
                        mainName = name; mainUrl = url;
                    }
                }
                if (!string.IsNullOrWhiteSpace(mainUrl))
                    return (mainName, mainUrl, cudartName, cudartUrl);
            }
        }
        catch { }
        return (string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private static async Task ExtractArchiveAsync(string archivePath, string destDir, CancellationToken ct)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destDir, overwriteFiles: true);
            return;
        }

        // Windows 11 built-in bsdtar supports .7z natively; also check 7-Zip if installed
        var sevenZipCandidates = new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe"
        };
        var sevenZip = sevenZipCandidates.FirstOrDefault(File.Exists);
        if (sevenZip != null)
        {
            var (code, err) = await RunProcessCaptureAsync(sevenZip, $"x \"{archivePath}\" -o\"{destDir}\" -y", destDir, ct);
            if (code == 0) return;
            throw new InvalidOperationException($"Giải nén 7-Zip thất bại: {err.Trim()}");
        }

        var (tarCode, tarErr) = await RunProcessCaptureAsync("tar", $"-xf \"{archivePath}\" -C \"{destDir}\"", destDir, ct);
        if (tarCode != 0)
            throw new InvalidOperationException($"Không thể tự giải nén tệp .7z ({tarErr.Trim()}). Hãy cài 7-Zip (winget install 7zip.7zip) rồi bấm thử lại.");
    }

    private static void EnsurePortableComfyLauncher(string workspaceRoot)
    {
        var batPath = Path.Combine(workspaceRoot, "run_comfyui.bat");
        if (File.Exists(batPath)) return;

        const string template =
            "@echo off\r\n" +
            "chcp 65001 >nul\r\n" +
            "title ComfyUI - Gula Studio\r\n" +
            "set \"ROOT=%~dp0\"\r\n" +
            "cd /d \"%ROOT%ComfyUI\"\r\n" +
            "set \"PY_EXE=\"\r\n" +
            "if exist \"%ROOT%python_embeded\\python.exe\" set \"PY_EXE=%ROOT%python_embeded\\python.exe\"\r\n" +
            "if not defined PY_EXE if exist \"%ROOT%ComfyUI\\venv\\Scripts\\python.exe\" set \"PY_EXE=%ROOT%ComfyUI\\venv\\Scripts\\python.exe\"\r\n" +
            "if not defined PY_EXE if exist \"%ROOT%ComfyUI\\.venv\\Scripts\\python.exe\" set \"PY_EXE=%ROOT%ComfyUI\\.venv\\Scripts\\python.exe\"\r\n" +
            "if not defined PY_EXE set \"PY_EXE=python\"\r\n" +
            "\"%PY_EXE%\" main.py --listen 127.0.0.1 --port 8188 --windows-standalone-build --use-pytorch-cross-attention --fast fp16_accumulation --reserve-vram 0.8 --disable-pinned-memory --disable-auto-launch\r\n" +
            "if /i \"%~1\" neq \"--no-pause\" pause\r\n";
        File.WriteAllText(batPath, template);
    }

    public async Task<(bool Ok, string Message)> InstallSqlServerExpressViaWingetAsync(CancellationToken ct)
    {
        try
        {
            var (code, output) = await RunProcessCaptureAsync(
                "winget",
                "install --id Microsoft.SQLServer.2022.Express --accept-package-agreements --accept-source-agreements --silent",
                Environment.CurrentDirectory,
                ct);
            if (code == 0)
                return (true, "Đã cài đặt SQL Server 2022 Express thành công qua winget.");
            return (false, $"winget trả mã {code}. Đang mở trang tải chính thức Microsoft SQL Server Express...");
        }
        catch
        {
            return (false, "Không tìm thấy winget. Đang mở trang tải Microsoft SQL Server Express...");
        }
    }

    private static async Task<(string Label, bool HasGpu)> ProbeNvidiaGpuAsync(CancellationToken ct)
    {
        try
        {
            var (code, output) = await RunProcessCaptureAsync(
                "nvidia-smi",
                "--query-gpu=name,memory.total --format=csv,noheader",
                Environment.CurrentDirectory,
                ct,
                timeoutMs: 4000);
            if (code == 0 && !string.IsNullOrWhiteSpace(output))
            {
                var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                if (!string.IsNullOrWhiteSpace(line))
                    return (line, true);
            }
        }
        catch { }
        return ("Chưa phát hiện GPU NVIDIA (cần RTX/GTX CUDA)", false);
    }

    private static async Task<(string Label, bool HasSql)> ProbeSqlServerAsync(CancellationToken ct)
    {
        try
        {
            var (code, output) = await RunProcessCaptureAsync(
                "powershell.exe",
                "-NoProfile -Command \"(Get-Service '*MSSQL*' -ErrorAction SilentlyContinue | Select-Object -First 1 | ForEach-Object { $_.Name + ':' + $_.Status })\"",
                Environment.CurrentDirectory,
                ct,
                timeoutMs: 4000);
            var trimmed = output.Trim();
            if (code == 0 && !string.IsNullOrWhiteSpace(trimmed))
                return ($"SQL Server ({trimmed})", true);
        }
        catch { }
        return ("Chưa thấy SQL Server Express", false);
    }

    private static (string Label, bool HasPython) ProbePython(string workspaceRoot)
    {
        var lang = Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage;
        var py = GulaWorkspaceLocator.FindComfyPythonExecutable(workspaceRoot);
        if (!string.IsNullOrWhiteSpace(py))
        {
            var dirName = Path.GetFileName(Path.GetDirectoryName(py));
            return lang switch
            {
                Infrastructure.Settings.AppLanguage.En => ($"Python Ready ({dirName})", true),
                Infrastructure.Settings.AppLanguage.Zh => ($"Python 已就绪 ({dirName})", true),
                _ => ($"Python sẵn sàng ({dirName})", true)
            };
        }
        return lang switch
        {
            Infrastructure.Settings.AppLanguage.En => ("Python not installed (bundled with ComfyUI Portable)", false),
            Infrastructure.Settings.AppLanguage.Zh => ("未检测到 Python（将随 ComfyUI Portable 自动安装）", false),
            _ => ("Chưa cài Python (sẽ tự kèm khi tải ComfyUI Portable)", false)
        };
    }

    private static (string Label, double FreeGb) ProbeFreeDisk(string workspaceRoot)
    {
        var lang = Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(workspaceRoot));
            if (!string.IsNullOrWhiteSpace(root))
            {
                var di = new DriveInfo(root);
                var freeGb = di.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                var drive = di.Name.TrimEnd('\\');
                var label = lang switch
                {
                    Infrastructure.Settings.AppLanguage.En => $"Drive {drive} · {freeGb:F1} GB free",
                    Infrastructure.Settings.AppLanguage.Zh => $"磁盘 {drive} · 可用 {freeGb:F1} GB",
                    _ => $"Ổ {drive} còn trống {freeGb:F1} GB"
                };
                return (label, freeGb);
            }
        }
        catch { }
        return (lang == Infrastructure.Settings.AppLanguage.En ? "Disk space unavailable" : "Không đo được dung lượng đĩa", 100);
    }

    private static void EnsureSufficientDiskSpace(string workspaceRoot, long requiredBytes)
    {
        if (requiredBytes <= 0) return;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(workspaceRoot));
            if (string.IsNullOrWhiteSpace(root)) return;
            var di = new DriveInfo(root);
            var neededWithBuffer = requiredBytes + 512_000_000L; // +512 MB safety buffer
            if (di.AvailableFreeSpace < neededWithBuffer)
            {
                throw new IOException(
                    $"Ổ đĩa {di.Name.TrimEnd('\\')} chỉ còn {FormatBytes(di.AvailableFreeSpace)}, cần tối thiểu {FormatBytes(neededWithBuffer)} để tải an toàn.");
            }
        }
        catch (IOException) { throw; }
        catch { }
    }

    private static string ResolveSafePath(string workspaceRoot, string relativePath)
    {
        var rootFull = Path.GetFullPath(workspaceRoot);
        var full = Path.GetFullPath(Path.Combine(rootFull, relativePath));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Đường dẫn đích không hợp lệ: {relativePath}");
        return full;
    }

    private static async Task<(int ExitCode, string Output)> RunProcessCaptureAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken ct,
        int timeoutMs = 600_000)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.Start();
        var stdOutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var output = (await stdOutTask) + "\n" + (await stdErrTask);
        return (proc.ExitCode, output);
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824L) return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576L) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
