# BPM Adjust upstream compatibility pin

Kumori's canonical BPM Adjust implementation is:

- Repository: <https://github.com/Lorenso0/Kumori-BPM>
- Verified commit: `285bcf68a571ce325cee2f89e2d3edf61fd39b42`
- Kumori BPM release: `2026.711.0-kumori.1`
- osu!lazer base: `2026.711.0-lazer`
- Last verified: 2026-07-20

Do not use an unrelated local osu! checkout as the behavioural specification.
Update this pin whenever Kumori is adapted to a newer BPM implementation.

## Canonical source files

- `osu.Game/Rulesets/Mods/ModBPMAdjust.cs`
- `osu.Game/Rulesets/Mods/BPMResolver.cs`
- `osu.Game.Rulesets.Osu/Mods/OsuModBPMAdjust.cs`
- `osu.Game.Rulesets.Taiko/Mods/TaikoModBPMAdjust.cs`
- `osu.Game.Rulesets.Catch/Mods/CatchModBPMAdjust.cs`
- `osu.Game.Rulesets.Mania/Mods/ManiaModBPMAdjust.cs`
- `BPM_MOD.md`

## Serialized contract

The mod acronym is `BPM`. Relevant settings currently include:

- `target_bpm`: nullable number
- `audio_mode`: `0`/`PreservePitch`, `1`/`AdjustPitch`, or `2`/`Nightcore`
- `scale_map_stats_with_bpm`: boolean, default `true`
- `target_initialised`: internal boolean used to retain an explicitly neutral target

Unknown settings must remain tolerated. `target_initialised` is preserved in
stored JSON but intentionally omitted from Kumori's user-facing tooltip.

## Updating the compatibility layer

Fetch the repository and inspect only BPM-related changes since the verified
commit:

```powershell
git fetch origin
git diff 285bcf68a571ce325cee2f89e2d3edf61fd39b42..origin/kumori -- `
  BPM_MOD.md `
  osu.Game/Rulesets/Mods/ModBPMAdjust.cs `
  osu.Game/Rulesets/Mods/BPMResolver.cs `
  osu.Game.Rulesets.Osu/Mods/OsuModBPMAdjust.cs `
  osu.Game.Rulesets.Taiko/Mods/TaikoModBPMAdjust.cs `
  osu.Game.Rulesets.Catch/Mods/CatchModBPMAdjust.cs `
  osu.Game.Rulesets.Mania/Mods/ManiaModBPMAdjust.cs
```

Recheck serialization, source-BPM selection, clock-rate calculation, stat
compensation order, replay frame timestamps, difficulty/performance output, and
all three audio modes before advancing the pin.
