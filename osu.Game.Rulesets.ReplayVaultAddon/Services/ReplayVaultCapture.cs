using System.Collections.Concurrent;
using Newtonsoft.Json;
using osu.Game.Online.Spectator;
using osu.Game.Replays.Legacy;

namespace osu.Game.Rulesets.ReplayVaultAddon.Services;

public static class ReplayVaultCapture
{
    private static readonly ConcurrentDictionary<int, CaptureSession> sessions = new();

    private static string dumpDir = string.Empty;
    private static int dumpsWritten;

    private const int max_frames_per_session = 2_000_000;

    public static bool Enabled { get; set; } = true;

    public static int ActiveSessions => sessions.Count;
    public static int DumpsWritten => dumpsWritten;

    public static void Initialise(string dumpDirectory)
    {
        dumpDir = dumpDirectory;
        Directory.CreateDirectory(dumpDir);

        _ = expireStaleSessionsAsync();
    }

    private static async Task expireStaleSessionsAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);

            foreach ((int userId, CaptureSession session) in sessions)
            {
                if (DateTimeOffset.UtcNow - session.LastActivityUtc < TimeSpan.FromMinutes(10))
                    continue;

                if (sessions.TryRemove(userId, out CaptureSession? stale))
                    writeDumpAsync(stale, aborted: true);
            }

            int count = finished.Count;

            for (int i = 0; i < count; i++)
            {
                if (!finished.TryDequeue(out CaptureSession? s))
                    break;

                if (DateTimeOffset.UtcNow - (s.FinishedAtUtc ?? s.LastActivityUtc) < TimeSpan.FromMinutes(20))
                    finished.Enqueue(s);
            }
        }
    }

    public static string DumpDirectory => dumpDir;

    public static void UserBeganPlaying(int userId, SpectatorState state, int localUserId, string? username)
    {
        if (!Enabled || userId == localUserId)
            return;

        if (sessions.TryRemove(userId, out CaptureSession? previous))
            writeDumpAsync(previous, aborted: true);

        sessions[userId] = new CaptureSession(userId, username, snapshotState(state), state);
    }

    public static void FramesArrived(int userId, FrameDataBundle bundle)
    {
        if (!Enabled || !sessions.TryGetValue(userId, out CaptureSession? session))
            return;

        session.Append(bundle);
    }

    public static void UserFinishedPlaying(int userId, SpectatorState state)
    {
        if (!sessions.TryRemove(userId, out CaptureSession? session))
            return;

        session.FinishState = snapshotState(state);
        session.FinishStateRaw = state;
        session.FinishedAtUtc = DateTimeOffset.UtcNow;

        finished.Enqueue(session);
        writeDumpAsync(session, aborted: false);
    }

    private static readonly ConcurrentQueue<CaptureSession> finished = new();

    public static CaptureSession? TakeFinished(int userId)
    {
        CaptureSession? match = null;
        int count = finished.Count;

        for (int i = 0; i < count; i++)
        {
            if (!finished.TryDequeue(out CaptureSession? s))
                break;

            if (s.UserId == userId)
                match = s; // keep the newest; older ones for the same user are dropped
            else
                finished.Enqueue(s);
        }

        return match;
    }

    public static List<CaptureSession> TakeQuitBefore(DateTimeOffset cutoff)
    {
        var drained = new List<CaptureSession>();
        int count = finished.Count;

        for (int i = 0; i < count; i++)
        {
            if (!finished.TryDequeue(out CaptureSession? s))
                break;

            if (s.FinishStateRaw?.State == SpectatedUserState.Quit && s.FinishedAtUtc < cutoff)
                drained.Add(s);
            else
                finished.Enqueue(s);
        }

        return drained;
    }

    public static void UserScoreProcessed(int userId, long scoreId)
    {
        if (string.IsNullOrEmpty(dumpDir))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                string line = $"{DateTimeOffset.UtcNow:O} user={userId} score={scoreId}\n";
                File.AppendAllText(Path.Combine(dumpDir, "score-ids.log"), line);
            }
            catch
            {
            }
        });
    }

    private static void writeDumpAsync(CaptureSession session, bool aborted)
    {
        if (string.IsNullOrEmpty(dumpDir))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                string user = ReplayVaultRuntime.SanitizeForFilename(session.Username ?? session.UserId.ToString());
                string suffix = aborted ? "captured-aborted" : "captured";
                string rand = Guid.NewGuid().ToString("N")[..8];
                string path = Path.Combine(dumpDir, $"{session.StartedAtUtc:yyyyMMdd-HHmmss}Z_u{session.UserId}_{user}_{rand}_{suffix}.json");

                using (var file = File.CreateText(path))
                using (var writer = new JsonTextWriter(file))
                    session.WriteTo(writer, aborted);

                Interlocked.Increment(ref dumpsWritten);
                ReplayVaultStatus.Update(s => s with { LastCaptureDump = Path.GetFileName(path) });
                ReplayVaultRuntime.log($"capture dump written: {path}");
            }
            catch (Exception ex)
            {
                ReplayVaultStatus.Update(s => s with { LastError = $"capture dump: {ex.Message}" });
                ReplayVaultRuntime.log($"capture dump failed: {ex}");
            }
        });
    }

    private static StateSnapshot snapshotState(SpectatorState state) => new(
        state.BeatmapID,
        state.RulesetID,
        state.Mods.Select(m => JsonConvert.SerializeObject(m)).ToArray(),
        state.State.ToString(),
        state.MaximumStatistics.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
    );

    internal sealed record StateSnapshot(
        int? BeatmapId,
        int? RulesetId,
        string[] ModsJson,
        string State,
        Dictionary<string, int> MaximumStatistics
    );

    public sealed class CaptureSession
    {
        public int UserId { get; }
        public string? Username { get; }
        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAtUtc { get; set; }
        internal StateSnapshot BeginState { get; }
        internal StateSnapshot? FinishState { get; set; }
        public SpectatorState BeginStateRaw { get; }
        public SpectatorState? FinishStateRaw { get; set; }

        private readonly object frameLock = new();
        private readonly List<BundleRecord> bundles = new();
        private int frameCount;
        private FrameHeader? lastHeader;
        private double lastFrameTime = double.NaN;

        public double? FirstFrameTime { get; private set; }

        public double LargestJumpMs { get; private set; }

        public FrameHeader? LastHeader
        {
            get
            {
                lock (frameLock)
                    return lastHeader;
            }
        }

        public int FrameCount
        {
            get
            {
                lock (frameLock)
                    return frameCount;
            }
        }

        internal CaptureSession(int userId, string? username, StateSnapshot beginState, SpectatorState beginStateRaw)
        {
            UserId = userId;
            Username = username;
            BeginState = beginState;
            BeginStateRaw = beginStateRaw;
        }

        public void Append(FrameDataBundle bundle)
        {
            LastActivityUtc = DateTimeOffset.UtcNow;

            lock (frameLock)
            {
                if (frameCount >= max_frames_per_session)
                    return;

                var frames = new (double, float?, float?, int)[bundle.Frames.Count];
                for (int i = 0; i < bundle.Frames.Count; i++)
                {
                    LegacyReplayFrame f = bundle.Frames[i];
                    frames[i] = (f.Time, f.MouseX, f.MouseY, (int)f.ButtonState);

                    FirstFrameTime ??= f.Time;
                    if (!double.IsNaN(lastFrameTime) && f.Time - lastFrameTime > LargestJumpMs)
                        LargestJumpMs = f.Time - lastFrameTime;
                    lastFrameTime = f.Time;
                }

                bundles.Add(new BundleRecord(DateTimeOffset.UtcNow, bundle.Header, frames));
                frameCount += frames.Length;
                lastHeader = bundle.Header;
            }
        }

        public List<LegacyReplayFrame> BuildLegacyFrames()
        {
            lock (frameLock)
            {
                var result = new List<LegacyReplayFrame>(frameCount);

                foreach (BundleRecord b in bundles)
                {
                    foreach ((double t, float? x, float? y, int buttons) in b.Frames)
                        result.Add(new LegacyReplayFrame(t, x, y, (ReplayButtonState)buttons));
                }

                return result;
            }
        }

        public void WriteTo(JsonTextWriter w, bool aborted)
        {
            List<BundleRecord> snapshot;
            FrameHeader? header;

            lock (frameLock)
            {
                snapshot = new List<BundleRecord>(bundles);
                header = lastHeader;
            }

            w.Formatting = Formatting.Indented;
            w.WriteStartObject();

            w.WritePropertyName("user_id");
            w.WriteValue(UserId);
            w.WritePropertyName("username");
            w.WriteValue(Username);
            w.WritePropertyName("aborted");
            w.WriteValue(aborted);
            w.WritePropertyName("started_at_utc");
            w.WriteValue(StartedAtUtc);
            w.WritePropertyName("finished_at_utc");
            w.WriteValue(DateTimeOffset.UtcNow);

            w.WritePropertyName("begin_state");
            w.WriteRawValue(JsonConvert.SerializeObject(BeginState));
            w.WritePropertyName("finish_state");
            w.WriteRawValue(FinishState == null ? "null" : JsonConvert.SerializeObject(FinishState));

            w.WritePropertyName("final_header");

            if (header == null)
                w.WriteNull();
            else
            {
                w.WriteStartObject();
                w.WritePropertyName("total_score");
                w.WriteValue(header.TotalScore);
                w.WritePropertyName("total_score_without_mods");
                w.WriteValue(header.TotalScoreWithoutMods);
                w.WritePropertyName("accuracy");
                w.WriteValue(header.Accuracy);
                w.WritePropertyName("combo");
                w.WriteValue(header.Combo);
                w.WritePropertyName("max_combo");
                w.WriteValue(header.MaxCombo);
                w.WritePropertyName("statistics");
                w.WriteRawValue(JsonConvert.SerializeObject(header.Statistics.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)));
                w.WritePropertyName("mods");
                w.WriteRawValue(JsonConvert.SerializeObject(header.Mods));
                w.WriteEndObject();
            }

            w.WritePropertyName("bundle_count");
            w.WriteValue(snapshot.Count);
            w.WritePropertyName("frame_count");
            w.WriteValue(snapshot.Sum(b => b.Frames.Length));

            w.WritePropertyName("bundles");
            w.WriteStartArray();

            foreach (BundleRecord b in snapshot)
            {
                w.WriteStartObject();
                w.WritePropertyName("received_at_utc");
                w.WriteValue(b.ReceivedAtUtc);
                w.WritePropertyName("frame_count");
                w.WriteValue(b.Frames.Length);
                w.WritePropertyName("first_time");
                w.WriteValue(b.Frames.Length > 0 ? b.Frames[0].Item1 : (double?)null);
                w.WritePropertyName("last_time");
                w.WriteValue(b.Frames.Length > 0 ? b.Frames[^1].Item1 : (double?)null);
                w.WritePropertyName("header_total_score");
                w.WriteValue(b.Header.TotalScore);
                w.WritePropertyName("header_combo");
                w.WriteValue(b.Header.Combo);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WritePropertyName("frames");
            w.Formatting = Formatting.None;
            w.WriteStartArray();

            foreach (BundleRecord b in snapshot)
            {
                foreach ((double time, float? x, float? y, int buttons) in b.Frames)
                {
                    w.WriteStartArray();
                    w.WriteValue(time);
                    w.WriteValue(x);
                    w.WriteValue(y);
                    w.WriteValue(buttons);
                    w.WriteEndArray();
                }
            }

            w.WriteEndArray();
            w.Formatting = Formatting.Indented;

            w.WriteEndObject();
        }

        private sealed record BundleRecord(
            DateTimeOffset ReceivedAtUtc,
            FrameHeader Header,
            (double, float?, float?, int)[] Frames
        );
    }
}
