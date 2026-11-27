using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using CharacterConceptAI.Desktop.Services;
using CharacterConceptAI.Shared.Contracts.Generations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CharacterConceptAI.Desktop.ViewModels;

public sealed partial class StoryFaceAngleVm : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    private string _angleLabel;

    [ObservableProperty]
    private string? _imagePath;

    [ObservableProperty]
    private Guid? _conceptVersionId;

    public Guid? UploadedProjectId { get; set; }

    [ObservableProperty]
    private BitmapImage? _preview;

    public StoryFaceAngleVm(Guid id, string angleLabel)
    {
        Id = id;
        _angleLabel = angleLabel;
    }
}

public sealed partial class StoryCharacterVm : ObservableObject
{
    public const int MaxTotalFaces = 5;
    public const int MaxExtraFaces = 4;

    public Guid Id { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _visualDna;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBodyDna))]
    [NotifyPropertyChangedFor(nameof(ProfileSummaryBadge))]
    private string _bodyDna = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileSummaryBadge))]
    private bool _enableBodyDnaLock = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFaceAnchor))]
    [NotifyPropertyChangedFor(nameof(TotalFaceCount))]
    [NotifyPropertyChangedFor(nameof(CanAddExtraFace))]
    [NotifyPropertyChangedFor(nameof(FaceCountBadge))]
    [NotifyPropertyChangedFor(nameof(FacePackSummary))]
    [NotifyPropertyChangedFor(nameof(FaceLockBadge))]
    private string? _faceImagePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFaceAnchor))]
    [NotifyPropertyChangedFor(nameof(TotalFaceCount))]
    [NotifyPropertyChangedFor(nameof(CanAddExtraFace))]
    [NotifyPropertyChangedFor(nameof(FaceCountBadge))]
    [NotifyPropertyChangedFor(nameof(FacePackSummary))]
    [NotifyPropertyChangedFor(nameof(FaceLockBadge))]
    private Guid? _faceConceptVersionId;

    public Guid? FaceUploadedProjectId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFaceAnchor))]
    [NotifyPropertyChangedFor(nameof(TotalFaceCount))]
    [NotifyPropertyChangedFor(nameof(CanAddExtraFace))]
    [NotifyPropertyChangedFor(nameof(FaceCountBadge))]
    [NotifyPropertyChangedFor(nameof(FacePackSummary))]
    [NotifyPropertyChangedFor(nameof(FaceLockBadge))]
    private BitmapImage? _facePreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FaceLockBadge))]
    [NotifyPropertyChangedFor(nameof(ProfileSummaryBadge))]
    private bool _enableFaceLock = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FaceStrengthPercent))]
    private double _faceSwapStrength = 0.90;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestoreStrengthPercent))]
    private double _faceRestoreStrength = 0.40;

    [ObservableProperty]
    private long? _anchorSeed;

    [ObservableProperty]
    private bool _isGeneratingPortrait;

    [ObservableProperty]
    private string _portraitProgressLabel = "Đang vẽ…";

    public ObservableCollection<StoryFaceAngleVm> ExtraFaces { get; } = [];

    public bool HasFaceAnchor => FaceConceptVersionId.HasValue || FacePreview != null || !string.IsNullOrWhiteSpace(FaceImagePath);
    public bool HasExtraFaces => ExtraFaces.Count > 0;
    public bool HasBodyDna => !string.IsNullOrWhiteSpace(BodyDna);
    public int TotalFaceCount => (HasFaceAnchor ? 1 : 0) + ExtraFaces.Count;
    public bool CanAddExtraFace => TotalFaceCount < MaxTotalFaces;
    public string FaceCountBadge => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En => $"{TotalFaceCount}/{MaxTotalFaces} imgs",
        Infrastructure.Settings.AppLanguage.Zh => $"{TotalFaceCount}/{MaxTotalFaces} 张",
        _ => $"{TotalFaceCount}/{MaxTotalFaces} ảnh"
    };
    public string FaceStrengthPercent => $"{Math.Round(FaceSwapStrength * 100):0}%";
    public string RestoreStrengthPercent => $"{Math.Round(FaceRestoreStrength * 100):0}%";
    public string ProfileSummaryBadge =>
        (HasBodyDna && EnableBodyDnaLock) ? $"{FaceLockBadge} · +Body DNA" : FaceLockBadge;
    public string FacePackSummary => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En => TotalFaceCount switch
        {
            0 => "Upload or generate a front portrait, then add 3/4 angles.",
            1 => "1 front portrait · Add 3/4 angles for stronger multi-view consistency.",
            _ => $"{TotalFaceCount} face angles locked · Auto-synced across head turns."
        },
        Infrastructure.Settings.AppLanguage.Zh => TotalFaceCount switch
        {
            0 => "上传或生成正面肖像，随后添加 3/4 侧面角度。",
            1 => "1 张正面图 · 添加 3/4 角度可显著提升多视角一致性。",
            _ => $"已锁定 {TotalFaceCount} 个面部角度 · 自动匹配转头视角。"
        },
        _ => TotalFaceCount switch
        {
            0 => "Tải hoặc tự vẽ ảnh chính diện, sau đó thêm góc nghiêng 3/4.",
            1 => "1 ảnh chính diện · Thêm góc nghiêng 3/4 để giữ mặt chuẩn hơn.",
            _ => $"Đã khóa {TotalFaceCount} góc mặt · Tự động đồng bộ theo góc quay đầu."
        }
    };
    public string FaceLockBadge => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En => !EnableFaceLock ? "Face lock off"
            : TotalFaceCount >= 2 ? $"Multi-angle ({TotalFaceCount}/{MaxTotalFaces})"
            : HasFaceAnchor ? "Face locked (1/5)"
            : "No face anchor",
        Infrastructure.Settings.AppLanguage.Zh => !EnableFaceLock ? "已关闭锁脸"
            : TotalFaceCount >= 2 ? $"多角度锁定 ({TotalFaceCount}/{MaxTotalFaces})"
            : HasFaceAnchor ? "已锁定人脸 (1/5)"
            : "未设人脸",
        _ => !EnableFaceLock ? "Tắt khóa mặt"
            : TotalFaceCount >= 2 ? $"🔒 Đa góc ({TotalFaceCount}/{MaxTotalFaces})"
            : HasFaceAnchor ? "🔒 Đã khóa mặt (1/5)"
            : "Chưa có ảnh mặt"
    };

    private const string LegacyDnaPlaceholder = "mô tả ngoại hình cố định: khuôn mặt, kiểu tóc, màu mắt, trang phục đặc trưng";

    public string BuildFullDna(bool? includeBodyOverride = null)
    {
        var parts = new List<string>();
        var faceDna = VisualDna?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(faceDna) && !faceDna.StartsWith(LegacyDnaPlaceholder, StringComparison.OrdinalIgnoreCase))
            parts.Add(faceDna.TrimEnd('.', ','));
        var includeBody = includeBodyOverride ?? EnableBodyDnaLock;
        if (includeBody && !string.IsNullOrWhiteSpace(BodyDna))
            parts.Add(BodyDna.Trim().TrimEnd('.', ','));
        return string.Join(", ", parts);
    }

    public StoryCharacterVm(Guid id, string name, string visualDna)
    {
        Id = id;
        _name = name;
        _visualDna = visualDna;
        ExtraFaces.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasExtraFaces));
            OnPropertyChanged(nameof(TotalFaceCount));
            OnPropertyChanged(nameof(CanAddExtraFace));
            OnPropertyChanged(nameof(FaceCountBadge));
            OnPropertyChanged(nameof(FacePackSummary));
            OnPropertyChanged(nameof(FaceLockBadge));
            OnPropertyChanged(nameof(ProfileSummaryBadge));
        };
    }
}

public sealed partial class StorySceneVm : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SceneNumberLabel))]
    private int _orderIndex;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private Guid? _characterId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecondaryCharacter))]
    private Guid? _secondaryCharacterId;

    public bool HasSecondaryCharacter => SecondaryCharacterId.HasValue;

    [ObservableProperty]
    private string _scenePrompt;

    [ObservableProperty]
    private string _dialogue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDepthPose))]
    private string? _depthPoseId;

    [ObservableProperty]
    private string? _depthPoseLabel;

    [ObservableProperty]
    private BitmapImage? _depthPoseThumb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DepthStrengthLabel))]
    private double _depthStrength = 0.92;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutfit))]
    private string? _outfitId;

    [ObservableProperty]
    private string? _outfitLabel;

    [ObservableProperty]
    private BitmapImage? _outfitThumb;

    [ObservableProperty]
    private long? _seed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpscale))]
    private Guid? _outputAssetId;

    [ObservableProperty]
    private string? _localImagePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpscaled))]
    [NotifyPropertyChangedFor(nameof(CanUpscale))]
    private string? _upscaledImagePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayImage))]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private BitmapImage? _originalImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayImage))]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    [NotifyPropertyChangedFor(nameof(HasUpscaled))]
    [NotifyPropertyChangedFor(nameof(CanUpscale))]
    private BitmapImage? _upscaledImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpscale))]
    private bool _isGenerating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpscale))]
    private bool _isUpscaling;

    [ObservableProperty]
    private bool _isEnhancing;

    [ObservableProperty]
    private string _statusText = "Chưa vẽ";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private DateTimeOffset? _generatedAt;

    public int Width { get; set; } = 896;
    public int Height { get; set; } = 1152;

    public string SceneNumberLabel => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En => $"Scene {OrderIndex + 1}",
        Infrastructure.Settings.AppLanguage.Zh => $"分镜 {OrderIndex + 1}",
        _ => $"Cảnh {OrderIndex + 1}"
    };
    public bool HasDepthPose => !string.IsNullOrWhiteSpace(DepthPoseId);
    public bool HasOutfit => !string.IsNullOrWhiteSpace(OutfitId);
    public string DepthStrengthLabel => $"Depth {DepthStrength:0.00}";
    public BitmapImage? DisplayImage => UpscaledImage ?? OriginalImage;
    public bool HasImage => DisplayImage != null;
    public bool HasUpscaled => UpscaledImage != null || !string.IsNullOrWhiteSpace(UpscaledImagePath);
    public bool CanUpscale => OutputAssetId.HasValue && !HasUpscaled && !IsGenerating && !IsUpscaling;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasDialogue => !string.IsNullOrWhiteSpace(Dialogue);

    partial void OnDialogueChanged(string value) => OnPropertyChanged(nameof(HasDialogue));

    public StorySceneVm(Guid id, int orderIndex, string title)
    {
        Id = id;
        _orderIndex = orderIndex;
        _title = title;
        _scenePrompt = string.Empty;
        _dialogue = string.Empty;
    }
}

