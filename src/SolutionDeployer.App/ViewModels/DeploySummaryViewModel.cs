using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.ViewModels;

/// <summary>One deployed target (profile or script) in the post-deploy summary.</summary>
public sealed record DeploySummaryTarget(string Name, bool IsSuccess, string Detail);

/// <summary>A project's targets in the post-deploy summary; successful only if all of them were.</summary>
public sealed record DeploySummaryProject(string Name, IReadOnlyList<DeploySummaryTarget> Targets)
{
    public bool IsSuccess => Targets.All(t => t.IsSuccess);

    public string StatusText => IsSuccess ? "✔ Succeeded" : "✘ Failed";
}

/// <summary>Backs the dialog shown after a deployment run: per-project success/failure.</summary>
public partial class DeploySummaryViewModel : ObservableObject
{
    public DeploySummaryViewModel(IReadOnlyList<DeploySummaryProject> projects)
    {
        Projects = projects;
        var targets = projects.SelectMany(p => p.Targets).ToList();
        var ok = targets.Count(t => t.IsSuccess);
        Heading = ok == targets.Count
            ? $"All {targets.Count} target(s) succeeded"
            : $"{ok} of {targets.Count} target(s) succeeded";
    }

    public IReadOnlyList<DeploySummaryProject> Projects { get; }

    public string Heading { get; }

    public event Action? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    /// <summary>Groups the run's results by project, keeping the order the jobs were queued in.</summary>
    public static DeploySummaryViewModel From(IReadOnlyList<PublishJob> jobs, IReadOnlyList<PublishResult> results)
    {
        var resultsById = results.ToDictionary(r => r.JobId);

        var projects = jobs
            .GroupBy(j => j.Project.ProjectPath)
            .Select(g => new DeploySummaryProject(
                g.First().Project.Name,
                g.Select(job =>
                {
                    var name = job.Profile?.Name ?? job.Script?.Name ?? "?";
                    if (!resultsById.TryGetValue(job.Id, out var result))
                        return new DeploySummaryTarget(name, false, "Not run");

                    var detail = result.Status switch
                    {
                        PublishStatus.Succeeded => $"Succeeded ({result.Duration.TotalSeconds:F1}s)",
                        PublishStatus.Cancelled => "Cancelled",
                        _ => result.ErrorMessage ?? result.Status.ToString(),
                    };
                    return new DeploySummaryTarget(name, result.IsSuccess, detail);
                }).ToList()))
            .ToList();

        return new DeploySummaryViewModel(projects);
    }
}
