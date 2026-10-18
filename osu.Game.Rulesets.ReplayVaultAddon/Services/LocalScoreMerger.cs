using System.Runtime.CompilerServices;
using osu.Framework.Bindables;
using osu.Framework.Logging;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Online.Leaderboards;
using osu.Game.Scoring;
using osu.Game.Screens.Play.Leaderboards;
using Realms;

namespace osu.Game.Rulesets.ReplayVaultAddon.Services;

/// <summary>
/// Appends the successful local scores of the selected beatmap (the player's own and other players' replays
/// stored in the local database) to the end of the Global leaderboard, so they get ranks after the online ones.
/// Online scores keep their ranks; scores already on the online list are not repeated.
/// The song select list numbers its rows by index, so the added rows show as #51, #52, ... rather than "-".
/// </summary>
public static class LocalScoreMerger
{
    private const int max_extra_scores = 100;

    private static readonly object sync = new();
    private static readonly ConditionalWeakTable<LeaderboardScores, object> produced = new();
    private static bool started;

    public static void Start(LeaderboardManager? manager, RealmAccess? realm)
    {
        if (manager == null || realm == null)
            return;

        lock (sync)
        {
            if (started)
                return;

            started = true;
        }

        manager.Scores.BindValueChanged(e => onScores(manager, realm, e.NewValue));
        ReplayVaultRuntime.log("local score merger started");
    }

    private static void onScores(LeaderboardManager manager, RealmAccess realm, LeaderboardScores? scores)
    {
        if (!ReplayVaultRuntime.MergeLocalScores || scores == null || scores.FailState != null)
            return;

        if (produced.TryGetValue(scores, out _))
            return; // our own merged result coming back through the binding

        var criteria = manager.CurrentCriteria;

        if (criteria?.Beatmap == null || criteria.Ruleset == null || criteria.Scope != BeatmapLeaderboardScope.Global)
            return;

        try
        {
            Guid beatmapId = criteria.Beatmap.ID;
            string rulesetShortName = criteria.Ruleset.ShortName;

            IEnumerable<ScoreInfo> local = realm.Run(r => r.All<ScoreInfo>()
                                                          .Filter($"{nameof(ScoreInfo.BeatmapInfo)}.{nameof(BeatmapInfo.ID)} == $0"
                                                                  + $" AND {nameof(ScoreInfo.BeatmapInfo)}.{nameof(BeatmapInfo.Hash)} == {nameof(ScoreInfo.BeatmapHash)}"
                                                                  + $" AND {nameof(ScoreInfo.Ruleset)}.{nameof(RulesetInfo.ShortName)} == $1"
                                                                  + $" AND {nameof(ScoreInfo.DeletePending)} == false"
                                                                  + $" AND {nameof(ScoreInfo.Passed)} == true", beatmapId, rulesetShortName)
                                                          .AsEnumerable()
                                                          .Detach());

            // same mod filtering as the local leaderboard scope
            if (criteria.ExactMods != null)
            {
                if (!criteria.ExactMods.Any())
                    local = local.Where(s => !s.Mods.Any());
                else
                {
                    var selected = criteria.ExactMods.Select(m => m.Acronym).ToHashSet();
                    local = local.Where(s => selected.SetEquals(s.Mods.Select(m => m.Acronym)));
                }
            }

            // do not repeat what the online list already shows (by local id or online id), nor the personal best slot
            var shown = scores.AllScores.ToList();
            bool alreadyShown(ScoreInfo s) => shown.Any(o => o.Equals(s) || (s.OnlineID > 0 && o.OnlineID == s.OnlineID));

            ScoreInfo[] extras = local.Where(s => !alreadyShown(s)).OrderByTotalScore().Take(max_extra_scores).ToArray();

            if (extras.Length == 0)
                return;

            var merged = LeaderboardScores.Success(
                scores.TopScores.Concat(extras).ToArray(),
                scores.ScoresRequested + extras.Length,
                scores.TotalScores + extras.Length,
                scores.UserScore);

            produced.Add(merged, new object());

            // Scores is exposed as IBindable, but the instance is the manager's own Bindable
            if (manager.Scores is Bindable<LeaderboardScores?> bindable)
            {
                bindable.Value = merged;
                ReplayVaultRuntime.log($"local score merger: +{extras.Length} local scores for {criteria.Beatmap.Metadata.Title} [{criteria.Beatmap.DifficultyName}]");
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "[replayvault] local score merge failed", LoggingTarget.Runtime);
        }
    }
}
