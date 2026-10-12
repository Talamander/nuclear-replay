using System.Text;

namespace NuclearReplay.Core.Steam;

/// <summary>A node of Valve's text KeyValues format (libraryfolders.vdf, appmanifest_*.acf, localconfig.vdf).</summary>
public sealed class VdfNode
{
    public string Key { get; }
    public string? Value { get; }
    public List<VdfNode> Children { get; } = new();

    public VdfNode(string key, string? value = null)
    {
        Key = key;
        Value = value;
    }

    public bool IsSection => Value == null;

    /// <summary>First child with this key (Steam keys are case-insensitive).</summary>
    public VdfNode? this[string key] => Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public string? GetString(string key) => this[key]?.Value;

    /// <summary>Follows a path of keys, e.g. Path("Software", "Valve", "Steam").</summary>
    public VdfNode? Path(params string[] keys)
    {
        VdfNode? node = this;
        foreach (var key in keys)
        {
            node = node?[key];
            if (node == null) return null;
        }
        return node;
    }
}

/// <summary>Parser for Valve's text KeyValues files. Tolerates comments, unquoted tokens, #include/#base lines and conditionals.</summary>
public static class Vdf
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode("");
        var stack = new Stack<VdfNode>();
        stack.Push(root);
        int i = 0;
        string? pendingKey = null;
        while (true)
        {
            var token = NextToken(text, ref i, out var quoted);
            if (token == null) break;
            if (!quoted && token == "{")
            {
                var section = new VdfNode(pendingKey ?? "");
                stack.Peek().Children.Add(section);
                stack.Push(section);
                pendingKey = null;
                continue;
            }
            if (!quoted && token == "}")
            {
                if (stack.Count > 1) stack.Pop();
                pendingKey = null;
                continue;
            }
            // Conditionals such as [$WIN32] follow a value; ignore them.
            if (!quoted && token.StartsWith('[') && token.EndsWith(']')) continue;
            if (pendingKey == null)
            {
                if (!quoted && (token == "#include" || token == "#base"))
                {
                    NextToken(text, ref i, out _);
                    continue;
                }
                pendingKey = token;
            }
            else
            {
                stack.Peek().Children.Add(new VdfNode(pendingKey, token));
                pendingKey = null;
            }
        }
        return root;
    }

    public static VdfNode ParseFile(string path) => Parse(File.ReadAllText(path));

    private static string? NextToken(string text, ref int i, out bool quoted)
    {
        quoted = false;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            break;
        }
        if (i >= text.Length) return null;
        if (text[i] == '{' || text[i] == '}') return text[i++].ToString();
        var sb = new StringBuilder();
        if (text[i] == '"')
        {
            quoted = true;
            i++;
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    char e = text[i + 1];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        default: sb.Append('\\').Append(e); break;
                    }
                    i += 2;
                    continue;
                }
                sb.Append(text[i++]);
            }
            i++; // closing quote (or end of text for a truncated file)
            return sb.ToString();
        }
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"')
            sb.Append(text[i++]);
        return sb.ToString();
    }
}
