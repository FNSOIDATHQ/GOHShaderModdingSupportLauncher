using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private ScrollViewer? galleryScroll;
    private StackPanel? galleryHost, galleryMessages;
    private Button? galleryPrevious, galleryNext, galleryEnlarge;
    private sealed record GalleryTile(ModImage Image, ModImageReference? Reference, StackPanel Panel);
    private readonly Dictionary<string, GalleryTile> galleryTiles = [];

    private string ReadingKey => session?.Article?.AbsoluteUri ?? "overview";

    private void CreateGallery()
    {
        if (galleryScroll is not null) galleryScroll.Content = null;
        galleryTiles.Clear(); images.Children.Clear(); images.Orientation = Orientation.Horizontal;
        images.Spacing = 4;
        galleryScroll = new ScrollViewer { Name = "subscriptionGallery", Content = images,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        galleryPrevious = Action("‹", () => { MoveGallery(-1); return Task.CompletedTask; }, independent: true);
        galleryNext = Action("›", () => { MoveGallery(1); return Task.CompletedTask; }, independent: true);
        galleryPrevious.Name = "subscriptionGalleryPrevious"; galleryNext.Name = "subscriptionGalleryNext";
        galleryEnlarge = Action(T("ViewImage"), ExpandSelectedGalleryAsync, independent: true);
        galleryEnlarge.Name = "subscriptionGalleryEnlarge"; galleryEnlarge.IsEnabled = false;
        ToolTip.SetTip(galleryPrevious, T("GalleryPrevious")); ToolTip.SetTip(galleryNext, T("GalleryNext"));
        Avalonia.Automation.AutomationProperties.SetName(galleryPrevious, T("GalleryPrevious"));
        Avalonia.Automation.AutomationProperties.SetName(galleryNext, T("GalleryNext"));
        var heading = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 4 };
        heading.Children.Add(Text(T("Gallery"), 16)); heading.Children.Add(galleryEnlarge);
        heading.Children.Add(galleryPrevious); heading.Children.Add(galleryNext);
        ToolTip.SetTip(heading, T("GalleryKeyboardHint"));
        galleryMessages = Column(); galleryHost = Column();
        galleryHost.Children.Add(heading); galleryHost.Children.Add(galleryScroll); galleryHost.Children.Add(galleryMessages);
        galleryScroll.ScrollChanged += (_, _) => UpdateGalleryButtons();
    }

    private static string GalleryKey(ModImage image, ModImageReference? reference = null) =>
        image.Id.Length > 0 ? "id:" + image.Id : reference?.PageUrl.AbsoluteUri ?? image.PageUrl?.AbsoluteUri ?? image.ImageUrl.AbsoluteUri;

    internal static Size GalleryImageSize(Size pixels)
    {
        double scale = Math.Min(260 / pixels.Height, 520 / pixels.Width);
        return new Size(pixels.Width * scale, pixels.Height * scale);
    }

    private void UpdateGallerySelection()
    {
        string? key = session?.GallerySelections.GetValueOrDefault(ReadingKey);
        foreach (var (candidate, tile) in galleryTiles)
            ((Border)tile.Panel.Children[0]).BorderBrush = candidate == key ? ReadingBrush("Link") : Brushes.Transparent;
        if (galleryEnlarge is not null) galleryEnlarge.IsEnabled = key is not null && galleryTiles.ContainsKey(key);
        if (key is not null && session?.Context?.GalleryKey == key && galleryTiles.TryGetValue(key, out var selectedTile))
            SetReadingContext(new("image", selectedTile.Reference?.PageUrl ?? selectedTile.Image.PageUrl ?? selectedTile.Image.ImageUrl, key));
    }

    private void SelectGalleryImage(string key, bool focus = false)
    {
        if (session is not { } current || !galleryTiles.TryGetValue(key, out var tile)) return;
        CancelLinkNavigation(); pendingImage = null;
        current.GallerySelections[ReadingKey] = key;
        SetReadingContext(new("image", tile.Reference?.PageUrl ?? tile.Image.PageUrl ?? tile.Image.ImageUrl, key));
        UpdateGallerySelection();
        if (focus) { tile.Panel.Focus(NavigationMethod.Directional); tile.Panel.BringIntoView(); }
    }

    private async Task GalleryKeyDownAsync(string key, KeyEventArgs args)
    {
        if (args.Key is Key.Enter or Key.Space)
        {
            args.Handled = true;
            SelectGalleryImage(key);
            await ExecuteAsync(ExpandSelectedGalleryAsync);
        }
        else if (args.Key is Key.Left or Key.Right or Key.Home or Key.End)
        {
            args.Handled = true;
            var keys = images.Children.OfType<StackPanel>().Select(panel => (string)panel.Tag!).ToArray();
            int index = Array.IndexOf(keys, key);
            int next = args.Key == Key.Home ? 0 : args.Key == Key.End ? keys.Length - 1 :
                Math.Clamp(index + (args.Key == Key.Left ? -1 : 1), 0, keys.Length - 1);
            SelectGalleryImage(keys[next], focus: true);
        }
    }

    private async Task ExpandSelectedGalleryAsync()
    {
        if (session is not { } current || !current.GallerySelections.TryGetValue(ReadingKey, out string? key) ||
            !galleryTiles.TryGetValue(key, out var tile)) return;
        SelectGalleryImage(key);
        if (tile.Reference is null) { await ShowImageAsync(tile.Image, DetailToken); return; }
        pendingImage = new(current, tile.Reference, details.SelectedIndex, current.Article, current.File);
        await RetryPendingImageAsync();
    }

    private void MoveGallery(int direction)
    {
        if (galleryScroll is not { } scroll) return;
        scroll.Offset = new Vector(Math.Clamp(scroll.Offset.X + direction * Math.Max(240, scroll.Viewport.Width - 40),
            0, Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width)), 0);
    }

    private void UpdateGalleryButtons()
    {
        if (galleryScroll is not { } scroll) return;
        galleryPrevious!.IsEnabled = scroll.Offset.X > 1;
        galleryNext!.IsEnabled = scroll.Offset.X + scroll.Viewport.Width < scroll.Extent.Width - 1;
    }

    private void RenderGallery(ReadingSession current, ModReadingDocument document, ModImage[] media, bool overview)
    {
        if (galleryHost is null || galleryMessages is null) return;
        var embedded = ModDocumentParser.Images(document).SelectMany(image => new[] { image.ImageUrl, image.ThumbnailUrl ?? image.ImageUrl }).ToHashSet();
        var known = new HashSet<Uri>(embedded);
        var keys = new HashSet<string>();
        var entries = new List<(ModImage Image, ModImageReference? Reference)>();
        bool Add(ModImage image, ModImageReference? reference = null)
        {
            if (known.Contains(image.ImageUrl) || known.Contains(image.ThumbnailUrl ?? image.ImageUrl)) return false;
            if (!keys.Add(GalleryKey(image, reference))) return false;
            known.Add(image.ImageUrl); known.Add(image.ThumbnailUrl ?? image.ImageUrl); entries.Add((image, reference)); return true;
        }
        foreach (var image in media) Add(image);
        if (overview)
            foreach (var reference in current.ImagePages.Values.SelectMany(state => state.Result?.Content?.Items ?? []).DistinctBy(item => item.PageUrl))
                Add(new() { Id = reference.Id, ImageUrl = reference.ThumbnailUrl, ThumbnailUrl = reference.ThumbnailUrl, Caption = reference.Caption }, reference);

        var wanted = entries.Select(entry => GalleryKey(entry.Image, entry.Reference)).ToHashSet();
        foreach (var key in galleryTiles.Keys.Where(key => !wanted.Contains(key)).ToArray())
        { images.Children.Remove(galleryTiles[key].Panel); galleryTiles.Remove(key); }
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            string key = GalleryKey(entry.Image, entry.Reference);
            if (galleryTiles.TryGetValue(key, out var old) && (old.Image != entry.Image || old.Reference != entry.Reference))
            { images.Children.Remove(old.Panel); galleryTiles.Remove(key); }
            if (!galleryTiles.TryGetValue(key, out var tile))
            {
                var panel = InlineImage(entry.Image, entry.Reference);
                galleryTiles.Add(key, tile = new(entry.Image, entry.Reference, panel)); images.Children.Insert(i, panel);
            }
            else if (images.Children.IndexOf(tile.Panel) != i) images.Children.Move(images.Children.IndexOf(tile.Panel), i);
        }
        if (!current.GallerySelections.TryGetValue(ReadingKey, out string? selectedKey) ||
            !wanted.Contains(selectedKey) && !refreshingNavigation && !current.Overview.Loading && !current.ImagePages.Values.Any(state => state.Loading))
        {
            if (entries.Count > 0) current.GallerySelections[ReadingKey] = GalleryKey(entries[0].Image, entries[0].Reference);
            else current.GallerySelections.Remove(ReadingKey);
            if (selectedKey is not null && current.Context?.GalleryKey == selectedKey)
                SetReadingContext(entries.Count > 0 ? new("image", entries[0].Reference?.PageUrl ?? entries[0].Image.PageUrl ?? entries[0].Image.ImageUrl,
                    GalleryKey(entries[0].Image, entries[0].Reference)) : DescriptionContext());
        }
        UpdateGallerySelection();
        galleryMessages.Children.Clear();
        foreach (var extra in images.Children.Where(child => !galleryTiles.Values.Any(tile => tile.Panel == child)).ToArray()) images.Children.Remove(extra);
        if (overview)
        {
            var nextPage = current.ImagePages.Values.LastOrDefault()?.Result?.Content?.NextPage;
            if (nextPage is not null && !current.ImagePages.ContainsKey(nextPage) && current.ImagePages.Count < 100)
            {
                var more = Action(T("MoreImages"), () => LoadImagesAsync(nextPage, DetailToken), independent: true);
                more.Name = "subscriptionGalleryMore"; more.Width = 130; more.VerticalAlignment = VerticalAlignment.Center; images.Children.Add(more);
            }
            else if (nextPage is not null && current.ImagePages.Count >= 100) galleryMessages.Children.Add(Text(T("PaginationLimit"), 12));
        }
        galleryHost.IsVisible = entries.Count > 0 || galleryMessages.Children.Count > 0 || images.Children.Count > 0;
        UpdateGalleryButtons();
    }
}
