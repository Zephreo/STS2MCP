using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace STS2_MCP;

public static partial class McpMod
{
    // Each state build owns its value mode on the serialized Godot main thread.
    // A best-effort DTO catch cannot hide a failed unpowered card preview.
    private static T WithCardValues<T>(bool unpowered, Func<T> build)
    {
        bool previous = UnpoweredValues;
        var previousFailure = UnpoweredCardFailure;
        UnpoweredValues = unpowered;
        UnpoweredCardFailure = null;
        try
        {
            var state = build();
            if (UnpoweredCardFailure != null)
                throw UnpoweredCardFailure;
            return state;
        }
        finally
        {
            UnpoweredValues = previous;
            UnpoweredCardFailure = previousFailure;
        }
    }

    private static Exception? UnpoweredCardFailure;

    private static Exception RefuseUnpoweredCardValues(CardModel card, Exception error)
    {
        UnpoweredCardFailure ??= new InvalidOperationException(
            $"Failed to render unpowered values for card {card.Id.Entry}: {error.Message}", error);
        return UnpoweredCardFailure;
    }

    // Card descriptions arrive multi-line; the JSON export keeps them on one line.
    private const char NEWLINE = '\n';

    // Whether the current state build renders card and intent numbers WITHOUT the
    // player's standing powers and relics applied. Set for the duration of one
    // main-thread build by HandleGetState/HandleGetMultiplayerState; see the
    // `values=powered|unpowered` query parameter.
    //
    // A plain static is safe: every build runs inside RunOnMainThread, so
    // concurrent HTTP requests serialize onto the one Godot main thread.
    internal static bool UnpoweredValues;

    private static string? SafeGetCardDescription(CardModel card, PileType pile = PileType.Hand)
    {
        if (UnpoweredValues)
        {
            try
            {
                using var scope = UnpoweredCardVars.Apply(card);
                return CleanCardDescription(card.GetDescriptionForPile(pile));
            }
            catch (Exception error) { throw RefuseUnpoweredCardValues(card, error); }
        }
        try { return CleanCardDescription(card.GetDescriptionForPile(pile)); }
        catch { return SafeGetText(() => card.Description)?.Replace(NEWLINE, ' '); }
    }

    private static string CleanCardDescription(string description)
        => StripRichTextTags(description).Replace(NEWLINE, ' ');

    /// <summary>
    /// Renders a card's DynamicVars at base + enchantment for the lifetime of the
    /// scope, then restores exactly what was there.
    ///
    /// CardModel.GetDescriptionForPile does no arithmetic - it prints
    /// DynamicVar.PreviewValue, which only DynamicVar.UpdateCardPreview writes, and
    /// that method's runGlobalHooks argument is precisely the "apply powers and
    /// relics" switch (DamageVar/BlockVar guard Hook.ModifyDamage/ModifyBlock behind
    /// it; PowerVar guards Hook.ModifyPowerAmountGiven). Passing false leaves the
    /// enchantment applied - the game treats an enchantment as part of the card, and
    /// so do we - while Strength, Dexterity, Vigor, Focus, Weak and every additive
    /// relic bonus drop out. CalculatedVar still runs its own base + extra * count
    /// arithmetic, which belongs to the card rather than to any power.
    ///
    /// CardModel.UpdateDynamicVarPreview cannot be used: it derives runGlobalHooks
    /// from the card's pile rather than taking it. DynamicVar.UpdateCardPreview is
    /// public, so the per-var loop is done here instead.
    ///
    /// The var set is live UI state shared with NCard, so the previous values are
    /// snapshotted and put back. Builds run on the Godot main thread, so no render
    /// can observe the window in between.
    /// </summary>
    private readonly struct UnpoweredCardVars : IDisposable
    {
        private readonly List<(DynamicVar Var, decimal Enchanted, decimal Preview)>? _saved;

        private UnpoweredCardVars(List<(DynamicVar, decimal, decimal)>? saved) => _saved = saved;

        internal static UnpoweredCardVars Apply(CardModel card)
        {
            var saved = new List<(DynamicVar, decimal, decimal)>();
            try
            {
                Unpower(card, card.DynamicVars, saved);
                var enchantment = card.Enchantment;
                if (enchantment != null)
                    Unpower(card, enchantment.DynamicVars, saved);
            }
            catch (Exception error)
            {
                Restore(saved);
                throw RefuseUnpoweredCardValues(card, error);
            }
            return new UnpoweredCardVars(saved);
        }

        private static void Unpower(
            CardModel card,
            DynamicVarSet set,
            List<(DynamicVar, decimal, decimal)> saved)
        {
            foreach (var dynamicVar in set.Values)
                saved.Add((dynamicVar, dynamicVar.EnchantedValue, dynamicVar.PreviewValue));
            set.ClearPreview();
            foreach (var dynamicVar in set.Values.ToList())
                dynamicVar.UpdateCardPreview(card, CardPreviewMode.None, null, runGlobalHooks: false);
        }

        private static void Restore(List<(DynamicVar Var, decimal Enchanted, decimal Preview)>? saved)
        {
            if (saved == null) return;
            foreach (var (dynamicVar, enchanted, preview) in saved)
            {
                // DynamicVar setters only clamp and assign decimal fields.
                dynamicVar.EnchantedValue = enchanted;
                dynamicVar.PreviewValue = preview;
            }
        }

        public void Dispose() => Restore(_saved);
    }

}
