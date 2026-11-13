using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModSubscriptionsPage
{
    private ScrollViewer? galleryScroll;
    private StackPanel? galleryHost, galleryMessages;
    private Button? galleryPrevious, galleryNext;
    private readonly Dictionary<(ModImage Image, ModImageReference? Reference), Control> galleryTiles = [];

    private string ReadingKey => session?.Article?.AbsoluteUri ?? "overview";

    private void CreateGallery()
    {
        if (galleryScroll is not null) galleryScroll.Content = null;
        galleryTiles.Clear(); images.Children.Clear(); images.Orientation = Orientation.Horizontal;
        images.Spacing = 12;
        galleryScroll = new ScrollViewer { Name = "subscriptionGallery", Content = images,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        galleryPrevious = Action("‹", () => { MoveGallery(-1); return Task.CompletedTask; }, independent: true);
        galleryNext = Action("›", () => { MoveGallery(1); return Task.CompletedTask; }, independent: true);
        galleryPrevious.Name = "subscriptionGalleryPrevious"; galleryNext.Name = "subscriptionGalleryNext";
        ToolTip.SetTip(galleryPrevious, T("GalleryPrevious")); ToolTip.SetTip(galleryNext, T("GalleryNext"));
        Avalonia.Automation.AutomationProperties.SetName(galleryPrevious, T("GalleryPrevious"));
        Avalonia.Automation.AutomationProperties.SetName(galleryNext, T("GalleryNext"));
        var heading = Row(); heading.Children.Add(Text(T("Gallery"), 16)); heading.Children.Add(galleryPrevious); heading.Children.Add(galleryNext);
        galleryMessages = Column(); galleryHost = Column();
        galleryHost.Children.Add(heading); galleryHost.Children.Add(galleryScroll); galleryHost.Children.Add(galleryMessages);
        galleryScroll.ScrollChanged += (_, _) => UpdateGalleryButtons();
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
        var entries = new List<(ModImage Image, ModImageReference? Reference)>();
        bool Add(ModImage image, ModImageReference? reference = null)
        {
            if (known.Contains(image.ImageUrl) || known.Contains(image.ThumbnailUrl ?? image.ImageUrl)) return false;
            known.Add(image.ImageUrl); known.Add(image.ThumbnailUrl ?? image.ImageUrl); entries.Add((image, reference)); return true;
        }
        foreach (var image in media) Add(image);
        if (overview)
            foreach (var reference in current.ImagePages.Values.SelectMany(state => state.Result?.Content?.Items ?? []).DistinctBy(item => item.PageUrl))
                Add(new() { Id = reference.Id, ImageUrl = reference.ThumbnailUrl, ThumbnailUrl = reference.ThumbnailUrl, Caption = reference.Caption }, reference);

        var wanted = entries.ToHashSet();
        foreach (var key in galleryTiles.Keys.Where(key => !wanted.Contains(key)).ToArray())
        { images.Children.Remove(galleryTiles[key]); galleryTiles.Remove(key); }
        for (int i = 0; i < entries.Count; i++)
        {
            var key = entries[i];
            if (!galleryTiles.TryGetValue(key, out var tile))
            {
                tile = InlineImage(key.Image, key.Reference); tile.Width = 240;
                galleryTiles.Add(key, tile); images.Children.Insert(i, tile);
            }
            else if (images.Children.IndexOf(tile) != i) images.Children.Move(images.Children.IndexOf(tile), i);
        }
        galleryMessages.Children.Clear();
        foreach (var extra in images.Children.Where(child => !galleryTiles.ContainsValue(child)).ToArray()) images.Children.Remove(extra);
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
