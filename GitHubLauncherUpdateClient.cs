using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

internal enum LauncherUpdateState { Available, Current, LocalNewer, Unsupported, NoRelease }
internal sealed record LauncherReleaseAsset(string Id, Uri DownloadUrl, long Size, string Sha256);
internal sealed record LauncherRelease(string Tag, string Notes, DateTimeOffset? PublishedAt, Uri PageUrl,
    LauncherUpdateState State, LauncherReleaseAsset? Asset = null, string? IssueKey = null);
internal sealed record LauncherUpdateCheck(LauncherRelease Release, DateTimeOffset CheckedAt, bool FromCache);
internal sealed record LauncherUpdateCache(int Version, DateTimeOffset CheckedAt, int Status, string? Body, string? ETag);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LauncherUpdateCache))]
internal partial class LauncherUpdateJsonContext : JsonSerializerContext { }

// Launcher updates have one fixed source and their own storage, independent of games and Mod topics.
internal sealed class GitHubLauncherUpdateClient : IDisposable
{
    internal const string Repository = "FNSOIDATHQ/GOHShaderModdingSupportLauncher";
    internal const string AssetName = "GOHShaderModdingSupportLauncher.exe";
    internal static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases");
    internal static readonly Uri LatestApi = new($"https://api.github.com/repos/{Repository}/releases/latest");
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);
    private const long MaxAssetBytes = 256L * 1024 * 1024;
    private readonly string statePath, downloadRoot;
    private readonly AnonymousModHttpClient http;
    private readonly ModFileTransferClient transfer;
    private readonly Func<DateTimeOffset> clock;
    private readonly SemaphoreSlim checkGate = new(1, 1);

    internal GitHubLauncherUpdateClient(string statePath, AnonymousModHttpClient? http = null,
        ModFileTransferClient? transfer = null, Func<DateTimeOffset>? clock = null)
    {
        this.statePath = Path.GetFullPath(statePath);
        downloadRoot = Path.Combine(Path.GetDirectoryName(this.statePath)!, "Updates");
        this.http = http ?? new(new ModHttpOptions { MaxTextBytes = 1024 * 1024,
            Diagnostic = entry => AppDiagnostics.Log("Launcher update HTTP: " + entry) });
        this.transfer = transfer ?? new(new ModTransferOptions { AllowHost = IsDownloadHost });
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal async Task<LauncherUpdateCheck> CheckAsync(string currentVersion, bool force, CancellationToken token = default)
    {
        await checkGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var now = clock();
            var saved = ReadCache(now);
            if (!force && saved is not null && now - saved.CheckedAt < CacheLifetime)
                return new(FromCache(saved, currentVersion), saved.CheckedAt, true);

            var response = await http.GetGitHubJsonAsync(LatestApi, saved?.ETag, token).ConfigureAwait(false);
            LauncherUpdateCache next;
            if (response.Status == HttpStatusCode.NotModified)
            {
                if (saved is null) throw new InvalidDataException(Text("LU_InvalidResponse"));
                next = saved with { CheckedAt = now, ETag = response.ETag ?? saved.ETag };
            }
            else next = new(1, now, (int)response.Status, response.Body, response.ETag);
            // A malformed response must not replace the last successfully parsed snapshot.
            var release = FromCache(next, currentVersion);
            SaveCache(next);
            return new(release, now, false);
        }
        finally { checkGate.Release(); }
    }

    private static LauncherRelease FromCache(LauncherUpdateCache cache, string currentVersion) => cache.Status switch
    {
        404 => new("", "", null, ReleasesPage, LauncherUpdateState.NoRelease),
        200 when cache.Body is not null => ParseRelease(cache.Body, currentVersion),
        _ => throw new InvalidDataException(Text("LU_InvalidResponse"))
    };

    private LauncherUpdateCache? ReadCache(DateTimeOffset now)
    {
        try
        {
            ManagedModFiles.EnsureSafe(statePath);
            if (!File.Exists(statePath)) return null;
            using var stream = File.OpenRead(statePath);
            if (stream.Length > 2 * 1024 * 1024) return null;
            var value = JsonSerializer.Deserialize(stream, LauncherUpdateJsonContext.Default.LauncherUpdateCache);
            if (value is null || value.Version != 1 || value.CheckedAt > now || value.CheckedAt == default ||
                value.ETag?.Length > 512) return null;
            _ = FromCache(value, AppVersion.Current);
            return value;
        }
        catch (Exception ex) when (FileManager.IsFileError(ex) || ex is JsonException or InvalidDataException or InvalidOperationException)
        { AppDiagnostics.Log("Ignoring invalid launcher update cache."); return null; }
    }

    private void SaveCache(LauncherUpdateCache value)
    {
        try
        {
            ManagedModFiles.EnsureSafe(statePath);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            FileManager.WriteTextAtomically(statePath, JsonSerializer.Serialize(value,
                LauncherUpdateJsonContext.Default.LauncherUpdateCache), overwrite: true);
        }
        catch (Exception ex) when (FileManager.IsFileError(ex))
        { AppDiagnostics.Log("Unable to save launcher update cache.", ex); }
    }

    internal static bool TryVersion(string value, out Version version)
    {
        version = new(0, 0, 0, 0);
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        string[] parts = value.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        int[] numbers = new int[4];
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length is < 1 or > 5 || parts[i].Any(c => c is < '0' or > '9') ||
                !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] > 65535) return false;
        version = new(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    internal static LauncherRelease ParseRelease(string json, string currentVersion)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("id").GetInt64() <= 0) throw new JsonException();
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (tag.Length > 128) throw new JsonException();
            string notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : "";
            if (notes.Length > 64 * 1024) notes = notes[..(64 * 1024)];
            DateTimeOffset? published = root.TryGetProperty("published_at", out var date) && date.TryGetDateTimeOffset(out var when) ? when : null;
            var result = new LauncherRelease(tag, notes, published, ReleasesPage, LauncherUpdateState.Unsupported);
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() ||
                !TryVersion(tag, out var remote)) return result with { IssueKey = "LU_UnsupportedRelease" };
            var page = new Uri($"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}");
            if (root.GetProperty("html_url").GetString() != page.AbsoluteUri) throw new JsonException();
            result = result with { PageUrl = page };
            if (!TryVersion(currentVersion, out var local)) return result with { IssueKey = "LU_UnknownVersion" };
            if (remote <= local) return result with { State = remote == local ? LauncherUpdateState.Current : LauncherUpdateState.LocalNewer };

            var matches = root.GetProperty("assets").EnumerateArray().Where(asset =>
                asset.GetProperty("name").GetString() == AssetName).ToArray();
            if (matches.Length != 1) return result with { IssueKey = "LU_MissingAsset" };
            var item = matches[0];
            long id = item.GetProperty("id").GetInt64(), size = item.GetProperty("size").GetInt64();
            string? digest = item.TryGetProperty("digest", out var hash) && hash.ValueKind == JsonValueKind.String ? hash.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                digest.Length != 71 || digest.AsSpan(7).ContainsAnyExcept("0123456789abcdefABCDEF"))
                return result with { IssueKey = "LU_MissingDigest" };
            var url = new Uri($"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{AssetName}");
            if (id <= 0 || size is <= 0 or > MaxAssetBytes || item.GetProperty("state").GetString() != "uploaded" ||
                item.GetProperty("browser_download_url").GetString() != url.AbsoluteUri)
                return result with { IssueKey = "LU_InvalidAsset" };
            return result with { State = LauncherUpdateState.Available,
                Asset = new(id.ToString(CultureInfo.InvariantCulture), url, size, digest[7..].ToUpperInvariant()) };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException or OverflowException)
        { throw new InvalidDataException(Text("LU_InvalidResponse"), ex); }
    }

    internal async Task<LauncherUpdate.Candidate> DownloadAsync(LauncherRelease release, string target,
        Action<long, long?> progress, CancellationToken token)
    {
        if (release.State != LauncherUpdateState.Available || release.Asset is not { } asset)
            throw new InvalidOperationException(Text("LU_InvalidAsset"));
        LauncherUpdate.CheckTarget(target);
        string targetHash = await Task.Run(() => LauncherUpdate.Hash(target), token).ConfigureAwait(false);
        string directory = Path.Combine(downloadRoot, Guid.NewGuid().ToString("N"));
        ManagedModFiles.EnsureSafe(directory);
        ManagedModFiles.EnsureSpace(directory, asset.Size);
        Directory.CreateDirectory(directory);
        string partial = Path.Combine(directory, "download.part"), resume = Path.Combine(directory, "resume.json"), source = Path.Combine(directory, "verified.exe");
        bool complete = false;
        try
        {
            var file = new RemoteModFile { Id = "launcher:" + asset.Id, Revision = asset.Sha256,
                Name = AssetName, PageUrl = release.PageUrl, SizeBytes = asset.Size };
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await transfer.TransferAsync(new(file, asset.DownloadUrl, clock()), partial, resume, progress, token).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (attempt < transfer.MaxRetries && !token.IsCancellationRequested &&
                    (ex is HttpRequestException || ex is OperationCanceledException || ex is EndOfStreamException))
                { await Task.Delay(transfer.RetryDelay, token).ConfigureAwait(false); }
            }
            await using (var input = File.OpenRead(partial))
                if (input.Length != asset.Size || !Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Text("LU_ChecksumFailed"));
            ValidateExecutable(partial);
            token.ThrowIfCancellationRequested();
            File.Move(partial, source);
            complete = true;
            return new(source, Path.GetFullPath(target), asset.Sha256, targetHash);
        }
        finally { if (!complete) CleanDirectory(directory); }
    }

    internal static void ValidateExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException(Text("LU_InvalidExecutable"));
        stream.Position = 0x3c;
        int offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 26) throw new InvalidDataException(Text("LU_InvalidExecutable"));
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664) throw new InvalidDataException(Text("LU_InvalidExecutable"));
        stream.Position = offset + 22;
        ushort flags = reader.ReadUInt16();
        if ((flags & 0x2002) != 2 || reader.ReadUInt16() != 0x20b) throw new InvalidDataException(Text("LU_InvalidExecutable"));
    }

    internal void CleanDownload(LauncherUpdate.Candidate candidate)
    {
        string? directory = Path.GetDirectoryName(candidate.Source);
        if (directory is not null && Path.GetDirectoryName(directory)?.Equals(downloadRoot, StringComparison.OrdinalIgnoreCase) == true &&
            Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) CleanDirectory(directory);
    }

    private static void CleanDirectory(string directory)
    {
        try
        {
            ManagedModFiles.EnsureSafe(directory);
            foreach (string name in new[] { "download.part", "resume.json", "verified.exe" }) File.Delete(Path.Combine(directory, name));
            Directory.Delete(directory); // Never recursively remove an update directory or unrelated files.
        }
        catch (Exception ex) when (FileManager.IsFileError(ex)) { AppDiagnostics.Log("Unable to clean launcher update download.", ex); }
    }

    internal static bool IsDownloadHost(Uri uri) => AnonymousModHttpClient.IsPublicHttps(uri) && uri.IdnHost is
        "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    private static string Text(string key) => LocalizedStrings.Instance.Get(key);
    public void Dispose() { http.Dispose(); transfer.Dispose(); }
}