public sealed partial class StoryProjectVm : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _stylePresetKey = "photoreal";

    [ObservableProperty]
    private string _aspectRatioKey = "3:4";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolutionLabel))]
    private int _width = 896;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolutionLabel))]
    private int _height = 1152;

    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ObservableCollection<StoryCharacterVm> Characters { get; } = [];
    public ObservableCollection<StorySceneVm> Scenes { get; } = [];

    public string ResolutionLabel => $"{Width} × {Height}";
    public string SummaryLabel => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En => $"{Characters.Count} characters · {Scenes.Count} scenes",
        Infrastructure.Settings.AppLanguage.Zh => $"{Characters.Count} 位角色 · {Scenes.Count} 个分镜",
        _ => $"{Characters.Count} nhân vật · {Scenes.Count} cảnh"
    };

    public StoryProjectVm(Guid id, string title, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        Id = id;
        _title = title;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Characters.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SummaryLabel));
        Scenes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SummaryLabel));
    }
}

public partial class MainViewModel
{
    private bool _storiesLoaded;
    private CancellationTokenSource? _storyBatchCts;

    public ObservableCollection<StoryProjectVm> Stories { get; } = [];
    public ObservableCollection<StoryCharacterVm> AllSavedCharacters { get; } = [];

    public void RefreshAllSavedCharacters()
    {
        var seen = new HashSet<Guid>();
        var list = new List<StoryCharacterVm>();
        foreach (var st in Stories)
        {
            foreach (var ch in st.Characters)
            {
                if (seen.Add(ch.Id))
                    list.Add(ch);
            }
        }
        AllSavedCharacters.Clear();
        foreach (var ch in list.OrderByDescending(c => c.HasFaceAnchor).ThenByDescending(c => c.HasBodyDna))
            AllSavedCharacters.Add(ch);
    }

    [RelayCommand]
    private void SelectStoryCharacter(StoryCharacterVm? character)
    {
        if (character == null) return;
        SelectedStoryCharacter = character;
    }

