using System.Text;

namespace GOHShaderModdingSupportLauncher;

// Recognizes a Mod descriptor and its display fields, not the game's complete command schema.
// Other declaration blocks are skipped structurally; their commands are never evaluated.
internal static class ModMetadataReader
{
    internal sealed record Metadata(string? Name, bool Valid, string? Description = null);
    private enum Kind { Open, Close, Word, String, End }
    private sealed record Token(Kind Kind, string Text);

    internal static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            throw new DecoderFallbackException();
        Encoding encoding = new UTF8Encoding(false, true);
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, true, true); bytes = bytes[2..]; }
        else if (bytes.StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, true, true); bytes = bytes[2..]; }
        else if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        string text = encoding.GetString(bytes);
        if (text.Contains('\0')) throw new DecoderFallbackException();
        return text;
    }

    internal static Metadata Parse(string text, bool readDescription = false)
    {
        var parser = new Parser(text, readDescription);
        try { return parser.Read(); }
        catch (FormatException) { return new(parser.Name, false); }
    }

    private sealed class Parser(string text, bool readDescription)
    {
        private int position;
        internal string? Name;
        private int names;
        private string? description;

        internal Metadata Read()
        {
            bool mod = false;
            while (true)
            {
                var next = Next();
                if (next.Kind == Kind.End) break;
                if (next.Kind != Kind.Open) throw new FormatException();
                var declaration = Expect(Kind.Word);
                if (declaration.Text.Equals("mod", StringComparison.OrdinalIgnoreCase))
                {
                    if (mod) throw new FormatException();
                    mod = true;
                    ReadBody(0);
                }
                else
                {
                    // Preserve a legacy display name, but do not impose a field schema on
                    // unrelated declarations or let them overwrite the real Mod name.
                    if (!mod && declaration.Text.Equals("name", StringComparison.OrdinalIgnoreCase))
                    {
                        int start = position;
                        var value = Next();
                        if (value.Kind == Kind.String) Name = value.Text;
                        position = start;
                    }
                    SkipBody(1);
                }
            }
            return new(Name, mod && names == 1 && !string.IsNullOrWhiteSpace(Name) && !Name.Any(char.IsControl), description);
        }

        private void ReadBody(int depth)
        {
            if (depth > 64) throw new FormatException();
            while (true)
            {
                var next = Next();
                if (next.Kind == Kind.Close) return;
                if (next.Kind == Kind.End) throw new FormatException();
                if (next.Kind != Kind.Open)
                {
                    if (depth == 0) throw new FormatException();
                    continue;
                }
                var field = Expect(Kind.Word);
                if (depth == 0 && field.Text.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    Name = Expect(Kind.String).Text;
                    names++;
                    Expect(Kind.Close);
                }
                else
                {
                    if (readDescription && depth == 0 && description is null && field.Text.Equals("desc", StringComparison.OrdinalIgnoreCase))
                    {
                        int start = position;
                        var value = Next();
                        if (value.Kind == Kind.String && Next().Kind == Kind.Close) { description = value.Text; continue; }
                        position = start;
                    }
                    SkipBody(depth + 1);
                }
            }
        }

        private void SkipBody(int depth)
        {
            if (depth > 64) throw new FormatException();
            while (true)
            {
                var next = Next();
                if (next.Kind == Kind.Close) return;
                if (next.Kind == Kind.End) throw new FormatException();
                if (next.Kind == Kind.Open) SkipBody(depth + 1);
            }
        }

        private Token Expect(Kind kind)
        {
            var token = Next();
            if (token.Kind != kind) throw new FormatException();
            return token;
        }

        private Token Next()
        {
            while (position < text.Length)
            {
                char c = text[position];
                if (char.IsWhiteSpace(c) || c == '\uFEFF') { position++; continue; }
                if (c == ';' || (c == '/' && position + 1 < text.Length && text[position + 1] == '/'))
                {
                    while (position < text.Length && text[position] != '\n') position++;
                    continue;
                }
                if (c == '/' && position + 1 < text.Length && text[position + 1] == '*')
                {
                    int end = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    if (end < 0) throw new FormatException();
                    position = end + 2;
                    continue;
                }
                position++;
                if (c == '{') return new(Kind.Open, "{");
                if (c == '}') return new(Kind.Close, "}");
                if (c == '"')
                {
                    var value = new StringBuilder();
                    while (position < text.Length)
                    {
                        c = text[position++];
                        if (c == '"') return new(Kind.String, value.ToString());
                        if (c == '\\' && position < text.Length && text[position] is '"' or '\\') c = text[position++];
                        value.Append(c);
                    }
                    throw new FormatException();
                }
                int start = position - 1;
                while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"' or ';'))
                    position++;
                return new(Kind.Word, text[start..position]);
            }
            return new(Kind.End, "");
        }
    }
}
