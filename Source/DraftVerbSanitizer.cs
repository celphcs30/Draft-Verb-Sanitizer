using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DraftVerbSanitizer
{
    [StaticConstructorOnStartup]
    public static class Startup
    {
        static Startup()
        {
            new Harmony("celphcs30.draftverbsanitizer").PatchAll();
        }
    }

    // Run sanitizer whenever player toggles drafted state.
    [HarmonyPatch(typeof(Pawn_DraftController), "Drafted", MethodType.Setter)]
    public static class Patch_DraftedSetter
    {
        public static void Postfix(Pawn_DraftController __instance)
        {
            var pawn = __instance?.pawn;
            if (pawn == null) return;

            VerbSanitizer.TrySanitize(pawn, out _);
        }
    }

    // Soft guard: if a bad verb reaches AdjustedRange, return a safe value instead of throwing.
    [HarmonyPatch(typeof(VerbProperties), nameof(VerbProperties.AdjustedRange))]
    public static class Patch_AdjustedRangeGuard
    {
        public static bool Prefix(Verb ownerVerb, Thing attacker, ref float __result)
        {
            // Keep this cheap; no try/catch on hot path.
            if (ownerVerb == null || ownerVerb.verbProps == null)
            {
                __result = 1f;
                return false;
            }

            // If caster/attacker are null, skip original to avoid NREs in downstream code.
            if (ownerVerb.CasterPawn == null || attacker == null)
            {
                // Fall back to the raw verb range or 1.
                __result = Math.Max(1f, ownerVerb.verbProps.range);
                return false;
            }

            return true; // run original
        }
    }

    public static class VerbSanitizer
    {
        public static bool TrySanitize(Pawn pawn, out int removed)
        {
            removed = 0;
            if (pawn == null || pawn.Destroyed) return false;

            var vt = pawn.verbTracker;
            if (vt == null) return false;

            List<Verb> verbs;
            try
            {
                verbs = vt.AllVerbs?.ToList(); // copy for safe iteration
            }
            catch
            {
                return false;
            }
            if (verbs == null || verbs.Count == 0) return false;

            var eq = pawn.equipment?.AllEquipmentListForReading;
            var hediffs = pawn.health?.hediffSet?.hediffs;
            var abilities = pawn.abilities?.abilities;

            foreach (var v in verbs)
            {
                if (ShouldCull(v, pawn, eq, hediffs, abilities))
                {
                    if (RemoveVerbFromTracker(vt, v))
                        removed++;
                }
            }

            if (removed > 0)
            {
                // Cancel burst/aiming tied to removed verbs to avoid odd stances.
                pawn.stances?.CancelBusyStanceSoft();
                // Best-effort nudge for MVCF verb managers if present.
                TryRefreshMVCF(pawn);
                // Optional: Log.Message($"[DVS] Removed {removed} orphaned verb(s) from {pawn.LabelShortCap}");
            }

            return true;
        }

        static bool ShouldCull(Verb v, Pawn pawn,
            List<ThingWithComps> eq,
            List<Hediff> hediffs,
            List<Ability> abilities)
        {
            if (v == null) return true;
            if (v.verbProps == null) return true;

            // Must belong to this pawn.
            if (v.CasterPawn == null || v.CasterPawn != pawn) return true;

            // Equipment verb orphaned?
            var srcEq = v.EquipmentSource;
            if (srcEq != null && (eq == null || !eq.Contains(srcEq))) return true;

            // Hediff verb orphaned?
            var srcHediff = v.HediffCompSource?.parent;
            if (srcHediff != null && (hediffs == null || !hediffs.Contains(srcHediff))) return true;

            // Ability verb orphaned? (use reflection since ability property may not exist on base Verb)
            Ability srcAbility = null;
            try
            {
                var abilityProp = AccessTools.Property(v.GetType(), "ability");
                if (abilityProp != null)
                    srcAbility = abilityProp.GetValue(v) as Ability;
            }
            catch { }
            if (srcAbility != null && (abilities == null || !abilities.Contains(srcAbility))) return true;

            // If owner is null and none of the above matched, still cull.
            if (v.verbTracker == null) return true;

            return false;
        }

        static bool RemoveVerbFromTracker(VerbTracker vt, Verb v)
        {
            if (vt == null || v == null) return false;

            try
            {
                var list = vt.AllVerbs;
                if (list != null && list.Remove(v))
                    return true;
            }
            catch
            {
                // fallthrough
            }

            // Reflection fallback for private field "verbs" (if AllVerbs blocks removal).
            try
            {
                var fld = AccessTools.Field(typeof(VerbTracker), "verbs");
                if (fld?.GetValue(vt) is List<Verb> inner && inner.Remove(v))
                    return true;
            }
            catch { }

            return false;
        }

        static void TryRefreshMVCF(Pawn pawn)
        {
            try
            {
                var util = AccessTools.TypeByName("MVCF.Utilities.VerbManagerUtility")
                           ?? AccessTools.TypeByName("MVCF.Utilities.Utilities");
                if (util == null) return;

                var tryGet = AccessTools.Method(util, "TryGetManager", new[] { typeof(Pawn) });
                var mgr = tryGet?.Invoke(null, new object[] { pawn });
                if (mgr == null) return;

                var recalc = AccessTools.Method(mgr.GetType(), "RecalculateVerbs");
                recalc?.Invoke(mgr, null);
            }
            catch { }
        }
    }
}

