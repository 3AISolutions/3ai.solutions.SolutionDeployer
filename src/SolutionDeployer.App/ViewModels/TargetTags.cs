using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SolutionDeployer.App.ViewModels;

/// <summary>A deploy-target row that carries user tags (a publish profile or a script).</summary>
public interface ITaggedTarget
{
    /// <summary>The settings key the tags are stored under (<c>AppSettings.TargetTags</c>).</summary>
    string TagKey { get; }

    TagList Tags { get; }
}

/// <summary>A row's tags, shown as chips, always sorted so every row lists them the same way.</summary>
public sealed class TagList : ObservableCollection<string>
{
    /// <summary>The order tags are shown in: alphabetical ignoring case (ties by exact spelling, so it's stable).</summary>
    public static readonly IComparer<string> Comparer = Comparer<string>.Create((a, b) =>
    {
        var byName = StringComparer.OrdinalIgnoreCase.Compare(a, b);
        return byName != 0 ? byName : StringComparer.Ordinal.Compare(a, b);
    });

    public void Replace(IEnumerable<string> tags)
    {
        Clear();
        foreach (var tag in tags.Order(Comparer))
            Add(tag);
    }

    public bool ContainsTag(string tag) => this.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One tag in the list's tag filter; ticking it narrows the tree to targets with that tag.</summary>
public sealed partial class TagFilterOption(string tag, bool isSelected = false) : ObservableObject
{
    public string Tag { get; } = tag;

    [ObservableProperty]
    private bool _isSelected = isSelected;
}
