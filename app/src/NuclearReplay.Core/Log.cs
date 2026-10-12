using System.Text;

namespace NuclearReplay.Core;

/// <summary>
/// Small rolling log file in the per-user data folder (<c>logs/nuclear-replay.log</c>).
/// When the file passes <see cref="MaxBytes"/> it becomes <c>.1.log</c> (then <c>.2.log</c>); older files are dropped.
/// Nothing is written until <see cref="Init"/> is called, so tests and library use stay silent.
/// Logging never throws.
/// </summary>
public static class Log
{
    public const string FileName = "nuclear-replay.log";
    public const long MaxBytes = 1024 * 1024;
    public const int KeepFiles = 2;

    private static readonly object Gate = new();
    private static string? file;
    private static string source = "app";

    public static string? FilePath => file;

    public static string LogDir(AppPaths paths) => Path.Combine(paths.DataDir, "logs");

    /// <summary>Starts logging to <c>&lt;data&gt;/logs/nuclear-replay.log</c>. <paramref name="sourceName"/> tags each line ("app" or "nr").</summary>
    public static void Init(AppPaths paths, string sourceName)
    {
        lock (Gate)
        {
            source = sourceName;
            try
            {
                var dir = LogDir(paths);
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, FileName);
            }
            catch (Exception) { file = null; }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? e = null) => Write("ERROR", e == null ? message : message + Environment.NewLine + e);

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (file == null) return;
            try
            {
                Roll(file);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level,-5} [{source} {Environment.ProcessId}] {message}{Environment.NewLine}";
                // Shared append: two nr commands may write at once.
                using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Encoding.UTF8.GetBytes(line);
                stream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception) { }
        }
    }

    private static void Roll(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytes) return;
        for (int i = KeepFiles; i >= 1; i--)
        {
            var from = i == 1 ? path : Rolled(path, i - 1);
            if (File.Exists(from)) File.Move(from, Rolled(path, i), overwrite: true);
        }
    }

    public static string Rolled(string path, int n) => Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "." + n + Path.GetExtension(path));

    /// <summary>The last <paramref name="maxLines"/> lines of a text file that may be open in another process, or null.</summary>
    public static string? Tail(string path, int maxLines, long maxBytes = 512 * 1024)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (start > 0 && lines.Length > 1) lines = lines[1..];   // first line is probably cut
            if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
            return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - maxLines)));
        }
        catch (Exception e) { return $"(could not read {path}: {e.Message})"; }
    }
}
