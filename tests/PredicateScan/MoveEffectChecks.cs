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

        private static void CheckRemove(string label, string methodName, int index,
            string power, string target)
        {
            var scan = ScanBody(methodName);
            if (scan.Removes.Count <= index)
            {
                Check($"{label} remove[{index}]", $"missing (found {scan.Removes.Count})", "present");
                return;
            }
            var removal = scan.Removes[index];
            Check($"{label} power", removal["power"], power);
            Check($"{label} target", removal["target"], target);
        }

        private static void CheckAllyBlock(string label, string methodName, int index,
            object? amount, object? monster)
        {
            var scan = ScanBody(methodName);
            if (scan.AllyBlock.Count <= index)
            {
                Check($"{label} ally_block[{index}]", $"missing (found {scan.AllyBlock.Count})", "present");
                return;
            }
            var grant = scan.AllyBlock[index];
            Check($"{label} amount", grant["amount"], amount);
            Check($"{label} monster", grant["monster"], monster);
            // Whatever else it did, it did not credit the Block to the mover.
            Check($"{label} not self block", scan.Block, null);
        }

        private static void CheckMoveEffects()
        {
            Console.WriteLine();

            var bowlbug = new BowlbugRock();
            var emptyStun = new MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState(
                BowlbugRock.Wrap(_ => System.Threading.Tasks.Task.CompletedTask), "STUNNED");
            var dizzyStun = new MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState(
                BowlbugRock.Wrap(bowlbug.DizzyMove), "STUNNED");
            Check("empty stun carries no Dizzy removal", GetMoveEffects(bowlbug, emptyStun) == null, true);
            var dizzyFx = GetMoveEffects(bowlbug, dizzyStun)!;
            var dizzyRemoves = (Dictionary<string, object?>[])dizzyFx["removes"]!;
            Check("wrapped Dizzy clears off-balance", dizzyRemoves[0]["power"], "IMBALANCED_STUN");
            Check("wrapped Dizzy clears own flag", dizzyRemoves[0]["target"], "self");
            Check("later empty stun stays empty", GetMoveEffects(bowlbug, emptyStun) == null, true);

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
            Check("block amount is self only",
                ScanBody(nameof(MoveBodies.BlockAmount)).AllyBlock.Count, 0);

            // Block aimed at somebody else. Crediting it to the mover gave the
            // Guardbot 15 Block a turn it never has while the Fabricator's real
            // Block went missing, and nothing flagged it — the move's own
            // DefendIntent was satisfied by the wrong creature's number.
            CheckAllyBlock("guardbot", nameof(MoveBodies.AllyBlockForNamedMonster), 0, 15, "Fabricator");
            CheckAllyBlock("unfiltered ally block", nameof(MoveBodies.AllyBlockWithoutFilter), 0, 15, null);
            CheckAllyBlock("ambiguous filter", nameof(MoveBodies.AllyBlockWithAmbiguousFilter), 0, 15, null);

            // A self-block surrounded by applies stays a self-block: the
            // creature expression is sampled at the amount, so the applier
            // argument that follows cannot flip it.
            Check("self block between applies",
                ScanBody(nameof(MoveBodies.DebuffThenSelfBlockThenBuff)).Block, 8);
            Check("self block between applies is not ally",
                ScanBody(nameof(MoveBodies.DebuffThenSelfBlockThenBuff)).AllyBlock.Count, 0);

            // A chain that leaves the monster (`Creature.CombatState.Players
            // .Count`) resolves against the live objects it reaches, so a
            // player-count-scaled heal exports its product rather than flagging.
            // The stub combat holds two players.
            Check("player-count heal", ScanBody(nameof(MoveBodies.PlayerCountScaledHeal)).Heal, 60);
            Check("stat x player-count heal", ScanBody(nameof(MoveBodies.StatTimesPlayerCountHeal)).Heal, 30);

            // The cold exporter has no combat. Source descriptors preserve the
            // multiplier; the native consumer owns the actual player count.
            foreach (var (healer, moveId, amount) in new[]
            {
                ((MegaCrit.Sts2.Core.Models.MonsterModel)new KnowledgeDemon(), "PONDER_MOVE", 30),
                (new WaterfallGiant(), "SIPHON_MOVE", 10),
                (new WaterfallGiant { ToughEnemies = true }, "SIPHON_MOVE", 15),
            })
            {
                var effects = new Dictionary<string, object?> { ["heal"] = amount * 2 };
                var move = new MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState(
                    (Action)(() => { }), moveId);
                AddStatefulMoveSemantics(healer, move, effects);
                Check($"{moveId} per-player heal", effects["heal_per_player"], amount);
                Check($"{moveId} no duplicated flat heal", effects.ContainsKey("heal"), false);
            }

            var demon = new KnowledgeDemon();
            var curse = new MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState(
                (Action)(() => { }), "CURSE_OF_KNOWLEDGE_MOVE");
            var firstCurse = GetMoveEffects(demon, curse)!;
            Check("first live curse index", firstCurse["knowledge_choice"], 0);
            demon.CurseOfKnowledgeCounter = 1;
            Check("cached second live curse index", GetMoveEffects(demon, curse)!["knowledge_choice"], 1);
            demon.CurseOfKnowledgeCounter = 2;
            Check("cached third live curse index", GetMoveEffects(demon, curse)!["knowledge_choice"], 2);
            Check("older curse observation remains intact", firstCurse["knowledge_choice"], 0);

            var giant = new WaterfallGiant();
            var gun = new MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState(
                (Action)(() => { }), "PRESSURE_GUN_MOVE");
            var firstGun = GetMoveEffects(giant, gun)!;
            Check("Pressure Gun initial base", firstGun["repeat_damage_base"], 20);
            Check("Pressure Gun initial completed", firstGun["repeat_damage_completed"], 0);
            giant.CurrentPressureGunDamage = 30;
            Check("cached Pressure Gun completed", GetMoveEffects(giant, gun)!["repeat_damage_completed"], 2);
            Check("older Pressure Gun observation intact", firstGun["repeat_damage_completed"], 0);

            var subject = new TestSubject();
            var claw = new MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState(
                (Action)(() => { }), "MULTI_CLAW_MOVE");
            var firstClaw = GetMoveEffects(subject, claw)!;
            Check("Multi Claw initial base", firstClaw["repeat_hit_base"], 3);
            subject.ExtraMultiClawCount = 4;
            Check("cached Multi Claw completed", GetMoveEffects(subject, claw)!["repeat_hit_completed"], 4);
            Check("older Multi Claw observation intact", firstClaw["repeat_hit_completed"], 0);

            var aeon = new Aeonglass();
            var intensity = new MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MoveState(
                (Action)(() => { }), "INCREASING_INTENSITY_MOVE");
            var firstIntensity = GetMoveEffects(aeon, intensity)!;
            Check("Intensity base", firstIntensity["repeat_power_base"], 3);
            aeon.AdditionalStrength = 2;
            Check("cached Intensity completed", GetMoveEffects(aeon, intensity)!["repeat_power_completed"], 2);
            Check("older Intensity observation intact", firstIntensity["repeat_power_completed"], 0);

            // Placement, including a move that splits across two piles.
            CheckInsert("discard insert", nameof(MoveBodies.DiscardInsert), 0, "Dazed", 3, "Discard", "Bottom");
            CheckInsert("hand insert", nameof(MoveBodies.HandInsert), 0, "Slimed", 2, "Hand", "Bottom");
            CheckInsert("generated discard", nameof(MoveBodies.SplitGeneratedInserts), 0,
                "Dazed", 1, "Discard", "Bottom");
            CheckInsert("generated draw", nameof(MoveBodies.SplitGeneratedInserts), 1,
                "Dazed", 1, "Draw", "Random");
            CheckInsert("getter count", nameof(MoveBodies.GetterCountInsert), 0, "Beckon", 4, "Discard", "Bottom");

            // Which of the attacker's own powers move its damage. The intent
            // label bakes the current value, so a consumer applies the rest as
            // a delta — and without this it has no way to know Dexterity is one
            // of them for exactly one attack in the game.
            var monster = new MoveBodies();
            Check("dexterity-scaled damage",
                string.Join(",", PowersScalingDamage(monster.DamageScaledByOwnDexterity()) ?? new List<string>()),
                "DexterityPower");
            Check("counter damage scales with nothing",
                PowersScalingDamage(monster.DamageFromAMutableCounter()), null);
            Check("constant damage scales with nothing",
                PowersScalingDamage(monster.DamageFromAConstant(7)), null);

            // The non-generic overload: its power comes from the preceding
            // ModelDb.Power<T>, and self versus player from the creature
            // expression, since the parameter type is Creature either way.
            CheckApply("instanced self", nameof(MoveBodies.InstancedPowerOnSelf), 0, "SandpitPower", "self", 4);
            CheckApply("instanced target", nameof(MoveBodies.InstancedPowerOnTarget), 0,
                "DampenPower", "player", 1);

            // The generic overloads read the same expression rather than
            // trusting the parameter type. A collection returned by
            // GetTeammatesOf is explicitly the mover's own side.
            CheckApply("generic own side", nameof(MoveBodies.GenericPowerOnOwnSide), 0,
                "StrengthPower", "allies", 3);
            CheckApply("generic opponents", nameof(MoveBodies.GenericPowerOnOpponents), 0,
                "FrailPower", "player", 1);
            CheckApply("own side then self #1", nameof(MoveBodies.OwnSideThenSelf), 0,
                "StrengthPower", "allies", 3);
            CheckApply("own side then self #2", nameof(MoveBodies.OwnSideThenSelf), 1,
                "IntangiblePower", "self", 2);

            // A creature taken out of the move's own `targets` is the player
            // side, whichever apply overload consumes it. Without this the
            // Spectral Knight's Hex read as an unplaceable unknown and never
            // reached the player at all.
            CheckApply("targets loop is the player", nameof(MoveBodies.GenericPowerOnLoopTarget), 0,
                "WeakPower", "player", 2);
            // ...but a loop over anything else still says nothing. Reading every
            // loop variable as a player would hand the Guardbot's own side the
            // debuffs meant for us.
            CheckApply("filtered own-side loop", nameof(MoveBodies.GenericPowerOnFilteredOwnSide), 0,
                "StrengthPower", "unknown", 2);
            CheckApply("second loop does not inherit the first",
                nameof(MoveBodies.OwnSideLoopThenTargetLoop), 0, "WeakPower", "player", 2);
            CheckAllyBlock("own-side loop before a target loop",
                nameof(MoveBodies.OwnSideLoopThenTargetLoop), 0, 15, "Fabricator");

            // Powers a move takes back. The Owl Magistrate grants itself Soar and
            // removes it three turns later, so exporting only the grant left it
            // permanently halving the player's own attacks.
            CheckRemove("verdict removal", nameof(MoveBodies.AttackDebuffThenRemoveOwnBuff), 0,
                "SoarPower", "self");
            CheckApply("verdict debuff still lands", nameof(MoveBodies.AttackDebuffThenRemoveOwnBuff), 0,
                "FrailPower", "player", 4);
            CheckRemove("removal from each target", nameof(MoveBodies.RemoveFromEachTarget), 0,
                "SoarPower", "player");
            CheckRemove("removal from a filtered ally", nameof(MoveBodies.RemoveFromAFilteredAlly), 0,
                "SoarPower", "unknown");
            CheckRemove("two removals #1", nameof(MoveBodies.RemoveTwoOwnPowers), 0, "SoarPower", "self");
            CheckRemove("two removals #2", nameof(MoveBodies.RemoveTwoOwnPowers), 1, "DampenPower", "self");
            Check("a removal is not an apply",
                ScanBody(nameof(MoveBodies.RemoveTwoOwnPowers)).Applies.Count, 0);

            // A body that chooses between effect sets exports their SUM, which
            // is a known defect rather than a modeled one: the Entomancer's Spit
            // gives +1 Personal Hive and +1 Strength OR +2 Strength, and the
            // linear scan records all three applies. Pinned here so the ledger's
            // claim about it is a fact the suite checks rather than a note that
            // can go stale, and so the exact fix has a failing expectation to
            // turn green.
            //
            // Detecting the branch was tried and rejected: an async body's own
            // state dispatch and `foreach` headers use the same comparison
            // branches a source-level `if` does, so a peephole rule flagged 13
            // of the 14 non-branching bodies in this file while missing this
            // one. Distinguishing them needs real control-flow reconstruction.
            var branched = ScanBody(nameof(MoveBodies.BranchedApplies));
            Check("branchy body exports both arms", branched.Applies.Count, 3);

            // Summons. The generic overload names the class; the non-generic one
            // takes a model picked at runtime and must stay silent rather than
            // export a creature it guessed at.
            var summoning = ScanBody(nameof(MoveBodies.SummonThenAttack));
            Check("summon class", summoning.Summons.Count > 0 ? summoning.Summons[0]["monster"] : null, "GasBomb");
            Check("one call is one summon", summoning.Summons.Count, 1);
            Check("runtime-picked summon is unreadable",
                ScanBody(nameof(MoveBodies.SummonAModelChosenAtRuntime)).Summons.Count, 0);
            // A computed slot exports none, so the consumer replays GetNextSlot.
            Check("computed slot is not a literal",
                summoning.Summons.Count > 0 ? summoning.Summons[0]["slot"] : "missing", "");
            // A literal one is read outright, and the animation name that
            // precedes it does not leak into the call after it.
            var literalSlot = ScanBody(nameof(MoveBodies.SummonAtALiteralSlot));
            Check("literal slot", literalSlot.Summons.Count > 0 ? literalSlot.Summons[0]["slot"] : null, "illusion");
        }
    }
}
