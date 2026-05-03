# Schedule1Mods

Mods and supporting analysis for [Schedule I](https://store.steampowered.com/app/3164500/) (TVGS, IL2CPP / MelonLoader).

## What's here

| Path | Contents |
| ---- | -------- |
| `EmployeeReset/` | A MelonLoader sidecar mod that adds a comprehensive smart-reset for stuck employees. Targets bugs where the eMployee mod's AUTO-RESET fires repeatedly without recovering the worker. Status: built, **save/load symptom verified fixed in one in-game test**, mid-cook ingredient-exhaustion symptom hypothesised but not directly tested. |
| `docs/employee-mod-bug-analysis.md` | Full root-cause analysis of two stuck-chemist bug classes, with line-cited references into both `eMployee.dll` v2.2.4 and Schedule I 0.4.5f2 game assemblies, plus a proposed upstream patch for the eMployee mod author. |

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

The output `*.dll` goes in `bin/Release/net6.0/` -- copy it to your
Schedule I `Mods/` folder.

## Status

| Mod | Version | Verified |
| --- | ------- | -------- |
| EmployeeReset | 0.1.0 | Symptom A (save/load NRE + stuck workers) verified fixed in one test. Symptom B (mid-cook wedge) untested. |
