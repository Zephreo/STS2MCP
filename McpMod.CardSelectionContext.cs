using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace STS2_MCP;

public static partial class McpMod
{
    private sealed record ActiveCardSelection(
        long Id,
        string EntryPoint,
        string Surface,
        string? SourcePile,
        int MinSelect,
        int MaxSelect,
        bool ManualConfirmation,
        bool Cancelable,
        string? OriginId,
        string? OriginType,
        string? OriginKind,
        string Effect);

    private static ActiveCardSelection? _activeCardSelection;
    private static long _nextCardSelectionId;

    private static void AddCardSelectionContext(Dictionary<string, object?> state)
    {
        var selection = Volatile.Read(ref _activeCardSelection);
        if (selection == null)
            return;

        state["selection_id"] = selection.Id;
        state["selection_entry_point"] = selection.EntryPoint;
        state["selection_surface"] = selection.Surface;
        state["selection_effect"] = selection.Effect;
        state["min_select"] = selection.MinSelect;
        state["max_select"] = selection.MaxSelect;
        state["manual_confirmation"] = selection.ManualConfirmation;
        state["cancelable"] = selection.Cancelable;
        if (selection.SourcePile != null)
            state["source_pile"] = selection.SourcePile;
        if (selection.OriginId != null)
            state["origin_id"] = selection.OriginId;
        if (selection.OriginType != null)
            state["origin_type"] = selection.OriginType;
        if (selection.OriginKind != null)
            state["origin_kind"] = selection.OriginKind;
    }

    private static string SelectionSurface(string methodName)
    {
        if (methodName.Contains("FromHand", StringComparison.Ordinal))
            return "hand";
        if (methodName.Contains("FromCombatPile", StringComparison.Ordinal))
            return "combat_pile";
        if (methodName.Contains("FromChooseACard", StringComparison.Ordinal))
            return "generated_choice";
        if (methodName.Contains("FromChooseABundle", StringComparison.Ordinal))
            return "generated_bundle";
        if (methodName.Contains("FromDeck", StringComparison.Ordinal))
            return "deck";
        return "simple_grid";
    }

    private static string? SelectionOriginKind(Type? type)
    {
        string ns = type?.Namespace ?? "";
        if (ns.Contains(".Cards", StringComparison.Ordinal)) return "card";
        if (ns.Contains(".Potions", StringComparison.Ordinal)) return "potion";
        if (ns.Contains(".Powers", StringComparison.Ordinal)) return "power";
        if (ns.Contains(".Relics", StringComparison.Ordinal)) return "relic";
        if (ns.Contains(".Monsters", StringComparison.Ordinal)) return "monster";
        if (ns.Contains(".Events", StringComparison.Ordinal)) return "event";
        return type == null ? null : "model";
    }

    private static Type? SelectionCallerType()
    {
        foreach (var frame in new StackTrace().GetFrames() ?? Array.Empty<StackFrame>())
        {
            var type = frame.GetMethod()?.DeclaringType;
            while (type?.DeclaringType != null)
                type = type.DeclaringType;
            string ns = type?.Namespace ?? "";
            if (type != null
                && ns.StartsWith("MegaCrit.Sts2.Core.Models", StringComparison.Ordinal)
                && type != typeof(CardSelectCmd))
                return type;
        }
        return null;
    }

    private static string SelectionEffect(string methodName, string? originType)
    {
        if (methodName == nameof(CardSelectCmd.FromHandForDiscard)) return "discard";
        if (methodName == nameof(CardSelectCmd.FromHandForUpgrade)) return "upgrade";

        return originType switch
        {
            "Headbutt" or "CosmicIndifference" => "discard_to_draw_top",
            "Cleanse" or "Brand" or "BurningPact" or "Purity" or "Scavenge" or "TrueGrit" or "TyrannyPower"
                => "exhaust",
            "Charge" => "transform_minion_dive_bomb",
            "Seance" => "transform_soul",
            "LiquidMemories" => "discard_to_hand_free",
            "Dredge" or "Graveblast" or "Hologram" or "DropletOfPrecognition" or "ForegoneConclusionPower"
                or "StratagemPower" or "NeowsFury" or "SeekerStrike" or "SecretWeapon" or "SecretTechnique" or "Wish"
                => "move_to_hand",
            "Glimmer" or "PhotonCut" or "ThinkingAhead" => "hand_to_draw_top",
            "Begone" or "Transfigure" or "EntropyPower" => "transform",
            "Nightmare" or "DualWield" => "copy",
            "Snap" or "WellLaidPlansPower" => "retain",
            "GamblersBrew" or "GamblingChip" or "ToolsOfTheTradePower" => "discard",
            _ when methodName.Contains("FromChooseACard", StringComparison.Ordinal) => "generated_to_hand",
            _ => "origin_specific",
        };
    }

    [HarmonyPatch]
    private static class CardSelectionContextPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name.StartsWith("From", StringComparison.Ordinal));
        }

        private static void Prefix(MethodBase __originalMethod, object[] __args)
        {
            var prefs = __args.OfType<CardSelectorPrefs>().Cast<CardSelectorPrefs?>().FirstOrDefault();
            var source = __args.OfType<AbstractModel>().FirstOrDefault();
            var pile = __args.OfType<CardPile>().FirstOrDefault();
            var callerType = source?.GetType() ?? SelectionCallerType();
            string? originId = null;
            try { originId = source?.Id.Entry; }
            catch { }

            string methodName = __originalMethod.Name;
            string? originType = callerType?.Name;
            var context = new ActiveCardSelection(
                Interlocked.Increment(ref _nextCardSelectionId),
                methodName,
                SelectionSurface(methodName),
                pile?.Type.ToString().ToLowerInvariant(),
                prefs?.MinSelect ?? 1,
                prefs?.MaxSelect ?? 1,
                prefs?.RequireManualConfirmation ?? false,
                prefs?.Cancelable ?? false,
                originId,
                originType,
                SelectionOriginKind(callerType),
                SelectionEffect(methodName, originType));
            Volatile.Write(ref _activeCardSelection, context);
        }
    }
}
