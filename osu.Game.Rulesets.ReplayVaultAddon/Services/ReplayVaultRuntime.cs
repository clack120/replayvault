using System.Collections.Concurrent;
using System.Text.Json;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.Multiplayer;
using osu.Game.Online.Rooms;
using osu.Game.Online.Spectator;
using osu.Game.Scoring;
using osu.Game.Rulesets;

namespace osu.Game.Rulesets.ReplayVaultAddon.Services;

public static class ReplayVaultRuntime
{
    private static readonly object sync = new();
    private static readonly HashSet<int> roomWatches = new();
    private static bool started;

    private static readonly ConcurrentDictionary<long, byte> in_flight = new();

    private static MultiplayerClient client = null!;
    private static SpectatorClient spectator = null!;
    private static IAPIProvider api = null!;
    private static ScoreManager scoreManager = null!;
    private static RealmAccess realm = null!;
    private static RulesetStore rulesets = null!;

    private static VaultConfig config = new();
    private static string configPath = string.Empty;
    private static string dumpDir = string.Empty;
    private static readonly JsonSerializerOptions json_options = new() { WriteIndented = true };

    public static string ClientVersion { get; private set; } = string.Empty;

    public static volatile bool InMultiplayerRoom;

    public static bool Enabled
    {
        get => config.Enabled;
        set
        {
            config.Enabled = value;
            saveConfig();
            ReplayVaultStatus.Update(s => s with { ServiceState = value ? "Waiting for multiplayer results" : "Disabled" });
        }
    }

    public static void TryStart(
        Storage storage,
        MultiplayerClient multiplayerClient,
        SpectatorClient spectatorClient,
        IAPIProvider apiProvider,
        ScoreManager scores,
        RealmAccess realmAccess,
        RulesetStore rulesetStore,
        string clientVersion)
    {
        lock (sync)
        {
            if (started)
                return;

            started = true;

            client = multiplayerClient;
            spectator = spectatorClient;
            api = apiProvider;
            scoreManager = scores;
            realm = realmAccess;
            rulesets = rulesetStore;
            ClientVersion = clientVersion;

            Storage rulesetsStorage = storage.GetStorageForDirectory("rulesets");
            string root = rulesetsStorage.GetFullPath("replayvault", true);
            Directory.CreateDirectory(root);
            configPath = Path.Combine(root, "config.json");
            loadConfig();

            dumpDir = Path.Combine(root, "dumps");
            ReplayVaultCapture.Initialise(dumpDir);
            ReplayVaultCapture.Enabled = config.DumpEnabled;

            client.ResultsReady += onResultsReady;
            client.RoomUpdated += () =>
            {
                InMultiplayerRoom = client.Room != null;
                syncRoomWatches();
            };

            spectatorClient.OnUserBeganPlaying += (userId, state) =>
                ReplayVaultCapture.UserBeganPlaying(userId, state, api.LocalUser.Value?.Id ?? 0, lookupUsername(userId));
            spectatorClient.OnNewFrames += ReplayVaultCapture.FramesArrived;
            spectatorClient.OnUserFinishedPlaying += ReplayVaultCapture.UserFinishedPlaying;
            spectatorClient.OnUserScoreProcessed += ReplayVaultCapture.UserScoreProcessed;

            SoloFailSaver.Enabled = config.SaveFailedPlays;
            SoloFailSaver.Start(spectatorClient, scoreManager, dumpDir, config.DumpEnabled);

            ReplayVaultStatus.Update(s => s with
            {
                ServiceState = config.Enabled ? "Waiting for multiplayer results" : "Disabled"
            });

            log("started");
        }
    }

    private static string? lookupUsername(int userId)
        => client.Room?.Users.FirstOrDefault(u => u.UserID == userId)?.User?.Username;

    private static void syncRoomWatches()
    {
        MultiplayerRoom? room = client.Room;
        HashSet<int> wanted = room?.Users
            .Select(u => u.UserID)
            .Where(id => id != (api.LocalUser.Value?.Id ?? 0))
            .ToHashSet() ?? new HashSet<int>();
        int added = 0;
        int removed = 0;

        foreach (int userId in wanted.Except(roomWatches).ToArray())
        {
            spectator.WatchUser(userId);
            roomWatches.Add(userId);
            added++;
        }

        foreach (int userId in roomWatches.Except(wanted).ToArray())
        {
            spectator.StopWatchingUser(userId);
            roomWatches.Remove(userId);
            removed++;
        }

        if (added > 0 || removed > 0)
            log($"spectator watches synced: +{added} -{removed} ({roomWatches.Count})");
    }

    public static string SanitizeForFilename(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "_" : clean;
    }

    private static void onResultsReady()
    {
        if (!config.Enabled)
            return;

        MultiplayerRoom? room = client.Room;
        if (room == null)
            return;

        long roomId = room.RoomID;
        long playlistItemId = room.Settings.PlaylistItemId;
        int localUserId = api.LocalUser.Value?.Id ?? 0;

        ReplayVaultStatus.Update(s => s with
        {
            LastEvent = $"ResultsReady room={roomId} item={playlistItemId} ({DateTimeOffset.Now:HH:mm:ss})"
        });
        log($"results ready: room={roomId} item={playlistItemId}");

        _ = processAsync(roomId, playlistItemId, localUserId);
    }

