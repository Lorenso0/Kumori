# Lazer-native Skin Studio

Kumori contains a separate `net10.0` osu.Framework application built directly
against the pinned `2026.726.0-lazer` source. It uses the real `OsuGameBase`,
`SkinManager`, osu!standard autoplay replay player, drawable ruleset, clocks,
shaders, HUD, cursor, animations, and hitsound pipeline.

## Safety invariants

- Opening a skin creates an immutable `.osk` snapshot inside the versioned
  Kumori draft workspace.
- Normal Studio operations never write to the detected player root.
- Publishing retains a complete `.osk` and invokes osu!lazer's normal import
  path.
- Live editing is disabled by default and requires explicit permission on each
  launch. Enabled edits are debounced, written transactionally, and queue an
  in-game reload when osu!lazer is focused.
- Live editing targets only `Kumori Live Preview - <draft>`, verifies expected
  hashes, and stops on external changes. The original installed skin is never
  modified.
- Before the first live-preview write, Kumori snapshots the Realm and every
  referenced skin blob, verifies SHA-256 hashes, and records a manifest.

## Release gate

The native executable reports `release_stage: release` and
`default_eligible: true` from `--probe`. The native Studio is the default Skin
Editor route. The existing WPF editor remains available as an explicit
fallback for one release. The completed row-by-row evidence is recorded in
`lazer-native-skin-studio-parity.md`.

## Semantic element previews

The embedded editor and standalone native workbench share
`SkinStudioSemanticPreviewCatalog`. Renderer protocol version 2 identifies the
selected family, component, ruleset, compatibility, asset provenance, and
optional mania key count rather than reducing the selection to a broad
timestamp.

- Hit-circle fonts are composed into ten real lazer hitcircles numbered 1–10.
- Followpoints use an exclusive native followpoint scene.
- Slider, cursor, judgement, spinner, HUD, ranking, and interface assets use
  their gameplay or UI context.
- Catch and Taiko previews first request their native ruleset components from
  the pinned legacy transformers, with a semantic legacy layout for sparse
  skins. Mania previews honour every configured `[Mania]` key count and fall
  back to 4K.
- Hitsound banks run at 120 BPM through `SkinnableSound`, cycling normal,
  whistle, finish, and clap with `LayeredHitSounds` respected. Slider, spinner,
  countdown, nightcore, result, pause, fail, and interface sounds use matching
  visual event loops.
- Selection changes, draft reloads, pause, focus loss, stop-audio, and shutdown
  all terminate active samples before a new sequence may begin.

The routing inventory is audited against the pinned lazer skin component enums
and transformers, plus the official osu! skinning, interface, `skin.ini`,
sound, Catch, Taiko, and Mania documentation. Recognised components may not use
the raw-asset fallback; it is reserved for unclassified custom files.
