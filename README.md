# Replay Vault

Unofficial osu!lazer addon for automatically importing multiplayer replays and
saving failed local plays.

## Features

- Imports other players' available multiplayer replays.
- Builds replays from spectator streams when possible.
- Falls back to server downloads when stream capture is unavailable.
- Saves failed local plays, including instant-restart attempts.

## Requirements

- .NET 10 SDK
- A compatible osu!lazer source checkout

## Build and install

```sh
./build-addon.sh
cp dist/osu.Game.Rulesets.ReplayVaultAddon-net10.0.dll \
  ~/.local/share/osu/rulesets/
```

Restart osu!lazer and open Settings > Rulesets > Replay Vault Addon to check
the status panel.

## Configuration

Configuration is stored at:

```text
~/.local/share/osu/rulesets/replayvault/config.json
```

The JSON keys use their C# property names:

```json
{
  "Enabled": true,
  "DownloadDelaySeconds": 3,
  "FirstSweepDelaySeconds": 3,
  "SecondSweepDelaySeconds": 30,
  "DumpEnabled": true,
  "SaveFailedPlays": true,
  "AssembleFromStream": true
}
```

The Settings → Rulesets panel provides the main download on/off toggle.

## Limitations

- This addon depends on osu!lazer internal APIs and may break after updates.
- Failed-play capture depends on osu!lazer's spectator client internals.
- Custom ruleset failed plays are not encoded by the legacy replay encoder.
- Replay dumps may contain player names, score data, and input frames. Enable
  them only if you need diagnostic copies.

This is a community addon and is not affiliated with osu! or ppy Pty Ltd.

## License

MIT No Attribution (`MIT-0`). See [LICENSE](LICENSE).
