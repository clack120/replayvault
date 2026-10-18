using System.Globalization;
using System.Text.Json;
using osu.Framework.Logging;
using osu.Game.Online;
using osu.Game.Overlays;
using osu.Game.Overlays.Notifications;

namespace osu.Game.Rulesets.ReplayVaultAddon.Services;

/// <summary>
/// Persists the "+x pp, +#x" change that osu! only flashes in the toolbar after a score is submitted.
/// It listens to the statistics update osu! already receives for the submitted score, so it makes no extra
/// requests. Each update is written to the runtime log, stdout, a JSONL file and the notification overlay.
/// </summary>
public static class SubmitDeltaReporter
{
    private static readonly object sync = new();
    private static bool started;
    private static string jsonlPath = string.Empty;

    public static void Start(UserStatisticsWatcher? watcher, INotificationOverlay? notifications, string directory)
    {
        if (watcher == null)
            return;

        lock (sync)
        {
            if (started)
                return;

            started = true;
        }

        jsonlPath = Path.Combine(directory, "submits.jsonl");
        watcher.LatestUpdate.BindValueChanged(e =>
        {
            if (e.NewValue != null)
                report(e.NewValue, notifications);
        });

        ReplayVaultRuntime.log("submit delta reporter started");
    }

    private static void report(ScoreBasedUserStatisticsUpdate update, INotificationOverlay? notifications)
    {
        try
        {
            var score = update.Score;
            decimal? ppBefore = update.Before.PP;
            decimal? ppAfter = update.After.PP;
            int? rankBefore = update.Before.GlobalRank;
            int? rankAfter = update.After.GlobalRank;

            decimal? ppDelta = ppAfter - ppBefore;
            int? rankDelta = rankBefore - rankAfter; // positive = the rank number got smaller (improved)

            var meta = score.BeatmapInfo?.Metadata;
            string map = $"{meta?.Artist} - {meta?.Title} [{score.BeatmapInfo?.DifficultyName}]";
            string mods = score.Mods.Length == 0 ? "NM" : string.Join("+", score.Mods.Select(m => m.Acronym));
            string scorePp = score.PP.HasValue ? $"{score.PP.Value:0.0}pp" : "no pp";

            string message =
                $"{map} {mods} {score.Accuracy:P2} {scorePp} | profile {fmt(ppBefore, "0.0")} -> {fmt(ppAfter, "0.0")} ({signed(ppDelta, "0.00")}pp)"
                + $" | rank #{fmt(rankBefore, "N0")} -> #{fmt(rankAfter, "N0")} ({signed(rankDelta, "0")})";

            ReplayVaultRuntime.log($"submit result: score={score.OnlineID} {message}");

            File.AppendAllText(jsonlPath, JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.Now,
                scoreId = score.OnlineID,
                map,
                mods,
                accuracy = score.Accuracy,
                scorePp = score.PP,
                ppBefore,
                ppAfter,
                rankBefore,
                rankAfter,
            }) + "\n");

            notifications?.Post(new SimpleNotification { Text = $"{signed(ppDelta, "0.00")}pp, rank {signed(rankDelta, "0")}  -  {map} {scorePp}" });
        }
        catch (Exception e)
        {
            Logger.Error(e, "[replayvault] submit delta report failed", LoggingTarget.Runtime);
        }
    }

    private static string fmt<T>(T? value, string format) where T : struct, IFormattable
        => value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "?";

    private static string signed<T>(T? value, string format) where T : struct, IFormattable, IComparable<T>
    {
        if (!value.HasValue)
            return "?";

        string s = value.Value.ToString(format, CultureInfo.InvariantCulture);
        return value.Value.CompareTo(default) >= 0 ? "+" + s : s;
    }
}
