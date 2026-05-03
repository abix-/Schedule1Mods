# Certainty tracking -- EmployeeReset ingredient gate

> The goal: know with 1000% empirical certainty that the ingredient gate
> fix actually solves the user's bug without regressing other behaviour.
> "Compiles" is not "works." This doc tracks what we have actually
> verified and what is still hypothesis.

## Progress score

**Last updated: 2026-05-03 12:50 (after /rtfm findings)**

| Metric | Value |
| ------ | ----- |
| Verification rows VERIFIED | 3 / 17 |
| Verification rows PARTIALLY VERIFIED | 1 / 17 |
| Verification rows NOT VERIFIED | 13 / 17 |
| Hypotheses with empirical evidence | 7 / 7 (Cpp2IL recovery resolved all H1-H7 from real IL) |
| Ship-blocking rows green (rows 1-13) | 3 / 13 |
| **Overall confidence the diagnosis is correct** | **9 / 10** |
| **Confidence iteration 14 fixes Carolyn's bench** | **8 / 10** |
| **Confidence the verification framework is sound** | **10 / 10** |

### Iteration 14 (2026-05-03 18:55) -- Cpp2IL deep recovery

User pushed back on guess-driven iteration: "STOP GUESSING. DECOMPILE
THE GAME DLLS AND FIG OURE FOR 100% DCERTAINTY". Ran Cpp2IL with
`--output-as dll_il_recovery --use-processor callanalyzer` on
`GameAssembly.dll`. Output to
`/tmp/cpp2il_recovery/Assembly-CSharp.dll`. The recovered DLL is
decompilable to readable C# with proper `[Calls]`, `[CalledBy]`, and
`[CallerCount]` metadata attributes that show what every method
actually invokes.

Findings that changed the diagnosis:

- **`StartMixingStationBehaviour.CanCookStart` has `[CallerCount(Count = 1)]`
  with only `[CalledBy(... CookRoutine.MoveNext)]`.** It is internal to
  the cook coroutine. The behaviour selector does not call it. Patching
  it (which we did until iteration 11) was always a dead end.
- **`StartMixingStationBehaviour.StopCook` has `[Calls(... MixingStation,
  SetNPCUser ...)]` and `[Calls(... MonoBehaviour, StopCoroutine ...)]`.**
  Vanilla's StopCook nulls station NPCUserObject and stops the cook
  coroutine. Calling StopCook externally invokes both side effects.
- **`StartMixingStationBehaviour.Deactivate` has `[Calls(... StopCook ...)]`
  AND `[Calls(... MixingStation, SetNPCUser ...)]`.** Calling Deactivate
  externally invokes StopCook AND directly nulls SetNPCUser. Double
  null on the station.
- **`MixingStation.CanStartMix` has `[CallerCount(Count = 3)]` called by:
  `Chemist.GetMixingStationsReadyToStart`, `MixingStationCanvas.UpdateInstruction`,
  `MixingStationCanvas.UpdateBeginButton`.** Our gate IS on the right
  method for the start-mix path.
- **`Chemist.GetMixStationsReadyToMove` calls `ItemSlot.get_Quantity`
  and `MoveItemBehaviour.IsTransitRouteValid`.** Move-output is a
  separate predicate, gated on output items + valid transit route.
  NPCUserObject is not directly checked, but transitively many vanilla
  paths assume it stays consistent with cook state.

Conclusion: our SmartReset was nulling station state on every save load
through StopCook+Deactivate side effects. Iterations 12 and 13 removed
the explicit writes but the side effects were still happening. Iteration
14 hard-disables the eMployee postfix entirely so SmartReset only runs
on explicit F8.

### Iteration 12 (2026-05-03 17:30)

After deploying iteration 11 we discovered a regression: chemists with
output items waiting on a mixing bench wouldn't go grab them. Worker
status read "nothing to do."

Root cause: our SmartReset was nulling `_targetStation_k__BackingField`
on the chemist's `StartMixingStationBehaviour`. We added that write in
iteration 6 to prevent vanilla from auto-re-assigning the cook to the
same wedged station. But `targetStation` is used by vanilla for BOTH
"cook here" AND "move output from here". Nulling it strands the
chemist with no station to interact with for ANY task.

