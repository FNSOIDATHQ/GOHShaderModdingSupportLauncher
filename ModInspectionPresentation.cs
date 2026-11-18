using System.Globalization;

namespace GOHShaderModdingSupportLauncher;

internal static class ModInspectionPresentation
{
    internal static string Text(string key) => LocalizedStrings.Instance.Get("MI_" + key);
    internal static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Text(key), args);

    internal static bool IsOther(ModInspectionResult result) => result.HasOtherFeature;

    internal static string Qualification(ModInspectionResult result) => Text(result.CanActivate ? "Eligible" : "ActivationBlocked");

    internal static IEnumerable<string> FeatureKeys(ModInspectionResult result)
    {
        if (result.HasRegularFeature) yield return "Regular";
        if (result.ExternalPrograms.Detected) yield return "Dll";
        if (result.Shaders.Detected) yield return "Shader";
        if (result.HasOtherFeature) yield return "Other";
    }

    internal static string Summary(ModInspectionResult? result)
    {
        if (result is null) return Text("NotInspected");
        return string.Join(" · ", FeatureKeys(result).Select(Text));
    }

    internal static string Details(Mod? mod)
    {
        if (mod is null) return Text("NotInspected");
        var lines = new List<string> { mod.DisplayName };
        if (mod.HasAlias) lines.Add(Format("OriginalName", mod.name));
        lines.Add(Format("Directory", mod.path));
        if (mod.IsInspectionPending) { lines.Add(Text("Checking")); return string.Join("\n", lines); }
        if (mod.Inspection is not { } result) { lines.Add(Text("NotInspected")); return string.Join("\n", lines); }
        lines.Add(Text("Source") + ": " + Text(result.Source == ModSourceKind.Local ? "Local" : "Workshop"));
        lines.Add(Format("LoadId", result.LoadId ?? Text("Unavailable")));
        lines.Add(Format("Qualification", Qualification(result)));
        if (mod.hasLoad && !result.CanActivate) lines.Add(Text("AlreadyEnabled"));
        lines.Add(Format("StructureCheck", Text(result.StructureComplete ? "Complete" : "Incomplete")));
        AddEvidence("ContentEvidence", result.ContentEvidence, result.ContentMatchCount);
        if (result.OtherMatchCount > 0) AddEvidence("OtherEvidence", result.OtherEvidence, result.OtherMatchCount);
        AddFeature("Dll", result.ExternalPrograms);
        AddFeature("Shader", result.Shaders);
        if (result.Issues.Count > 0)
        {
            lines.Add("\n" + Text("Reasons"));
            foreach (var issue in result.Issues)
            {
                lines.Add("• " + Text("Reason_" + issue.Reason));
                if (issue.RelativePath.Length > 0) lines.Add("  " + issue.RelativePath);
                if (issue.Detail.Length > 0) lines.Add("  " + Format("Diagnostic", issue.Detail));
            }
        }
        return string.Join("\n", lines);

        void AddFeature(string key, ModFeatureInspection feature)
        {
            lines.Add("\n" + Text(key) + ": " + Text(feature.Detected ? "Detected" : feature.Complete ? "NotDetected" : "Unknown") +
                " · " + Text(feature.Complete ? "Complete" : "Incomplete"));
            AddEvidence("Evidence", feature.Evidence, feature.MatchCount);
        }
        void AddEvidence(string key, IReadOnlyList<ModInspectionEvidence> evidence, int total)
        {
            lines.Add(Format(key, total));
            foreach (var item in evidence) lines.Add("  " + item.RelativePath + " [" + EvidenceKind(item.Kind) + "]");
            if (total > evidence.Count) lines.Add(Format("Truncated", evidence.Count, total));
        }
    }

    private static string EvidenceKind(string kind) => kind switch
    {
        "resource" => Text("Resource"), "localization" => Text("Localization"), "pak-content" => Text("PakContent"),
        "loose-shader" => Text("LooseShader"), "pak-shader" => Text("PakShader"), _ => kind
    };
}
