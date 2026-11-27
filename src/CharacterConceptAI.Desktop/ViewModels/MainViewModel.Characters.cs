using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Scripts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CharacterConceptAI.Desktop.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private string _characterEditorName = string.Empty;
    [ObservableProperty] private string _characterEditorRole = string.Empty;
    [ObservableProperty] private string _characterEditorDescription = string.Empty;
    [ObservableProperty] private bool _hasUnsavedChanges;
    [ObservableProperty] private AttributeDefinitionDto? _selectedAttributeDefinition;
    [ObservableProperty] private string _newAttributeCode = string.Empty;
    [ObservableProperty] private string _newAttributeName = string.Empty;
    [ObservableProperty] private string _newAttributeDataType = "Text";
    [ObservableProperty] private string _newAttributeAllowedValues = string.Empty;
    [ObservableProperty] private string _newAttributeValue = string.Empty;
    [ObservableProperty] private string _newVariantName = string.Empty;
    [ObservableProperty] private string _newVariantDescription = string.Empty;
    [ObservableProperty] private string _newTemplateName = string.Empty;
    [ObservableProperty] private CharacterTemplateDto? _selectedTemplate;
    [ObservableProperty] private string _templatePreviewText = string.Empty;
    [ObservableProperty] private CharacterDto? _relationshipTargetCharacter;
    [ObservableProperty] private string _relationshipType = string.Empty;
    [ObservableProperty] private string _relationshipNotes = string.Empty;
    [ObservableProperty] private CharacterRevisionDto? _revisionFrom;
    [ObservableProperty] private CharacterRevisionDto? _revisionTo;
    [ObservableProperty] private bool _isEditingVariantAttributes;
    private Guid? _attributeEditingVariantId;
    public string AttributeEditScope => IsEditingVariantAttributes
        ? $"Đang sửa thuộc tính ghi đè của biến thể: {SelectedCharacterVariants.FirstOrDefault(v => v.Id == _attributeEditingVariantId)?.Name}"
        : "Đang sửa thuộc tính gốc của nhân vật";
    public bool CanSaveAttributes => CanEditCharacter && !IsEditingVariantAttributes;
    public bool CanSaveVariantAttributes => CanEditCharacter && IsEditingVariantAttributes && SelectedVariant?.Id == _attributeEditingVariantId;
    public ObservableCollection<AttributeEditor> AttributeEditors { get; } = [];
    public ObservableCollection<AttributeDefinitionDto> AttributeDefinitions { get; } = [];
    public ObservableCollection<CharacterTemplateDto> Templates { get; } = [];
    public ObservableCollection<CharacterRelationshipDto> Relationships { get; } = [];

    partial void OnCharacterEditorNameChanged(string value) => MarkDirty();
    partial void OnCharacterEditorRoleChanged(string value) => MarkDirty();
    partial void OnCharacterEditorDescriptionChanged(string value) => MarkDirty();
    private void MarkDirty() { if (!_resettingEditor && SelectedCharacter != null) HasUnsavedChanges = true; }

    [RelayCommand]
    private void ResetCharacterEditor()
    {
        _resettingEditor = true;
        try
        {
            CharacterEditorName = SelectedCharacter?.Name ?? string.Empty;
            CharacterEditorRole = SelectedCharacter?.Role ?? string.Empty;
            CharacterEditorDescription = SelectedCharacter?.Description ?? string.Empty;
            RebuildAttributeEditors(); HasUnsavedChanges = false; EditorError = string.Empty;
        }
        finally { _resettingEditor = false; }
    }

    private void RebuildAttributeEditors(bool useVariant = false)
    {
        _attributeEditingVariantId = useVariant ? SelectedVariant?.Id : null;
        IsEditingVariantAttributes = useVariant;
        OnPropertyChanged(nameof(AttributeEditScope));
        NotifyWorkspaceState();
        AttributeEditors.Clear();
        var values = useVariant ? SelectedVariant?.EffectiveAttributes : SelectedCharacter?.Attributes;
        foreach (var attr in values ?? [])
        {
            var editor = new AttributeEditor(attr, AttributeDefinitions.FirstOrDefault(d => d.Id == attr.DefinitionId));
            editor.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AttributeEditor.Value) or nameof(AttributeEditor.BooleanValue)) MarkDirty(); };
            AttributeEditors.Add(editor);
        }
    }

    private async Task LoadAdvancedProjectDataAsync(Guid projectId)
    {
        var definitions = await _api.GetAttributeDefinitionsAsync(projectId);
        var templates = await _api.GetTemplatesAsync(projectId);
        var relationships = await _api.GetRelationshipsAsync(projectId);
        if (SelectedProject?.Id != projectId) return;
        AttributeDefinitions.Clear(); foreach (var item in definitions) AttributeDefinitions.Add(item);
        Templates.Clear(); foreach (var item in templates) Templates.Add(item);
        Relationships.Clear(); foreach (var item in relationships) Relationships.Add(item);
        if (!HasUnsavedChanges) RebuildAttributeEditors();
    }

    private async Task<bool> EditCharacterAsync(Func<Guid, Guid, Task<bool>> action, bool savesProfile = false, bool savesAttributes = false)
    {
        if (!CanEditProject || SelectedProject == null || SelectedCharacter == null) return false;
        var projectId = SelectedProject.Id; var characterId = SelectedCharacter.Id;
        var nameDraft = CharacterEditorName; var roleDraft = CharacterEditorRole; var descriptionDraft = CharacterEditorDescription;
        var hasProfileDraft = nameDraft != SelectedCharacter.Name || roleDraft != SelectedCharacter.Role || descriptionDraft != (SelectedCharacter.Description ?? string.Empty);
        var attributeDrafts = AttributeEditors.Where(a => a.IsModified).ToDictionary(a => a.Code, a => a.Value);
        var variantId = SelectedVariant?.Id;
        var variantScope = IsEditingVariantAttributes;
        IsBusy = true; EditorError = string.Empty;
        try
        {
            if (!await action(projectId, characterId)) { EditorError = StatusMessage = ApiError("Không lưu được thay đổi. Thông tin đã nhập vẫn được giữ lại."); return false; }
            if (SelectedProject?.Id != projectId || SelectedCharacter?.Id != characterId) return true;
            var updated = await _api.GetCharacterAsync(projectId, characterId);
            if (SelectedProject?.Id != projectId || SelectedCharacter?.Id != characterId) return true;
            if (updated != null)
            {
                HasUnsavedChanges = false;
                _restoringSelection = true;
                try
                {
                    var index = Characters.ToList().FindIndex(c => c.Id == characterId);
                    if (index >= 0) Characters[index] = updated;
                    SelectedCharacter = updated;
                    SelectedCharacterAttributes.Clear(); foreach (var attribute in updated.Attributes) SelectedCharacterAttributes.Add(attribute);
                    SelectedCharacterVariants.Clear(); foreach (var variant in updated.Variants) SelectedCharacterVariants.Add(variant);
                    ResetCharacterEditor();
                }
                finally { _restoringSelection = false; }
                SelectedVariant = updated.Variants.FirstOrDefault(v => v.Id == variantId) ?? SelectedVariant;
                if (!savesAttributes && attributeDrafts.Count > 0)
                {
                    RebuildAttributeEditors(variantScope);
                    foreach (var editor in AttributeEditors)
                        if (attributeDrafts.TryGetValue(editor.Code, out var draft)) editor.Value = draft;
                }
                _lastPreviewKey = null;
                Observe(LoadRevisionsAsync(projectId,characterId));
                NotifyWorkspaceState();
                if (!savesProfile && hasProfileDraft)
                {
                    CharacterEditorName = nameDraft; CharacterEditorRole = roleDraft; CharacterEditorDescription = descriptionDraft;
                }
            }
            StatusMessage = "Đã lưu thay đổi.";
            return true;
        }
        catch (Exception ex) { ReportError(ex); return false; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEditCharacter))]
    private async Task SaveCharacterProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(CharacterEditorName) || string.IsNullOrWhiteSpace(CharacterEditorRole))
        { EditorError = "Tên và vai trò nhân vật không được để trống."; return; }
        var request = new UpdateCharacterRequest(CharacterEditorName.Trim(), CharacterEditorRole.Trim(), CharacterEditorDescription, SelectedCharacter?.RowVersion ?? string.Empty);
        await EditCharacterAsync(async (p,c) => await _api.UpdateCharacterAsync(p,c,request) != null, savesProfile: true);
    }

    [RelayCommand(CanExecute = nameof(CanSaveAttributes))]
    private async Task SaveAttributesAsync()
    {
        try
        {
            var request = new SetCharacterAttributesRequest(AttributeEditors.ToDictionary(a => a.Code, a => a.SerializeValue()));
            await EditCharacterAsync(async (p,c) => await _api.SetCharacterAttributesAsync(p,c,request) != null, savesAttributes: true);
        }
        catch (FormatException ex) { EditorError = ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task CreateAttributeDefinitionAsync()
    {
        if (SelectedProject == null || string.IsNullOrWhiteSpace(NewAttributeCode) || string.IsNullOrWhiteSpace(NewAttributeName))
        { EditorError = "Nhập mã và tên hiển thị cho thuộc tính."; return; }
        string? allowed = null;
        if (NewAttributeDataType is "SingleSelect" or "MultiSelect")
        {
            var values = NewAttributeAllowedValues.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (values.Length == 0) { EditorError = "Nhập các lựa chọn, phân cách bằng dấu phẩy."; return; }
            allowed = JsonSerializer.Serialize(values);
        }
        var projectId = SelectedProject.Id; IsBusy = true;
        try
        {
            var item = await _api.CreateAttributeDefinitionAsync(projectId, new CreateAttributeDefinitionRequest(NewAttributeCode.Trim(), NewAttributeName.Trim(), NewAttributeDataType, allowed));
            if (item == null) { EditorError = ApiError("Không tạo được định nghĩa thuộc tính."); return; }
            if (SelectedProject?.Id != projectId) return;
            AttributeDefinitions.Add(item); SelectedAttributeDefinition = item;
            NewAttributeCode = NewAttributeName = string.Empty; StatusMessage = "Đã tạo định nghĩa thuộc tính.";
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task AssignAttributeAsync()
    {
        if (SelectedAttributeDefinition == null) { EditorError = "Chọn một định nghĩa thuộc tính."; return; }
        var definition = SelectedAttributeDefinition;
        var editor = new AttributeEditor(new CharacterAttributeValueDto(definition.Id,definition.Code,definition.DisplayName,definition.DataType,null,definition.IsIdentityCritical),definition) { Value = NewAttributeValue };
        try
        {
            var request = new SetCharacterAttributesRequest(new() { [definition.Code] = editor.SerializeValue() });
            await EditCharacterAsync(async (p,c) => await _api.SetCharacterAttributesAsync(p,c,request) != null);
        }
        catch (FormatException ex) { EditorError = ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanEditCharacter))]
    private async Task CreateVariantAsync()
    {
        if (string.IsNullOrWhiteSpace(NewVariantName)) { EditorError = "Nhập tên biến thể."; return; }
        if (await EditCharacterAsync(async (p,c) => await _api.CreateVariantAsync(p,c,new CreateVariantRequest(NewVariantName.Trim(),NewVariantDescription)) != null))
            NewVariantName = NewVariantDescription = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanEditCharacter))]
    private async Task MakeDefaultVariantAsync()
    {
        if (SelectedVariant == null) return;
        var variantId = SelectedVariant.Id;
        await EditCharacterAsync(async (p,c) => await _api.SetDefaultVariantAsync(p,c,variantId) != null);
    }

    [RelayCommand(CanExecute = nameof(CanSaveVariantAttributes))]
    private async Task SaveVariantAttributesAsync()
    {
        if (SelectedVariant == null) { EditorError = "Chọn biến thể cần lưu ghi đè."; return; }
        try
        {
            var variantId = SelectedVariant.Id;
            var request = new SetVariantAttributesRequest(AttributeEditors.ToDictionary(a => a.Code,a => a.SerializeValue()));
            await EditCharacterAsync(async (p,c) => await _api.SetVariantAttributesAsync(p,c,variantId,request) != null, savesAttributes: true);
        }
        catch (FormatException ex) { EditorError = ex.Message; }
    }

    [RelayCommand]
    private void LoadVariantAttributes()
    {
        if (SelectedVariant != null && ConfirmDiscardChanges()) RebuildAttributeEditors(true);
    }

    [RelayCommand]
    private void LoadBaseAttributes()
    {
        if (ConfirmDiscardChanges()) RebuildAttributeEditors();
    }

    partial void OnSelectedVariantChanged(CharacterVariantDto? value) => NotifyWorkspaceState();

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task SaveTemplateAsync()
    {
        if (SelectedProject == null || string.IsNullOrWhiteSpace(NewTemplateName)) { EditorError = "Nhập tên mẫu nhân vật."; return; }
        var projectId = SelectedProject.Id;
        IsBusy = true;
        try
        {
            var items = AttributeEditors.Select(a => new CreateTemplateAttributeItemRequest(a.DefinitionId, a.SerializeValue())).ToList();
            var item = await _api.CreateTemplateAsync(projectId,new CreateTemplateRequest(NewTemplateName.Trim(),null,items));
            if (!IsLoggedIn || SelectedProject?.Id != projectId) return;
            if (item == null) { EditorError = ApiError("Không lưu được mẫu."); return; }
            Templates.Add(item); SelectedTemplate = item; NewTemplateName = string.Empty; StatusMessage = "Đã lưu mẫu nhân vật.";
        }
        catch (FormatException ex) { EditorError = ex.Message; }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task PreviewTemplateAsync()
    {
        if (SelectedProject == null || SelectedCharacter == null || SelectedTemplate == null) return;
        var projectId = SelectedProject.Id; var characterId = SelectedCharacter.Id; var templateId = SelectedTemplate.Id;
        try
        {
            var preview = await _api.PreviewApplyTemplateAsync(projectId,characterId,new ApplyTemplateRequest(templateId));
            if (!IsLoggedIn || SelectedProject?.Id != projectId || SelectedCharacter?.Id != characterId || SelectedTemplate?.Id != templateId) return;
            TemplatePreviewText = preview == null ? ApiError("Không tải được bản xem trước.") : string.Join("\n",preview.PreviewItems.Select(a => $"{a.DisplayName}: {a.CurrentValue ?? "(trống)"} → {a.NewValue ?? "(trống)"} {(a.WillChange ? "· sẽ thay đổi" : "· giữ nguyên")}"));
        }
        catch (Exception ex) { ReportError(ex); }
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task ApplyTemplateAsync()
    {
        if (SelectedTemplate == null) return;
        var templateId = SelectedTemplate.Id;
        await EditCharacterAsync(async (p,c) => await _api.ApplyTemplateAsync(p,c,new ApplyTemplateRequest(templateId)) != null);
    }

    [RelayCommand(CanExecute = nameof(CanEditCharacter))]
    private async Task CreateRelationshipAsync()
    {
        if (SelectedProject == null || SelectedCharacter == null || RelationshipTargetCharacter == null || string.IsNullOrWhiteSpace(RelationshipType))
        { EditorError = "Chọn nhân vật đích và nhập loại quan hệ."; return; }
        if (RelationshipTargetCharacter.Id == SelectedCharacter.Id) { EditorError = "Nhân vật không thể có quan hệ với chính mình."; return; }
        var projectId = SelectedProject.Id; var characterId = SelectedCharacter.Id;
        var request = new CreateCharacterRelationshipRequest(characterId,RelationshipTargetCharacter.Id,RelationshipType.Trim(),RelationshipNotes);
        IsBusy = true;
        try
        {
            var item = await _api.CreateRelationshipAsync(projectId,request);
            if (!IsLoggedIn || SelectedProject?.Id != projectId || SelectedCharacter?.Id != characterId) return;
            if (item == null) { EditorError = ApiError("Không tạo được quan hệ."); return; }
            Relationships.Add(item); RelationshipType = RelationshipNotes = string.Empty; StatusMessage = "Đã thêm quan hệ giữa hai nhân vật.";
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task DeleteRelationshipAsync(CharacterRelationshipDto? relationship)
    {
        if (SelectedProject == null || relationship == null) return;
        if (MessageBox.Show($"Xóa quan hệ {relationship.SourceCharacterName} → {relationship.TargetCharacterName}?","Xóa quan hệ",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            if (await _api.DeleteRelationshipAsync(SelectedProject.Id,relationship.Id)) Relationships.Remove(relationship);
            else EditorError = ApiError("Không xóa được quan hệ.");
        }
        catch (Exception ex) { ReportError(ex); }
    }

    [ObservableProperty] private string _scriptEditorTitle = string.Empty;
    [ObservableProperty] private bool _hasUnsavedScriptChanges;
    [ObservableProperty] private bool _hasUnsavedSceneChanges;
    [ObservableProperty] private string _scriptEditorSummary = string.Empty;
    [ObservableProperty] private string _sceneEditorTitle = string.Empty;
    [ObservableProperty] private string _sceneEditorSetting = string.Empty;
    [ObservableProperty] private string _sceneEditorContent = string.Empty;
    public ObservableCollection<SceneCharacterLinkEditor> SceneCharacterLinks { get; } = [];

    partial void OnScriptEditorTitleChanged(string value) { if (!_resettingEditor && SelectedScript != null) HasUnsavedScriptChanges = true; }
    partial void OnScriptEditorSummaryChanged(string value) { if (!_resettingEditor && SelectedScript != null) HasUnsavedScriptChanges = true; }
    partial void OnSceneEditorTitleChanged(string value) { if (!_resettingEditor && SelectedScene != null) HasUnsavedSceneChanges = true; }
    partial void OnSceneEditorSettingChanged(string value) { if (!_resettingEditor && SelectedScene != null) HasUnsavedSceneChanges = true; }
    partial void OnSceneEditorContentChanged(string value) { if (!_resettingEditor && SelectedScene != null) HasUnsavedSceneChanges = true; }

    private void ResetScriptAndSceneEditors()
    {
        _resettingEditor = true;
        try
        {
            ScriptEditorTitle = SelectedScript?.Title ?? string.Empty;
            ScriptEditorSummary = SelectedScript?.Summary ?? string.Empty;
            ResetSceneEditor();
            HasUnsavedScriptChanges = HasUnsavedSceneChanges = false;
        }
        finally { _resettingEditor = false; }
    }

    private void ResetSceneEditor()
    {
        var value = SelectedScene;
        SceneEditorTitle = value?.Title ?? string.Empty; SceneEditorSetting = value?.Setting ?? string.Empty; SceneEditorContent = value?.Content ?? string.Empty;
        SceneCharacterLinks.Clear();
        foreach (var character in Characters)
        {
            var link = new SceneCharacterLinkEditor(character.Id,character.Name,value?.LinkedCharacterIds.Contains(character.Id) == true);
            link.PropertyChanged += (_,_) => { if (!_resettingEditor && SelectedScene != null) HasUnsavedSceneChanges = true; };
            SceneCharacterLinks.Add(link);
        }
        HasUnsavedSceneChanges = false;
    }

    partial void OnSelectedSceneChanged(SceneDto? oldValue, SceneDto? newValue)
    {
        var value = newValue;
        if (_restoringSelection) return;
        if (!ConfirmDiscardChanges()) { _restoringSelection = true; try { SelectedScene = oldValue; } finally { _restoringSelection = false; } return; }
        _resettingEditor = true;
        try { ResetSceneEditor(); } finally { _resettingEditor = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task SaveScriptAsync()
    {
        if (SelectedProject == null || SelectedScript == null || string.IsNullOrWhiteSpace(ScriptEditorTitle)) { EditorError = "Chọn kịch bản và nhập tiêu đề."; return; }
        var projectId = SelectedProject.Id; var original = SelectedScript;
        var request = new UpdateScriptRequest(ScriptEditorTitle.Trim(),ScriptEditorSummary,original.RowVersion ?? string.Empty);
        IsBusy = true;
        try
        {
            var item = await _api.UpdateScriptAsync(projectId,original.Id,request);
            if (SelectedProject?.Id != projectId || SelectedScript?.Id != original.Id) return;
            if (item == null) { EditorError = ApiError("Không lưu được kịch bản. Nội dung đã nhập vẫn được giữ lại."); return; }
            _restoringSelection = _resettingEditor = true;
            try
            {
                var index = Scripts.IndexOf(original); if (index >= 0) Scripts[index] = item; SelectedScript = item;
                ScriptEditorTitle = item.Title; ScriptEditorSummary = item.Summary ?? string.Empty;
                HasUnsavedScriptChanges = false;
            }
            finally { _restoringSelection = _resettingEditor = false; }
            StatusMessage = "Đã lưu kịch bản.";
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task SaveSceneAsync()
    {
        if (SelectedProject == null || SelectedScript == null || SelectedScene == null || string.IsNullOrWhiteSpace(SceneEditorTitle)) { EditorError = "Chọn phân cảnh và nhập tiêu đề."; return; }
        var projectId = SelectedProject.Id; var scriptId = SelectedScript.Id; var original = SelectedScene;
        var draftLinks = SceneCharacterLinks.ToDictionary(link => link.CharacterId,link => link.IsLinked);
        var request = new UpdateSceneRequest(SceneEditorTitle.Trim(),SceneEditorSetting,SceneEditorContent,original.RowVersion ?? string.Empty);
        IsBusy = true;
        try
        {
            var item = await _api.UpdateSceneAsync(projectId,scriptId,original.Id,request);
            if (SelectedProject?.Id != projectId || SelectedScript?.Id != scriptId || SelectedScene?.Id != original.Id) return;
            if (item == null) { EditorError = ApiError("Không lưu được phân cảnh. Nội dung đã nhập vẫn được giữ lại."); return; }
            ApplySceneResult(item);
            foreach (var link in SceneCharacterLinks)
                if (draftLinks.TryGetValue(link.CharacterId,out var linked)) link.IsLinked = linked;
            StatusMessage = "Đã lưu phân cảnh. Các liên kết chưa lưu được giữ lại.";
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

    private void ApplySceneResult(SceneDto item)
    {
        _restoringSelection = _resettingEditor = true;
        try
        {
            var index = SelectedScene == null ? -1 : Scenes.IndexOf(SelectedScene);
            if (index >= 0) Scenes[index] = item;
            SelectedScene = item; ResetSceneEditor();
        }
        finally { _restoringSelection = _resettingEditor = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEditProject))]
    private async Task SaveSceneLinksAsync()
    {
        if (SelectedProject == null || SelectedScript == null || SelectedScene == null) return;
        var projectId = SelectedProject.Id; var scriptId = SelectedScript.Id; var original = SelectedScene;
        var titleDraft = SceneEditorTitle; var settingDraft = SceneEditorSetting; var contentDraft = SceneEditorContent;
        var hadTextDraft = titleDraft != original.Title || settingDraft != (original.Setting ?? string.Empty) || contentDraft != (original.Content ?? string.Empty);
        var visibleCharacterIds = SceneCharacterLinks.Select(c => c.CharacterId).ToHashSet();
        var characterIds = original.LinkedCharacterIds.Where(id => !visibleCharacterIds.Contains(id))
            .Concat(SceneCharacterLinks.Where(c => c.IsLinked).Select(c => c.CharacterId)).Distinct().ToList();
        var request = new UpdateSceneCharactersRequest(characterIds);
        IsBusy = true;
        try
        {
            var ok = await _api.UpdateSceneCharactersAsync(projectId,scriptId,original.Id,request);
            if (!ok) { EditorError = ApiError("Không cập nhật được liên kết nhân vật."); return; }
            var item = await _api.GetSceneAsync(projectId,scriptId,original.Id);
            if (SelectedProject?.Id != projectId || SelectedScript?.Id != scriptId || SelectedScene?.Id != original.Id) return;
            if (item != null)
            {
                ApplySceneResult(item);
                if (hadTextDraft) { SceneEditorTitle = titleDraft; SceneEditorSetting = settingDraft; SceneEditorContent = contentDraft; }
            }
            StatusMessage = "Đã lưu nhân vật xuất hiện trong phân cảnh. Nội dung chưa lưu được giữ lại.";
        }
        catch (Exception ex) { ReportError(ex); }
        finally { IsBusy = false; }
    }

}

public partial class SceneCharacterLinkEditor(Guid characterId,string name,bool isLinked) : ObservableObject
{
    public Guid CharacterId { get; } = characterId;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isLinked = isLinked;
}

public partial class AttributeEditor : ObservableObject
{
    private readonly string _originalValue;
    public bool IsModified => Value != _originalValue;
    public Guid DefinitionId { get; }
    public string Code { get; }
    public string DisplayName { get; }
    public string DataType { get; }
    public bool IsIdentityCritical { get; }
    public bool IsBoolean => DataType == "Boolean";
    public bool IsChoice => DataType == "SingleSelect";
    public bool IsNumeric => DataType == "Number";
    public bool IsText => !IsBoolean && !IsChoice;
    public IReadOnlyList<string> Choices { get; }
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private bool _booleanValue;

    partial void OnBooleanValueChanged(bool value) => Value = value ? "true" : "false";
    partial void OnValueChanged(string value)
    {
        if (IsBoolean && bool.TryParse(value, out var parsed) && BooleanValue != parsed) BooleanValue = parsed;
    }

    public AttributeEditor(CharacterAttributeValueDto value, AttributeDefinitionDto? definition)
    {
        DefinitionId=value.DefinitionId; Code=value.Code; DisplayName=value.DisplayName; DataType=value.DataType; IsIdentityCritical=value.IsIdentityCritical;
        try { Choices = JsonSerializer.Deserialize<string[]>(definition?.AllowedValuesJson ?? "[]") ?? []; } catch (JsonException) { Choices = []; }
        try
        {
            using var doc = JsonDocument.Parse(value.ValueJson ?? "null");
            Value = doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString() ?? string.Empty,
                JsonValueKind.Null => string.Empty,
                JsonValueKind.Array => string.Join(", ", doc.RootElement.EnumerateArray().Select(v=>v.GetString())),
                _ => doc.RootElement.ToString()
            };
        }
        catch (JsonException) { Value = value.ValueJson ?? string.Empty; }
        BooleanValue = bool.TryParse(Value,out var boolean) && boolean;
        _originalValue = Value;
    }

    public string? SerializeValue()
    {
        if (IsBoolean) return JsonSerializer.Serialize(BooleanValue);
        if (DataType == "Number")
        {
            if (!decimal.TryParse(Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)) throw new FormatException($"{DisplayName}: cần nhập một số hợp lệ (dấu thập phân '.').");
            return JsonSerializer.Serialize(number);
        }
        if (DataType == "SingleSelect" && !Choices.Contains(Value)) throw new FormatException($"{DisplayName}: chọn một giá trị trong danh sách.");
        if (DataType == "MultiSelect")
        {
            var values = Value.Split(',',StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (Choices.Count > 0 && values.Any(v=>!Choices.Contains(v))) throw new FormatException($"{DisplayName}: một lựa chọn không có trong định nghĩa.");
            return JsonSerializer.Serialize(values);
        }
        return JsonSerializer.Serialize(Value);
    }
}