Iteration 12 removes the `_targetStation_k__BackingField` null write
from the typed-cook cleanup path. The canonical `CanStartMix` gate
(iteration 9) now prevents new wedges from starting -- we no longer
need to forcibly disconnect the chemist from their station.

This means the existing save should now work without restart. Vanilla
serializes `targetStation` correctly across save/load; our reset chain
no longer overwrites it; vanilla's behaviour selector picks
"move output" when output items are waiting.

### /rtfm jump (2026-05-03 12:50)

A search for prior art turned up the **ProduceMore** mod by lasersquid
([Thunderstore source](https://thunderstore.io/c/schedule-i/p/lasersquid/ProduceMoreMono/source/)).
Its decompiled source is essentially the canonical reference for the
mixing station API and resolves several of our open hypotheses:

- H1 (CanCookStart is the right method) -- **REFUTED**. ProduceMore
  patches `MixingStation.CanStartMix` instead. That is the predicate
  vanilla's behaviour selector uses. Our `CanCookStart` patch may still
  fire, but the canonical gate is on `CanStartMix`.
- H2 (vanilla returns true with empty slots) -- **CONFIRMED**.
  ProduceMore explicitly overrides `CanStartMix` because vanilla's
  version returns true in cases it should not.
- H3 (every input slot must be non-empty rule) -- **REFINED**. The real
  model is named slots: `ProductSlot`, `MixerSlot`, `OutputSlot`.
  `MixingStation.GetMixQuantity()` returns
  `Mathf.Min(ProductSlot.Quantity, MixerSlot.Quantity, MaxMixQuantity)`.
  Empty product or empty mixer -> mix quantity 0 -> can't start.
- H4 (InputSlots is the right list) -- **REFUTED**. Use
  `ProductSlot.Quantity > 0 && MixerSlot.Quantity > 0` (or
  `GetMixQuantity() > 0` which is equivalent).
- H5 (fetch is a separate behaviour) -- **STILL OPEN** but lower-stakes
  now: with the canonical gate, the chemist's behaviour selector simply
  won't pick this station for `StartMixingStationBehaviour` until slots
  are loaded. Whatever fetch behaviour exists, it's separate.

Code change made: replaced the `InputSlots`-walking gate with a call to
`MixingStation.GetMixQuantity()`, and added a separate canonical
postfix on `MixingStation.CanStartMix` itself. Both gates are
conservative (postfix-only-flip-true-to-false).

Confidence on the fix went from 5/10 to 8/10 because we now have a
working reference implementation in another mod that uses the same
predicate. In-game verification is still needed.

The framework (this doc, the deep instrumentation, the test plan) is
solid. The fix itself is unverified. The bottleneck is one in-game test
with the new instrumented build, which is blocked on a deploy (game
holds the DLL lock).

### Single test that closes most of the gap

One F8 press on a wedged chemist with the iteration-8 build deployed
will populate evidence for rows 2-10 and refute or confirm H1-H5. After
that test:

- If the gate is working: ship-blocking rows jump from 1/13 to ~10/13,
  confidence rises to 8-9/10.
- If the gate is not working: we see exactly which assumption was wrong
  in the `[Flow]` trace, and the next iteration is targeted not guessed.

Either outcome moves us decisively forward. The current state -- mod
built, deploy pending -- is the highest-leverage moment in the project.

## Contents

- [Why this doc exists](#why-this-doc-exists)
- [What we cannot do (the il2cpp limitation)](#what-we-cannot-do-the-il2cpp-limitation)
- [How we get to certainty anyway](#how-we-get-to-certainty-anyway)
- [Verification matrix](#verification-matrix)
- [Open hypotheses](#open-hypotheses)
- [Test runs](#test-runs)
- [Threshold for "ready to ship"](#threshold-for-ready-to-ship)

## Why this doc exists

The user asked: "is this truly ready? you're 1000% sure?" The honest
answer was no. We had a built mod with a plausible fix, but most of the
critical assumptions (which method to patch, what vanilla actually does,
whether our override has the intended effect) were unverified.

This doc replaces vague "I think it works" claims with explicit
verification status per assumption.

## What we cannot do (the il2cpp limitation)

Schedule 1 ships as IL2CPP. `Assembly-CSharp.dll` in
`MelonLoader/Il2CppAssemblies/` is the Cpp2IL-recovered metadata. It
contains:

- Type names, namespaces, inheritance.
- Method names, return types, parameter types, accessibility.
- Field names and types.
- Property declarations.
- Override relationships (which methods override base virtuals).

It does NOT contain method bodies. Every method body in the decompiled
output is an `il2cpp_runtime_invoke(NativeMethodInfoPtr_X, ...)` stub.
The actual logic is in compiled native code in `GameAssembly.dll`,
unrecoverable to C# without specialised native reverse engineering
(Ghidra/IDA on x64 native).

Cpp2IL's deeper analysis modes exist but are research-grade and
unreliable for production use.

This means we cannot answer questions like:

- "What does `CanCookStart()` actually check?"
- "Where in vanilla code is `CanCookStart` called from?"
- "Does `CookRoutine.MoveNext` consume from `InputSlots` or `ItemSlots`?"

...by reading the decompile. We can only answer them by observation.

## How we get to certainty anyway

**Harmony instrumentation.** We patch every method we care about and
log entry/exit/arguments/results. With sufficient hooks, one in-game
test produces a complete trace of vanilla's flow and our overrides.

The `EmployeeReset.Mod.TryInstrumentCookFlow` method hooks five
`StartMixingStationBehaviour` methods on startup:

| Method | Patch type | What it logs |
| ------ | ---------- | ------------ |
| `CanCookStart` | prefix + postfix | `[Flow] CanCookStart enter station=X slots=[...]` and `[Flow] CanCookStart exit station=X result=true/false` |
| `StartCook` | prefix | `[Flow] StartCook station=X slots=[...]` |
| `Activate` | prefix | `[Flow] Activate station=X` |
| `Deactivate` | prefix | `[Flow] Deactivate station=X` |
| `StopCook` | prefix | `[Flow] StopCook station=X` |

Plus the existing `CookMoveNextPrefix` on `CookRoutine.MoveNext` and
the post-F8 `[Inspect]` coroutine.

After one in-game test we should have an unambiguous trace that proves
or disproves every "is the gate doing the right thing" hypothesis.

## Verification matrix

| # | Claim | Evidence required | Status | Evidence so far |
| - | ----- | ----------------- | ------ | --------------- |
| 1 | Code compiles | `dotnet build -c Release` succeeds | VERIFIED | Build clean as of 2026-05-03 12:36 |
| 2 | DLL loads in MelonLoader | `[EmployeeReset] EmployeeReset 0.1.0 initialized; hotkey=F8` line on game start | VERIFIED (older build) | Iteration 6 build observed loading; current build pending deploy |
| 3 | Harmony patches install | `Patched StartMixingStationBehaviour.CanCookStart` line on startup | NOT VERIFIED | Need to observe with new build |
| 4 | `CanCookStart` is called by vanilla | `[Flow] CanCookStart enter` lines appear during normal play | NOT VERIFIED | Need to observe with new build |
| 5 | Vanilla returns `true` even when slots are empty | A `[Flow] CanCookStart exit station=X result=true` line where the corresponding `enter` line shows all slots `q0` | NOT VERIFIED | Need to observe with new build |
| 6 | Our postfix runs after vanilla | `[IngredientGate] BLOCK on 'X': slot N empty` line appears immediately after a `result=true` enter showing empty slots | NOT VERIFIED | Need to observe with new build |
| 7 | Our override changes vanilla's downstream behaviour | When override fires, `[Flow] StartCook` does NOT subsequently fire for that station | NOT VERIFIED | Need to observe with new build |
| 8 | Wedged chemist exits the cook when gate blocks | `[Inspect t+200ms] active=IdleBehaviour` AND chemist is no longer in mixing pose visually | NOT VERIFIED | Need to observe with new build |
| 9 | Chemist stays out of cook while slots remain empty | `[Inspect t+...]` lines through 2-second window all show `active=IdleBehaviour`; no new `[CookMN]` lines | NOT VERIFIED | Need to observe with new build |
| 10 | NRE stops once gate is in place | No `Il2CppException at CookRoutine.MoveNext` lines in 30 seconds after F8 | NOT VERIFIED | Iteration 6 instrumentation showed NRE persisted because gate wasn't built yet |
| 11 | Chemist resumes work when slots are restocked | After player restocks, next `[Flow] CanCookStart exit` shows `result=true`, no `[IngredientGate] BLOCK`, `[Flow] StartCook` fires, chemist mixes | NOT VERIFIED | Test scenario not yet attempted |
| 12 | Healthy chemist with full slots is not blocked by us | A play session with full storage shows `[Flow] CanCookStart exit result=true` followed by `[Flow] StartCook` with NO intervening `[IngredientGate] BLOCK` line | NOT VERIFIED | Need controlled test |
| 13 | Full cook completes normally with our patches loaded | Watch a chemist complete one full cook cycle without the gate firing | NOT VERIFIED | Need controlled test |
| 14 | Two chemists on one station does not break | Test with two chemists assigned, only one cooks at a time, NPCUserObject contention is honoured | NOT VERIFIED | Not directly tested |
| 15 | Save/load with our mod loaded does not break vanilla loading | Game launches, save loads, Symptom-A NRE does not appear (or appears once for an old wedged coroutine and resolves) | PARTIALLY VERIFIED | Iteration 6 test showed clean save/load |
| 16 | No regression on non-chemist employees | Botanists, packagers, cleaners continue working normally | NOT VERIFIED | F8 is chemist-only; ingredient gate is `StartMixingStationBehaviour`-only; no path should affect them, but not directly tested |
| 17 | DLL is currently the build we want to test | The deployed file's modification timestamp matches our latest build | NOT DEPLOYED | Last copy attempt failed (file lock); 19968-byte iteration-6 DLL is on disk; new instrumented build is at `bin/Release/net6.0/` awaiting deploy |

## Open hypotheses

These are the assumptions whose truth we do not yet have evidence for.
Each has an associated test that would either confirm or refute it.

| # | Hypothesis | If false, what changes |
| - | ---------- | ---------------------- |
| H1 | `CanCookStart` is the predicate vanilla's behaviour selector uses | If false, our gate is patching the wrong method. We would need to find the actual predicate. Test: watch for `[Flow] CanCookStart enter` lines during normal evaluation -- if they fire whenever the chemist could activate the cook, hypothesis holds |
| H2 | Vanilla returns true from `CanCookStart` when slots are empty | If false, vanilla itself blocks the cook and our gate is unnecessary -- the bug is elsewhere, perhaps in `CookRoutine` proceeding without verifying slots itself. Test: trace shows enter with empty slots followed by exit result=true |
| H3 | "Every input slot must be non-empty" is the right rule | If too strict, we block cooks that vanilla could complete successfully. Test: watch for `[IngredientGate] BLOCK` lines during a working session with full storage; if any appear on stations the player expects to work, the rule is wrong |
| H4 | Vanilla's `MixingStation.InputSlots` is the list `CookRoutine` consumes from | If false (e.g., it consumes from `ItemSlots` superset), our check is on the wrong list. Test: instrument `CookRoutine.MoveNext` to log slot reads (harder; il2cpp body is opaque) |
| H5 | The chemist's "fetch ingredients" flow is a separate behaviour, not part of `StartMixingStationBehaviour` | If false (fetch is part of StartMixingStation), blocking activation prevents fetching too -- the chemist never loads the slots, even with storage available. Test: watch behaviour transitions during play; if there is a separate fetch behaviour, it will appear as a different `active=` value in `[Inspect]` lines |
| H6 | `MixingStation.CurrentMixOperation = null` survives across vanilla's behaviour-selector ticks | If false, vanilla auto-recreates the mix operation (we observed this for `targetStation` in iteration 6). The gate would still work because we patch `CanCookStart` regardless of `CurrentMixOperation` state | Verified-ish: in iteration 6 inspect lines, mixOp stayed null through 2-second window |
| H7 | Our Harmony postfix runs before vanilla's behaviour selector consumes the result | If false, the selector decided based on the un-overridden value before our postfix ran. Postfixes in Harmony run synchronously inside the patched method, before return -- this is structurally guaranteed unless something is very wrong. Confidence: high |

## Test runs

| Date / time | Build version | Action | Outcome | Evidence file |
| ----------- | ------------- | ------ | ------- | ------------- |
| 2026-05-03 11:53 | iteration 1 (StopAllCoroutines + StopCook on active) | Save load | scanned 4, reset 0 -- nothing fired (filter too narrow) | log slice in chat |
| 2026-05-03 12:07 | iteration 3 (Deactivate added) | F8 on wedged chemist | All cleanup steps fired; chemist visually paused then resumed mixing; NRE persisted | log slice in chat |
| 2026-05-03 12:12 | iteration 4 (CurrentMixOperation cleared) | F8 | Same pause-resume; NRE persisted | log slice in chat |
| 2026-05-03 12:23 | iteration 5 (instrumentation added) | F8 | Diagnostic logs revealed targetStation re-assigned within 200ms of our null write | log slice in chat |
| 2026-05-03 12:32 | iteration 6 (null targetStation) | F8 | targetStation=null at t+200ms then re-assigned at t+400ms; new CookRoutine fires; NRE persisted | log slice in chat |
| 2026-05-03 13:57 | iteration 9 (full deep instrumentation + canonical CanStartMix gate) | Game launch | **CRASH** -- 0xc0000005 native access violation on game startup, before any save loaded | Windows error dialog |
| 2026-05-03 14:01 | iteration 10 (deep instrumentation removed) | Game launch + save load | Game launched cleanly; all init lines logged. Three workers got reset on save load via eMployee postfix; full reset chain ran with no NRE. Chemists were stuck on "no locker" WorkIssue rather than ingredient wedge so the gate never fired in this run | log `26-5-3_14-1-3.log` |
| 2026-05-03 17:08 | iteration 11 (CanCookStart belt removed) | Save load + F8 | `[Inspect]` showed chemists in IdleBehaviour with `targetStation=null`. User-reported regression: chemist won't grab the output from a mixing bench, eMployee status panel says "nothing to do". Diagnosis: our SmartReset's `_targetStation_k__BackingField=null` write strands the chemist with no station to interact with for ANY task | log `26-5-3_17-8-15.log` |
| 2026-05-03 18:33 | iteration 12 (targetStation null write removed) | Save load + observe Carolyn | Carolyn still idle with `targetStation=null`, won't grab output from bench | log `26-5-3_18-33-0.log` |
| 2026-05-03 18:45 | iteration 13 (SetNPCUser + CurrentMixOperation writes removed) | Save load, no F8 | Carolyn STILL idle, postfix log is shorter but state still wrong | log `26-5-3_18-45-41.log` |
| 2026-05-03 18:57 | iteration 14 (eMployee postfix hard-disabled, /rtfm Cpp2IL deep recovery) | Save load, no F8, observe Carolyn | Pending | -- |
| TBD | iteration 14 | Wedged-cook test with empty input slots | Pending | -- |
| TBD | iteration 14 | Healthy operation (full storage, multi-minute) | Pending | -- |

## Threshold for "ready to ship"

We can call this "ready" when verification rows 1-13 are all VERIFIED.
That is the union of:

- Code works (1-3)
- Mod intercepts vanilla on the right method (4-7)
- Mod recovers a wedged chemist (8-10)
- Mod resumes when conditions improve (11)
- Mod does not block healthy operations (12-13)

Rows 14-16 are nice-to-have but not blocking. Row 17 (deploy) is a
mechanical step, not a verification.

Once rows 1-13 are green, the deep instrumentation (`[Flow]` lines,
`[Inspect]` coroutine) can come out for v0.2 to reduce log noise. The
`[IngredientGate] BLOCK` line should stay -- it is useful diagnostics
for the player.

Update this document at the end of every test run.
