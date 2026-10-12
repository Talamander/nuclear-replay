using System.Diagnostics;
using System.Text.Json.Nodes;
using NuclearReplay.Core.Clips;
using NuclearReplay.Core.Game;
using NuclearReplay.Core.Render;

namespace NuclearReplay.Core.Preview;

/// <summary>A preview run: status and frames out, commands in. <c>nr preview</c> uses <see cref="GamePreviewSource"/>.</summary>
public interface IPreviewSource : IAsyncDisposable
{
    event Action<PreviewStatus>? StatusChanged;
    event Action<PreviewFrame>? FrameReceived;
    PreviewStatus Status { get; }
    /// <summary>True when the frames are not game footage (the simulated game used by tests and demos).</summary>
    bool Simulated { get; }
    /// <summary>Starts the game in the background. Returns once it is launched; status and frames follow as events.</summary>
    Task StartAsync(PreviewRequest request, CancellationToken cancellationToken = default);
    /// <summary>Sends a command (see <see cref="PreviewCommands"/>). False when the game is not connected.</summary>
    bool Send(JsonObject command);
    /// <summary>Asks the game to quit and waits for it; closes it if it does not.</summary>
    Task StopAsync();
}

/// <summary>Runs the preview in the real game through the replay plugin (-ncPreview).</summary>
public sealed class GamePreviewSource : IPreviewSource
{
    private readonly NcEnvironment env;
    private readonly GameInstall game;
    private readonly Stopwatch clock = new();
    private readonly CancellationTokenSource cts = new();
    private PreviewListener? listener;
    private PreviewConnection? connection;
    private TaskCompletionSource closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool stopping;
    private PreviewStatus status = new();
    private TimeSpan waitingSince;

    public event Action<PreviewStatus>? StatusChanged;
    public event Action<PreviewFrame>? FrameReceived;
    public PreviewStatus Status => status;
    public bool Simulated { get; init; }

    /// <summary>How long the game may take to start and connect (Steam, game boot, mod loader).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromMinutes(4);
    /// <summary>How long loading, skipping ahead or going back may take before the preview gives up.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromMinutes(12);
    /// <summary>How long to wait for the game to quit by itself before closing it.</summary>
    public TimeSpan QuitGrace { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Status updates while nothing arrives from the game (elapsed time keeps moving).</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);

    public PreparedPreview? Job { get; private set; }
    public LaunchPlan? Plan { get; private set; }
    public bool Connected => connection != null;

    public GamePreviewSource(NcEnvironment env, GameInstall game)
    {
        this.env = env;
        this.game = game;
    }

