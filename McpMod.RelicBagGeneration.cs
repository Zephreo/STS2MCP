using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    // One process-wide generation covers every player's bag. Reading it adds
    // no bag traversal to the main state endpoint; mutations are rare and pay
    // one atomic increment. It intentionally never resets, so a response from
    // an earlier run cannot accidentally match a later run in this process.
    private static long _relicBagGeneration = 1;

    private static long RelicBagGeneration => Interlocked.Read(ref _relicBagGeneration);

    private static void MarkRelicBagsChanged()
    {
        Interlocked.Increment(ref _relicBagGeneration);
    }

    private static readonly FieldInfo? _relicDequesField =
        typeof(RelicGrabBag).GetField("_deques", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? _relicFallbackField =
        typeof(RelicGrabBag).GetField("_mpFallbackDequeue", BindingFlags.Instance | BindingFlags.NonPublic);

    private static int RelicBagCount(RelicGrabBag bag)
    {
        int count = 0;
        if (_relicDequesField?.GetValue(bag) is Dictionary<RelicRarity, List<RelicModel>> deques)
            count += deques.Values.Sum(deque => deque.Count);
        if (_relicFallbackField?.GetValue(bag) is List<RelicModel> fallback)
            count += fallback.Count;
        return count;
    }

    [HarmonyPatch]
    private static class RelicBagVoidMutationPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var names = new HashSet<string>
            {
                nameof(RelicGrabBag.Populate),
                nameof(RelicGrabBag.Remove),
                nameof(RelicGrabBag.MoveToFallback),
                nameof(RelicGrabBag.LoadFromSerializable),
            };
            return AccessTools.GetDeclaredMethods(typeof(RelicGrabBag))
                .Where(method => names.Contains(method.Name) && !method.ContainsGenericParameters);
        }

        private static void Postfix()
        {
            MarkRelicBagsChanged();
        }
    }

    [HarmonyPatch]
    private static class RelicBagPullMutationPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var names = new HashSet<string>
            {
                nameof(RelicGrabBag.PullFromFront),
                nameof(RelicGrabBag.PullFromBack),
            };
            // PullFromFront's two-argument overload delegates to the filtered
            // overload. Patch only the widest overload so one removal advances
            // the generation once.
            return AccessTools.GetDeclaredMethods(typeof(RelicGrabBag))
                .Where(method => names.Contains(method.Name))
                .GroupBy(method => method.Name)
                .Select(group => group.OrderByDescending(method => method.GetParameters().Length).First());
        }

        private static void Postfix(RelicModel? __result)
        {
            if (__result != null)
                MarkRelicBagsChanged();
        }
    }

    [HarmonyPatch(typeof(RelicGrabBag), "RemoveDisallowedRelicsFromDeques")]
    private static class RelicBagPruneMutationPatch
    {
        private static void Prefix(RelicGrabBag __instance, out int __state)
        {
            __state = RelicBagCount(__instance);
        }

        private static void Postfix(RelicGrabBag __instance, int __state)
        {
            // This internal query only removes entries. Comparing the five
            // deque lengths avoids hashing or walking any relic IDs.
            if (RelicBagCount(__instance) != __state)
                MarkRelicBagsChanged();
        }
    }

    [HarmonyPatch(typeof(RelicGrabBag), "RefreshRarity")]
    private static class RelicBagRefreshMutationPatch
    {
        private static void Postfix()
        {
            MarkRelicBagsChanged();
        }
    }
}
