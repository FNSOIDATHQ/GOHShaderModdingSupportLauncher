using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GOHShaderModdingSupportLauncher;

// The legend uses the same badge factory as the actual Mod rows.
public sealed class ModFeatureHintView : UserControl
{
    internal static readonly string[] FeatureKeys = ["Regular", "Dll", "Shader", "Other"];
    private readonly Grid legend = new()
    { ColumnDefinitions = new("Auto,*"), RowDefinitions = new("Auto,Auto,Auto,Auto"), ColumnSpacing = 4, RowSpacing = 8 };

    public ModFeatureHintView()
    {
        MaxWidth = 420;
        Content = legend;
        Render();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LocalizedStrings.Instance.PropertyChanged += LanguageChanged;
        Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LocalizedStrings.Instance.PropertyChanged -= LanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(LocalizedStrings.MI_FeatureHint)) Render(); }

    private void Render()
    {
        legend.Children.Clear();
        for (int row = 0; row < FeatureKeys.Length; row++)
        {
            string key = FeatureKeys[row];
            var badge = ModFeatureBadge.Create(key);
            Grid.SetRow(badge, row); legend.Children.Add(badge);
            var explanation = new TextBlock
            {
                Text = ModInspectionPresentation.Text("FeatureHint" + key),
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(explanation, 1); Grid.SetRow(explanation, row); legend.Children.Add(explanation);
        }
        AutomationProperties.SetName(this, LocalizedStrings.Instance.MI_FeatureHint);
    }
}
