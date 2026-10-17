using System;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Players;

namespace STS2_MCP;

public static partial class McpMod
{
    private static readonly FieldInfo HistoryPlayerTurnNumbersField = typeof(CombatHistoryEntry)
        .GetField("_playerTurnNumbers", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(CombatHistoryEntry).FullName, "_playerTurnNumbers");

    // Emotion Chip queries HappenedLastPlayerTurn after the next owner turn
    // starts. Seed that future query from this owner's current turn number,
    // including damage on the enemy side. HappenedThisTurn would exclude it.
    private static bool DamageNotFullyBlockedThisOwnerTurn(Player player, IEnumerable<DamageReceivedEntry> entries)
    {
        var turn = player.PlayerCombatState?.TurnNumber
            ?? throw new InvalidOperationException("Owner-turn damage history requires an active player combat state");
        foreach (var entry in entries)
        {
            if (entry.Receiver != player.Creature || entry.Result.WasFullyBlocked)
                continue;
            var turns = HistoryPlayerTurnNumbersField.GetValue(entry) as IReadOnlyDictionary<ulong, int>
                ?? throw new InvalidOperationException("Combat history player turn numbers are unavailable");
            if (turns.TryGetValue(player.NetId, out var captured) && captured == turn)
                return true;
        }
        return false;
    }
}
