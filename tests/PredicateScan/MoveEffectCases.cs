using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// Move bodies copied from the decompiled monsters, reduced to the calls the
// scanner reads. Each one pins a way the game reaches an amount or an insert.

public sealed class MoveBodies : MegaCrit.Sts2.Core.Models.MonsterModel
{
    private int _ritualGain = 6;

    private int HissStrengthGain => 3;
    private int BugStingDamage => 11;
    private int BugStingTimes => 2;
    private int BootUpStrGain => 5;
    private int StockAmount => 1;
    private int SpikenAmount => 2;
    private int WitherAmount => 4;

    private IReadOnlyList<Creature> Targets => Array.Empty<Creature>();

    /// <summary>`Nibbit.HissMove`: an int stat getter reaches the amount.</summary>
    public async Task StatGetterAmount(IReadOnlyList<Creature> targets) =>
        await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(), base.Creature,
            HissStrengthGain, base.Creature, null);

    /// <summary>`BowlbugSilk.SpitMove`: `1m` is a Decimal field constant.</summary>
    public async Task OneLiteralAmount(IReadOnlyList<Creature> targets) =>
        await PowerCmd.Apply<WeakPower>(new ThrowingPlayerChoiceContext(), targets, 1m, base.Creature, null);

    /// <summary>`Flyconid.VulnerableSporesMove`: `2m` needs the Decimal ctor.</summary>
    public async Task TwoLiteralAmount(IReadOnlyList<Creature> targets) =>
        await PowerCmd.Apply<IntangiblePower>(new ThrowingPlayerChoiceContext(), base.Creature,
            2m, base.Creature, null);

    /// <summary>
    /// `Crusher.BugStingMove`: an attack precedes two literal-amount debuffs.
    /// The attack's own damage must not leak into either.
    /// </summary>
    public async Task AttackThenTwoDebuffs(IReadOnlyList<Creature> targets)
    {
        await DamageCmd.Attack(BugStingDamage).WithHitCount(BugStingTimes).FromMonster(this).Execute(null);
        await PowerCmd.Apply<WeakPower>(new ThrowingPlayerChoiceContext(), targets, 2m, base.Creature, null);
        await PowerCmd.Apply<FrailPower>(new ThrowingPlayerChoiceContext(), targets, 2m, base.Creature, null);
    }

    /// <summary>`Toadpole.SpikeSpitMove`: a negated stat is a steal.</summary>
    public async Task NegatedAmount(IReadOnlyList<Creature> targets) =>
        await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(), targets,
            -SpikenAmount, base.Creature, null);

    /// <summary>`Axebot.BootUpMove`: arithmetic over two stat getters.</summary>
    public async Task ComputedAmount(IReadOnlyList<Creature> targets) =>
        await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(), base.Creature,
            BootUpStrGain * (2 - StockAmount), base.Creature, null);

    /// <summary>`DevotedSculptor`: the amount is a mutable counter field.</summary>
    public async Task FieldAmount(IReadOnlyList<Creature> targets) =>
        await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(), base.Creature,
            _ritualGain, base.Creature, null);

    /// <summary>An amount no static read can reach must report as unknown.</summary>
    public async Task UnknowableAmount(IReadOnlyList<Creature> targets)
    {
        int fromRuntime = targets.Count * 7;
        await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(), base.Creature,
            fromRuntime, base.Creature, null);
    }

    /// <summary>`Nibbit.SliceMove`: block from a stat getter.</summary>
    public async Task BlockAmount(IReadOnlyList<Creature> targets) =>
        await CreatureCmd.GainBlock(base.Creature, 5, ValueProp.Move, null);

    /// <summary>`Chomper.ScreechMove`: a bottom-of-discard status insert.</summary>
    public async Task DiscardInsert(IReadOnlyList<Creature> targets) =>
        await CardPileCmd.AddToCombatAndPreview<Dazed>(targets, PileType.Discard, 3, null);

    /// <summary>`MechaKnight.FlamethrowerMove`: an insert straight to hand.</summary>
    public async Task HandInsert(IReadOnlyList<Creature> targets) =>
        await CardPileCmd.AddToCombatAndPreview<Slimed>(targets, PileType.Hand, 2, null);

    /// <summary>
    /// `Noisebot.NoiseMove`: two generated cards, one to the discard and one at
    /// a random draw-pile position.
    /// </summary>
    public async Task SplitGeneratedInserts(IReadOnlyList<Creature> targets)
    {
        CardModel first = base.CombatState.CreateCard<Dazed>(null);
        await CardPileCmd.AddGeneratedCardToCombat(first, PileType.Discard, null);
        CardModel second = base.CombatState.CreateCard<Dazed>(null);
        await CardPileCmd.AddGeneratedCardToCombat(second, PileType.Draw, null, CardPilePosition.Random);
    }

    /// <summary>
    /// `TheInsatiable.LiquifyGroundMove`: an instanced power applied to self
    /// through the non-generic overload.
    /// </summary>
    public async Task InstancedPowerOnSelf(IReadOnlyList<Creature> targets)
    {
        SandpitPower sandpit = (SandpitPower)ModelDb.Power<SandpitPower>().ToMutable();
        await PowerCmd.Apply(new ThrowingPlayerChoiceContext(), sandpit, base.Creature, 4m, base.Creature, null);
    }

    /// <summary>
    /// `MagiKnight.DampenMove`: the same overload aimed at a target creature.
    /// </summary>
    public async Task InstancedPowerOnTarget(IReadOnlyList<Creature> targets)
    {
        DampenPower dampen = (DampenPower)ModelDb.Power<DampenPower>().ToMutable();
        foreach (Creature target in targets)
            await PowerCmd.Apply(new ThrowingPlayerChoiceContext(), dampen, target, 1m, base.Creature, null);
    }

    /// <summary>`Aeonglass`: the insert count is itself a stat getter.</summary>
    public async Task GetterCountInsert(IReadOnlyList<Creature> targets) =>
        await CardPileCmd.AddToCombatAndPreview<Beckon>(targets, PileType.Discard, WitherAmount, null);
}

public class CombatStateStub
{
    public CardModel CreateCard<T>(Player? player) where T : CardModel, new() => new T();
}
