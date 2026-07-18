using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Sefirah.Platforms.Desktop.Services;

public partial class UpdateService(
    ILogger<UpdateService> logger,
    IPlatformNotificationHandler notificationHandler) : ObservableObject, IUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/shrimqy/Sefirah/releases/latest";
    private static readonly HttpClient Http = CreateClient();

    private string? latestReleaseUrl;
    private string? latestTag;

    [ObservableProperty]
    public partial bool IsUpdateAvailable { get; set; }

    [ObservableProperty]
    public partial bool IsUpdating { get; set; }

    public async Task CheckForUpdatesAsync()
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.Debug($"Update check failed: HTTP {(int)response.StatusCode}");
                return;
            }

            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>().ConfigureAwait(false);
            if (release?.TagName is null || release.HtmlUrl is null)
                return;

            var remote = ParseVersion(release.TagName);
            var local = AppLifecycleHelperVersion();
            if (remote is null || local is null || remote <= local)
            {
                IsUpdateAvailable = false;
                return;
            }

            latestTag = release.TagName;
            latestReleaseUrl = release.HtmlUrl;
            IsUpdateAvailable = true;
            logger.Info($"Update available: {latestTag} (current {local})");

            notificationHandler.ShowClipboardNotification(
                "Update available",
                $"Sefirah {latestTag} is available. Open GitHub releases to download.",
                "Open",
                latestReleaseUrl);
        }
        catch (Exception ex)
        {
            logger.Debug($"Update check failed: {ex.Message}");
        }
    }

    public Task DownloadUpdatesAsync()
    {
        if (string.IsNullOrEmpty(latestReleaseUrl))
            return Task.CompletedTask;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { latestReleaseUrl },
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            logger.Warn("Failed to open release page", ex);
        }

        return Task.CompletedTask;
    }

    private static Version? AppLifecycleHelperVersion()
    {
        try
        {
            return Helpers.AppLifecycleHelper.AppVersion;
        }
        catch
        {
            return typeof(UpdateService).Assembly.GetName().Version;
        }
    }

    private static Version? ParseVersion(string tag)
    {
        var trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];
        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sefirah-Desktop");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}
