using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SolutionDeployer.Core.Configuration;

/// <summary>
/// Exports <see cref="AppSettings"/> to a portable file, optionally encrypted with a password, and reads it back.
/// Settings that belong to this machine (<see cref="KeepLocal"/>) are left out of an export and kept on restore.
/// A password-protected export can also carry the saved passwords (<see cref="CollectSecrets"/>); an export
/// without a password never does.
/// </summary>
/// <remarks>
/// An encrypted file is AES-256-GCM over the settings and saved passwords, keyed by PBKDF2-SHA256 of the password
/// with a random salt; the format header is bound in as associated data so it can't be swapped.
/// </remarks>
public static class SettingsTransfer
{
    public const string Format = "3ai.SolutionDeployer.Settings";
    public const int CurrentVersion = 1;

    /// <summary>Credential-store key holding the password for automatic exports.</summary>
    public const string AutoExportPasswordKey = "settings-export-password";

    /// <summary>Default file name offered when exporting.</summary>
    public const string DefaultFileName = "SolutionDeployer-settings.json";

    private const int Pbkdf2Iterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes($"{Format}/v{CurrentVersion}");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The settings JSON an export would contain: a copy of <paramref name="settings"/> without the
    /// machine-local fields. Two calls give the same text for the same settings, so it doubles as a change check.
    /// </summary>
    public static string ToExportJson(AppSettings settings)
    {
        var copy = Clone(settings);
        copy.WindowWidth = copy.WindowHeight = null;
        copy.WindowX = copy.WindowY = null;
        copy.WindowMaximized = false;
        copy.LastSeenWhatsNewVersion = null;
        copy.StartWithOperatingSystem = false;
        copy.McpServerEnabled = false;
        copy.McpServerPort = new AppSettings().McpServerPort;
        copy.AutoExport = new SettingsAutoExport();
        return JsonSerializer.Serialize(copy, JsonOptions);
    }

