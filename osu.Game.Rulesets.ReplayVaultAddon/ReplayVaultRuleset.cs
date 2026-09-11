using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Bindings;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Overlays.Settings;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Spectator;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Skills;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.ReplayVaultAddon.Objects;
using osu.Game.Rulesets.ReplayVaultAddon.Services;
using osu.Game.Rulesets.ReplayVaultAddon.UI;
using osu.Game.Rulesets.UI;
using osu.Game.Scoring;
using osu.Game;
using osu.Framework.Allocation;
using osu.Framework.Platform;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.ReplayVaultAddon;

public partial class ReplayVaultRuleset : Ruleset
{
    public override string Description => "Replay Vault Addon";
    public override string ShortName => "replayvaultaddon";

    public override DrawableRuleset CreateDrawableRulesetWith(IBeatmap beatmap, IReadOnlyList<Mod>? mods = null)
        => new DrawableReplayVaultRuleset(this, beatmap, mods);

    public override IBeatmapConverter CreateBeatmapConverter(IBeatmap beatmap)
        => new IdentityBeatmapConverter(beatmap, this);

    public override DifficultyCalculator CreateDifficultyCalculator(IWorkingBeatmap beatmap)
        => new ReplayVaultDifficultyCalculator(RulesetInfo, beatmap);

    public override IEnumerable<Mod> GetModsFor(ModType type) => Array.Empty<Mod>();
    public override IEnumerable<KeyBinding> GetDefaultKeyBindings(int variant = 0) => Array.Empty<KeyBinding>();

    public override Drawable CreateIcon() => new Icon();

    public override RulesetSettingsSubsection CreateSettings() => new ReplayVaultSettings(this);

    public override string RulesetAPIVersionSupported => CURRENT_RULESET_API_VERSION;

    private sealed partial class Icon : CompositeDrawable
    {
        [BackgroundDependencyLoader]
        private void load(
            Storage storage,
            MultiplayerClient multiplayerClient,
            SpectatorClient spectatorClient,
            IAPIProvider api,
            ScoreManager scoreManager,
            RealmAccess realm,
            RulesetStore rulesetStore,
            OsuGameBase gameBase)
        {
            ReplayVaultRuntime.TryStart(storage, multiplayerClient, spectatorClient, api, scoreManager, realm, rulesetStore, gameBase.Version);
        }

        public Icon()
        {
            Alpha = 0;

            InternalChildren = new Drawable[]
            {
                new Circle
                {
                    Size = new Vector2(20),
                    Colour = Color4.DarkGreen,
                },
                new OsuSpriteText
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Text = "R",
                    Font = OsuFont.Default.With(size: 14, weight: FontWeight.Bold),
                    Colour = Color4.White
                }
            };
        }
    }

    private sealed class IdentityBeatmapConverter : BeatmapConverter<ReplayVaultHitObject>
    {
        public IdentityBeatmapConverter(IBeatmap beatmap, Ruleset ruleset)
            : base(beatmap, ruleset)
        {
        }

        public override bool CanConvert() => true;

        protected override IEnumerable<ReplayVaultHitObject> ConvertHitObject(HitObject original, IBeatmap beatmap, CancellationToken cancellationToken)
        {
            yield return new ReplayVaultHitObject
            {
                StartTime = original.StartTime,
                Samples = original.Samples,
                Position = (original as IHasPosition)?.Position ?? Vector2.Zero,
            };
        }
    }

    private sealed class ReplayVaultDifficultyCalculator : DifficultyCalculator
    {
        public ReplayVaultDifficultyCalculator(IRulesetInfo ruleset, IWorkingBeatmap beatmap)
            : base(ruleset, beatmap)
        {
        }

        protected override DifficultyAttributes CreateDifficultyAttributes(IBeatmap beatmap, Mod[] mods, Skill[] skills)
            => new DifficultyAttributes(mods, 0);

        protected override IEnumerable<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, Mod[] mods)
            => Array.Empty<DifficultyHitObject>();

        protected override Skill[] CreateSkills(IBeatmap beatmap, Mod[] mods)
            => Array.Empty<Skill>();
    }
}
