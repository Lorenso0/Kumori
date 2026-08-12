# Lazer-native Skin Studio parity matrix

This is the release gate for making the native Studio the default while the
legacy WPF editor remains available as a fallback for one release.

## Evidence

- **B** — verified source and persistent-data restore points:
  `20260729-211520-before-lazer-native-studio` and
  `20260729-215821-preexisting-upstream-dirty`.
- **U** — full solution suite: 1,109 passed, 0 failed, 0 skipped.
- **C** — packaged command acceptance:
  `.artifacts/packaged-command-acceptance/command-acceptance-manifest.json/command-acceptance-manifest.json`;
  108 commands, 17 captures, verified `.osk`, and player-root export block.
- **V** — fixed-clock native visual acceptance covering 16 gameplay/workbench
  targets with the real lazer renderer, SkinManager, animation, audio, and
  fallback paths.
- **E** — final packaged embedded walkthrough:
  `WS_CHILD=True`, `WS_POPUP=False`, `WS_EX_APPWINDOW=False`,
  `WS_EX_TOOLWINDOW=True`; 1862x1000 maximized, 1142x720 at 1200x800, and
  842x570 at 900x650; one responsive native PID survived
  Studio -> Dashboard -> Studio; real ReplayPlayer gameplay rendered inline.
- **D** — host DPI matrix at 96, 120, 144, and 192 DPI plus
  `WM_DPICHANGED`/`WM_SIZE` regressions. The packaged hardware path ran at
  96 DPI.
- **L** — real-store guarded live-preview acceptance and independent audit.
  Backup `20260730-023603-878-before-live-preview` verified the Realm and all
  1,934 referenced blobs before the first write; all 26 original skins and
  their blobs remained byte-identical.
- **P** — real lazer publish acceptance. The complete `.osk` was retained,
  imported through lazer's normal import path, and verified as skin
  `510828e2-b969-4baa-8c7d-5d1a0b6488fa` after a separate 27-skin/1,935-blob
  backup.

`Complete` means the capability is user-visible and backed by the referenced
automated or packaged-runtime evidence.

## Hosting and workspaces

| Capability | Status | Evidence |
|---|---|---|
| Embedded inside the Kumori window, never a popup/taskbar window | Complete | E, U |
| Resize with Kumori | Complete | E, U |
| DPI handling | Complete | D, U |
| Navigation reuses one Studio process | Complete | E |
| Inline crash recovery/restart | Complete | E, U |
| Clean owned-child shutdown | Complete | E, U |
| All-elements workbench is the default | Complete | C, V, E |
| Explicit authoritative real-gameplay workspace | Complete | C, V, E |
| Isolated custom beatmap/media import | Complete | U, C |

## Draft lifecycle

| Capability | Status | Evidence |
|---|---|---|
| Create blank skin | Complete | C, U |
| Build from Extras with composition readiness | Complete | C, U |
| Open/import valid and malformed `.osk` | Complete | C, U |
| Read-only installed-lazer browser and isolated snapshot | Complete | C, U |
| Search, switch, and reopen drafts | Complete | C, U |
| Duplicate draft with independent effective files | Complete | C, U |
| Rename skin and author with line-preserving `skin.ini` update | Complete | C, U |
| Two-step recoverable delete and restore | Complete | C, U |
| Recover interrupted manifest save | Complete | C, U |
| Source conflict detection | Complete | C, U |
| Undo and redo | Complete | C, U |
| Discard one family or one exact review entry | Complete | C, U |
| Discard all with mandatory verified backup and undo | Complete | C, U |
| Change review with hashes and sizes | Complete | C, U |
| Atomic save and interrupted-save recovery | Complete | C, U |
| Manual verified backup and independent restore | Complete | C, U |
| Automatic backup preferences and retention | Complete | C, U |

## Asset workbench

