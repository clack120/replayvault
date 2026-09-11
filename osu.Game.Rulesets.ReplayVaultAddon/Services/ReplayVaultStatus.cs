namespace osu.Game.Rulesets.ReplayVaultAddon.Services;

public sealed record ReplayVaultRuntimeStatus(
    string ServiceState,
    string LastEvent,
    string LastFetch,
    int Downloaded,
    int Assembled,
    int SkippedExisting,
    int SkippedNoReplay,
    string LastCaptureDump,
    string LastError,
    DateTimeOffset UpdatedAtUtc
);

public static class ReplayVaultStatus
{
    private static readonly object sync = new();

    private static ReplayVaultRuntimeStatus status = new(
        ServiceState: "Waiting for multiplayer results",
        LastEvent: "(none)",
        LastFetch: "(none)",
        Downloaded: 0,
        Assembled: 0,
        SkippedExisting: 0,
        SkippedNoReplay: 0,
        LastCaptureDump: "(none)",
        LastError: "(none)",
        UpdatedAtUtc: DateTimeOffset.UtcNow
    );

    public static ReplayVaultRuntimeStatus GetSnapshot()
    {
        lock (sync)
            return status;
    }

    public static void Update(Func<ReplayVaultRuntimeStatus, ReplayVaultRuntimeStatus> updater)
    {
        lock (sync)
        {
            status = updater(status) with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }
    }
}
