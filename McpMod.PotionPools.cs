using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;

namespace STS2_MCP;

// The potion pools the game rolls against are STATIC for the duration of a run,
// exactly as the card pools are: PotionPoolModel.GetUnlockedPotions is a pure
// function of the unlock state. Serving them here rather than on every state
// poll keeps the ~10 Hz state build off the enumeration entirely.
//
// This is the missing half of predicting a merchant's shelf. Everything else it
// stocks is already derivable — cards and prices off `player.rng.shops`, relics
// out of /api/v1/relicbag — but PotionFactory.CreateRandomPotion does
// `rng.NextItem(list.Where(rarity))` over the *unlocked* pool, so without the
// list in declaration order a client can predict a potion's rarity and price
// but never which potion it is.
//
// Run-scoped, NOT profile-scoped: in multiplayer `player.UnlockState` is the
// union of every player's, so clients must refetch when a run starts.
public static partial class McpMod
{
    // Order is load-bearing. `Rng.NextItem` indexes straight into the list
    // GetUnlockedPotions returns, so the declaration order of each pool's
    // GenerateAllPotions() IS the mapping from roll to potion. Never sort.
    private static readonly object _potionPoolsCacheLock = new();
    private static UnlockState? _potionPoolsCacheUnlocks;
    private static Dictionary<string, object?>? _potionPoolsCache;

    /// <summary>
    /// GET /api/v1/potionpools — every unlocked potion pool, in draw order.
    /// </summary>
    private static void HandleGetPotionPools(HttpListenerResponse response)
    {
        try
        {
            var dataTask = RunOnMainThread(BuildPotionPools);
            SendJson(response, dataTask.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Failed to build potion pools: {ex.Message}");
        }
    }

    internal static object GetPotionPools()
    {
        return BuildPotionPools();
    }

    private static Dictionary<string, object?> BuildPotionPools()
    {
        var unlockState = ResolvePotionPoolUnlockState(out var source);
        if (unlockState == null)
            return new Dictionary<string, object?> { ["error"] = "No profile or run data available." };

        lock (_potionPoolsCacheLock)
        {
            // The UnlockState instance is stable for a run, so reference
            // equality is a sound cache key and costs nothing to check.
            if (_potionPoolsCache != null && ReferenceEquals(_potionPoolsCacheUnlocks, unlockState))
                return _potionPoolsCache;
        }

        var pools = new Dictionary<string, object?>();
        foreach (var pool in ModelDb.AllPotionPools)
        {
            // PotionPoolModel has no Title (unlike CardPoolModel), so the
            // ModelId entry is the stable key — "SHARED_POTION_POOL" and so on.
            var title = SafeGet(() => (object?)pool.Id?.Entry) as string;
            if (string.IsNullOrWhiteSpace(title) || pools.ContainsKey(title!))
                continue;

            List<PotionModel> potions;
            try
            {
                potions = pool.GetUnlockedPotions(unlockState).ToList();
            }
            catch
            {
                // A modded or malformed pool must not take down the response.
                continue;
            }

            pools[title!] = new Dictionary<string, object?>
            {
                ["potions"] = potions.Select(BuildPotionPoolEntry).ToList()
            };
        }

        // PotionFactory.GetPotionOptions concatenates the character's own pool
        // with SharedPotionPool, in that order, so a client needs to know which
        // pool belongs to which character to rebuild the same list.
        var characters = new Dictionary<string, object?>();
        foreach (var character in ModelDb.AllCharacters)
        {
            var name = SafeGetText(() => character.Title);
            if (!string.IsNullOrWhiteSpace(name))
                characters[name!] = SafeGet(() => (object?)character.PotionPool?.Id?.Entry);
        }

        var result = new Dictionary<string, object?>
        {
            ["source"] = source,
            ["characters"] = characters,
            ["shared_pool"] = SafeGet(() => (object?)ModelDb.PotionPool<SharedPotionPool>()?.Id?.Entry),
            ["pools"] = pools
        };

        lock (_potionPoolsCacheLock)
        {
            _potionPoolsCacheUnlocks = unlockState;
            _potionPoolsCache = result;
        }
        return result;
    }

    private static Dictionary<string, object?> BuildPotionPoolEntry(PotionModel potion)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = potion.Id.Entry,
            ["name"] = SafeGetText(() => potion.Title) ?? potion.Id.Entry,
            ["rarity"] = potion.Rarity.ToString(),
            // PotionFactory.CreateRandomPotionInCombat filters on this; the
            // out-of-combat path (shops, rewards) does not.
            ["can_be_generated_in_combat"] = SafeGet(() => (object?)potion.CanBeGeneratedInCombat)
        };
    }

    /// <summary>
    /// The run's unlock state, falling back to the profile between runs.
    /// Mirrors ResolveCardPoolUnlockState, minus the multiplayer constraint —
    /// PotionPoolModel.GetUnlockedPotions does not take one.
    /// </summary>
    private static UnlockState? ResolvePotionPoolUnlockState(out string source)
    {
        if (RunManager.Instance?.IsInProgress == true)
        {
            var runState = RunManager.Instance.DebugOnlyGetState();
            var player = runState != null ? LocalContext.GetMe(runState) : null;
            if (player != null)
            {
                source = "run";
                return player.UnlockState;
            }
        }

        var progress = SaveManager.Instance?.Progress;
        if (progress == null)
        {
            source = "none";
            return null;
        }
        source = "profile";
        return new UnlockState(progress);
    }
}
