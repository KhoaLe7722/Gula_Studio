using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Concepts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CharacterConceptAI.Desktop.ViewModels;

public partial class MainViewModel
{
    private CancellationTokenSource? _characterQueryCts;
    private int _characterQueryRevision;
    private int _conceptQueryRevision;
    [ObservableProperty] private int _characterPage = 1;
    [ObservableProperty] private int _characterTotalCount;
    [ObservableProperty] private int _conceptPage = 1;
    [ObservableProperty] private int _conceptTotalCount;
    [ObservableProperty] private string _conceptStatusFilter = "Tất cả";
    public string[] ConceptStatusFilters { get; } = ["Tất cả", "Chờ duyệt", "Đã duyệt", "Bị từ chối"];
    public bool HasPreviousCharacterPage => CharacterPage > 1;
    public bool HasNextCharacterPage => CharacterPage * 24 < CharacterTotalCount;
    public bool HasPreviousConceptPage => ConceptPage > 1;
    public bool HasNextConceptPage => ConceptPage * 24 < ConceptTotalCount;
    public string CharacterPageLabel => $"Trang {CharacterPage} / {Math.Max(1,(int)Math.Ceiling(CharacterTotalCount / 24d))} · {CharacterTotalCount} nhân vật";
    public string ConceptPageLabel => SelectedCharacter != null ? $"Concept của {SelectedCharacter.Name} · {ConceptTotalCount} mục"
        : $"Trang {ConceptPage} / {Math.Max(1,(int)Math.Ceiling(ConceptTotalCount / 24d))} · {ConceptTotalCount} concept";

    partial void OnCharacterPageChanged(int value) => NotifyLibraryPagination();
    partial void OnCharacterTotalCountChanged(int value) => NotifyLibraryPagination();
    partial void OnConceptPageChanged(int value) => NotifyLibraryPagination();
    partial void OnConceptTotalCountChanged(int value) => NotifyLibraryPagination();
    partial void OnConceptStatusFilterChanged(string value)
    {
        ConceptPage = 1;
        Observe(RefreshConceptsAsync());
    }

    private void NotifyLibraryPagination()
    {
        foreach (var name in new[] { nameof(CharacterPageLabel),nameof(ConceptPageLabel),nameof(HasPreviousCharacterPage),nameof(HasNextCharacterPage),nameof(HasPreviousConceptPage),nameof(HasNextConceptPage) }) OnPropertyChanged(name);
        PreviousCharacterPageCommand.NotifyCanExecuteChanged(); NextCharacterPageCommand.NotifyCanExecuteChanged();
        PreviousConceptPageCommand.NotifyCanExecuteChanged(); NextConceptPageCommand.NotifyCanExecuteChanged();
    }

    private async void ScheduleCharacterQuery()
    {
        _characterQueryCts?.Cancel(); _characterQueryCts?.Dispose();
        _characterQueryCts = new CancellationTokenSource(); var ct = _characterQueryCts.Token;
        ++_characterQueryRevision;
        try
        {
            await Task.Delay(300,ct);
            if (SelectedProject == null || !ConfirmDiscardChanges()) return;
            CharacterPage = 1; await LoadCharactersAsync(SelectedProject.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError(ex); }
    }

    [RelayCommand(CanExecute = nameof(HasPreviousCharacterPage))]
    private async Task PreviousCharacterPageAsync()
    {
        if (SelectedProject == null || !ConfirmDiscardChanges()) return;
        CharacterPage--; await LoadCharactersAsync(SelectedProject.Id);
    }
    [RelayCommand(CanExecute = nameof(HasNextCharacterPage))]
    private async Task NextCharacterPageAsync()
    {
        if (SelectedProject == null || !ConfirmDiscardChanges()) return;
        CharacterPage++; await LoadCharactersAsync(SelectedProject.Id);
    }
    [RelayCommand(CanExecute = nameof(HasPreviousConceptPage))]
    private async Task PreviousConceptPageAsync() { ConceptPage--; await RefreshConceptsAsync(); }
    [RelayCommand(CanExecute = nameof(HasNextConceptPage))]
    private async Task NextConceptPageAsync() { ConceptPage++; await RefreshConceptsAsync(); }

    [RelayCommand]
    private void ClearCharacterFilter()
    {
        if (!ConfirmDiscardChanges()) return;
        SelectedCharacter = null; ConceptPage = 1;
        Observe(RefreshConceptsAsync());
    }

    private string? ConceptServerStatus => ConceptStatusFilter switch
    { "Chờ duyệt" => "PendingReview", "Đã duyệt" => "Approved", "Bị từ chối" => "Rejected", _ => null };

    private async Task LoadProjectConceptsAsync(Guid projectId)
    {
        var revision = ++_conceptQueryRevision;
        var result = await _api.GetProjectConceptsPagedAsync(projectId,ConceptServerStatus,ConceptPage,24);
        if (SelectedProject?.Id != projectId || revision != _conceptQueryRevision || SelectedCharacter != null) return;
        if (result == null) { EditorError = StatusMessage = ApiError("Không tải được thư viện của dự án."); return; }
        var selectedId = SelectedConcept?.Id;
        CharacterConcepts.Clear(); foreach (var item in result.Items) CharacterConcepts.Add(item);
        ConceptTotalCount = result.TotalCount;
        SelectedConcept = result.Items.FirstOrDefault(c=>c.Id == selectedId) ?? result.Items.FirstOrDefault();
        Observe(LoadConceptThumbnailsAsync(projectId, result.Items));
    }
}