| Capability | Status | Evidence |
|---|---|---|
| Organized 135+ element coverage view across ten families | Complete | C, V, U |
| Skin/fallback labels and sparse-skin behaviour | Complete | C, V, U |
| Real animation grouping, playback, timing, insert/move/delete | Complete | C, V, U |
| Search and category composition | Complete | C, U |
| Hide fallback and fully transparent placeholders | Complete | C, U |
| Select a logical asset family | Complete | C |
| Atomic multi-file folder import and validation | Complete | C, U |
| Exact frame/resolution replacement including `@2x` | Complete | C, U |
| Delete family with fallback refresh and undo | Complete | C, U |
| Byte-identical family export | Complete | C, U |
| Isolated external editing, watcher, validation, and stale-write rejection | Complete | C, U |
| Copy/paste complete element families | Complete | C, U |
| Reset selected family or filtered category | Complete | C, U |
| Extract selected family or filtered category to Extras | Complete | C, U |
| Full render and scrolling layout | Complete | C, V, E |
| 1x/2x pairing and animation-frame scope | Complete | C, U |

## Image editing

| Capability | Status | Evidence |
|---|---|---|
| Colorize | Complete | C, U |
| Luminance tint | Complete | C, U |
| Multiplicative tint | Complete | C, U |
| Hue/saturation/lightness | Complete | C, U |
| Native graphical HSV/hex colour picker | Complete | C, U |
| Persistent normalized swatches | Complete | C, U |
| Full family, primary pair, 1x, 2x, and exact-frame scopes | Complete | C, U |
| Non-destructive byte-for-byte reset | Complete | C, U |

## Audio

| Capability | Status | Evidence |
|---|---|---|
| Preview common samples through `SkinnableSound` | Complete | C, V |
| Exclusive sample routing and stop | Complete | C, V |
| Real `TrackBass` play/pause/stop/restart/seek transport | Complete | C, U |
| Searchable current-skin and Extras track selection | Complete | C, U |
| Targeted compatible audio replacement | Complete | C, U |
| Waveform metadata and bounded 16-bit PCM normalization | Complete | C, U |
| Gameplay hitsound validation | Complete | V |

## `skin.ini`

| Capability | Status | Evidence |
|---|---|---|
| Raw line-preserving editing with insert/reorder/save | Complete | C, U |
| Structured General, Colours, Fonts, Catch, and Mania editing | Complete | C, U |
| Lossless structured/raw switching with unsaved buffer | Complete | C, E, U |
| Preserve comments and unknown keys | Complete | C, U |
| Preserve ordering, line endings, and encoding | Complete | C, U |
| Context links from structured fields to workbench assets | Complete | C, U |

## Extras

| Capability | Status | Evidence |
|---|---|---|
| Persistent Extras library | Complete | C, E, U |
| Search/filter and favourites-only state | Complete | C, U |
| Favourite persistence | Complete | C, U |
| Exact pack/file/setting comparison | Complete | C, E, U |
| Logical element and setting selection with explicit replacement policy | Complete | C, U |
| Transactional staging into a draft after backup | Complete | C, U |
| Extract skin/category/elements with lazer-used filtering | Complete | C, U |
| Import portable package, `.osk`, and folder | Complete | C, U |
| Deterministic portable package export | Complete | C, U |
| Atomic rename and recoverable delete/restore | Complete | C, U |
| Check/repair after verified backup | Complete | C, U |
| Signed catalog install, offline cache, cancel, retry, and update | Complete | C, U |

## Publishing and safety

| Capability | Status | Evidence |
|---|---|---|
| Complete `.osk` export and round trip | Complete | C, U |
| Explicit publish through lazer's normal import path | Complete | P, U |
| Normal activity cannot write to the detected player root | Complete | C, E, U |
| Live preview is launch-scoped and opt-in | Complete | L, U |
| Only a disposable `Kumori Live Preview - <draft>` copy is changed | Complete | L, U |
| Mandatory pre-sync Realm/blob backup | Complete | L |
| Opt-in transactional live editing while lazer runs, with queued reloads | Complete | L, U |
| External preview-copy changes stop synchronization | Complete | L, U |
| Every source skin and referenced blob remains unchanged | Complete | L |
| Upstream `third_party/osu` checkout remains pinned with the tracked renderer patch | Complete | U |
| Legacy WPF editor remains available for one release | Complete | E, U |

## Release decision

Every row is complete. The native Studio is the default Skin Editor route and
is eligible for release; the legacy editor remains an explicit fallback.
