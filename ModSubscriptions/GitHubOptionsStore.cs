using System.Text.Json;
using System.Text.Json.Serialization;

namespace GOHShaderModdingSupportLauncher.ModSubscriptions;

internal sealed record GitHubSourceSettings
{
    public int Version { get; init; } = 1;
    public string[] Topics { get; init; } = ["gates-of-hell"];
    public bool MatchAll { get; init; }
}

// Anonymous discovery preferences are independent of game-specific subscriptions.
internal sealed class GitHubOptionsStore(string? path = null)
{
    private readonly object sync = new();
    private readonly string path = path ?? Path.Combine(Path.GetDirectoryName(SubscriptionStore.DefaultPath)!, "github-settings.json");

    internal GitHubSourceSettings Snapshot()
    {
        lock (sync)
        {
            if (!File.Exists(path)) return new();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 16 * 1024) throw new IOException("The GitHub settings file exceeds its size limit.");
            var settings = JsonSerializer.Deserialize(stream, GitHubSettingsJsonContext.Default.GitHubSourceSettings)
                ?? throw new IOException("The GitHub settings file is invalid.");
            if (settings.Version != 1 || settings.Topics is null) throw new IOException("The GitHub settings format is unsupported.");
            return settings with { Topics = ValidateTopics(settings.Topics) };
        }
    }

    internal GitHubSourceSettings Save(string[] topics, bool matchAll)
    {
        var settings = new GitHubSourceSettings { Topics = ValidateTopics(topics), MatchAll = matchAll };
        lock (sync)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, settings, GitHubSettingsJsonContext.Default.GitHubSourceSettings);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        return settings;
    }

    internal static string[] ValidateTopics(string[] topics)
    {
        ArgumentNullException.ThrowIfNull(topics);
        if (topics.Length > 10) throw new ArgumentException("Configure at most ten GitHub topics.", nameof(topics));
        var normalized = topics.Select(topic => topic?.Trim().ToLowerInvariant() ?? "").Where(topic => topic.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        foreach (string topic in normalized)
            if (topic.Length > 50 || topic.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) || topic[0] == '-')
                throw new ArgumentException("GitHub topics use up to fifty lowercase letters, numbers and hyphens.", nameof(topics));
        return normalized;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GitHubSourceSettings))]
internal partial class GitHubSettingsJsonContext : JsonSerializerContext { }
