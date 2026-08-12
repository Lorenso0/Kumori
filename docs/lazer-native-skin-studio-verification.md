# Lazer-native Skin Studio final verification

Date: 2026-07-30  
Kumori version: 0.6.2  
Pinned lazer version: `2026.726.0-lazer`  
Pinned upstream commit: `5da71008b082d1a77e4bb301dc98886f1f24b895`

## Mandatory backups

- Source, worktree, refs, patches, and persistent Kumori data:
  `.restore-points/20260729-211520-before-lazer-native-studio`
- Pre-existing upstream-dirty preservation:
  `.restore-points/20260729-215821-preexisting-upstream-dirty`
- Real lazer pre-live-preview backup:
  `C:\Users\Lorenzo\AppData\Roaming\Kumori\skins\studio\real-lazer-backups\20260730-023603-878-before-live-preview`

All backup gates completed successfully. The real live-preview backup verified
the Realm, 26 skins, and all 1,934 referenced blobs before the first write.

## Automated acceptance

- Full solution: 1,114 passed, 0 failed, 0 skipped.
- Packaged native command acceptance: 108 passed, 0 failed.
- Packaged command captures: 17.
- Exported acceptance `.osk`: 18,934 bytes,
  SHA-256 `8a15841a59a86c02a2b0d6efbf66f8bde4b464e02e4fe9ab4b4501ec91d8f589`.
- Player-root write/export boundary: passed.
- Fixed-clock native visual acceptance: 16 targets passed.
- Real publish acceptance: imported and verified as lazer skin
  `510828e2-b969-4baa-8c7d-5d1a0b6488fa`.
- Real live-preview audit: every original skin and referenced blob remained
  byte-identical; only the mapped disposable preview copy changed.
- Upstream checkout: pinned commit plus the tracked Kumori renderer patch.
- `git diff --check`: passed.

## Final packaged runtime

The exact final `dist/app/Kumori.exe` was opened and the native Studio was
attached at the compact 900x650 acceptance size.

- Native child size: 842x570.
- `WS_CHILD=True`
- `WS_POPUP=False`
- `WS_EX_APPWINDOW=False`
- `WS_EX_TOOLWINDOW=True`
- Native process was responsive and had no top-level main-window handle.
- Default surface was the all-elements workbench.
- The explicit real-gameplay option rendered the real lazer `ReplayPlayer`
  scene inline during the preceding full-size packaged walkthrough.
- Studio navigation reused one native PID.
- Startup now keeps the branded Kumori loading surface visible until the
  native workbench sends its session-bound readiness signal. Captures at
  250 ms and 1 second show the loading surface; the 3.5-second capture shows
  a direct handoff to the populated all-elements workbench with no exposed
  black or blank renderer frame.
- Kumori tray Exit closed the app and its owned native child without an orphan.

Runtime captures:
`.artifacts/gui/editor-final-0250ms.png`,
`.artifacts/gui/editor-final-1s.png`, and
`.artifacts/gui/editor-final-3500ms.png`.

## Release artifacts

- `dist/app/Kumori.exe`
  - Size: 510,340,959 bytes
  - SHA-256:
    `4D6DC94050878D44F6A3FF507475A822A9A1F353AE549890DC6FDCE0118415F9`
- `artifacts/Kumori.NativeTools.zip`
  - Size: 178,605,287 bytes
  - SHA-256:
    `DF8C28FBB58655F99F54DD846023FDB41F157C4EEAA98855B8596F19B939DB96`

The native probe reports:

```json
{
  "status": "ok",
  "contract_version": 1,
  "lazer_revision": "2026.726.0-lazer",
  "embedded_host": "child-hwnd-v1",
  "default_workspace": "all-elements-workbench",
  "gameplay_workspace": "inline-real-gameplay",
  "release_stage": "release",
  "default_eligible": true
}
```

The native Studio is the default Skin Editor route. The legacy WPF editor
remains available as an explicit fallback for one release.
