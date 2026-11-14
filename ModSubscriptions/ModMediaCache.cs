using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record ModCachedImage(ModImagePayload Payload, bool IsStale = false,
    ModSourceFailure? Failure = null, string? CacheWarning = null);

internal sealed record ModMediaCacheMetadata(int Version, string Key, DateTimeOffset FetchedAt,
    string Hash, string FinalUrl, string? ContentType, string? ETag, DateTimeOffset? LastModified);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ModMediaCacheMetadata))]
internal partial class ModMediaCacheJsonContext : JsonSerializerContext { }

// Compressed bytes are shared across decode sizes. Controls own the visible native
// bitmaps; the runtime owns bounded memory/disk data and coalesces concurrent reads.
internal sealed class ModMediaCache(ModCacheMaintenance maintenance, long diskLimit = 256L * 1024 * 1024,
    int entryLimit = 1024, long memoryLimit = 32L * 1024 * 1024, TimeProvider? clock = null)
{
    private sealed record Cached(ModImagePayload Payload, DateTimeOffset FetchedAt);
    private sealed class Transfer
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal Task<ModCachedImage> Task = null!;
        internal int Readers;
    }
    private readonly Dictionary<string, Cached> memory = [];
    private readonly LinkedList<string> lru = [];
    private readonly Dictionary<string, Transfer> transfers = [];
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private long memoryBytes;
    private string DirectoryPath => maintenance.DirectoryFor("media-cache");
    private static string Key(string provider, Uri url) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes("media-v1\n" + provider + "\n" + new UriBuilder(url) { Fragment = "" }.Uri.AbsoluteUri)));
    private static bool Persistent(Uri url) => url.Query.Length == 0 && url.Fragment.Length == 0 && AnonymousModHttpClient.IsPublicHttps(url);
    private bool Fresh(Cached value) => clock.GetUtcNow() - value.FetchedAt is var age && age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(10);

    internal bool IsFresh(string provider, Uri url)
    { lock (maintenance.Gate) return maintenance.Generation >= 0 && memory.TryGetValue(Key(provider, url), out var value) && Fresh(value); }

    internal async Task<ModCachedImage> GetAsync(Uri url, string provider,
        Func<ModImagePayload?, CancellationToken, Task<ModImagePayload>> fetch,
        Func<ModImagePayload, CancellationToken, Task> validate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ModImageClient.IsAllowed(url, url, provider))
            throw new ModSourceException(new(ModSourceError.AccessDenied, "The image address is not supported.", DiagnosticKind: ModHttpFailureKind.RedirectRejected));
        string key = Key(provider, url);
        Transfer transfer;
        lock (maintenance.Gate)
        {
            if (!transfers.TryGetValue(key, out transfer!))
            {
                transfer = new();
                long generation = maintenance.Generation;
                transfer.Task = Task.Run(() => LoadAsync(key, url, provider, generation, fetch, validate, transfer.Cancellation.Token));
                transfers.Add(key, transfer);
            }
            transfer.Readers++;
        }
        try { return await transfer.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            lock (maintenance.Gate)
            {
                if (--transfer.Readers == 0)
                {
                    if (transfers.GetValueOrDefault(key) == transfer) transfers.Remove(key);
                    if (!transfer.Task.IsCompleted) transfer.Cancellation.Cancel();
                    _ = transfer.Task.ContinueWith(task => { _ = task.Exception; transfer.Cancellation.Dispose(); }, TaskScheduler.Default);
                }
            }
        }
    }

    private async Task<ModCachedImage> LoadAsync(string key, Uri url, string provider, long generation,
        Func<ModImagePayload?, CancellationToken, Task<ModImagePayload>> fetch,
        Func<ModImagePayload, CancellationToken, Task> validate, CancellationToken token)
    {
        Cached? cached;
        lock (maintenance.Gate)
        {
            cached = maintenance.IsCurrent(generation) ? memory.GetValueOrDefault(key) ?? ReadDisk(key, url, provider) : null;
            if (cached is not null) { TouchMemory(key, cached); TouchDisk(key); }
        }
        if (cached is not null)
        {
            try { await validate(cached.Payload, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (ModSourceException)
            {
                lock (maintenance.Gate)
                {
                    if (maintenance.IsCurrent(generation)) { RemoveMemory(key); DeleteDisk(key); }
                }
                cached = null;
            }
            token.ThrowIfCancellationRequested();
            if (cached is not null && Fresh(cached)) return new(cached.Payload);
        }
        try
        {
            var payload = await fetch(cached?.Payload, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (payload.Bytes.Length is 0 or > ModImageClient.MaxBytes || !ModImageClient.IsAllowed(url, payload.FinalUri, provider))
                throw new ModSourceException(new(ModSourceError.ParseFailed, "The image payload is not supported."));
            await validate(payload, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string? warning = null;
            lock (maintenance.Gate)
            {
                if (maintenance.IsCurrent(generation))
                {
                    var value = new Cached(payload, clock.GetUtcNow());
                    TouchMemory(key, value);
                    if (Persistent(url) && Persistent(payload.FinalUri)) warning = WriteDisk(key, value);
                }
            }
            return new(payload, CacheWarning: warning);
        }
        catch (ModSourceException ex) when (cached is not null && ex.Failure.Kind is
            ModSourceError.Offline or ModSourceError.Timeout or ModSourceError.RequestFailed or ModSourceError.NotFound or ModSourceError.ParseFailed)
        {
            token.ThrowIfCancellationRequested();
            return new(cached.Payload, true, ex.Failure);
        }
    }

    private void RemoveMemory(string key)
    {
        if (memory.Remove(key, out var old)) memoryBytes -= old.Payload.Bytes.LongLength;
        lru.Remove(key);
    }
    private void TouchMemory(string key, Cached value)
    {
        RemoveMemory(key);
        if (value.Payload.Bytes.LongLength > memoryLimit) return;
        while ((memory.Count >= 1024 || memoryBytes + value.Payload.Bytes.LongLength > memoryLimit) && lru.First is { } first) RemoveMemory(first.Value);
        memory.Add(key, value); lru.AddLast(key); memoryBytes += value.Payload.Bytes.LongLength;
    }

    private Cached? ReadDisk(string key, Uri initial, string provider)
    {
        if (!Persistent(initial)) return null;
        string path = Path.Combine(DirectoryPath, key + ".cache");
        try
        {
            ManagedModFiles.EnsureSafe(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is < 5 or > ModImageClient.MaxBytes + 8196) throw new InvalidDataException();
            Span<byte> header = stackalloc byte[4]; stream.ReadExactly(header);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is <= 0 or > 8192 || stream.Length - length - 4 is <= 0 or > ModImageClient.MaxBytes) throw new InvalidDataException();
            byte[] json = new byte[length]; stream.ReadExactly(json);
            var metadata = JsonSerializer.Deserialize(json, ModMediaCacheJsonContext.Default.ModMediaCacheMetadata);
            if (metadata is not { Version: 1 } || metadata.Key != key || metadata.FetchedAt > clock.GetUtcNow() ||
                !Uri.TryCreate(metadata.FinalUrl, UriKind.Absolute, out var final) || !Persistent(final) ||
                !ModImageClient.IsAllowed(initial, final, provider)) throw new InvalidDataException();
            byte[] bytes = new byte[checked((int)(stream.Length - stream.Position))]; stream.ReadExactly(bytes);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != metadata.Hash) throw new InvalidDataException();
            File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime);
            return new(new(bytes, final, metadata.ContentType, metadata.ETag, metadata.LastModified), metadata.FetchedAt);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { DeleteDisk(key); return null; }
    }

    private string? WriteDisk(string key, Cached value)
    {
        string path = Path.Combine(DirectoryPath, key + ".cache");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var payload = value.Payload;
            var metadata = new ModMediaCacheMetadata(1, key, value.FetchedAt,
                Convert.ToHexStringLower(SHA256.HashData(payload.Bytes)), payload.FinalUri.AbsoluteUri,
                payload.ContentType, payload.ETag, payload.LastModified);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata, ModMediaCacheJsonContext.Default.ModMediaCacheMetadata);
            long size = 4L + json.Length + payload.Bytes.Length;
            if (json.Length > 8192 || size > diskLimit || entryLimit <= 0) return null;
            ManagedModFiles.EnsureSafe(path);
            Directory.CreateDirectory(DirectoryPath);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Span<byte> header = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
                stream.Write(header); stream.Write(json); stream.Write(payload.Bytes); stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
            File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime);
            var entries = new DirectoryInfo(DirectoryPath).GetFiles().OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            long total = 0; int count = 0;
            foreach (var entry in entries)
            {
                total += entry.Length; count++;
                if (total <= diskLimit && count <= entryLimit) continue;
                ManagedModFiles.EnsureSafe(entry.FullName); entry.Delete();
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "The image cache could not be saved."; }
        finally
        {
            try { ManagedModFiles.EnsureSafe(temporary); File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void TouchDisk(string key)
    {
        try
        {
            string path = Path.Combine(DirectoryPath, key + ".cache");
            ManagedModFiles.EnsureSafe(path);
            if (File.Exists(path)) File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void DeleteDisk(string key)
    {
        try
        {
            string path = Path.Combine(DirectoryPath, key + ".cache");
            ManagedModFiles.EnsureSafe(path); File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal void ClearMemory()
    {
        lock (maintenance.Gate)
        {
            memory.Clear(); lru.Clear(); memoryBytes = 0;
            foreach (var transfer in transfers.Values) transfer.Cancellation.Cancel();
            transfers.Clear();
        }
    }
}
