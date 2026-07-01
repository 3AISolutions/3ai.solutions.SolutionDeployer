using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SolutionDeployer.Core.WhatsNew;

/// <summary>
/// Fetches the release notes for a given version from the project's GitHub Releases. Best-effort:
/// returns <c>null</c> when offline, rate-limited, or the tag isn't published. No secrets, no auth.
/// </summary>
public sealed class WhatsNewProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Returns the markdown body of the GitHub Release tagged <c>v{version}</c>, or <c>null</c> when
    /// the release can't be found or the request fails. <paramref name="version"/> is normalized to a
    /// leading <c>v</c> tag.
    /// </summary>
    public async Task<string?> GetNotesForVersionAsync(
        string repository,
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(version))
            return null;

        var ownerRepo = repository.Trim('/');
        var tag = version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : $"v{version}";
        var url = $"https://api.github.com/repos/{ownerRepo}/releases/tags/{Uri.EscapeDataString(tag)}";

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("3ai-SolutionDeployer");
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            http.Timeout = TimeSpan.FromSeconds(15);

            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(release?.Body) ? null : release.Body;
        }
        catch
        {
            // Offline / rate-limited / parse error — the changelog is informative only.
            return null;
        }
    }

    private sealed class GitHubRelease
    {
        public string? TagName { get; set; }
        public string? Name { get; set; }
        public string? Body { get; set; }
    }
}