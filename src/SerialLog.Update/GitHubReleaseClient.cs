using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialLog.Update;

public sealed class GitHubReleaseClient
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/wangjingping-88/serial-log/releases/latest";

    private readonly HttpClient _httpClient;

    public GitHubReleaseClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<GitHubRelease> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.UserAgent.ParseAdd("SerialLog-Updater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            linkedSource.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
                ? values.FirstOrDefault()
                : null;
            var suffix = remaining == "0" ? "，GitHub API 请求额度已用完，请稍后重试" : string.Empty;
            throw new HttpRequestException(
                $"GitHub Release 请求失败：{(int)response.StatusCode} {response.ReasonPhrase}{suffix}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(linkedSource.Token).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            stream,
            cancellationToken: linkedSource.Token).ConfigureAwait(false);

        return release ?? throw new InvalidDataException("GitHub Release 返回内容为空。");
    }
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("draft")]
    public bool IsDraft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool IsPrerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAsset> Assets { get; set; } = [];
}

public sealed class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}