    /// <summary>
    /// The credential-store keys of the passwords these settings refer to: profiles (by file path) and scripts
    /// with a remembered user, deploy history or backup destination, and the S3 destinations' secret keys. This
    /// machine's own secrets (the MCP token, the automatic-export password) are never among them.
    /// </summary>
    public static IReadOnlySet<string> SecretKeys(AppSettings settings)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        keys.UnionWith(settings.RememberedUserNames.Keys);
        keys.UnionWith(settings.DeployHistory.Keys);
        keys.UnionWith(settings.ProfileBackupTarget.Keys);
        keys.UnionWith(settings.ScriptTargets.Values.SelectMany(list => list).Select(t => t.CredentialKey));
        keys.UnionWith(settings.RemoteBackupTargets.Select(t => t.SecretCredentialKey));
        keys.Remove(AutoExportPasswordKey);
        return keys;
    }

    /// <summary>The saved passwords (<see cref="SecretKeys"/>) that <paramref name="store"/> holds.</summary>
    public static IReadOnlyDictionary<string, string> CollectSecrets(AppSettings settings, ICredentialStore store)
    {
        var secrets = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!store.IsAvailable)
            return secrets;

        foreach (var key in SecretKeys(settings))
        {
            if (store.Get(key) is { Length: > 0 } secret)
                secrets[key] = secret;
        }
        return secrets;
    }

    /// <summary>Writes an export of <paramref name="settings"/> to <paramref name="filePath"/>, replacing it.</summary>
    /// <param name="password">Encrypts the file when non-empty.</param>
    /// <param name="secrets">Saved passwords to include; only allowed with a <paramref name="password"/>.</param>
    public static void Export(
        AppSettings settings, string filePath, string? password,
        IReadOnlyDictionary<string, string>? secrets = null, string? appVersion = null) =>
        ExportJson(ToExportJson(settings), filePath, password, secrets, appVersion);

    /// <summary>Writes an already-prepared <see cref="ToExportJson"/> payload to <paramref name="filePath"/>.</summary>
    /// <exception cref="ArgumentException">Saved passwords were given for an export without a password.</exception>
    public static void ExportJson(
        string exportJson, string filePath, string? password,
        IReadOnlyDictionary<string, string>? secrets = null, string? appVersion = null)
    {
        var withSecrets = secrets is { Count: > 0 };
        if (withSecrets && string.IsNullOrEmpty(password))
            throw new ArgumentException("Saved passwords are only exported to a password-protected file.", nameof(secrets));

        var file = new SettingsExportFile
        {
            Format = Format,
            Version = CurrentVersion,
            ExportedUtc = DateTimeOffset.UtcNow,
            AppVersion = appVersion ?? EntryVersion(),
            MachineName = Environment.MachineName,
        };

        var settings = JsonSerializer.Deserialize<JsonElement>(exportJson);
        if (string.IsNullOrEmpty(password))
        {
            file.Settings = settings;
        }
        else
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new EncryptedPayload
            {
                Settings = settings,
                Secrets = withSecrets ? new Dictionary<string, string>(secrets!) : null,
            }, FileJsonOptions);
            try
            {
                file.Encrypted = Encrypt(payload, password);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }

        var fullPath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // Write beside the target, then swap, so a crash never leaves a half-written export behind.
        var temp = fullPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, FileJsonOptions));
        File.Move(temp, fullPath, overwrite: true);
    }

    /// <summary>Reads an export's header without decrypting it.</summary>
    /// <exception cref="InvalidDataException">The file is not a settings export.</exception>
    public static SettingsExportInfo ReadInfo(string filePath)
    {
        var file = ReadFile(filePath);
        return new SettingsExportInfo(file.ExportedUtc, file.AppVersion, file.MachineName, file.Encrypted is not null);
    }

    /// <summary>
    /// Reads the settings, and any saved passwords, from an export, decrypting with <paramref name="password"/>
    /// when needed. Only passwords the imported settings refer to (<see cref="SecretKeys"/>) are returned.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a settings export, or the contents can't be read.</exception>
    /// <exception cref="SettingsPasswordException">The file is encrypted and the password is missing or wrong.</exception>
    public static SettingsImport Import(string filePath, string? password)
    {
        var file = ReadFile(filePath);

        JsonElement settingsJson;
        Dictionary<string, string>? secrets = null;
        if (file.Encrypted is { } encrypted)
        {
            if (string.IsNullOrEmpty(password))
                throw new SettingsPasswordException("This export is password-protected. Enter its password.");

            var plaintext = Decrypt(encrypted, password);
            try
            {
                var payload = JsonSerializer.Deserialize<EncryptedPayload>(plaintext)
                              ?? throw new InvalidDataException("The export contains no settings.");
                settingsJson = payload.Settings ?? throw new InvalidDataException("The export contains no settings.");
                secrets = payload.Secrets;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"The exported settings can't be read: {ex.Message}", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        else
        {
            settingsJson = file.Settings ?? throw new InvalidDataException("The export contains no settings.");
        }

        AppSettings settings;
        try
        {
            settings = settingsJson.Deserialize<AppSettings>()
                       ?? throw new InvalidDataException("The export contains no settings.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The exported settings can't be read: {ex.Message}", ex);
        }

        // A file can't plant secrets under keys these settings don't use (the MCP token, say).
        var allowed = SecretKeys(settings);
        var restoredSecrets = (secrets ?? [])
            .Where(s => allowed.Contains(s.Key) && !string.IsNullOrEmpty(s.Value))
            .ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
        return new SettingsImport(settings, restoredSecrets);
    }

    /// <summary>
    /// Copies the fields that belong to this machine from <paramref name="current"/> onto <paramref name="restored"/>:
    /// window placement, the sign-in entry, the MCP server, the "what's new" marker and the automatic export.
    /// </summary>
    public static void KeepLocal(AppSettings restored, AppSettings current)
    {
        restored.WindowWidth = current.WindowWidth;
        restored.WindowHeight = current.WindowHeight;
        restored.WindowX = current.WindowX;
        restored.WindowY = current.WindowY;
        restored.WindowMaximized = current.WindowMaximized;
        restored.LastSeenWhatsNewVersion = current.LastSeenWhatsNewVersion;
        restored.StartWithOperatingSystem = current.StartWithOperatingSystem;
        restored.McpServerEnabled = current.McpServerEnabled;
        restored.McpServerPort = current.McpServerPort;
        restored.AutoExport = current.AutoExport;
    }

    private static string? EntryVersion() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : null;

    /// <summary>A deep copy of <paramref name="settings"/>, e.g. to export off the UI thread.</summary>
    public static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

    private static SettingsExportFile ReadFile(string filePath)
    {
        SettingsExportFile? file;
        try
        {
            file = JsonSerializer.Deserialize<SettingsExportFile>(File.ReadAllText(filePath));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("This is not a Solution Deployer settings export.", ex);
        }

        if (file is null || file.Format != Format)
            throw new InvalidDataException("This is not a Solution Deployer settings export.");
        if (file.Version > CurrentVersion)
            throw new InvalidDataException(
                $"This export was made by a newer version of Solution Deployer (format v{file.Version}). Update the app to restore it.");
        return file;
    }

    private static EncryptedSettings Encrypt(byte[] plaintext, string password)
    {
        EnsureAesGcm();
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt, Pbkdf2Iterations);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, cipher, tag, AssociatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return new EncryptedSettings
        {
            Iterations = Pbkdf2Iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            CipherText = Convert.ToBase64String(cipher),
        };
    }

    private static byte[] Decrypt(EncryptedSettings encrypted, string password)
    {
        EnsureAesGcm();
        if (encrypted.Cipher != EncryptedSettings.AesGcm || encrypted.Kdf != EncryptedSettings.Pbkdf2)
            throw new InvalidDataException($"Unsupported encryption ({encrypted.Cipher}, {encrypted.Kdf}).");

        byte[] salt, nonce, tag, cipher;
        try
        {
            salt = Convert.FromBase64String(encrypted.Salt);
            nonce = Convert.FromBase64String(encrypted.Nonce);
            tag = Convert.FromBase64String(encrypted.Tag);
            cipher = Convert.FromBase64String(encrypted.CipherText);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The export is damaged.", ex);
        }

        if (encrypted.Iterations <= 0 || nonce.Length != NonceSize || tag.Length != TagSize)
            throw new InvalidDataException("The export is damaged.");

        var key = DeriveKey(password, salt, encrypted.Iterations);
        var plaintext = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plaintext, AssociatedData);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new SettingsPasswordException("The password is wrong, or the file has been changed.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);

    private static void EnsureAesGcm()
    {
        if (!AesGcm.IsSupported)
            throw new PlatformNotSupportedException("Password-protected exports need AES-GCM, which this system doesn't provide.");
    }
}

/// <summary>What an export says about itself, readable without its password.</summary>
public sealed record SettingsExportInfo(DateTimeOffset ExportedUtc, string? AppVersion, string? MachineName, bool IsEncrypted);

/// <summary>What an export holds: the settings, and the saved passwords (credential-store key → secret) if any.</summary>
public sealed record SettingsImport(AppSettings Settings, IReadOnlyDictionary<string, string> Secrets);

/// <summary>An encrypted export was opened without its password, or with the wrong one.</summary>
public sealed class SettingsPasswordException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>On-disk shape of a settings export.</summary>
internal sealed class SettingsExportFile
{
    public string Format { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTimeOffset ExportedUtc { get; set; }
    public string? AppVersion { get; set; }
    public string? MachineName { get; set; }

    /// <summary>The settings, when the export is not encrypted.</summary>
    public JsonElement? Settings { get; set; }

    /// <summary>The settings, when the export is encrypted.</summary>
    public EncryptedSettings? Encrypted { get; set; }
}

/// <summary>The plaintext inside an encrypted export.</summary>
internal sealed class EncryptedPayload
{
    public JsonElement? Settings { get; set; }

    /// <summary>Saved passwords, keyed by credential-store key.</summary>
    public Dictionary<string, string>? Secrets { get; set; }
}

internal sealed class EncryptedSettings
{
    public const string AesGcm = "AES-256-GCM";
    public const string Pbkdf2 = "PBKDF2-SHA256";

    public string Cipher { get; set; } = AesGcm;
    public string Kdf { get; set; } = Pbkdf2;
    public int Iterations { get; set; }
    public string Salt { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string CipherText { get; set; } = string.Empty;
}
