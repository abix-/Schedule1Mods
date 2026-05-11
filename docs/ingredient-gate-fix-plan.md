# Ingredient gate fix. Implementation plan

## Contents

- [Requirements (user-stated)](#requirements-user-stated)
- [Phase 0. Cleanup](#phase-0----cleanup)
- [Phase 1. Reconnaissance (results)](#phase-1----reconnaissance-results)
- [Phase 2. Predicate design](#phase-2----predicate-design-revised-after-phase-1)
- [Phase 3. Harmony wiring](#phase-3----harmony-wiring)
- [Phase 4. Caching](#phase-4----caching)
- [Phase 5. Validation](#phase-5----validation)
- [Phase 6. Known edge cases](#phase-6----known-edge-cases)
- [Status tracking](#status-tracking)

The chemist wedges at the MixingStation when a recipe ingredient becomes
unreachable mid-cook. Vanilla's `StartMixingStationBehaviour.CanCookStart()`
predicate does not check ingredient availability before saying yes, so the
behaviour selector activates the cook, the cook iterates, hits the missing
ingredient, and wedges. eMployee's reset cannot fix this because the
predicate is in vanilla code one layer below eMployee.

This plan implements the right fix: extend `CanCookStart()` via Harmony
postfix so that when ingredients are not available in storage the chemist
can access, the predicate returns false. Vanilla then picks a fallback
behaviour (idle / walk-to-rest), the chemist stays out of the cook
automatically. When the player restocks, the predicate flips to true on
the next evaluation (within the cache TTL), and the chemist resumes work
without any manual intervention.

This document is the standing plan for implementing that fix.

## Requirements (user-stated)

> "I need mixing to NOT occur unless there's ingredients. THAT'S the root
> issue."

- No cooldowns or timers.
- No manual intervention required to start working again once ingredients
  return.
- Preserves all player setup (recipe, station assignment, chemist
  configuration).

## Phase 0. Cleanup

Remove the iteration-7 cooldown code from `EmployeeReset/src/Mod.cs`. It
embodies the rejected approach.

Concretely:
- Remove `_cookCooldown` dictionary, `_cookCooldownSeconds` constant.
- Remove `TryPatchCanCookStart` method.
- Remove `CanCookStartPrefix` method.
- Remove the cooldown set-up block inside `SmartReset` (the
  `_cookCooldown[gid] = ...` lines and surrounding try/catch).
- Remove the `TryPatchCanCookStart()` call from `OnInitializeMelon`.

The diagnostic instrumentation (`TryInstrumentCookRoutine`,
`CookMoveNextPrefix`, `_diag*` flags, `_cookMoveNextCount`) stays. It is
useful for verifying the new fix.

## Phase 1. Reconnaissance (results)

Outcome: the original plan was over-engineered. Schedule I does not have
an explicit recipe object with an ingredient list. The "recipe" is
implicit. Defined by what items the player has loaded into the
station's input slots.

### Findings

**`Il2CppScheduleOne.Management.MixingStationConfiguration`** (decompiled)
has the following public fields, none of which are a recipe:

| Field | Type | Purpose |
| ----- | ---- | ------- |
| `AssignedChemist` | NPCField | which chemist is assigned to this station |
| `Destination` | ObjectField | where finished output goes |
| `StartThrehold` (sic, typo in vanilla) | NumberField | minimum quantity in input slots before chemist starts mixing |
| `DestinationRoute` | TransitRoute | transport route for finished output |

**There is no `Recipe` field.** The mix's "recipe" is the set of items
in `MixingStation.InputSlots` plus the lookup table that maps input-item
combinations to output-item combinations (an internal vanilla data set).

**`Il2CppScheduleOne.ObjectScripts.MixingStation`** exposes the input
slots directly:

| Member | Type | Notes |
| ------ | ---- | ----- |
| `InputSlots` | `List<ItemSlot>` | virtual property at line 1714 of the decompile |
| `ItemSlots` | `List<ItemSlot>` | base-class slots; superset that includes inputs and outputs |
| `CurrentMixOperation` | `MixOperation` | in-progress cook reference (line 1560) |
| `StationConfiguration` | `MixingStationConfiguration` | the configuration block above (line 1846) |

**`Il2CppScheduleOne.ItemFramework.ItemSlot`** is the per-slot data:

| Member | Type | Notes |
| ------ | ---- | ----- |
| `ItemInstance` | `ItemInstance` | the current item in the slot, or null if empty |
| `Quantity` | `int` | how many units (line 409) |
| `IsLocked` | `bool` | slot lock state (line 438) |

**Storage enumeration is not needed.** The user's reported bug is
"chemist runs out of ingredients to continue mixing." That maps to:
the chemist is at the station, the mix is running, an `InputSlot`
quantity dropped to zero, the next iteration of the mix tries to
consume from the empty slot and wedges. The check we need is therefore
against the station's own input slots, not against property storage.

### Decompiles produced

- `/tmp/mixconfig.cs`. 348 lines (MixingStationConfiguration)
- `/tmp/mixingstation.cs`. 3767 lines (MixingStation, has InputSlots)
- `/tmp/itemslot.cs`. 1007 lines (ItemSlot)
- `/tmp/startmix.cs`. 679 lines (StartMixingStationBehaviour)

These are local scratch decompiles, not committed to the repo.

### Implications for the fix

The Phase 2 design from the original plan is much simpler than originally
written. We do not need to:

- Walk property storage (we are not checking ingredient availability in
  storage; we are checking ingredient presence in the station's own
  input slots).
- Resolve `Property` -> storage list (we do not enumerate property
  storage).
- Identify items by `ItemDefinition` (we just need `Quantity > 0`
  per slot).
- Cache aggressively (the check is O(InputSlots.Count), typically 2-3
  iterations; caching may still help but is not load-bearing for
  performance).

## Phase 2. Predicate design (final, after /rtfm)

The /rtfm pass against ProduceMore (Thunderstore decompiled source)
revealed the canonical predicate. The original "walk InputSlots" idea
is wrong -- `InputSlots` on a MixingStation is a virtual override
whose contents we never confirmed. The actual model is named slots:
`ProductSlot` and `MixerSlot`. The predicate is:

```csharp
static bool CanMix(MixingStation station)
{
    if (station == null) return false;
    return station.GetMixQuantity() > 0;
    // GetMixQuantity returns Mathf.Min(
    //   ProductSlot.Quantity,
    //   MixerSlot.Quantity,
    //   MaxMixQuantity
    // )
}
```

Semantics: "the station has at least one product unit AND at least one
mixer unit AND has not hit its max-batch limit." If any of those is
zero, GetMixQuantity returns 0 and the chemist should not start a new
mix.

Per-iteration completeness: not checked here. The cook may still wedge
if a slot drains to zero mid-cook before the cook completes. The
behaviour selector should re-evaluate after each cook iteration; on the
next eval our predicate would see GetMixQuantity == 0 and block the
next attempt. An already-wedged in-progress cook still has to be killed
by the F8 hotkey. The predicate prevents future wedges but does not
unwedge an existing one.

## Phase 3. Harmony wiring

```csharp
[HarmonyPatch(typeof(StartMixingStationBehaviour), "CanCookStart")]
class CanCookStartIngredientGate
{
    static void Postfix(StartMixingStationBehaviour __instance, ref bool __result)
    {
        if (!__result) return;       // never override false to true
        if (__instance == null) return;
        try
        {
            if (!HasAllIngredientsCached(__instance))
                __result = false;
        }
        catch (Exception ex)
        {
            // Fail open: defer to vanilla on error so a bug in our
            // predicate cannot freeze the colony.
            MelonLogger.Warning($"[IngredientGate] exception, deferring to vanilla: {ex.Message}");
        }
    }
}
```

Postfix, not prefix. Vanilla makes the call, we only override `true` to
`false` when ingredients are missing. We never override `false` to `true`.

Patch installation runs from `OnInitializeMelon` alongside the existing
`TryHookEMployee` and `TryInstrumentCookRoutine` calls.

## Phase 4. Caching

`CanCookStart` runs on the behaviour-selector hot path: ~10-30 Hz per
chemist. Without caching, walking every storage container per call is
infeasible.

```csharp
private struct CacheKey
{
    public int stationId;
    public int recipeId;
}
private static readonly Dictionary<CacheKey, (float expiresAt, bool result)> _gateCache = new();
private const float _gateCacheTtl = 0.5f;

private static bool HasAllIngredientsCached(StartMixingStationBehaviour beh)
{
    var key = MakeKey(beh);
    if (_gateCache.TryGetValue(key, out var entry) && Time.realtimeSinceStartup < entry.expiresAt)
        return entry.result;

    bool r = HasAllIngredients(beh);
    _gateCache[key] = (Time.realtimeSinceStartup + _gateCacheTtl, r);
    return r;
}
```

Cache TTL of 0.5 s gives <=2 storage walks per second per station
regardless of vanilla's call frequency, while keeping the worst-case
"player restocks, chemist resumes" lag under half a second.

`MakeKey` should produce an integer pair from the station's NetworkObject
ID (or InstanceID) and the recipe's identifier. Record the recipe's
identifier path in Phase 1.

## Phase 5. Validation

| Test | Setup | Expected | Confirms |
| ---- | ----- | -------- | -------- |
| 1. Stuck chemist, missing ingredient | Save where chemist is wedged at station, recipe needs ingredient X, storage has zero of X | Chemist exits cook within ~1 s of save load (or on next behaviour eval), goes to idle, stays out. No NRE | Predicate returns false when ingredient missing |
| 2. Restock recovery | Test 1 setup, then player adds X to storage | Chemist resumes work within ~1 s of restock | Predicate flips true on availability return; cache TTL works |
| 3. Healthy operation | Chemist with full storage, recipe complete | Chemist starts and completes cooks normally; no measurable framerate impact | Postfix does not break happy path; cache prevents perf regression |
| 4. Multi-chemist | Two chemists, one station, one recipe | Only one cooks at a time (vanilla's NPCUserObject still works); both see same predicate result | Cache keyed by station works in multi-chemist case |
| 5. Multi-storage | Property has 3 storage containers, ingredient X spread across all 3 | Predicate sums correctly across containers | Storage enumeration covers full property |
| 6. Partial availability | Recipe needs A, B, C. Storage has A and B but not C | Predicate returns false (not "1 of 3 ingredients is missing, close enough") | Predicate is strict on completeness |

## Phase 6. Known edge cases

To investigate during Phase 1, address as scope expands:

1. **Pre-fetched ingredients in `MixingStation.InputSlots`.** If chemist
   has already grabbed some ingredients to the input slots before
   wedging, do those count toward "available"? Read `CookRoutine` flow
   to determine. Conservative answer: count input slots as available.
2. **Per-batch vs per-iteration quantity.** Recipe may say "needs 3 units
   to make 1 output". Predicate should check enough for one full output,
   not one iteration of an internal loop.
3. **Cross-property routes (eMployee feature).** A packager may be
   transporting items toward the chemist's property. Conservative
   answer: count only currently-present items.
4. **Locked or inaccessible storage.** If some storage on the property
   is filtered (e.g., reserved for output items), it should not count.
   Determine in Phase 1 whether the storage list has accessibility flags.
5. **Recipes that consume varying ingredient sets.** Some recipes may
   have alternative ingredients ("requires X OR Y"). Check whether the
   recipe data model supports this; if so, the check needs OR semantics.

## Status tracking

| Phase | Status |
| ----- | ------ |
| 0. Cleanup | Done. Cooldown code removed from `EmployeeReset/src/Mod.cs` |
| 1. Reconnaissance | Done. `MixingStationConfiguration` has no recipe field; the slots are named (`ProductSlot`, `MixerSlot`, `OutputSlot`) not a generic `InputSlots` |
| 1.5. /rtfm prior art | Done. ProduceMore mod's decompiled source confirmed the canonical predicate: `MixingStation.GetMixQuantity() > 0`. Their patch site is `MixingStation.CanStartMix`, not `StartMixingStationBehaviour.CanCookStart` |
| 2. Predicate design | Done (revised after /rtfm). Use `station.GetMixQuantity() <= 0` as the "block this" condition |
| 3. Harmony wiring | Done (revised iteration 11). Single postfix on `MixingStation.CanStartMix` only. The CanCookStart belt patch was removed because it over-blocked the chemist's "move output" task |
| 4. Caching | Skipped. Each check is O(1). One call to GetMixQuantity. No cache needed |
| 5. Validation | Pending. Build is deployed; awaiting test on the existing save with output items waiting |
| 6. Edge cases | Open: networked-multiplayer (`RpcLogic___StartCook_2166136261`), MixingStationMk2 variant |
| 7. Regressions encountered | Iteration 10: deep instrumentation crashed game on launch. Iteration 11: CanCookStart belt patch over-blocked move-output. Iteration 12: targetStation null write in SmartReset stranded chemists with no station to interact with. All three regressions were over-aggressive; the right answer in each case was to remove a patch or write |

Update this table as each phase completes.
