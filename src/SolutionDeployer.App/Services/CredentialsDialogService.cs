using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using SolutionDeployer.App.ViewModels;
using SolutionDeployer.App.Views;
using SolutionDeployer.Core.Configuration;
using SolutionDeployer.Core.Publishing;

namespace SolutionDeployer.App.Services;

public sealed class CredentialsDialogService(
    IClassicDesktopStyleApplicationLifetime lifetime,
    SettingsStore settingsStore,
    ICredentialStore credentialStore,
    CredentialTester tester) : ICredentialsDialogService
{
    public async Task<CredentialsDialogResult> EditAsync(
        AppSettings settings, CredentialsDialogRequest request, Func<string, string> describeTarget)
    {
        var owner = lifetime.MainWindow;
        if (owner is null)
            return new CredentialsDialogResult(false, false);

        var (test, reason) = TestFor(request);
        var vm = new TargetCredentialsViewModel(settings, settingsStore, credentialStore, request, test, reason);
        var window = new TargetCredentialsWindow { DataContext = vm };
        vm.CloseRequested += () => window.Close();
        vm.ManageLibraryHandler = () => ShowLibraryAsync(settings, describeTarget, window);

        await window.ShowDialog(owner);
        return new CredentialsDialogResult(vm.Saved, vm.LibraryChanged);
    }

    public Task<bool> ShowLibraryAsync(AppSettings settings, Func<string, string> describeTarget) =>
        lifetime.MainWindow is { } owner ? ShowLibraryAsync(settings, describeTarget, owner) : Task.FromResult(false);

    private async Task<bool> ShowLibraryAsync(AppSettings settings, Func<string, string> describeTarget, Window owner)
    {
        var vm = new CredentialLibraryViewModel(settings, settingsStore, credentialStore, describeTarget);
        var window = new CredentialLibraryWindow { DataContext = vm };
        vm.CloseRequested += () => window.Close();

        await window.ShowDialog(owner);
        return vm.Changed;
    }

    /// <summary>The test for the request's target, or why it has none.</summary>
    private (CredentialTest? Test, string? Reason) TestFor(CredentialsDialogRequest request)
    {
        if (request.Profile is { } profile)
        {
            if (!CredentialTester.CanTest(profile, out var reason))
                return (null, reason);
            return ((credentials, onOutput, ct) =>
                tester.TestProfileAsync(profile, credentials, allowUntrustedCertificate: true, onOutput, ct), null);
        }

        if (request.Script is { } script && request.ProjectDirectory is { } projectDirectory)
        {
            if (!CredentialTester.CanTest(script))
                return (null, "Add a test command in the script's settings (⋯ → Edit script…) to test these credentials.");
            return ((credentials, onOutput, ct) =>
                tester.TestScriptAsync(script, projectDirectory, credentials, onOutput, ct), null);
        }

        return (null, null);
    }
}
