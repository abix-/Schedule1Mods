# EmployeeReset

A Schedule 1 sidecar mod that adds a comprehensive smart-reset for stuck
employees. Targets the failure mode where the eMployee mod's AUTO-RESET
fires repeatedly without recovering the worker, and the recurring
`Il2CppException at StartMixingStationBehaviour+CookRoutine.MoveNext`
NRE on save/load.

This mod does NOT replace eMployee. It runs alongside, providing:

1. A hotkey (default F8) that runs the smart reset on every stuck employee
   on the current property.
2. A Harmony postfix on `eMployeeMod.ResetEmployeeCore` so eMployee's
   existing AUTO-RESET path also gains the comprehensive cleanup.

The mod also works standalone, without eMployee installed. The postfix
hook silently no-ops if eMployee is not loaded; the hotkey path still
works.

## What smart-reset actually does

For each affected employee, in order:

1. Zeroes `Employee.consecutivePathingFailures` via the typed property
   setter. Note: eMployee's reset uses `GetField` for this, which returns
   null on the il2cpp bridge type, so eMployee's counter-zero is a silent
   no-op. We use the typed setter and it actually works.
2. If the active behaviour is a chemist work behaviour
   (`StartMixingStationBehaviour`):
   - Calls `cook.StopCook()` -- vanilla's intended end-of-cook hook.
   - Calls `cook.targetStation.SetNPCUser(null)` as belt-and-suspenders.
3. Calls `MonoBehaviour.StopAllCoroutines()` on the active behaviour as
   a final guard against walk-routines and other state machines.
4. Nulls `_activeBehaviour_k__BackingField` on the chemist's
   `NPCBehaviour` so the next tick re-evaluates the priority stack.
5. Calls `NavMeshAgent.ResetPath()` to clear any stale destination.
6. Calls `NPCBehaviour.SortBehaviourStack()` to force re-evaluation so a
   fallback behaviour can take over while broken cook conditions remain
   unsatisfied.

See `../docs/employee-mod-bug-analysis.md` for the full root-cause
analysis.

## Requirements

- Schedule 1 v0.4.5f2 or compatible.
- MelonLoader 0.7.0+.
- .NET 6 SDK to build.
- (Optional) eMployee v2.2.x for the AUTO-RESET postfix path.

## Build

```powershell
dotnet build -c Release
```

If your game install is somewhere other than the default
`C:\Games\Steam\steamapps\common\Schedule I`:

```powershell
dotnet build -c Release -p:GameDir="D:\Games\Schedule I"
```

Output: `bin/Release/net6.0/EmployeeReset.dll`. Copy that file into
`<Schedule I>\Mods\`.

## Configuration

Exposed via MelonPreferences (file: `<Schedule I>\UserData\MelonPreferences.cfg`):

| Key | Default | Effect |
| --- | ------- | ------ |
| `EmployeeReset.Hotkey` | `F8` | Key to trigger smart reset on all stuck employees on the current property |
| `EmployeeReset.HookEMployeeAutoReset` | `true` | If true, also runs SmartReset as a Harmony postfix after `eMployeeMod.ResetEmployeeCore` |
| `EmployeeReset.VerboseLog` | `true` | If true, log every reset step; if false, log only the start/end summary |

## Status

| Aspect | Status |
| ------ | ------ |
| Compiled | Yes |
| Symptom A (save/load NRE + stuck workers) | Verified fixed in one in-game test |
| Symptom B (mid-cook ingredient exhaustion wedge) | Hypothesised fixed, not directly tested |

Field names referenced by the mod, all verified against
`Assembly-CSharp.dll` from Schedule I 0.4.5f2:

- `Il2CppScheduleOne.Employees.Employee.consecutivePathingFailures` (property)
- `Il2CppScheduleOne.NPCs.NPC.Behaviour` (property -> NPCBehaviour)
- `Il2CppScheduleOne.NPCs.Behaviour.NPCBehaviour.activeBehaviour` (property)
- `Il2CppScheduleOne.NPCs.Behaviour.NPCBehaviour.SortBehaviourStack()` (method)
- `Il2CppScheduleOne.NPCs.Behaviour.StartMixingStationBehaviour.targetStation` (property)
- `Il2CppScheduleOne.NPCs.Behaviour.StartMixingStationBehaviour.StopCook()` (method)
- `Il2CppScheduleOne.ObjectScripts.MixingStation.SetNPCUser(NetworkObject)` (method)
