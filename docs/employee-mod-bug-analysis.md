# Schedule 1 -- eMployee Mod Chemist Bug Analysis

## Contents

- [TL;DR](#tldr)
- [Environment](#environment)
- [Reproduction](#reproduction)
- [Observed log slice](#observed-log-slice)
- [Root cause](#root-cause)
  - Bug 1 -- ResetEmployeeCore does not stop coroutines
  - Bug 2 -- MixingCoroutineMoveNextPrefix
  - Why save/load is the trigger
  - Bug 3 -- Watchdog reset is too narrow, abandons after 3 retries
  - Underlying cause for Symptom B
- [Affected behaviours](#affected-behaviours)
- [Fix proposal](#fix-proposal)
  - Fix 1 -- Stop coroutines and disable behaviour cleanly
  - Fix 2 -- Make the MoveNext prefix safer (or remove it)
  - Field-name resilience
  - Fix 3 -- Expand reset scope
- [For the eMployee mod author: integrating these fixes upstream](#for-the-employee-mod-author-integrating-these-fixes-upstream)
  - The right fix: gate the predicate on ingredient availability
  - What "ingredients" actually means in vanilla
  - Implementation
  - Approaches we rejected
- [Sidecar mod: EmployeeReset](#sidecar-mod-employeereset)
  - Current state
  - How we discovered each layer
  - Diagnostic instrumentation results
  - Root cause (iteration 6 conclusion)
- [Test plan](#test-plan)
- [Workarounds for players (no rebuild)](#workarounds-for-players-no-rebuild)
- [Code references](#code-references)
- [Open questions](#open-questions)
- [References](#references)

Root-cause analysis and fix proposal for two distinct chemist failure modes
caused by the eMployee mod v2.2.4:

- Symptom A: NullReferenceException at
  `StartMixingStationBehaviour+<<StartCook>g__CookRoutine|13_0>d.MoveNext`
  on save/load.
- Symptom B: Chemist runs out of an ingredient mid-cook, gets stuck at the
  MixingStation in the "mixing" state, and never exits. AUTO-RESET fires
  three times then "gives up", chemist remains wedged. Underlying cause:
  vanilla `CookRoutine` yields on a condition (next ingredient available)
  that becomes unreachable mid-cook, and eMployee's reset does not call
  `StopCook()` to force the coroutine to exit cleanly. Also reproducible
  after fire/rehire when the new chemist inherits a cook with stale
  station state, but the more common in-play trigger is ingredient
  exhaustion.

## TL;DR

> **Status (2026-05-03):** Symptom A (recurring NRE in
> `StartMixingStationBehaviour+CookRoutine.MoveNext` on save/load) is
> **verified fixed** by the EmployeeReset sidecar mod in one in-game test.
> Symptom B (chemist wedged at MixingStation after running out of
> ingredients mid-cook) is hypothesised to share the same root cause and
> the same fix, but has not been directly tested.

Three independent bugs in `eMployee.dll` v2.2.4:

1. `ResetEmployeeCore` nulls the active behaviour reference but never stops
   the running coroutine on the chemist's MonoBehaviour. On save/load the
   orphaned `CookRoutine` resumes and dereferences a stale field, throwing
   NRE. Drives Symptom A.
2. `MixingCoroutineMoveNextPrefix` writes directly into the compiler-generated
   state-machine field `_i_5__5` to skip cook iterations when
   `_chemistSpeedMult > 1.01f`. Compiler-generated field names are unstable
   across Schedule 1 patches and the prefix swallows exceptions, so partial
   writes corrupt state silently. Aggravates Symptom A.
3. The stuck-watchdog's reset action is too narrow: it nulls
   `_activeBehaviour` but does not clear the chemist's queued task,
   `AssignedStation`, or the `MixingStation`'s reservation/recipe state. The
   vanilla behaviour stack immediately re-activates the same broken behaviour
   on the next tick, the chemist re-enters the stuck state, AUTO-RESET fires
   again, and after `AutoResetMaxRetries` (default 3) the chemist is added to
   `_autoResetAbandoned` and reset stops being called. Drives Symptom B.

Both symptoms exist independently. Symptom A also occurs on save/load even at
default speed multiplier 1.0x (Bug 2 disabled). Symptom B can occur without
ever loading a save -- it triggers in normal play after a chemist fires/rehires
or after MixingStation state is left dirty by any prior chemist's broken
session.

## Environment

| Component        | Version |
| ---------------- | ------- |
| Schedule I       | 0.4.5f2 |
| Unity            | 2022.3.62f2 |
| MelonLoader      | 0.7.2 Open-Beta (net6, Il2Cpp x64) |
| eMployee         | 2.2.4 (by V4LEXL) |
| eMployee SHA256  | 9945A3A142FEA27E2314BCB233EBC9D945BA9819EAA2925FA03BB88423B6FFDE |
| Game install     | C:\Games\Steam\steamapps\common\Schedule I |
| Mod path (Vortex)| C:\Users\Abix\AppData\Roaming\Vortex\schedule1\mods\eMployee-1625-2-2-4-1777740757\mods\eMployee.dll |

Other mods active in the affected save (none implicated as cause):
DealerPlus 2.0.4-BETA, DealersRecruitCustomers 1.11.9,
ModManager&PhoneApp 2.2.3, FatStacks 1.9.3, S1API 3.0.3.

## Reproduction

1. Save a colony where at least one chemist has been actively cooking at a
   `MixingStation` (mid-recipe, ingredients reserved or in hand).
2. Quit and reload the save.
3. Observe MelonLoader log within ~2 seconds of save load:
   - eMployee logs `[STUCK-DIAG] ... [AUTO-RESET]` for the chemist immediately
     (`after 0.0s`) with `beh='?' active=False pathStatus=PathComplete`.
   - ~1.6 s later: `Il2CppInterop.Runtime.Il2CppException:
     NullReferenceException at StartMixingStationBehaviour+<<StartCook>g__CookRoutine|13_0>d.MoveNext`.

Two chemists assigned to the same `MixingStation` reproduce more reliably
because both spawn at the same `StorageUnit` slot post-load and only one can
hold the reservation.

## Observed log slice

```
[10:24:36.230] [eMployee] [STUCK-DIAG] Karen Diaz [AUTO-RESET] (Chemist) STUCK at Storage Unit after 0.0s
               beh='?' active=False behPathFails=0 | empPathFails=0 ticks=0 paid=True cross=False
               pos=(-4.8, 0.1, 111.2) nav=hasPath=False stopped=False pathStatus=PathComplete
[10:24:36.231] [eMployee] [STUCK-DIAG] Jason Green [AUTO-RESET] (Chemist) STUCK at Storage Unit after 0.0s
               pos=(-4.8, 0.1, 111.2)   <-- identical coords, both chemists overlapping
[10:24:37.858] Il2CppInterop.Runtime.Il2CppException: NullReferenceException
               at StartMixingStationBehaviour+<<StartCook>g__CookRoutine|13_0>d.MoveNext () [0x00000]
```

## Root cause

### Bug 1 -- `ResetEmployeeCore` does not stop coroutines

File: `eMployee.dll` (decompiled with ilspycmd 9.1.0, single source file
`eMployeeMod.cs` of ~21500 lines).

`ResetEmployeeCore(object emp, bool reapplyPriorities, bool isManual)` at
line 2560 is the AUTO-RESET / MANUAL-RESET worker. Relevant slice:

```csharp
// line 2597-2616
object obj3 = emp.GetType().GetProperty("Behaviour", BindingFlags.Instance | BindingFlags.Public)
                  ?.GetValue(emp);
if (obj3 != null)
{
    object obj4 = obj3.GetType().GetProperty("activeBehaviour", BindingFlags.Instance | BindingFlags.Public)
                      ?.GetValue(obj3);
    if (obj4 != null)
    {
        FieldInfo field2 = obj4.GetType().GetField("consecutivePathingFailures",
                                BindingFlags.Instance | BindingFlags.Public);
        if (field2 != null) { field2.SetValue(obj4, 0); num++; }
    }
    FieldInfo field3 = obj3.GetType().GetField("_activeBehaviour_k__BackingField",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (field3 != null)
    {
        field3.SetValue(obj3, null);   // <-- nulls active behaviour reference
        num++;
    }
}
```

Followed by `NavMeshAgent.ResetPath()` and bookkeeping. Reset does NOT:

- call `Disable()` on the active behaviour (vanilla cleanup hook)
- call `End()` on the behaviour
- call `MonoBehaviour.StopAllCoroutines()` on the chemist GameObject
- call `StopCoroutine` on the specific `CookRoutine` IEnumerator handle

Consequence: `StartMixingStationBehaviour` is a Unity `MonoBehaviour` attached
to the chemist GameObject. Its `CookRoutine` coroutine is registered with
Unity's coroutine scheduler, which is independent of the
`NPCBehaviour._activeBehaviour` reference. Nulling that reference does not
remove the coroutine from the scheduler.

When the coroutine resumes on the next frame tick (~1.6 s after AUTO-RESET in
the observed log), it executes the next `MoveNext` step. That step
dereferences a captured field whose target was either:

- left stale by save/load (Unity-destroyed object that is `==null` but the
  managed reference still exists), or
- was being maintained by the now-detached `_activeBehaviour` setter logic.

Result: NRE at `MoveNext () [0x00000]`. The IL offset in il2cpp is unreliable
but the throw is on the first captured-field deref inside the state machine.

### Bug 2 -- `MixingCoroutineMoveNextPrefix` writes into compiler-generated state machine fields

`MixingCoroutineMoveNextPrefix(object __instance)` at line 4668 is a Harmony
prefix patch on the `CookRoutine.MoveNext` method, registered at line 4401-4402:

```csharp
// line 4399-4406
if (type6.FullName.Contains("StartMixingStationBehaviour")
    && type6.GetProperty("_i_5__5", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null)
{
    HarmonyMethod val4 = new HarmonyMethod(typeof(eMployeeMod).GetMethod("MixingCoroutineMoveNextPrefix",
                                            BindingFlags.Static | BindingFlags.NonPublic));
    ((MelonBase)this).HarmonyInstance.Patch((MethodBase)method3, val4, ...);
}
```

Prefix body:

```csharp
// line 4668-4709
private static void MixingCoroutineMoveNextPrefix(object __instance)
{
    if (_chemistSpeedMult <= 1.01f) return;     // no-op at default 1.0x
    try
    {
        Type type = __instance.GetType();
        PropertyInfo property = type.GetProperty("__1__state",
                                  BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null || (int)property.GetValue(__instance) != 2) return;

        PropertyInfo property2 = type.GetProperty("_i_5__5", ...);            // captured int i
        PropertyInfo property3 = type.GetProperty("_mixQuantity_5__4", ...);  // captured int mixQuantity
        if (property2 == null || property3 == null) return;

        int num = (int)property2.GetValue(__instance);
        int num2 = (int)property3.GetValue(__instance);
        int num3 = Mathf.RoundToInt(_chemistSpeedMult) - 1;
        int num4 = Math.Min(num + num3, num2);
        property2.SetValue(__instance, num4);                                  // FORCE-ADVANCE i
    }
    catch (Exception ex)
    {
        if (_debugLog) MelonLogger.Msg("[eMployee] [COROUTINE-SKIP-ERR] " + ex.Message);
    }
}
```

Three problems with this approach:

1. The field names `_i_5__5` and `_mixQuantity_5__4` are C# closure-display-class
   field names. The `_5` suffix is a scope index assigned by the compiler at
   build time. Any Schedule 1 update that re-emits `StartCook` with a different
   captured-variable layout silently breaks the patch (the registration check
   at line 4399 prevents the patch from being applied if the fields are gone,
   but does not protect against a different field with the same name being
   wired to a different local).
2. Force-advancing `i` skips the body of the cook loop. Inside that body,
   vanilla code is responsible for reacquiring or revalidating per-iteration
   state -- including the active `MixingStation` reference and ingredient
   handles. Skipping iterations leaves those references in a state the rest of
   the coroutine does not expect.
3. The `try { ... } catch { log }` swallows all reflection failures. A partial
   write (read `i` succeeded, write failed mid-call) leaves the state machine
   in a torn state with no log unless `_debugLog` is on.

Default value `_chemistSpeedMult = 1f` (line 614) makes the prefix a no-op for
players who never touch the speed slider. Players who set it >= 2 are running
the patched path.

### Why save/load is the trigger

`StartMixingStationBehaviour` keeps its operating context as captured locals
inside `CookRoutine`'s state machine (the `<<StartCook>g__CookRoutine|13_0>d`
class). When the save serializer writes a chemist mid-cook, those captured
references go to disk. On load:

- The MixingStation GameObject is rebuilt with a new instance ID.
- Unity's serialization wiring may not re-link every captured `Object`
  reference inside the state machine -- the C# managed reference is restored
  but the underlying Il2Cpp pointer is `IntPtr.Zero` or stale, so Unity's
  overloaded `==` operator reports the field as `null` while a regular
  `ReferenceEquals(field, null)` would return false.
- eMployee's stuck scanner runs at first NPC tick post-load, sees
  `beh='?' active=False pathStatus=PathComplete`, decides STUCK, fires
  AUTO-RESET, nulls `_activeBehaviour`, but the orphaned coroutine is still
  scheduled.
- Coroutine resumes, hits the first `Unity == null` deref or the prefix
  force-advance lands it in a code path that assumes a valid station, NRE.

### Bug 3 -- Watchdog reset is too narrow, abandons after 3 retries

This is the cause of Symptom B (chemist re-stuck after fire/rehire, "reset
isn't working").

The stuck watchdog at line 3290-3429 detects two stuck conditions:

```csharp
// line 3398-3399
bool flag  = num3 >= num && ticksSinceLastWork >= 30;   // GAME stuck: same beh too long, no productive ticks
bool flag2 = num4 >= num;                                // PHYS stuck: hasn't moved in too long
```

Where `num` is the configured stuck-threshold seconds, `num3` is time on the
current behaviour pointer, `num4` is time since `transform.position` last
moved by > 0.1 m, and `ticksSinceLastWork` is the chemist's own counter that
the vanilla game increments only when the NPC successfully completes a unit
of work.

When `flag` (GAME stuck) is true the watchdog enters this block:

```csharp
// line 3408-3424
if (!_autoResetAbandoned.Contains(text) && flag)
{
    int num5 = _autoResetTries.TryGetValue(text, out value4) ? value4 : 0;
    if (num5 < _autoResetMaxRetries)            // default 3
    {
        _autoResetTries[text] = num5 + 1;
        MelonLogger.Msg("[eMployee] [WATCHDOG] {text}: gameStuck -> reset (try {num5+1}/3)");
        ResetEmployeeCore(cachedEmployee);
        _activeBehTrack.Remove(text);
        _stuckDiagLogged.Remove(text);
        _lastMovedAt[text] = (val3, realtimeSinceStartup);
        return;
    }
    _autoResetAbandoned.Add(text);
    MelonLogger.Msg("[eMployee] [WATCHDOG] {text}: gave up after {num5} auto-resets, awaiting manual reset");
}
```

What `ResetEmployeeCore` actually does (recap from Bug 1):

- Zeros `consecutivePathingFailures` on NPC and on `activeBehaviour`.
- Nulls `_activeBehaviour_k__BackingField` on the `NPCBehaviour`.
- Calls `NavMeshAgent.ResetPath()`.

What it does NOT do:

- Touch the chemist's queued behaviours (the `NPCBehaviour` priority stack
  still has `StartMixingStationBehaviour` on it).
- Clear the chemist's `AssignedStation` field.
- Clear the chemist's current `WorkOrder` / pending `Recipe`.
- Touch the `MixingStation` itself: its `IsBeingUsed` flag, current cook
  recipe, slot reservations, and "reserved by" pointer are untouched.

Result is a self-healing-failed loop:

1. Chemist enters `StartMixingStationBehaviour` (active=True, real beh name).
2. Vanilla coroutine cannot make progress (suspected cause: stale state on
   the assigned `MixingStation` or on the chemist's task queue, see "Suspected
   underlying cause" below). `TicksSinceLastWork` keeps incrementing.
3. After ~stuck-threshold seconds, watchdog detects GAME stuck, calls
   `ResetEmployeeCore`. Active behaviour goes null for one tick.
4. Vanilla NPCBehaviour stack re-evaluates next tick. Same queued
   `StartMixingStationBehaviour` is the highest-priority behaviour with
   conditions met, so it re-activates. Same broken state.
5. Steps 2-4 repeat. After `_autoResetMaxRetries` = 3 cycles, the chemist's
   name is added to `_autoResetAbandoned`.
6. From this point, the watchdog still computes `flag=true` and still calls
   `LogStuckDiagnostic` (line 3406) on every tick the diagnostic is not
   already deduped, but the gating `!_autoResetAbandoned.Contains(text)`
   blocks the reset call. The user sees `[STUCK-DIAG]` lines forever and no
   recovery.

The abandoned flag is only cleared in two places:

- `ResetEmployeeCore` line 2651-2655 when called with `isManual=true` (i.e.
  the user clicks Reset in the eMployee phone app).
- Implicitly when the chemist's tracking key (their full name) changes -- so
  firing and rehiring with a different name resets tracking, but the new
  chemist still walks into the same broken `MixingStation` and the cycle
  starts over.

### Underlying cause for Symptom B

The user-reported in-play scenario:

> "The chemist runs out of ingredients to continue mixing. The chemist
> continues to be stuck in the mixing state and never stops 'mixing'.
> I want the chemist to stop mixing at the table and return to a 'ready'
> state so normal logic can make it continue working."

This is a wedged-coroutine bug. The mechanism:

1. Chemist is at the `MixingStation` with `StartMixingStationBehaviour`
   active. `CookRoutine` is mid-loop (state machine state 2, the cook
   inner loop).
2. The recipe needs N ingredient units. Mid-loop, the next ingredient
   becomes unreachable -- storage drained by another worker, ingredient
   moved, slot locked.
3. Vanilla `CookRoutine` yields waiting for the next ingredient. The
   yield condition never becomes true.
4. The chemist stays at the table in the "mixing" pose. `Started == true`,
   `_activeBehaviour == StartMixingStationBehaviour`, animation playing,
   no progress.
5. eMployee watchdog observes `TicksSinceLastWork >= 30`, fires AUTO-RESET.
6. `ResetEmployeeCore` nulls `_activeBehaviour_k__BackingField` but does
   NOT call `StopCook()` and does NOT call `MonoBehaviour.StopAllCoroutines()`
   on the behaviour. The wedged coroutine remains scheduled with Unity.
7. On the next NPC tick, vanilla behaviour selector re-evaluates
   priorities. `StartMixingStationBehaviour` still has `targetStation`
   set, the station's `NPCUserObject` still points at this chemist (no
   one cleared it), `CanCookStart()` returns true, the behaviour
   re-activates. The coroutine is still wedged -- vanilla just attached
   `_activeBehaviour` back to it.
8. Steps 5-7 loop. After `_autoResetMaxRetries = 3` cycles, eMployee
   adds the chemist to `_autoResetAbandoned` and stops resetting.

The wedged coroutine is the proximate cause. The cleanup gap is that
`ResetEmployeeCore` never calls the vanilla "stop the cook session" API
that exists for exactly this case: `StartMixingStationBehaviour.StopCook()`
(Assembly-CSharp.dll line 541).

Calling `StopCook()` on a wedged cook is *presumed* to:

- Flip whatever flag `CookRoutine` reads at each yield to decide whether
  to keep iterating. The next yield resolves with "exit", the coroutine
  exits cleanly.
- Release `MixingStation.NPCUserObject` (the "currently in use by" slot).
- Clear `MixingStation.CurrentMixOperation` (the in-progress cook).
- Return the chemist to a state where vanilla's `CanCookStart()`
  predicate evaluates the *current* world (no ingredients) instead of
  the stale "I have a cook in progress" condition.

After `StopCook()`, the next behaviour evaluation should pick a different
behaviour (idle, walk-to-rest-point, or "go fetch ingredients" if any
exist anywhere) because `StartMixingStationBehaviour.CanCookStart()` will
return false. The chemist returns to a ready state, normal vanilla logic
resumes.

> **Verification still needed.** The body of `StopCook()` is in il2cpp
> native code and unreadable from Cpp2IL output. We have not run the
> patched mod against a live save with this exact symptom. Every claim
> about what `StopCook` does is inferred from its name and from the
> existence of its `StartCook` counterpart at line 518. If `StopCook`
> turns out to be a no-op or to require additional preconditions, the
> patch needs revision.

Other contributing factors that the previous version of this section
discussed (fire/rehire-induced stale `NPCUserObject`, dangling
`AssignedStation`) are real and worth fixing, but they are secondary to
the wedged-coroutine case. The fix below addresses both: calling
`StopCook()` handles the wedged-coroutine case, and explicitly nulling
`NPCUserObject` afterward as belt-and-suspenders handles the
fire/rehire-stale-reservation case.

## Affected behaviours

The reset path applies to all employees. The MoveNext prefix applies only
where `_i_5__5` exists. Per line 4445, the chemist-side patches register on:

```
StartMixingStationBehaviour
StartChemistryStationBehaviour
StartCauldronBehaviour
StartLabOvenBehaviour
FinishLabOvenBehaviour
BrickPressBehaviour
```

Plus `PackagingStationBehaviour`, `GrowContainer`, `BagTrashCan`,
`EmptyTrashGrabber`, `DisposeTrashBag`, `PickUpTrash` from the postfix path
(line 4731-4746). Any of those can race AUTO-RESET on save load. The chemist
case is the most reproducible because chemists run the longest cook loops.

## Fix proposal

Two fixes, applied independently. Fix 1 is mandatory; Fix 2 is recommended for
players who use the speed multiplier.

### Fix 1 -- Stop coroutines and disable behaviour cleanly in `ResetEmployeeCore`

File: source equivalent of decompiled `eMployeeMod.cs` line 2597-2616.

Replace the active-behaviour-nulling block with:

```csharp
// === FIX 1 START ===
object obj3 = emp.GetType().GetProperty("Behaviour", BindingFlags.Instance | BindingFlags.Public)
                  ?.GetValue(emp);
if (obj3 != null)
{
    object obj4 = obj3.GetType().GetProperty("activeBehaviour", BindingFlags.Instance | BindingFlags.Public)
                      ?.GetValue(obj3);
    if (obj4 != null)
    {
        // 1. Reset the pathing-failure counter (existing behaviour).
        FieldInfo field2 = obj4.GetType().GetField("consecutivePathingFailures",
                                BindingFlags.Instance | BindingFlags.Public);
        if (field2 != null) { field2.SetValue(obj4, 0); num++; }

        // 2. NEW: stop any in-flight coroutines on the behaviour MonoBehaviour
        //    BEFORE we detach it from the NPC. This is what kills CookRoutine
        //    cleanly so MoveNext cannot resume against torn state.
        try
        {
            MonoBehaviour mb = ((Il2CppObjectBase)obj4).TryCast<MonoBehaviour>();
            if ((Object)(object)mb != (Object)null)
            {
                mb.StopAllCoroutines();
                num++;
            }
        }
        catch (Exception ex)
        {
            if (_debugLog) MelonLogger.Warning("[eMployee] StopAllCoroutines failed: " + ex.Message);
        }

        // 3. NEW: invoke the behaviour's vanilla Disable() / End() if present
        //    so any other cleanup (reservation release, item drop) runs.
        try
        {
            MethodInfo disable = obj4.GetType().GetMethod("Disable",
                                    BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
                              ?? obj4.GetType().GetMethod("End",
                                    BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (disable != null) { disable.Invoke(obj4, null); num++; }
        }
        catch (Exception ex)
        {
            if (_debugLog) MelonLogger.Warning("[eMployee] Disable/End failed: " + ex.Message);
        }
    }

    // 4. Existing: null the active-behaviour backing field.
    FieldInfo field3 = obj3.GetType().GetField("_activeBehaviour_k__BackingField",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (field3 != null)
    {
        field3.SetValue(obj3, null);
        num++;
    }
}
// === FIX 1 END ===
```

Notes for the implementer:

- `Il2CppObjectBase.TryCast<MonoBehaviour>()` is the Il2CppInterop helper for
  bridging il2cpp objects to mono types. The mod already uses `TryCast<Employee>`
  at line 2573, so the import is in scope.
- Vanilla `Behaviour` (the Schedule 1 base class, not Unity's
  `UnityEngine.Behaviour`) exposes `Disable()` -- the mod itself calls
  `val6.Disable()` at line 9734, confirming the API exists. If `Disable()` is
  missing, fall back to `End()` and ultimately to the field-null write.
- Order matters: `StopAllCoroutines` first, then `Disable()`, then null the
  field. Reversing means `Disable()` runs without a coroutine to clean up but
  after the field that some `Disable()` implementations read is gone.

### Fix 2 -- Make the MoveNext prefix safer (or remove it)

File: source equivalent of decompiled `eMployeeMod.cs` line 4668-4709.

Recommended option A (safer skip): replace the property-write skip with a
WaitForSeconds shortener. The mod already has
`MelonLogger.Msg("[eMployee] [WFS-SPEED] WaitForSeconds")` at line 4663 doing
exactly that for other behaviours via the il2cpp `WaitForSeconds.ctor` patch
at line 4640-4666. Drop the prefix entirely and rely on the WaitForSeconds
shortener. This removes the compiler-name dependency and eliminates the
torn-state risk.

Concrete change at line 4399-4407: delete the conditional registration of
`MixingCoroutineMoveNextPrefix` so only the `CoroutineMoveNextPostfix` is
applied. Delete the `MixingCoroutineMoveNextPrefix` method (line 4668-4709).

Recommended option B (keep skip with safety net): if the speed boost from
skipping iterations is required (the WaitForSeconds shortener only speeds the
yield gaps, not the loop body work), guard the prefix:

```csharp
// === FIX 2 OPTION B START ===
private static void MixingCoroutineMoveNextPrefix(object __instance)
{
    if (_chemistSpeedMult <= 1.01f) return;
    try
    {
        Type type = __instance.GetType();

        // Guard: only proceed if all required fields exist with expected types.
        PropertyInfo state = type.GetProperty("__1__state", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        PropertyInfo iProp = type.GetProperty("_i_5__5",     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        PropertyInfo qProp = type.GetProperty("_mixQuantity_5__4", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (state == null || iProp == null || qProp == null) return;
        if (state.PropertyType != typeof(int) || iProp.PropertyType != typeof(int) || qProp.PropertyType != typeof(int)) return;
        if ((int)state.GetValue(__instance) != 2) return;

        // Guard: refuse to skip if the captured `this` reference is null/destroyed.
        PropertyInfo thisProp = type.GetProperty("__4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (thisProp != null)
        {
            object capturedThis = thisProp.GetValue(__instance);
            Il2CppObjectBase il2 = capturedThis as Il2CppObjectBase;
            if (il2 == null || il2.Pointer == IntPtr.Zero) return;
        }

        int i  = (int)iProp.GetValue(__instance);
        int q  = (int)qProp.GetValue(__instance);
        int sk = Mathf.RoundToInt(_chemistSpeedMult) - 1;
        int newI = Math.Min(i + sk, q);
        if (newI <= i) return;     // never write a non-progressing or backwards value
        iProp.SetValue(__instance, newI);
    }
    catch (Exception ex)
    {
        // Surface, do not swallow silently. A failure here is a real bug.
        MelonLogger.Warning("[eMployee] [COROUTINE-SKIP-ERR] " + ex.Message);
    }
}
// === FIX 2 OPTION B END ===
```

Changes versus the existing prefix:

- Type-check each property to avoid casting an unrelated `_i_5__5` to `int`.
- Verify the captured `this` is a valid il2cpp pointer; if it is null/zero,
  the cook is already broken and skipping iterations would corrupt further.
- Refuse a no-progress write (`newI <= i`) so a failed `Mathf.RoundToInt`
  cannot cause a regression.
- Surface exceptions at warning level instead of swallowing -- the mod will
  log if a future Schedule 1 patch breaks the field names.

### Field-name resilience (nice-to-have)

The `_i_5__5` and `_mixQuantity_5__4` literals will rot. A small helper that
scans the state-machine type for fields by Mono.Cecil-style heuristics
(integer fields whose names match `_i_\d+__\d+`) would survive game updates.
Out of scope for the urgent fix.

### Fix 3 -- Expand reset scope and replace abandonment with escalation

This fix targets Symptom B (post-fire chemist re-stuck loop). The change
lives in `ResetEmployeeCore` (line 2560-2669) and the watchdog block (line
3408-3424). It is independent of Fixes 1 and 2 but stacks cleanly on top of
Fix 1.

**Part A: widen ResetEmployeeCore to clear queued task and station state.**

After the existing null-of-`_activeBehaviour` write, add three more cleanup
steps. Pseudocode (use the same reflection style as the surrounding code):

```csharp
// === FIX 3 PART A START ===  (insert after the existing field3.SetValue(obj3, null) block, ~line 2616)

// 5. NEW: clear the chemist's AssignedStation reference.
//    This is the chemist's pointer to the MixingStation it is assigned to.
//    Vanilla will pick up a fresh assignment on the next behaviour evaluation.
try
{
    PropertyInfo assignedProp = emp.GetType().GetProperty("AssignedStation",
                                    BindingFlags.Instance | BindingFlags.Public);
    if (assignedProp != null && assignedProp.CanWrite)
    {
        object station = assignedProp.GetValue(emp);
        assignedProp.SetValue(emp, null);
        num++;

        // 6. NEW: release the station's "in use" / reservation state so the
        //    next chemist (or this same chemist) can claim it cleanly.
        //    Field name is a guess; confirm against game decompile.
        if (station != null)
        {
            FieldInfo inUse = station.GetType().GetField("IsBeingUsed",
                                  BindingFlags.Instance | BindingFlags.Public);
            if (inUse != null) { inUse.SetValue(station, false); num++; }

            // Release the "reserved by" pointer if the station tracks it.
            FieldInfo reservedBy = station.GetType().GetField("CurrentEmployee",
                                       BindingFlags.Instance | BindingFlags.Public);
            if (reservedBy != null) { reservedBy.SetValue(station, null); num++; }
        }
    }
}
catch (Exception ex)
{
    if (_debugLog) MelonLogger.Warning("[eMployee] AssignedStation clear failed: " + ex.Message);
}

// 7. NEW: pop the topmost behaviour from the chemist's stack. The vanilla
//    behaviour stack will re-add it on the next tick if conditions still
//    apply, but popping it forces a re-evaluation of priorities so a
//    fallback behaviour (idle, walk-to-station-area) can take over while
//    the broken cook task waits.
try
{
    object obj3 = emp.GetType().GetProperty("Behaviour", BindingFlags.Instance | BindingFlags.Public)
                     ?.GetValue(emp);
    if (obj3 != null)
    {
        MethodInfo sortMethod = obj3.GetType().GetMethod("SortBehaviourStack",
                                    BindingFlags.Instance | BindingFlags.Public);
        if (sortMethod != null) { sortMethod.Invoke(obj3, null); num++; }
    }
}
catch (Exception ex)
{
    if (_debugLog) MelonLogger.Warning("[eMployee] SortBehaviourStack failed: " + ex.Message);
}
// === FIX 3 PART A END ===
```

Field names `IsBeingUsed` and `CurrentEmployee` on `MixingStation` are
guesses based on Schedule 1 conventions; confirm by decompiling
`Il2CppScheduleOne.ObjectScripts.MixingStation` from the game's
`GameAssembly.dll`. The reset uses `try/catch` per step so a wrong field name
fails gracefully without breaking the rest of the reset.

**Part B: replace abandonment with diagnostic-only escalation.**

The existing watchdog gives up after 3 retries. With Part A widening the
reset, retries are more likely to succeed -- but if they don't, surrendering
silently is the wrong policy. Replace abandonment with a slower, noisier
retry cadence and surface the situation to the player.

Replace lines 3408-3424:

```csharp
// === FIX 3 PART B START ===
if (flag)
{
    int num5 = _autoResetTries.TryGetValue(text, out value4) ? value4 : 0;

    // First N tries: aggressive, every detection.
    // After N: throttle to one reset per `EscalationCooldownSeconds`.
    bool allowReset;
    if (num5 < _autoResetMaxRetries)
    {
        allowReset = true;
    }
    else
    {
        float lastResetAt = _lastResetRealTime.TryGetValue(text, out var t) ? t : 0f;
        allowReset = (realtimeSinceStartup - lastResetAt) >= _escalationCooldownSeconds;  // e.g. 60s
    }

    if (allowReset)
    {
        _autoResetTries[text] = num5 + 1;
        _lastResetRealTime[text] = realtimeSinceStartup;
        string mode = (num5 < _autoResetMaxRetries) ? "fast" : "slow";
        MelonLogger.Msg($"[eMployee] [WATCHDOG] {text}: gameStuck -> reset (try {num5+1}, {mode})");
        ResetEmployeeCore(cachedEmployee);
        _activeBehTrack.Remove(text);
        _stuckDiagLogged.Remove(text);
        _lastMovedAt[text] = (val3, realtimeSinceStartup);
        return;
    }

    // Surface the persistent stuck state to the player UI rather than going silent.
    if (!_persistentStuckNotified.Contains(text))
    {
        _persistentStuckNotified.Add(text);
        MelonLogger.Warning($"[eMployee] [WATCHDOG] {text} stuck >{num5} resets; flagging in UI for manual attention");
        // Optional: push a toast / phone notification via the mod's existing UI plumbing.
    }
}
// === FIX 3 PART B END ===
```

This change:

- Removes the hard `_autoResetAbandoned` HashSet path (or keeps it but never
  populates it from the watchdog).
- Continues retrying at a slower cadence (default suggestion: 60 s between
  resets after the fast retries are exhausted), so a transient cause that
  resolves on its own (e.g. another worker frees the storage slot) lets the
  chemist recover without manual intervention.
- Notifies the player UI exactly once per persistent-stuck episode so the
  player knows manual attention is needed.
- Keeps `isManual=true` reset intact -- manual reset still bypasses
  everything and clears tracking.

New private fields needed near line 894-908:

```csharp
private static Dictionary<string, float> _lastResetRealTime = new Dictionary<string, float>();
private static HashSet<string> _persistentStuckNotified = new HashSet<string>();
private static float _escalationCooldownSeconds = 60f;   // expose as a Pref entry
```

Add the cooldown to the prefs system at line 1456 alongside `_autoResetMaxRetriesPref`.

**Part C: make the abandoned flag self-clearing on station change.**

Even with Parts A and B, if a fired chemist's name reappears (rare, but
possible if the player rehires by the same name), the abandoned flag from
their previous incarnation is still in the HashSet. Add an explicit clear
when a new employee is detected -- e.g., in the existing
`OnEmployeeHired` / NPC enumeration hook. Search for `_resetCount.Remove`
or similar tracking-clear calls and add `_autoResetAbandoned.Remove(text)`
adjacent to them. (Out of scope to fully spec without reading the hire path;
flag this as a follow-up.)

## For the eMployee mod author: integrating these fixes upstream

> **STATUS: HYPOTHESIS, NOT VERIFIED IN A RUNNING GAME.** This section
> describes a candidate diagnosis based on signature-level decompiles of
> `eMployee.dll` v2.2.4 and `Assembly-CSharp.dll` from Schedule I 0.4.5f2.
> Method bodies inside the il2cpp game assemblies (`Behaviour.Disable`,
> `StartMixingStationBehaviour.StartCook`/`StopCook`, `Employee.Fire`,
> `Chemist.Fire`) are emitted as native code by Cpp2IL and are not
> recoverable to C#. We can read **declarations and field/method names**
> but not **bodies**. Treat every claim about *what a vanilla method
> does* as a guess until verified in-game.
>
> Verified vs. hypothesis is called out per step below.

This section is written for V4LEXL or any future maintainer of `eMployee.dll`.
It explains, step by step, what `ResetEmployeeCore` does, what it misses,
and a candidate fix. Field names, line numbers, and call sites all reference
the v2.2.4 decompile of `eMployee.dll`. Game type/field/method names
reference `Assembly-CSharp.dll` in `<game>/MelonLoader/Il2CppAssemblies/`.

The goal is not to fork `eMployee` -- the goal is to give you a starting
point for a candidate v2.2.5 patch. A companion sidecar mod called
`EmployeeReset` exists that applies these same steps as a Harmony postfix
on `ResetEmployeeCore`; you can read its source as a reference
implementation. We would prefer for the sidecar to become unnecessary, but
we have not yet confirmed in-game that even the sidecar fully resolves the
"chemist re-stuck after fire/rehire" symptom.

### Things you should verify in your local environment before merging

1. **Does `Chemist.Fire()` (Assembly-CSharp.dll line 1007) call
   `StartMixingStationBehaviour.StopCook()` to release the station?** The
   `Fire()` body is in il2cpp native code and unreadable from Cpp2IL
   output. If yes, the bug we describe below does not exist on a fresh
   fire and we are diagnosing the wrong thing.
2. **What does `Behaviour.Disable()` do?** Same problem. We know
   `StartMixingStationBehaviour` does NOT override it, so it falls
   through to the base `Behaviour` class -- but we cannot read that body.
   Our patch deliberately does not call it.
3. **Does `MixingStation.CurrentMixOperation` need to be cleared?** YES,
   confirmed by in-game test. It is a public `MixOperation` reference
   (Assembly-CSharp.dll line 1560) that tracks the in-progress cook.
   When a chemist wedges mid-cook and we run `StopCook` / `Deactivate`
   on them, vanilla's behaviour selector re-picks the same cook on the
   next tick because `CurrentMixOperation` still holds the in-progress
   mix. The visible symptom: chemist pauses (Deactivate worked), then
   resumes mixing within ~1 second (vanilla re-activated). Only after
   adding `station.CurrentMixOperation = null` does the chemist stay
   freed.
4. **Is `consecutivePathingFailures` actually being zeroed today?** No.
   We confirmed this by inspection of `Assembly-CSharp.dll`: the il2cpp
   interop bridge exposes `consecutivePathingFailures` as a *property*,
   not a field. Your current code uses
   `GetField("consecutivePathingFailures")` (line 2585-2589, 2603-2608)
   which returns `null` on the bridge type. The `if (field != null)`
   then skips the reset silently. **This means every player on
   eMployee v2.2.4 has been running with a no-op pathing-failure-counter
   reset since the v2.2 release.** Switching to
   `GetProperty("consecutivePathingFailures")?.SetValue(emp, 0)` -- or
   if you compile against `Assembly-CSharp.dll` directly, the typed
   setter `((Employee)emp).consecutivePathingFailures = 0` -- fixes it.

If any of items 1-3 invalidates our model, the patch below needs revision.

### Side-by-side comparison: ResetEmployeeCore vs. comprehensive reset

Each row is one cleanup action. "Required" indicates whether the action is
needed to reliably recover a chemist whose `StartMixingStationBehaviour` got
stuck (Symptom B in this doc) or threw NRE on save load (Symptom A).

| # | Action | eMployee `ResetEmployeeCore` (line 2560-2669) | Comprehensive reset | Required for | Why |
|---|--------|---|---|---|---|
| 1 | Log STUCK-DIAG with `[AUTO-RESET]` / `[MANUAL-RESET]` tag | yes (2577) | optional | neither | Telemetry only |
| 2 | Zero `NPC.consecutivePathingFailures` | yes (2585-2589) | yes | both | Without it, vanilla's `>= 5 path failures -> ResetEmployee` fast-path re-fires mid-recovery |
| 3 | Zero `Behaviour.consecutivePathingFailures` | yes (2603-2608) | yes | both | Same as 2, on the behaviour level |
| 4 | Release `MixingStation.NPCUserObject` for the assigned station | NO | yes | Symptom B | The actual root cause for "chemist stuck after fire/rehire". A fired chemist's reservation is never released |
| 5 | `MonoBehaviour.StopAllCoroutines()` on the active behaviour | NO | yes | Symptom A | Without it, an in-flight `CookRoutine` resumes after reset and dereferences fields the reset just nulled |
| 6a | `Behaviour.Disable()` (vanilla cleanup hook) | NO | NO -- intentionally skipped | -- | Body unreadable from Cpp2IL output; risk of unknown side effects. We use `Deactivate()` instead -- the `StartMixingStationBehaviour`-specific override -- which has clear semantics |
| 6b | `Behaviour.Deactivate()` (chemist-specific cleanup) | NO | yes | Symptom B | Override on `StartMixingStationBehaviour` (line 474 of decompiled). Confirmed in-game: chemist visually pauses when Deactivate runs, exits the mixing pose. Without it the chemist stays at the table even with the cook coroutine torn down |
| 6c | `MixingStation.CurrentMixOperation = null` | NO | yes | Symptom B | Confirmed in-game: without nulling this, vanilla's behaviour selector re-picks the same cook within ~1 second of our reset because the station still has an in-progress mix |
| 7 | Null `_activeBehaviour_k__BackingField` on `NPCBehaviour` | yes (2610-2615) | yes | both | Standard behaviour-detach |
| 8 | `NavMeshAgent.ResetPath()` | yes (2626-2631) | yes | both | Clears stale destination |
| 9 | `NPCBehaviour.SortBehaviourStack()` | NO (helper at 2526 unused in reset path) | yes | Symptom B | Forces re-evaluation so a fallback behaviour can take over while the broken cook task waits for conditions |
| 10 | Bookkeeping: `_idleSinceRealtime`, `_resetCount`, `_activeBehTrack`, etc. | yes (2637-2650) | n/a | neither | Internal mod state, only meaningful inside eMployee |
| 11 | Manual-reset clears `_autoResetTries` / `_autoResetAbandoned` | yes (2651-2655) | n/a | neither | Internal mod state |

The four rows that say "NO" in the eMployee column (4, 5, 6, 9) are the
gap. Adding them turns reset from "narrow but well-instrumented" into
"wide enough to actually heal the worker".

### Why each missing step matters, in detail

#### Step 4: Release `MixingStation.NPCUserObject`

`Il2CppScheduleOne.ObjectScripts.MixingStation` exposes a `NetworkObject`
property called `NPCUserObject` (Assembly-CSharp.dll, line 1650 of the
decompile) plus a setter `SetNPCUser(NetworkObject npcObject)` (line 2909).
This field is the station's record of "which NPC is currently using me".

When a chemist is fired or removed mid-cook, the station retains the
reference to their NetworkObject. The new chemist arrives, runs
`StartMixingStationBehaviour`, the coroutine checks "is the station
available?" and the answer is "no, NPCUserObject is set". The chemist sits
in `StartMixingStationBehaviour` with `TicksSinceLastWork` climbing,
`STUCK-DETECT` fires, `ResetEmployeeCore` runs -- but it does not touch the
station, so on the next tick the new chemist re-enters
`StartMixingStationBehaviour` and the loop continues until
`_autoResetAbandoned` adds them. This is "Symptom B" in this document.

How to add it. After the existing `_activeBehaviour_k__BackingField` null
write at line 2615, before the `NavMeshAgent.ResetPath()` block at
line 2622, insert:

```csharp
// Release the assigned MixingStation's reservation so the next chemist can claim it.
try
{
    object obj4 = obj3?.GetType().GetProperty("activeBehaviour",
                       BindingFlags.Instance | BindingFlags.Public)?.GetValue(obj3);
    if (obj4 != null)
    {
        // Try to access targetStation if this behaviour has one (StartMixingStationBehaviour does).
        PropertyInfo stationProp = obj4.GetType().GetProperty("targetStation",
                                       BindingFlags.Instance | BindingFlags.Public);
        object station = stationProp?.GetValue(obj4);
        if (station != null)
        {
            MethodInfo setUser = station.GetType().GetMethod("SetNPCUser",
                                     BindingFlags.Instance | BindingFlags.Public);
            if (setUser != null)
            {
                setUser.Invoke(station, new object[] { null });
                num++;
            }
        }
    }
}
catch (Exception ex)
{
    if (_debugLog) MelonLogger.Warning("[eMployee] SetNPCUser(null) failed: " + ex.Message);
}
```

The reflection-style fits the rest of `ResetEmployeeCore`. If you prefer
typed access, the typed version (using the Il2CppInterop bridge) is:

```csharp
StartMixingStationBehaviour cook = active.TryCast<StartMixingStationBehaviour>();
MixingStation station = cook?.targetStation;
station?.SetNPCUser(null);
```

This same pattern applies to `StartChemistryStationBehaviour` (with its
`ChemistryStation` target), `StartCauldronBehaviour`, `StartLabOvenBehaviour`,
and `BrickPressBehaviour`. Each has its own typed station property and each
station has a `NPCUserObject` field. For a generic implementation, walk the
behaviour for any property whose type derives from a common
`NetworkOccupiable`-style base (or just check for a property literally
named `targetStation`, since that is the convention).

#### Step 5: `MonoBehaviour.StopAllCoroutines()` on the active behaviour

`StartMixingStationBehaviour` is a `MonoBehaviour` attached to the chemist
GameObject. Inside it, `StartCook()` (Assembly-CSharp.dll, line 518)
launches a local-function coroutine `CookRoutine` whose state machine class
is named `<<StartCook>g__CookRoutine|13_0>d`. Unity's coroutine scheduler
holds the IEnumerator independently of the `_activeBehaviour` reference on
`NPCBehaviour`.

When `ResetEmployeeCore` nulls `_activeBehaviour_k__BackingField`, it
detaches the behaviour from the NPC's logical stack -- but Unity's coroutine
runner does not care. On the next frame tick the coroutine's `MoveNext` runs,
dereferences `targetStation` or another captured field whose target was
freshly invalidated by save/load or by Step 4 above, and throws
`NullReferenceException`. This is "Symptom A" in this document. The exact
log line we observed:

```
Il2CppInterop.Runtime.Il2CppException: NullReferenceException
  at StartMixingStationBehaviour+<<StartCook>g__CookRoutine|13_0>d.MoveNext () [0x00000]
```

How to add it. In `ResetEmployeeCore`, immediately before nulling
`_activeBehaviour_k__BackingField`:

```csharp
try
{
    MonoBehaviour mb = ((Il2CppObjectBase)obj4).TryCast<MonoBehaviour>();
    if ((Object)(object)mb != (Object)null)
    {
        mb.StopAllCoroutines();
        num++;
    }
}
catch (Exception ex)
{
    if (_debugLog) MelonLogger.Warning("[eMployee] StopAllCoroutines failed: " + ex.Message);
}
```

`Il2CppObjectBase.TryCast<MonoBehaviour>()` is the Il2CppInterop helper. The
mod already uses `TryCast<Employee>` at line 2573, so the import is in scope.

`StopAllCoroutines` removes every coroutine the active behaviour has started,
including the `CookRoutine` causing the NRE. Yes, this is somewhat
heavy-handed -- but the active behaviour is about to be detached from the
NPC anyway, so its coroutines have no reason to keep running.

#### Step 6: `Behaviour.Disable()` -- the vanilla cleanup hook

`Il2CppScheduleOne.NPCs.Behaviour.Behaviour` (the base class for all worker
behaviours) defines a public `Disable()` method that vanilla calls to
deactivate a behaviour. It releases reservations, cancels in-flight network
syncs, and is the official "I'm done, clean up" hook.

eMployee already has the call elsewhere -- line 9734 of the decompile shows
`val6.Disable()` in another context. But `ResetEmployeeCore` does not call
it. Skipping the official cleanup means any state that `Disable()` would
clear (additional reservations on items, secondary station holds, sync-var
deactivation) lingers, and the next behaviour activation may inherit
contaminated state.

How to add it. After Step 5 (StopAllCoroutines), before nulling the field:

```csharp
try
{
    MethodInfo disable = obj4.GetType().GetMethod("Disable",
                            BindingFlags.Instance | BindingFlags.Public,
                            null, Type.EmptyTypes, null);
    if (disable != null) { disable.Invoke(obj4, null); num++; }
}
catch (Exception ex)
{
    if (_debugLog) MelonLogger.Warning("[eMployee] Disable() failed: " + ex.Message);
}
```

Order matters: `StopAllCoroutines` first (so `Disable()` is not racing a
running coroutine), then `Disable()`, then null the backing field.

#### Step 9: `NPCBehaviour.SortBehaviourStack()` -- force re-evaluation

After detaching the active behaviour, the `NPCBehaviour` priority stack
still has the same task queued. Without a re-sort, vanilla's behaviour
selector picks the highest-priority behaviour with conditions met -- which
is, again, `StartMixingStationBehaviour` -- and re-activates it. If the
underlying cause has not yet cleared (or has not been observed by the
selector since the cleanup), the chemist re-enters the same broken state.

`SortBehaviourStack()` exists on `NPCBehaviour`. eMployee even has a helper
called `ResortBehaviourStack(NPC npc)` at line 2526 of the decompile that
calls it -- but `ResetEmployeeCore` never calls that helper.

How to add it. At the end of `ResetEmployeeCore`, just before the closing
`return num`:

```csharp
try
{
    object obj3 = emp.GetType().GetProperty("Behaviour",
                       BindingFlags.Instance | BindingFlags.Public)?.GetValue(emp);
    if (obj3 != null)
    {
        MethodInfo sort = obj3.GetType().GetMethod("SortBehaviourStack",
                              BindingFlags.Instance | BindingFlags.Public);
        if (sort != null) { sort.Invoke(obj3, null); num++; }
    }
}
catch (Exception ex)
{
    if (_debugLog) MelonLogger.Warning("[eMployee] SortBehaviourStack failed: " + ex.Message);
}
```

This is also why item 9 in the comparison table calls out that the helper
already exists in your code (line 2526) but is unused in this code path.
You can simply call `ResortBehaviourStack(((NPC)val))` instead of the
reflection above.

### Suggested complete patch

A unified version that combines all four steps. Insert after the existing
`field3.SetValue(obj3, null); num++;` at line 2615, and replace the rest of
the method body up to the bookkeeping at line 2637 with this:

```csharp
// === BEGIN COMPREHENSIVE CLEANUP (Steps 4, 5, 6, 9 from upstream proposal) ===
object active = obj3?.GetType().GetProperty("activeBehaviour",
                     BindingFlags.Instance | BindingFlags.Public)?.GetValue(obj3);
// NB: at this point we have already nulled the backing field, so re-fetch
// from a local we captured BEFORE nulling. Move the active-fetch up.
// (Pseudo-positioning here -- you'll want to read 'active' once at the
// top of the cleanup block and reuse, see notes below.)

// Step 5: stop coroutines on the active behaviour MonoBehaviour.
try
{
    MonoBehaviour mb = ((Il2CppObjectBase)active)?.TryCast<MonoBehaviour>();
    if ((Object)(object)mb != (Object)null) { mb.StopAllCoroutines(); num++; }
}
catch (Exception ex) { if (_debugLog) MelonLogger.Warning("[eMployee] StopAllCoroutines failed: " + ex.Message); }

// Step 4: release MixingStation.NPCUserObject if the active behaviour has a targetStation.
try
{
    object station = active?.GetType().GetProperty("targetStation",
                          BindingFlags.Instance | BindingFlags.Public)?.GetValue(active);
    if (station != null)
    {
        MethodInfo setUser = station.GetType().GetMethod("SetNPCUser",
                                  BindingFlags.Instance | BindingFlags.Public);
        if (setUser != null) { setUser.Invoke(station, new object[] { null }); num++; }
    }
}
catch (Exception ex) { if (_debugLog) MelonLogger.Warning("[eMployee] SetNPCUser(null) failed: " + ex.Message); }

// Step 6: vanilla Disable() cleanup hook.
try
{
    MethodInfo disable = active?.GetType().GetMethod("Disable",
                              BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
    if (disable != null) { disable.Invoke(active, null); num++; }
}
catch (Exception ex) { if (_debugLog) MelonLogger.Warning("[eMployee] Disable() failed: " + ex.Message); }

// (existing field3.SetValue(obj3, null) -- keep where it is)

// Step 9: force behaviour stack re-sort so a fallback behaviour can take over.
try { ResortBehaviourStack(((NPC)val)); num++; }
catch (Exception ex) { if (_debugLog) MelonLogger.Warning("[eMployee] ResortBehaviourStack failed: " + ex.Message); }
// === END COMPREHENSIVE CLEANUP ===
```

Sequencing notes for the maintainer:

1. Capture `active = obj3.activeBehaviour` once at the top of the cleanup
   block, before any null-write. Otherwise after nulling the backing field
   you cannot fetch the active behaviour again.
2. Order: `StopAllCoroutines -> SetNPCUser(null) -> Disable() -> null backing
   field -> SortBehaviourStack`. Stopping the coroutine first means
   `Disable()` is not racing it. Releasing `NPCUserObject` before `Disable()`
   means the station is in a clean state when `Disable()` runs.
3. All four steps wrap their own `try/catch` so a partial failure does not
   abort the rest. This matches the surrounding code's resilience style.
4. `num` is incremented once per successful step so the
   `[RESET-A] {text} ({num} actions, ...)` log line at line 2658 reflects
   the new richer cleanup.

### Why this preserves the retry-budget design

You explicitly designed the `_autoResetMaxRetries` / `_autoResetAbandoned`
mechanism to "not loop on unrecoverable issues" -- that is in your Nexus
description. The patch above does NOT remove that gate. It widens what each
retry actually does, so each retry has a higher probability of succeeding
within the existing budget.

Concretely: today, three retries against a broken `MixingStation.NPCUserObject`
all fail because none of them touch the station. With the patch, the first
retry already releases `NPCUserObject`, and the next behaviour evaluation
finds a clean station. The retry budget is rarely exhausted because the
first retry is now sufficient.

If you want to keep abandonment as a safety net for genuinely unrecoverable
states (e.g. broken save, missing GameObject), the patch supports that --
the behaviour stack still surfaces an underlying issue line through your
existing `EmpStatus`/`IssueLine` system, and the `_autoResetAbandoned` gate
still kicks in if even the comprehensive cleanup cannot recover the worker.

### Acknowledgments

The MixingStation field name `NPCUserObject` and the public setter
`SetNPCUser(NetworkObject)` were verified in `Assembly-CSharp.dll` from
Schedule I 0.4.5f2, decompiled with `ilspycmd 9.1.0`. The behaviour-side
field `targetStation` and method `AssignStation(MixingStation)` were
similarly verified in
`Il2CppScheduleOne.NPCs.Behaviour.StartMixingStationBehaviour`.

The companion sidecar `EmployeeReset` lives in this same repository at
`./EmployeeReset/`. Its `SmartReset(Employee emp)` method in
`EmployeeReset/src/Mod.cs` is the typed reference implementation of the
steps above. Source:
<https://github.com/abix-/Schedule1Mods/tree/main/EmployeeReset>.

## Sidecar mod: EmployeeReset

A standalone MelonLoader mod that applies the comprehensive cleanup as both
a Harmony postfix on `eMployeeMod.ResetEmployeeCore` (so eMployee's
existing AUTO-RESET path inherits the wider cleanup) and a player-driven
hotkey (so the player can force-recover a stuck chemist on demand).

### Current state (2026-05-03)

| Aspect | Status |
| ------ | ------ |
| Source | Single C# project at `./EmployeeReset/` (this repository) |
| Compiled | Yes -- `bin/Release/net6.0/EmployeeReset.dll`, 12.8 KB |
| Symptom A (save/load NRE + stuck workers) | **Verified fixed in one test run.** Loaded a save where botanist Mathew Martin and chemist Richard Perez both flagged STUCK at Storage Unit on save load; the recurring `Il2CppException at StartMixingStationBehaviour+CookRoutine.MoveNext` did NOT fire, and the chemist recovered to working state. |
| Symptom B (mid-cook ingredient exhaustion wedge) | **Diagnosed in iteration 6, fix built in iteration 8, deploy pending.** The instrumentation showed vanilla auto-re-assigns `targetStation` within ~200ms of our null write. Vanilla's `CanCookStart` returns true even when the station's input slots are empty. The fix is a Harmony postfix on `CanCookStart` that returns false when any input slot has Quantity == 0 -- making vanilla's behaviour selector pick a fallback (idle) instead of starting another wedge. |
| Hotkey scope | F8 is chemist-only. Other roles are skipped. The chemist-specific bug is what we have evidence of; widening the hotkey to other roles needs separate testing. |
| Which step did the work for Symptom A | Uncertain. In that test the active-behaviour reference was already null by the time our postfix ran, so the cook-cleanup branch did not execute. The fix likely came from one or more of: `consecutivePathingFailures = 0` via the typed property setter (eMployee's `GetField` version is a silent no-op), `NPCBehaviour.SortBehaviourStack()` (eMployee never calls this in their reset path), or a combination. Strongest hypothesis: `SortBehaviourStack` is the differentiator for Symptom A. |
| Ingredient gate (iteration 8) | **The actual root-cause fix.** Postfix on `CanCookStart` blocks the chemist from starting a cook with empty input slots. Built and ready to deploy; pending one in-game verification. |
| Repository | <https://github.com/abix-/Schedule1Mods> |
| Target platform | Schedule I 0.4.5f2 + MelonLoader 0.7.0+ (net6 / IL2CPP x64) |
| Dependencies | MelonLoader, 0Harmony, Il2CppInterop, Assembly-CSharp |
| eMployee dependency | Optional. Mod runs standalone; the postfix hook is best-effort and silently no-ops if eMployee is absent |

### What it actually does

For each affected employee, in order. The chain expanded over several
test iterations as each level of state became visible.

1. Zeroes `Employee.consecutivePathingFailures` via the typed property
   setter. The bridge type exposes it as a property; `GetField` returns
   null, which is why eMployee's reset is a silent no-op for this
   counter.
2. **Chemist-typed path** (always runs for chemists, regardless of
   `_activeBehaviour` state). Reaches the chemist's
   `StartMixingStationBehaviour` MonoBehaviour via the typed
   `Chemist.StartMixingStationBehaviour` property (line 530 of decompiled
   `Chemist`). This catches wedged coroutines even when eMployee already
   nulled `_activeBehaviour`. On that component, in order:
   - `StopCook()` -- vanilla's intended end-of-cook hook.
   - `targetStation.SetNPCUser(null)` -- release the station's
     "in use by NPC" reservation.
   - `targetStation.CurrentMixOperation = null` -- clear the in-progress
     mix operation. **Critical: without this, vanilla immediately
     re-picks `StartMixingStationBehaviour` after the reset because the
     station still has an in-progress cook waiting to be resumed.**
   - `MonoBehaviour.StopAllCoroutines()` -- final guard against any
     coroutine that `StopCook` did not cover.
   - `Deactivate()` -- vanilla's "stop being active, unwind animation
     and position lock" hook. Override exists on
     `StartMixingStationBehaviour` at line 474 of the decompile.
3. **Active-behaviour path** (runs only if `_activeBehaviour` is currently
   `StartMixingStationBehaviour`). Same five operations, applied to the
   active reference. Belt-and-suspenders -- the typed path usually covers
   this case, but if eMployee's reset didn't run yet `_activeBehaviour`
   may still be set.
4. Nulls `_activeBehaviour_k__BackingField` on the chemist's
   `NPCBehaviour` so the next tick re-evaluates the priority stack from
   scratch.
5. `NavMeshAgent.ResetPath()` to clear any stale destination.
6. `NPCBehaviour.SortBehaviourStack()` to force re-evaluation so a
   fallback behaviour can take over while broken cook conditions remain
   unsatisfied.

The mod does NOT call `Behaviour.Disable()`. The base `Behaviour` class's
`Disable()` body is unreadable from Cpp2IL output and we have no evidence
it does the right thing for a chemist mid-cook. `StopCook()` plus
`Deactivate()` are the documented public APIs and are preferred.

### How we discovered each layer (test history)

The state-clearing chain was not designed up front. Each iteration
exposed the next missing piece:

| Iteration | Visible symptom on F8 | Diagnosis | Code added |
| --- | --- | --- | --- |
| 0 (baseline, eMployee only) | Chemist wedged, NRE every ~30 s | eMployee's reset null-writes `_activeBehaviour` but does not stop coroutines or release station state | n/a -- bug unfixed in eMployee |
| 1 (StopAllCoroutines + StopCook on active) | `scanned 4, reset 0` -- nothing fired | `LooksStuck` heuristic too narrow; `_activeBehaviour` was already null by the time we ran | Drop heuristic from F8 path; add typed `Chemist.StartMixingStationBehaviour` access path |
| 2 (typed cook + StopCook + StopAllCoroutines) | All log lines fire cleanly, but chemist still wedged at table; NRE persists | `StopCook()` is a flag-set, not a hard kill; chemist's animation/position still locked | Add `Deactivate()` to both paths |
| 3 (Deactivate added) | Chemist visually pauses, then immediately resumes mixing; NRE persists | Vanilla's behaviour selector re-picks the cook because `MixingStation.CurrentMixOperation` still holds the in-progress cook | Add `station.CurrentMixOperation = null` to both paths |
| 4 (CurrentMixOperation cleared) | Same pause-then-resume; NRE persists | All station and behaviour state cleared, but vanilla still re-activates within ~600ms | Add HAMMER `StopAllCoroutines` on every MonoBehaviour on the chemist's GameObject |
| 5 (instrumentation added) | -- | Harmony-prefix on CookRoutine.MoveNext + post-F8 inspect coroutine sampling at 200ms intervals reveals: vanilla auto-re-assigns `targetStation` within ~200ms of our null write, then activates a fresh `StartMixingStationBehaviour` at t+400ms, fresh CookRoutine starts at t+790ms (different state-machine hash from before our reset), state 1 NRE again at t+1290ms | Instrumentation only; no fix yet |
| 6 (null targetStation) | Inspect log shows targetStation=null at t+200ms then targetStation=Mixing Station at t+400ms; cook re-activates anyway | Vanilla's behaviour selector re-derives `targetStation` from `MixingStationConfiguration` every evaluation tick. Writing null to the chemist's targetStation is overwritten by vanilla within ~200ms. The configuration-level assignment is sticky and we cannot remove it without losing the player's setup | Identified the actual root cause: vanilla's `CanCookStart()` returns true even when ingredient reachability cannot be satisfied |
| 7 (cooldown via CanCookStart prefix) | Built but rejected by user before testing | User: "I don't need a cooking cooldown. I need mixing to NOT occur unless there's ingredients." Cooldown was a workaround, not a fix | Reverted: cooldown code removed |
| 8 (ingredient gate via CanCookStart postfix) | Built but predicate was wrong (`InputSlots` is not the right list) | Phase 1 decompile of `MixingStationConfiguration` revealed there is no recipe field; assumed "ingredients" lived in `MixingStation.InputSlots` collection | See `ingredient-gate-fix-plan.md` |
| 9 (canonical CanStartMix gate via GetMixQuantity, /rtfm-driven) | Built, deploy pending | /rtfm pass against [ProduceMore mod](https://thunderstore.io/c/schedule-i/p/lasersquid/ProduceMoreMono/source/) revealed the real model: named slots `ProductSlot` / `MixerSlot` / `OutputSlot`, and the canonical predicate is `MixingStation.GetMixQuantity() > 0`. ProduceMore patches `MixingStation.CanStartMix`, not `CanCookStart` | We now patch both `CanStartMix` (canonical) and `CanCookStart` (belt) with the same predicate |

Each iteration corresponds to one F8 press in the actual game with logs
captured. The "pause then resume" pattern in iteration 3 was the cleanest
diagnostic moment for the deactivation layer. Iteration 5's instrumentation
(see "Diagnostic instrumentation results" below) was the cleanest diagnostic
moment for the assignment layer -- it told us in one F8 press exactly which
state field vanilla was re-deriving and on what cadence.

### Diagnostic instrumentation results (iteration 5)

We added two instruments before the iteration-5 F8 press:

1. Harmony prefix on `StartMixingStationBehaviour.<<StartCook>g__CookRoutine|13_0>d.MoveNext`
   that logs the state-machine instance hash and `__1__state` value.
2. A MelonCoroutine spawned after F8 that samples chemist state every 200 ms
   for 2 seconds.

Sample of the iteration-5 log (timestamps relative to F8 press at 12:27:53.786):

```
pre-F8:
  [CookMN] hash=38049356 state=0 count=1 NEW-INSTANCE STATE-CHANGE
  [CookMN] hash=38049356 state=1 count=2 STATE-CHANGE
  Il2CppException: NullReferenceException at CookRoutine.MoveNext

F8 fires (all 11 cleanup steps, including HAMMER on 12 MonoBehaviours):
  [Reset] Richard Perez: smart reset complete

post-F8 inspect window:
  t+200ms:  active=IdleBehaviour      targetStation=Mixing Station mixOp=null  npcUser=null
  t+400ms:  active=IdleBehaviour      targetStation=Mixing Station mixOp=null  npcUser=null
  t+600ms:  active=StartMixingStationBehaviour  targetStation=Mixing Station mixOp=null  npcUser=null  <-- vanilla RE-ACTIVATED
  t+790ms:  [CookMN] hash=58655942 state=0 count=1 NEW-INSTANCE  <-- new state machine, different hash
  t+800ms:  active=StartMixingStationBehaviour  targetStation=Mixing Station mixOp=null  npcUser=null
  ...
  t+1290ms: [CookMN] hash=58655942 state=1 count=2 STATE-CHANGE
  Il2CppException: NullReferenceException at CookRoutine.MoveNext  <-- new cook NREs at same state
  t+2000ms: active=StartMixingStationBehaviour  targetStation=Mixing Station mixOp=null  npcUser=SET  <-- new cook claimed station
```

This single F8 press revealed:

- Our `StopAllCoroutines` (including the HAMMER) successfully killed the
  original wedged coroutine. The new state-machine hash (58655942) is
  different from the original (38049356).
- Our `Deactivate` worked. Chemist transitioned to `IdleBehaviour` for
  ~400 ms.
- Our `mixOp = null` and `npcUser = null` writes stuck through the entire
  inspection window (until vanilla's new cook re-set `npcUser` at t+2000ms).
- But: targetStation stayed `Mixing Station` the entire time -- our null
  write was overwritten almost immediately by vanilla, before the first
  inspect sample could capture it. (Iteration 6 confirmed this: with our
  null write added, the t+200ms sample showed `targetStation=null` and the
  t+400ms sample showed `targetStation=Mixing Station` again, an explicit
  re-assignment within 200ms.)
- The new cook fires its own MoveNext at t+790ms (state 0 -- setup) then
  state 1 -- which NREs at the same point as the original wedged cook.

### Root cause (iteration 6 conclusion)

**Vanilla's `StartMixingStationBehaviour.CanCookStart()` predicate does not
check whether the recipe's ingredients are actually reachable from the
station's storage.** It checks station availability, recipe presence, and
chemist-on-shift status, all of which return true. So vanilla repeatedly
chooses to start the cook, the cook iterates ingredients, hits the missing
one, and wedges.

The chemist is not "being stupid" -- they are following vanilla's
instructions. Vanilla is telling them "yes, you can cook" without
verifying that they actually can. **The bug is in the predicate, not in
the chemist's response to it.**

### The right fix: gate the predicate on ingredient availability

The user-stated requirement is unambiguous:

> "I need mixing to NOT occur unless there's ingredients. THAT'S the root
> issue."

The correct fix is to extend `CanCookStart()` so it verifies the
ingredients are present before saying yes. When ingredients are missing,
`CanCookStart` returns false, vanilla's behaviour selector picks a
fallback (idle or walk-to-rest), the chemist stays out of the cook
automatically. When the player restocks, `CanCookStart` returns true on
the next evaluation tick, the chemist resumes work without any manual
intervention. No cooldown. No timeout. No state.

#### What "ingredients" actually means in vanilla

Phase 1 reconnaissance (decompiling `MixingStationConfiguration`,
`MixingStation`, `ItemSlot`) revealed that **Schedule I does not have a
formal recipe object with an ingredient list.** The "recipe" is implicit:
defined by what items the player has loaded into the station's two named
slots (`ProductSlot` and `MixerSlot`). `MixingStationConfiguration` only
carries `AssignedChemist`, `Destination`, `StartThrehold` (sic), and
`DestinationRoute` -- no recipe field.

A subsequent /rtfm pass against the Schedule I mod ecosystem (see the
ProduceMore mod's [decompiled source](https://thunderstore.io/c/schedule-i/p/lasersquid/ProduceMoreMono/source/))
confirmed the canonical predicate:

```csharp
station.GetMixQuantity()  // Mathf.Min(ProductSlot.Quantity, MixerSlot.Quantity, MaxMixQuantity)
```

If `GetMixQuantity()` returns 0, either input slot is empty or the
station is at capacity. That is the "can I mix right now?" check
vanilla itself uses internally; vanilla's `MixingStation.CanStartMix`
should call it but apparently does not enforce it correctly, which is
why ProduceMore overrides `CanStartMix` to:

```csharp
__result = station.GetMixQuantity() > 0 && station.OutputSlot.Quantity == 0;
```

Our gate uses the same predicate. We patch it as a postfix that only
flips `true` to `false` -- never the other way -- so we cannot make the
chemist over-eager.

#### Implementation (current)

Two postfixes, belt-and-suspenders:

```csharp
// Canonical: matches the predicate ProduceMore uses
[HarmonyPatch(typeof(MixingStation), "CanStartMix")]
class CanStartMixIngredientGate
{
    static void Postfix(MixingStation __instance, ref bool __result)
    {
        if (!__result) return;
        if (__instance == null) return;
        if (__instance.GetMixQuantity() <= 0)
            __result = false;
    }
}

// Belt: in case some vanilla code path uses CanCookStart directly
[HarmonyPatch(typeof(StartMixingStationBehaviour), "CanCookStart")]
class CanCookStartIngredientGate
{
    static void Postfix(StartMixingStationBehaviour __instance, ref bool __result)
    {
        if (!__result) return;
        if (__instance == null) return;
        MixingStation station = __instance.targetStation;
        if (station != null && station.GetMixQuantity() <= 0)
            __result = false;
    }
}
```

Both gates are O(1) (one virtual call into the engine for
`GetMixQuantity`). No caching needed.

Postfix, not prefix. Vanilla makes the call, we only override `true` to
`false` when slots are empty. We never override `false` to `true`. The
chemist can become more conservative because of us, never more eager.

The postfixes are installed in `EmployeeReset.Mod.OnInitializeMelon` via
`TryPatchCanStartMixIngredientGate()` and
`TryPatchCanCookStartIngredientGate()`. See
[`ingredient-gate-fix-plan.md`](ingredient-gate-fix-plan.md) for
the full plan and Phase 1 findings, and
[`certainty-tracking.md`](certainty-tracking.md) for the verification
status.

### Approaches we rejected and why

1. **Cooldown via Harmony prefix that lies to the predicate for 30 s after
   F8.** We started building this in iteration 7. Rejected because: (a) it
   does not address the underlying bug; (b) the cooldown window is
   arbitrary; (c) if ingredients are still missing after the cooldown
   expires, the chemist re-wedges; (d) the user has clearly stated they
   want a real ingredient check, not a timer.
2. **Patch `CookRoutine.MoveNext` to swallow NREs.** Cosmetic only --
   stops the log flood but the chemist still wedges. Rejected because the
   visible symptom (stuck-mixing) remains.
3. **Clear the chemist's `MixingStationConfiguration` to remove the
   station-recipe assignment.** Destructive -- the player loses their
   setup and has to redo it. Rejected because it solves the stuck state
   only by deleting the configuration.

### Build instructions

```powershell
cd EmployeeReset
dotnet build -c Release
```

If the game is installed somewhere other than the default
`C:\Games\Steam\steamapps\common\Schedule I`:

```powershell
dotnet build -c Release -p:GameDir="D:\Path\To\Schedule I"
```

Output: `bin\Release\net6.0\EmployeeReset.dll`. No additional packaging
needed -- the DLL is the entire mod.

### Install instructions

1. Close Schedule I if running.
2. Copy `EmployeeReset.dll` to `<Schedule I install>\Mods\`.
3. Launch Schedule I. Look for these lines in
   `<Schedule I install>\MelonLoader\Latest.log`:

```
EmployeeReset 0.1.0 initialized; hotkey=F8
```

If eMployee is also installed, also expect:

```
Hooked eMployeeMod.ResetEmployeeCore
```

If the postfix hook fails (e.g. eMployee version mismatch), expect a
warning line instead. The hotkey path still works.

### Test procedure

This is the procedure to validate that `StopCook()` actually does what
its name suggests. We have not run this yet.

1. Load a save where a chemist is wedged at a `MixingStation` because
   storage ran out of an ingredient mid-cook (Symptom B). If you do not
   have such a save, induce the condition: assign a chemist to a
   3-ingredient recipe with only enough storage for 1 unit of one of
   the ingredients, start the cook, wait until they wedge.
2. Confirm the chemist is wedged: `[STUCK-DIAG]` lines in the log,
   chemist standing at the station in mixing pose, no progress.
3. Press F8 (or the hotkey configured in MelonPreferences for
   `EmployeeReset/Hotkey`).
4. Expected log lines, in order:
   ```
   [EmployeeReset] [Reset] hotkey: scanned N, reset 1
   [EmployeeReset] [Reset] {chemist}: StopCook() on active behaviour
   [EmployeeReset] [Reset] {chemist}: cleared MixingStation.NPCUserObject on '{station name}'
   [EmployeeReset] [Reset] {chemist}: StopAllCoroutines on StartMixingStationBehaviour
   [EmployeeReset] [Reset] {chemist}: smart reset complete
   ```
5. Within 1-2 seconds in-game:
   - Chemist should exit the mixing pose.
   - Chemist should walk away from the station OR enter idle pose
     where they stand.
   - `MixingStation.NPCUserObject` should be null on the next inspect
     cycle (eMployee Worker Status panel will show the station as free).
6. Watch for 30 seconds:
   - Chemist should NOT immediately re-enter the same broken cook
     (because `CanCookStart()` should return false with ingredients
     missing).
   - eMployee's `[STUCK-DIAG]` lines should stop firing for this
     chemist.

### Failure modes to report

If F8 throws an exception, capture from `Latest.log`:

- The `[EmployeeReset] [Reset] {chemist}: ...` line that failed.
- The full stack trace (everything between the `[Reset]` line and the
  next mod's log line).

If F8 runs cleanly but the chemist stays wedged (animation continues,
no movement), this means `StopCook()` is not sufficient on its own. The
likely missing piece is `MixingStation.CurrentMixOperation` -- a
`MixOperation` reference at line 1560 of decompiled `MixingStation` that
holds the in-progress cook independently of `NPCUserObject`. The next
revision should null that field too.

If F8 runs cleanly AND the chemist exits, but they re-enter the same
broken cook within 30 seconds, the cause is upstream of our reset:
either `StartMixingStationBehaviour.targetStation` is still pointing at
the same station (we did not clear it) or the chemist's recipe
assignment is still locked to the unreachable ingredient (we cannot
clear that without touching `ChemistConfiguration`).

### Configuration

Exposed via MelonPreferences (visible in the Mod Manager phone app
provided by the existing `ModManager&PhoneApp` mod, or directly editable
in `<Schedule I>\UserData\MelonPreferences.cfg`):

| Key | Default | Effect |
| --- | ------- | ------ |
| `EmployeeReset.Hotkey` | `F8` | Key to trigger smart reset on all stuck employees on the current property |
| `EmployeeReset.HookEMployeeAutoReset` | `true` | If true, also runs SmartReset as a Harmony postfix after `eMployeeMod.ResetEmployeeCore` |
| `EmployeeReset.VerboseLog` | `true` | If true, log every reset step; if false, log only the start/end summary |

### Why a sidecar instead of patching eMployee directly

- We do not have permission to redistribute a forked `eMployee.dll`.
- The decompile-and-recompile loop has high friction: ilspycmd output
  is not always re-compilable cleanly, and the project must be set up
  with all of the original mod's reference assemblies.
- Sidecar mods survive eMployee updates as long as
  `eMployeeMod.ResetEmployeeCore` keeps its name and signature.
- The sidecar can be used standalone (without eMployee) for the
  player-hotkey path, which broadens its utility.
- If V4LEXL accepts the upstream patch from the previous section, this
  sidecar becomes redundant and can be retired.

## Test plan

Build the patched DLL and verify in this order:

1. Unit-equivalent: load a save with a chemist mid-cook on the unpatched mod
   and confirm the NRE reproduces. (Baseline for Symptom A.)
2. Apply Fix 1 only. Reload the same save. Expect:
   - `[STUCK-DIAG] ... [AUTO-RESET]` still logs.
   - No NRE in `StartMixingStationBehaviour+CookRoutine.MoveNext` for at least
     30 seconds after load.
   - Chemist resumes work after AUTO-RESET (or stays idle waiting for a new
     station assignment, depending on station availability).
3. Apply Fix 1 + Fix 2 Option A (drop the MoveNext prefix). Set
   `_chemistSpeedMult = 3f` and confirm chemist still works at speed via the
   WaitForSeconds shortener path. Measure cook duration vs. a 1x baseline; if
   the speedup is acceptable, ship Option A.
4. If cook speedup at 3x is too small with Option A only, apply Fix 2 Option
   B and re-measure.
5. Stress test: two chemists assigned to one MixingStation, save/load five
   times. Count NRE occurrences and STUCK-DIAG-then-recovery occurrences.

**Symptom B test sequence (Fix 3):**

6. Baseline for Symptom B: on the unpatched mod, fire a chemist mid-cook,
   hire a replacement, observe the new chemist enter and re-enter
   `StartMixingStationBehaviour` until `[WATCHDOG] ... gave up after 3
   auto-resets, awaiting manual reset` appears in the log. Confirm
   `[STUCK-DIAG]` lines continue afterward without further reset attempts.
7. Apply Fix 3 Part A only (widen ResetEmployeeCore). Repeat the fire/rehire
   flow. Expect: the new chemist's first AUTO-RESET clears `AssignedStation`
   and the station's `IsBeingUsed`, allowing vanilla to re-evaluate and
   either re-assign cleanly or pick a different behaviour. The chemist
   should NOT loop through 3 resets in succession.
8. Apply Fix 3 Part A + Part B (escalation instead of abandonment). Force
   a permanent stuck condition (e.g. assign a chemist to a recipe whose
   ingredient is locked in an inaccessible storage). Verify:
   - First 3 resets fire at fast cadence (existing behaviour).
   - After that, resets continue every `_escalationCooldownSeconds`.
   - `[WATCHDOG] ... persistent stuck` warning is logged exactly once per
     episode.
   - When the underlying cause is fixed (player unlocks the storage), the
     chemist recovers on the next reset without manual intervention.
9. Manual reset path also exercises `ResetEmployeeCore`. Verify the eMployee
   phone app's manual reset still works post-fix (clicking "Reset" in the
   in-game UI should still snap a stuck NPC out of a bad state without
   throwing) and clears all tracking including `_persistentStuckNotified`.

## Workarounds for players (no rebuild)

If the mod cannot be rebuilt, mitigate with eMployee settings and play habits.

For Symptom A (NRE on save/load):

- Set chemist speed multiplier back to 1.0x in the eMployee phone app.
  Disables Bug 2.
- If eMployee exposes an AUTO-RESET toggle in its phone app, disable it.
  Disables Bug 1's trigger (the watchdog still logs STUCK-DIAG without
  acting). Manual reset still works because `isManual=true` runs the same code
  path; recommend manual reset only when no chemist is mid-cook.
- Save the colony only when chemists are idle (not mid-recipe). Wait until
  cook completes or fire/replace the chemist before saving.
- Ensure each chemist has a dedicated `MixingStation` to avoid the
  two-on-one-tile post-load overlap.

For Symptom B (chemist re-stuck after fire/rehire, "reset doesn't work"):

- Use the eMployee phone app's MANUAL reset on the affected chemist. Manual
  reset clears the `_autoResetAbandoned` flag (line 2653) and lets the
  watchdog try again. If manual reset works once but the chemist re-sticks,
  the underlying cause is on the MixingStation -- proceed to the next bullets.
- Increase `AutoResetMaxRetries` in the eMployee preferences from the default
  3 to a higher number (e.g. 20). This lets the watchdog keep trying without
  abandoning, at the cost of `[STUCK-DIAG]` log spam if recovery never
  happens.
- Cycle the MixingStation: pick it up and re-place it. This forces vanilla
  to reinitialise the station's `IsBeingUsed` / reservation state and breaks
  any lingering pointer the fired chemist left behind.
- Reassign the chemist to a different MixingStation if you have multiple.
  If the bug is on the station, moving the chemist works; if the bug follows
  the chemist, fire and rehire (then run manual reset on the rehire to clear
  any inherited tracking).
- Restart the game (full quit, not just save/load). Restart wipes all
  in-memory eMployee state including `_autoResetAbandoned` and
  `_autoResetTries`.

## Code references

| Concern | File | Line |
| ------- | ---- | ---- |
| Stuck diagnostic log format | eMployeeMod.cs | 3231 |
| AUTO-RESET vs MANUAL-RESET tag | eMployeeMod.cs | 2577 |
| ResetEmployeeCore entry point | eMployeeMod.cs | 2560 |
| `_activeBehaviour_k__BackingField` null write | eMployeeMod.cs | 2610-2615 |
| Manual reset clears tracking | eMployeeMod.cs | 2651-2655 |
| Watchdog tick (per-employee) | eMployeeMod.cs | 3290+ |
| `consecutivePathingFailures >= 5` fast-path | eMployeeMod.cs | 3304-3315 |
| Idle/Stationary/Dialogue early-out | eMployeeMod.cs | 3361-3367 |
| Active behaviour pointer tracking | eMployeeMod.cs | 3375-3382 |
| GAME stuck / PHYS stuck flags | eMployeeMod.cs | 3398-3399 |
| Retry/abandonment block | eMployeeMod.cs | 3408-3424 |
| `_autoResetMaxRetries` preference | eMployeeMod.cs | 1456 |
| `_autoResetTries` field | eMployeeMod.cs | 902 |
| `_autoResetAbandoned` field | eMployeeMod.cs | 904 |
| `_activeBehTrack` field | eMployeeMod.cs | 894 |
| MixingStation prefix registration | eMployeeMod.cs | 4399-4407 |
| MixingCoroutineMoveNextPrefix body | eMployeeMod.cs | 4668-4709 |
| CoroutineMoveNextPostfix body | eMployeeMod.cs | 4711+ |
| WaitForSeconds shortener | eMployeeMod.cs | 4640-4666 |
| `_chemistSpeedMult` default | eMployeeMod.cs | 614 |
| Behaviour list patched | eMployeeMod.cs | 4445 |
| Vanilla `Disable()` example call | eMployeeMod.cs | 9734 |
| Watchdog at 5 pathing failures | eMployeeMod.cs | 3304-3309 |

Decompile reproducible with:

```
ilspycmd -p --disable-updatecheck -o <out_dir> "<path>\eMployee.dll"
```

## Open questions

1. Does Schedule 1's `Behaviour.Disable()` call `StopAllCoroutines` itself? If
   yes, Fix 1 step 2 (explicit `StopAllCoroutines`) is redundant but harmless;
   if no, it is essential. Confirm by decompiling
   `Il2CppScheduleOne.NPCs.Behaviour.Behaviour` from the game's
   `GameAssembly.dll` (via Il2CppDumper).
2. Is `__4__this` the correct name for the captured `this` reference in the
   `<<StartCook>g__CookRoutine|13_0>d` class on Schedule 1 0.4.5f2? The C#
   compiler typically uses `<>4__this` or `__4__this` depending on closure
   shape; confirm before relying on Fix 2 Option B's safety check.
3. Is the WaitForSeconds shortener at line 4640-4666 sufficient on its own to
   provide perceived chemist speedup, or does the loop-skip prefix actually
   contribute more? Measure before deciding between Fix 2 Option A and B.
4. What are the actual field names on `Il2CppScheduleOne.ObjectScripts.MixingStation`
   for "currently in use", "reserved by employee", and "current recipe"?
   Fix 3 Part A guesses `IsBeingUsed` and `CurrentEmployee`; confirm by
   decompiling `MixingStation` from `GameAssembly.dll`.
5. Does vanilla Schedule 1 have an `OnEmployeeFired` or `OnEmployeeRemoved`
   hook that releases station reservations? If yes, the underlying cause of
   Symptom B is that hook failing; if no, Fix 3 Part A is the only place that
   can release them. The game version 0.4.5f2 should be inspected.
6. Is Symptom B reproducible without firing/rehiring? Test: take a working
   chemist, force-stop them by closing/opening the property, see whether the
   station's reservation state survives and traps the same chemist or a
   different one. If reproducible, the bug class is broader than the
   fire/rehire framing in this doc.
7. What is the actual stuck-threshold value `num` used in
   `flag = num3 >= num`? It is set somewhere in the watchdog initialisation
   block we have not read yet. Confirm to estimate user-visible time-to-reset
   so the test plan timings are accurate.

## References

- Schedule 1 game build: TVGS, v0.4.5f2.
- eMployee mod: V4LEXL, v2.2.4 (Vortex Schedule 1 mod 1625).
- MelonLoader: 0.7.2 Open-Beta, github.com/LavaGang/MelonLoader.
- Il2CppInterop: 1.5.1, github.com/BepInEx/Il2CppInterop.
- C# state machine field naming: dotnet/roslyn closure conversion, see
  `LocalRewriter_StateMachine` in roslyn for `_<varname>_<scope>__<index>` pattern.
