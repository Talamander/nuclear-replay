using System.CommandLine;
using NuclearReplay.Core;
using NuclearReplay.Core.Clips;
using NuclearReplay.Core.Game;

namespace NuclearReplay.Cli;

public sealed partial class Commands
{
    // ---------- ffmpeg ----------

    private Command FfmpegCommand()
    {
        var cmd = new Command("ffmpeg", "Show or download the video encoder (ffmpeg) that clips and renders use.");

        var status = new Command("status", "Show which ffmpeg is used and whether the downloaded copy is in the game folder.");
        status.SetAction(parse => Execute(parse, "ffmpeg status", output =>
        {
            var settings = LoadSettings(parse);
            var game = env.FindGame(settings);
            var installer = env.CreateFfmpegInstaller();
            var state = installer.State(settings, game?.GameDir);
            output.Result("ffmpeg status", FfmpegData(installer, state, game), w =>
            {
                w.WriteLine(state.Present
                    ? $"Using {state.Path} ({SourceText(state.Source)})."
                    : state.Path != null ? $"\"{state.Path}\" ({SourceText(state.Source)}) does not exist." : "No ffmpeg yet.");
                if (state.SharedPath != null)
                    w.WriteLine($"Download location: {state.SharedPath} ({(state.SharedPresent ? "present" : "not downloaded")}).");
                if (!state.Present) w.WriteLine($"Run 'nr ffmpeg install' to download ffmpeg {installer.Version} (about {installer.SizeText} from {installer.Host}).");
            });
            return ExitCodes.Ok;
        }));
        cmd.Subcommands.Add(status);

        var force = new Option<bool>("--force") { Description = "Download again even when the pinned build is already there." };
        var install = new Command("install", $"Download ffmpeg (a free video tool, about {FfmpegPackageSizeText()}) into <game>/NuclearReplay/tools/ffmpeg and check its SHA-256.");
        install.Options.Add(force);
        install.SetAction(parse => Execute(parse, "ffmpeg install", output =>
        {
            var settings = LoadSettings(parse);
            var game = RequireGame(env, settings);
            var installer = env.CreateFfmpegInstaller();
            var path = DownloadFfmpeg(output, installer, game.GameDir, parse.GetValue(force));
            var state = installer.State(settings, game.GameDir);
            output.Result("ffmpeg install", FfmpegData(installer, state, game), w =>
            {
                w.WriteLine($"ffmpeg {installer.Version} is in {path}.");
                if (state.Source == "setting")
                    w.WriteLine($"Note: the ffmpeg_path setting ({state.Path}) still takes priority. Clear it with: nr settings set ffmpeg_path \"\"");
            });
            return ExitCodes.Ok;
        }));
        cmd.Subcommands.Add(install);
        return cmd;
    }

    private string FfmpegPackageSizeText() => env.CreateFfmpegInstaller().SizeText;

    /// <summary>Downloads with NDJSON progress lines (about every 5%). Throws NcError on failure.</summary>
    private static string DownloadFfmpeg(Output output, FfmpegInstaller installer, string gameDir, bool force)
    {
        int lastPercent = -1;
        string? lastStage = null;
        var progress = new SyncProgress<FfmpegProgress>(p =>
        {
            int percent = (int)(p.Fraction * 100);
            if (p.Stage == lastStage && (p.Stage != "download" || percent / 5 == lastPercent / 5)) return;
            lastStage = p.Stage;
            lastPercent = percent;
            output.Progress(new { state = "ffmpeg", stage = p.Stage, received = p.Received, total = p.Total, fraction = Math.Round(p.Fraction, 4), message = p.Text }, p.Text);
        });
        return installer.InstallAsync(gameDir, progress, force).GetAwaiter().GetResult();
    }

    private static object FfmpegData(FfmpegInstaller installer, FfmpegState state, GameInstall? game) => new
    {
        path = state.Path,
        source = state.Source,
        present = state.Present,
        game_dir = game?.GameDir,
        shared_path = state.SharedPath,
        shared_present = state.SharedPresent,
        matches_pin = state.SharedMatchesPin,
        pinned = new
        {
            version = installer.Version,
            url = installer.Url,
            size = installer.Size,
            sha256 = installer.Sha256,
            publisher = installer.Publisher,
            source_code = installer.SourceUrl,
        },
    };

    private static string SourceText(string? source) => source switch
    {
        "setting" => "the ffmpeg_path setting",
        "shared" => "downloaded into the game folder",
        "bundled" => "bundled next to nr",
        _ => "unknown",
    };

    /// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; would post to the thread pool and reorder lines).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
