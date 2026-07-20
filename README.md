# Kumori

Kumori is a Windows companion for [osu!](https://osu.ppy.sh/) that keeps your play history, sessions, map statistics, performance trends, and replay analysis together in one place.

![A quick tour of the Kumori dashboard, performance, maps, and Replay Analyzer](docs/media/kumori-product-tour-1180x920.gif)

Kumori works with both osu!stable and osu!lazer. It does not change your osu! installation, and your play history stays on your computer.

## What can Kumori do?

- **Remember every play.** See the map, score, accuracy, combo, misses, mods, performance points, and when you played.
- **Organize your sessions.** Kumori groups plays together so you can review an entire practice session at a glance.
- **Show your progress.** Follow your play count, completion rate, average accuracy, best performance, total play time, and daily activity.
- **Find your favorite maps.** Browse maps by how often you play them and compare your average and best results.
- **Help explain mistakes.** The optional Replay Analyzer lets you revisit difficult moments, move through a replay, and inspect timing and cursor movement.
- **Stay out of the way.** Kumori can start with Windows and keep running quietly in the system tray while you play.

## Your session at a glance

The Dashboard combines your recent plays with a detailed view of the selected result. Search your history, filter by outcome, group plays into sessions, and jump into replay analysis when capture data is available.

![Kumori dashboard showing a recent session, selected play, hit timing, and map pressure](docs/media/kumori-dashboard-1180x920.png)

## See the bigger picture

The Maps page highlights the beatmaps you return to most and compares completion, accuracy, performance, and combo results. The Performance page summarizes your overall results and recent daily consistency.

![Kumori maps page showing the most-played beatmaps](docs/media/kumori-maps-1180x920.png)

![Kumori performance page showing activity and accuracy over time](docs/media/kumori-performance-1180x920.png)

## Getting started

1. Open the [latest Kumori release](https://github.com/Lorenso0/Kumori/releases/latest).
2. Download `Kumori.exe`.
3. Run the app and follow the short setup guide.
4. Start osu! and play as usual. Kumori will begin building your history.

Kumori is made for 64-bit Windows. During setup, you can choose whether to enable replay capture and optional extras. These choices can be changed later in Settings.

## Replay Analyzer

When replay capture is enabled, Kumori can open a play in its built-in Replay Analyzer. You can:

![Replay Analyzer showing playback controls, judgement filters, comparison tools, and audio settings](docs/media/kumori-replay-analyzer-controls-1180x920.png)

- Jump directly to misses, slider breaks, 50s, and 100s.
- Slow down playback, step through frames, or loop a difficult moment.
- Review timing and cursor movement around a mistake.
- Compare compatible attempts on the same map or temporarily load a matching `.osr` replay.
- Import an osu! skin for use in the replay viewer.

The advanced analyzer focuses the replay on review events. Its event browser and timeline work alongside loop and frame controls, confidence-tagged timing evidence, and cursor-path analysis.

![Advanced Replay Analyzer showing review events, playback controls, timing evidence, and cursor-path analysis](docs/media/kumori-replay-analyzer-events-1180x920.png)

The analyzer explains what the captured information suggests, but it cannot always know the exact reason for a mistake. Results may be less precise when a replay capture is incomplete. Replay capture for osu! is experimental and may need updates when the game changes.

## Your data stays yours

Kumori stores its settings, history, replay data, skins, backups, and logs locally in:

`%APPDATA%\Kumori\`

Kumori does **not** upload your play history. It only uses the internet when it needs to check for updates, download its local tracking helper, or fetch optional beatmap artwork and map files.

Automatic backups are enabled by default and can be managed in Settings. For protection against a failed or lost drive, copy important backups somewhere else too.

## How tracking works

Kumori uses [tosu](https://github.com/tosuapp/tosu), a small helper that reads live osu! information on your own computer. Kumori can install, update, start, and stop its managed copy automatically. The connection remains local to your PC at `127.0.0.1:24051`.

Some play details are only available when Kumori was running and able to capture them. Older plays may therefore contain less information than newer ones.

## Optional extras

Kumori can also manage a few convenience features from Settings:

- Start automatically when you sign in to Windows, optionally minimized to the system tray.
- Run OpenTabletDriver while Kumori is open.
- Switch supported LG monitors into Dual Mode when osu! starts, then restore them afterward.
- Check tracking, storage, backups, and companion services from the maintenance tools.
- Create a problem-report file when you need help.

OpenTabletDriver and LG Dual Mode support are optional. LG Dual Mode only works with compatible monitors and setups.

## Building from source

This section is for developers. On Windows, install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), clone the repository, and run one of these commands from the repository folder:

```bat
build-app.cmd
```

Build, test, and launch Kumori:

```bat
build-app.cmd run
```

Create a self-contained release at `dist\app\Kumori.exe`:

```bat
build-app.cmd publish
```

Compatibility with the custom BPM Adjust client is tracked against a specific
upstream commit in [docs/BPM_ADJUST_UPSTREAM.md](docs/BPM_ADJUST_UPSTREAM.md).

## Project note

Kumori is an independent project and is not affiliated with, endorsed by, or supported by ppy Pty Ltd, osu!, or tosu.

Third-party components keep their own licenses. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details.
