using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NuclearReplay.Core.Clips;
using NuclearReplay.Core.Demos;
using NuclearReplay.Core.Game;
using NuclearReplay.Core.Render;
using NuclearReplay.Core.Shots;

namespace NuclearReplay.Core.Preview;

/// <summary>One thumbnail to make: an event (by its index in 'nr events') or the match card picture.</summary>
public sealed record ThumbTarget(string Id, int? EventIndex, double T, string Out, string Camera, string Target);

/// <summary>What one thumbnail run will make, and the shot list that frames each picture.</summary>
public sealed class ThumbPlan
{
    public required string DemoPath { get; init; }
    public required string CacheDir { get; init; }
    public required List<ThumbTarget> Items { get; init; }
    public required string ShotsJson { get; init; }
    /// <summary>The event whose picture doubles as the match card picture, when there is no separate "match" item.</summary>
    public int? MatchFromEvent { get; init; }
}

/// <summary>
/// Still pictures of moments and matches, made by the replay plugin (-ncPreview thumbs mode) and cached per recording
/// under &lt;data&gt;/thumbs/&lt;recording key&gt;/ as event-&lt;index&gt;.jpg and match.jpg. The key changes when the file
/// changes (name, size, modified time), so a re-recorded file never shows old pictures.
/// </summary>
public sealed class ThumbnailService
{
    private readonly NcEnvironment env;
    public int Width { get; init; } = 320;
    public int Height { get; init; } = 180;

    public ThumbnailService(NcEnvironment env) => this.env = env;

    /// <summary>Cache folder; tests and <c>nr thumbs --simulate</c> use a separate one so simulated pictures never show as real.</summary>
    public string? RootDir { get; init; }
    public string Root => RootDir ?? Path.Combine(env.Paths.DataDir, "thumbs");
    public static string SimulatedRoot(AppPaths paths) => Path.Combine(paths.DataDir, "thumbs-simulated");

    public static string Key(DemoSummary summary)
    {
        var stem = RenderJobWriter.Sanitize(Path.GetFileNameWithoutExtension(summary.FileName));
        if (stem.Length > 60) stem = stem[..60];
        var identity = $"{summary.FileName}|{summary.SizeBytes}|{summary.ModifiedUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..10].ToLowerInvariant();
        return stem + "-" + hash;
    }

    public string CacheDir(DemoSummary summary) => Path.Combine(Root, Key(summary));
    public string EventPath(DemoSummary summary, int eventIndex) => Path.Combine(CacheDir(summary), $"event-{eventIndex.ToString(CultureInfo.InvariantCulture)}.jpg");
    public string MatchPath(DemoSummary summary) => Path.Combine(CacheDir(summary), "match.jpg");

    public string? EventThumb(DemoSummary summary, int eventIndex) => Existing(EventPath(summary, eventIndex));
    public string? MatchThumb(DemoSummary summary) => Existing(MatchPath(summary));

    private static string? Existing(string path) => File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;

    /// <summary>
    /// Plans thumbnails: every moment (launch and kill) unless <paramref name="eventIndexes"/> picks some, plus the
    /// match picture. With <paramref name="onlyMissing"/> pictures already in the cache are skipped.
    /// </summary>
    public ThumbPlan Plan(DemoDetails details, IReadOnlyCollection<int>? eventIndexes = null, bool includeMatch = true, bool onlyMissing = true)
    {
        var summary = details.Summary;
        if (summary.Error != null) throw new NcError("unreadable", summary.Error, "The file is not a Nuclear Replay recording or is damaged.");
        var items = new List<ThumbTarget>();
        var chosen = eventIndexes != null
            ? eventIndexes.Distinct().OrderBy(i => i).ToList()
            : details.Events.Select((e, i) => (e, i)).Where(x => x.e.IsMoment).Select(x => x.i).ToList();
        foreach (var index in chosen)
        {
            if (index < 0 || index >= details.Events.Count)
                throw new NcError("bad_event", $"Event #{index} does not exist (this recording has {details.Events.Count}).", "Run nr events <demo> to see the indexes.", invalid: true);
            if (onlyMissing && EventThumb(summary, index) != null) continue;
            var e = details.Events[index];
            var (camera, target, t) = Framing(e, summary.DurationS);
            items.Add(new ThumbTarget("event-" + index.ToString(CultureInfo.InvariantCulture), index, t, EventPath(summary, index), camera, target));
        }
        int? matchFrom = null;
        if (includeMatch && (!onlyMissing || MatchThumb(summary) == null))
        {
            // The match card shows the first kill when there is one, else the action a third of the way in.
            int firstKill = details.Events.FindIndex(e => e.Event == "kill");
            if (firstKill >= 0 && items.Any(i => i.EventIndex == firstKill)) matchFrom = firstKill;
            else if (firstKill >= 0)
            {
                var (camera, target, t) = Framing(details.Events[firstKill], summary.DurationS);
                items.Add(new ThumbTarget("match", null, t, MatchPath(summary), camera, target));
            }
            else
            {
                double t = Math.Round(Math.Clamp(summary.DurationS / 3, Math.Min(5, summary.DurationS / 2), Math.Max(0, summary.DurationS - 1)), 2);
                items.Add(new ThumbTarget("match", null, t, MatchPath(summary), "orbit", "auto"));
            }
        }
        items = items.OrderBy(i => i.T).ToList();
        return new ThumbPlan
        {
            DemoPath = summary.Path, CacheDir = CacheDir(summary), Items = items, ShotsJson = BuildShots(items), MatchFromEvent = matchFrom,
        };
    }

