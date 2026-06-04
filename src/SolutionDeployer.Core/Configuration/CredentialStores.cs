using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SolutionDeployer.Core.Configuration;

/// <summary>Picks the appropriate secure credential store for the current OS.</summary>
public static class CredentialStoreFactory
{
    public const string ServiceName = "3ai.SolutionDeployer";

    public static ICredentialStore Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsDpapiCredentialStore();
        if (OperatingSystem.IsMacOS())
            return new MacKeychainCredentialStore();
        if (OperatingSystem.IsLinux() && CliCredentialStore.ToolExists("secret-tool"))
            return new LinuxSecretToolCredentialStore();

        return new NullCredentialStore();
    }
}

/// <summary>No secure backend available — never persists secrets.</summary>
public sealed class NullCredentialStore : ICredentialStore
{
    public bool IsAvailable => false;
    public string? Get(string key) => null;
    public void Set(string key, string secret) { }
    public void Delete(string key) { }
}

/// <summary>
/// Windows store: per-entry DPAPI encryption (CurrentUser scope) persisted to a small file under the
/// app-data folder. Secrets are unreadable by other users and never leave the machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiCredentialStore : ICredentialStore
{
    private static readonly byte[] Entropy = "3ai.SolutionDeployer/credentials/v1"u8.ToArray();
    private readonly string _filePath;
    private Dictionary<string, string> _entries;

    public WindowsDpapiCredentialStore(string? overridePath = null)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            CredentialStoreFactory.ServiceName);
        Directory.CreateDirectory(dir);
        _filePath = overridePath ?? Path.Combine(dir, "credentials.dat");
        _entries = Load();
    }

    public bool IsAvailable => OperatingSystem.IsWindows();

    public string? Get(string key)
    {
        if (!OperatingSystem.IsWindows() || !_entries.TryGetValue(key, out var cipher))
            return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    public void Set(string key, string secret)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        _entries[key] = Convert.ToBase64String(cipher);
        Save();
    }

    public void Delete(string key)
    {
        if (_entries.Remove(key))
            Save();
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_filePath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath))
                       ?? new Dictionary<string, string>();
        }
        catch
        {
            // Corrupt store — start fresh rather than crash.
        }
        return new Dictionary<string, string>();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries));
        }
        catch
        {
            // Best-effort.
        }
    }
}

/// <summary>Base for stores that shell out to a platform secret-management CLI.</summary>
public abstract class CliCredentialStore : ICredentialStore
{
    public abstract bool IsAvailable { get; }
    public abstract string? Get(string key);
    public abstract void Set(string key, string secret);
    public abstract void Delete(string key);

    protected static (int ExitCode, string StdOut) Run(string fileName, IReadOnlyList<string> args, string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)!;
        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout);
    }

    public static bool ToolExists(string tool)
    {
        try
        {
            var which = OperatingSystem.IsWindows() ? "where" : "which";
            return Run(which, [tool]).ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>macOS store backed by the login Keychain via the <c>security</c> CLI.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainCredentialStore : CliCredentialStore
{
    private const string Service = CredentialStoreFactory.ServiceName;

    public override bool IsAvailable => OperatingSystem.IsMacOS();

    public override string? Get(string key)
    {
        try
        {
            var (exit, stdout) = Run("security", ["find-generic-password", "-a", key, "-s", Service, "-w"]);
            return exit == 0 ? stdout.TrimEnd('\n', '\r') : null;
        }
        catch
        {
            return null;
        }
    }

    public override void Set(string key, string secret)
    {
        try
        {
            // -U updates the item if it already exists.
            Run("security", ["add-generic-password", "-U", "-a", key, "-s", Service, "-w", secret]);
        }
        catch
        {
            // Best-effort.
        }
    }

    public override void Delete(string key)
    {
        try
        {
            Run("security", ["delete-generic-password", "-a", key, "-s", Service]);
        }
        catch
        {
            // Best-effort.
        }
    }
}

/// <summary>Linux store backed by libsecret via the <c>secret-tool</c> CLI.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSecretToolCredentialStore : CliCredentialStore
{
    private const string Service = CredentialStoreFactory.ServiceName;

    public override bool IsAvailable => OperatingSystem.IsLinux() && ToolExists("secret-tool");

    public override string? Get(string key)
    {
        try
        {
            var (exit, stdout) = Run("secret-tool", ["lookup", "service", Service, "account", key]);
            return exit == 0 ? stdout.TrimEnd('\n', '\r') : null;
        }
        catch
        {
            return null;
        }
    }

    public override void Set(string key, string secret)
    {
        try
        {
            Run("secret-tool",
                ["store", "--label", $"{Service} ({key})", "service", Service, "account", key],
                stdin: secret);
        }
        catch
        {
            // Best-effort.
        }
    }

    public override void Delete(string key)
    {
        try
        {
            Run("secret-tool", ["clear", "service", Service, "account", key]);
        }
        catch
        {
            // Best-effort.
        }
    }
}
