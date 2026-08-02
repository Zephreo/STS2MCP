using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace STS2_MCP
{
    public static partial class McpMod
    {
        /// <summary>Runs the move-effect scanner over one body.</summary>
        private static MoveFxScan ScanBody(string methodName)
        {
            var monster = new MoveBodies();
            var method = typeof(MoveBodies).GetMethod(methodName)
                ?? throw new InvalidOperationException(methodName);
            var scan = new MoveFxScan();
            // An async body compiles to a stub plus a nested state machine; the
            // effects live in the latter, exactly as in the shipped moves.
            foreach (var body in BodiesOf(method))
                WalkForEffects(body, monster, scan);
            return scan;
        }

        private static IEnumerable<MethodBase> BodiesOf(MethodInfo method)
        {
            yield return method;
            var stateMachine = method.DeclaringType?
                .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(t => t.Name.StartsWith($"<{method.Name}>d__"));
            var moveNext = stateMachine?.GetMethod("MoveNext",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (moveNext != null)
                yield return moveNext;
        }

        private static void CheckApply(string label, string methodName, int index,
            string power, string target, object? amount)
        {
            var scan = ScanBody(methodName);
            if (scan.Applies.Count <= index)
            {
                Check($"{label} apply[{index}]", $"missing (found {scan.Applies.Count})", "present");
                return;
            }
            var apply = scan.Applies[index];
            Check($"{label} power", apply["power"], power);
            Check($"{label} target", apply["target"], target);
            Check($"{label} amount", apply["amount"], amount);
        }

        private static void CheckInsert(string label, string methodName, int index,
            string card, object? count, object? pile, object? position)
        {
            var scan = ScanBody(methodName);
            if (scan.StatusCards.Count <= index)
            {
                Check($"{label} insert[{index}]", $"missing (found {scan.StatusCards.Count})", "present");
                return;
            }
            var insert = scan.StatusCards[index];
            Check($"{label} card", insert["card"], card);
            Check($"{label} count", insert["count"], count);
            Check($"{label} pile", insert["pile"], pile);
            Check($"{label} position", insert["position"], position);
        }

        private static void CheckMoveEffects()
        {
            Console.WriteLine();

            // Every way the game reaches a Decimal amount.
            CheckApply("stat getter", nameof(MoveBodies.StatGetterAmount), 0, "StrengthPower", "self", 3);
            CheckApply("1m literal", nameof(MoveBodies.OneLiteralAmount), 0, "WeakPower", "player", 1);
            CheckApply("2m literal", nameof(MoveBodies.TwoLiteralAmount), 0, "IntangiblePower", "self", 2);

            // The bleed that had no flag: an attack's damage must not become the
            // amount of a debuff that follows it.
            CheckApply("after attack #1", nameof(MoveBodies.AttackThenTwoDebuffs), 0, "WeakPower", "player", 2);
            CheckApply("after attack #2", nameof(MoveBodies.AttackThenTwoDebuffs), 1, "FrailPower", "player", 2);

            // A steal keeps its sign; arithmetic over stats folds; a counter
            // field reads live; anything unreachable reports null.
            CheckApply("negated", nameof(MoveBodies.NegatedAmount), 0, "StrengthPower", "player", -2);
            CheckApply("computed", nameof(MoveBodies.ComputedAmount), 0, "StrengthPower", "self", 5);
            CheckApply("field", nameof(MoveBodies.FieldAmount), 0, "StrengthPower", "self", 6);
            CheckApply("unknowable", nameof(MoveBodies.UnknowableAmount), 0, "StrengthPower", "self", null);

            Check("block amount", ScanBody(nameof(MoveBodies.BlockAmount)).Block, 5);

            // Placement, including a move that splits across two piles.
            CheckInsert("discard insert", nameof(MoveBodies.DiscardInsert), 0, "Dazed", 3, "Discard", "Bottom");
            CheckInsert("hand insert", nameof(MoveBodies.HandInsert), 0, "Slimed", 2, "Hand", "Bottom");
            CheckInsert("generated discard", nameof(MoveBodies.SplitGeneratedInserts), 0,
                "Dazed", 1, "Discard", "Bottom");
            CheckInsert("generated draw", nameof(MoveBodies.SplitGeneratedInserts), 1,
                "Dazed", 1, "Draw", "Random");
            CheckInsert("getter count", nameof(MoveBodies.GetterCountInsert), 0, "Beckon", 4, "Discard", "Bottom");

            // The non-generic overload: its power comes from the preceding
            // ModelDb.Power<T>, and self versus player from the creature
            // expression, since the parameter type is Creature either way.
            CheckApply("instanced self", nameof(MoveBodies.InstancedPowerOnSelf), 0, "SandpitPower", "self", 4);
            CheckApply("instanced target", nameof(MoveBodies.InstancedPowerOnTarget), 0,
                "DampenPower", "player", 1);
        }
    }
}
