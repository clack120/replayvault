using osu.Game.Database;
using osu.Game.Extensions;
using osu.Game.IO.Archives;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Rooms;
using osu.Game.Online.Spectator;
using osu.Game.Replays;
using osu.Game.Rulesets;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Beatmaps;

namespace osu.Game.Rulesets.ReplayVaultAddon.Services;

public static class StreamReplayAssembler
{
    public const double HEAD_GAP_TOLERANCE_MS = 1000;

    public const double JUMP_GAP_TOLERANCE_MS = 10_000;

    public static bool IsGapless(ReplayVaultCapture.CaptureSession session)
        => session.FrameCount > 0
           && session.FirstFrameTime is { } first && first <= HEAD_GAP_TOLERANCE_MS
           && session.LargestJumpMs <= JUMP_GAP_TOLERANCE_MS;

    public static bool Verify(ReplayVaultCapture.CaptureSession session, MultiplayerScore score)
    {
        var header = session.LastHeader;
        if (header == null)
            return false;

        if (score.User?.Id != session.UserId)
            return false;

        if (score.TotalScore != header.TotalScore || score.MaxCombo != header.MaxCombo)
            return false;

        foreach (var key in header.Statistics.Keys.Union(score.Statistics.Keys))
        {
            header.Statistics.TryGetValue(key, out int a);
            score.Statistics.TryGetValue(key, out int b);
            if (a != b)
                return false;
        }

        var capMods = session.BeginStateRaw.Mods.Select(m => m.Acronym).Where(a => a != "TD").OrderBy(a => a, StringComparer.Ordinal);
        var scoreMods = (score.Mods ?? Array.Empty<Online.API.APIMod>()).Select(m => m.Acronym).Where(a => a != "TD").OrderBy(a => a, StringComparer.Ordinal);
        if (!capMods.SequenceEqual(scoreMods))
            return false;

        return true;
    }

    public static byte[] AssembleAndImport(
        ReplayVaultCapture.CaptureSession session,
        MultiplayerScore? verifiedScore,
        RulesetStore rulesets,
        RealmAccess realm,
        ScoreManager scoreManager)
    {
        SpectatorState state = session.BeginStateRaw;
        var header = session.LastHeader ?? throw new InvalidOperationException("no frames captured");

        RulesetInfo ruleset = rulesets.GetRuleset(state.RulesetID ?? 0)
                              ?? throw new InvalidOperationException($"unknown ruleset {state.RulesetID}");

        BeatmapInfo beatmap = realm.Run(r =>
                                  r.All<BeatmapInfo>().FirstOrDefault(b => b.OnlineID == (state.BeatmapID ?? -1))?.Detach())
                              ?? throw new InvalidOperationException($"beatmap {state.BeatmapID} not in local database");

        bool passed = verifiedScore?.Passed
                      ?? session.FinishStateRaw?.State == SpectatedUserState.Passed;

        var info = new ScoreInfo
        {
            Ruleset = ruleset,
            BeatmapInfo = beatmap,
            BeatmapHash = beatmap.MD5Hash,
            User = new APIUser { Id = session.UserId, Username = session.Username ?? verifiedScore?.User?.Username ?? session.UserId.ToString() },
            APIMods = verifiedScore?.Mods ?? state.Mods.ToArray(),
            Statistics = new Dictionary<Scoring.HitResult, int>(header.Statistics),
            MaximumStatistics = new Dictionary<Scoring.HitResult, int>(state.MaximumStatistics),
            TotalScore = header.TotalScore,
            TotalScoreWithoutMods = header.TotalScoreWithoutMods ?? 0,
            Accuracy = header.Accuracy,
            MaxCombo = header.MaxCombo,
            Combo = header.Combo,
            Passed = passed,
            OnlineID = verifiedScore?.ID ?? -1,
            Date = verifiedScore?.EndedAt ?? session.FinishedAtUtc ?? DateTimeOffset.UtcNow,
            ClientVersion = ReplayVaultRuntime.ClientVersion,
        };

        if (header.Pauses != null)
            foreach (int p in header.Pauses)
                info.Pauses.Add(p);

        if (verifiedScore != null)
            info.Rank = verifiedScore.Rank;
        else if (!passed)
            info.Rank = ScoreRank.F;
        else
            info.Rank = ruleset.CreateInstance().CreateScoreProcessor().RankFromScore(header.Accuracy, header.Statistics);

        var score = new Score
        {
            ScoreInfo = info,
            Replay = new Replay { Frames = session.BuildLegacyFrames().Cast<Rulesets.Replays.ReplayFrame>().ToList() },
        };

        byte[] encoded;

        using (var stream = new MemoryStream())
        {
            new LegacyScoreEncoder(score, null).Encode(stream);
            encoded = stream.ToArray();
        }

        scoreManager.Import(info.DeepClone(), new ByteArrayArchiveReader(encoded, "replay.osr"));

        return encoded;
    }
}
