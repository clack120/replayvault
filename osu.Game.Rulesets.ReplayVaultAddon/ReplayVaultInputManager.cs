using osu.Framework.Input.Bindings;
using osu.Game.Rulesets.UI;

namespace osu.Game.Rulesets.ReplayVaultAddon;

public sealed partial class ReplayVaultInputManager : RulesetInputManager<ReplayVaultAction>
{
    public ReplayVaultInputManager(RulesetInfo ruleset)
        : base(ruleset, 0, SimultaneousBindingMode.None)
    {
    }
}

public enum ReplayVaultAction
{
}
