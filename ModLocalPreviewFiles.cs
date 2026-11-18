using Avalonia.Media.Imaging;
using GOHShaderModdingSupportLauncher.ModSubscriptions;

namespace GOHShaderModdingSupportLauncher;

internal sealed class ModLocalPreviewFiles
{
    private const int MaxTextBytes = 1024 * 1024;
    private const int MaxImageBytes = 20 * 1024 * 1024;
    private readonly Dictionary<string, FileInfo> imagesByName;

    internal FileInfo[] Images { get; }
    internal FileInfo[] Documents { get; }

    private ModLocalPreviewFiles(FileInfo[] images, FileInfo[] documents)
    {
        Images = images;
        Documents = documents;
        imagesByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images) imagesByName.TryAdd(image.Name, image);
    }

    internal static ModLocalPreviewFiles Read(string path, CancellationToken token)
    {
        var root = new DirectoryInfo(path);
        if (!ModInspectionService.CanReadDirectory(root)) throw new DirectoryNotFoundException(path);
        var images = new List<FileInfo>();
        var documents = new List<FileInfo>();
        foreach (var file in root.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            switch (file.Extension.ToLowerInvariant())
            {
                case ".png": case ".jpg": case ".jpeg": case ".webp": case ".bmp":
                    images.Add(file);
                    break;
                case ".md": case ".markdown": case ".txt": case ".log": case ".ini": case ".cfg":
                case ".csv": case ".json": case ".xml": case ".yaml": case ".yml": case ".toml":
                    documents.Add(file);
                    break;
                case ".info" when IsDescriptor(file):
                    documents.Add(file);
                    break;
            }
        }

        return new(images.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            documents.OrderBy(DocumentPriority).ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool IsDescriptor(FileInfo file) => file.Name.Equals("mod.info", StringComparison.OrdinalIgnoreCase);

    private static int DocumentPriority(FileInfo file)
    {
        if (IsDescriptor(file)) return 0;
        return Path.GetFileNameWithoutExtension(file.Name).Equals("README", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    internal sealed record Document(string? Text, ModReadingDocument? Markdown, bool MissingDescription = false);

    internal Document ReadDocument(FileInfo file, CancellationToken token)
    {
        string text = ModMetadataReader.Decode(ReadBytes(file, MaxTextBytes, token));
        if (IsDescriptor(file))
        {
            var description = ModMetadataReader.Parse(text, readDescription: true).Description;
            return new(description, null, string.IsNullOrWhiteSpace(description));
        }
        if (file.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            file.Extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
            return new(null, ModDocumentParser.LocalMarkdown(text, new Uri(file.FullName), ResolveImage));
        return new(text, null);
    }

    private ModImage? ResolveImage(string reference, string caption)
    {
        string name = Uri.UnescapeDataString(reference);
        if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        if (!imagesByName.TryGetValue(name, out var file)) return null;
        return new() { Id = file.Name, ImageUrl = new Uri(file.FullName), Caption = caption };
    }

    internal Bitmap ReadInlineImage(string name, CancellationToken token) => ReadImage(imagesByName[name], 640, 480, token);

    internal static Bitmap ReadImage(FileInfo file, int width, int height, CancellationToken token) =>
        ModImageDecoder.DecodeRaster(ReadBytes(file, MaxImageBytes, token), width, height, token, out _, out _);

    internal sealed class PreviewTooLargeException : IOException;

    private static byte[] ReadBytes(FileInfo file, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ModInspectionService.CanReadDirectory(file.Directory!) ||
            (File.GetAttributes(file.FullName) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The preview file is outside the Mod root.");
        using var stream = file.OpenRead();
        if (stream.Length > limit) throw new PreviewTooLargeException();
        using var data = new MemoryStream((int)stream.Length);
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if (data.Length + count > limit) throw new PreviewTooLargeException();
            data.Write(buffer, 0, count);
        }
        return data.ToArray();
    }
}
