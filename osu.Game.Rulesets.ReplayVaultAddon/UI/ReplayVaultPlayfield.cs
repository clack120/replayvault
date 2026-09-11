using osu.Framework.Allocation;
using osu.Game.Rulesets.UI;

namespace osu.Game.Rulesets.ReplayVaultAddon.UI;

[Cached]
public partial class ReplayVaultPlayfield : Playfield
{
    [BackgroundDependencyLoader]
    private void load()
    {
        AddInternal(HitObjectContainer);
    }
}
