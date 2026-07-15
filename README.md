# Kumori

Kumori is a Windows companion app for osu! players. It records your plays, groups them into sessions, and helps you understand your performance over time. Everything is shown in a desktop app that can stay quietly in the system tray while you play.

Kumori does not change your osu! installation and does not upload your play history. Your data stays on your computer.

> Kumori is an independent project. It is not affiliated with, endorsed by, or supported by ppy Pty Ltd, osu!, or tosu.

## Main features

### Play history and sessions

Kumori records each play and keeps it in a local history. You can:

- Search your history by map title, artist, difficulty, or mods.
- Filter plays by completed, failed, retried, or quit results.
- Group plays by day or session.
- End an active session yourself when needed.
- See your score, accuracy, grade, combo, misses, performance points, mods, progress, and play time.
- View beatmap artwork and difficulty information.
- Open every recorded play for a specific map.
- Delete a single play, a complete session, older entries, or all tracking data.

The dashboard gives you a quick summary of the current or most recent session, including play count, play time, key presses, best play, performance points gained, and rank changes.

### Detailed play information

Selecting a play opens a detailed view with the information Kumori captured for it. Depending on the game client and available data, this can include:

- Beatmap settings such as star rating, circle size, approach rate, overall difficulty, drain rate, BPM, and map length.
- Hit counts, misses, slider breaks, slider ticks, slider tails, unstable rate, current performance points, full-combo performance points, and maximum possible performance points.
- Key presses, alternation, simultaneous presses, average and peak keys per second, and key hold times.
- Hit-timing graphs with early and late hit information.
- Movement and map-pressure graphs across the play.
- Technical details about the recording source, sample rate, dropped samples, and captured events.

Some details are only available when the required data was captured during that play.

### Performance view

The Performance page shows your long-term activity and consistency across all recorded plays. It includes:

- Total plays, completed plays, failed plays, and completion rate.
- Average accuracy, best performance, total score, and total play time.
- Daily activity with play count, completion rate, average accuracy, and best performance.

### Maps view

The Maps page groups your history by beatmap. It shows your most-played maps first, together with average results, best results, completion rate, and the last time each map was played. Selecting a map opens all recorded plays for it.

## Replay capture and Replay Analyzer

Kumori can capture replay frames from both osu!stable and osu!lazer. When a play has enough captured data, you can open it in the built-in Replay Analyzer.

The replay viewer uses osu!lazer's gameplay and replay components to show the beatmap, replay input, mods, audio, and playback controls. The analyzer adds tools for reviewing difficult moments:

- A seekable timeline with markers for misses, slider breaks, 50s, and 100s.
- Filters that let you show or hide each marker type.
- A list of review events with their time, object type, and available evidence.
- Controls to jump between events, step through replay frames, change playback speed, and loop around a selected event.
- Adjustable time before and after the selected event.
- Input timing, cursor distance, timing-window information, and confidence level for each diagnosis.
- Simple explanations such as an early or late tap, a cursor stopping short, an overshoot, no detected tap, an early slider release, or leaving the slider follow area.
- A local cursor-path view with movement samples, held-button samples, click or release markers, and direction information.
- Pattern summaries that can highlight repeated aim direction, timing changes, or differences from recent attempts on the same map.
- In-viewer cross-attempt comparison: launch a dedicated native, collapsible replay-settings sidebar, choose a captured attempt from the same map with matching playback-rate, hit-geometry, layout, and target-motion mod settings, or temporarily load a checksum-validated `.osr` without adding it to history. Compare its independently coloured skin cursor/trail, comparison-labelled bad judgements when captured events are available, and recorded score statistics after the replay reloads in comparison mode. Visibility, audio, fail, assistance, and scoring mods do not need to match. The sidebar also retains playback, speed, and audio controls; reopen its collapsed handle and use **Stop comparison** to return to the normal replay.

Analyzer settings such as marker visibility, playback speed, loop timing, visual markers, background appearance, and audio levels are remembered.

The analyzer explains the evidence it can see, but it cannot always know the exact reason for a mistake. Results may be less precise when a capture is incomplete. Replay-frame capture is optional, and osu!lazer capture is experimental because game updates can change how it works.

### Replay skins and `.osr` comparison

Kumori includes a skin library for the replay viewer. You can import an `.osk` file or a skin folder, choose the active skin, and remove imported skins. Replay skins use osu!lazer layouts.

For a recorded play, Kumori can also compare its stored result and movement data with a matching `.osr` replay. The comparison can show differences in accuracy, score, combo, cursor movement, click matching, and sample coverage.

## Live tracking with tosu

Kumori uses tosu to read live osu! status and play information through a connection on your own computer. This provides the metadata used for session history, scores, performance points, judgements, and beatmap details.

Kumori manages its own standard copy of tosu. It can install or update it, starts it when osu! is detected, and closes the copy it started when the osu! session ends. It is configured to listen only on `127.0.0.1:24051`.

An internet connection is needed when Kumori downloads or updates tosu. Normal tracking uses the local connection between Kumori and tosu.

## Optional companion tools

Kumori can also:

- Start automatically when you sign in to Windows, optionally minimized to the system tray.
- Run OpenTabletDriver minimized in the tray while Kumori is open, close the copy Kumori launched on exit, and transiently refresh live tablet display mappings after resolution changes without overwriting OTD's saved settings.
- Switch supported LG monitors into Dual Mode when osu! starts, with an option to restore the display after osu! closes.
- Check for new Kumori versions and show an update notice.
- Keep running in the system tray when the main window is closed.

OpenTabletDriver automation and LG Dual Mode are optional. Dual Mode only works with compatible LG monitors and may behave differently depending on the monitor and graphics setup.

## Setup, appearance, and maintenance

The first-run setup guides you through tracking, replay capture, and optional integrations. These choices can be changed later in Settings.

Kumori includes three visual themes: Refined Kumori, Pulse, and Windows Fluent. The layout adapts to the window size, and the app remembers its window and sidebar settings.

Built-in maintenance tools let you:

- Check the health of tracking, storage, replay capture, and companion services.
- Review a local data inventory, cache sizes, backup status, and every class of optional network endpoint.
- Clear downloaded beatmap artwork and map files.
- Find and clean up invalid play records.
- Open app logs and the Kumori data folder.
- Create a problem-report file, with the choice to include or leave out the tracking database.
- Check the tosu connection and view capture diagnostics.
- Create consistent SQLite backups, rotate automatic backups, and stage a verified restore for the next launch.

## Your data

Kumori stores its settings, play history, captured replay data, imported replay skins, cached beatmap media, reports, and logs under:

`%APPDATA%\Kumori\`

The play history is stored in a versioned local SQLite database. Schema upgrades run transactionally, and timestamps used for filtering are stored in UTC while the interface groups and displays them in local time. Cached artwork or map files may be downloaded from the media mirror selected in Settings. Update checks and managed tosu downloads also use the internet, but Kumori does not upload your play history.

Automatic backups are enabled by default and can be configured under Settings. The backup manager creates a consistent database snapshot, validates archives before staging a restore, and applies the restore before the database is opened on the next launch. Copy the backup archive off the computer if you need protection against disk loss.

## Supported environment

Kumori is a Windows desktop app for 64-bit systems. It supports tracking osu!stable and osu!lazer through tosu. The amount of detail available for an older play depends on what Kumori was able to capture at the time.

Third-party components used by the app have their own licences and notices. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for details.
