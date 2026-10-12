using System.Text.Json;
using System.Text.Json.Nodes;
using NuclearReplay.Core;

namespace NuclearReplay.Cli;

/// <summary>Exit codes: 0 success, 1 the operation failed, 2 the input was invalid.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Invalid = 2;
}

public sealed class CliError : Exception
{
    public string Code { get; }
    public string? Fix { get; }
    public int ExitCode { get; }

    public CliError(string code, string message, string? fix = null, int exitCode = ExitCodes.Failed) : base(message)
    {
        Code = code;
        Fix = fix;
        ExitCode = exitCode;
    }
}

/// <summary>Writes either human text or one JSON document per command (plus NDJSON progress lines for long commands).</summary>
public sealed class Output
{
    public bool JsonMode { get; }
    private readonly TextWriter stdout;
    private readonly TextWriter stderr;
    // Long commands (preview) report from background threads; one line at a time.
    private readonly object gate = new();

    public Output(bool json, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        JsonMode = json;
        this.stdout = stdout ?? Console.Out;
        this.stderr = stderr ?? Console.Error;
    }

    /// <summary>Final result. JSON: {"ok":true,"command":...,"data":...}.</summary>
    public void Result(string command, object? data, Action<TextWriter>? human = null, bool ok = true)
    {
        if (JsonMode)
        {
            var envelope = new JsonObject
            {
                ["ok"] = ok,
                ["command"] = command,
                ["data"] = data == null ? null : data as JsonNode ?? JsonSerializer.SerializeToNode(data, data.GetType(), Json.Options),
            };
            lock (gate) stdout.WriteLine(envelope.ToJsonString(Json.Options));
        }
        else lock (gate) human?.Invoke(stdout);
    }

    /// <summary>Plain text on stdout (human mode only).</summary>
    public void Text(string text) { lock (gate) stdout.WriteLine(text); }

    public void Error(string command, CliError error)
    {
        if (JsonMode)
        {
            var envelope = new JsonObject
            {
                ["ok"] = false,
                ["command"] = command,
                ["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message, ["fix"] = error.Fix },
            };
            stdout.WriteLine(envelope.ToJsonString(Json.Options));
        }
        else
        {
            stderr.WriteLine("Error: " + error.Message);
            if (error.Fix != null) stderr.WriteLine("Fix: " + error.Fix);
        }
    }

    /// <summary>Progress line. JSON: one compact object per line with "type":"progress". Human: stderr.</summary>
    public void Progress(object data, string humanLine)
    {
        if (JsonMode)
        {
            var node = JsonSerializer.SerializeToNode(data, data.GetType(), Json.Options) as JsonObject ?? new JsonObject();
            node.Insert(0, "type", "progress");
            lock (gate)
            {
                stdout.WriteLine(node.ToJsonString(Json.Compact));
                stdout.Flush();
            }
        }
        else lock (gate) stderr.WriteLine(humanLine);
    }

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
