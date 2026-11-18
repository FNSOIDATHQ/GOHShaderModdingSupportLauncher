using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace GOHShaderModdingSupportLauncher;

internal sealed record CachedModInspection(int Version, string Fingerprint, ModInspectionResult Result);

[JsonSerializable(typeof(CachedModInspection))]
[JsonSourceGenerationOptions(IgnoreReadOnlyProperties = true)]
internal partial class ModInspectionCacheJson : JsonSerializerContext;

internal sealed class ModInspectionCache(string? directory)
{
    // Bump when either the stored representation or the classification rules change.
    private const int Version = 1, MaxRecordBytes = 2 * 1024 * 1024, MaxRecords = 1024;
    private const long MaxBytes = 32 * 1024 * 1024;
    private readonly Dictionary<string, CachedModInspection> memory = new(StringComparer.Ordinal);
    private int writes;

    internal static string Key(string scope, ModSourceKind source, string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}\0{source}\0{path}")));

    // The inspection worker serializes access. A corrupt or inaccessible cache is disposable.
    internal ModInspectionResult? Read(string key, string fingerprint, string path, ModSourceKind source)
    {
        try
        {
            if (!memory.TryGetValue(key, out var entry) && directory is not null)
            {
                using var stream = File.OpenRead(Path.Combine(directory, key + ".json"));
                if (stream.Length > MaxRecordBytes) return null;
                entry = JsonSerializer.Deserialize(stream, ModInspectionCacheJson.Default.CachedModInspection);
            }
            if (entry is null || entry.Version != Version || entry.Fingerprint != fingerprint ||
                entry.Result is not { } result || result.DirectoryPath != path || result.Source != source ||
                result.DisplayName is null || result.DiscoveryId is null || !Enum.IsDefined(result.Structure) ||
                result.ExternalPrograms?.Evidence is null || result.Shaders?.Evidence is null ||
                result.ContentEvidence is null || result.OtherEvidence is null || result.Issues is null ||
                result.Issues.Any(issue => issue is null || issue.Reason == ModInspectionReason.DuplicateLoadId)) return null;
            Remember(key, entry);
            return result;
        }
        catch (Exception ex) when (FileManager.IsFileError(ex) || ex is JsonException or NotSupportedException) { return null; }
    }

    internal void Write(string key, string fingerprint, ModInspectionResult result, Action verify)
    {
        var entry = new CachedModInspection(Version, fingerprint, result);
        if (directory is null) { verify(); Remember(key, entry); return; }
        string? temporary = null;
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry, ModInspectionCacheJson.Default.CachedModInspection);
            if (bytes.Length > MaxRecordBytes) { verify(); Remember(key, entry); return; }
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            string path = Path.Combine(directory, key + ".json");
            temporary = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllBytes(temporary, bytes);
            verify();
            File.Move(temporary, path, overwrite: true);
            if (writes++ % 16 == 0) Trim();
        }
        catch (Exception ex) when (FileManager.IsFileError(ex) || ex is JsonException or NotSupportedException)
        { AppDiagnostics.Log("Unable to write Mod inspection cache.", ex); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception ex) when (FileManager.IsFileError(ex)) { }
        }
        verify();
        Remember(key, entry);
    }

    private void Remember(string key, CachedModInspection entry)
    {
        if (memory.Count >= 128 && !memory.ContainsKey(key)) memory.Remove(memory.First().Key);
        memory[key] = entry;
    }

    private void Trim()
    {
        long bytes = 0;
        int count = 0;
        foreach (var file in new DirectoryInfo(directory!).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc))
        {
            if (file.Name.Length != 69 || !file.Name.Take(64).All(Uri.IsHexDigit) ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            bytes += file.Length;
            if (++count > MaxRecords || bytes > MaxBytes) file.Delete();
        }
    }
}

internal readonly record struct ModArchiveVersion(long Length, long Modified, long Changed, ulong Volume, ulong IdLow, ulong IdHigh)
{
    internal static bool HasReliableVersions(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            return drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.DriveFormat is "NTFS" or "ReFS";
        }
        catch (Exception ex) when (FileManager.IsFileError(ex) || ex is NotSupportedException) { return false; }
    }

    internal static ModArchiveVersion? Read(FileStream stream)
    {
        if (!OperatingSystem.IsWindows() || !ReadBasic(stream.SafeFileHandle, 0, out var basic, 40) || basic.Changed <= 0 ||
            !ReadId(stream.SafeFileHandle, 18, out var id, 24) || (id.Low == 0 && id.High == 0)) return null;
        if ((basic.Attributes & (uint)FileAttributes.ReparsePoint) != 0) return null;
        return new(stream.Length, basic.Modified, basic.Changed, id.Volume, id.Low, id.High);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo { public long Created, Accessed, Modified, Changed; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong Volume, Low, High; }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadBasic(SafeFileHandle file, int informationClass, out FileBasicInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadId(SafeFileHandle file, int informationClass, out FileIdInfo info, uint size);
}
