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
        static void TextArchive(string path, string relative, params (string Path, string Text)[] entries)
        {
            string file = Path.Combine(path, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
            foreach (var entry in entries)
            {
                var item = archive.CreateEntry(entry.Path);
                if (entry.Path.EndsWith('/')) continue;
                using var writer = new StreamWriter(item.Open());
                writer.Write(entry.Text);
            }
        }
        ModInspectionResult Inspect(string path) => inspector.InspectDirectory(path, ModSourceKind.Local);

        string loose = Make("loose", "\uFEFF; {name \"comment\"}\n{MOD {desc \"{name \\\"wrong\\\"} ; //\"} {NAME \"Actual \\\"name\\\"\"} {unknown {child 1}}}");
        Write(loose, "RESOURCE/map/mission.custom-extension");
        var result = Inspect(loose);
        Require(result is { IsRegular: true, CanActivate: true, DisplayName: "Actual \"name\"", StructureComplete: true } &&
            result.ExternalPrograms is { Detected: false, Complete: true }, "loose content, metadata grammar or independent states");

        string[] extendedDescriptors =
        [
            "{delete \"resource/entity/old/\"}\n{mod {name \"Extended\"}}\n{delete \"resource/set/old.set\"}",
            "{future_extension arbitrary \"quoted } { content\" {nested {name \"Not the Mod\"}}}\n{mod {name \"Extended\"}}\n{another_extension {child 1}}",
            "{future_extension {mod {name \"Nested decoy\"}}}\n{mod {name \"Extended\"}}",
            "{name \"Unrelated top-level name\"}\n{mod {name \"Extended\"}}\n{unknown {name \"Also unrelated\"}}",
            "{extension {\"opaque data\"} {}}\n{mod {name \"Extended\"} {future_field {\"opaque data\"} {}}}\n{name \"Do not replace the Mod name\"}",
            "{name unrelated scalar values}\n{mod {name \"Extended\"}}",
            "/* {mod {name \"Comment\"}} */ {delete \"../not-a-path-validation-task\"}\n{mod {name \"Extended\"} {delete \"inside-mod\"} {minGameVersion \"any\"} {require \"unresolved_dependency\"}} ; trailing comment\n"
        ];
        for (int i = 0; i < extendedDescriptors.Length; i++)
        {
            string extended = Make("extendedmeta_" + i, extendedDescriptors[i]);
            Write(extended, "resource/normal.set");
            Require(Inspect(extended) is { HasRegularFeature: true, CanActivate: true, DisplayName: "Extended" },
                "balanced extension declarations around the unique Mod block do not invalidate metadata: " + i);
        }
        string sentinel = Path.Combine(root, "declaration-sentinel.txt");
        File.WriteAllText(sentinel, "The inspection must retain this file unchanged.");
        byte[] sentinelBytes = File.ReadAllBytes(sentinel);
        string quotedSentinel = "\"" + sentinel.Replace("\\", "\\\\") + "\"";
        string declarative = Make("declarativemeta", "{delete " + quotedSentinel + "}\n{mod {name \"Declarative\"} {delete " + quotedSentinel + "}}\n{future_action {target " + quotedSentinel + "}}");
        Write(declarative, "resource/normal.set");
        Require(Inspect(declarative) is { HasRegularFeature: true, CanActivate: true, DisplayName: "Declarative" } &&
            File.Exists(sentinel) && File.ReadAllBytes(sentinel).SequenceEqual(sentinelBytes),
            "extension and delete declarations remain passive data; inspection never executes author operations");

        string localization = Make("localization");
        Write(localization, "localizations/default/interface/text/main.pot", "msgctxt \"name\"\nmsgid \"Unit\"\nmsgstr \"\"");
        Require(Inspect(localization).IsRegular, "localization-only content");
        string localizedPak = Make("localizedpak");
        Pak(localizedPak, "localizations/default/text.pak", "interface/text/main.po");
        Require(Inspect(localizedPak).IsRegular, "localization PAK content");
        string nestedLocalization = Make("nestedlocalization");
        Pak(nestedLocalization, "localizations/ch/interface/text/messages.pak", "desc/main.po");
        Require(Inspect(nestedLocalization) is { HasRegularFeature: true, HasOtherFeature: false, CanActivate: true },
            "localization PAK below the language root remains regular");

        string nestedPak = Make("nestedpak");
        Pak(nestedPak, "resource/entity/-vehicle.pak", "-vehicle/test/model.mdl", "-vehicle/test/body.mtl");
        result = Inspect(nestedPak);
        Require(result is { HasRegularFeature: true, HasOtherFeature: false, CanActivate: true, ContentMatchCount: 2 } &&
            result.ContentEvidence.All(item => item.RelativePath.StartsWith("resource/entity/-vehicle.pak!/", StringComparison.Ordinal)),
            "deep game PAK preserves both resource evidence and physical archive paths");
        string nestedShaderBoundary = Make("nestedshaderboundary");
        Pak(nestedShaderBoundary, "resource/entity/content.pak", "shader/dx10/test.fx");
        Require(Inspect(nestedShaderBoundary) is { HasRegularFeature: true, Shaders.Detected: false },
            "shader-like entry below entity does not mount at the resource shader root");
        string prefixedPak = Make("prefixedpak");
        Pak(prefixedPak, "resource/content.pak", "resource/entity/test/model.def", "resource/shader/dx10/known.fx");
        Require(Inspect(prefixedPak) is { HasRegularFeature: true, HasOtherFeature: false, ContentMatchCount: 2, Shaders: { Detected: true, Complete: true } },
            "a complete resource prefix within its mount is recognized without duplicating the mount path");
        string crossMountPak = Make("crossmountpak");
        Write(crossMountPak, "resource/entity/normal.def");
        Pak(crossMountPak, "resource/entity/content.pak", "resource/shader/dx10/not-mounted.fx");
        result = Inspect(crossMountPak);
        Require(result is { HasRegularFeature: true, CanActivate: true, ContentMatchCount: 1, Shaders: { Detected: false, Complete: false } } &&
            result.Issues.Any(item => item.Reason == ModInspectionReason.UnrecognizedLayout),
            "cross-mount shader prefix remains uncertain instead of relocating an entity package into the shader root");

        string template = Make("templatepatch");
        Write(template, "resource/map/template.inc");
        TextArchive(template, "1.pat", (".patch", "{Patch {id 1}}"), ("set/", ""));
        Require(Inspect(template) is { HasRegularFeature: true, HasOtherFeature: false, CanActivate: true,
            ExternalPrograms.Complete: true, Shaders.Complete: true }, "normal PAT companion does not remove ordinary content or invent Other");
        string markerlessPatch = Make("markerlesspatch");
        Write(markerlessPatch, "resource/normal.set");
        TextArchive(markerlessPatch, "1.pat", ("set/change.set", "fixture"));
        Require(Inspect(markerlessPatch) is { HasRegularFeature: true, HasOtherFeature: true, CanActivate: true, ContentMatchCount: 1 },
            "root PAT without a patch marker is additional content and cannot invent a mounted resource");
        string emptyMarkerPatch = Make("emptymarkerpatch");
        Write(emptyMarkerPatch, "resource/normal.set");
        TextArchive(emptyMarkerPatch, "1.pat", (".patch", ""));
        Require(Inspect(emptyMarkerPatch) is { HasRegularFeature: true, HasOtherFeature: true, CanActivate: true },
            "empty patch marker does not silently qualify an arbitrary PAT as a normal game patch");
        string nestedPatch = Make("nestedpatch");
        Write(nestedPatch, "resource/normal.set");
        TextArchive(nestedPatch, "file/backup.pat", (".patch", "{Patch {id 1}}"), ("set/change.set", "fixture"));
        Require(Inspect(nestedPatch) is { HasRegularFeature: true, HasOtherFeature: true, CanActivate: true, ContentMatchCount: 1 },
            "PAT with a marker outside the Mod root remains an attachment without removing ordinary eligibility");
        string patchOnly = Make("patchonly");
        TextArchive(patchOnly, "1.pat", (".patch", "{Patch {id 1}}"), ("set/", ""), ("bin/plugin.dll", "static fixture"));
        Require(Inspect(patchOnly) is { HasRegularFeature: false, CanActivate: false, ExternalPrograms: { Detected: true, Complete: true } },
            "PAT marker alone does not establish an unverified load mechanism and program entries remain visible");
        string patchShader = Make("patchshader");
        Write(patchShader, "resource/normal.set");
        TextArchive(patchShader, "1.pat", (".patch", "{Patch {id 1}}"), ("resource/shader/dx10/example.fx", "fixture"));
        Require(Inspect(patchShader) is { HasRegularFeature: true, CanActivate: true, Shaders: { Detected: false, Complete: false } },
            "unverified PAT mount retains shader uncertainty without denying ordinary activation");

        string localizationBackup = Make("localizationbackup");
        Write(localizationBackup, "localizations/ch/interface/text/main.po", "msgid \"Unit\"\nmsgstr \"单位\"");
        TextArchive(localizationBackup, "localizations/ch/interface/text/desc.zip", ("desc/main.po", "msgid \"Unit\"\nmsgstr \"单位\""));
        Require(Inspect(localizationBackup) is { HasRegularFeature: true, HasOtherFeature: false, CanActivate: true, Shaders.Detected: false },
            "recognized localization text backup does not manufacture Other");
        string sourcePackage = Make("sourcepackage");
        Write(sourcePackage, "resource/entity/model.def");
        Write(sourcePackage, "file/base/source.max");
        Write(sourcePackage, "file/base/converter.exe");
        TextArchive(sourcePackage, "extras.zip", ("resource/shader/dx10/not-deployed.fx", "fixture"));
        result = Inspect(sourcePackage);
        Require(result is { HasRegularFeature: true, HasOtherFeature: true, CanActivate: true,
            ExternalPrograms.Detected: true, Shaders.Detected: false } && result.OtherEvidence.Count > 0,
            "ordinary content, development material and external tools coexist without treating an attachment shader as deployed");
        string undeployedAttachment = Make("undeployedattachment");
        TextArchive(undeployedAttachment, "tools.zip", ("bin/tool.dll", "static fixture"), ("resource/shader/dx10/not-deployed.fx", "fixture"));
        Require(Inspect(undeployedAttachment) is { HasRegularFeature: false, HasOtherFeature: true, CanActivate: false, ContentMatchCount: 0,
            ExternalPrograms: { Detected: true, MatchCount: 1 }, Shaders.Detected: false },
            "valid ZIP indexing detects its program while neither deploying shader entries nor inventing ordinary content");

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
            Require(Inspect(only) is { Structure: ModStructureState.Unconfirmed, HasRegularFeature: false, ExternalPrograms.Detected: true, CanActivate: false },
                "external-only remains ineligible without claiming an unrecognized load mechanism is invalid: " + extension);
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
        foreach (string text in new[]
        {
            "{mod {desc \"{name wrong}\"}}", "{mod {name \"A\"}", "{mod {name \"A\"}} trailing",
            "{mod {name \"A\"} {name \"B\"}}", "{name \"A\"}", "{mod {nested {name \"A\"}}}",
            "{mod {name \"A\"}} {mod {name \"B\"}}", "{mod {name \"A\"}} {MOD {name \"A\"}}",
            "{extension {mod {name \"Nested only\"}}}", "{extension {name \"Borrowed\"}} {mod {desc \"No direct name\"}}",
            "; {mod {name \"Comment only\"}}\n{extension {name \"Borrowed\"}}",
            "{mod {name \"A\"}} {delete \"unfinished\"", "{mod {name \"A\"}} {extra \"unfinished}",
            "{mod {name \"A\"}} \"unwrapped tail\""
        })
        {
            string invalid = Make("badmeta_" + checks, text);
            Write(invalid, "resource/mission.set");
            Require(Inspect(invalid).Structure == ModStructureState.Invalid, "malformed metadata must not borrow a nested or quoted name");
        }
        string deepMetadata = Make("deepmetadata", "{mod {name \"Too deep\"}} {extension " +
            string.Concat(Enumerable.Repeat("{layer ", 66)) + "value" + new string('}', 66) + "}");
        Write(deepMetadata, "resource/normal.set");
        Require(Inspect(deepMetadata) is { Structure: ModStructureState.Invalid, CanActivate: false },
            "extension declarations retain the descriptor nesting-depth limit after a valid Mod block");
        string largeMetadata = Make("largemetadata", "{mod {name \"Too large\"}} {extension \"" + new string('x', 1024 * 1024) + "\"}");
        Write(largeMetadata, "resource/normal.set");
        result = Inspect(largeMetadata);
        Require(result is { Structure: ModStructureState.Unconfirmed, CanActivate: false } &&
            result.Issues.Any(issue => issue.Reason == ModInspectionReason.ScanLimit && issue.RelativePath == "mod.info"),
            "extension declarations do not bypass the metadata byte limit");
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
        Require(Inspect(broken) is { HasRegularFeature: true, HasOtherFeature: false, CanActivate: true,
            StructureComplete: true, Shaders.Complete: false }, "unreadable optional sibling retains ordinary activation without manufacturing Other");
        string brokenPatch = Make("brokenpatch");
        Write(brokenPatch, "resource/map/valid.set");
        Write(brokenPatch, "1.pat", "broken patch");
        Require(Inspect(brokenPatch) is { HasRegularFeature: true, HasOtherFeature: false, CanActivate: true,
            ExternalPrograms.Complete: false, Shaders.Complete: false }, "unreadable PAT reports its coverage separately from ordinary activation");
        string limitedSibling = Make("limitedsibling");
        Write(limitedSibling, "resource/map/valid.set");
        string branch = limitedSibling;
        for (int i = 0; i < 67; i++) branch = Directory.CreateDirectory(Path.Combine(branch, "deep")).FullName;
        File.WriteAllText(Path.Combine(branch, "unseen.dll"), "static fixture");
        Require(Inspect(limitedSibling) is { HasRegularFeature: true, CanActivate: true, HasOtherFeature: false,
            ExternalPrograms.Complete: false, Shaders.Complete: false }, "depth-limited optional subtree does not erase prioritized metadata and resource evidence");
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
        Require(Inspect(invalidId) is { HasRegularFeature: true, HasOtherFeature: false, LoadId: null, CanActivate: false },
            "invalid load ID independently blocks activation without erasing ordinary content");
        string duplicate = Make("mod_123");
        string workshopMod = Directory.CreateDirectory(Path.Combine(workshop, "123")).FullName;
        Write(workshopMod, "mod.info", "{mod {name \"Workshop\"}}");
        Write(workshopMod, "resource/test.set");
        Write(duplicate, "resource/test.set");
        Make(".gohsms");
        var scan = inspector.Scan(workshop, local);
        Require(scan.Mods.Count(item => item.LoadId == "mod_123") == 2 && scan.Mods.Where(item => item.LoadId == "mod_123").All(item =>
            item.HasRegularFeature && !item.CanActivate && item.Issues.Any(issue => issue.Reason == ModInspectionReason.DuplicateLoadId)),
            "duplicate IDs independently block activation while retaining ordinary labels");
        Require(scan.Mods.All(item => Path.GetFileName(item.DirectoryPath) != ".gohsms"), "launcher internals excluded");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool canceled = false;
        try { inspector.Scan(workshop, local, cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
        Require(canceled, "cancellation propagated without a partial snapshot");
        AppDiagnostics.Log($"Mod inspection self-test passed: {checks} checks; ordinary/mixed/localization/PAK content, independent states, metadata, IDs, refresh and cancellation.");
    }
}
