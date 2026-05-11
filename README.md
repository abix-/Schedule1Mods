# Schedule1Mods

Mods and supporting analysis for [Schedule I](https://store.steampowered.com/app/3164500/) (TVGS, IL2CPP / MelonLoader).

## Contents

- [What's here](#whats-here)
- [Build the mods](#build-the-mods)
- [Status](#status)

## What's here

| Path | Contents |
| ---- | -------- |
| `EmployeeReset/` | A MelonLoader sidecar mod that fixes two chemist bugs: the recurring NRE on save load, and the mid-cook wedge when ingredients run out. Includes a smart-reset hotkey (F8) and a Harmony postfix on `StartMixingStationBehaviour.CanCookStart` that blocks the cook when input slots are empty. Status: save/load symptom verified fixed in one in-game test; ingredient-gate fix built and ready for in-game verification. |
| `docs/employee-mod-bug-analysis.md` | Full root-cause analysis of two stuck-chemist bug classes, with line-cited references into `eMployee.dll` v2.2.4 and Schedule I 0.4.5f2 game assemblies, plus a proposed upstream patch for the eMployee mod author. |
| `docs/ingredient-gate-fix-plan.md` | Standing implementation plan for the ingredient-availability fix. Covers the Phase 1 reconnaissance findings, the predicate design, the Harmony wiring, and the validation matrix. |
| `docs/certainty-tracking.md` | Verification matrix for the ingredient gate. Tracks which claims are empirically proven vs. still hypothesis, and what evidence is needed to close each gap. The doc that says "are we sure yet?". |

## Build the mods

Each mod has its own `*.csproj`. From inside the mod's folder:

```powershell
dotnet build -c Release
```

If Schedule I is installed somewhere other than the default
`C:\Games\Steam\steamapps\common\Schedule I`:

```powershell
dotnet build -c Release -p:GameDir="D:\Games\Schedule I"
```

The output `*.dll` goes in `bin/Release/net6.0/`. Copy it to your
Schedule I `Mods/` folder.

## Status

| Mod | Version | Verified |
| --- | ------- | -------- |
| EmployeeReset | 0.1.0 | Symptom A (save/load NRE + stuck workers) verified fixed in one test. Symptom B (mid-cook wedge). Ingredient-gate fix built; awaiting in-game verification. |
