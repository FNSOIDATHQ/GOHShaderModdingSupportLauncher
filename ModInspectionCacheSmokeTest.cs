using System.IO.Compression;
using System.Text.Json;
using Avalonia.Threading;

namespace GOHShaderModdingSupportLauncher;

// Shipped with the offline desktop self-test. All inputs and caches are inside its fixture.
internal static class ModInspectionCacheSmokeTest
{
    private sealed class TestWindow(string settings) : MainWindow(settings)
    { protected override void OnOpened(EventArgs e) { } }

    internal static void Run(string fixture)
    {
        string root = Directory.CreateDirectory(Path.Combine(fixture, "incremental-inspection")).FullName;
        string local = Directory.CreateDirectory(Path.Combine(root, "mods")).FullName;
        string workshop = Directory.CreateDirectory(Path.Combine(root, "workshop")).FullName;
        string first = MakeMod(local, "one");
        string second = MakeMod(workshop, "123");
        string pak = Path.Combine(first, "resource", "data.pak");
        WritePak(pak, "shader/dx10/a.fx");
        bool reliable = ModArchiveVersion.HasReliableVersions(pak);
        string cache = Path.Combine(root, "cache");
        var inspector = new ModInspectionService(cache);
        var initial = inspector.Scan(workshop, local);
        Require(initial.Mods.Single(mod => mod.DirectoryPath == first).Shaders.Detected &&
            inspector.Statistics == (2, 2, 1, 0), "cold scan visits both Mod directories and reads one archive");
        var warm = inspector.Scan(workshop, local);
        Require(inspector.Statistics == (4, 2, reliable ? 1 : 2, 2) &&
            warm.Mods.Zip(initial.Mods).All(pair => SameResult(pair.First, pair.Second)), "unchanged inputs skip classification");
        var restarted = new ModInspectionService(cache);
        var fromDisk = restarted.Scan(workshop, local);
        Require(restarted.Statistics == (2, 0, reliable ? 0 : 1, 2) &&
            fromDisk.Mods.Zip(initial.Mods).All(pair => SameResult(pair.First, pair.Second)), "disk hits match the original results");
        var direct = restarted.InspectDirectory(first, ModSourceKind.Local);
        Require(SameResult(direct, fromDisk.Mods.Single(mod => mod.DirectoryPath == first)), "a cached result agrees with direct classification");

        long size = new FileInfo(pak).Length;
        DateTime modified = File.GetLastWriteTimeUtc(pak);
        WritePak(pak, "models/base/a.fx");
        File.SetLastWriteTimeUtc(pak, modified);
        Require(new FileInfo(pak).Length == size && File.GetLastWriteTimeUtc(pak) == modified, "archive replacement preserves size and mtime");
        var expected = restarted.Statistics;
        var changed = restarted.Scan(workshop, local);
        Require(!changed.Mods.Single(mod => mod.DirectoryPath == first).Shaders.Detected &&
            restarted.Statistics.Classifications == expected.Classifications + 1 &&
            restarted.Statistics.Archives == expected.Archives + 1, "same-size archive change affects only its owner");

        // Exercise the fresh-index fallback explicitly, including a usable cache hit after reading the index.
        var weak = new ModInspectionService(Path.Combine(root, "weak-cache"), _ => false);
        _ = weak.Scan(workshop, local);
        _ = weak.Scan(workshop, local);
        Require(weak.Statistics == (4, 2, 2, 2), "weak versions reread the index and reuse the verified classification");

        string description = Path.Combine(first, "mod.info");
        DateTime descriptionTime = File.GetLastWriteTimeUtc(description);
        File.WriteAllText(description, "{mod {name \"two\"}}");
        File.SetLastWriteTimeUtc(description, descriptionTime);
        Require(restarted.Scan(workshop, local).Mods.Single(mod => mod.DirectoryPath == first).DisplayName == "two",
            "descriptor bytes are checked independently of mtime");
        using (File.Open(pak, FileMode.Open, FileAccess.Read, FileShare.None))
            Require(restarted.Scan(workshop, local).Mods.Single(mod => mod.DirectoryPath == first) is
                { HasRegularFeature: true, Shaders.Complete: false }, "a locked archive reports unknown coverage with current loose evidence");

        expected = restarted.Statistics;
        string duplicate = MakeMod(local, "mod_123");
        var duplicates = restarted.Scan(workshop, local).Mods.Where(mod => mod.LoadId == "mod_123").ToArray();
        Require(duplicates.Length == 2 && duplicates.All(mod => mod.HasRegularFeature && !mod.CanActivate) &&
            restarted.Statistics.Classifications == expected.Classifications + 1, "duplicate ID changes reuse the other owner's classification");
        Directory.Move(duplicate, Path.Combine(root, "removed-duplicate"));
        Require(restarted.Scan(workshop, local).Mods.Single(mod => mod.DirectoryPath == second).CanActivate,
            "removed duplicate restores eligibility");

        foreach (string path in Directory.EnumerateFiles(cache, "*.json")) File.WriteAllText(path, "{invalid");
        var corrupt = new ModInspectionService(cache);
        Require(corrupt.Scan(workshop, local).Mods.Count == 2 && corrupt.Statistics.Classifications == 2, "corrupt cache can be rebuilt");
        string blocked = Path.Combine(root, "cache-is-a-file");
        File.WriteAllText(blocked, "fixture");
        var unwritable = new ModInspectionService(blocked);
        _ = unwritable.Scan(workshop, local);
        _ = unwritable.Scan(workshop, local);
        Require(unwritable.Statistics.Hits == 2 && unwritable.Statistics.Classifications == 2, "failed disk writes retain usable memory results");

        ModInspectionService? interrupted = null;
        bool interrupt = true;
        interrupted = new ModInspectionService(null, path =>
        {
            if (interrupt) interrupted!.Invalidate(path, null, archiveMayHaveChanged: true);
            return ModArchiveVersion.HasReliableVersions(path);
        });
        try { interrupted.Scan(workshop, local); throw new InvalidOperationException("Invalidated scan was published."); }
        catch (ModInspectionChangedException) { }
        interrupt = false;
        Require(interrupted.Scan(workshop, local).Mods.Count == 2 && interrupted.Statistics.Hits == 0,
            "changed input prevents publication and can be retried");

        string options = Path.Combine(root, "options.set");
        File.WriteAllText(options, "{options\n{mods\n\"one:0\"\n}\n}\n");
        var window = new TestWindow(Path.Combine(root, "settings.conf")) { SuppressSubscriptionActivation = true };
        window.universalVars.localDir = new DirectoryInfo(local);
        window.universalVars.workshopDir = new DirectoryInfo(workshop);
        window.universalVars.optionLoc = options;
        try
        {
            var launch = window.CheckEnabledModsForLaunchAsync();
            ModActivationSmokeTest.Pump(launch);
            Require(launch.Result && window.ModInspectionStatistics.Trees == 1 &&
                window.universalVars.modDic["mod_123"].IsInspectionPending, "launch verifies only the enabled Mod");
            window.Content = new ModManager(window);
            window.Show();
            ModActivationSmokeTest.Pump(window.PendingModRefresh);
            expected = window.ModInspectionStatistics;
            var lastRefresh = window.PendingModRefresh;
            window.EnsureModsScanned();
            Require(window.ModInspectionStatistics == expected && ReferenceEquals(lastRefresh, window.PendingModRefresh),
                "unchanged manager entry does not schedule a directory traversal");

            var other = window.universalVars.modDic["mod_123"];
            var otherResult = other.Inspection;
            string file = Path.Combine(first, "resource", "tool.dll");
            File.WriteAllText(file, "fixture");
            ModActivationSmokeTest.Pump(WaitForAsync(() => window.universalVars.modDic["one"] is
                { IsInspectionPending: false, Inspection.ExternalPrograms.Detected: true }));
            ModActivationSmokeTest.Pump(window.PendingModRefresh);
            Require(window.ModInspectionStatistics.Classifications > expected.Classifications &&
                ReferenceEquals(window.universalVars.modDic["mod_123"], other) &&
                ReferenceEquals(other.Inspection, otherResult) && !other.IsInspectionPending &&
                window.ModScanProgress?.InspectedDirectories == 1,
                $"watcher changes only reclassify the affected row ({expected} to {window.ModInspectionStatistics}, other pending: {other.IsInspectionPending})");

            expected = window.ModInspectionStatistics;
            lastRefresh = window.PendingModRefresh;
            window.LoseModDirectoryCoverage(workshop);
            Dispatcher.UIThread.RunJobs();
            Require(other.IsInspectionPending && !window.universalVars.modDic["one"].IsInspectionPending,
                "lost coverage makes only the affected root unverified");
            ModActivationSmokeTest.Pump(WaitForAsync(() => !ReferenceEquals(lastRefresh, window.PendingModRefresh) && !other.IsInspectionPending));
            ModActivationSmokeTest.Pump(window.PendingModRefresh);
            Require(window.ModInspectionStatistics.Trees == expected.Trees + 1 &&
                window.ModInspectionStatistics.Classifications == expected.Classifications, "lost coverage reconciles the root and reuses matching results");

            Directory.Move(second, Path.Combine(workshop, "456"));
            ModActivationSmokeTest.Pump(WaitForAsync(() => !window.universalVars.modDic.ContainsKey("mod_123") &&
                window.universalVars.modDic.TryGetValue("mod_456", out var moved) && moved.CanActivate));
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
        AppDiagnostics.Log($"Incremental Mod inspection self-test passed: stable/bypass and disk parity, {(reliable ? "strong Windows" : "weak")} versions, targeted archive/metadata changes, unknown coverage, duplicates, corrupt/unwritable cache, invalidation, targeted launch, live changes/rename and lost-coverage recovery.");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        while (!condition()) await Task.Delay(20);
    }

    private static string MakeMod(string parent, string name)
    {
        string mod = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
        File.WriteAllText(Path.Combine(mod, "mod.info"), "{mod {name \"" + name + "\"}}");
        string resource = Directory.CreateDirectory(Path.Combine(mod, "resource")).FullName;
        File.WriteAllText(Path.Combine(resource, "content.set"), "fixture");
        return mod;
    }

    private static void WritePak(string path, string entry)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        using var payload = zip.CreateEntry(entry, CompressionLevel.NoCompression).Open();
        payload.WriteByte(1);
    }

    private static bool SameResult(ModInspectionResult left, ModInspectionResult right)
    {
        var json = ModInspectionCacheJson.Default.CachedModInspection;
        return JsonSerializer.Serialize(new CachedModInspection(1, "", left), json) ==
            JsonSerializer.Serialize(new CachedModInspection(1, "", right), json);
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Incremental Mod inspection: " + message); }
}
