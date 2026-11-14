using Avalonia.Controls;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    internal Mod? InstalledModInspection(string game, ModInstallation installation)
    {
        if (!string.Equals(game, main.SubscriptionGameDirectory, SubscriptionPaths.Comparison) ||
            installation.State != InstallationState.Installed) return null;
        try
        {
            string path = SubscriptionPaths.InstallationDirectory(game, installation.DirectoryPath);
            var mod = main.FindInspectedLocalMod(path);
            return mod?.Inspection?.Source == ModSourceKind.Local ? mod : null;
        }
        catch (ArgumentException) { return null; }
    }

    private void AddLocalInspection(StackPanel panel, GameModSubscription? subscription, Guid? downloadId)
    {
        var installations = subscription?.Installations.Where(item => item.DownloadId == downloadId)
            .DistinctBy(item => item.DirectoryPath, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        if (installations.Length == 0)
        {
            panel.Children.Add(Text(ModInspectionPresentation.Format("LocalInspection", ModInspectionPresentation.Text("NotInspected")), 12));
            return;
        }
        foreach (var installation in installations)
        {
            var mod = InstalledModInspection(subscription!.GameDirectory, installation);
            panel.Children.Add(Text(ModInspectionPresentation.Format("LocalInspection", Path.GetFileName(installation.DirectoryPath)), 12));
            if (mod is null)
                panel.Children.Add(Text(ModInspectionPresentation.Text("NotInspected"), 12));
            else
                panel.Children.Add(new ModFeaturesView { Mod = mod });
        }
    }
}
