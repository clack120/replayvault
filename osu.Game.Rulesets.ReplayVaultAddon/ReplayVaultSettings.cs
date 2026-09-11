using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Platform;
using osu.Game.Database;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Online.API;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Spectator;
using osu.Game.Overlays.Settings;
using osu.Game.Rulesets.ReplayVaultAddon.Services;
using osu.Game.Scoring;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.ReplayVaultAddon;

public sealed partial class ReplayVaultSettings : RulesetSettingsSubsection
{
    public ReplayVaultSettings(ReplayVaultRuleset ruleset)
        : base(ruleset)
    {
    }

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

        Children = new Drawable[]
        {
            new StatusPanel()
        };
    }

    private sealed partial class StatusPanel : CompositeDrawable
    {
        private readonly BindableBool enabled = new(true);

        private readonly OsuSpriteText serviceState = valueText();
        private readonly OsuSpriteText lastEvent = valueText();
        private readonly OsuSpriteText lastFetch = valueText();
        private readonly OsuSpriteText counters = valueText();
        private readonly OsuSpriteText capture = valueText();
        private readonly OsuSpriteText lastError = valueText();
        private readonly OsuSpriteText updatedAt = valueText();

        private double nextRefreshTime;

        public StatusPanel()
        {
            AutoSizeAxes = Axes.Y;
            RelativeSizeAxes = Axes.X;

            InternalChild = new Container
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Margin = new MarginPadding { Bottom = 12 },
                CornerRadius = 12,
                Masking = true,
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = new Color4(10, 20, 12, 180)
                    },
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Padding = new MarginPadding(12),
                        Spacing = new Vector2(0, 6),
                        Children = new Drawable[]
                        {
                            new OsuSpriteText
                            {
                                Text = "Replay Vault Status",
                                Font = OsuFont.Default.With(size: 20, weight: FontWeight.Bold),
                                Colour = Color4.White
                            },
                            new SettingsItemV2(new FormCheckBox
                            {
                                Caption = "Auto-download all replays at multiplayer results",
                                Current = enabled
                            }) { ShowRevertToDefaultButton = false },
                            row("Service", serviceState),
                            row("Last Event", lastEvent),
                            row("Last Fetch", lastFetch),
                            row("Totals", counters),
                            row("Capture", capture),
                            row("Last Error", lastError),
                            row("Updated", updatedAt)
                        }
                    }
                }
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            enabled.Value = ReplayVaultRuntime.Enabled;
            enabled.BindValueChanged(e =>
            {
                if (ReplayVaultRuntime.Enabled != e.NewValue)
                    ReplayVaultRuntime.Enabled = e.NewValue;
            });
        }

        protected override void Update()
        {
            base.Update();

            if (Time.Current < nextRefreshTime)
                return;

            nextRefreshTime = Time.Current + 300;

            ReplayVaultRuntimeStatus status = ReplayVaultStatus.GetSnapshot();
            serviceState.Text = status.ServiceState;
            lastEvent.Text = status.LastEvent;
            lastFetch.Text = status.LastFetch;
            counters.Text = $"assembled {status.Assembled}, downloaded {status.Downloaded}, already local {status.SkippedExisting}, no replay {status.SkippedNoReplay}";
            capture.Text = $"recording {ReplayVaultCapture.ActiveSessions}, dumps {ReplayVaultCapture.DumpsWritten}, failed saved {SoloFailSaver.SavedCount}, last: {status.LastCaptureDump}";
            lastError.Text = status.LastError;
            updatedAt.Text = status.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }

        private static Drawable row(string label, OsuSpriteText value) => new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Horizontal,
            Spacing = new Vector2(8, 0),
            Children = new Drawable[]
            {
                new OsuSpriteText
                {
                    Text = label + ":",
                    Font = OsuFont.Default.With(size: 13, weight: FontWeight.SemiBold),
                    Colour = new Color4(185, 205, 190, 255)
                },
                value
            }
        };

        private static OsuSpriteText valueText() => new()
        {
            Font = OsuFont.Default.With(size: 13),
            Colour = Color4.White
        };
    }
}