    [RelayCommand]
    private void UseStoryCharacterInStudio(StoryCharacterVm? character)
    {
        var target = character ?? SelectedStoryCharacter;
        if (target == null) return;
        SelectStudioCharacter(target);
        NavigateTo(0);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveStory))]
    [NotifyPropertyChangedFor(nameof(ActiveStoryCharacters))]
    [NotifyPropertyChangedFor(nameof(ActiveStoryScenes))]
    private StoryProjectVm? _activeStory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedStoryCharacter))]
    [NotifyPropertyChangedFor(nameof(SelectedStoryCharacterId))]
    private StoryCharacterVm? _selectedStoryCharacter;

    public Guid SelectedStoryCharacterId => SelectedStoryCharacter?.Id ?? Guid.Empty;

    [ObservableProperty]
    private bool _isStoryBatchGenerating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StoryReaderButtonLabel))]
    private bool _isStoryReaderMode;

    [ObservableProperty]
    private bool _isStoryLightboxOpen;

    [ObservableProperty]
    private StorySceneVm? _storyLightboxScene;

    [ObservableProperty]
    private BitmapImage? _storyLightboxImage;

    [ObservableProperty]
    private string _storyLightboxTitle = string.Empty;

    [ObservableProperty]
    private string _storyLightboxSubtitle = string.Empty;

    public bool HasActiveStory => ActiveStory != null;
    public bool HasSelectedStoryCharacter => SelectedStoryCharacter != null;
    public ObservableCollection<StoryCharacterVm>? ActiveStoryCharacters => ActiveStory?.Characters;
    public ObservableCollection<StorySceneVm>? ActiveStoryScenes => ActiveStory?.Scenes;
    public string StoryReaderButtonLabel => Infrastructure.Settings.LocalizationService.Instance.CurrentLanguage switch
    {
        Infrastructure.Settings.AppLanguage.En => IsStoryReaderMode ? "← Back to Editor" : "Story Reader",
        Infrastructure.Settings.AppLanguage.Zh => IsStoryReaderMode ? "← 返回编辑" : "阅读模式",
        _ => IsStoryReaderMode ? "← Quay lại chỉnh sửa" : "Xem dạng truyện"
    };

    private CancellationTokenSource? _storyHydrateCts;

    partial void OnActiveStoryChanged(StoryProjectVm? value)
    {
        SelectedStoryCharacter = value?.Characters.FirstOrDefault();
        if (value != null)
        {
            TrimInactiveStoryMediaRam(value);
            _ = HydrateStoryMediaAsync(value);
        }
    }

    private void TrimInactiveStoryMediaRam(StoryProjectVm active)
    {
        foreach (var st in Stories)
        {
            if (st == active) continue;
            foreach (var sc in st.Scenes)
            {
                if (sc.IsGenerating || sc == StoryLightboxScene) continue;
                if (!string.IsNullOrWhiteSpace(sc.LocalImagePath) && File.Exists(sc.LocalImagePath))
                {
                    sc.OriginalImage = null;
                    sc.UpscaledImage = null;
                }
            }
        }
    }

    public void EnsureStoriesLoaded()
    {
        if (_storiesLoaded) return;
        _storiesLoaded = true;
        Stories.Clear();

        if (IsRealSession)
        {
            var records = StoryStore.Load();
            foreach (var rec in records.OrderByDescending(r => r.UpdatedAt))
            {
                var story = new StoryProjectVm(rec.Id, rec.Title, rec.CreatedAt, rec.UpdatedAt)
                {
                    StylePresetKey = string.IsNullOrWhiteSpace(rec.StylePresetKey) ? "photoreal" : rec.StylePresetKey,
                    AspectRatioKey = string.IsNullOrWhiteSpace(rec.AspectRatioKey) ? "3:4" : rec.AspectRatioKey,
                    Width = rec.Width >= 512 ? rec.Width : 896,
                    Height = rec.Height >= 512 ? rec.Height : 1152
                };
                foreach (var ch in rec.Characters)
                {
                    var chVm = new StoryCharacterVm(ch.Id, ch.Name, ch.VisualDna)
                    {
                        BodyDna = ch.BodyDna ?? string.Empty,
                        EnableBodyDnaLock = ch.EnableBodyDnaLock,
                        FaceImagePath = ch.FaceImagePath,
                        FaceConceptVersionId = ch.FaceConceptVersionId,
                        FaceUploadedProjectId = ch.FaceUploadedProjectId,
                        EnableFaceLock = ch.EnableFaceLock,
                        FaceSwapStrength = ch.FaceSwapStrength,
                        FaceRestoreStrength = ch.FaceRestoreStrength,
                        AnchorSeed = ch.AnchorSeed
                    };
                    if (ch.ExtraFaceAngles is { Count: > 0 })
                    {
                        foreach (var angle in ch.ExtraFaceAngles.Take(StoryCharacterVm.MaxExtraFaces))
                        {
                            chVm.ExtraFaces.Add(new StoryFaceAngleVm(angle.Id, angle.AngleLabel)
                            {
                                ImagePath = angle.ImagePath,
                                ConceptVersionId = angle.ConceptVersionId,
                                UploadedProjectId = angle.UploadedProjectId
                            });
                        }
                    }
                    story.Characters.Add(chVm);
                }
                foreach (var sc in rec.Scenes.OrderBy(s => s.OrderIndex))
                {
                    story.Scenes.Add(new StorySceneVm(sc.Id, sc.OrderIndex, sc.Title)
                    {
                        CharacterId = sc.CharacterId,
                        SecondaryCharacterId = sc.SecondaryCharacterId,
                        ScenePrompt = sc.ScenePrompt,
                        Dialogue = sc.Dialogue,
                        DepthPoseId = sc.DepthPoseId,
                        DepthPoseLabel = sc.DepthPoseLabel,
                        DepthStrength = sc.DepthStrength > 0.1 ? sc.DepthStrength : 0.92,
                        OutfitId = sc.OutfitId,
                        OutfitLabel = sc.OutfitLabel,
                        Seed = sc.Seed,
                        OutputAssetId = sc.OutputAssetId,
                        LocalImagePath = sc.LocalImagePath,
                        UpscaledImagePath = sc.UpscaledImagePath,
                        Width = sc.Width >= 512 ? sc.Width : story.Width,
                        Height = sc.Height >= 512 ? sc.Height : story.Height,
                        StatusText = string.IsNullOrWhiteSpace(sc.StatusText) ? "Chưa vẽ" : sc.StatusText,
                        GeneratedAt = sc.GeneratedAt
                    });
                }
                Stories.Add(story);
            }
        }

        if (Stories.Count == 0)
        {
            var starter = CreateDefaultStarterStory();
            Stories.Add(starter);
            // Never overwrite a corrupt stories.json with a blank starter story!
            if (StoryStore.LastLoadHadCorruptError)
                StatusMessage = "Cảnh báo: Không đọc được stories.json (đã sao lưu bản .corrupt.bak).";
            else
                SaveLocalStories();
        }

        ActiveStory = Stories[0];
        RefreshAllSavedCharacters();
        // ActiveStory already triggers full HydrateStoryMediaAsync via OnActiveStoryChanged;
        // for inactive stories, only load lightweight 240px character face thumbnails for the shared character picker.
        foreach (var st in Stories.Skip(1))
            _ = HydrateStoryCharactersAsync(st);
    }

    private static StoryProjectVm CreateDefaultStarterStory()
    {
        var now = DateTimeOffset.Now;
        var story = new StoryProjectVm(Guid.NewGuid(), "Câu chuyện đầu tiên", now, now)
        {
            StylePresetKey = "photoreal",
            AspectRatioKey = "3:4",
            Width = 896,
            Height = 1152
        };

        var mainChar = new StoryCharacterVm(
            Guid.NewGuid(),
            "Linh",
            "cô gái Việt Nam 22 tuổi xinh đẹp thanh tú, tóc đen dài ngang vai hơi uốn nhẹ có mái thưa, mắt nâu to trong trẻo, mặc áo khoác cardigan màu kem bên ngoài váy sơ mi trắng cổ điển");
        story.Characters.Add(mainChar);

        story.Scenes.Add(new StorySceneVm(Guid.NewGuid(), 0, "Cảnh 1 · Quán cà phê buổi sáng")
        {
            CharacterId = mainChar.Id,
            ScenePrompt = "ngồi bên cửa sổ quán cà phê ấm cúng buổi sáng, nắng sớm dịu nhẹ xuyên qua kính, mỉm cười nhẹ nhàng cầm tách cà phê sữa đá, không gian yên bình",
            Dialogue = "Buổi sáng ở góc quán quen lúc nào cũng dịu dàng như thế…"
        });
        story.Scenes.Add(new StorySceneVm(Guid.NewGuid(), 1, "Cảnh 2 · Dạo phố dưới mưa chiều")
        {
            CharacterId = mainChar.Id,
            ScenePrompt = "bước đi trên con phố cổ mưa nhẹ buổi chiều tà, tay cầm chiếc ô trong suốt, ánh đèn phố vàng ấm phản chiếu trên mặt đường ướt",
            Dialogue = "Cơn mưa bất chợt ập xuống, nhưng lòng lại thấy nhẹ tênh."
        });
        story.Scenes.Add(new StorySceneVm(Guid.NewGuid(), 2, "Cảnh 3 · Hoàng hôn trên cầu")
        {
            CharacterId = mainChar.Id,
            ScenePrompt = "đứng tựa lan can cây cầu lúc hoàng hôn rực rỡ, gió thổi nhẹ làm bay vài sợi tóc, ánh nắng cam vàng chiếu từ phía sau tạo viền sáng đẹp mắt",
            Dialogue = "Hẹn gặp lại ngày mai, ở một chương mới thật rực rỡ."
        });

        return story;
    }

    public void SaveLocalStories()
    {
        if (!IsRealSession || !_storiesLoaded) return;
        var records = Stories.Select(st => new StoryProjectRecord(
            st.Id,
            st.Title,
            st.StylePresetKey,
            st.AspectRatioKey,
            st.Width,
            st.Height,
            st.CreatedAt,
            st.UpdatedAt,
            st.Characters.Select(c => new StoryCharacterRecord(
                c.Id, c.Name, c.VisualDna, c.FaceImagePath, c.FaceConceptVersionId,
                c.EnableFaceLock, c.FaceSwapStrength, c.FaceRestoreStrength, c.AnchorSeed,
                c.ExtraFaces.Count > 0
                    ? c.ExtraFaces.Take(StoryCharacterVm.MaxExtraFaces)
                        .Select(a => new StoryFaceAngleRecord(a.Id, a.AngleLabel, a.ImagePath, a.ConceptVersionId, a.UploadedProjectId)).ToList()
                    : null,
                c.BodyDna,
                c.EnableBodyDnaLock,
                c.FaceUploadedProjectId)).ToList(),
            st.Scenes.Select((s, idx) => new StorySceneRecord(
                s.Id, idx, s.Title, s.CharacterId, s.ScenePrompt, s.Dialogue,
                s.DepthPoseId, s.DepthPoseLabel, s.DepthStrength, s.Seed,
                s.OutputAssetId, s.LocalImagePath, s.UpscaledImagePath,
                s.Width, s.Height, s.IsGenerating ? "Đã dừng" : s.StatusText, s.GeneratedAt,
                s.OutfitId, s.OutfitLabel, s.SecondaryCharacterId)).ToList())).ToList();
        StoryStore.SaveDebounced(records);
    }

    private async Task HydrateStoryCharactersAsync(StoryProjectVm story, CancellationToken ct = default)
    {
        var chars = story.Characters.ToList();
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = chars.Select(async ch =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (ch.FacePreview == null && !string.IsNullOrWhiteSpace(ch.FaceImagePath) && File.Exists(ch.FaceImagePath))
                    {
                        try
                        {
                            var bytes = await File.ReadAllBytesAsync(ch.FaceImagePath, ct);
                            ch.FacePreview = await DecodeSafeBitmapAsync(bytes, 240);
                        }
                        catch { }
                    }
                    foreach (var extra in ch.ExtraFaces.ToList())
                    {
                        if (ct.IsCancellationRequested) break;
                        if (extra.Preview == null && !string.IsNullOrWhiteSpace(extra.ImagePath) && File.Exists(extra.ImagePath))
                        {
                            try
                            {
                                var bytes = await File.ReadAllBytesAsync(extra.ImagePath, ct);
                                extra.Preview = await DecodeSafeBitmapAsync(bytes, 180);
                            }
                            catch { }
                        }
                    }
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
        });
        await Task.WhenAll(tasks);
    }

    private async Task HydrateStoryMediaAsync(StoryProjectVm story)
    {
        _storyHydrateCts?.Cancel();
        _storyHydrateCts?.Dispose();
        _storyHydrateCts = new CancellationTokenSource();
        var ct = _storyHydrateCts.Token;

        await HydrateStoryCharactersAsync(story, ct);
        if (ct.IsCancellationRequested) return;

        // Hydrate story scene cards in parallel at 560px preview decode (full-res lazy-loaded in Lightbox)
        var scenes = story.Scenes.ToList();
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = scenes.Select(async sc =>
        {
            try
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (sc.OriginalImage == null && !string.IsNullOrWhiteSpace(sc.LocalImagePath) && File.Exists(sc.LocalImagePath))
                    {
                        try
                        {
                            var bytes = await File.ReadAllBytesAsync(sc.LocalImagePath, ct);
                            sc.OriginalImage = await DecodeSafeBitmapAsync(bytes, 560);
                        }
                        catch { }
                    }
                    if (sc.UpscaledImage == null && !string.IsNullOrWhiteSpace(sc.UpscaledImagePath) && File.Exists(sc.UpscaledImagePath))
                    {
                        try
                        {
                            var bytes = await File.ReadAllBytesAsync(sc.UpscaledImagePath, ct);
                            sc.UpscaledImage = await DecodeSafeBitmapAsync(bytes, 560);
                        }
                        catch { }
                    }
                    if (sc.DepthPoseThumb == null && !string.IsNullOrWhiteSpace(sc.DepthPoseId) && IsRealSession)
                    {
                        try
                        {
                            var bytes = await _api.DownloadPoseGalleryImageBytesAsync(sc.DepthPoseId, "thumb", "mannequin", ct);
                            if (bytes != null) sc.DepthPoseThumb = await DecodeSafeBitmapAsync(bytes, 180);
                        }
                        catch { }
                    }
                    if (sc.OutfitThumb == null && !string.IsNullOrWhiteSpace(sc.OutfitId) && IsRealSession)
                    {
                        try
                        {
                            var bytes = await _api.DownloadOutfitGalleryImageBytesAsync(sc.OutfitId, "thumb", "front", ct);
                            if (bytes != null) sc.OutfitThumb = await DecodeSafeBitmapAsync(bytes, 180);
                        }
                        catch { }
                    }
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
        });
        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    private void CreateNewStory()
    {
        EnsureStoriesLoaded();
        var now = DateTimeOffset.Now;
        var num = Stories.Count + 1;
        var story = new StoryProjectVm(Guid.NewGuid(), $"Câu chuyện {num}", now, now);
        var defaultChar = new StoryCharacterVm(Guid.NewGuid(), "Nhân vật chính", "cô gái trẻ Việt Nam 20 tuổi, tóc đen mượt, mắt sáng, trang phục thường ngày thanh lịch");
        story.Characters.Add(defaultChar);
        story.Scenes.Add(new StorySceneVm(Guid.NewGuid(), 0, "Cảnh 1")
        {
            CharacterId = defaultChar.Id,
            ScenePrompt = "đứng giữa công viên đầy nắng sáng, mỉm cười nhìn về phía trước"
        });
        Stories.Insert(0, story);
        ActiveStory = story;
        IsStoryReaderMode = false;
        SaveLocalStories();
        StatusMessage = $"Đã tạo {story.Title}.";
    }

    [RelayCommand]
    private void DeleteActiveStory()
    {
        if (ActiveStory == null || Stories.Count <= 1)
        {
            StatusMessage = "Cần giữ lại ít nhất 1 câu chuyện.";
            return;
        }
        var target = ActiveStory;
        Stories.Remove(target);
        ActiveStory = Stories.FirstOrDefault();
        SaveLocalStories();
        StatusMessage = $"Đã xóa câu chuyện '{target.Title}'.";
    }

    [RelayCommand]
    private void SetStoryAspectRatio(string? ratio)
    {
        if (ActiveStory == null || string.IsNullOrWhiteSpace(ratio)) return;
        ActiveStory.AspectRatioKey = ratio;
        (ActiveStory.Width, ActiveStory.Height) = ratio switch
        {
            "1:1" => (1024, 1024),
            "2:3" => (832, 1216),
            "16:9" => (1280, 704),
            "512" => (512, 768),
            _ => (896, 1152)
        };
        SaveLocalStories();
        StatusMessage = $"Đã đặt tỉ lệ cả bộ truyện: {ratio} ({ActiveStory.Width}×{ActiveStory.Height}).";
    }

    [RelayCommand]
    private void AddStoryCharacter()
    {
        if (ActiveStory == null) return;
        var idx = ActiveStory.Characters.Count + 1;
        var ch = new StoryCharacterVm(Guid.NewGuid(), $"Nhân vật {idx}", string.Empty);
        ActiveStory.Characters.Add(ch);
        SelectedStoryCharacter = ch;
        RefreshAllSavedCharacters();
        SaveLocalStories();
        StatusMessage = $"Đã thêm {ch.Name} vào dàn nhân vật — hãy tải ảnh mặt gốc và điền mô tả tóc/khuôn mặt!";
    }

    [RelayCommand]
    private void DeleteStoryCharacter(StoryCharacterVm? character)
    {
        var target = character ?? SelectedStoryCharacter;
        if (ActiveStory == null || target == null) return;
        if (ActiveStudioCharacter?.Id == target.Id) ClearStudioFaceLock();
        ActiveStory.Characters.Remove(target);
        foreach (var sc in ActiveStory.Scenes.Where(s => s.CharacterId == target.Id))
            sc.CharacterId = ActiveStory.Characters.FirstOrDefault()?.Id;
        foreach (var sc in ActiveStory.Scenes.Where(s => s.SecondaryCharacterId == target.Id))
            sc.SecondaryCharacterId = null;
        if (SecondaryStudioCharacter?.Id == target.Id) SecondaryStudioCharacter = null;
        SelectedStoryCharacter = ActiveStory.Characters.FirstOrDefault();
        RefreshAllSavedCharacters();
        SaveLocalStories();
        StatusMessage = $"Đã xóa nhân vật '{target.Name}'.";
    }

    private const long MaxFaceUploadBytes = 10 * 1024 * 1024;

    [RelayCommand]
    private async Task BrowseStoryCharacterFaceAsync(StoryCharacterVm? character)
    {
        var target = character ?? SelectedStoryCharacter;
        if (target == null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Chọn ảnh gương mặt gốc cho {target.Name}",
            Filter = "Hình ảnh (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var info = new FileInfo(dialog.FileName);
            if (!info.Exists || info.Length == 0)
            {
                StatusMessage = "Không đọc được tệp ảnh gương mặt đã chọn.";
                return;
            }
            if (info.Length > MaxFaceUploadBytes)
            {
                StatusMessage = "Ảnh gương mặt quá lớn (tối đa 10 MB).";
                return;
            }
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            await SetCharacterFaceFromBytesAsync(target, bytes, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private async Task<bool> SetCharacterFaceFromBytesAsync(StoryCharacterVm character, byte[] bytes, string fileName)
    {
        if (SelectedProject == null || SelectedCharacter == null) await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null || SelectedCharacter == null)
        {
            StatusMessage = "Chưa kết nối được máy chủ cục bộ để lưu ảnh gương mặt.";
            return false;
        }

        var uploaded = await _api.UploadReferenceImageAsync(SelectedProject.Id, SelectedCharacter.Id, bytes, fileName);
        if (uploaded == null)
        {
            StatusMessage = ApiError("Không tải được ảnh gương mặt lên máy chủ.");
            return false;
        }

        character.FaceConceptVersionId = uploaded.ConceptVersionId;
        character.FaceUploadedProjectId = SelectedProject.Id;
        character.FaceImagePath = StoryStore.SaveStoryImageBytes(character.Id, bytes, "face");
        character.FacePreview = await DecodeSafeBitmapAsync(bytes, 240);
        character.EnableFaceLock = true;
        SaveLocalStories();
        StatusMessage = $"🔒 Đã khóa gương mặt gốc cho nhân vật '{character.Name}' (Local 0-VRAM)!";
        return true;
    }

    [RelayCommand]
    private async Task AddStoryCharacterExtraFacesAsync(StoryCharacterVm? character)
    {
        var target = character ?? SelectedStoryCharacter;
        if (target == null) return;
        if (target.TotalFaceCount >= StoryCharacterVm.MaxTotalFaces)
        {
            StatusMessage = $"Nhân vật '{target.Name}' đã đủ tối đa {StoryCharacterVm.MaxTotalFaces} ảnh góc mặt. Xóa bớt góc cũ nếu muốn thêm mới.";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Chọn thêm ảnh góc mặt / biểu cảm cho {target.Name} (tối đa {StoryCharacterVm.MaxTotalFaces} ảnh)",
            Filter = "Hình ảnh (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0) return;

        int added = 0;
        foreach (var filePath in dialog.FileNames)
        {
            if (target.TotalFaceCount >= StoryCharacterVm.MaxTotalFaces) break;
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists || info.Length == 0 || info.Length > MaxFaceUploadBytes)
                {
                    StatusMessage = $"Bỏ qua '{Path.GetFileName(filePath)}' (ảnh trống hoặc vượt quá 10 MB).";
                    continue;
                }
                var bytes = await File.ReadAllBytesAsync(filePath);
                var fileName = Path.GetFileName(filePath);
                if (!target.HasFaceAnchor)
                {
                    if (await SetCharacterFaceFromBytesAsync(target, bytes, fileName))
                        added++;
                }
                else
                {
                    var label = $"Góc mặt {target.ExtraFaces.Count + 2}";
                    if (await AddCharacterExtraFaceFromBytesAsync(target, bytes, fileName, label) != null)
                        added++;
                }
            }
            catch (Exception ex) { ReportError(ex); }
        }

        if (added > 0)
            StatusMessage = $"🔒 Đã cập nhật bộ đa góc mặt cho '{target.Name}' ({target.TotalFaceCount}/{StoryCharacterVm.MaxTotalFaces} ảnh · ArcFace Yaw-Fusion)!";
    }

    private async Task<StoryFaceAngleVm?> AddCharacterExtraFaceFromBytesAsync(
        StoryCharacterVm character,
        byte[] bytes,
        string fileName,
        string angleLabel)
    {
        if (character.ExtraFaces.Count >= StoryCharacterVm.MaxExtraFaces) return null;
        if (SelectedProject == null || SelectedCharacter == null) await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null || SelectedCharacter == null)
        {
            StatusMessage = "Chưa kết nối được máy chủ cục bộ để lưu góc mặt phụ.";
            return null;
        }

        var uploaded = await _api.UploadReferenceImageAsync(SelectedProject.Id, SelectedCharacter.Id, bytes, fileName);
        if (uploaded == null)
        {
            StatusMessage = ApiError("Không tải được ảnh góc mặt phụ lên máy chủ.");
            return null;
        }

        var angleId = Guid.NewGuid();
        var angleVm = new StoryFaceAngleVm(angleId, angleLabel)
        {
            ConceptVersionId = uploaded.ConceptVersionId,
            UploadedProjectId = SelectedProject.Id,
            ImagePath = StoryStore.SaveStoryImageBytes(angleId, bytes, "face_angle"),
            Preview = await DecodeSafeBitmapAsync(bytes, 180)
        };
        character.ExtraFaces.Add(angleVm);
        character.EnableFaceLock = true;
        SaveLocalStories();
        return angleVm;
    }

    [RelayCommand]
    private void RemoveStoryCharacterExtraFace(StoryFaceAngleVm? angle)
    {
        if (angle == null || ActiveStory == null) return;
        var owner = SelectedStoryCharacter is { } sel && sel.ExtraFaces.Contains(angle)
            ? sel
            : ActiveStory.Characters.FirstOrDefault(c => c.ExtraFaces.Contains(angle));
        if (owner == null) return;
        owner.ExtraFaces.Remove(angle);
        SaveLocalStories();
        StatusMessage = $"Đã gỡ '{angle.AngleLabel}' khỏi bộ góc mặt của '{owner.Name}' ({owner.TotalFaceCount}/{StoryCharacterVm.MaxTotalFaces}).";
    }

    [RelayCommand]
    private async Task OpenStoryExtraFaceLightboxAsync(StoryFaceAngleVm? angle)
    {
        if (angle == null) return;
        BitmapImage? fullImg = null;
        if (!string.IsNullOrWhiteSpace(angle.ImagePath) && File.Exists(angle.ImagePath))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(angle.ImagePath);
                fullImg = await DecodeSafeBitmapAsync(bytes, 0);
            }
            catch { }
        }
        fullImg ??= angle.Preview;
        if (fullImg == null) return;

        var charName = SelectedStoryCharacter?.Name ?? "Nhân vật";
        StoryLightboxScene = null;
        StoryLightboxImage = fullImg;
        StoryLightboxTitle = $"Ảnh góc mặt · {charName} ({angle.AngleLabel})";
        StoryLightboxSubtitle = $"{fullImg.PixelWidth} × {fullImg.PixelHeight} px · ArcFace Multi-Angle";
        IsStoryLightboxOpen = true;
    }

    [RelayCommand]
    private async Task GenerateStoryCharacterPortraitAsync(StoryCharacterVm? character)
    {
        var target = character ?? SelectedStoryCharacter;
        if (ActiveStory == null || target == null || target.IsGeneratingPortrait) return;
        if (string.IsNullOrWhiteSpace(target.VisualDna))
        {
            StatusMessage = "Hãy nhập mô tả ngoại hình DNA cho nhân vật trước khi tự vẽ chân dung gốc.";
            return;
        }
        if (SelectedProject == null || SelectedCharacter == null) await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null || SelectedCharacter == null) return;

        target.IsGeneratingPortrait = true;
        target.PortraitProgressLabel = "Đang vẽ…";
        StatusMessage = $"Đang vẽ ảnh chân dung gốc chính diện cho '{target.Name}'…";
        try
        {
            var styleCue = GetStoryStyleCue(ActiveStory.StylePresetKey, target.VisualDna);
            var portraitPrompt = $"{target.VisualDna.Trim().TrimEnd('.', ',')}, ảnh chân dung chính diện rõ nét từ vai trở lên, nhìn thẳng tự nhiên, ánh sáng studio mềm đều làm nổi bật đường nét khuôn mặt, nền trơn nhẹ{(string.IsNullOrWhiteSpace(styleCue) ? "" : $", {styleCue}")}";

            var result = await ExecuteSingleServerGenerationAsync(
                prompt: portraitPrompt,
                width: 896,
                height: 1152,
                seed: target.AnchorSeed,
                depthPoseId: null,
                depthStrength: null,
                faceRefVersionId: null,
                faceSwapStrength: null,
                faceRestoreStrength: null,
                additionalFaceRefVersionIds: null,
                onStatus: s => StatusMessage = $"Chân dung '{target.Name}': {s}",
                ct: CancellationToken.None);

            if (result == null) return;
            target.AnchorSeed = result.Value.Seed;
            await SetCharacterFaceFromBytesAsync(target, result.Value.Bytes, $"{target.Name}_portrait.png");
        }
        catch (Exception ex) { ReportError(ex); }
        finally { target.IsGeneratingPortrait = false; }
    }

    [RelayCommand]
    private async Task GenerateStoryCharacterThreeAnglePackAsync(StoryCharacterVm? character)
    {
        var target = character ?? SelectedStoryCharacter;
        if (ActiveStory == null || target == null || target.IsGeneratingPortrait) return;
        if (string.IsNullOrWhiteSpace(target.VisualDna))
        {
            StatusMessage = "Hãy nhập mô tả ngoại hình DNA cho nhân vật trước khi tự vẽ bộ 3 góc mặt.";
            return;
        }
        if (target.TotalFaceCount >= StoryCharacterVm.MaxTotalFaces)
        {
            StatusMessage = $"Nhân vật '{target.Name}' đã đủ tối đa {StoryCharacterVm.MaxTotalFaces} ảnh góc mặt. Xóa bớt góc cũ nếu muốn vẽ lại.";
            return;
        }
        if (SelectedProject == null || SelectedCharacter == null) await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null || SelectedCharacter == null) return;

        target.IsGeneratingPortrait = true;
        try
        {
            var dna = target.VisualDna.Trim().TrimEnd('.', ',');
            var styleCue = GetStoryStyleCue(ActiveStory.StylePresetKey, dna);
            var styleSuffix = string.IsNullOrWhiteSpace(styleCue) ? string.Empty : $", {styleCue}";

            // Step 1: Ensure primary frontal portrait exists (keep existing portrait if user already has one!)
            if (!target.HasFaceAnchor || !target.FaceConceptVersionId.HasValue)
            {
                if (!string.IsNullOrWhiteSpace(target.FaceImagePath) && File.Exists(target.FaceImagePath))
                {
                    var existingBytes = await File.ReadAllBytesAsync(target.FaceImagePath);
                    await SetCharacterFaceFromBytesAsync(target, existingBytes, Path.GetFileName(target.FaceImagePath));
                }
                else
                {
                    target.PortraitProgressLabel = "1/3 Chính diện…";
                    StatusMessage = $"Bộ 3 góc mặt '{target.Name}' (1/3): Đang vẽ chân dung chính diện…";
                    var frontPrompt = $"{dna}, ảnh chân dung chính diện rõ nét từ vai trở lên, nhìn thẳng tự nhiên, biểu cảm dịu dàng trung tính, ánh sáng studio mềm đều, nền xám nhạt trơn{styleSuffix}";
                    var frontRes = await ExecuteSingleServerGenerationAsync(
                        prompt: frontPrompt, width: 896, height: 1152, seed: target.AnchorSeed,
                        depthPoseId: null, depthStrength: null,
                        faceRefVersionId: null, faceSwapStrength: null, faceRestoreStrength: null,
                        additionalFaceRefVersionIds: null,
                        onStatus: s => StatusMessage = $"Bộ 3 góc mặt (1/3 Chính diện): {s}",
                        ct: CancellationToken.None);
                    if (frontRes == null) return;
                    target.AnchorSeed = frontRes.Value.Seed;
                    if (!await SetCharacterFaceFromBytesAsync(target, frontRes.Value.Bytes, $"{target.Name}_front.png"))
                        return;
                }
            }

            if (!target.FaceConceptVersionId.HasValue) return;
            var primaryFaceId = target.FaceConceptVersionId.Value;
            var baseSeed = target.AnchorSeed ?? Random.Shared.Next(10000, 900000);

            // Step 2: 3/4 Left angle + gentle smile (locked to primary face)
            Guid? leftAngleVersionId = null;
            if (target.TotalFaceCount < StoryCharacterVm.MaxTotalFaces)
            {
                target.PortraitProgressLabel = "2/3 Trái 3/4…";
                StatusMessage = $"Bộ 3 góc mặt '{target.Name}' (2/3): Đang vẽ góc nghiêng trái 3/4 mỉm cười…";
                var leftPrompt = $"{dna}, ảnh chân dung từ vai trở lên, góc mặt nghiêng trái ba phần tư (three-quarter view facing left), mỉm cười nhẹ nhàng tự nhiên, ánh sáng studio mềm đều, nền xám nhạt trơn{styleSuffix}";
                var leftRes = await ExecuteSingleServerGenerationAsync(
                    prompt: leftPrompt, width: 896, height: 1152, seed: baseSeed + 101,
                    depthPoseId: null, depthStrength: null,
                    faceRefVersionId: primaryFaceId, faceSwapStrength: 0.92, faceRestoreStrength: 0.40,
                    additionalFaceRefVersionIds: null,
                    onStatus: s => StatusMessage = $"Bộ 3 góc mặt (2/3 Trái 3/4): {s}",
                    ct: CancellationToken.None);
                if (leftRes != null)
                {
                    var addedLeft = await AddCharacterExtraFaceFromBytesAsync(target, leftRes.Value.Bytes, $"{target.Name}_left34.png", "Trái 3/4 · Cười");
                    leftAngleVersionId = addedLeft?.ConceptVersionId;
                }
            }

            // Step 3: 3/4 Right angle + expressive gaze (locked to primary + left angle!)
            if (target.TotalFaceCount < StoryCharacterVm.MaxTotalFaces)
            {
                target.PortraitProgressLabel = "3/3 Phải 3/4…";
                StatusMessage = $"Bộ 3 góc mặt '{target.Name}' (3/3): Đang vẽ góc nghiêng phải 3/4 tự nhiên…";
                var rightPrompt = $"{dna}, ảnh chân dung từ vai trở lên, góc mặt nghiêng phải ba phần tư (three-quarter view facing right), ánh mắt biểu cảm tự nhiên thanh thoát, ánh sáng studio mềm đều, nền xám nhạt trơn{styleSuffix}";
                IReadOnlyList<Guid>? extraIds = leftAngleVersionId.HasValue ? [leftAngleVersionId.Value] : null;
                var rightRes = await ExecuteSingleServerGenerationAsync(
                    prompt: rightPrompt, width: 896, height: 1152, seed: baseSeed + 202,
                    depthPoseId: null, depthStrength: null,
                    faceRefVersionId: primaryFaceId, faceSwapStrength: 0.92, faceRestoreStrength: 0.40,
                    additionalFaceRefVersionIds: extraIds,
                    onStatus: s => StatusMessage = $"Bộ 3 góc mặt (3/3 Phải 3/4): {s}",
                    ct: CancellationToken.None);
                if (rightRes != null)
                {
                    await AddCharacterExtraFaceFromBytesAsync(target, rightRes.Value.Bytes, $"{target.Name}_right34.png", "Phải 3/4");
                }
            }

            StatusMessage = $"✨ Hoàn tất bộ đa góc mặt cho '{target.Name}' ({target.TotalFaceCount}/{StoryCharacterVm.MaxTotalFaces} ảnh · sẵn sàng gộp vector ArcFace)!";
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            target.IsGeneratingPortrait = false;
            target.PortraitProgressLabel = "Đang vẽ…";
        }
    }

    [RelayCommand]
    private void AddStoryScene()
    {
        if (ActiveStory == null) return;
        var nextIdx = ActiveStory.Scenes.Count;
        var scene = new StorySceneVm(Guid.NewGuid(), nextIdx, $"Cảnh {nextIdx + 1}")
        {
            CharacterId = SelectedStoryCharacter?.Id ?? ActiveStory.Characters.FirstOrDefault()?.Id,
            Width = ActiveStory.Width,
            Height = ActiveStory.Height
        };
        ActiveStory.Scenes.Add(scene);
        ActiveStory.UpdatedAt = DateTimeOffset.Now;
        SaveLocalStories();
        StatusMessage = $"Đã thêm {scene.Title}.";
    }

    [RelayCommand]
    private void DuplicateStoryScene(StorySceneVm? scene)
    {
        if (ActiveStory == null || scene == null) return;
        var idx = ActiveStory.Scenes.IndexOf(scene);
        var copy = new StorySceneVm(Guid.NewGuid(), idx + 1, $"{scene.Title} (bản sao)")
        {
            CharacterId = scene.CharacterId,
            SecondaryCharacterId = scene.SecondaryCharacterId,
            ScenePrompt = scene.ScenePrompt,
            Dialogue = scene.Dialogue,
            DepthPoseId = scene.DepthPoseId,
            DepthPoseLabel = scene.DepthPoseLabel,
            DepthPoseThumb = scene.DepthPoseThumb,
            DepthStrength = scene.DepthStrength,
            OutfitId = scene.OutfitId,
            OutfitLabel = scene.OutfitLabel,
            OutfitThumb = scene.OutfitThumb,
            Width = ActiveStory.Width,
            Height = ActiveStory.Height
        };
        ActiveStory.Scenes.Insert(idx + 1, copy);
        ReindexStoryScenes();
        SaveLocalStories();
    }

    [RelayCommand]
    private void DeleteStoryScene(StorySceneVm? scene)
    {
        if (ActiveStory == null || scene == null) return;
        ActiveStory.Scenes.Remove(scene);
        ReindexStoryScenes();
        SaveLocalStories();
    }

    [RelayCommand]
    private void MoveStorySceneUp(StorySceneVm? scene)
    {
        if (ActiveStory == null || scene == null) return;
        var idx = ActiveStory.Scenes.IndexOf(scene);
        if (idx <= 0) return;
        ActiveStory.Scenes.Move(idx, idx - 1);
        ReindexStoryScenes();
        SaveLocalStories();
    }

    [RelayCommand]
    private void MoveStorySceneDown(StorySceneVm? scene)
    {
        if (ActiveStory == null || scene == null) return;
        var idx = ActiveStory.Scenes.IndexOf(scene);
        if (idx < 0 || idx >= ActiveStory.Scenes.Count - 1) return;
        ActiveStory.Scenes.Move(idx, idx + 1);
        ReindexStoryScenes();
        SaveLocalStories();
    }

    private void ReindexStoryScenes()
    {
        if (ActiveStory == null) return;
        for (var i = 0; i < ActiveStory.Scenes.Count; i++)
            ActiveStory.Scenes[i].OrderIndex = i;
    }

    [RelayCommand]
    private void ClearScenePose(StorySceneVm? scene)
    {
        if (scene == null) return;
        scene.DepthPoseId = null;
        scene.DepthPoseLabel = null;
        scene.DepthPoseThumb = null;
        SaveLocalStories();
    }

    [RelayCommand]
    private void ClearSceneOutfit(StorySceneVm? scene)
    {
        if (scene == null) return;
        scene.OutfitId = null;
        scene.OutfitLabel = null;
        scene.OutfitThumb = null;
        SaveLocalStories();
    }

    [RelayCommand]
    private void ClearSceneSecondaryCharacter(StorySceneVm? scene)
    {
        if (scene == null) return;
        scene.SecondaryCharacterId = null;
        SaveLocalStories();
    }

    [RelayCommand]
    private async Task EnhanceStoryScenePromptAsync(StorySceneVm? scene)
    {
        if (ActiveStory == null || scene == null || scene.IsEnhancing) return;
        var raw = scene.ScenePrompt?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            StatusMessage = "Hãy nhập mô tả bối cảnh/hành động ngắn cho cảnh trước khi bấm nâng cấp.";
            return;
        }
        scene.IsEnhancing = true;
        StatusMessage = $"AI Qwen3-4B đang viết chi tiết {scene.Title} (giữ nguyên DNA nhân vật)…";
        try
        {
            var res = await _api.EnhancePromptAsync(new EnhancePromptRequest(
                Prompt: raw,
                StylePreset: ActiveStory.StylePresetKey,
                Width: ActiveStory.Width,
                Height: ActiveStory.Height,
                EnableReasoning: false));
            if (res is { EnhancedPrompt.Length: > 0 })
            {
                scene.ScenePrompt = res.EnhancedPrompt.Trim();
                SaveLocalStories();
                StatusMessage = $"Đã nâng cấp mô tả cho {scene.Title}.";
            }
        }
        catch (Exception ex) { ReportError(ex); }
        finally { scene.IsEnhancing = false; }
    }

    [RelayCommand]
    private async Task GenerateStorySceneAsync(StorySceneVm? scene)
    {
        if (ActiveStory == null || scene == null || scene.IsGenerating) return;
        await RunSingleStorySceneGenerationAsync(ActiveStory, scene, CancellationToken.None);
    }

    [RelayCommand]
    private async Task GenerateAllStoryScenesAsync()
    {
        var story = ActiveStory;
        if (story == null || IsStoryBatchGenerating) return;
        var targets = story.Scenes.Where(s => !s.HasImage).ToList();
        if (targets.Count == 0) targets = story.Scenes.ToList();
        if (targets.Count == 0) return;

        _storyBatchCts?.Cancel();
        _storyBatchCts?.Dispose();
        _storyBatchCts = new CancellationTokenSource();
        var ct = _storyBatchCts.Token;
        IsStoryBatchGenerating = true;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                StatusMessage = $"Đang vẽ hàng loạt cảnh {i + 1}/{targets.Count}: {targets[i].Title}…";
                await RunSingleStorySceneGenerationAsync(story, targets[i], ct);
            }
            if (!ct.IsCancellationRequested)
                StatusMessage = $"Hoàn thành vẽ {targets.Count} cảnh cho câu chuyện '{story.Title}'!";
        }
        finally
        {
            IsStoryBatchGenerating = false;
        }
    }

    [RelayCommand]
    private void CancelStoryBatch()
    {
        _storyBatchCts?.Cancel();
        StatusMessage = "Đã dừng vẽ hàng loạt sau cảnh hiện tại.";
    }

    private async Task RunSingleStorySceneGenerationAsync(StoryProjectVm story, StorySceneVm scene, CancellationToken ct)
    {
        ReleaseVoiceModelMemory();
        if (SelectedProject == null || SelectedCharacter == null || SelectedCharacter.ProjectId != SelectedProject.Id)
            await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null || SelectedCharacter == null) return;

        var character = scene.CharacterId.HasValue
            ? story.Characters.FirstOrDefault(c => c.Id == scene.CharacterId.Value)
            : null;
        var secondaryCharacter = scene.SecondaryCharacterId.HasValue && scene.SecondaryCharacterId != scene.CharacterId
            ? story.Characters.FirstOrDefault(c => c.Id == scene.SecondaryCharacterId.Value)
            : null;

        // Re-upload cached local primary & extra face angles seamlessly when missing, project switched, or DB reset (404)
        async Task EnsureCharFacesUploadedAsync(StoryCharacterVm? ch, bool forceReupload = false)
        {
            if (ch is not { EnableFaceLock: true }) return;
            var curProjId = SelectedProject!.Id;
            var curCharId = SelectedCharacter!.Id;
            if ((forceReupload || ch.FaceConceptVersionId == null || ch.FaceUploadedProjectId != curProjId) &&
                !string.IsNullOrWhiteSpace(ch.FaceImagePath) && File.Exists(ch.FaceImagePath))
            {
                var faceBytes = await File.ReadAllBytesAsync(ch.FaceImagePath, ct);
                await SetCharacterFaceFromBytesAsync(ch, faceBytes, Path.GetFileName(ch.FaceImagePath));
            }
            var extraUploaded = false;
            foreach (var extra in ch.ExtraFaces.Take(StoryCharacterVm.MaxExtraFaces))
            {
                if ((forceReupload || extra.ConceptVersionId == null || extra.UploadedProjectId != curProjId) &&
                    !string.IsNullOrWhiteSpace(extra.ImagePath) && File.Exists(extra.ImagePath))
                {
                    var extraBytes = await File.ReadAllBytesAsync(extra.ImagePath, ct);
                    var uploaded = await _api.UploadReferenceImageAsync(curProjId, curCharId, extraBytes, Path.GetFileName(extra.ImagePath), ct);
                    if (uploaded != null)
                    {
                        extra.ConceptVersionId = uploaded.ConceptVersionId;
                        extra.UploadedProjectId = curProjId;
                        extraUploaded = true;
                    }
                }
            }
            if (extraUploaded) SaveLocalStories();
        }

        await EnsureCharFacesUploadedAsync(character);
        await EnsureCharFacesUploadedAsync(secondaryCharacter);

        var composedPrompt = ComposeStoryScenePrompt(story, character, secondaryCharacter, scene);
        if (string.IsNullOrWhiteSpace(composedPrompt) && !scene.HasOutfit)
        {
            scene.ErrorMessage = "Hãy nhập mô tả cảnh, chọn nhân vật có DNA hoặc chọn một trang phục.";
            return;
        }

        scene.IsGenerating = true;
        scene.ErrorMessage = null;
        scene.StatusText = "Đang gửi đến GPU…";
        try
        {
            (Guid? PrimaryId, List<Guid>? ExtraIds, Guid? SecId, List<Guid>? SecExtraIds) ResolveFaceIds()
            {
                var fId = character is { EnableFaceLock: true } ? character.FaceConceptVersionId : null;
                List<Guid>? eIds = null;
                if (fId.HasValue && character is { ExtraFaces.Count: > 0 })
                {
                    var validExtras = character.ExtraFaces
                        .Where(a => a.ConceptVersionId.HasValue && a.ConceptVersionId.Value != fId.Value)
                        .Select(a => a.ConceptVersionId!.Value)
                        .Distinct()
                        .Take(StoryCharacterVm.MaxExtraFaces)
                        .ToList();
                    if (validExtras.Count > 0) eIds = validExtras;
                }
                var sfId = secondaryCharacter is { EnableFaceLock: true } ? secondaryCharacter.FaceConceptVersionId : null;
                List<Guid>? seIds = null;
                if (sfId.HasValue && secondaryCharacter is { ExtraFaces.Count: > 0 })
                {
                    var validSec = secondaryCharacter.ExtraFaces
                        .Where(a => a.ConceptVersionId.HasValue && a.ConceptVersionId.Value != sfId.Value)
                        .Select(a => a.ConceptVersionId!.Value)
                        .Distinct()
                        .Take(2)
                        .ToList();
                    if (validSec.Count > 0) seIds = validSec;
                }
                return (fId, eIds, sfId, seIds);
            }

            var (faceVerId, extraFaceIds, secFaceVerId, secExtraFaceIds) = ResolveFaceIds();
            var totalFacesUsed = faceVerId.HasValue ? 1 + (extraFaceIds?.Count ?? 0) : 0;

            var sceneSeed = scene.Seed ?? (character?.AnchorSeed is long anchorSeed
                ? anchorSeed + (scene.OrderIndex + 1) * 7919L
                : null);

            async Task<(Guid AssetId, long Seed, byte[] Bytes, BitmapImage Image, string ElapsedLabel)?> RunSubmitAsync() =>
                await ExecuteSingleServerGenerationAsync(
                    prompt: composedPrompt,
                    width: story.Width,
                    height: story.Height,
                    seed: sceneSeed,
                    depthPoseId: scene.DepthPoseId,
                    depthStrength: scene.HasDepthPose ? scene.DepthStrength : null,
                    faceRefVersionId: faceVerId,
                    faceSwapStrength: faceVerId.HasValue ? character!.FaceSwapStrength : null,
                    faceRestoreStrength: faceVerId.HasValue ? character!.FaceRestoreStrength : null,
                    additionalFaceRefVersionIds: extraFaceIds,
                    onStatus: st => scene.StatusText = st,
                    ct: ct,
                    outfitId: scene.OutfitId,
                    secondaryFaceRefVersionId: secFaceVerId,
                    secondaryFaceSwapStrength: secFaceVerId.HasValue ? secondaryCharacter!.FaceSwapStrength : null,
                    secondaryFaceRestoreStrength: secFaceVerId.HasValue ? secondaryCharacter!.FaceRestoreStrength : null,
                    secondaryAdditionalFaceRefVersionIds: secExtraFaceIds,
                    loras: GetActiveLoraSelections());

            var res = await RunSubmitAsync();
            if (res == null && _api.LastStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Transparent 404 self-healing: refresh workspace & re-upload cached face anchors into current project
                scene.StatusText = "Đang đồng bộ lại ảnh gương mặt…";
                if (IsLocalStudioMode) await EnsureAutoWorkspaceAsync();
                await EnsureCharFacesUploadedAsync(character, forceReupload: true);
                await EnsureCharFacesUploadedAsync(secondaryCharacter, forceReupload: true);
                (faceVerId, extraFaceIds, secFaceVerId, secExtraFaceIds) = ResolveFaceIds();
                totalFacesUsed = faceVerId.HasValue ? 1 + (extraFaceIds?.Count ?? 0) : 0;
                res = await RunSubmitAsync();
            }

            if (res == null)
            {
                scene.ErrorMessage = StatusMessage;
                scene.StatusText = "Thất bại";
                return;
            }

            var (nativeW, nativeH) = TryReadPngDimensions(res.Value.Bytes);
            scene.OutputAssetId = res.Value.AssetId;
            scene.Seed = res.Value.Seed;
            scene.Width = nativeW > 0 ? nativeW : story.Width;
            scene.Height = nativeH > 0 ? nativeH : story.Height;
            scene.OriginalImage = res.Value.Image;
            scene.UpscaledImage = null;
            scene.UpscaledImagePath = null;
            scene.GeneratedAt = DateTimeOffset.Now;
            var faceTag = secFaceVerId.HasValue && faceVerId.HasValue ? "🔒 Cặp đôi 2 mặt"
                : totalFacesUsed >= 2 ? $"🔒 Đa góc ({totalFacesUsed} ảnh)"
                : totalFacesUsed == 1 ? "🔒 Khóa mặt" : null;
            scene.StatusText = faceTag != null
                ? (scene.HasDepthPose ? $"Xong · {faceTag} + Depth · {res.Value.ElapsedLabel}" : $"Xong · {faceTag} · {res.Value.ElapsedLabel}")
                : (scene.HasDepthPose ? $"Xong · Depth Pose · {res.Value.ElapsedLabel}" : $"Xong · {res.Value.ElapsedLabel}");

            if (IsRealSession)
            {
                scene.LocalImagePath = await StoryStore.SaveStoryImageBytesAsync(scene.Id, res.Value.Bytes);
                TryAutoExportImage(res.Value.Seed, scene.Width, scene.Height, false, res.Value.Bytes);
                story.UpdatedAt = DateTimeOffset.Now;
                SaveLocalStories();
            }
            StatusMessage = $"Đã vẽ xong {scene.Title} ({scene.StatusText}).";
        }
        catch (OperationCanceledException)
        {
            scene.StatusText = "Đã hủy";
        }
        catch (Exception ex)
        {
            scene.ErrorMessage = ex.Message;
            scene.StatusText = "Lỗi";
            ReportError(ex);
        }
        finally
        {
            scene.IsGenerating = false;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex SceneSpecifiesOutfitRegex = new(
        @"\b(wearing|dressed in|in a|spacesuit|space suit|astronaut|graduation|gown|robe|toga|uniform|suit|tuxedo|dress|jacket|hoodie|armor|kimono|ao dai|bikini|swimsuit|vest|coat|shirt|sweater)\b|mặc|đội mũ|áo cử nhân|lễ phục|phi hành gia|đồng phục|áo dài|áo khoác|váy|đầm|áo giáp|đồ bảo hộ",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex DnaClothingClauseRegex = new(
        @",?\s*(?:wearing\b[^,;.]*(?:\([^)]*\))?|mặc\b[^,;.]*)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SceneSpecifiesExpressionRegex = new(
        @"\b(smiling|smile|beaming|grinning|laughing|joyful|proud|happy|focused|concentrat\w*|furrowed|frowning|intense|determined|serious|shocked|astonished|devastated|horrified|crying|tears|sad|heartbroken|angry|exhausted|tired|awe|amazed|worried|anxious|excited|surprised|emotional)\b|cười|mỉm cười|rạng rỡ|tự hào|tập trung|chăm chú|cau mày|nhíu mày|quyết tâm|bàng hoàng|sững sờ|kinh ngạc|sốc|khóc|rưng rưng|buồn|đau khổ|tức giận|mệt mỏi|lo lắng|ngạc nhiên|xúc động|căng thẳng",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex DnaExpressionClauseRegex = new(
        @",?\s*(?:subtle\s+gentle\s+composed\s+expression|gentle\s+composed\s+expression|composed\s+expression|calm\s+neutral\s+expression|neutral\s+expression|serene\s+expression|with\s+soft\s+gaze|biểu cảm dịu dàng trung tính|biểu cảm trung tính)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripDefaultOutfitFromDna(string? dna, bool shouldStripOutfit, bool shouldStripExpression)
    {
        if (string.IsNullOrWhiteSpace(dna)) return string.Empty;
        var result = dna;
        if (shouldStripOutfit)
            result = DnaClothingClauseRegex.Replace(result, string.Empty);
        if (shouldStripExpression)
            result = DnaExpressionClauseRegex.Replace(result, string.Empty);
        return result.Trim(' ', ',', ';', '.');
    }

    private string ComposeStoryScenePrompt(StoryProjectVm story, StoryCharacterVm? character, StoryCharacterVm? secondaryCharacter, StorySceneVm scene)
    {
        var parts = new List<string>();
        var sceneText = scene.ScenePrompt?.Trim().TrimEnd('.', ',') ?? string.Empty;
        var overrideOutfit = scene.HasOutfit || SceneSpecifiesOutfitRegex.IsMatch(sceneText);
        var overrideExpression = SceneSpecifiesExpressionRegex.IsMatch(sceneText);
        var fullDna = StripDefaultOutfitFromDna(character?.BuildFullDna(), overrideOutfit, overrideExpression);
        var secondDna = StripDefaultOutfitFromDna(secondaryCharacter?.BuildFullDna(), overrideOutfit, overrideExpression);
        var isVi = sceneText.Any(ch => ch is >= '\u00C0' and <= '\u024F' or >= '\u1E00' and <= '\u1EFF');

        if (!string.IsNullOrWhiteSpace(fullDna) && !string.IsNullOrWhiteSpace(secondDna))
        {
            parts.Add(isVi
                ? $"hai nhân vật đứng cùng khung hình — nhân vật bên trái là {fullDna}; nhân vật bên phải là {secondDna}"
                : $"two distinct people in the same frame — person on the left: {fullDna}; person on the right: {secondDna}");
        }
        else if (!string.IsNullOrWhiteSpace(fullDna))
        {
            var replaced = false;
            foreach (var ph in new[] { "[MÔ TẢ NHÂN VẬT CỦA BẠN]", "[MO TA NHAN VAT CUA BAN]", "[CHARACTER]", "[NHÂN VẬT]" })
            {
                if (sceneText.Contains(ph, StringComparison.OrdinalIgnoreCase))
                {
                    sceneText = sceneText.Replace(ph, fullDna, StringComparison.OrdinalIgnoreCase);
                    replaced = true;
                }
            }
            if (!replaced) parts.Add(fullDna);
        }

        if (!string.IsNullOrWhiteSpace(sceneText))
            parts.Add(sceneText);

        var styleCue = GetStoryStyleCue(story.StylePresetKey, string.Join(' ', parts));
        if (!string.IsNullOrWhiteSpace(styleCue))
            parts.Add(styleCue);

        return string.Join(", ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private string GetStoryStyleCue(string styleKey, string sampleText)
    {
        if (string.IsNullOrWhiteSpace(styleKey) || styleKey == "none") return string.Empty;
        var preset = AvailableStylePresets.FirstOrDefault(p => p.Key == styleKey);
        if (preset == null) return string.Empty;
        var isVietnamese = sampleText.Any(ch => ch is >= '\u00C0' and <= '\u024F' or >= '\u1E00' and <= '\u1EFF');
        return isVietnamese ? preset.PromptCueVi : preset.PromptCueEn;
    }

    private async Task<(Guid AssetId, long Seed, byte[] Bytes, BitmapImage Image, string ElapsedLabel)?> ExecuteSingleServerGenerationAsync(
        string prompt,
        int width,
        int height,
        long? seed,
        string? depthPoseId,
        double? depthStrength,
        Guid? faceRefVersionId,
        double? faceSwapStrength,
        double? faceRestoreStrength,
        IReadOnlyList<Guid>? additionalFaceRefVersionIds,
        Action<string> onStatus,
        CancellationToken ct,
        string? outfitId = null,
        Guid? secondaryFaceRefVersionId = null,
        double? secondaryFaceSwapStrength = null,
        double? secondaryFaceRestoreStrength = null,
        IReadOnlyList<Guid>? secondaryAdditionalFaceRefVersionIds = null,
        IReadOnlyList<LoraSelectionDto>? loras = null)
    {
        var projectId = SelectedProject!.Id;
        var characterId = SelectedCharacter!.Id;
        var jobId = await _api.SubmitGenerationAsync(
            projectId: projectId,
            characterId: characterId,
            variantId: null,
            promptMode: PromptMode.Raw,
            rawPrompt: prompt,
            finalPrompt: prompt,
            width: width,
            height: height,
            requestedCount: 1,
            promptTemplateVersion: "1.0",
            referenceConceptVersionId: null,
            denoise: null,
            ct: ct,
            seed: seed,
            fullQuality: false,
            depthPoseId: depthPoseId,
            depthStrength: depthStrength,
            faceReferenceConceptVersionId: faceRefVersionId,
            faceSwapStrength: faceSwapStrength,
            faceRestoreStrength: faceRestoreStrength,
            additionalFaceReferenceConceptVersionIds: additionalFaceRefVersionIds,
            outfitId: outfitId,
            secondaryFaceReferenceConceptVersionId: secondaryFaceRefVersionId,
            secondaryFaceSwapStrength: secondaryFaceSwapStrength,
            secondaryFaceRestoreStrength: secondaryFaceRestoreStrength,
            secondaryAdditionalFaceReferenceConceptVersionIds: secondaryAdditionalFaceRefVersionIds,
            loras: loras);

        if (jobId == null)
        {
            StatusMessage = ApiError("Máy chủ không chấp nhận tác vụ sinh cảnh.");
            return null;
        }

        var sw = Stopwatch.StartNew();
        onStatus("Đang xếp hàng GPU…");
        while (!ct.IsCancellationRequested)
        {
            var job = await _api.GetJobStatusAsync(projectId, jobId.Value, ct);
            if (job == null)
            {
                StatusMessage = ApiError("Mất kết nối khi theo dõi tác vụ.");
                return null;
            }
            if (job.Status == GenerationStatus.Completed)
            {
                if (job.OutputAssetIds.Count == 0)
                {
                    StatusMessage = "Tác vụ hoàn tất nhưng không có ảnh đầu ra.";
                    return null;
                }
                var assetId = job.OutputAssetIds[0];
                var bytes = await _api.DownloadImageBytesAsync(projectId, assetId, ct);
                if (bytes == null) return null;
                var img = await DecodeImageAsync(bytes, 560);
                var dur = ComputeJobDuration(job, sw.Elapsed);
                return (assetId, job.Seed, bytes, img, FormatElapsed(dur, showDecimal: true));
            }
            if (job.Status is GenerationStatus.Failed or GenerationStatus.Cancelled or GenerationStatus.Interrupted)
            {
                StatusMessage = job.ErrorSummary ?? $"Tác vụ {job.Status}";
                return null;
            }
            onStatus($"{JobStatusLabel(job.Status)} · {FormatElapsed(sw.Elapsed, showDecimal: false)}");
            var delayMs = !_api.IsLoopbackBaseUrl ? 1000
                : sw.Elapsed.TotalSeconds < 12 ? 250
                : sw.Elapsed.TotalSeconds < 30 ? 450
                : 750;
            await Task.Delay(delayMs, ct);
        }
        return null;
    }

    [RelayCommand]
    private async Task UseSceneImageAsCharacterFaceAsync(StorySceneVm? scene)
    {
        if (ActiveStory == null || scene == null || !scene.HasImage) return;
        var character = scene.CharacterId.HasValue
            ? ActiveStory.Characters.FirstOrDefault(c => c.Id == scene.CharacterId.Value)
            : SelectedStoryCharacter;
        if (character == null)
        {
            StatusMessage = "Chọn một nhân vật để gắn ảnh gương mặt gốc.";
            return;
        }

        byte[]? bytes = null;
        if (!string.IsNullOrWhiteSpace(scene.LocalImagePath) && File.Exists(scene.LocalImagePath))
            bytes = await File.ReadAllBytesAsync(scene.LocalImagePath);
        else if (scene.OutputAssetId is Guid assetId && SelectedProject != null)
            bytes = await _api.DownloadImageBytesAsync(SelectedProject.Id, assetId);

        if (bytes == null) return;
        await SetCharacterFaceFromBytesAsync(character, bytes, $"{character.Name}_from_scene.png");
    }

    [RelayCommand]
    private async Task AddSceneImageAsExtraFaceAsync(StorySceneVm? scene)
    {
        if (ActiveStory == null || scene == null || !scene.HasImage) return;
        var character = scene.CharacterId.HasValue
            ? ActiveStory.Characters.FirstOrDefault(c => c.Id == scene.CharacterId.Value)
            : SelectedStoryCharacter;
        if (character == null)
        {
            StatusMessage = "Chọn một nhân vật để thêm ảnh góc mặt.";
            return;
        }
        if (character.TotalFaceCount >= StoryCharacterVm.MaxTotalFaces)
        {
            StatusMessage = $"Nhân vật '{character.Name}' đã đủ tối đa {StoryCharacterVm.MaxTotalFaces} ảnh góc mặt.";
            return;
        }

        byte[]? bytes = null;
        if (!string.IsNullOrWhiteSpace(scene.LocalImagePath) && File.Exists(scene.LocalImagePath))
            bytes = await File.ReadAllBytesAsync(scene.LocalImagePath);
        else if (scene.OutputAssetId is Guid assetId && SelectedProject != null)
            bytes = await _api.DownloadImageBytesAsync(SelectedProject.Id, assetId);
        if (bytes == null) return;

        if (!character.HasFaceAnchor)
        {
            await SetCharacterFaceFromBytesAsync(character, bytes, $"{character.Name}_scene.png");
        }
        else
        {
            var label = $"Từ {scene.SceneNumberLabel}";
            if (await AddCharacterExtraFaceFromBytesAsync(character, bytes, $"{character.Name}_scene_angle.png", label) != null)
                StatusMessage = $"🔒 Đã thêm góc mặt từ {scene.SceneNumberLabel} cho '{character.Name}' ({character.TotalFaceCount}/{StoryCharacterVm.MaxTotalFaces} ảnh)!";
        }
    }

    [RelayCommand]
    private async Task UpscaleStorySceneAsync(StorySceneVm? scene)
    {
        if (scene == null || scene.OutputAssetId is not Guid assetId || scene.IsUpscaling) return;
        ReleaseVoiceModelMemory();
        if (SelectedProject == null) await EnsureAutoWorkspaceAsync();
        if (SelectedProject == null) return;

        scene.IsUpscaling = true;
        scene.StatusText = "Đang nâng nét 2×…";
        try
        {
            byte[]? bytes = null;
            if (!string.IsNullOrWhiteSpace(scene.LocalImagePath) && File.Exists(scene.LocalImagePath))
            {
                var srcBytes = await File.ReadAllBytesAsync(scene.LocalImagePath);
                bytes = await _api.HiresUpscale2KAsync(
                    SelectedProject.Id,
                    srcBytes,
                    prompt: scene.ScenePrompt,
                    useTiledHires: false);
            }
            bytes ??= await _api.UpscaleAssetAsync(SelectedProject.Id, assetId);
            if (bytes == null || bytes.Length == 0)
            {
                StatusMessage = ApiError("Không thể nâng nét 2× cho cảnh này.");
                return;
            }
            var bmp = await DecodeImageAsync(bytes, 560);
            var (nativeW, nativeH) = TryReadPngDimensions(bytes);
            var w = nativeW > 0 ? nativeW : bmp.PixelWidth;
            var h = nativeH > 0 ? nativeH : bmp.PixelHeight;
            scene.UpscaledImage = bmp;
            if (IsRealSession)
            {
                scene.UpscaledImagePath = await StoryStore.SaveStoryImageBytesAsync(scene.Id, bytes, "2x");
                TryAutoExportImage(scene.Seed ?? 0, w, h, true, bytes);
                SaveLocalStories();
            }
            scene.StatusText = $"Full 2× ({w}×{h})";
            StatusMessage = $"Đã nâng nét 2× cho {scene.Title}!";
        }
        catch (Exception ex) { ReportError(ex); }
        finally { scene.IsUpscaling = false; }
    }

    [RelayCommand]
    private async Task OpenStorySceneLightboxAsync(StorySceneVm? scene)
    {
        if (scene?.DisplayImage == null) return;
        StoryLightboxScene = scene;
        StoryLightboxTitle = scene.Title;
        // Lazily load native full-resolution bitmap from disk for crisp 1:1 Lightbox viewing
        BitmapImage? fullImg = null;
        var sourcePath = !string.IsNullOrWhiteSpace(scene.UpscaledImagePath) && File.Exists(scene.UpscaledImagePath)
            ? scene.UpscaledImagePath
            : (!string.IsNullOrWhiteSpace(scene.LocalImagePath) && File.Exists(scene.LocalImagePath) ? scene.LocalImagePath : null);
        if (sourcePath != null)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(sourcePath);
                fullImg = await DecodeSafeBitmapAsync(bytes, 0);
            }
            catch { }
        }
        var display = fullImg ?? scene.DisplayImage;
        StoryLightboxImage = display;
        StoryLightboxSubtitle = $"{display.PixelWidth} × {display.PixelHeight} px · {scene.StatusText}";
        IsStoryLightboxOpen = true;
    }

    [RelayCommand]
    private async Task OpenStoryCharacterFaceLightboxAsync(StoryCharacterVm? character)
    {
        var target = character ?? SelectedStoryCharacter;
        if (target == null || !target.HasFaceAnchor) return;

        BitmapImage? fullImg = null;
        if (!string.IsNullOrWhiteSpace(target.FaceImagePath) && File.Exists(target.FaceImagePath))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(target.FaceImagePath);
                fullImg = await DecodeSafeBitmapAsync(bytes, 0);
            }
            catch { }
        }

        fullImg ??= target.FacePreview;
        if (fullImg == null) return;

        StoryLightboxScene = null;
        StoryLightboxImage = fullImg;
        StoryLightboxTitle = $"Ảnh nhân vật · {target.Name}";
        StoryLightboxSubtitle = $"{fullImg.PixelWidth} × {fullImg.PixelHeight} px · {target.FaceLockBadge}";
        IsStoryLightboxOpen = true;
    }

    [RelayCommand]
    private void SaveStoryLightboxImage()
    {
        if (StoryLightboxImage == null) return;
        var safeName = string.Concat(StoryLightboxTitle.Split(Path.GetInvalidFileNameChars())).Trim().Replace(' ', '_');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "story_image";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Lưu ảnh",
            Filter = "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg",
            FileName = $"{safeName}.png"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            BitmapEncoder encoder = Path.GetExtension(dialog.FileName).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder { QualityLevel = 95 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(StoryLightboxImage));
            using var fs = File.Create(dialog.FileName);
            encoder.Save(fs);
            StatusMessage = $"Đã lưu ảnh: {dialog.FileName}";
        }
        catch (Exception ex) { ReportError(ex); }
    }

    [RelayCommand]
    private void CopyStoryLightboxImage()
    {
        if (StoryLightboxImage == null) return;
        try
        {
            System.Windows.Clipboard.SetImage(StoryLightboxImage);
            StatusMessage = "Đã sao chép ảnh vào bộ nhớ tạm.";
        }
        catch (Exception ex) { ReportError(ex); }
    }

    [RelayCommand]
    private void CloseStoryLightbox()
    {
        IsStoryLightboxOpen = false;
        StoryLightboxScene = null;
        StoryLightboxImage = null;
        StoryLightboxTitle = string.Empty;
        StoryLightboxSubtitle = string.Empty;
    }

    [RelayCommand]
    private void ToggleStoryReaderMode() => IsStoryReaderMode = !IsStoryReaderMode;

    [RelayCommand]
    private void ExportStoryImages()
    {
        if (ActiveStory == null) return;
        var rendered = ActiveStory.Scenes.Where(s => !string.IsNullOrWhiteSpace(s.UpscaledImagePath ?? s.LocalImagePath)).ToList();
        if (rendered.Count == 0)
        {
            StatusMessage = "Chưa có cảnh nào đã vẽ để xuất ảnh.";
            return;
        }
        try
        {
            var baseDir = string.IsNullOrWhiteSpace(AutoExportFolder) ? ChatHistoryStore.DefaultAutoExportFolder : AutoExportFolder;
            var safeTitle = string.Concat(ActiveStory.Title.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrWhiteSpace(safeTitle)) safeTitle = "Story";
            var outDir = Path.Combine(baseDir, $"Story_{safeTitle}_{DateTime.Now:yyyyMMdd_HHmm}");
            Directory.CreateDirectory(outDir);

            for (var i = 0; i < rendered.Count; i++)
            {
                var sc = rendered[i];
                var srcPath = !string.IsNullOrWhiteSpace(sc.UpscaledImagePath) && File.Exists(sc.UpscaledImagePath)
                    ? sc.UpscaledImagePath
                    : sc.LocalImagePath;
                if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath)) continue;
                var safeScene = string.Concat(sc.Title.Split(Path.GetInvalidFileNameChars())).Trim().Replace(' ', '_');
                var dst = Path.Combine(outDir, $"{i + 1:D2}_{safeScene}.png");
                File.Copy(srcPath, dst, overwrite: true);
            }

            Process.Start(new ProcessStartInfo { FileName = outDir, UseShellExecute = true });
            StatusMessage = $"Đã xuất {rendered.Count} ảnh truyện vào {outDir}!";
        }
        catch (Exception ex) { ReportError(ex); }
    }
}
