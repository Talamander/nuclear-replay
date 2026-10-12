using System.Text;

namespace NuclearReplay.Core.Game;

/// <summary>
/// BepInEx loads through a proxy winhttp.dll in the game folder. Wine only uses it when winhttp is
/// overridden to "native,builtin". Most guides put WINEDLLOVERRIDES="winhttp=n,b" %command% in the
/// Steam launch options; writing the override into the game's own Proton prefix does the same without
/// asking the player to edit launch options.
/// </summary>
public static class ProtonPrefix
{
    private const string Section = @"[Software\\Wine\\DllOverrides]";

    public static string UserReg(string compatDataDir) => Path.Combine(compatDataDir, "pfx", "user.reg");

    public static bool HasWinhttpOverride(string userRegText)
    {
        bool inSection = false;
        foreach (var raw in userRegText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('['))
            {
                inSection = line.StartsWith(Section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            if (line.StartsWith("\"winhttp\"=", StringComparison.OrdinalIgnoreCase))
                return line.Contains("native", StringComparison.OrdinalIgnoreCase) && !line.EndsWith("\"\"");
        }
        return false;
    }

    /// <summary>Returns the registry text with "winhttp"="native,builtin" present in the global DllOverrides key.</summary>
    public static string WithWinhttpOverride(string userRegText, long unixTime)
    {
        if (HasWinhttpOverride(userRegText)) return userRegText;
        var newline = userRegText.Contains("\r\n") ? "\r\n" : "\n";
        var lines = userRegText.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        int header = lines.FindIndex(l => l.StartsWith(Section, StringComparison.OrdinalIgnoreCase));
        const string entry = "\"winhttp\"=\"native,builtin\"";
        if (header < 0)
        {
            var sb = new StringBuilder(userRegText);
            if (!userRegText.EndsWith('\n')) sb.Append(newline);
            sb.Append(newline).Append(Section).Append(' ').Append(unixTime).Append(newline).Append(entry).Append(newline);
            return sb.ToString();
        }
        // Remove an existing disabled entry ("winhttp"="") inside the section, then insert after the header and #time line.
        int end = lines.FindIndex(header + 1, l => l.StartsWith('['));
        if (end < 0) end = lines.Count;
        for (int i = end - 1; i > header; i--)
            if (lines[i].StartsWith("\"winhttp\"=", StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(i);
        int insert = header + 1;
        while (insert < lines.Count && lines[insert].StartsWith('#')) insert++;
        lines.Insert(insert, entry);
        return string.Join(newline, lines);
    }
}
