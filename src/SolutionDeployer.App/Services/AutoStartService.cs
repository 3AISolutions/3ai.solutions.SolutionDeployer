using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace SolutionDeployer.App.Services;

/// <summary>Registers the app to start (in the tray) when the user signs in.</summary>
public interface IAutoStartService
{
    bool IsSupported { get; }

    /// <summary>Adds or removes the sign-in entry. Throws when the OS refuses.</summary>
    void SetEnabled(bool enabled);
}

/// <summary>
/// Per-user sign-in entries: the <c>Run</c> registry key on Windows, an XDG autostart <c>.desktop</c> file on Linux
/// and a LaunchAgent on macOS. Each launches the app with <see cref="BackgroundArgument"/>, so it starts in the
/// tray instead of opening its window.
/// </summary>
public sealed class AutoStartService : IAutoStartService
{
    public const string BackgroundArgument = "--background";

    private const string EntryName = "3ai.SolutionDeployer";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsSupported => ExecutablePath is not null &&
                               (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            Remove();
            return;
        }

        var exe = ExecutablePath ?? throw new InvalidOperationException("Could not find the app's executable.");
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(EntryName, $"\"{exe}\" {BackgroundArgument}");
        }
        else if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LinuxEntryPath)!);
            File.WriteAllText(LinuxEntryPath, LinuxDesktopEntry(exe));
        }
        else if (OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MacEntryPath)!);
            File.WriteAllText(MacEntryPath, MacLaunchAgent(exe));
        }
        else
        {
            throw new PlatformNotSupportedException("Starting at sign-in isn't supported on this OS.");
        }
    }

    /// <summary>Removes the sign-in entry, if there is one. Also run by the uninstaller.</summary>
    public static void Remove()
    {
        if (OperatingSystem.IsWindows())
            RemoveRunValue();
        else if (OperatingSystem.IsLinux())
            File.Delete(LinuxEntryPath);
        else if (OperatingSystem.IsMacOS())
            File.Delete(MacEntryPath);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(EntryName, throwOnMissingValue: false);
    }

    /// <summary>
    /// What to launch. An AppImage runs from a temporary mount, so its own file (<c>APPIMAGE</c>) is what lasts;
    /// a Velopack install keeps the executable in its stable <c>current</c> folder across updates.
    /// </summary>
    private static string? ExecutablePath =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath;

    private static string LinuxEntryPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "3ai-solution-deployer.desktop");

    private static string MacEntryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "solutions.3ai.SolutionDeployer.plist");

    public static string LinuxDesktopEntry(string exe) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=3ai Solution Deployer
        Exec="{exe.Replace("\"", "\\\"")}" {BackgroundArgument}
        X-GNOME-Autostart-enabled=true
        Terminal=false
        """ + "\n";

    public static string MacLaunchAgent(string exe) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>solutions.3ai.SolutionDeployer</string>
          <key>ProgramArguments</key>
          <array>
            <string>{SecurityElement.Escape(exe)}</string>
            <string>{BackgroundArgument}</string>
          </array>
          <key>RunAtLoad</key>
          <true/>
        </dict>
        </plist>
        """ + "\n";
}