    public Task StartAsync(PreviewRequest request, CancellationToken cancellationToken = default)
    {
        if (Job != null) throw new InvalidOperationException("A preview source runs one preview; make a new one.");
        if (env.GameRunning())
            throw new NcError("game_running", "Nuclear Option is running.", "Close the game, then try again. The preview starts it in the background.");
        listener = new PreviewListener();
        Job = PreviewJobWriter.PreparePreview(request, env.Paths.JobsDir, PathMapper.For(game), listener.Port, listener.Token);
        clock.Start();
        Publish(new PreviewStatus { State = "launching", From = request.From, To = request.To });
        try
        {
            Plan = env.Launcher.Launch(game, Job.GameArgs);
        }
        catch (LaunchException e)
        {
            listener.Dispose();
            Publish(status with { State = "error", Error = e.Message, Fix = e.Fix });
            throw new NcError("launch_failed", e.Message, e.Fix);
        }
        Log.Info($"Preview: started {Plan.CommandLine}");
        _ = Task.Run(() => RunAsync(cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            var accept = listener!.AcceptAsync(token);
            bool seenGame = false;
            TimeSpan? goneSince = null;
            while (!accept.IsCompleted)
            {
                await Task.WhenAny(accept, Task.Delay(TickInterval, token)).ConfigureAwait(false);
                if (accept.IsCompleted) break;
                if (ProgressError() is { } rejected) { Fail(rejected, "Check the mod on the Setup page, then try again."); return; }
                bool running = env.GameRunning();
                if (running) { seenGame = true; goneSince = null; }
                else if (seenGame)
                {
                    goneSince ??= clock.Elapsed;
                    if (clock.Elapsed - goneSince > TimeSpan.FromSeconds(10)) { Fail("The game closed before the preview started.", "Start the preview again. If it keeps closing, run nr diagnostics and attach the file to a bug report."); return; }
                }
                if (clock.Elapsed > ConnectTimeout)
                {
                    Fail($"The game did not start the preview within {ConnectTimeout.TotalMinutes:0} minutes.",
                        seenGame ? "Check that the Nuclear Replay mod is installed (Setup page), then try again." : "Make sure Steam is running and signed in, then try again.");
                    return;
                }
                Publish(status with { State = status.State == "launching" ? "launching" : status.State });
            }
            var conn = await accept.ConfigureAwait(false);
            connection = conn;
            conn.MessageReceived += OnMessage;
            conn.FrameReceived += frame => FrameReceived?.Invoke(frame);
            conn.Closed += OnClosed;
            conn.Start();
            Publish(status with { State = "starting" });
            waitingSince = clock.Elapsed;
            while (!token.IsCancellationRequested && !status.IsFinal)
            {
                await Task.Delay(TickInterval, token).ConfigureAwait(false);
                if (!status.IsWaiting) { waitingSince = clock.Elapsed; continue; }
                if (clock.Elapsed - waitingSince > LoadTimeout)
                {
                    Fail($"The match did not load within {LoadTimeout.TotalMinutes:0} minutes.", "Try again. Long matches take longer to skip through; a moment near the start loads faster.");
                    return;
                }
                Publish(status); // keeps the elapsed time moving while the game is busy
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Error("Preview failed", e);
            Fail("The preview stopped: " + e.Message, "Try again.");
        }
    }

    private void OnMessage(JsonObject message)
    {
        switch ((string?)message["type"])
        {
            case "status":
                var next = PreviewStatus.FromMessage(message, clock.Elapsed);
                if (next.IsWaiting && !status.IsWaiting) waitingSince = clock.Elapsed;
                if (next.State == "error") next = next with { Fix = "Try again. If it keeps failing, run nr diagnostics and attach the file to a bug report." };
                Publish(next);
                break;
            case "error":
            case "restart_needed":
                Log.Warn("Preview: game says " + message.ToJsonString());
                Publish(status with { Message = (string?)message["message"] });
                break;
        }
    }

    private void OnClosed(string? error)
    {
        closedSignal.TrySetResult();
        if (status.State == "error") return;
        if (stopping) { Publish(status with { State = "closed", Message = "Preview stopped." }); return; }
        var reason = ProgressError() ?? error;
        if (reason != null) Publish(status with { State = "error", Error = reason, Fix = "Try again. If it keeps failing, run nr diagnostics and attach the file to a bug report." });
        else Publish(status with { State = "closed", Message = "The game closed." });
    }

    private string? ProgressError()
    {
        if (Job == null) return null;
        var progress = RenderProgress.TryRead(Job.ProgressFile);
        return progress?.State == "error" ? progress.Error ?? "the game rejected the preview" : null;
    }

    private void Fail(string message, string fix)
    {
        if (status.State == "error") return;
        Log.Warn("Preview: " + message);
        Publish(status with { State = "error", Error = message, Fix = fix });
        _ = Task.Run(StopAsync);
    }

    private void Publish(PreviewStatus next)
    {
        status = next with { Elapsed = clock.Elapsed };
        StatusChanged?.Invoke(status);
    }

    public bool Send(JsonObject command) => connection?.Send(command) ?? false;

    public async Task StopAsync()
    {
        if (stopping) return;
        stopping = true;
        try
        {
            if (connection != null && connection.Send(PreviewCommands.Quit()))
                await Task.WhenAny(closedSignal.Task, Task.Delay(QuitGrace)).ConfigureAwait(false);
            // The preview started this game: give it a few seconds to exit, then close it if it is still there
            // (still starting up, or not answering).
            var deadline = DateTime.UtcNow + (connection != null ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
            while (env.GameRunning() && DateTime.UtcNow < deadline) await Task.Delay(250).ConfigureAwait(false);
            if (Plan != null && env.GameRunning())
            {
                Log.Info("Preview: closing the game");
                env.StopGame();
            }
        }
        finally
        {
            cts.Cancel();
            connection?.Dispose();
            listener?.Dispose();
            if (!status.IsFinal) Publish(status with { State = "closed", Message = "Preview stopped." });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        cts.Dispose();
    }
}
