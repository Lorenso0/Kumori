# Kumori

Kumori is a Windows companion app for osu! players who want to understand their sessions—not just keep a list of scores. It records local play history, turns attempts into useful session and performance views, and opens a replay analyser for investigating the moments that cost a play.

> Kumori is an independent project. It is not affiliated with, endorsed by, or supported by ppy Pty Ltd, osu!, or tosu.

## What Kumori does

### Local play history and session analysis

Kumori records attempts and groups them into sessions, then presents the information in a desktop dashboard designed for review after a play session. It includes:

- Recent-play and session history, with filters for completed, failed, retried, quit, and multiplayer attempts.
- Per-attempt details such as score result, mods, accuracy, rank, play time, key activity, and captured movement data.
- Session and account-change metrics, including plays, completions, performance points, rank movement, accuracy, and time played.
- Beatmap artwork, cached map media, history search, day/session grouping, and an attempt inspector for quickly moving from an overview into a specific play.
- Local SQLite storage, so play history stays on the PC rather than being uploaded by Kumori.

### Advanced replay analysis

For a saved attempt with replay/movement data, Kumori can open its native replay viewer. The viewer is based on the official osu!lazer gameplay and replay components, so it can render the beatmap, mods, replay input, timing, and playback in a familiar gameplay view.

The **Advanced Analyzer** is built for asking *why* a miss or weak hit happened:

- A seekable timeline marks misses, slider breaks, 50s, and 100s. Marker types can be shown or hidden independently.
- An event browser lists reviewable moments and lets you filter directly to misses, slider breaks, 50s, or 100s.
- Selecting an event focuses replay playback around that object. You can step one replay frame at a time, move between events, change playback speed, and loop a configurable window before and after the selected moment.
- The analysis panel displays the object type, input timing, cursor distance from the target, and a practical diagnosis such as likely aim, likely timing, a released slider input, or a cursor leaving the slider path.
- Miss and slider-break heatmaps visualise the local cursor path around the target, including cursor samples, held-button samples, the selected click/release, and overshoot or undershoot direction.
- Viewer preferences—including marker visibility, loop timing, speed, and visual overlays—are remembered between sessions.

These diagnoses are evidence-based helpers, not a replacement for judgement data: the quality of an analysis depends on the replay frames captured for that attempt. The optional osu!lazer frame capture is experimental and may need updating when osu!lazer changes.

### Tracking and capture

Kumori uses [tosu](https://github.com/tosuapp/tosu) to receive live osu! state and play metadata over a local connection. The app can also capture osu!lazer replay frames while you play, allowing later replay inspection instead of relying only on final score data.

Other optional companion features include startup registration, OpenTabletDriver launch when osu! starts, and LG monitor dual-mode switching on compatible hardware.

## Getting started

1. Install and run osu!.
2. Launch Kumori. It will offer to set up tosu if it is not available.
3. Play normally. Kumori records local attempts and sessions while tracking is enabled.
4. Select a saved attempt in Kumori and open the replay viewer to inspect its timeline and Advanced Analyzer.

All user data is stored beneath `%APPDATA%\Kumori\`, including settings, local history, caches, replay contracts, and logs. Back up that folder if you want to preserve your Kumori data; do not commit it to Git.

## Tosu

Kumori manages an installation of the upstream, unmodified `tosu.exe`; tosu is neither bundled with nor committed to this project.

- On first launch, Kumori downloads the latest compatible Windows release from [tosu GitHub releases](https://github.com/tosuapp/tosu/releases). It then checks for updates no more than once every 24 hours, unless you choose **Install or update** in the app’s **tosu Setup** window.
- The managed executable and configuration live in `%APPDATA%\Kumori\tools\tosu\`.
- Kumori configures tosu to keep its dashboard closed and listen only on `127.0.0.1:24051`.
- GitHub access is needed for the initial download and future updates. Kumori checks that a downloaded file is a Windows executable and attempts Authenticode signature verification before replacing its managed copy.

tosu is maintained by its own project and is licensed under LGPL-3.0-only. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for its required notice and upstream links.

## Replay viewer and osu!lazer dependency

The replay viewer is a separate executable shipped beside Kumori in a published build. It uses the official [ppy/osu](https://github.com/ppy/osu) and osu.Framework NuGet packages, pinned in `replay_viewer\Kumori.ReplayViewer.csproj`. No osu!lazer source checkout or local upstream patch is required.

The viewer’s package version is reported by its `--probe` command. See [replay_viewer/THIRD-PARTY-NOTICES.md](replay_viewer/THIRD-PARTY-NOTICES.md) for licence information.

## Build from source

Requirements: Windows 10/11 x64, Git, and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
# On a fresh clone, this restores the replay viewer's pinned NuGet dependencies,
# then builds Kumori, the viewer, and the tests.
.\build-app.cmd

# Build and start Kumori.
.\build-app.cmd run

# Create a distributable build in dist\app\.
.\build-app.cmd publish
```

Update the pinned osu! NuGet package versions only after validating the replay viewer against representative replays.

## Creating a GitHub Release

Pushing a version tag such as `v0.1.0` runs the GitHub Actions release workflow. It builds a self-contained Windows x64 publish and creates a GitHub Release containing one file: `Kumori.exe`.

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Run the downloaded `Kumori.exe` directly. The replay viewer is embedded in the executable and is extracted automatically into Kumori's private runtime storage when advanced replay analysis is used; users do not need to manage a companion folder. You can also run the workflow manually from GitHub's **Actions** tab to download a test build as an artifact without creating a Release.
