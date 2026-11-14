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
internal sealed class ModContentCache(string? directory = null, ModCacheMaintenance? maintenance = null)
{
    private const int MaxEntryBytes = 4 * 1024 * 1024;
    private readonly string directory = directory ?? Path.Combine(Path.GetDirectoryName(SubscriptionStore.DefaultPath)!, "moddb-cache");
    private readonly ModCacheMaintenance maintenance = maintenance ?? new(Path.GetDirectoryName(directory ?? SubscriptionStore.DefaultPath)!);

    internal IDisposable BeginRead() => maintenance.BeginRead();

    private string PathFor(string key) => Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");

    internal CachedModContent<T>? Read<T>(string key, JsonTypeInfo<CachedModContent<T>> type)
    {
        lock (maintenance.Gate)
        {
            if (!maintenance.CanWrite) return null;
            try
            {
                ManagedModFiles.EnsureSafe(PathFor(key));
                using var stream = new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length > MaxEntryBytes) return null;
                var value = JsonSerializer.Deserialize(stream, type);
                if (value is not { Version: 1 or 2 or 3 or 4, Content: not null } || value.Key != key) return null;
                // Version-one caches predate GitHub policy metadata. Missing init-only strings
                // deserialize as null, so retain old offline content with the new field's default.
                T content = value.Content switch
                {
                    RemoteMod mod => (T)(object)(mod with { InstallationHint = mod.InstallationHint ?? "" }),
                    ModFileSnapshot snapshot => (T)(object)(snapshot with { Groups = snapshot.Groups ?? [] }),
                    ModSourcePage<RemoteMod> page when page.Items is not null => (T)(object)(page with
                    {
                        Items = page.Items.Select(mod => mod is null ? null! : mod with
                        { InstallationHint = mod.InstallationHint ?? "" }).ToArray()
                    }),
                    _ => value.Content
                };
                if (value.Version == 1)
                    content = content switch
                    {
                        RemoteModFile file => (T)(object)SubscriptionStore.LegacyFile(file),
                        ModFileSnapshot snapshot when snapshot.Files is not null => (T)(object)(snapshot with
                        { Files = snapshot.Files.Select(SubscriptionStore.LegacyFile).ToArray(), PageRevision = "" }),
                        _ => content
                    };
                return value with { Content = content };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
    }

    internal string? Write<T>(string key, T content, DateTimeOffset fetchedAt, JsonTypeInfo<CachedModContent<T>> type)
    {
        lock (maintenance.Gate)
        {
            if (!maintenance.CanWrite) return null;
            string path = PathFor(key);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                ManagedModFiles.EnsureSafe(path);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new CachedModContent<T>
                {
                    Version = 4, Key = key, FetchedAt = fetchedAt, Content = content
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
                {
                    ManagedModFiles.EnsureSafe(old.FullName);
                    old.Delete();
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "The content was read successfully but the offline cache could not be saved.";
            }
            finally
            {
                try { ManagedModFiles.EnsureSafe(temporary); File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
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
[JsonSerializable(typeof(ModReadingDocument))]
[JsonSerializable(typeof(ModFileGroup))]
internal partial class ModContentJsonContext : JsonSerializerContext { }