    /// <summary>Camera for a moment's picture: a kill orbits the victim just after the hit, a launch chases the shooter with a level horizon.</summary>
    public static (string Camera, string Target, double T) Framing(DemoEvent e, double duration)
    {
        double clamp(double t) => Math.Round(Math.Clamp(t, 0, Math.Max(0, duration - 0.1)), 2);
        return e.Event switch
        {
            "kill" => ("orbit", e.Victim?.ShotTarget ?? e.Subject?.ShotTarget ?? "auto", clamp(e.T + 0.4)),
            // The cinematic chase keeps the horizon level; the game's chase camera rolls with the aircraft, which
            // left many launch pictures sideways.
            "launch" => ("chase_cine", e.Shooter?.ShotTarget ?? "auto", clamp(e.T + 0.6)),
            _ => ("orbit", e.Subject?.ShotTarget ?? "auto", clamp(e.T)),
        };
    }

    /// <summary>
    /// One shot list for all pictures: each picture's camera starts 1.5 s before it (so the camera has settled), but
    /// never before the previous picture was taken.
    /// </summary>
    public static string BuildShots(IReadOnlyList<ThumbTarget> items)
    {
        var list = new ShotList();
        double previous = -1;
        foreach (var item in items.OrderBy(i => i.T))
        {
            double start = Math.Max(Math.Max(0, item.T - 1.5), previous + 0.05);
            if (start > item.T) start = item.T;
            list.Shots.Add(new Shot { T = Math.Round(start, 3), Camera = item.Camera, Target = item.Target, Fov = item.Camera == "orbit" ? 45 : 55, Hold = 1 });
            previous = item.T;
        }
        if (list.Shots.Count == 0) list.Shots.Add(new Shot { T = 0, Camera = "orbit", Target = "auto" });
        return list.ToJson();
    }

    public PreparedPreview Prepare(ThumbPlan plan, GameInstall game, bool showWindow = false)
    {
        Directory.CreateDirectory(plan.CacheDir);
        var request = new ThumbsRequest
        {
            DemoPath = plan.DemoPath,
            Items = plan.Items.Select(i => (i.Id, i.T, i.Out)).ToList(),
            ShotsJson = plan.ShotsJson,
            Width = Width, Height = Height, ShowWindow = showWindow,
        };
        return PreviewJobWriter.PrepareThumbs(request, env.Paths.JobsDir, PathMapper.For(game));
    }

    /// <summary>Starts the game in the background to make the pictures and waits until it has quit.</summary>
    public async Task<ThumbRunResult> RunAsync(ThumbPlan plan, GameInstall game, Action<RenderProgress>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (plan.Items.Count == 0) return Finish(plan, new RenderProgress { State = "done" }, null);
        if (env.GameRunning())
            throw new NcError("game_running", "Nuclear Option is running.", "Close the game, then try again. Thumbnails start it in the background.");
        var job = Prepare(plan, game);
        var launch = env.Launcher.Launch(game, job.GameArgs);
        Log.Info($"Thumbnails: {plan.Items.Count} pictures, {launch.CommandLine}");
        var watcher = new ProgressWatcher(job.ProgressFile, () => env.GameRunning());
        RenderProgress final;
        try { final = await watcher.WatchAsync(onProgress, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (env.GameRunning()) env.StopGame();
            throw;
        }
        return Finish(plan, final, job);
    }

    private ThumbRunResult Finish(ThumbPlan plan, RenderProgress final, PreparedPreview? job)
    {
        var written = plan.Items.Where(i => File.Exists(i.Out)).ToList();
        if (plan.MatchFromEvent is { } index && plan.Items.FirstOrDefault(i => i.EventIndex == index) is { } source && File.Exists(source.Out))
        {
            var match = Path.Combine(plan.CacheDir, "match.jpg");
            File.Copy(source.Out, match, overwrite: true);
        }
        return new ThumbRunResult(final, written, plan.Items.Except(written).ToList(), job);
    }
}

public sealed record ThumbRunResult(RenderProgress Final, List<ThumbTarget> Written, List<ThumbTarget> Missing, PreparedPreview? Job)
{
    public bool Ok => Final.State == "done";
}
