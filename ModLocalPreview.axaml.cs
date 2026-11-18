using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

public sealed partial class ModLocalPreview : UserControl
{
    private string? path;
    private bool hasSelection;
    private bool active;
    private ModLocalPreviewFiles? files;
    private int imageIndex;
    private int documentIndex;
    private TextBlock? documentMessage;
    private CancellationTokenSource? listing, picture, document;
    private readonly List<Bitmap> documentImages = [];

    public ModLocalPreview() => InitializeComponent();

    internal event Action? CollapseRequested;

    internal void SetHeight(double height) => previewGallery.Height = Math.Clamp(height * 0.32, 130, 265);
    internal void FocusCollapse() => collapseModPreview.Focus();

    internal void SetMod(Mod? mod, bool refresh = false)
    {
        previewModName.Text = mod?.DisplayName;
        previewModName.IsVisible = mod is not null;
        ToolTip.SetTip(previewModName, mod?.DisplayName);
        string? directory = mod is { IsMissingReference: false } && !string.IsNullOrWhiteSpace(mod.path) ? mod.path : null;
        if (!refresh && hasSelection == (mod is not null) && string.Equals(path, directory, StringComparison.OrdinalIgnoreCase)) return;
        path = directory;
        hasSelection = mod is not null;
        StopReading();
        files = null;
        imageIndex = 0;
        documentIndex = 0;
        previewGallery.IsVisible = previewDocuments.IsVisible = false;
        if (active) ReadDirectory();
    }

    internal void SetActive(bool value)
    {
        if (active == value) return;
        active = value;
        if (active)
        {
            LocalizedStrings.Instance.PropertyChanged += OnCultureChanged;
            UpdateMessages();
            if (files is null) ReadDirectory();
            else { ReadImage(); ReadDocument(); }
        }
        else
        {
            LocalizedStrings.Instance.PropertyChanged -= OnCultureChanged;
            StopReading();
        }
    }

    private void Collapse_Click(object? sender, RoutedEventArgs e) => CollapseRequested?.Invoke();

    private void ReadDirectory()
    {
        if (path is not { } directory)
        {
            Message(previewMessage, hasSelection ? "MP_FolderUnavailable" : "MP_SelectOne");
            return;
        }
        Message(previewMessage, "MP_Loading");
        var token = Restart(ref listing);
        _ = LoadAsync(token, ct => ModLocalPreviewFiles.Read(directory, ct), ShowDirectory,
            _ => Message(previewMessage, "MP_FolderUnavailable"));
    }

    private void ShowDirectory(ModLocalPreviewFiles catalog)
    {
        files = catalog;
        previewGallery.IsVisible = catalog.Images.Length > 0;
        previewDocuments.IsVisible = catalog.Documents.Length > 0;
        Message(previewMessage, catalog.Documents.Length != 0 ? null :
            catalog.Images.Length == 0 ? "MP_Empty" : "MP_NoText");
        ReadImage();
        ReadDocument();
    }

    private void PreviousImage_Click(object? sender, RoutedEventArgs e) => MoveImage(-1);
    private void NextImage_Click(object? sender, RoutedEventArgs e) => MoveImage(1);

    private void MoveImage(int step)
    {
        int next = imageIndex + step;
        if (!active || files is null || next < 0 || next >= files.Images.Length) return;
        imageIndex = next;
        ReadImage();
    }

    private void ReadImage()
    {
        if (files is not { Images.Length: > 0 } catalog) return;
        var token = Restart(ref picture);
        var file = catalog.Images[imageIndex];
        ClearPicture();
        AutomationProperties.SetName(previewImage, file.Name);
        ToolTip.SetTip(previewImage, file.Name);
        int count = catalog.Images.Length;
        UpdateNavigationButtons(previousPreviewImage, nextPreviewImage, imageIndex, count);
        previewImagePosition.Text = count == 1 ? file.Name : $"{imageIndex + 1} / {count}  ·  {file.Name}";
        ToolTip.SetTip(previewImagePosition, file.Name);
        Message(previewImageMessage, "MP_Loading");
        _ = LoadAsync(token, ct => ModLocalPreviewFiles.ReadImage(file, 1280, 960, ct), bitmap =>
        {
            previewImage.Source = bitmap;
            Message(previewImageMessage, null);
        }, key => Message(previewImageMessage, key));
    }

    private void PreviousDocument_Click(object? sender, RoutedEventArgs e) => MoveDocument(-1);
    private void NextDocument_Click(object? sender, RoutedEventArgs e) => MoveDocument(1);

    private void MoveDocument(int step)
    {
        int next = documentIndex + step;
        if (!active || files is null || next < 0 || next >= files.Documents.Length) return;
        documentIndex = next;
        ReadDocument();
    }

    private static void UpdateNavigationButtons(Button previous, Button next, int index, int count)
    {
        previous.IsVisible = next.IsVisible = count > 1;
        previous.IsEnabled = index > 0;
        next.IsEnabled = index + 1 < count;
    }

