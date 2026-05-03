# Certainty tracking -- EmployeeReset ingredient gate

> The goal: know with 1000% empirical certainty that the ingredient gate
> fix actually solves the user's bug without regressing other behaviour.
> "Compiles" is not "works." This doc tracks what we have actually
> verified and what is still hypothesis.

## Progress score

**Last updated: 2026-05-03 12:36**

| Metric | Value |
| ------ | ----- |
| Verification rows VERIFIED | 1 / 17 |
| Verification rows PARTIALLY VERIFIED | 1 / 17 |
| Verification rows NOT VERIFIED | 15 / 17 |
| Hypotheses with empirical evidence | 1 / 7 (H6 partial) |
| Ship-blocking rows green (rows 1-13) | 1 / 13 |
| **Overall confidence the fix works in-game** | **5 / 10** |
| **Confidence the verification framework is sound** | **9 / 10** |

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
| TBD | iteration 8 (ingredient gate + deep instrumentation) | Deploy + game launch + F8 | Pending | -- |
| TBD | iteration 8 | Restock test (chemist with empty station, then player adds ingredients) | Pending | -- |
| TBD | iteration 8 | Healthy test (chemist with full storage, runs normally for several minutes) | Pending | -- |

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
