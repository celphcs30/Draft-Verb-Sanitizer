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
    [HarmonyPriority(Priority.First)]
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

    // Prevent NRE in HostilityResponse when verbs are invalid
    [HarmonyPatch(typeof(JobGiver_ConfigurableHostilityResponse), "TryGetAttackNearbyEnemyJob")]
    [HarmonyPriority(Priority.First)]
    [HarmonyBefore("OskarPotocki.VEF", "legodude17.mvcf")]
    public static class Patch_HostilityResponse_Safety
    {
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            // Bail fast if pawn is not actionable
            if (pawn == null || pawn.Destroyed || pawn.Dead)
            {
                __result = null;
                return false;
            }

            // Try to sanitize orphaned/bad verbs first
            VerbSanitizer.TrySanitize(pawn, out _);

            // If verbTracker is gone or no verbs, skip running the original (and modded) logic
            var vt = pawn.verbTracker;
            var verbs = vt?.AllVerbs;
            if (verbs == null || verbs.Count == 0)
            {
                __result = null;
                return false;
            }

            // Ensure at least one usable attack verb exists before proceeding
            bool hasUsable = false;
            for (int i = 0; i < verbs.Count; i++)
            {
                var v = verbs[i];
                if (v == null || v.verbProps == null) continue;
                // Available() is safe; our AdjustedRange guard prevents null-related crashes on hot path.
                // Check if it's an attack verb (melee or ranged)
                if (v.Available() && v.verbProps.violent)
                {
                    hasUsable = true;
                    break;
                }
            }

            if (!hasUsable)
            {
                __result = null;
                return false;
            }

            // let original (and other patches) run
            return true;
        }
    }

    // Suppress "has no available melee attack" spam
    [HarmonyPatch(typeof(Pawn_MeleeVerbs), "TryGetMeleeVerb")]
    [HarmonyPriority(Priority.First)]
    [HarmonyBefore("OskarPotocki.VEF", "legodude17.mvcf")]
    public static class Patch_TryGetMeleeVerb_SuppressError
    {
        static readonly MethodInfo GetList = FindGetUpdatedVerbListMethod();
        static readonly FieldInfo PawnField = AccessTools.Field(typeof(Pawn_MeleeVerbs), "pawn");

        internal static MethodInfo FindGetUpdatedVerbListMethod()
        {
            var type = typeof(Pawn_MeleeVerbs);
            var methods = AccessTools.GetDeclaredMethods(type)
                .Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(m => m != null && m.Name.IndexOf("GetUpdated", StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(m =>
                {
                    var returnType = m.ReturnType;
                    if (returnType == typeof(List<Verb>) || 
                        (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
                    {
                        var genArgs = returnType.GetGenericArguments();
                        if (genArgs.Length == 1 && genArgs[0] == typeof(Verb))
                        {
                            var parms = m.GetParameters();
                            return parms.Length == 0 || (parms.Length == 1 && parms[0].ParameterType == typeof(bool));
                        }
                    }
                    return false;
                })
                .ToList();

            return methods.FirstOrDefault();
        }

        static bool Prefix(Pawn_MeleeVerbs __instance, Thing target, ref Verb __result)
        {
            if (__instance == null)
            {
                __result = null;
                return false;
            }

            // Get pawn via reflection (may be private field)
            Pawn pawn = null;
            if (PawnField != null)
            {
                try
                {
                    pawn = PawnField.GetValue(__instance) as Pawn;
                }
                catch { }
            }

            if (pawn == null || pawn.Dead || pawn.Destroyed)
            {
                __result = null;
                return false;
            }

            // Keep verbs sane first; cheap and main-thread safe
            VerbSanitizer.TrySanitize(pawn, out _);

            // Check for available melee verbs - if we can't find any, return null to suppress ErrorOnce spam
            bool hasVerbs = false;
            
            // Try the robust method first
            if (GetList != null)
            {
                try
                {
                    var parms = GetList.GetParameters();
                    var listObj = parms.Length == 1
                        ? GetList.Invoke(__instance, new object[] { false })
                        : GetList.Invoke(__instance, null);

                    if (listObj is IEnumerable<Verb> verbEnum)
                    {
                        hasVerbs = verbEnum.Any();
                    }
                }
                catch
                {
                    // Reflection failed, will try fallback
                }
            }

            // Fallback: check verbTracker directly for melee verbs
            if (!hasVerbs)
            {
                try
                {
                    var vt = pawn.verbTracker;
                    if (vt != null)
                    {
                        var allVerbs = vt.AllVerbs;
                        if (allVerbs != null)
                        {
                            // Check for any melee attack verbs
                            hasVerbs = allVerbs.Any(v => v != null && v.IsMeleeAttack && v.verbProps != null);
                        }
                    }
                }
                catch
                {
                    // If we can't check, assume no verbs to suppress error
                    hasVerbs = false;
                }
            }

            // If no melee verbs found, return null to suppress the ErrorOnce
            if (!hasVerbs)
            {
                __result = null;
                return false;
            }

            return true; // proceed to original method
        }
    }

    // Prevent asking for a melee gizmo when none exists
    [HarmonyPatch(typeof(PawnAttackGizmoUtility), nameof(PawnAttackGizmoUtility.GetMeleeAttackGizmo))]
    [HarmonyPriority(Priority.First)]
    public static class Patch_GetMeleeAttackGizmo_Safety
    {
        static readonly MethodInfo GetList = Patch_TryGetMeleeVerb_SuppressError.FindGetUpdatedVerbListMethod();

        static bool Prefix(Pawn pawn, ref Gizmo __result)
        {
            if (pawn == null || pawn.Dead || pawn.Downed)
            {
                __result = null;
                return false;
            }

            // If the pawn clearly has no melee verbs, skip creating the gizmo.
            // This is conservative: if we can't determine, we let original proceed.
            var pmv = pawn.meleeVerbs;
            if (pmv == null)
            {
                __result = null;
                return false;
            }

            // Try to avoid calling into ChooseMeleeVerb; it's already guarded above,
            // but this short-circuits earlier and saves work.
            if (GetList != null)
            {
                try
                {
                    var parms = GetList.GetParameters();
                    var listObj = parms.Length == 1
                        ? GetList.Invoke(pmv, new object[] { false })
                        : GetList.Invoke(pmv, null);

                    if (listObj is IEnumerable<Verb> verbEnum)
                    {
                        if (!verbEnum.Any())
                        {
                            __result = null;
                            return false;
                        }
                    }
                }
                catch
                {
                    // If we can't check, let the original run and rely on the suppression above.
                }
            }

            return true; // proceed to original
        }
    }

    public static class VerbSanitizer
    {
        // Cache reflection for ability property lookup
        private static readonly PropertyInfo CachedAbilityProperty = AccessTools.Property(typeof(Verb), "ability");

        public static bool TrySanitize(Pawn pawn, out int removed)
        {
            removed = 0;
            if (pawn == null || pawn.Destroyed) return false;

            var vt = pawn.verbTracker;
            if (vt == null) return false;

            List<Verb> list;
            try
            {
                list = vt.AllVerbs;
            }
            catch
            {
                return false;
            }
            if (list == null || list.Count == 0) return false;

            var eq = pawn.equipment?.AllEquipmentListForReading;
            var hediffs = pawn.health?.hediffSet?.hediffs;
            var abilities = pawn.abilities?.abilities;

            // HashSets for O(1) membership checks; default reference equality is correct here.
            HashSet<ThingWithComps> eqSet = (eq != null && eq.Count > 0) ? new HashSet<ThingWithComps>(eq) : null;
            HashSet<Hediff> hediffSet = (hediffs != null && hediffs.Count > 0) ? new HashSet<Hediff>(hediffs) : null;
            HashSet<Ability> abilitySet = (abilities != null && abilities.Count > 0) ? new HashSet<Ability>(abilities) : null;

            // Remove in-place, iterating backwards to avoid index shifting.
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var v = list[i];
                if (ShouldCull(v, pawn, eqSet, hediffSet, abilitySet))
                {
                    list.RemoveAt(i);
                    removed++;
                }
            }

            if (removed > 0)
            {
                pawn.stances?.CancelBusyStanceSoft();
                TryRefreshMVCF_Cached(pawn);
                // Optional: Log.Message($"[DVS] Removed {removed} orphaned verb(s) from {pawn.LabelShortCap}");
            }

            return true;
        }

        static bool ShouldCull(Verb v, Pawn pawn,
            HashSet<ThingWithComps> eq,
            HashSet<Hediff> hediffs,
            HashSet<Ability> abilities)
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

            // Ability verb orphaned? (use cached reflection)
            Ability srcAbility = null;
            if (CachedAbilityProperty != null)
            {
                try
                {
                    srcAbility = CachedAbilityProperty.GetValue(v) as Ability;
                }
                catch { }
            }
            if (srcAbility != null && (abilities == null || !abilities.Contains(srcAbility))) return true;

            // If owner is null and none of the above matched, still cull.
            if (v.verbTracker == null) return true;

            return false;
        }

        // Cached MVCF reflection helpers.
        static class MVCFCache
        {
            public static readonly Type UtilType =
                AccessTools.TypeByName("MVCF.Utilities.VerbManagerUtility")
                ?? AccessTools.TypeByName("MVCF.Utilities.Utilities");

            public static readonly MethodInfo TryGetManager =
                UtilType != null ? AccessTools.Method(UtilType, "TryGetManager", new[] { typeof(Pawn) }) : null;

            static readonly Dictionary<Type, MethodInfo> RecalcCache = new Dictionary<Type, MethodInfo>();

            public static void TryRecalculate(object mgr)
            {
                if (mgr == null) return;

                var t = mgr.GetType();
                if (!RecalcCache.TryGetValue(t, out var m))
                {
                    m = AccessTools.Method(t, "RecalculateVerbs");
                    RecalcCache[t] = m;
                }

                m?.Invoke(mgr, null);
            }
        }

        static void TryRefreshMVCF_Cached(Pawn pawn)
        {
            try
            {
                if (MVCFCache.TryGetManager == null) return;

                var mgr = MVCFCache.TryGetManager.Invoke(null, new object[] { pawn });
                MVCFCache.TryRecalculate(mgr);
            }
            catch { }
        }
    }
}
