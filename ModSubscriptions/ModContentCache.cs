using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record CachedModContent<T>
{
    public required int Version { get; init; }
    public required string Key { get; init; }
    public required DateTimeOffset FetchedAt { get; init; }
    public required T Content { get; init; }
}

// Only successfully parsed, stable metadata is cached. HTML, mirror pages and signed URLs are never written.
internal sealed class ModContentCache(string? directory = null)
{
    private const int MaxEntryBytes = 4 * 1024 * 1024;
    private readonly string directory = directory ?? Path.Combine(Path.GetDirectoryName(SubscriptionStore.DefaultPath)!, "moddb-cache");

    private string PathFor(string key) => Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");

    internal CachedModContent<T>? Read<T>(string key, JsonTypeInfo<CachedModContent<T>> type)
    {
        try
        {
            using var stream = new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxEntryBytes) return null;
            var value = JsonSerializer.Deserialize(stream, type);
            return value is { Version: 1, Content: not null } && value.Key == key ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal string? Write<T>(string key, T content, DateTimeOffset fetchedAt, JsonTypeInfo<CachedModContent<T>> type)
    {
        string path = PathFor(key);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new CachedModContent<T>
            {
                Version = 1, Key = key, FetchedAt = fetchedAt, Content = content
            }, type);
            if (bytes.Length > MaxEntryBytes) return "The content is too large for the offline cache.";
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            // Bound this disposable cache independently from the subscription library.
            foreach (var old in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc).Skip(256))
                old.Delete();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The content was read successfully but the offline cache could not be saved.";
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CachedModContent<ModSourcePage<RemoteMod>>))]
[JsonSerializable(typeof(CachedModContent<RemoteMod>))]
[JsonSerializable(typeof(CachedModContent<ModSourcePage<ModArticle>>))]
[JsonSerializable(typeof(CachedModContent<ModArticle>))]
[JsonSerializable(typeof(CachedModContent<ModSourcePage<ModImageReference>>))]
[JsonSerializable(typeof(CachedModContent<ModImage>))]
[JsonSerializable(typeof(CachedModContent<ModSourcePage<ModFileReference>>))]
[JsonSerializable(typeof(CachedModContent<RemoteModFile>))]
[JsonSerializable(typeof(CachedModContent<ModFileHints>))]
[JsonSerializable(typeof(CachedModContent<ModFileSnapshot>))]
internal partial class ModContentJsonContext : JsonSerializerContext { }
