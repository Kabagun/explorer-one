using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ExplorerOne.Updating;

internal sealed record ReleaseUpdate(Version Version, Uri DownloadUri, int Size, byte[] Sha256);

/// <summary>Reads public release metadata and downloads only the corresponding, verified Setup asset.</summary>
internal sealed class GitHubUpdateClient(HttpClient http)
{
    // Numeric ID preserves the identity of the repository across name changes.
    private static readonly Uri LatestRelease = new("https://api.github.com/repositories/1431843869/releases/latest");
    private const int MaxMetadataBytes = 512 * 1024;
    private const int MaxInstallerBytes = 10 * 1024 * 1024;

    internal async Task<ReleaseUpdate?> CheckAsync(Version current, CancellationToken token)
    {
        using var response = await SendAsync(LatestRelease, metadata: true, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxMetadataBytes)
            throw new InvalidDataException("Release metadata is too large.");

        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(input, buffer, MaxMetadataBytes, token).ConfigureAwait(false);
        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: token).ConfigureAwait(false);
        return ReadUpdate(document.RootElement, current);
    }

    internal static ReleaseUpdate? ReadUpdate(JsonElement release, Version current)
    {
        if (release.ValueKind != JsonValueKind.Object ||
            !TryProperty(release, "draft", JsonValueKind.False, out _) ||
            !TryProperty(release, "prerelease", JsonValueKind.False, out _) ||
            !TryProperty(release, "tag_name", JsonValueKind.String, out var tagValue))
            return null;

        var tag = tagValue.GetString()!;
        if (!tag.StartsWith('v') || !Version.TryParse(tag.AsSpan(1), out var version) ||
            version.Build < 0 || version.Revision >= 0 || tag != $"v{version.ToString(3)}" || version <= current ||
            !TryProperty(release, "assets", JsonValueKind.Array, out var assets))
            return null;

        var name = $"ExplorerOne-Setup-{version.ToString(3)}-win-x64.exe";
        var url = $"https://github.com/Kabagun/explorer-one/releases/download/{tag}/{name}";
        ReleaseUpdate? update = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (Text(asset, "name") != name)
                continue;
            if (update is not null || Text(asset, "state") != "uploaded" || Text(asset, "browser_download_url") != url ||
                !TryProperty(asset, "size", JsonValueKind.Number, out var length) || !length.TryGetInt32(out var size) ||
                size <= 0 || size > MaxInstallerBytes)
                return null;

            var digest = Text(asset, "digest");
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                digest.Length != 71 || !digest.Skip(7).All(Uri.IsHexDigit))
                return null;
            update = new(version, new Uri(url), size, Convert.FromHexString(digest.AsSpan(7)));
        }
        return update;
    }

    internal async Task DownloadAsync(ReleaseUpdate update, string destination, CancellationToken token)
    {
        var uri = update.DownloadUri;
        // Follow only the HTTPS release CDN redirects expected from GitHub.
        for (var redirects = 0;; redirects++)
        {
            using var response = await SendAsync(uri, metadata: false, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect)
            {
                if (redirects >= 3 || response.Headers.Location is not { IsAbsoluteUri: true } location ||
                    location.Scheme != Uri.UriSchemeHttps || !location.IsDefaultPort || location.UserInfo.Length != 0 ||
                    location.IdnHost is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("Unexpected update redirect.");
                uri = location;
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != update.Size)
                throw new InvalidDataException("Release asset length does not match its metadata.");
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);
            await CopyBoundedAsync(input, output, update.Size, token).ConfigureAwait(false);
            if (output.Length != update.Size)
                throw new InvalidDataException("Release asset download was incomplete.");
            output.Position = 0;
            var hash = await SHA256.HashDataAsync(output, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hash, update.Sha256))
                throw new InvalidDataException("Release asset SHA-256 does not match.");
            return;
        }
    }

    internal static void VerifyBeforeLaunch(Stream file, ReleaseUpdate update)
    {
        if (file.Length != update.Size || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), update.Sha256))
            throw new InvalidDataException("Downloaded installer changed before launch.");
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, bool metadata, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ExplorerOne", "Updater"));
        if (metadata)
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, int limit, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Position + read > limit)
                throw new InvalidDataException("Update response exceeded its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }

    private static string? Text(JsonElement value, string name) =>
        TryProperty(value, name, JsonValueKind.String, out var property) ? property.GetString() : null;

    private static bool TryProperty(JsonElement value, string name, JsonValueKind kind, out JsonElement property)
    {
        property = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out property) && property.ValueKind == kind;
    }
}
