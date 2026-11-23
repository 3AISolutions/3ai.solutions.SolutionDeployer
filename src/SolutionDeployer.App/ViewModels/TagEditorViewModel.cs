using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.ViewModels;

/// <summary>The "Edit tags" dialog for one deploy target: add tags (suggesting ones already in use), remove with ×.</summary>
public sealed partial class TagEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> _knownTags;

    public TagEditorViewModel(string targetName, IEnumerable<string> tags, IEnumerable<string> knownTags)
    {
        TargetName = targetName;
        _knownTags = knownTags.ToList();
        foreach (var tag in AppSettings.NormalizeTags(tags))
            Tags.Add(tag);
        Tags.CollectionChanged += (_, _) => RefreshSuggestions();
        RefreshSuggestions();
    }

    public string TargetName { get; }

    public string Title => $"Tags for {TargetName}";

    public ObservableCollection<string> Tags { get; } = [];

    /// <summary>Tags used on other targets that this one doesn't have yet (the add box's autocomplete).</summary>
    public ObservableCollection<string> Suggestions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string? _newTag;

    /// <summary>The tags to save, or null when the dialog was cancelled.</summary>
    public IReadOnlyList<string>? Result { get; private set; }

    public event Action? CloseRequested;

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewTag);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        // A comma-separated entry adds several tags at once.
        foreach (var tag in AppSettings.NormalizeTags((NewTag ?? string.Empty).Split(',')))
        {
            if (!Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                Tags.Add(tag);
        }
        NewTag = string.Empty;
    }

    [RelayCommand]
    private void Remove(string? tag)
    {
        if (tag is not null)
            Tags.Remove(tag);
    }

    [RelayCommand]
    private void Save()
    {
        if (CanAdd())
            Add(); // typed but not added yet: the user still means it
        Result = Tags.ToList();
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke();
    }

    private void RefreshSuggestions()
    {
        Suggestions.Clear();
        foreach (var tag in _knownTags.Where(k => !Tags.Any(t => string.Equals(t, k, StringComparison.OrdinalIgnoreCase))))
            Suggestions.Add(tag);
    }
}
