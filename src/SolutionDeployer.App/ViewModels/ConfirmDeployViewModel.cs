using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SolutionDeployer.App.ViewModels;

/// <summary>Backs the pre-deploy confirmation dialog: lists the targets and returns OK/Cancel.</summary>
public partial class ConfirmDeployViewModel : ObservableObject
{
    public ConfirmDeployViewModel(IEnumerable<string> targets, bool runInParallel)
    {
        foreach (var t in targets)
            Targets.Add(t);
        ModeText = runInParallel ? "in parallel" : "sequentially";
    }

    public ObservableCollection<string> Targets { get; } = [];

    public string Heading => $"Deploy {Targets.Count} target(s) {ModeText}?";

    public string ModeText { get; }

    /// <summary>When true, the caller should stop asking for confirmation in future.</summary>
    [ObservableProperty]
    private bool _dontAskAgain;

    public bool Confirmed { get; private set; }

    public event Action? CloseRequested;

    [RelayCommand]
    private void Confirm()
    {
        Confirmed = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        CloseRequested?.Invoke();
    }
}
