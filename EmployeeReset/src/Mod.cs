using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;
using UnityEngine.AI;

using Il2CppScheduleOne.Employees;
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

        MelonLogger.Msg($"EmployeeReset 0.1.0 initialized; hotkey={_hotkeyPref.Value}");
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

        for (int i = 0; i < all.Count; i++)
        {
            Employee emp = all[i];
            if (emp == null) continue;
            total++;

            if (LooksStuck(emp))
            {
                if (SmartReset(emp))
                    reset++;
            }
        }

        MelonLogger.Msg($"[Reset] hotkey: scanned {total}, reset {reset}");
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
