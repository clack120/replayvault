using osu.Game.Replays;
using osu.Game.Rulesets.Replays;

namespace osu.Game.Rulesets.ReplayVaultAddon.Replays;

public class ReplayVaultFramedReplayInputHandler : FramedReplayInputHandler<ReplayVaultReplayFrame>
{
    public ReplayVaultFramedReplayInputHandler(Replay replay)
        : base(replay)
    {
    }
}
