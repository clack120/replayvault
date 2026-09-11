using osu.Framework.Graphics;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.ReplayVaultAddon.Objects;
using osu.Game.Rulesets.Scoring;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.ReplayVaultAddon.Objects.Drawables;

public partial class DrawableReplayVaultHitObject : DrawableHitObject<ReplayVaultHitObject>
{
    public DrawableReplayVaultHitObject(ReplayVaultHitObject hitObject)
        : base(hitObject)
    {
        Size = new Vector2(20);
        Origin = Anchor.Centre;
        Alpha = 0;
    }

    protected override void CheckForResult(bool userTriggered, double timeOffset)
    {
        if (timeOffset >= 0)
            ApplyResult(HitResult.Perfect);
    }

    protected override void UpdateHitStateTransforms(ArmedState state)
    {
        switch (state)
        {
            case ArmedState.Hit:
                this.FadeOut(100).Expire();
                break;

            case ArmedState.Miss:
                this.FadeColour(Color4.Red, 100);
                this.FadeOut(100).Expire();
                break;
        }
    }
}
