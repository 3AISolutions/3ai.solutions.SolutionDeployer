using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.Core.Configuration;

namespace SolutionDeployer.App.ViewModels;

/// <summary>
/// The "Edit tags" dialog for one deploy target: add tags by typing or by clicking one already in use, remove with ×.
/// Tags are kept sorted, as the rows show them.
/// </summary>
public sealed partial class TagEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<string> _knownTags;

    public TagEditorViewModel(string targetName, IEnumerable<string> tags, IEnumerable<string> knownTags)
    {
        TargetName = targetName;
        _knownTags = knownTags.ToList();
        foreach (var tag in AppSettings.NormalizeTags(tags))
            Insert(tag);
        Tags.CollectionChanged += (_, _) => RefreshSuggestions();
        RefreshSuggestions();
    }

    public string TargetName { get; }

    public string Title => $"Tags for {TargetName}";

    public ObservableCollection<string> Tags { get; } = [];

    /// <summary>
    /// Tags used on other targets that this one doesn't have yet: the add box's autocomplete and the clickable
    /// "existing tags" chips.
    /// </summary>
    public ObservableCollection<string> Suggestions { get; } = [];

    public bool HasSuggestions => Suggestions.Count > 0;

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
            Insert(tag);
        NewTag = string.Empty;
    }

    /// <summary>Adds a tag already used on another target (a click on its chip).</summary>
    [RelayCommand]
    private void AddExisting(string? tag)
    {
        if (!string.IsNullOrWhiteSpace(tag))
            Insert(tag.Trim());
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

    /// <summary>Adds <paramref name="tag"/> at its sorted place, unless the target has it already (in any casing).</summary>
    private void Insert(string tag)
    {
        if (Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            return;
        var index = 0;
        while (index < Tags.Count && TagList.Comparer.Compare(Tags[index], tag) < 0)
            index++;
        Tags.Insert(index, tag);
    }

    private void RefreshSuggestions()
    {
        Suggestions.Clear();
        foreach (var tag in _knownTags
                     .Where(k => !Tags.Any(t => string.Equals(t, k, StringComparison.OrdinalIgnoreCase)))
                     .Order(TagList.Comparer))
            Suggestions.Add(tag);
        OnPropertyChanged(nameof(HasSuggestions));
    }
}
