using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.AI;

using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.ObjectScripts;

// Il2CppScheduleOne.NPCs.Behaviour.Behaviour clashes with UnityEngine.Behaviour.
// We always mean the chemist's behaviour base class; alias it to keep code readable.
using SBehaviour = Il2CppScheduleOne.NPCs.Behaviour.Behaviour;

namespace EmployeeReset;

public class Mod : MelonMod
{
    private const string PrefCategory = "EmployeeReset";

    private static MelonPreferences_Category _prefs;
    private static MelonPreferences_Entry<KeyCode> _hotkeyPref;
    private static MelonPreferences_Entry<bool> _hookEMployeePref;
    private static MelonPreferences_Entry<bool> _verboseLogPref;

    // -------------------------------------------------------------------------
    //  Diagnostic instrumentation (captured for one round of telemetry)
    // -------------------------------------------------------------------------

    // Hash codes of CookRoutine state-machine instances we have observed
    // running MoveNext. Maps hash -> MoveNext call count.
    private static readonly Dictionary<int, int> _cookMoveNextCount = new();

    // Set to true on F8. While true, every CookRoutine MoveNext is logged
    // (until we hit _diagMoveNextLimit). After the limit, only state-change
    // and instance-change events are logged.
    private static bool _diagActive = false;
    private static int _diagMoveNextLogged = 0;
    private const int _diagMoveNextLimit = 30;
    private static int _diagLastHash = 0;
    private static int _diagLastState = -999;

    public override void OnInitializeMelon()
    {
        _prefs = MelonPreferences.CreateCategory(PrefCategory, "Employee Reset");
        _hotkeyPref       = _prefs.CreateEntry("Hotkey", KeyCode.F8,
            "Hotkey", "Press to smart-reset all stuck employees on the current property");
        _hookEMployeePref = _prefs.CreateEntry("HookEMployeeAutoReset", true,
            "Hook eMployee AUTO-RESET", "If true, our smart cleanup also runs after eMployee's ResetEmployeeCore");
        _verboseLogPref   = _prefs.CreateEntry("VerboseLog", true,
            "Verbose Log", "Log every reset step");

        if (_hookEMployeePref.Value)
            TryHookEMployee();

        // Deep instrumentation removed in iteration 10 -- caused 0xc0000005
        // crash on game launch. CanCookStart postfix removed in iteration 11
        // because it was over-blocking the chemist's "move output to
        // destination" behaviour: when inputs were empty but output had
        // items, our gate said the chemist couldn't interact with the
        // station at all, so they reported "nothing to do" instead of
        // moving the output. Keep only the canonical CanStartMix gate
        // (per ProduceMore reference).
        TryPatchCanStartMixIngredientGate();

        MelonLogger.Msg($"EmployeeReset 0.1.0 initialized; hotkey={_hotkeyPref.Value}");
    }

    // -------------------------------------------------------------------------
    //  Deep instrumentation: log every CanCookStart, StartCook, Activate call
    //  on StartMixingStationBehaviour. One F8 press with this on gives us the
    //  empirical ground truth we cannot get from the il2cpp decompile.
    // -------------------------------------------------------------------------

    private static int _flowLogCount = 0;
    private const int _flowLogLimit = 200;

