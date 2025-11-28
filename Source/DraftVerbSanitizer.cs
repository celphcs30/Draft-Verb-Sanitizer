using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DraftVerbSanitizer
{
    [StaticConstructorOnStartup]
    public static class Startup
    {
        static Startup()
        {
            var harmony = new Harmony("celphcs30.draftverbsanitizer");
            harmony.PatchAll();
            LogHarmonyPatches();
        }

        static void LogHarmonyPatches()
        {
            try
            {
                var methods = new[]
                {
                    typeof(JobGiver_ConfigurableHostilityResponse).GetMethod("TryGetAttackNearbyEnemyJob"),
                    typeof(Pawn_MeleeVerbs).GetMethod("TryGetMeleeVerb"),
                    typeof(Pawn_MeleeVerbs).GetMethod("ChooseMeleeVerb"),
                    typeof(PawnAttackGizmoUtility).GetMethod("GetMeleeAttackGizmo")
                };

                foreach (var method in methods)
                {
                    if (method == null) continue;
                    var patches = Harmony.GetPatchInfo(method);
                    if (patches == null) continue;

                    var owners = new HashSet<string>();
                    if (patches.Prefixes != null) foreach (var p in patches.Prefixes) owners.Add(p.owner);
                    if (patches.Postfixes != null) foreach (var p in patches.Postfixes) owners.Add(p.owner);
                    if (patches.Transpilers != null) foreach (var p in patches.Transpilers) owners.Add(p.owner);
                    if (patches.Finalizers != null) foreach (var p in patches.Finalizers) owners.Add(p.owner);

                    Log.Message($"[DVS] {method.DeclaringType?.Name}.{method.Name} patches: {string.Join(", ", owners)}");
                }
            }
            catch { }
        }
    }

    // Runs once per load/new game and sanitizes every pawn by forcing a verb rebuild.
    public class DVS_GameComponent : GameComponent
    {
        private bool ranThisLoad;

        public DVS_GameComponent(Game game) { }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref ranThisLoad, "DVS_ranThisLoad", false);

            // Post-load-init is the safe point where everything exists.
            if (Scribe.mode == LoadSaveMode.PostLoadInit && !ranThisLoad)
            {
                Log.Message("[DVS] ExposeData: PostLoadInit detected, scheduling sanitize");
                ScheduleSanitize("PostLoadInit");
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();

            // New game path (no load), run once.
            if (!ranThisLoad)
            {
                Log.Message("[DVS] FinalizeInit: New game detected, scheduling sanitize");
                ScheduleSanitize("FinalizeInit");
            }
        }

        private void ScheduleSanitize(string reason)
        {
            ranThisLoad = true;
            LongEventHandler.QueueLongEvent(
                () => RunSanitizeAllPawns(reason),
                "DVS_SanitizingVerbs",
                doAsynchronously: false,
                null);
        }

        private void RunSanitizeAllPawns(string reason)
        {
            int total = 0;
            int rebuilt = 0;

            try
            {
                var seen = new HashSet<int>();

                // Maps (spawned pawns)
                foreach (var map in Find.Maps)
                {
                    foreach (var p in map.mapPawns.AllPawns)
                    {
                        if (p == null) continue;
                        if (!seen.Add(p.thingIDNumber)) continue;
                        total++;
                        if (VerbSanitizer.RebuildVerbs(p)) rebuilt++;
                    }
                }

                // World pawns (caravans, etc.)
                if (Find.World != null)
                {
                    foreach (var p in Find.WorldPawns.AllPawnsAliveOrDead) // include downed/away pawns
                    {
                        if (p == null) continue;
                        if (!seen.Add(p.thingIDNumber)) continue;
                        total++;
                        if (VerbSanitizer.RebuildVerbs(p)) rebuilt++;
                    }
                }

                Log.Message($"[DVS] On {reason}: inspected {total} pawns, rebuilt verb caches for {rebuilt}.");
            }
            catch (Exception e)
            {
                Log.Warning($"[DVS] Sanitize failed: {e}");
            }
        }
    }

    // Note: No patches on Draft toggle, no HostilityResponse finalizers, no AdjustedRange guards here.
    public static class VerbSanitizer
    {
        // Force a rebuild of the pawn's verbs by clearing AllVerbs and letting RimWorld rebuild.
        public static bool RebuildVerbs(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return false;
            var vt = pawn.verbTracker;
            if (vt == null) return false;

            try
            {
                // Try multiple approaches to force verb rebuild
                var vtType = vt.GetType();
                
                // Approach 1: Try to find and clear a cached field
                var fields = vtType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                bool foundCache = false;
                foreach (var f in fields)
                {
                    // Look for fields that might be caches (List<Verb>, Verb[], etc.)
                    if (f.FieldType == typeof(List<Verb>) || 
                        (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>)))
                    {
                        var list = f.GetValue(vt) as List<Verb>;
                        if (list != null && list.Count > 0)
                        {
                            // Found a verb list - clear it to force rebuild
                            list.Clear();
                            foundCache = true;
                            Log.Message($"[DVS] Cleared cache field '{f.Name}' for {pawn.LabelShortCap}");
                        }
                    }
                }
                
                // Approach 2: Try to find a method that rebuilds verbs
                var rebuildMethods = vtType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(m => m.Name.Contains("Rebuild") || m.Name.Contains("Refresh") || m.Name.Contains("Update"))
                    .Where(m => m.GetParameters().Length == 0)
                    .ToList();
                
                foreach (var m in rebuildMethods)
                {
                    try
                    {
                        m.Invoke(vt, null);
                        Log.Message($"[DVS] Called rebuild method '{m.Name}' for {pawn.LabelShortCap}");
                        foundCache = true;
                        break;
                    }
                    catch { }
                }
                
                // Approach 3: Directly clear AllVerbs if it's a List we can modify
                var allVerbs = vt.AllVerbs;
                if (allVerbs != null && allVerbs.Count > 0)
                {
                    // Try to clear the list - this will force RimWorld to rebuild
                    allVerbs.Clear();
                    foundCache = true;
                    Log.Message($"[DVS] Cleared AllVerbs list for {pawn.LabelShortCap}");
                }

                if (foundCache)
                {
                    // Clear any stance that might be using an old verb ref.
                    pawn.stances?.CancelBusyStanceSoft();
                    TryRefreshMVCF(pawn);
                    return true;
                }
                else
                {
                    Log.Warning($"[DVS] Could not find way to rebuild verbs for {pawn.LabelShortCap}");
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[DVS] RebuildVerbs failed for {pawn?.LabelShortCap ?? "null"}: {e.Message}");
            }

            return false;
        }

        // MVCF compatibility: ask its manager to recalc after we invalidate verb cache.
        private static void TryRefreshMVCF(Pawn pawn)
        {
            try
            {
                var util =
                    AccessTools.TypeByName("MVCF.Utilities.VerbManagerUtility") ??
                    AccessTools.TypeByName("MVCF.Utilities.Utilities");
                if (util == null) return;

                // TryGetManager(Pawn, out Manager) – method signature varies across MVCF versions.
                var tryGet = util
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "TryGetManager") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 2 &&
                               ps[0].ParameterType == typeof(Pawn) &&
                               ps[1].ParameterType.IsByRef;
                    });

                if (tryGet == null) return;

                var args = new object[] { pawn, null };
                bool ok = tryGet.Invoke(null, args) as bool? ?? false;
                if (!ok) return;

                var mgr = args[1];
                if (mgr == null) return;

                // RecalculateVerbs/Recalculate depending on MVCF build.
                var recalc =
                    mgr.GetType().GetMethod("RecalculateVerbs", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                    mgr.GetType().GetMethod("Recalculate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                recalc?.Invoke(mgr, null);
            }
            catch { }
        }
    }
}
