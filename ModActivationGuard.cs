namespace GOHShaderModdingSupportLauncher;

internal sealed class ModActivationRejectedException(IReadOnlyList<Mod> mods) : Exception(MessageFor(mods))
{
    internal IReadOnlyList<Mod> Mods { get; } = mods;
    private static string MessageFor(IReadOnlyList<Mod> mods) => ModInspectionPresentation.Text("ActivationBlocked") +
        (mods.Any(mod => !mod.IsInspectionPending && mod.Inspection?.ExternalPrograms.Detected == true)
            ? "\n" + ModInspectionPresentation.Text("DllManualInstall") : "");
}

internal static class ModActivationGuard
{
    internal static void RequireRegular(IEnumerable<Mod> mods)
    {
        var rejected = mods.Where(mod => !mod.CanActivate).ToArray();
        if (rejected.Length > 0) throw new ModActivationRejectedException(rejected);
    }
}
