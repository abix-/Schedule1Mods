# EmployeeReset

A Schedule 1 sidecar mod that fixes two chemist bugs caused by vanilla
game logic.

## Contents

- [What this mod does for you](#what-this-mod-does-for-you)
- [What you'll see in-game](#what-youll-see-in-game)
- [What this mod does NOT do](#what-this-mod-does-not-do)
- [Install](#install)
- [Configuration](#configuration)
- [Build from source](#build-from-source)
- [Technical: ingredient gate](#technical-ingredient-gate)
- [Technical: smart-reset (F8)](#technical-smart-reset-f8)
- [Technical: eMployee postfix](#technical-employee-postfix)
- [Status](#status)
- [Verified game APIs](#verified-game-apis)
- [Related docs](#related-docs)

## What this mod does for you

You play Schedule 1 with chemists. Two things keep going wrong:

1. **Your chemist gets stuck "mixing" at the table when ingredients run
   out.** The mixing animation keeps playing but no progress happens.
   The MelonLoader log fills with `NullReferenceException` errors. The
   chemist never recovers on their own. Even firing and rehiring doesn't
   help -- the new chemist immediately gets stuck the same way.
2. **The MelonLoader log spams `NullReferenceException` from
   `CookRoutine.MoveNext`** every ~30 seconds while a chemist is stuck,
   especially after loading a save.

This mod prevents both. After installing:

- **Chemists stop trying to mix at empty stations.** When the input
  slots run out of items, the chemist immediately stops, walks away,
  goes idle. They will resume work automatically the moment the player
  restocks the slots.
- **The recurring NRE goes away.** No more red error spam in the log.
- **Already-stuck chemists** can be unwedged with a single F8 press.
  After that they go idle. They stay idle until they have ingredients
  to actually work with.

You do not need to micromanage the chemist. You do not need to set
schedules or cooldowns. The chemist's behaviour now responds to the
real state of the world (slots empty -> stop, slots loaded -> work).

## What you'll see in-game

| Situation | What happens | Log |
| --------- | ------------ | --- |
| Healthy chemist with full input slots | Cooks normally. No change from vanilla. | nothing from us |
| Chemist's input slots become empty mid-cook | Chemist exits the mixing pose, walks away, goes idle. | `[IngredientGate] BLOCK on 'Mixing Station': slot N empty` |
| Player restocks the station's input slots | Chemist returns to the station and resumes mixing within ~1 second. | nothing |
| You load a save with an already-wedged chemist | Old NRE may fire once for the old wedged coroutine. After that, no new cooks start (gate blocks them). | the existing NRE may appear once |
| You press F8 with a wedged chemist | Chemist exits mixing pose immediately. Goes idle. The wedged coroutine is killed. | a sequence of `[Reset] {chemist}: ...` lines, then `[Reset] hotkey: scanned 4 (skipped 3 non-chemist), reset 1` |
| eMployee fires its AUTO-RESET on a chemist | eMployee's reset runs as before, then our postfix runs the comprehensive cleanup on top. | eMployee's `[STUCK-DIAG]` followed by `[EmployeeReset] [Reset] ...` lines |

## What this mod does NOT do

- It does not modify the recipe, the items in storage, or the station's
  configuration. The chemist's setup is preserved.
- It does not affect botanists, packagers, or cleaners. F8 only resets
  chemists. The ingredient gate only patches `StartMixingStationBehaviour`.
- It does not have a cooldown or timer. The chemist's status is recomputed
  every behaviour-evaluation tick from real world state.
- It does not require eMployee. If eMployee is installed, the postfix
  hook adds our cleanup to eMployee's AUTO-RESET. If not, the gate and
  the F8 hotkey both still work.
- It cannot kill an already-running wedged coroutine without an F8 press.
  The ingredient gate prevents new wedges; F8 cleans up existing ones.

## Install

1. Install MelonLoader 0.7.0+ for Schedule 1.
2. Build this mod (see below) or grab the prebuilt DLL.
3. Copy `EmployeeReset.dll` into `<Schedule I>\Mods\`.
4. Launch the game. The mod loads alongside any other MelonLoader mods.

## Configuration

Edit `<Schedule I>\UserData\MelonPreferences.cfg` after first launch
(file is created on first run):

| Key | Default | Effect |
| --- | ------- | ------ |
| `EmployeeReset.Hotkey` | `F8` | Key to smart-reset every chemist on the current property |
| `EmployeeReset.HookEMployeeAutoReset` | `true` | Also run SmartReset as a Harmony postfix after `eMployeeMod.ResetEmployeeCore` (no-op if eMployee absent) |
| `EmployeeReset.VerboseLog` | `true` | Log every reset step and ingredient-gate block. Turn off for production |

## Build from source

```powershell
cd EmployeeReset
dotnet build -c Release
```

If Schedule 1 is installed somewhere other than
`C:\Games\Steam\steamapps\common\Schedule I`:

```powershell
dotnet build -c Release -p:GameDir="D:\Games\Schedule I"
```

Output is `bin/Release/net6.0/EmployeeReset.dll`. Copy that file to
`<Schedule I>\Mods\`.

Requirements:

- Schedule 1 v0.4.5f2 or compatible.
- MelonLoader 0.7.0+.
- .NET 6 SDK to build.
- (Optional) eMployee v2.2.x for the AUTO-RESET postfix path.

## Technical: ingredient gate

Vanilla's mixing station has two named slots that hold the inputs:
`MixingStation.ProductSlot` and `MixingStation.MixerSlot`. The
canonical "can I mix right now?" check is
`MixingStation.GetMixQuantity()`, which returns
`Mathf.Min(ProductSlot.Quantity, MixerSlot.Quantity, MaxMixQuantity)`.
If either slot is empty, GetMixQuantity returns 0.

Despite this, vanilla's `MixingStation.CanStartMix` and
`StartMixingStationBehaviour.CanCookStart` apparently still return true
in cases where they should not -- the chemist starts a cook, immediately
wedges on an empty slot, and stays wedged.

We install two Harmony postfixes:

1. **`MixingStation.CanStartMix`** -- the canonical patch, matching the
   predicate the [ProduceMore mod](https://thunderstore.io/c/schedule-i/p/lasersquid/ProduceMoreMono/source/)
   uses. Override `true` to `false` when `GetMixQuantity() <= 0`.
2. **`StartMixingStationBehaviour.CanCookStart`** -- belt to the above
   suspenders. Same predicate, in case some vanilla code path calls
   CanCookStart directly without going through CanStartMix.

Both postfixes only ever flip `true` to `false`. We never override
vanilla's `false`. The chemist can never become more eager because of
us, only more conservative.

Each check is O(1) -- a single call into `GetMixQuantity()`. No caching
needed.

## Technical: smart-reset (F8)

For each chemist (other roles are skipped on F8), in order:

1. Zero `Employee.consecutivePathingFailures` via the typed property
   setter. Note: eMployee's reset uses `GetField` for this, which
   returns null on the il2cpp bridge type, so eMployee's counter-zero
   is a silent no-op. We use the typed setter and it actually works.
2. Reach the chemist's `StartMixingStationBehaviour` directly via
   `Chemist.StartMixingStationBehaviour` (typed access, line 530 of
   decompiled Chemist). This catches wedged coroutines even when
   eMployee already nulled `_activeBehaviour`. On that component:
   - `StopCook()` -- vanilla's intended end-of-cook hook.
   - `targetStation.SetNPCUser(null)` -- release station reservation.
   - `targetStation.CurrentMixOperation = null` -- clear in-progress mix.
   - `StopAllCoroutines()` -- final guard against running coroutines.
   - `Deactivate()` -- vanilla's stop-being-active cleanup hook.
   - **Note (iteration 12):** we previously nulled
     `_targetStation_k__BackingField` here, but that broke the
     chemist's "move output" task. Removed -- the canonical
     `MixingStation.CanStartMix` gate now prevents new wedges
     without needing to disconnect the chemist from the station.
3. If the active behaviour is `StartMixingStationBehaviour`, run the
   same operations on the active reference (belt-and-suspenders).
4. HAMMER `StopAllCoroutines()` on every MonoBehaviour on the chemist's
   GameObject. The wedged coroutine may be hosted on Chemist, Employee,
   or NPC base, not just on `StartMixingStationBehaviour`.
5. Null `_activeBehaviour_k__BackingField` on `NPCBehaviour`.
6. `NavMeshAgent.ResetPath()`.
7. `NPCBehaviour.SortBehaviourStack()`.

## Technical: eMployee postfix

When eMployee fires its AUTO-RESET via `eMployeeMod.ResetEmployeeCore`,
our SmartReset runs immediately after as a Harmony postfix
(chemist-only). This means eMployee's existing watchdog gains the
comprehensive cleanup without us forking eMployee's source.

The hook silently no-ops if eMployee is not loaded.

## Status

| Aspect | Status |
| ------ | ------ |
| Compiled | Yes |
| Symptom A (save/load NRE + stuck workers) | Verified fixed in one in-game test |
| Symptom B (mid-cook ingredient exhaustion wedge) | Ingredient gate built; pending in-game verification |

## Verified game APIs

All field/method names verified against `Assembly-CSharp.dll` from
Schedule I 0.4.5f2:

- `Il2CppScheduleOne.Employees.Employee.consecutivePathingFailures` (property)
- `Il2CppScheduleOne.Employees.Chemist.StartMixingStationBehaviour` (typed property)
- `Il2CppScheduleOne.NPCs.NPC.Behaviour` (property -> NPCBehaviour)
- `Il2CppScheduleOne.NPCs.Behaviour.NPCBehaviour.activeBehaviour` (property)
- `Il2CppScheduleOne.NPCs.Behaviour.NPCBehaviour.SortBehaviourStack()` (method)
- `Il2CppScheduleOne.NPCs.Behaviour.StartMixingStationBehaviour.targetStation` (property)
- `Il2CppScheduleOne.NPCs.Behaviour.StartMixingStationBehaviour.StopCook()` (method)
- `Il2CppScheduleOne.NPCs.Behaviour.StartMixingStationBehaviour.Deactivate()` (override of base virtual)
- `Il2CppScheduleOne.NPCs.Behaviour.StartMixingStationBehaviour.CanCookStart()` (method, secondary ingredient-gate target)
- `Il2CppScheduleOne.ObjectScripts.MixingStation.CanStartMix` (method, primary ingredient-gate target)
- `Il2CppScheduleOne.ObjectScripts.MixingStation.GetMixQuantity()` (method, returns `min(ProductSlot.Quantity, MixerSlot.Quantity, MaxMixQuantity)`)
- `Il2CppScheduleOne.ObjectScripts.MixingStation.ProductSlot` (property -> ItemSlot)
- `Il2CppScheduleOne.ObjectScripts.MixingStation.MixerSlot` (property -> ItemSlot)
- `Il2CppScheduleOne.ObjectScripts.MixingStation.OutputSlot` (property -> ItemSlot)
- `Il2CppScheduleOne.ObjectScripts.MixingStation.NPCUserObject` (property -> NetworkObject)
- `Il2CppScheduleOne.ObjectScripts.MixingStation.CurrentMixOperation` (property -> MixOperation)
- `Il2CppScheduleOne.ObjectScripts.MixingStation.SetNPCUser(NetworkObject)` (method)
- `Il2CppScheduleOne.ItemFramework.ItemSlot.Quantity` (property)

Mixing-station API confirmed against the [ProduceMore mod's
decompiled source](https://thunderstore.io/c/schedule-i/p/lasersquid/ProduceMoreMono/source/) on Thunderstore.

## Related docs

- [`../docs/employee-mod-bug-analysis.md`](../docs/employee-mod-bug-analysis.md) -- root cause analysis of both bugs, line-cited references, proposed upstream patch for the eMployee mod author
- [`../docs/ingredient-gate-fix-plan.md`](../docs/ingredient-gate-fix-plan.md) -- the implementation plan for the ingredient gate, including Phase 1 reconnaissance findings
