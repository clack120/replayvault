using osu.Game.Rulesets.Replays;

namespace osu.Game.Rulesets.ReplayVaultAddon.Replays;

public class ReplayVaultReplayFrame : ReplayFrame
{
    public override bool IsEquivalentTo(ReplayFrame other)
        => other is ReplayVaultReplayFrame frame && frame.Time == Time;
}
