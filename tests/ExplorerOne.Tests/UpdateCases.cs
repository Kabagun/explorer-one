using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ExplorerOne.Updating;

internal static class UpdateCases
{
    private static readonly Version Current = new(1, 2, 0);
    private static readonly byte[] SetupBytes = "Synthetic setup fixture. Never executed."u8.ToArray();
    private static readonly byte[] Digest = SHA256.HashData(SetupBytes);
    private const string Tag = "v1.10.0";
    private const string Name = "ExplorerOne-Setup-1.10.0-win-x64.exe";
    private const string Url = "https://github.com/Kabagun/explorer-one/releases/download/v1.10.0/" + Name;

    internal static (string Name, Func<Task> Run)[] All =>
    [
        ("Updater compares stable versions numerically", () =>
        {
            Require(Parse() is { Version: var version } && version == new Version(1, 10, 0));
            Require(Parse(current: new Version(1, 10, 0)) is null && Parse(current: new Version(2, 0, 0)) is null);
            return Task.CompletedTask;
        }),
        ("Updater rejects prereleases, drafts and noncanonical tags", () =>
        {
            Require(Parse(draft: true) is null && Parse(prerelease: true) is null);
            Require(Parse(tag: "v1.10.0-rc.1") is null && Parse(tag: "v1.10.0.0") is null);
            return Task.CompletedTask;
        }),
        ("Updater requires the exact uploaded Setup and a valid SHA-256", () =>
        {
            Require(Parse(url: "https://example.invalid/setup.exe") is null);
            Require(Parse(state: "new") is null && Parse(size: 100_000_000) is null);
            Require(Parse(digest: "") is null && Parse(digest: "sha256:" + new string('z', 64)) is null);
            Require(Parse(duplicate: true) is null);
            return Task.CompletedTask;
        }),
        ("Updater checks downloaded bytes after an allowed GitHub CDN redirect", () =>
            WithFile(async path =>
            {
                using var http = new HttpClient(new Stub(request => request.RequestUri!.Host == "github.com"
                    ? Redirect("https://release-assets.githubusercontent.com/fixture")
                    : new(HttpStatusCode.OK) { Content = new ByteArrayContent(SetupBytes) }));
                await new GitHubUpdateClient(http).DownloadAsync(Parse()!, path, default);
                using var file = File.OpenRead(path);
                GitHubUpdateClient.VerifyBeforeLaunch(file, Parse()!);
            })),
        ("Updater rejects an off-site or non-HTTPS redirect before writing a file", () =>
            WithFile(async path =>
            {
                foreach (var redirect in new[] { "https://example.invalid/fixture", "http://release-assets.githubusercontent.com/fixture" })
                {
                    using var http = new HttpClient(new Stub(_ => Redirect(redirect)));
                    await ExpectBadDownload(new GitHubUpdateClient(http), path);
                    Require(!File.Exists(path));
                }
            })),
        ("Updater rejects a changed installer and a truncated download", () =>
            WithFile(async path =>
            {
                var changed = SetupBytes.ToArray();
                changed[0] ^= 1;
                using var wrongHash = new HttpClient(new Stub(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(changed) }));
                await ExpectBadDownload(new GitHubUpdateClient(wrongHash), path);
                File.Delete(path);
                using var shortDownload = new HttpClient(new Stub(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(SetupBytes[..^1]) }));
                await ExpectBadDownload(new GitHubUpdateClient(shortDownload), path);

                using var tampered = new MemoryStream(changed);
                try { GitHubUpdateClient.VerifyBeforeLaunch(tampered, Parse()!); }
                catch (InvalidDataException) { return; }
                throw new InvalidOperationException("The launch check accepted changed bytes.");
            }))
    ];

    internal static Task CheckPublicRelease() => WithFile(async path =>
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var client = new GitHubUpdateClient(http);
        var release = await client.CheckAsync(new Version(0, 0, 0), timeout.Token)
            ?? throw new InvalidOperationException("Public release was not recognized.");
        await client.DownloadAsync(release, path, timeout.Token);
        using var file = File.OpenRead(path);
        GitHubUpdateClient.VerifyBeforeLaunch(file, release);
        Require(await client.CheckAsync(release.Version, timeout.Token) is null);
    });

    private static ReleaseUpdate? Parse(Version? current = null, bool draft = false, bool prerelease = false, string tag = Tag,
        string url = Url, string state = "uploaded", int? size = null, string? digest = null, bool duplicate = false)
    {
        var asset = new { name = Name, browser_download_url = url, state, size = size ?? SetupBytes.Length,
            digest = digest ?? "sha256:" + Convert.ToHexString(Digest).ToLowerInvariant() };
        var release = JsonSerializer.SerializeToElement(new { draft, prerelease, tag_name = tag, assets = duplicate ? new[] { asset, asset } : [asset] });
        return GitHubUpdateClient.ReadUpdate(release, current ?? Current);
    }

    private static async Task WithFile(Func<string, Task> action)
    {
        var root = Path.GetFullPath("temp");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"update-check-{Guid.NewGuid():N}.part");
        try { await action(path); }
        finally { File.Delete(path); }
    }

    private static async Task ExpectBadDownload(GitHubUpdateClient client, string path)
    {
        try { await client.DownloadAsync(Parse()!, path, default); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid installer was accepted.");
    }

    private static HttpResponseMessage Redirect(string url) => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(url) } };
    private static void Require(bool passed) { if (!passed) throw new InvalidOperationException("Update check failed."); }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
