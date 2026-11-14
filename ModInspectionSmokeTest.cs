using System.IO.Compression;
using System.Text;

namespace GOHShaderModdingSupportLauncher;

// Shipped with --self-test-ui so the same rules run in the final Native AOT executable.
internal static class ModInspectionSmokeTest
{
    internal static void Run(string fixture)
    {
        string root = Directory.CreateDirectory(Path.Combine(fixture, "inspection")).FullName;
        string local = Directory.CreateDirectory(Path.Combine(root, "mods")).FullName;
        string workshop = Directory.CreateDirectory(Path.Combine(root, "workshop")).FullName;
        var inspector = new ModInspectionService();
        int checks = 0;
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Mod inspection self-test: " + message);
            checks++;
        }
        string Make(string name, string? metadata = "{mod {name \"Example\"}}")
        {
            string path = Directory.CreateDirectory(Path.Combine(local, name)).FullName;
            if (metadata is not null) File.WriteAllText(Path.Combine(path, "mod.info"), metadata);
            return path;
        }
        static void Write(string path, string relative, string text = "fixture")
        {
            string file = Path.Combine(path, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        static void Pak(string path, string relative, params string[] entries)
        {
            string file = Path.Combine(path, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
                writer.Write("fixture");
            }
        }
        ModInspectionResult Inspect(string path) => inspector.InspectDirectory(path, ModSourceKind.Local);

        string loose = Make("loose", "\uFEFF; {name \"comment\"}\n{MOD {desc \"{name \\\"wrong\\\"} ; //\"} {NAME \"Actual \\\"name\\\"\"} {unknown {child 1}}}");
        Write(loose, "RESOURCE/map/mission.custom-extension");
        var result = Inspect(loose);
        Require(result is { IsRegular: true, CanActivate: true, DisplayName: "Actual \"name\"", StructureComplete: true } &&
            result.ExternalPrograms is { Detected: false, Complete: true }, "loose content, metadata grammar or independent states");

        string localization = Make("localization");
        Write(localization, "localizations/default/interface/text/main.pot", "msgctxt \"name\"\nmsgid \"Unit\"\nmsgstr \"\"");
        Require(Inspect(localization).IsRegular, "localization-only content");
        string localizedPak = Make("localizedpak");
        Pak(localizedPak, "localizations/default/text.pak", "interface/text/main.po");
        Require(Inspect(localizedPak).IsRegular, "localization PAK content");

        string packed = Make("packed");
        Pak(packed, "resource/content.pak", "texture/asset.dds", "bin/loader.DLL", "./SHADER\\DX10\\nested\\example.fx");
        result = Inspect(packed);
        Require(result.IsRegular && result.ExternalPrograms.Detected && result.Shaders.Detected && result.CanActivate &&
            result.ExternalPrograms.Evidence.Any(item => item.RelativePath.Contains("!/bin/loader.DLL")), "PAK mixed content and normalized shader paths");

        string rar = Make("rar");
        Directory.CreateDirectory(Path.Combine(rar, "resource"));
        File.WriteAllBytes(Path.Combine(rar, "resource/shader.pak"), UiSmokeTest.CreateRarShaderFixture());
        Require(Inspect(rar) is { IsRegular: true, Shaders.Detected: true }, "RAR PAK-only shader compatibility");

        string shader = Make("shader");
        Write(shader, "resource/shader/dx10/test.fx");
        Write(shader, "bin/extension.asi");
        Write(shader, "install/install.cmd");
        Require(Inspect(shader) is { IsRegular: true, CanActivate: true, Shaders.Detected: true, ExternalPrograms.Detected: true }, "loose three-feature mix");
        string boundary = Make("boundary");
        Pak(boundary, "resource/content.pak", "notshader/dx10/test.fx", "shader/dx100/test.fx");
        Write(boundary, "resource/effects/other.fx");
        Require(Inspect(boundary) is { IsRegular: true, Shaders.Detected: false }, "shader path segment boundary");

        foreach (string extension in new[] { "dll", "EXE", "AsI", "com", "scr", "msi", "msp", "bat", "cmd", "ps1", "vbs", "vbe", "wsf" })
        {
            string only = Make("only_" + extension);
            Write(only, "bin/tool." + extension);
            Require(Inspect(only) is { Structure: ModStructureState.Invalid, ExternalPrograms.Detected: true, CanActivate: false }, "external-only " + extension);
        }
        string fakeResource = Make("fakeresource");
        Write(fakeResource, "resource/plugin.dll");
        Write(fakeResource, "resource/README.txt");
        Write(fakeResource, "resource/preview.jpg");
        Require(Inspect(fakeResource) is { IsRegular: false, CanActivate: false, ExternalPrograms.Detected: true }, "program and release documentation are not ordinary resources");
        string resourcePreview = Make("resourcepreview");
        Write(resourcePreview, "resource/texture/preview.dds");
        Require(Inspect(resourcePreview).IsRegular, "preview-named game assets remain ordinary resources");
        string misplacedPak = Make("misplacedpak");
        Pak(misplacedPak, "content.pak", "map/test.set");
        Write(misplacedPak, "install.bat");
        Require(Inspect(misplacedPak) is { Structure: ModStructureState.Unconfirmed, ExternalPrograms.Detected: true }, "unsupported PAK placement stays unconfirmed");
        string gameScripts = Make("scripts");
        foreach (string extension in new[] { "lua", "set", "inc", "fx", "js", "py", "dll.txt" }) Write(gameScripts, "resource/script." + extension);
        Require(Inspect(gameScripts) is { IsRegular: true, ExternalPrograms: { Detected: false, Complete: true } }, "no executable heuristic for game or unfamiliar scripts");
        string many = Make("many");
        for (int i = 0; i < 130; i++) Write(many, "bin/part" + i + ".dll");
        Require(Inspect(many).ExternalPrograms is { Detected: true, Complete: true, MatchCount: 130, EvidenceTruncated: true }, "bounded evidence preserves total match count");

        string noMetadata = Make("missing", null);
        Write(noMetadata, "resource/map/mission.set");
        Require(Inspect(noMetadata) is { Structure: ModStructureState.Invalid, CanActivate: false }, "missing metadata");
        Write(noMetadata, "inner/mod.info", "{mod {name \"Inner\"}}");
        Require(Inspect(noMetadata).Issues.Any(item => item.Reason == ModInspectionReason.NestedModRoot && item.RelativePath == "inner/mod.info"), "nested root evidence");
        foreach (string text in new[] { "{mod {desc \"{name wrong}\"}}", "{mod {name \"A\"}", "{mod {name \"A\"}} trailing", "{mod {name \"A\"} {name \"B\"}}", "{name \"A\"}", "{mod {nested {name \"A\"}}}" })
        {
            string invalid = Make("badmeta_" + checks, text);
            Write(invalid, "resource/mission.set");
            Require(Inspect(invalid).Structure == ModStructureState.Invalid, "malformed metadata must not borrow a nested or quoted name");
        }
        string metadataOnly = Make("metadataonly", "{mod {name \"Collection\"} {require \"other_mod\"}}");
        Require(Inspect(metadataOnly) is { Structure: ModStructureState.Unconfirmed, StructureComplete: false }, "unverified metadata-only mechanism stays unconfirmed");
        string encoded = Make("encoding");
        File.WriteAllBytes(Path.Combine(encoded, "mod.info"), [0xff, 0x80, 0x81]);
        Write(encoded, "resource/test.set");
        Require(Inspect(encoded) is { Structure: ModStructureState.Unconfirmed, ExternalPrograms.Complete: true }, "encoding failure is not invalid syntax or a failed program scan");
        File.WriteAllText(Path.Combine(encoded, "mod.info"), "{mod {name \"Unicode\"}}", Encoding.Unicode);
        Require(Inspect(encoded).IsRegular, "UTF-16 BOM metadata");

        string broken = Make("broken");
        Write(broken, "resource/broken.pak");
        result = Inspect(broken);
        Require(result is { Structure: ModStructureState.Unconfirmed, ExternalPrograms.Complete: false, Shaders.Complete: false }, "unreadable sole PAK does not invent absence");
        Write(broken, "resource/map/valid.set");
        Require(Inspect(broken) is { IsRegular: true, StructureComplete: true, Shaders.Complete: false }, "unreadable optional sibling retains positive ordinary evidence");
        string attachment = Make("attachment");
        Write(attachment, "installer.zip");
        Require(Inspect(attachment) is { Structure: ModStructureState.Unconfirmed, ExternalPrograms.Complete: false }, "nested archives have an explicit uninspected scope");
        string unsafePak = Make("unsafepak");
        Pak(unsafePak, "resource/bad.pak", "../shader/dx10/test.fx", "../bin/plugin.dll");
        Require(Inspect(unsafePak) is { IsRegular: false, ExternalPrograms: { Detected: false, Complete: false }, Shaders.Detected: false }, "traversal entries cannot establish positive evidence");

        string locked = Make("locked");
        Write(locked, "resource/test.set");
        using (var handle = new FileStream(Path.Combine(locked, "mod.info"), FileMode.Open, FileAccess.Read, FileShare.None))
            Require(Inspect(locked) is { Structure: ModStructureState.Unconfirmed, ExternalPrograms.Complete: true }, "locked metadata is not diagnosed as malformed");

        string cached = Make("cached");
        Pak(cached, "resource/test.pak", "shader/dx10/example.fx");
        Require(Inspect(cached).Shaders.Detected, "initial PAK index");
        using (var handle = new FileStream(Path.Combine(cached, "resource/test.pak"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Require(Inspect(cached).Shaders.Detected, "index reuse within unchanged path/length/mtime");
            inspector.ClearCache();
            Require(Inspect(cached) is { Shaders: { Detected: false, Complete: false } }, "explicit refresh invalidates PAK hints");
        }
        Require(FileManager.TryReadPakEntries(Path.Combine(packed, "resource/content.pak"), maxEntries: 2) is null,
            "PAK index budget stops oversized indexes");
        string invalidId = Make("contains space");
        Write(invalidId, "resource/test.set");
        Require(Inspect(invalidId) is { IsRegular: true, LoadId: null, CanActivate: false }, "display identity differs from load ID");
        string duplicate = Make("mod_123");
        string workshopMod = Directory.CreateDirectory(Path.Combine(workshop, "123")).FullName;
        Write(workshopMod, "mod.info", "{mod {name \"Workshop\"}}");
        Write(workshopMod, "resource/test.set");
        Write(duplicate, "resource/test.set");
        Make(".gohsms");
        var scan = inspector.Scan(workshop, local);
        Require(scan.Mods.Count(item => item.LoadId == "mod_123") == 2 && scan.Mods.Where(item => item.LoadId == "mod_123").All(item =>
            !item.CanActivate && item.Issues.Any(issue => issue.Reason == ModInspectionReason.DuplicateLoadId)), "duplicate IDs retained with reasons");
        Require(scan.Mods.All(item => Path.GetFileName(item.DirectoryPath) != ".gohsms"), "launcher internals excluded");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool canceled = false;
        try { inspector.Scan(workshop, local, cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
        Require(canceled, "cancellation propagated without a partial snapshot");
        AppDiagnostics.Log($"Mod inspection self-test passed: {checks} checks; ordinary/mixed/localization/PAK content, independent states, metadata, IDs, refresh and cancellation.");
    }
}
