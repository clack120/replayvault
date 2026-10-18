using System.Runtime.CompilerServices;
using osu.Framework.Bindables;
using osu.Framework.Logging;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Online.Leaderboards;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.Leaderboards;
using Realms;

namespace osu.Game.Rulesets.ReplayVaultAddon.Services;

/// <summary>
/// Adds the successful local scores of the played beatmap (the player's own and other players' replays stored in
/// the local database) to the leaderboard shown while playing, when the Global scope is selected.
/// The scores are merged into <see cref="LeaderboardManager.Scores"/> only while the player screen is active and the
/// original online result is restored afterwards, so song select and the results screen are unchanged.
/// Local scores have no online position, so on a partial online leaderboard the in-play list shows them as "-".
/// </summary>
public static class LocalScoreMerger
{
    private const int max_extra_scores = 100;

    private static readonly object sync = new();
    private static readonly ConditionalWeakTable<LeaderboardScores, object> produced = new();

    private static bool started;
    private static bool hooked;
    private static bool inGameplay;
    private static LeaderboardScores? lastOnline;

    private static LeaderboardManager manager = null!;
    private static RealmAccess realm = null!;
    private static OsuGameBase? gameBase;

    public static void Start(LeaderboardManager? leaderboardManager, RealmAccess? realmAccess, OsuGameBase? game)
    {
        if (leaderboardManager == null || realmAccess == null)
            return;

        lock (sync)
        {
            if (started)
                return;

            started = true;
        }

        manager = leaderboardManager;
        realm = realmAccess;
        gameBase = game;

        manager.Scores.BindValueChanged(e => onScores(e.NewValue));
        hookScreens();
        ReplayVaultRuntime.log("local score merger started");
    }

    private static void hookScreens()
    {
        if (hooked || gameBase is not OsuGame { ScreenStack: { } stack })
            return;

        hooked = true;
        stack.ScreenPushed += (_, next) => setGameplay(isGameplayScreen(next));
        stack.ScreenExited += (_, next) => setGameplay(isGameplayScreen(next));
    }

    private static bool isGameplayScreen(IScreen? screen) => screen is Player or PlayerLoader;

    private static void onScores(LeaderboardScores? scores)
    {
        hookScreens(); // the screen stack may not exist yet when the addon starts

        if (scores == null || produced.TryGetValue(scores, out _))
            return; // nothing yet, or our own merged result coming back through the binding

        lastOnline = scores;

        if (inGameplay)
            apply(scores);
    }

    private static void setGameplay(bool value)
    {
        if (inGameplay == value)
            return;

        inGameplay = value;

        if (manager.Scores is not Bindable<LeaderboardScores?> bindable)
            return;

        if (value)
        {
            if (lastOnline != null)
                apply(lastOnline);
        }
        else if (bindable.Value != null && produced.TryGetValue(bindable.Value, out _) && lastOnline != null)
        {
            bindable.Value = lastOnline; // back to the plain online result for song select / results
        }
    }

    private static void apply(LeaderboardScores online)
    {
        if (!ReplayVaultRuntime.MergeLocalScores || manager.Scores is not Bindable<LeaderboardScores?> bindable)
            return;

        var merged = merge(online, out int added);

        if (merged == null)
            return;

        produced.Add(merged, new object());
        bindable.Value = merged;

        var map = manager.CurrentCriteria?.Beatmap;
        ReplayVaultRuntime.log($"local score merger: +{added} local scores for {map?.Metadata.Title} [{map?.DifficultyName}] (in-play leaderboard)");
    }

    private static LeaderboardScores? merge(LeaderboardScores online, out int added)
    {
        added = 0;

        var criteria = manager.CurrentCriteria;

        if (online.FailState != null || criteria?.Beatmap == null || criteria.Ruleset == null || criteria.Scope != BeatmapLeaderboardScope.Global)
            return null;

        try
        {
            Guid beatmapId = criteria.Beatmap.ID;
            string rulesetShortName = criteria.Ruleset.ShortName;

            IEnumerable<ScoreInfo> local = realm.Run(r => r.All<ScoreInfo>()
                                                          .Filter($"{nameof(ScoreInfo.BeatmapInfo)}.{nameof(BeatmapInfo.ID)} == $0"
                                                                  + $" AND {nameof(ScoreInfo.BeatmapInfo)}.{nameof(BeatmapInfo.Hash)} == {nameof(ScoreInfo.BeatmapHash)}"
                                                                  + $" AND {nameof(ScoreInfo.Ruleset)}.{nameof(RulesetInfo.ShortName)} == $1"
                                                                  + $" AND {nameof(ScoreInfo.DeletePending)} == false"
                                                                  + $" AND {nameof(ScoreInfo.Rank)} != $2", beatmapId, rulesetShortName, (int)ScoreRank.F) // Passed is not persisted; a failed play is stored with rank F
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

            // do not repeat what the online list already has (by local id or online id), nor the personal best
            var shown = online.AllScores.ToList();
            bool alreadyShown(ScoreInfo s) => shown.Any(o => o.Equals(s) || (s.OnlineID > 0 && o.OnlineID == s.OnlineID));

            ScoreInfo[] extras = local.Where(s => !alreadyShown(s)).OrderByTotalScore().Take(max_extra_scores).ToArray();

            if (extras.Length == 0)
                return null;

            added = extras.Length;

            // ScoresRequested and TotalScores grow together, so a partial online leaderboard stays partial
            return LeaderboardScores.Success(
                online.TopScores.Concat(extras).ToArray(),
                online.ScoresRequested + extras.Length,
                online.TotalScores + extras.Length,
                online.UserScore);
        }
        catch (Exception e)
        {
            Logger.Error(e, "[replayvault] local score merge failed", LoggingTarget.Runtime);
            return null;
        }
    }
}
