using osu.Framework.Allocation;
using osu.Framework.Input;
using osu.Game.Beatmaps;
using osu.Game.Input.Handlers;
using osu.Game.Replays;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.ReplayVaultAddon.Objects;
using osu.Game.Rulesets.ReplayVaultAddon.Objects.Drawables;
using osu.Game.Rulesets.ReplayVaultAddon.Replays;
using osu.Game.Rulesets.UI;

namespace osu.Game.Rulesets.ReplayVaultAddon.UI;

[Cached]
public partial class DrawableReplayVaultRuleset : DrawableRuleset<ReplayVaultHitObject>
{
    public DrawableReplayVaultRuleset(ReplayVaultRuleset ruleset, IBeatmap beatmap, IReadOnlyList<Mod>? mods = null)
        : base(ruleset, beatmap, mods)
    {
    }

    protected override Playfield CreatePlayfield() => new ReplayVaultPlayfield();

    protected override ReplayInputHandler CreateReplayInputHandler(Replay replay)
        => new ReplayVaultFramedReplayInputHandler(replay);

    public override DrawableHitObject<ReplayVaultHitObject> CreateDrawableRepresentation(ReplayVaultHitObject h)
        => new DrawableReplayVaultHitObject(h);

    protected override PassThroughInputManager CreateInputManager()
        => new ReplayVaultInputManager(Ruleset?.RulesetInfo!);
}
