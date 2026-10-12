namespace NuclearReplay.Core.Tests;

/// <summary>A unique temporary folder, removed after the test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nc-tests", Guid.NewGuid().ToString("N")[..10]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public string Write(string relative, string content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string Write(string relative, byte[] content)
    {
        var full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    /// <summary>A folder that passes GameInfo.LooksLikeGameDir, laid out like a Steam library.</summary>
    public string FakeGame(string library = "lib")
    {
        var game = Combine(library, "steamapps", "common", "Nuclear Option");
        Directory.CreateDirectory(System.IO.Path.Combine(game, "NuclearOption_Data"));
        File.WriteAllText(System.IO.Path.Combine(game, "NuclearOption.exe"), "fake exe");
        return game;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (Exception) { }
    }
}
