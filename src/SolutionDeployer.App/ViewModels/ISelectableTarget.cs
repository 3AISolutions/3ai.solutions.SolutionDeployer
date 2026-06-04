using SolutionDeployer.Core.Models;

namespace SolutionDeployer.App.ViewModels;

/// <summary>A deploy target the user can tick and that shows run status: a publish profile or a script.</summary>
public interface ISelectableTarget
{
    bool IsSelected { get; set; }

    PublishStatus Status { get; set; }

    string ResultText { get; set; }
}
