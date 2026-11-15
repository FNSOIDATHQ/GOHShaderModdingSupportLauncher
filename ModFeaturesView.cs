using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GOHShaderModdingSupportLauncher;

// The same control renders both manager columns and explicitly associated local installations.
public sealed class ModFeaturesView : UserControl
{
    public static readonly StyledProperty<Mod?> ModProperty = AvaloniaProperty.Register<ModFeaturesView, Mod?>(nameof(Mod));
    public Mod? Mod { get => GetValue(ModProperty); set => SetValue(ModProperty, value); }

    private readonly WrapPanel badges = new()
    { Name = "modFeatureBadges", ItemSpacing = 4, LineSpacing = 3, Margin = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
    private bool listening;

    public ModFeaturesView()
    {
        Content = badges;
        Render();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ModProperty) return;
        if (listening && change.OldValue is Mod old) old.PropertyChanged -= ModChanged;
        if (listening && Mod is { } current) current.PropertyChanged += ModChanged;
        Render();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        listening = true;
        LocalizedStrings.Instance.PropertyChanged += LanguageChanged;
        if (Mod is { } mod) mod.PropertyChanged += ModChanged;
        Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizedStrings.Instance.PropertyChanged -= LanguageChanged;
        if (Mod is { } mod) mod.PropertyChanged -= ModChanged;
        listening = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(LocalizedStrings.MI_Features)) Render(); }
    private void ModChanged(object? sender, PropertyChangedEventArgs e) => Render();
    private void Render()
    {
        badges.Children.Clear();
        if (Mod?.IsInspectionPending == true)
        {
            AddBadge("Checking");
            AutomationProperties.SetName(this, Mod.DisplayName + ": " + ModInspectionPresentation.Text("Checking"));
            return;
        }
        var result = Mod?.Inspection;
        if (Mod?.IsMissingReference == true) AddBadge("Other");
        else if (result is null)
            badges.Children.Add(new TextBlock { Text = ModInspectionPresentation.Text("NotInspected"), FontSize = 11 });
        else
            foreach (string key in ModInspectionPresentation.FeatureKeys(result)) AddBadge(key);
        string name = (Mod?.DisplayName is { } title ? title + ": " : "") +
            (Mod?.IsMissingReference == true ? ModInspectionPresentation.Text("Other") : ModInspectionPresentation.Summary(result));
        AutomationProperties.SetName(this, name);
    }

    private void AddBadge(string key) => badges.Children.Add(ModFeatureBadge.Create(key));
}

internal static class ModFeatureBadge
{
    internal static Border Create(string key) => new()
    {
        CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1), VerticalAlignment = VerticalAlignment.Center,
        Background = Brush.Parse(key switch
        { "Regular" => "#25483E", "Dll" => "#574222", "Shader" => "#344565", _ => "#454A50" }),
        Child = new TextBlock { Text = ModInspectionPresentation.Text(key), Foreground = Brushes.White, FontSize = 11 }
    };
}