    private void TryInstrumentCookFlow()
    {
        try
        {
            Type t = typeof(StartMixingStationBehaviour);

            MethodInfo can = t.GetMethod("CanCookStart",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (can != null)
            {
                MethodInfo prePre = typeof(Mod).GetMethod(nameof(TraceCanCookStartPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo postPost = typeof(Mod).GetMethod(nameof(TraceCanCookStartPostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                HarmonyInstance.Patch(can,
                    prefix: new HarmonyMethod(prePre),
                    postfix: new HarmonyMethod(postPost));
                MelonLogger.Msg("Traced CanCookStart");
            }

            MethodInfo start = t.GetMethod("StartCook",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (start != null)
            {
                MethodInfo pre = typeof(Mod).GetMethod(nameof(TraceStartCookPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                HarmonyInstance.Patch(start, prefix: new HarmonyMethod(pre));
                MelonLogger.Msg("Traced StartCook");
            }

            MethodInfo activate = t.GetMethod("Activate",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (activate != null)
            {
                MethodInfo pre = typeof(Mod).GetMethod(nameof(TraceActivatePrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                HarmonyInstance.Patch(activate, prefix: new HarmonyMethod(pre));
                MelonLogger.Msg("Traced StartMixingStationBehaviour.Activate");
            }

            MethodInfo deact = t.GetMethod("Deactivate",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (deact != null)
            {
                MethodInfo pre = typeof(Mod).GetMethod(nameof(TraceDeactivatePrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                HarmonyInstance.Patch(deact, prefix: new HarmonyMethod(pre));
                MelonLogger.Msg("Traced StartMixingStationBehaviour.Deactivate");
            }

            MethodInfo stop = t.GetMethod("StopCook",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (stop != null)
            {
                MethodInfo pre = typeof(Mod).GetMethod(nameof(TraceStopCookPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                HarmonyInstance.Patch(stop, prefix: new HarmonyMethod(pre));
                MelonLogger.Msg("Traced StopCook");
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"TryInstrumentCookFlow failed: {ex}");
        }
    }

    private static string SlotSummary(StartMixingStationBehaviour beh)
    {
        try
        {
            MixingStation s = beh?.targetStation;
            if (s == null) return "noStation";
            var slots = s.InputSlots;
            if (slots == null) return "slots=null";
            string sep = "";
            string r = "[";
            for (int i = 0; i < slots.Count; i++)
            {
                ItemSlot sl = slots[i];
                int q = (sl != null) ? sl.Quantity : -1;
                string item;
                try { item = (sl != null && sl.ItemInstance != null) ? sl.ItemInstance.GetType().Name : "empty"; }
                catch { item = "?"; }
                r += $"{sep}{i}:{item}q{q}";
                sep = ",";
            }
            return r + "]";
        }
        catch { return "ERR"; }
    }

    private static void Flow(string s)
    {
        if (_flowLogCount >= _flowLogLimit) return;
        _flowLogCount++;
        MelonLogger.Msg($"[Flow] {s}");
    }

    private static void TraceCanCookStartPrefix(StartMixingStationBehaviour __instance)
    {
        Flow($"CanCookStart enter station={SafeStation(__instance)} slots={SlotSummary(__instance)}");
    }

    private static void TraceCanCookStartPostfix(StartMixingStationBehaviour __instance, ref bool __result)
    {
        Flow($"CanCookStart exit station={SafeStation(__instance)} result={__result}");
    }

    private static void TraceStartCookPrefix(StartMixingStationBehaviour __instance)
    {
        Flow($"StartCook station={SafeStation(__instance)} slots={SlotSummary(__instance)}");
    }

    private static void TraceActivatePrefix(StartMixingStationBehaviour __instance)
    {
        Flow($"Activate station={SafeStation(__instance)}");
    }

    private static void TraceDeactivatePrefix(StartMixingStationBehaviour __instance)
    {
        Flow($"Deactivate station={SafeStation(__instance)}");
    }

    private static void TraceStopCookPrefix(StartMixingStationBehaviour __instance)
    {
        Flow($"StopCook station={SafeStation(__instance)}");
    }

    private static string SafeStation(StartMixingStationBehaviour beh)
    {
        try
        {
            MixingStation s = beh?.targetStation;
            return s != null ? s.Name : "null";
        }
        catch { return "ERR"; }
    }

    // -------------------------------------------------------------------------
    //  Ingredient gate -- the actual root-cause fix
    // -------------------------------------------------------------------------

    /// <summary>
    /// Harmony-postfix CanCookStart so the predicate also requires every
    /// MixingStation input slot to have Quantity > 0. Vanilla's predicate
    /// is incomplete -- it returns true when it should not, leading to
    /// the chemist starting a cook that wedges mid-loop on an empty slot.
    /// Our postfix only ever flips true -> false; it never overrides a
    /// vanilla "no" to "yes".
    /// </summary>
    private void TryPatchCanCookStartIngredientGate()
    {
        try
        {
            MethodInfo target = typeof(StartMixingStationBehaviour).GetMethod(
                "CanCookStart",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (target == null)
            {
                MelonLogger.Warning("CanCookStart not found; ingredient gate will not be installed");
                return;
            }
            MethodInfo postfix = typeof(Mod).GetMethod(nameof(CanCookStartIngredientGatePostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            HarmonyInstance.Patch(target, postfix: new HarmonyMethod(postfix));
            MelonLogger.Msg("Ingredient gate installed on StartMixingStationBehaviour.CanCookStart");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"TryPatchCanCookStartIngredientGate failed: {ex}");
        }
    }

    private static int _gateLogCount = 0;
    private const int _gateLogLimit = 20;

    /// <summary>
    /// The canonical fix per ProduceMore mod: postfix MixingStation.CanStartMix
    /// to require ProductSlot and MixerSlot have items. Reference behaviour:
    /// __result = (GetMixQuantity() > 0) && (OutputSlot.Quantity == 0)
    /// Our version is conservative: only flip true to false; never override
    /// vanilla's false to true.
    /// </summary>
    private void TryPatchCanStartMixIngredientGate()
    {
        try
        {
            MethodInfo target = typeof(MixingStation).GetMethod(
                "CanStartMix",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (target == null)
            {
                MelonLogger.Warning("MixingStation.CanStartMix not found; canonical gate not installed");
                return;
            }
            MethodInfo postfix = typeof(Mod).GetMethod(nameof(CanStartMixIngredientGatePostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            HarmonyInstance.Patch(target, postfix: new HarmonyMethod(postfix));
            MelonLogger.Msg("Canonical ingredient gate installed on MixingStation.CanStartMix");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"TryPatchCanStartMixIngredientGate failed: {ex}");
        }
    }

    private static void CanStartMixIngredientGatePostfix(MixingStation __instance, ref bool __result)
    {
        if (!__result) return;
        if (__instance == null) return;
        try
        {
            int q = 0;
            try { q = __instance.GetMixQuantity(); } catch { }
            if (q <= 0)
            {
                __result = false;
                if (_verboseLogPref?.Value == true && _gateLogCount < _gateLogLimit)
                {
                    _gateLogCount++;
                    string n = "?";
                    try { n = __instance.Name; } catch { }
                    int p = -1, m = -1, o = -1;
                    try { p = __instance.ProductSlot != null ? __instance.ProductSlot.Quantity : -2; } catch { }
                    try { m = __instance.MixerSlot != null ? __instance.MixerSlot.Quantity : -2; } catch { }
                    try { o = __instance.OutputSlot != null ? __instance.OutputSlot.Quantity : -2; } catch { }
                    MelonLogger.Msg($"[IngredientGate] CanStartMix BLOCK on '{n}': product={p} mixer={m} output={o} mixQty={q}");
                }
            }
        }
        catch
        {
            // Fail open
        }
    }

    /// <summary>
    /// Postfix that uses the canonical MixingStation slot model:
    /// ProductSlot + MixerSlot must both have Quantity > 0 (the same check
    /// MixingStation.GetMixQuantity() uses). Reference: ProduceMore mod
    /// by lasersquid demonstrates these are the slots that matter.
    /// </summary>
    private static void CanCookStartIngredientGatePostfix(StartMixingStationBehaviour __instance, ref bool __result)
    {
        if (!__result) return;                  // already false; respect vanilla's no
        if (__instance == null) return;
        try
        {
            MixingStation station = __instance.targetStation;
            if (station == null) return;

            // Use GetMixQuantity which is min(ProductSlot.Quantity, MixerSlot.Quantity, MaxMixQuantity).
            // If either input slot is empty, this returns 0.
            int mixQty = 0;
            try { mixQty = station.GetMixQuantity(); } catch { }
            if (mixQty <= 0)
            {
                __result = false;
                MaybeLogGate(__instance, "GetMixQuantity()=0");
                return;
            }
        }
        catch (Exception ex)
        {
            // Fail open. Defer to vanilla's answer.
            if (_verboseLogPref?.Value == true)
                MelonLogger.Warning($"[IngredientGate] exception, deferring to vanilla: {ex.Message}");
        }
    }

    private static void MaybeLogGate(StartMixingStationBehaviour beh, string reason)
    {
        if (_verboseLogPref?.Value != true) return;
        if (_gateLogCount >= _gateLogLimit) return;
        _gateLogCount++;
        try
        {
            string stationName = beh.targetStation != null ? beh.targetStation.Name : "?";
            MelonLogger.Msg($"[IngredientGate] BLOCK on '{stationName}': {reason}");
        }
        catch { }
    }

    /// <summary>
    /// Find StartMixingStationBehaviour's nested CookRoutine state-machine
    /// type and Harmony-prefix its MoveNext, so we can observe which
    /// instance fires MoveNext and when.
    /// </summary>
    private void TryInstrumentCookRoutine()
    {
        try
        {
            Type host = typeof(StartMixingStationBehaviour);
            Type[] nested = host.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public);
            int patched = 0;
            foreach (Type t in nested)
            {
                MethodInfo mn = t.GetMethod("MoveNext",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mn == null) continue;
                MethodInfo prefix = typeof(Mod).GetMethod(nameof(CookMoveNextPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);
                HarmonyInstance.Patch(mn, prefix: new HarmonyMethod(prefix));
                patched++;
                MelonLogger.Msg($"Instrumented {t.FullName}.MoveNext");
            }
            if (patched == 0)
                MelonLogger.Warning("No CookRoutine nested types found to instrument");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"TryInstrumentCookRoutine failed: {ex}");
        }
    }

    private static void CookMoveNextPrefix(object __instance)
    {
        if (__instance == null) return;
        try
        {
            int hash = __instance.GetHashCode();
            int state = -999;
            try
            {
                PropertyInfo sp = __instance.GetType().GetProperty("__1__state",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (sp != null) state = (int)sp.GetValue(__instance);
            }
            catch { }

            if (!_cookMoveNextCount.TryGetValue(hash, out int count)) count = 0;
            _cookMoveNextCount[hash] = ++count;

            // Log conditions:
            //  - Diagnostic mode (post-F8): log every MoveNext up to the limit.
            //  - Always log when we see a NEW instance hash (vanilla started a fresh cook).
            //  - Always log when state changes on an existing hash.
            bool newInstance = hash != _diagLastHash;
            bool stateChanged = state != _diagLastState;
            bool diagFresh = _diagActive && _diagMoveNextLogged < _diagMoveNextLimit;

            if (newInstance || stateChanged || diagFresh)
            {
                MelonLogger.Msg($"[CookMN] hash={hash} state={state} count={count}{(newInstance ? " NEW-INSTANCE" : "")}{(stateChanged ? " STATE-CHANGE" : "")}");
                _diagLastHash = hash;
                _diagLastState = state;
                if (diagFresh) _diagMoveNextLogged++;
            }
        }
        catch { }
    }

    public override void OnUpdate()
    {
        if (Input.GetKeyDown(_hotkeyPref.Value))
            ResetAllStuckChemists();
    }

    // -------------------------------------------------------------------------
    //  Top-level reset entry points
    // -------------------------------------------------------------------------

    private static void ResetAllStuckChemists()
    {
        EmployeeManager mgr = EmployeeManager.Instance;
        if (mgr == null)
        {
            MelonLogger.Warning("[Reset] EmployeeManager.Instance is null; not in-game?");
            return;
        }

        int total = 0;
        int reset = 0;
        Il2CppSystem.Collections.Generic.List<Employee> all = mgr.AllEmployees;
        if (all == null) return;

        int skippedNonChemist = 0;
        for (int i = 0; i < all.Count; i++)
        {
            Employee emp = all[i];
            if (emp == null) continue;
            total++;

            // F8 hotkey is chemist-only by user preference: this is the
            // role that exhibits the wedged-cook bug. Botanist, Packager,
            // and Cleaner are reachable via eMployee's manual reset and
            // via our postfix on eMployee's AUTO-RESET; the F8 path is
            // narrowly scoped to avoid disturbing healthy non-chemists.
            if (emp.TryCast<Chemist>() == null)
            {
                skippedNonChemist++;
                continue;
            }

            if (SmartReset(emp))
                reset++;
        }

        MelonLogger.Msg($"[Reset] hotkey: scanned {total} (skipped {skippedNonChemist} non-chemist), reset {reset}");

        // Diagnostic: after F8, snapshot chemist state every 200ms for 2s
        // so we can see what vanilla does AFTER our reset returns. Resets
        // _diagActive flag so MoveNext logs every call for the next ~30 calls.
        _diagActive = true;
        _diagMoveNextLogged = 0;
        _cookMoveNextCount.Clear();
        MelonCoroutines.Start(PostResetInspector(all));
    }

    private static System.Collections.IEnumerator PostResetInspector(
        Il2CppSystem.Collections.Generic.List<Employee> all)
    {
        // Sample 10 times at ~200ms each (12 frames at 60fps)
        for (int sample = 0; sample < 10; sample++)
        {
            // Wait ~12 frames
            for (int f = 0; f < 12; f++) yield return null;

            for (int i = 0; i < all.Count; i++)
            {
                Employee emp = all[i];
                if (emp == null) continue;
                Chemist c = emp.TryCast<Chemist>();
                if (c == null) continue;

                string activeName;
                try
                {
                    SBehaviour active = ((NPC)emp).Behaviour?.activeBehaviour;
                    activeName = active != null ? (active.GetIl2CppType()?.Name ?? "?") : "null";
                }
                catch { activeName = "ERR"; }

                string targetStation = "?";
                string mixOpStr = "?";
                string npcUserStr = "?";
                try
                {
                    StartMixingStationBehaviour cook = c.StartMixingStationBehaviour;
                    if (cook != null)
                    {
                        MixingStation s = cook.targetStation;
                        targetStation = s != null ? s.Name : "null";
                        if (s != null)
                        {
                            try { mixOpStr = s.CurrentMixOperation != null ? "SET" : "null"; } catch { mixOpStr = "ERR"; }
                            try { npcUserStr = s.NPCUserObject != null ? "SET" : "null"; } catch { npcUserStr = "ERR"; }
                        }
                    }
                    else targetStation = "noBehav";
                }
                catch (Exception ex) { targetStation = $"ERR:{ex.Message}"; }

                int ticks = 0;
                try { ticks = emp.TicksSinceLastWork; } catch { }

                MelonLogger.Msg($"[Inspect t+{(sample+1)*200}ms] {SafeName(emp)}: active={activeName} targetStation={targetStation} mixOp={mixOpStr} npcUser={npcUserStr} ticks={ticks}");
            }
        }
        _diagActive = false;
        MelonLogger.Msg("[Inspect] post-reset window complete");
    }

    /// <summary>
    /// Heuristic for "looks stuck": active behaviour is a work behaviour that
    /// has been on the stack with no work-tick progress. We do not try to
    /// duplicate eMployee's full STUCK-DETECT logic; we just want a coarse
    /// filter so the hotkey does not slam every healthy worker.
    /// </summary>
    private static bool LooksStuck(Employee emp)
    {
        if (emp == null) return false;
        NPCBehaviour beh = ((NPC)emp).Behaviour;
        if (beh == null) return false;
        SBehaviour active = beh.activeBehaviour;
        if (active == null) return false;

        // Always-OK behaviours.
        string name = active.GetIl2CppType()?.Name ?? "";
        if (name == "IdleBehaviour" || name == "StationaryBehaviour" || name == "GenericDialogueBehaviour")
            return false;

        // TicksSinceLastWork is incremented by vanilla every NPC tick that does
        // not produce productive output. eMployee's threshold is 30; we use the
        // same value so the hotkey covers what AUTO-RESET would have covered.
        return emp.TicksSinceLastWork >= 30;
    }

    // -------------------------------------------------------------------------
    //  The smart reset itself
    // -------------------------------------------------------------------------

    /// <summary>
    /// Comprehensive reset that addresses the root cause eMployee misses:
    /// the MixingStation's NPCUserObject reservation, the behaviour's running
    /// coroutine, and the active behaviour reference. Returns true if any
    /// cleanup action ran.
    /// </summary>
    public static bool SmartReset(Employee emp)
    {
        if (emp == null) return false;
        bool didAnything = false;
        string who = SafeName(emp);

        try
        {
            NPCBehaviour beh = ((NPC)emp).Behaviour;
            SBehaviour active = beh?.activeBehaviour;

            // 0. Zero pathing-failure counters via the typed property setter.
            //    NB: on the il2cpp bridge type these are PROPERTIES, not
            //    fields; using GetField would silently return null. eMployee's
            //    reset has this exact bug (their line 2585-2608) -- their
            //    counter-zero is a no-op. We use the typed property here.
            try { emp.consecutivePathingFailures = 0; }
            catch (Exception ex) { Log(true, $"[Reset] {who}: clear NPC pathFails threw {ex.Message}"); }

            // 0a. CHEMIST-SPECIFIC: reach the StartMixingStationBehaviour
            //     component directly via Chemist.StartMixingStationBehaviour
            //     (typed property, line 530 of decompiled Chemist). This
            //     catches wedged CookRoutine coroutines even when
            //     _activeBehaviour is currently null (e.g. eMployee already
            //     reset the NPC but the coroutine on the MonoBehaviour kept
            //     running, or vanilla flipped the chemist to a different
            //     behaviour). The cook component lives on the chemist's
            //     GameObject regardless of activeBehaviour state.
            Chemist chemist = emp.TryCast<Chemist>();
            if (chemist != null)
            {
                StartMixingStationBehaviour typedCook = chemist.StartMixingStationBehaviour;
                if (typedCook != null)
                {
                    try
                    {
                        typedCook.StopCook();
                        didAnything = true;
                        Log(_verboseLogPref?.Value == true,
                            $"[Reset] {who}: typed StopCook() on chemist's StartMixingStationBehaviour");
                    }
                    catch (Exception ex)
                    {
                        Log(true, $"[Reset] {who}: typed StopCook() threw {ex.Message}");
                    }

                    MixingStation typedStation = typedCook.targetStation;
                    if (typedStation != null)
                    {
                        try
                        {
                            typedStation.SetNPCUser(null);
                            didAnything = true;
                            Log(_verboseLogPref?.Value == true,
                                $"[Reset] {who}: typed SetNPCUser(null) on '{typedStation.Name}'");
                        }
                        catch (Exception ex)
                        {
                            Log(true, $"[Reset] {who}: typed SetNPCUser(null) threw {ex.Message}");
                        }

                        // CurrentMixOperation is the station's "in-progress
                        // cook" reference (line 1560 of decompiled
                        // MixingStation). Without nulling this, vanilla's
                        // behaviour selector sees an active mix operation and
                        // immediately re-picks StartMixingStationBehaviour
                        // after our reset, putting the chemist right back in
                        // the wedged cook.
                        try
                        {
                            typedStation.CurrentMixOperation = null;
                            didAnything = true;
                            Log(_verboseLogPref?.Value == true,
                                $"[Reset] {who}: typed CurrentMixOperation=null on '{typedStation.Name}'");
                        }
                        catch (Exception ex)
                        {
                            Log(true, $"[Reset] {who}: typed CurrentMixOperation=null threw {ex.Message}");
                        }
                    }

                    // Stop coroutines on the chemist's MixingStationBehaviour
                    // MonoBehaviour regardless of active state.
                    MonoBehaviour typedMb = typedCook.TryCast<MonoBehaviour>();
                    if (typedMb != null)
                    {
                        try
                        {
                            typedMb.StopAllCoroutines();
                            didAnything = true;
                            Log(_verboseLogPref?.Value == true,
                                $"[Reset] {who}: typed StopAllCoroutines on chemist's StartMixingStationBehaviour");
                        }
                        catch (Exception ex)
                        {
                            Log(true, $"[Reset] {who}: typed StopAllCoroutines threw {ex.Message}");
                        }
                    }

                    // Deactivate is the vanilla "stop being the active
                    // behaviour, unwind animation/position lock" hook.
                    // StartMixingStationBehaviour overrides it (line 474 of
                    // the decompile) which means it has chemist-specific
                    // cleanup we cannot replicate by ourselves. Call it
                    // last because it expects the cook coroutine and station
                    // reservation to already be done.
                    try
                    {
                        typedCook.Deactivate();
                        didAnything = true;
                        Log(_verboseLogPref?.Value == true,
                            $"[Reset] {who}: typed Deactivate() on chemist's StartMixingStationBehaviour");
                    }
                    catch (Exception ex)
                    {
                        Log(true, $"[Reset] {who}: typed Deactivate() threw {ex.Message}");
                    }

                    // ITERATION 12: do NOT null targetStation. The canonical
                    // CanStartMix gate now prevents new wedges from starting,
                    // so we no longer need to forcibly disconnect the
                    // chemist from their station. Vanilla uses targetStation
                    // for BOTH "cook here" AND "move output from here"; if
                    // we null it the chemist has no station to interact with
                    // and reports "nothing to do" even when output items
                    // need to be moved.
                }
            }

            // 1. If active is a chemist work behaviour, call its dedicated
            //    Stop method. StopCook() is the vanilla public API for ending
            //    a cook session cleanly -- presumed (but not verified) to
            //    release MixingStation.NPCUserObject and clear MixOperation.
            //    We do not call Behaviour.Disable() because its body is
            //    unreadable from the il2cpp decompile and we have no evidence
            //    it does the right thing for a chemist mid-cook.
            if (active != null)
            {
                StartMixingStationBehaviour cook = active.TryCast<StartMixingStationBehaviour>();
                if (cook != null)
                {
                    MixingStation station = cook.targetStation;

                    // 1a. Call StopCook() first. This is vanilla's intended
                    //     end-of-cook hook. If it works as expected the next
                    //     two steps are belt-and-suspenders.
                    try
                    {
                        cook.StopCook();
                        didAnything = true;
                        Log(_verboseLogPref?.Value == true, $"[Reset] {who}: StopCook() on active behaviour");
                    }
                    catch (Exception ex)
                    {
                        Log(true, $"[Reset] {who}: StopCook() threw {ex.Message}");
                    }

                    // 1b. Belt-and-suspenders: if station is still alive and
                    //     still references some NPC, null NPCUserObject. Safe
                    //     even if StopCook already did this.
                    if (station != null)
                    {
                        try
                        {
                            station.SetNPCUser(null);
                            didAnything = true;
                            Log(_verboseLogPref?.Value == true,
                                $"[Reset] {who}: cleared MixingStation.NPCUserObject on '{station.Name}'");
                        }
                        catch (Exception ex)
                        {
                            Log(true, $"[Reset] {who}: SetNPCUser(null) threw {ex.Message}");
                        }

                        // Clear the station's in-progress mix operation so
                        // vanilla does not immediately re-pick this cook.
                        try
                        {
                            station.CurrentMixOperation = null;
                            didAnything = true;
                            Log(_verboseLogPref?.Value == true,
                                $"[Reset] {who}: CurrentMixOperation=null on '{station.Name}'");
                        }
                        catch (Exception ex)
                        {
                            Log(true, $"[Reset] {who}: CurrentMixOperation=null threw {ex.Message}");
                        }
                    }
                }

                // 2. Stop the active behaviour's coroutines as a final guard
                //    against any walk-routine / state-machine that StopCook
                //    did not cover.
                MonoBehaviour mb = active.TryCast<MonoBehaviour>();
                if (mb != null)
                {
                    try
                    {
                        mb.StopAllCoroutines();
                        didAnything = true;
                        Log(_verboseLogPref?.Value == true,
                            $"[Reset] {who}: StopAllCoroutines on {active.GetIl2CppType()?.Name}");
                    }
                    catch (Exception ex)
                    {
                        Log(true, $"[Reset] {who}: StopAllCoroutines threw {ex.Message}");
                    }
                }

                // 3. Deactivate is the vanilla "stop being active, unwind
                //    animation/position lock" hook. StartMixingStationBehaviour
                //    overrides it (line 474 of decompiled). Without this the
                //    chemist stays animated at the table even with the cook
                //    coroutine torn down.
                try
                {
                    active.Deactivate();
                    didAnything = true;
                    Log(_verboseLogPref?.Value == true,
                        $"[Reset] {who}: Deactivate() on active behaviour");
                }
                catch (Exception ex)
                {
                    Log(true, $"[Reset] {who}: Deactivate() threw {ex.Message}");
                }
            }

            // 4. Null the active-behaviour backing field so the next behaviour
            //    evaluation picks fresh. eMployee already does this; we repeat
            //    it for the standalone-hotkey path where eMployee may not run.
            if (beh != null)
            {
                FieldInfo backing = beh.GetType().GetField(
                    "_activeBehaviour_k__BackingField",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (backing != null)
                {
                    backing.SetValue(beh, null);
                    didAnything = true;
                }
            }

            // 4a. HAMMER: stop coroutines on EVERY MonoBehaviour attached to
            //     the chemist's GameObject. Iterations 2-4 showed that
            //     StopAllCoroutines on StartMixingStationBehaviour does not
            //     reach the wedged CookRoutine -- the routine is hosted on
            //     a different component (Chemist, Employee, or NPC base).
            //     Since we cannot tell from the il2cpp decompile which
            //     MonoBehaviour holds the coroutine, hit them all.
            try
            {
                GameObject go = ((Component)emp).gameObject;
                Il2CppArrayBase<MonoBehaviour> mbs = go.GetComponents<MonoBehaviour>();
                int stopped = 0;
                for (int i = 0; i < mbs.Length; i++)
                {
                    MonoBehaviour mb = mbs[i];
                    if (mb == null) continue;
                    try { mb.StopAllCoroutines(); stopped++; } catch { }
                }
                if (stopped > 0)
                {
                    didAnything = true;
                    Log(_verboseLogPref?.Value == true,
                        $"[Reset] {who}: HAMMER StopAllCoroutines on {stopped} MonoBehaviours on chemist GameObject");
                }
            }
            catch (Exception ex)
            {
                Log(true, $"[Reset] {who}: HAMMER StopAllCoroutines threw {ex.Message}");
            }

            // 5. Reset the chemist's NavMeshAgent so any in-progress path
            //    (often pointing at a stale storage slot) is cleared.
            NavMeshAgent agent = ((Component)emp).GetComponent<NavMeshAgent>();
            if (agent != null && agent.isOnNavMesh)
            {
                agent.ResetPath();
                didAnything = true;
            }

            // 6. Force the behaviour stack to re-sort so a fallback behaviour
            //    can take over while the broken cook task waits for conditions.
            try
            {
                beh?.SortBehaviourStack();
            }
            catch (Exception ex)
            {
                if (_verboseLogPref?.Value == true)
                    MelonLogger.Warning($"[Reset] {who}: SortBehaviourStack threw {ex.Message}");
            }

            if (didAnything)
                MelonLogger.Msg($"[Reset] {who}: smart reset complete");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[Reset] {who}: smart reset failed: {ex}");
        }

        return didAnything;
    }

    // -------------------------------------------------------------------------
    //  eMployee Harmony postfix (optional)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Try to install a Harmony postfix on eMployeeMod.ResetEmployeeCore so
    /// that whenever eMployee fires AUTO-RESET, our SmartReset cleanup runs
    /// after their narrower reset. Silently no-ops if eMployee is not loaded.
    /// </summary>
    private void TryHookEMployee()
    {
        try
        {
            Assembly emp = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "eMployee", StringComparison.OrdinalIgnoreCase));
            if (emp == null)
            {
                MelonLogger.Msg("eMployee not loaded; skipping AUTO-RESET hook");
                return;
            }

            Type modType = emp.GetType("eMployeeMod") ?? emp.GetTypes().FirstOrDefault(t => t.Name == "eMployeeMod");
            if (modType == null)
            {
                MelonLogger.Warning("eMployee assembly loaded but eMployeeMod type not found");
                return;
            }

            MethodInfo target = modType.GetMethod("ResetEmployeeCore",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (target == null)
            {
                MelonLogger.Warning("eMployeeMod.ResetEmployeeCore not found; eMployee version mismatch?");
                return;
            }

            MethodInfo postfix = typeof(Mod).GetMethod(nameof(EMployeeResetPostfix),
                BindingFlags.Static | BindingFlags.NonPublic);
            HarmonyInstance.Patch(target, postfix: new HarmonyMethod(postfix));
            MelonLogger.Msg("Hooked eMployeeMod.ResetEmployeeCore");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"TryHookEMployee failed: {ex}");
        }
    }

    private static void EMployeeResetPostfix(object emp)
    {
        Employee typed = (emp as Il2CppObjectBase)?.TryCast<Employee>();
        if (typed != null)
            SmartReset(typed);
    }

    // -------------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------------

    private static string SafeName(Employee emp)
    {
        try
        {
            // fullName lives on the NPC base class.
            string n = ((NPC)emp).fullName;
            if (!string.IsNullOrEmpty(n)) return n;
        }
        catch { }
        return "(unknown)";
    }

    private static void Log(bool verbose, string msg)
    {
        if (verbose) MelonLogger.Msg(msg);
    }

}