    private void ReadDocument()
    {
        ClearDocument();
        if (files is not { Documents.Length: > 0 } catalog) return;
        var file = catalog.Documents[documentIndex];
        int count = catalog.Documents.Length;
        UpdateNavigationButtons(previousPreviewDocument, nextPreviewDocument, documentIndex, count);
        previewDocumentPosition.Text = file.Name;
        ToolTip.SetTip(previewDocumentPosition, file.Name);
        previewDocumentCount.IsVisible = count > 1;
        previewDocumentCount.Text = $"{documentIndex + 1} / {count}";
        var token = Restart(ref document);
        ShowMessage("MP_Loading");
        _ = LoadAsync(token, ct => catalog.ReadDocument(file, ct), result =>
        {
            documentMessage = null;
            if (result.Markdown is { Blocks.Length: > 0 } markdown)
                ShowMarkdown(catalog, markdown, token);
            else if (result.Text is { Length: > 0 } text && !result.MissingDescription)
                previewDocumentScroll.Content = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            else ShowMessage(result.MissingDescription ? "MP_NoDescription" : "MP_EmptyText");
        }, ShowMessage);

        void ShowMessage(string key)
        {
            documentMessage = new TextBlock { TextWrapping = TextWrapping.Wrap };
            Message(documentMessage, key);
            previewDocumentScroll.Content = documentMessage;
        }
    }

    private void ShowMarkdown(ModLocalPreviewFiles catalog, ModReadingDocument markdown, CancellationToken token)
    {
        var images = new Dictionary<string, List<(Image Image, Control Caption)>>(StringComparer.Ordinal);
        var panel = new StackPanel { Spacing = 8 };
        foreach (var block in markdown.Blocks) panel.Children.Add(Render(block));
        previewDocumentScroll.Content = panel;
        _ = ReadImagesAsync();

        Control Render(ModContentBlock block)
        {
            if (block.Image is not { } reference) return ModDocumentBlockView.Render(block, Render);
            var image = new Image { IsVisible = false, MaxHeight = 220, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            AutomationProperties.SetName(image, reference.Caption.Length > 0 ? reference.Caption : reference.Id);
            var caption = new SelectableTextBlock { Text = reference.Caption.Length > 0 ? reference.Caption : reference.Id,
                TextWrapping = TextWrapping.Wrap };
            if (!images.TryGetValue(reference.Id, out var placements)) images.Add(reference.Id, placements = []);
            placements.Add((image, caption));
            return new Panel { Children = { image, caption } };
        }

        async Task ReadImagesAsync()
        {
            foreach (var (name, placements) in images)
            {
                if (token.IsCancellationRequested) break;
                await LoadAsync(token, ct => catalog.ReadInlineImage(name, ct), bitmap =>
                {
                    documentImages.Add(bitmap);
                    foreach (var (image, caption) in placements)
                    {
                        image.Source = bitmap;
                        image.IsVisible = true;
                        caption.IsVisible = false;
                    }
                }, _ => { });
            }
        }
    }

    private void StopReading()
    {
        Cancel(ref listing);
        Cancel(ref picture);
        ClearPicture();
        ClearDocument();
    }

    private void ClearPicture()
    {
        var bitmap = previewImage.Source as Bitmap;
        previewImage.Source = null;
        bitmap?.Dispose();
    }

    private void ClearDocument()
    {
        Cancel(ref document);
        previewDocumentScroll.Content = null;
        previewDocumentScroll.Offset = default;
        documentMessage = null;
        foreach (var bitmap in documentImages) bitmap.Dispose();
        documentImages.Clear();
    }

    private static CancellationToken Restart(ref CancellationTokenSource? source)
    {
        Cancel(ref source);
        source = new CancellationTokenSource();
        return source.Token;
    }

    private static void Cancel(ref CancellationTokenSource? source)
    {
        if (source is null) return;
        source.Cancel();
        source.Dispose();
        source = null;
    }

    private static async Task LoadAsync<T>(CancellationToken token, Func<CancellationToken, T> read, Action<T> show,
        Action<string> onError)
    {
        T result;
        try { result = await Task.Run(() => read(token), token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            FormatException or InvalidDataException or InvalidOperationException or NotSupportedException or ModSourceException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested)
                    onError(ex is ModLocalPreviewFiles.PreviewTooLargeException ? "MP_TooLarge" : "MP_ReadError");
            });
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!token.IsCancellationRequested) show(result);
            else if (result is IDisposable disposable) disposable.Dispose();
        });
    }

    private static void Message(TextBlock element, string? key)
    {
        element.Tag = key;
        element.IsVisible = key is not null;
        element.Text = key is null ? null : LocalizedStrings.Instance.Get(key);
    }

    private void OnCultureChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalizedStrings.MP_Title)) UpdateMessages();
    }

    private void UpdateMessages()
    {
        Message(previewMessage, previewMessage.Tag as string);
        Message(previewImageMessage, previewImageMessage.Tag as string);
        if (documentMessage is { } message) Message(message, message.Tag as string);
    }
}