    private static async Task processAsync(long roomId, long playlistItemId, int localUserId)
    {
        try
        {
            DateTimeOffset resultsAt = DateTimeOffset.UtcNow;

            int[] delays = { config.FirstSweepDelaySeconds, config.SecondSweepDelaySeconds };

            for (int i = 0; i < delays.Length; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, delays[i]))).ConfigureAwait(false);
                await sweepAsync(roomId, playlistItemId, localUserId).ConfigureAwait(false);
            }

            if (config.AssembleFromStream)
            {
                foreach (ReplayVaultCapture.CaptureSession session in ReplayVaultCapture.TakeQuitBefore(resultsAt))
                    assemblePartial(session);
            }
        }
        catch (Exception ex)
        {
            ReplayVaultStatus.Update(s => s with { LastError = ex.Message });
            log($"process failed: {ex}");
        }
    }

    private static async Task sweepAsync(long roomId, long playlistItemId, int localUserId)
    {
        var request = new IndexPlaylistScoresRequest(roomId, playlistItemId);

        try
        {
            await api.PerformAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReplayVaultStatus.Update(s => s with { LastError = $"score fetch: {ex.Message}" });
            log($"score index request failed: {ex.Message}");
            return;
        }

        List<MultiplayerScore>? scores = request.Response?.Scores;
        if (scores == null || scores.Count == 0)
        {
            ReplayVaultStatus.Update(s => s with { LastFetch = "0 scores" });
            return;
        }

        int withReplay = scores.Count(s => s.HasReplay);
        ReplayVaultStatus.Update(s => s with { LastFetch = $"{scores.Count} scores, {withReplay} with replay" });
        log($"fetched {scores.Count} scores ({withReplay} with replay) for item {playlistItemId}");

        foreach (MultiplayerScore score in scores)
        {
            if (score.User?.Id == localUserId)
                continue;

            long scoreId = score.ID;

            if (!in_flight.TryAdd(scoreId, 0))
                continue;

            try
            {
                bool exists = realm.Run(r => r.All<ScoreInfo>().Any(sc => sc.OnlineID == scoreId));
                if (exists)
                {
                    ReplayVaultStatus.Update(s => s with { SkippedExisting = s.SkippedExisting + 1 });
                    continue;
                }

                if (config.AssembleFromStream && tryAssemble(score))
                    continue;

                if (!score.HasReplay)
                {
                    ReplayVaultStatus.Update(s => s with { SkippedNoReplay = s.SkippedNoReplay + 1 });
                    continue;
                }

                await downloadOneAsync(score).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, config.DownloadDelaySeconds))).ConfigureAwait(false);
            }
            finally
            {
                in_flight.TryRemove(scoreId, out _);
            }
        }
    }

    private static bool tryAssemble(MultiplayerScore score)
    {
        ReplayVaultCapture.CaptureSession? session = ReplayVaultCapture.TakeFinished(score.User?.Id ?? -1);
        if (session == null)
            return false;

        string user = session.Username ?? score.User?.Username ?? "?";

        try
        {
            bool verified = StreamReplayAssembler.Verify(session, score);
            bool gapless = StreamReplayAssembler.IsGapless(session);

            if (score.HasReplay && (!verified || !gapless))
            {
                log($"assembly skipped for {user}: verified={verified} gapless={gapless} (head={session.FirstFrameTime:0}ms jump={session.LargestJumpMs:0}ms) → download fallback");
                return false;
            }

            byte[] encoded = StreamReplayAssembler.AssembleAndImport(session, verified ? score : null, rulesets, realm, scoreManager);

            if (config.DumpEnabled)
            {
                try
                {
                    string path = Path.Combine(dumpDir,
                        $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}Z_u{session.UserId}_{SanitizeForFilename(user)}_{(verified ? $"s{score.ID}" : "unverified")}_assembled.osr");
                    File.WriteAllBytes(path, encoded);
                }
                catch
                {
                }
            }

            ReplayVaultStatus.Update(s => s with { Assembled = s.Assembled + 1 });
            log($"assembled replay of {user} from stream ({session.FrameCount} frames, verified={verified}, gapless={gapless}, {(score.HasReplay ? "server copy skipped" : "no server copy exists")})");
            return true;
        }
        catch (Exception ex)
        {
            ReplayVaultStatus.Update(s => s with { LastError = $"assembly {user}: {ex.Message}" });
            log($"assembly failed for {user}: {ex.Message} → download fallback");
            return false;
        }
    }

    private static void assemblePartial(ReplayVaultCapture.CaptureSession session)
    {
        string user = session.Username ?? session.UserId.ToString();

        try
        {
            if (session.FrameCount < 10 || session.LastHeader == null)
                return;

            if (session.LastHeader.MaxCombo == 0)
                return;

            byte[] encoded = StreamReplayAssembler.AssembleAndImport(session, null, rulesets, realm, scoreManager);

            if (config.DumpEnabled)
            {
                try
                {
                    string path = Path.Combine(dumpDir,
                        $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}Z_u{session.UserId}_{SanitizeForFilename(user)}_quit_assembled.osr");
                    File.WriteAllBytes(path, encoded);
                }
                catch
                {
                }
            }

            ReplayVaultStatus.Update(s => s with { Assembled = s.Assembled + 1 });
            log($"assembled partial (quit) replay of {user} ({session.FrameCount} frames)");
        }
        catch (Exception ex)
        {
            ReplayVaultStatus.Update(s => s with { LastError = $"partial assembly {user}: {ex.Message}" });
            log($"partial assembly failed for {user}: {ex.Message}");
        }
    }

    private const int download_attempts = 3;

    private static async Task downloadOneAsync(MultiplayerScore score)
    {
        double baseDelay = 4 + Random.Shared.NextDouble() * 4; // 4-8s, jittered to avoid thundering herd

        for (int attempt = 1; attempt <= download_attempts; attempt++)
        {
            if (await downloadAttemptAsync(score, attempt).ConfigureAwait(false))
                return;

            if (attempt < download_attempts)
                await Task.Delay(TimeSpan.FromSeconds(baseDelay * (1 << (attempt - 1)))).ConfigureAwait(false);
        }
    }

    private static async Task<bool> downloadAttemptAsync(MultiplayerScore score, int attempt)
    {
        string user = score.User?.Username ?? "(unknown)";
        ReplayVaultStatus.Update(s => s with { ServiceState = $"Downloading {user} ({score.ID}) attempt {attempt}/{download_attempts}" });

        var request = new DownloadReplayRequest(new ScoreInfo { OnlineID = score.ID });
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        request.Success += filename => completion.TrySetResult(filename);
        request.Failure += ex => completion.TrySetException(ex);

        try
        {
            await api.PerformAsync(request).ConfigureAwait(false);

            Task<string> fileTask = completion.Task;
            Task finished = await Task.WhenAny(fileTask, Task.Delay(TimeSpan.FromMinutes(2))).ConfigureAwait(false);
            if (finished != fileTask)
                throw new TimeoutException("replay download timed out");

            string filename = await fileTask.ConfigureAwait(false);

            if (config.DumpEnabled)
            {
                try
                {
                    string officialPath = Path.Combine(dumpDir,
                        $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}Z_u{score.User?.Id ?? 0}_{SanitizeForFilename(user)}_s{score.ID}_official.osr");
                    File.Copy(filename, officialPath, overwrite: true);
                    log($"official replay dumped: {officialPath}");
                }
                catch (Exception ex)
                {
                    log($"official dump failed: {ex.Message}");
                }
            }

            try
            {
                await scoreManager.Import(new[] { new ImportTask(filename) }).ConfigureAwait(false);
            }
            finally
            {
                tryDelete(filename);
            }

            ReplayVaultStatus.Update(s => s with
            {
                ServiceState = "Waiting for multiplayer results",
                Downloaded = s.Downloaded + 1
            });
            log($"imported replay of {user} (score {score.ID})");
            return true;
        }
        catch (Exception ex)
        {
            ReplayVaultStatus.Update(s => s with
            {
                ServiceState = "Waiting for multiplayer results",
                LastError = $"{user}: {ex.Message}"
            });
            log($"download attempt {attempt}/{download_attempts} failed for {user} (score {score.ID}): {ex.Message}");
            return false;
        }
    }

    private static void tryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void loadConfig()
    {
        try
        {
            if (File.Exists(configPath))
                config = JsonSerializer.Deserialize<VaultConfig>(File.ReadAllText(configPath)) ?? new VaultConfig();
            else
                saveConfig();
        }
        catch (Exception ex)
        {
            log($"config load failed, using defaults: {ex.Message}");
            config = new VaultConfig();
        }
    }

    private static void saveConfig()
    {
        try
        {
            string json = JsonSerializer.Serialize(config, json_options);
            string tmp = configPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, configPath, overwrite: true);
        }
        catch (Exception ex)
        {
            log($"config save failed: {ex.Message}");
        }
    }

    internal static void log(string message)
    {
        Console.WriteLine($"[replayvault] {message}");
        Logger.Log($"[replayvault] {message}", LoggingTarget.Runtime);
    }

    private sealed class VaultConfig
    {
        public bool Enabled { get; set; } = true;
        public int DownloadDelaySeconds { get; set; } = 3;
        public int FirstSweepDelaySeconds { get; set; } = 3;
        public int SecondSweepDelaySeconds { get; set; } = 30;

        public bool DumpEnabled { get; set; } = true;

        public bool SaveFailedPlays { get; set; } = true;

        public bool AssembleFromStream { get; set; } = true;

    }
}
