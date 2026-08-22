using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private static Dictionary<string, object?> BuildMultiplayerGameState()
    {
        var result = new Dictionary<string, object?>();
        var tree = Engine.GetMainLoop() as SceneTree;

        // Surface blocking FTUE/tutorial/popup prompts before normal run state, so MP
        // automation gets the same dismissal contract as singleplayer (see #71). Every
        // FTUE in `Decompiled/src/Core/Nodes/Ftue/` gates only on per-profile flags, not
        // on run mode, so they are equally reachable in multiplayer runs.
        if (tree?.Root != null)
        {
            var ftueState = BuildVisibleFtueState(tree.Root);
            if (ftueState != null)
                return ftueState;
        }

        if (!RunManager.Instance.IsInProgress)
        {
            result["state_type"] = "menu";
            result["message"] = "No run in progress. Player is in the main menu.";
            return result;
        }

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            result["state_type"] = "unknown";
            return result;
        }

        if (!RunManager.Instance.NetService.Type.IsMultiplayer())
        {
            result["state_type"] = "error";
            result["message"] = "Not in a multiplayer run. Use /api/v1/singleplayer instead.";
            return result;
        }

        // Multiplayer metadata
        result["game_mode"] = "multiplayer";
        result["net_type"] = RunManager.Instance.NetService.Type.ToString();
        result["player_count"] = runState.Players.Count;
        var localPlayer = LocalContext.GetMe(runState);
        if (localPlayer != null)
        {
            for (int i = 0; i < runState.Players.Count; i++)
            {
                if (runState.Players[i] == localPlayer)
                {
                    result["local_player_slot"] = i;
                    break;
                }
            }
        }

        // Same overlay-first detection logic as singleplayer
        var topOverlay = NOverlayStack.Instance?.Peek();
        var currentRoom = runState.CurrentRoom;
        bool mapIsOpen = IsMapScreenOpenOrVisible();

        if (topOverlay is NCardGridSelectionScreen cardSelectScreen)
        {
            result["state_type"] = "card_select";
            result["card_select"] = BuildCardSelectState(cardSelectScreen, runState);
        }
        else if (topOverlay is NChooseACardSelectionScreen chooseCardScreen)
        {
            result["state_type"] = "card_select";
            result["card_select"] = BuildChooseCardState(chooseCardScreen, runState);
        }
        else if (topOverlay is NChooseABundleSelectionScreen bundleScreen)
        {
            result["state_type"] = "bundle_select";
            result["bundle_select"] = BuildBundleSelectState(bundleScreen, runState);
        }
        else if (topOverlay is NChooseARelicSelection relicSelectScreen)
        {
            result["state_type"] = "relic_select";
            result["relic_select"] = BuildRelicSelectState(relicSelectScreen, runState);
        }
        else if (topOverlay is NCrystalSphereScreen crystalSphereScreen)
        {
            result["state_type"] = "crystal_sphere";
            result["crystal_sphere"] = BuildCrystalSphereState(crystalSphereScreen, runState);
        }
        else if (!mapIsOpen && topOverlay is NCardRewardSelectionScreen cardRewardScreen)
        {
            result["state_type"] = "card_reward";
            result["card_reward"] = BuildCardRewardState(cardRewardScreen);
        }
        else if (topOverlay is NRewardsScreen rewardsScreen
                 && (!mapIsOpen || RewardsSetIsOutstanding(rewardsScreen)))
        {
            // Mirrors the singleplayer builder: a rewards set the run is still
            // waiting on outranks a visible map screen, or a set offered from
            // inside a room action (Tiny Mailbox's rest-site potions) is masked
            // as the room it came from and nobody can act on it.
            result["state_type"] = "rewards";
            result["rewards"] = BuildRewardsState(rewardsScreen, runState);
        }
        else if (topOverlay is IOverlayScreen
                 && topOverlay is not NRewardsScreen
                 && topOverlay is not NCardRewardSelectionScreen)
        {
            result["state_type"] = "overlay";
            result["overlay"] = new Dictionary<string, object?>
            {
                ["screen_type"] = topOverlay.GetType().Name,
                ["message"] = $"An overlay ({topOverlay.GetType().Name}) is active. It may require manual interaction in-game."
            };
        }
        else if (currentRoom is CombatRoom loadingCombat
                 && !CombatManager.Instance.IsInProgress
                 && (CombatManager.Instance.IsStarting
                     || IsMapTravelInFlight()
                     || IsRunTransitionInFlight()))
        {
            // Mirrors the singleplayer builder: the combat room is entered but
            // not started yet, and the map screen is still visible over it. See
            // the long comment there - reporting "map" here lets a client travel
            // a second time on top of the travel that is still resolving.
            result["state_type"] = loadingCombat.RoomType.ToString().ToLower();
            result["combat_starting"] = true;
            result["message"] = "Combat is starting. Wait for the battle state; do not send actions yet.";
        }
        else if (currentRoom is CombatRoom combatRoom)
        {
            if (CombatManager.Instance.IsInProgress)
            {
                var playerHand = NPlayerHand.Instance;
                if (playerHand != null && playerHand.IsInCardSelection)
                {
                    result["state_type"] = "hand_select";
                    result["hand_select"] = BuildHandSelectState(playerHand, runState);
                    result["battle"] = BuildMultiplayerBattleState(runState, combatRoom);
                }
                else
                {
                    result["state_type"] = combatRoom.RoomType.ToString().ToLower();
                    result["battle"] = BuildMultiplayerBattleState(runState, combatRoom);
                }
            }
            else
            {
                // After combat ends - reward/card overlays are caught by top-level checks above.
                if (IsMapScreenOpenOrVisible())
                {
                    result["state_type"] = "map";
                    result["map"] = BuildMultiplayerMapState(runState);
                }
                else
                {
                    result["state_type"] = combatRoom.RoomType.ToString().ToLower();
                    result["message"] = "Combat ended. Waiting for rewards...";
                }
            }
        }
        else if (currentRoom is EventRoom eventRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMultiplayerMapState(runState);
            }
            else if (eventRoom.CanonicalEvent is FakeMerchant)
            {
                result["state_type"] = "fake_merchant";
                result["fake_merchant"] = BuildFakeMerchantState(eventRoom, runState);
            }
            else
            {
                result["state_type"] = "event";
                result["event"] = BuildMultiplayerEventState(eventRoom, runState);
            }
        }
        else if (currentRoom is MapRoom)
        {
            result["state_type"] = "map";
            result["map"] = BuildMultiplayerMapState(runState);
        }
        else if (currentRoom is MerchantRoom merchantRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMultiplayerMapState(runState);
            }
            else
            {
                result["state_type"] = "shop";
                result["shop"] = BuildShopState(merchantRoom, runState);
            }
        }
        else if (currentRoom is RestSiteRoom restSiteRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMultiplayerMapState(runState);
            }
            else
            {
                result["state_type"] = "rest_site";
                result["rest_site"] = BuildRestSiteState(restSiteRoom, runState);
            }
        }
        else if (currentRoom is TreasureRoom treasureRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMultiplayerMapState(runState);
            }
            else
            {
                result["state_type"] = "treasure";
                result["treasure"] = BuildMultiplayerTreasureState(treasureRoom, runState);
            }
        }
        else
        {
            result["state_type"] = "unknown";
            result["room_type"] = currentRoom?.GetType().Name;
        }

        if (currentRoom is CombatRoom overlayCombat
            && CombatManager.Instance.IsInProgress
            && result.TryGetValue("state_type", out var overlayStateType)
            && overlayStateType is string overlayType
            && overlayType is "card_select" or "bundle_select")
        {
            result["battle"] = BuildMultiplayerBattleState(runState, overlayCombat);
        }

        // Common run info
        var runInfo = new Dictionary<string, object?>
        {
            ["act"] = runState.CurrentActIndex + 1,
            ["floor"] = runState.TotalFloor,
            ["ascension"] = runState.AscensionLevel
        };
        // Run RNG streams on every screen, not just in combat. The host-synced
        // RunRngSet is shared by all players, so these counters reflect everyone.
        var runRng = BuildRngStreams(runState);
        if (runRng != null)
            runInfo["rng"] = runRng;
        runInfo["visited_event_ids"] = runState.VisitedEventIds.Select(id => id.Entry).ToList();
        result["run"] = runInfo;

        // Keep the route-planning contract identical to singleplayer. These
        // pre-rolled queues are shared run state in co-op too; omitting them
        // forced every multiplayer map decision onto the local-node fallback.
        var actInfo = BuildActState(runState);
        if (actInfo != null)
            result["act"] = actInfo;

        try
        {
            result["profile"] = new Dictionary<string, object?>
            {
                ["number_of_runs"] = runState.UnlockState.NumberOfRuns
            };
        }
        catch { }

        // All players summary (always included for multiplayer)
        result["players"] = BuildAllPlayersState(runState);

        // Always include full local player data (relics, potions, deck, etc.) on every screen,
        // matching singleplayer behavior from BuildGameState()
        if (localPlayer != null)
        {
            result["player"] = BuildPlayerState(localPlayer);
        }

        return result;
    }

    private static Dictionary<string, object?> BuildMultiplayerBattleState(RunState runState, CombatRoom combatRoom)
    {
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        var battle = new Dictionary<string, object?>();

        if (combatState == null)
        {
            battle["error"] = "Combat state unavailable";
            return battle;
        }

        battle["round"] = combatState.RoundNumber;
        battle["turn"] = combatState.CurrentSide.ToString().ToLower();
        battle["is_play_phase"] = IsPlayPhase(combatState);
        battle["all_players_ready"] = CombatManager.Instance.AllPlayersReadyToEndTurn();

        // Enemies
        var enemies = new List<Dictionary<string, object?>>();
        var entityCounts = new Dictionary<string, int>();
        var localCreature = LocalContext.GetMe(runState)?.Creature;
        foreach (var creature in combatState.Enemies)
        {
            if (creature.IsAlive)
                enemies.Add(BuildEnemyState(creature, entityCounts, localCreature));
        }
        battle["enemies"] = enemies;

        // Run RNG stream coordinates — the host-synced RunRngSet is shared by
        // all players in lockstep MP, so the local counters reflect everyone.
        var rngStreams = BuildRngStreams(runState);
        if (rngStreams != null)
            battle["rng_streams"] = rngStreams;

        // Combat history: every player's card plays + per-player damage dealt
        // to enemies.  MP combat is lockstep (ActionQueueSynchronizer replays
        // every peer's actions locally), so the local CombatHistory contains
        // remote players' plays too.
        try
        {
            battle["card_plays"] = BuildCardPlaysState();
            battle["card_history"] = BuildCardHistoryState(combatState);
            battle["player_damage"] = BuildPlayerDamageState(runState, combatState.RoundNumber);
        }
        catch
        {
            // History unavailable (e.g. combat tearing down) — omit the fields
        }

        return battle;
    }

    // CombatHistoryEntry.RoundNumber is private; read it via cached reflection.
    private static readonly PropertyInfo? _historyRoundProp =
        typeof(CombatHistoryEntry).GetProperty("RoundNumber",
            BindingFlags.NonPublic | BindingFlags.Instance);

    private static int GetEntryRound(CombatHistoryEntry entry)
    {
        try { return _historyRoundProp?.GetValue(entry) is int r ? r : 0; }
        catch { return 0; }
    }

    // CombatHistoryEntry._playerTurnNumbers is private; read it via cached
    // reflection so each row can carry the card OWNER's turn number. The public
    // RoundNumber is not enough: an extra turn carries the same RoundNumber as
    // the turn before it, so a consumer grouping rows into turns would merge the
    // two. PlayerCombatState.TurnNumber counts extra turns, and this dictionary
    // is the snapshot of it taken when the entry was logged.
    private static readonly FieldInfo? _historyTurnNumbersField =
        typeof(CombatHistoryEntry).GetField("_playerTurnNumbers",
            BindingFlags.NonPublic | BindingFlags.Instance);

    private static int? GetEntryPlayerTurn(CombatHistoryEntry entry, Player? owner)
    {
        if (owner == null)
            return null;
        try
        {
            if (_historyTurnNumbersField?.GetValue(entry) is Dictionary<ulong, int> turns
                && turns.TryGetValue(owner.NetId, out var turn))
                return turn;
        }
        catch { /* layout changed — the row still carries round/card id */ }
        return null;
    }

    // The multiplayer synchronizer ids every mutable combat card; that identity
    // is what tells two copies of Strike apart across polls. Deliberately NOT
    // gated on Pile?.IsCombatPile the way BuildCardInfo is: a history row may
    // name a card that has since been transformed or destroyed and so belongs to
    // no pile, and its id is exactly what lets a consumer match it back to the
    // hand it was last seen in.
    private static uint? GetCombatCardId(CardModel? card)
    {
        if (card == null)
            return null;
        try { return NetCombatCard.FromModel(card).CombatCardIndex; }
        catch { return null; }
    }

    // CardModel.Owner throws once the owning creature is gone (combat teardown,
    // a destroyed card), and every caller here is inside a state build that must
    // not fail as a whole.
    private static Player? CardOwner(CardModel? card)
    {
        if (card == null)
            return null;
        try { return card.Owner; }
        catch { return null; }
    }

    private static List<Dictionary<string, object?>> BuildCardPlaysState()
    {
        var plays = new List<Dictionary<string, object?>>();
        int idx = 0;
        foreach (var entry in CombatManager.Instance.History.CardPlaysFinished)
        {
            var cardPlay = entry.CardPlay;
            string? target = null;
            try
            {
                target = cardPlay.Target?.Monster?.Id.Entry
                         ?? SafeGetText(() => cardPlay.Target?.Player?.Character.Title);
            }
            catch { /* target creature may be gone */ }

            plays.Add(new Dictionary<string, object?>
            {
                // Stable per-combat sequence number — the history only appends
                // within a fight, so pollers dedup with "index > last seen".
                ["index"] = idx++,
                ["round"] = GetEntryRound(entry),
                // An extra turn shares its round with the turn before it, so
                // `round` alone cannot group plays into turns.
                ["player_turn"] = GetEntryPlayerTurn(entry, CardOwner(cardPlay.Card)),
                ["player"] = SafeGetText(() => cardPlay.Card.Owner.Character.Title),
                ["is_local"] = LocalContext.IsMe(cardPlay.Card.Owner),
                ["card_id"] = SafeGetText(() => cardPlay.Card.Id.Entry),
                ["card_name"] = SafeGetText(() => cardPlay.Card.Title),
                // Joins this play to the instance-identified pile snapshots.
                ["combat_card_id"] = GetCombatCardId(cardPlay.Card),
                // Where the card went after resolving — the difference between
                // "played and discarded" and "played and exhausted".
                ["result_pile"] = cardPlay.ResultPile.ToString(),
                ["play_index"] = cardPlay.PlayIndex,
                ["play_count"] = cardPlay.PlayCount,
                ["is_auto_play"] = cardPlay.IsAutoPlay,
                ["target"] = target
            });
        }
        return plays;
    }

    /// <summary>
    /// Where every card went during the current and previous player turn.
    ///
    /// `card_plays` answers "what was played"; this answers everything else, and
    /// the two together are what let a client reconstruct a whole turn's card
    /// churn without diffing pile snapshots across polls. Diffing cannot work on
    /// its own: a card drawn and discarded between two polls is never observed
    /// in any pile, and a card mid-resolution sits in PileType.Play, which is not
    /// exported at all.
    ///
    /// Deliberately NOT a source for "left in hand at end of turn":
    /// CombatManager.FlushPlayerHand moves the leftovers with CardPileCmd.Add
    /// rather than CardCmd.Discard, and only the latter reaches
    /// CombatHistory.CardDiscarded — so the end-of-turn flush logs nothing here.
    /// Every `discarded` row is therefore a mid-turn discard, and a client after
    /// the flush must read the hand instead (see `should_retain_this_turn`).
    ///
    /// Bounded to this turn plus each owner's previous turn so a long fight does
    /// not re-serialize hundreds of rows on every poll. Reading the previous turn
    /// is what lets a client wait until a turn is complete — the end-of-turn
    /// cleanup resolves after the turn ends — before scoring it.
    /// </summary>
    private static List<Dictionary<string, object?>> BuildCardHistoryState(ICombatState combatState)
    {
        var rows = new List<Dictionary<string, object?>>();
        int idx = 0;
        foreach (var entry in CombatManager.Instance.History.Entries)
        {
            string kind;
            CardModel? card;
            switch (entry)
            {
                case CardPlayFinishedEntry played: kind = "played"; card = played.CardPlay.Card; break;
                case CardDrawnEntry drawn: kind = "drawn"; card = drawn.Card; break;
                case CardDiscardedEntry discarded: kind = "discarded"; card = discarded.Card; break;
                case CardExhaustedEntry exhausted: kind = "exhausted"; card = exhausted.Card; break;
                case CardGeneratedEntry generated: kind = "generated"; card = generated.Card; break;
                default: continue;
            }
            if (card == null)
                continue;

            var owner = CardOwner(card);
            bool inScope;
            try
            {
                inScope = entry.HappenedThisTurn(combatState)
                          || (owner != null && entry.HappenedLastPlayerTurn(owner));
            }
            catch { continue; }
            if (!inScope)
                continue;

            rows.Add(new Dictionary<string, object?>
            {
                ["index"] = idx++,
                ["kind"] = kind,
                ["round"] = GetEntryRound(entry),
                ["player_turn"] = GetEntryPlayerTurn(entry, owner),
                ["is_local"] = LocalContext.IsMine(card),
                ["card_id"] = SafeGetText(() => card.Id.Entry),
                ["card_name"] = SafeGetText(() => card.Title),
                ["is_upgraded"] = card.IsUpgraded,
                ["combat_card_id"] = GetCombatCardId(card),
                ["from_hand_draw"] = entry is CardDrawnEntry drawnEntry && drawnEntry.FromHandDraw,
                // Machine-readable CardKeyword names. The `keywords` field on a
                // card object is the localized hover-tip list and cannot be
                // matched against reliably.
                ["keyword_ids"] = BuildKeywordIds(card),
            });
        }
        return rows;
    }

    /// <summary>
    /// CardKeyword enum names for a card, or null when they cannot be read.
    /// Distinct from the localized `keywords` hover tips that card objects carry.
    /// </summary>
    private static string[]? BuildKeywordIds(CardModel? card)
    {
        if (card == null)
            return null;
        try { return card.Keywords.Select(kw => kw.ToString()).ToArray(); }
        catch { return null; }
    }

    private static List<Dictionary<string, object?>> BuildPlayerDamageState(RunState runState, int currentRound)
    {
        // Aggregate unblocked (HP) damage dealt to enemies, per player per
        // round.  Pet damage credits the pet's owner; unattributable sources
        // (dealer-less DoTs) are skipped.
        int rounds = Math.Max(1, currentRound);
        var totals = new Dictionary<Player, int[]>();
        foreach (var entry in CombatManager.Instance.History.Entries)
        {
            if (entry is not DamageReceivedEntry dmg)
                continue;
            if (dmg.Dealer == null || !dmg.Receiver.IsMonster)
                continue;
            var owner = dmg.Dealer.Player ?? dmg.Dealer.PetOwner;
            if (owner == null)
                continue;
            int round = Math.Clamp(GetEntryRound(entry), 1, rounds);
            if (!totals.TryGetValue(owner, out var byRound))
            {
                byRound = new int[rounds];
                totals[owner] = byRound;
            }
            byRound[round - 1] += dmg.Result.UnblockedDamage;
        }

        var result = new List<Dictionary<string, object?>>();
        foreach (var player in runState.Players)
        {
            totals.TryGetValue(player, out var byRound);
            result.Add(new Dictionary<string, object?>
            {
                ["player"] = SafeGetText(() => player.Character.Title),
                ["is_local"] = LocalContext.IsMe(player),
                // by_round[i] = HP damage dealt to enemies in round i+1
                ["by_round"] = (byRound ?? new int[rounds]).ToList(),
                ["total"] = byRound?.Sum() ?? 0
            });
        }
        return result;
    }

    private static Dictionary<string, object?> BuildMultiplayerMapState(RunState runState)
    {
        // Start with the standard map state
        var state = BuildMapState(runState);

        // Add per-player vote data
        try
        {
            var mapSync = RunManager.Instance.MapSelectionSynchronizer;
            var votes = new List<Dictionary<string, object?>>();

            foreach (var player in runState.Players)
            {
                var vote = mapSync.GetVote(player);
                votes.Add(new Dictionary<string, object?>
                {
                    ["player"] = SafeGetText(() => player.Character.Title),
                    ["is_local"] = LocalContext.IsMe(player),
                    ["voted"] = vote != null,
                    ["vote_col"] = vote?.coord.col,
                    ["vote_row"] = vote?.coord.row
                });
            }

            state["votes"] = votes;
            state["all_voted"] = votes.All(v => v["voted"] is true);
        }
        catch
        {
            // MapSelectionSynchronizer may not be available in all contexts
        }

        // All players summary
        state["players"] = BuildAllPlayersState(runState);

        return state;
    }

    private static Dictionary<string, object?> BuildMultiplayerEventState(EventRoom eventRoom, RunState runState)
    {
        // Start with the standard event state
        var state = BuildEventState(eventRoom, runState);

        // Add multiplayer-specific event data
        try
        {
            var eventSync = RunManager.Instance.EventSynchronizer;
            bool isShared = false;
            try { isShared = eventSync.IsShared; } catch { /* throws if no event in progress */ }
            state["is_shared"] = isShared;

            if (isShared)
            {
                var votes = new List<Dictionary<string, object?>>();
                foreach (var player in runState.Players)
                {
                    var vote = eventSync.GetPlayerVote(player);
                    votes.Add(new Dictionary<string, object?>
                    {
                        ["player"] = SafeGetText(() => player.Character.Title),
                        ["is_local"] = LocalContext.IsMe(player),
                        ["voted"] = vote != null,
                        ["vote_option"] = vote
                    });
                }
                state["votes"] = votes;
                state["all_voted"] = votes.All(v => v["voted"] is true);
            }
        }
        catch
        {
            // EventSynchronizer may not be available
        }

        // All players summary
        state["players"] = BuildAllPlayersState(runState);

        return state;
    }

    private static Dictionary<string, object?> BuildMultiplayerTreasureState(TreasureRoom treasureRoom, RunState runState)
    {
        // Auto-open chest same as singleplayer. BeginRelicPicking() runs during
        // TreasureRoom.Enter(), so relics are already generated. The chest click
        // just triggers the UI animation + gold via OneOffSynchronizer - same path
        // as a human click or the game's own AutoSlay handler.
        var state = BuildTreasureState(treasureRoom, runState);

        // Add per-player bid data
        try
        {
            var treasureSync = RunManager.Instance.TreasureRoomRelicSynchronizer;
            var currentRelics = treasureSync.CurrentRelics;

            state["is_bidding_phase"] = currentRelics != null;

            if (currentRelics != null)
            {
                var bids = new List<Dictionary<string, object?>>();
                foreach (var player in runState.Players)
                {
                    var vote = treasureSync.GetPlayerVote(player);
                    bids.Add(new Dictionary<string, object?>
                    {
                        ["player"] = SafeGetText(() => player.Character.Title),
                        ["is_local"] = LocalContext.IsMe(player),
                        ["voted"] = vote != null,
                        ["vote_relic_index"] = vote
                    });
                }
                state["bids"] = bids;
                state["all_bid"] = bids.All(b => b["voted"] is true);
            }
        }
        catch
        {
            // TreasureRoomRelicSynchronizer may not be available
        }

        // All players summary
        state["players"] = BuildAllPlayersState(runState);

        return state;
    }

    private static List<Dictionary<string, object?>> BuildAllPlayersState(RunState runState)
    {
        bool inCombat = CombatManager.Instance.IsInProgress;
        var players = new List<Dictionary<string, object?>>();
        for (int i = 0; i < runState.Players.Count; i++)
        {
            var player = runState.Players[i];
            var entry = new Dictionary<string, object?>
            {
                // Stable target id for ally/player-targeting cards and potions
                // (play_card / use_potion 'target' parameter).
                ["entity_id"] = $"player_{i}",
                ["character"] = SafeGetText(() => player.Character.Title),
                ["is_local"] = LocalContext.IsMe(player),
                ["hp"] = player.Creature.CurrentHp,
                ["max_hp"] = player.Creature.MaxHp,
                ["gold"] = player.Gold,
                ["is_alive"] = player.Creature.IsAlive
            };

            // Each player rolls their own rewards/shops/transformations off a
            // slot-seeded PlayerRngSet. See McpMod.RngStreams.cs.
            var playerRng = BuildPlayerRngStreams(player);
            if (playerRng != null)
                entry["rng"] = playerRng;

            if (inCombat)
            {
                entry["combat_id"] = player.Creature.CombatId;
                entry["block"] = player.Creature.Block;
                entry["is_ready_to_end_turn"] = CombatManager.Instance.IsPlayerReadyToEndTurn(player);

                // Include pets for teammates (local player's pets are under "player")
                if (!LocalContext.IsMe(player))
                {
                    var pets = BuildPetsState(player);
                    if (pets.Count > 0)
                    {
                        entry["pets"] = pets;
                    }

                    // Reveal the teammate's hand + pile counts. MP combat is
                    // lockstep — every player's PlayerCombatState is replicated
                    // locally — so a teammate's hand cards are known client-side
                    // even though the game UI hides them (this mirrors the
                    // STS2-ShowPlayerHandCards mod). Use BuildCardInfo rather than
                    // BuildCardState so we don't run CanPlay hooks against a
                    // non-local player's card in the local combat context.
                    try
                    {
                        var cs = player.PlayerCombatState;
                        if (cs != null)
                        {
                            var hand = new List<Dictionary<string, object?>>();
                            int cardIndex = 0;
                            foreach (var card in cs.Hand.Cards)
                            {
                                var info = BuildCardInfo(card);
                                info["index"] = cardIndex++;
                                info["target_type"] = card.TargetType.ToString();
                                hand.Add(info);
                            }
                            entry["hand"] = hand;
                            entry["draw_pile_count"] = cs.DrawPile.Cards.Count;
                            entry["discard_pile_count"] = cs.DiscardPile.Cards.Count;
                            entry["exhaust_pile_count"] = cs.ExhaustPile.Cards.Count;

                            // Full pile contents so the teammate's turn can be
                            // forward-modelled. Draw pile is in true order
                            // (index 0 = next draw), valid until their next
                            // shuffle — same guarantee as the local player's.
                            entry["draw_pile"] = BuildPileCardList(cs.DrawPile.Cards, PileType.Draw);
                            entry["discard_pile"] = BuildPileCardList(cs.DiscardPile.Cards, PileType.Discard);
                            entry["exhaust_pile"] = BuildPileCardList(cs.ExhaustPile.Cards, PileType.Exhaust);

                            // Powers/status + orbs — also needed to model their
                            // turn (Strength/Vulnerable scaling, Defect orbs).
                            entry["status"] = BuildPowersState(player.Creature);
                            AddOrbsState(entry, player);

                            // Relics + potions make the teammate's turn fully
                            // searchable (per-card play legality, potion actions).
                            entry["relics"] = BuildRelicsList(player);
                            AddPotionsState(entry, player);

                            // Energy getters run hooks (Hook.ModifyMaxEnergy) —
                            // isolate them so a throw still leaves the hand intact.
                            try
                            {
                                entry["energy"] = cs.Energy;
                                entry["max_energy"] = cs.MaxEnergy;
                            }
                            catch { /* energy hooks threw — omit energy only */ }
                        }
                    }
                    catch { /* teammate combat state mid-transition — omit hand */ }
                }
            }
            players.Add(entry);
        }
        return players;
    }
}
