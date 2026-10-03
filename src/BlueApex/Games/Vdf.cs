namespace BlueApex.Games;

/// <summary>
/// Reads Valve's text KeyValues format (.vdf / .acf): nested blocks of
/// <c>"key" "value"</c> and <c>"key" { ... }</c>. Just enough for Steam's
/// library files; comments and the rare unquoted token are tolerated.
/// </summary>
internal sealed class Vdf
{
    /// <summary>Child values: either a string or another <see cref="Vdf"/> block.</summary>
    public Dictionary<string, object> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string key] => Items.GetValueOrDefault(key) as string;
    public Vdf? Block(string key) => Items.GetValueOrDefault(key) as Vdf;
    public IEnumerable<Vdf> Blocks => Items.Values.OfType<Vdf>();

    public static Vdf Parse(string text)
    {
        var pos = 0;
        var root = new Vdf();
        ParseInto(root, text, ref pos);
        return root;
    }

    private static void ParseInto(Vdf block, string text, ref int pos)
    {
        while (true)
        {
            var key = NextToken(text, ref pos);
            if (key == null || key == "}") return;
            var value = NextToken(text, ref pos);
            if (value == null) return;
            if (value == "{")
            {
                var child = new Vdf();
                ParseInto(child, text, ref pos);
                block.Items[key] = child;
            }
            else
            {
                block.Items[key] = value;
            }
        }
    }

    // A quoted string (with \" and \\ escapes), a brace, or a bare word.
    private static string? NextToken(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            var c = text[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }
            break;
        }
        if (pos >= text.Length) return null;
        if (text[pos] == '{' || text[pos] == '}') return text[pos++].ToString();
        if (text[pos] == '"')
        {
            pos++;
            var sb = new System.Text.StringBuilder();
            while (pos < text.Length && text[pos] != '"')
            {
                if (text[pos] == '\\' && pos + 1 < text.Length) pos++;
                sb.Append(text[pos++]);
            }
            pos++; // closing quote
            return sb.ToString();
        }
        var start = pos;
        while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] != '{' && text[pos] != '}') pos++;
        return text[start..pos];
    }
}
