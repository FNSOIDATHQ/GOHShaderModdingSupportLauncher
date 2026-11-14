using System.Text;

namespace GOHShaderModdingSupportLauncher;

// GEM's descriptor syntax: brace blocks, quoted strings and semicolon comments.
// Unknown fields are retained syntactically; they do not establish loadable content.
internal static class ModMetadataReader
{
    internal sealed record Metadata(string? Name, bool Valid);
    private enum Kind { Open, Close, Word, String, End }
    private sealed record Token(Kind Kind, string Text);

    internal static Metadata Parse(string text)
    {
        var parser = new Parser(text);
        try { return parser.Read(); }
        catch (FormatException) { return new(parser.Name, false); }
    }

    private sealed class Parser(string text)
    {
        private int position;
        internal string? Name;
        private int names;

        internal Metadata Read()
        {
            Expect(Kind.Open);
            var root = Expect(Kind.Word);
            bool mod = root.Text.Equals("mod", StringComparison.OrdinalIgnoreCase);
            // Keep display names from old/bad descriptors without granting regular status.
            if (!mod && root.Text.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                Name = Expect(Kind.String).Text;
                Expect(Kind.Close); Expect(Kind.End);
                return new(Name, false);
            }
            if (!mod) throw new FormatException();
            ReadBody(0);
            Expect(Kind.End);
            return new(Name, names == 1 && !string.IsNullOrWhiteSpace(Name) && !Name.Any(char.IsControl));
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
                else ReadBody(depth + 1);
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
