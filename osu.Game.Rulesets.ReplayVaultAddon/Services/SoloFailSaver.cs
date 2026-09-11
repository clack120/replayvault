using System.Reflection;
using System.Security.Cryptography;
using osu.Game.Beatmaps;
using osu.Game.Extensions;
using osu.Game.IO.Archives;
using osu.Game.Online.Spectator;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Utils;

namespace osu.Game.Rulesets.ReplayVaultAddon.Services;

public static class SoloFailSaver
{
    // SpectatorClient has no public accessor for the active score.
    private static readonly FieldInfo? current_score_field =
        typeof(SpectatorClient).GetField("currentScore", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? current_beatmap_field =
        typeof(SpectatorClient).GetField("currentBeatmap", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly PropertyInfo? is_playing_property =
        typeof(SpectatorClient).GetProperty("isPlaying", BindingFlags.NonPublic | BindingFlags.Instance);

    private static int savedCount;
    public static int SavedCount => savedCount;

    public static bool Enabled { get; set; } = true;

    public static void Start(SpectatorClient spectatorClient, ScoreManager scoreManager, string dumpDir, bool dumpEnabled)
    {
        if (current_score_field == null || current_beatmap_field == null || is_playing_property == null)
        {
            ReplayVaultStatus.Update(s => s with { LastError = "SoloFailSaver: SpectatorClient private members not found (game update?)" });
            return;
        }

        _ = runAsync(spectatorClient, scoreManager, dumpDir, dumpEnabled);
    }

    private static async Task runAsync(SpectatorClient spectatorClient, ScoreManager scoreManager, string dumpDir, bool dumpEnabled)
    {
        Score? trackedScore = null;
        IBeatmap? trackedBeatmap = null;

        while (true)
        {
            await Task.Delay(500).ConfigureAwait(false);

            if (!Enabled)
                continue;

            try
            {
                bool isPlaying = (bool)is_playing_property!.GetValue(spectatorClient)!;
                var currentScore = (Score?)current_score_field!.GetValue(spectatorClient);
                var currentBeatmap = (IBeatmap?)current_beatmap_field!.GetValue(spectatorClient);

                if (isPlaying && currentScore != null)
                {
                    if (!ReferenceEquals(currentScore, trackedScore))
                    {
                        if (trackedScore != null)
                        {
                            Score ended = trackedScore;
                            IBeatmap? endedBeatmap = trackedBeatmap;
                            _ = processEndedSessionAsync(ended, endedBeatmap, scoreManager, dumpDir, dumpEnabled);
                        }

                        trackedScore = currentScore;
                        trackedBeatmap = currentBeatmap;
                    }
                }
                else if (trackedScore != null)
                {
                    Score ended = trackedScore;
                    IBeatmap? endedBeatmap = trackedBeatmap;
                    trackedScore = null;
                    trackedBeatmap = null;

                    if (!ReplayVaultRuntime.InMultiplayerRoom)
                        _ = processEndedSessionAsync(ended, endedBeatmap, scoreManager, dumpDir, dumpEnabled);
                }
            }
            catch (Exception ex)
            {
                ReplayVaultStatus.Update(s => s with { LastError = $"SoloFailSaver poll: {ex.Message}" });
            }
        }
    }

    private static async Task processEndedSessionAsync(Score score, IBeatmap? beatmap, ScoreManager scoreManager, string dumpDir, bool dumpEnabled)
    {
        try
        {
            await Task.Delay(2000).ConfigureAwait(false);

            ScoreInfo info = score.ScoreInfo;

            if (info.Passed)
                return;

            if (score.Replay.Frames.Count < 10)
                return;

            if (info.MaxCombo == 0)
                return;

            if (!info.Ruleset.IsLegacyRuleset())
            {
                ReplayVaultStatus.Update(s => s with { LastError = "failed-play save skipped: custom ruleset" });
                return;
            }

            if (beatmap == null)
            {
                ReplayVaultStatus.Update(s => s with { LastError = "failed-play save skipped: no beatmap reference" });
                return;
            }

            if (info.Date == default)
                info.Date = DateTimeOffset.UtcNow;

            byte[] encoded;

            using (var stream = new MemoryStream())
            {
                new LegacyScoreEncoder(score, beatmap).Encode(stream);
                encoded = stream.ToArray();
            }

            if (dumpEnabled)
            {
                try
                {
                    string name = ReplayVaultRuntime.SanitizeForFilename(info.User?.Username ?? "self");
                    string sha = Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant()[..10];
                    string path = Path.Combine(dumpDir, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}Z_self_{name}_{info.TotalScore}_{sha}_failed.osr");
                    await File.WriteAllBytesAsync(path, encoded).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            var importable = info.DeepClone();
            var reader = new ByteArrayArchiveReader(encoded, "replay.osr");

            try
            {
                scoreManager.Import(importable, reader);
            }
            catch (Realms.Exceptions.RealmDuplicatePrimaryKeyValueException)
            {
                ReplayVaultRuntime.log($"failed-play save skipped: score already imported by the game ({info.User?.Username})");
                return;
            }

            Interlocked.Increment(ref savedCount);
            ReplayVaultStatus.Update(s => s with { LastError = "(none)" });
            ReplayVaultRuntime.log($"saved failed play: {info.User?.Username} on {info.BeatmapInfo?.ToString() ?? "?"} ({score.Replay.Frames.Count} frames)");
        }
        catch (Exception ex)
        {
            ReplayVaultStatus.Update(s => s with { LastError = $"failed-play save: {ex.Message}" });
            ReplayVaultRuntime.log($"failed-play save error: {ex}");
        }
    }
}
