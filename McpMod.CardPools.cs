using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace STS2_MCP;

// The card pools the game rolls against are STATIC for the duration of a run:
// CardPoolModel.GetUnlockedCards is a pure function of the unlock state and the
// multiplayer constraint, and CardFactory's combat/transform filters read only
// per-card model flags (Pool, Type, Rarity, CanBeGeneratedInCombat). Serving
// them here instead of on every state poll keeps the ~10 Hz state build off the
// pool enumeration entirely.
//
// Run-scoped, NOT profile-scoped: in multiplayer `player.UnlockState` is the
// union of every player's unlock state, and the constraint follows the player
// count. Clients must refetch when a run starts.
public static partial class McpMod
{
    // Order is load-bearing: the game's `Rng.NextItem` indexes straight into
    // the list GetUnlockedCards returns, so the declaration order of each
    // pool's GenerateAllCards() IS the mapping from roll to card. Never sort.
    private static readonly object _cardPoolsCacheLock = new();
    private static UnlockState? _cardPoolsCacheUnlocks;
    private static CardMultiplayerConstraint _cardPoolsCacheConstraint;
    private static Dictionary<string, object?>? _cardPoolsCache;

    private static void HandleGetCardPools(HttpListenerResponse response)
    {
        try
        {
            var dataTask = RunOnMainThread(BuildCardPools);
            SendJson(response, dataTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Failed to build card pools: {ex.Message}");
        }
    }

    internal static object GetCardPools()
    {
        return BuildCardPools();
    }

    private static Dictionary<string, object?> BuildCardPools()
    {
        var unlockState = ResolveCardPoolUnlockState(out var constraint, out var source);
        if (unlockState == null)
            return new Dictionary<string, object?> { ["error"] = "No profile or run data available." };

        lock (_cardPoolsCacheLock)
        {
            // The UnlockState instance is stable for a run, so reference
            // equality is a sound cache key and costs nothing to check.
            if (_cardPoolsCache != null
                && ReferenceEquals(_cardPoolsCacheUnlocks, unlockState)
                && _cardPoolsCacheConstraint == constraint)
            {
                return _cardPoolsCache;
            }
        }

        var pools = new Dictionary<string, object?>();
        foreach (var pool in ModelDb.AllCardPools)
        {
            var title = pool.Title;
            if (string.IsNullOrWhiteSpace(title) || pools.ContainsKey(title))
                continue;

            List<CardModel> cards;
            try
            {
                cards = pool.GetUnlockedCards(unlockState, constraint).ToList();
            }
            catch
            {
                // A modded or malformed pool must not take down the response;
                // its cards simply have no derivable domain client-side.
                continue;
            }

            pools[title] = new Dictionary<string, object?>
            {
                ["is_colorless"] = pool.IsColorless,
                ["cards"] = cards.Select(BuildCardPoolEntry).ToList()
            };
        }

        var characters = new Dictionary<string, object?>();
        var characterPoolOrder = new List<string>();
        foreach (var character in ModelDb.AllCharacters)
        {
            var name = SafeGetText(() => character.Title);
            var poolTitle = character.CardPool?.Title;
            if (!string.IsNullOrWhiteSpace(name))
                characters[name!] = poolTitle;
            if (!string.IsNullOrWhiteSpace(poolTitle) && !characterPoolOrder.Contains(poolTitle))
                characterPoolOrder.Add(poolTitle);
        }

        var result = new Dictionary<string, object?>
        {
            ["source"] = source,
            ["multiplayer_constraint"] = constraint.ToString(),
            ["characters"] = characters,
            ["character_pool_order"] = characterPoolOrder,
            ["pools"] = pools
        };

        lock (_cardPoolsCacheLock)
        {
            _cardPoolsCacheUnlocks = unlockState;
            _cardPoolsCacheConstraint = constraint;
            _cardPoolsCache = result;
        }
        return result;
    }

    private static Dictionary<string, object?> BuildCardPoolEntry(CardModel card)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = card.Id.Entry,
            ["name"] = SafeGetText(() => card.Title) ?? card.Id.Entry,
            ["type"] = card.Type.ToString(),
            ["rarity"] = card.Rarity.ToString(),
            // A card's own Pool can differ from the pool list it appears in
            // (character starting decks are concatenated into ModelDb.AllCards),
            // and GetDefaultTransformationOptions reads THIS one. Export it so
            // clients never have to invert the pool -> cards mapping.
            ["pool"] = SafeGetCardPoolTitle(card),
            // CardFactory.FilterForPlayerCount drops the constraint that does
            // not match the lobby, so anything replaying a generation roll
            // (shop shelves, card rewards) needs it to index the same list the
            // game did.
            ["multiplayer_constraint"] = card.MultiplayerConstraint.ToString(),
            ["can_be_generated_in_combat"] = SafeCanBeGeneratedInCombat(card)
        };
    }

    private static string? SafeGetCardPoolTitle(CardModel card)
    {
        try { return card.Pool?.Title; }
        catch { return null; }
    }

    // CanBeGeneratedInCombat is `virtual`, so an override is free to consult
    // run context that does not exist outside a combat. Match the game's own
    // default when reading it throws.
    private static bool SafeCanBeGeneratedInCombat(CardModel card)
    {
        try { return card.CanBeGeneratedInCombat; }
        catch { return true; }
    }

    // Prefers the run's unlock state, which in multiplayer is the union across
    // players and therefore wider than the local profile's. Falls back to the
    // local profile between runs so the endpoint stays useful at the menu.
    private static UnlockState? ResolveCardPoolUnlockState(
        out CardMultiplayerConstraint constraint,
        out string source)
    {
        if (RunManager.Instance?.IsInProgress == true)
        {
            var runState = RunManager.Instance.DebugOnlyGetState();
            var player = runState != null ? LocalContext.GetMe(runState) : null;
            if (runState != null && player != null)
            {
                // Read through Player.RunState (typed IRunState): the
                // constraint is an interface default member derived from
                // Players.Count and is not visible on RunState itself.
                constraint = player.RunState.CardMultiplayerConstraint;
                source = "run";
                return player.UnlockState;
            }
        }

        var progress = SaveManager.Instance?.Progress;
        if (progress == null)
        {
            constraint = CardMultiplayerConstraint.SingleplayerOnly;
            source = "none";
            return null;
        }

        // IRunState.CardMultiplayerConstraint reports SingleplayerOnly for a
        // player count of 1, so that is the right stand-in with no run loaded.
        constraint = CardMultiplayerConstraint.SingleplayerOnly;
        source = "profile";
        return new UnlockState(progress);
    }
}
