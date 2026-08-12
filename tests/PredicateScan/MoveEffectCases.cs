using System;
using System.Collections.Generic;
using System.Linq;
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
    private int SiphonHeal => 15;

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

    /// <summary>
    /// `KnowledgeDemon.PonderMove`: a heal scaled by the run's player count,
    /// reached through a property chain that leaves the monster entirely.
    /// </summary>
    public async Task PlayerCountScaledHeal(IReadOnlyList<Creature> targets) =>
        await CreatureCmd.Heal(base.Creature, 30 * base.Creature.CombatState.Players.Count);

    /// <summary>`WaterfallGiant.SiphonMove`: the same, over a stat getter.</summary>
    public async Task StatTimesPlayerCountHeal(IReadOnlyList<Creature> targets) =>
        await CreatureCmd.Heal(base.Creature, SiphonHeal * base.Creature.CombatState.Players.Count);

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

    /// <summary>
    /// `Guardbot.GuardMove`: the one shipped move that shields somebody else.
    /// The recipient class is readable off the `Where` predicate's type test.
    /// </summary>
    public async Task AllyBlockForNamedMonster(IReadOnlyList<Creature> targets)
    {
        List<Creature> bots = base.Creature.CombatState.Enemies
            .Where((Creature c) => c.Monster is Fabricator).ToList();
        foreach (Creature bot in bots)
            await CreatureCmd.GainBlock(bot, 15m, ValueProp.Move, null);
    }

    /// <summary>
    /// A non-self gain with no filter to read: the amount is still exported,
    /// but with no recipient, which the consumer must treat as unknown rather
    /// than crediting it to the mover or to a guess.
    /// </summary>
    public async Task AllyBlockWithoutFilter(IReadOnlyList<Creature> targets)
    {
        foreach (Creature ally in targets)
            await CreatureCmd.GainBlock(ally, 15m, ValueProp.Move, null);
    }

    /// <summary>
    /// A filter testing two monster classes says nothing: which one selects the
    /// recipient is not something a linear read can decide.
    /// </summary>
    public async Task AllyBlockWithAmbiguousFilter(IReadOnlyList<Creature> targets)
    {
        List<Creature> bots = base.Creature.CombatState.Enemies
            .Where((Creature c) => c.Monster is Fabricator || c.Monster is Noisebot).ToList();
        foreach (Creature bot in bots)
            await CreatureCmd.GainBlock(bot, 15m, ValueProp.Move, null);
    }

    /// <summary>
    /// `TheForgotten.MiasmaMove`: a self-block between two applies. The filter
    /// state left by any earlier lambda must not make this one look ally-bound.
    /// </summary>
    public async Task DebuffThenSelfBlockThenBuff(IReadOnlyList<Creature> targets)
    {
        await PowerCmd.Apply<WeakPower>(new ThrowingPlayerChoiceContext(), targets, -2m, base.Creature, null);
        await CreatureCmd.GainBlock(base.Creature, 8m, ValueProp.Move, null);
        await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(), base.Creature,
            2m, base.Creature, null);
    }

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

    /// <summary>
    /// `SpectralKnight.HexMove`: the GENERIC single-`Creature` overload aimed at
    /// a loop variable over the move's targets — the player, not the mover.
    /// Reading the parameter type alone called this a self-buff.
    /// </summary>
    public async Task GenericPowerOnLoopTarget(IReadOnlyList<Creature> targets)
    {
        foreach (Creature target in targets)
            await PowerCmd.Apply<WeakPower>(new ThrowingPlayerChoiceContext(), target, 2m, base.Creature, null);
    }

    /// <summary>
    /// `TheObscura.WailMove`: the GENERIC collection overload aimed at the
    /// mover's OWN side. Reading the parameter type alone handed the player 3
    /// Strength every time this move ran.
    /// </summary>
    public async Task GenericPowerOnOwnSide(IReadOnlyList<Creature> targets) =>
        await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(),
            base.Creature.CombatState.GetTeammatesOf(base.Creature), 3m, base.Creature, null);

    /// <summary>
    /// `Rocket.TargetingReticleMove`: the same overload over `GetOpponentsOf`,
    /// which really is the player side and must stay one.
    /// </summary>
    public async Task GenericPowerOnOpponents(IReadOnlyList<Creature> targets) =>
        await PowerCmd.Apply<FrailPower>(new ThrowingPlayerChoiceContext(),
            base.CombatState.GetOpponentsOf(base.Creature), 1m, base.Creature, null);

    /// <summary>
    /// A self-buff after an own-side one: the flag must not survive its own
    /// apply, or every later effect in the move would inherit the side.
    /// </summary>
    public async Task OwnSideThenSelf(IReadOnlyList<Creature> targets)
    {
        await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(),
            base.Creature.CombatState.GetTeammatesOf(base.Creature), 3m, base.Creature, null);
        await PowerCmd.Apply<IntangiblePower>(new ThrowingPlayerChoiceContext(),
            base.Creature, 2m, base.Creature, null);
    }

    /// <summary>
    /// `OwlMagistrate.VerdictMove`: an attack, a debuff, and then the removal of
    /// the buff its own Judicial Flight granted three turns earlier.
    /// </summary>
    public async Task AttackDebuffThenRemoveOwnBuff(IReadOnlyList<Creature> targets)
    {
        await DamageCmd.Attack(33).FromMonster(this).Execute(null);
        await PowerCmd.Apply<FrailPower>(new ThrowingPlayerChoiceContext(), targets, 4m, base.Creature, null);
        await PowerCmd.Remove<SoarPower>(base.Creature);
    }

    /// <summary>
    /// A removal aimed at each of the move's targets: the player side, read by
    /// the same rule an apply's target is.
    /// </summary>
    public async Task RemoveFromEachTarget(IReadOnlyList<Creature> targets)
    {
        foreach (Creature target in targets)
            await PowerCmd.Remove<SoarPower>(target);
    }

    /// <summary>
    /// A removal aimed at somebody the scan cannot place must say so rather than
    /// be credited to the mover, exactly as a misplaced apply is.
    /// </summary>
    public async Task RemoveFromAFilteredAlly(IReadOnlyList<Creature> targets)
    {
        List<Creature> bots = base.Creature.CombatState.Enemies
            .Where((Creature c) => c.Monster is Fabricator).ToList();
        foreach (Creature bot in bots)
            await PowerCmd.Remove<SoarPower>(bot);
    }

    /// <summary>
    /// `ToughEgg.HatchMove` / `TestSubject`: two removals in one body, both self.
    /// </summary>
    public async Task RemoveTwoOwnPowers(IReadOnlyList<Creature> targets)
    {
        await PowerCmd.Remove<SoarPower>(base.Creature);
        await PowerCmd.Remove<DampenPower>(base.Creature);
    }

    /// <summary>
    /// A loop over a filtered list of the mover's OWN side, applying to each. The
    /// creature is a loop variable exactly as the Spectral Knight's is, but the
    /// collection is not `targets`, so it must stay an honest unknown.
    /// </summary>
    public async Task GenericPowerOnFilteredOwnSide(IReadOnlyList<Creature> targets)
    {
        List<Creature> bots = base.Creature.CombatState.Enemies
            .Where((Creature c) => c.Monster is Fabricator).ToList();
        foreach (Creature bot in bots)
            await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(), bot, 2m, base.Creature, null);
    }

    /// <summary>
    /// A body that loops over its own side and THEN over its targets: the second
    /// loop must not inherit the first loop's collection.
    /// </summary>
    public async Task OwnSideLoopThenTargetLoop(IReadOnlyList<Creature> targets)
    {
        List<Creature> bots = base.Creature.CombatState.Enemies
            .Where((Creature c) => c.Monster is Fabricator).ToList();
        foreach (Creature bot in bots)
            await CreatureCmd.GainBlock(bot, 15m, ValueProp.Move, null);
        foreach (Creature target in targets)
            await PowerCmd.Apply<WeakPower>(new ThrowingPlayerChoiceContext(), target, 2m, base.Creature, null);
    }

    /// <summary>
    /// `Entomancer.SpitMove`: the applies differ by branch, and a linear scan
    /// records both sides. The export is their SUM, which is why the body has to
    /// declare that it branched.
    /// </summary>
    public async Task BranchedApplies(IReadOnlyList<Creature> targets)
    {
        PersonalHivePower hive = base.Creature.GetPower<PersonalHivePower>();
        if (hive.Amount < 3)
        {
            await PowerCmd.Apply<PersonalHivePower>(new ThrowingPlayerChoiceContext(), base.Creature,
                1m, base.Creature, null);
            await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(), base.Creature,
                1m, base.Creature, null);
        }
        else
        {
            await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(), base.Creature,
                2m, base.Creature, null);
        }
    }

    /// <summary>
    /// `LivingFog.BloatMove`: a loop that adds a creature, then an attack. The
    /// class name in the generic argument is the only part of a summon a static
    /// read can recover.
    /// </summary>
    public async Task SummonThenAttack(IReadOnlyList<Creature> targets)
    {
        for (int i = 0; i < 1; i++)
        {
            string slot = "second";
            if (!string.IsNullOrEmpty(slot))
                await CreatureCmd.Add<GasBomb>(base.CombatState, slot);
        }
        await DamageCmd.Attack(5).FromMonster(this).Execute(null);
    }

    /// <summary>
    /// `Fogmog.IllusionMove`: the slot is a string LITERAL, and the sound and
    /// animation names before it must not be mistaken for one.
    /// </summary>
    public async Task SummonAtALiteralSlot(IReadOnlyList<Creature> targets)
    {
        await CreatureCmd.TriggerAnim(base.Creature, "Summon", 0.75f);
        await CreatureCmd.Add<GasBomb>(base.CombatState, "illusion");
    }

    /// <summary>
    /// `Fabricator.FabricateMove`: the non-generic overload, whose model is
    /// picked at runtime. Nothing static names the creature, so it must export
    /// no summon at all rather than a wrong one.
    /// </summary>
    public async Task SummonAModelChosenAtRuntime(IReadOnlyList<Creature> targets)
    {
        MegaCrit.Sts2.Core.Models.MonsterModel picked = new GasBomb();
        await CreatureCmd.Add(picked, base.CombatState, 0, "second");
    }

    // --- Damage calcs, as `SingleAttackIntent` receives them ---

    /// <summary>`TheForgotten.DreadDamage`: base plus the attacker's own Dexterity.</summary>
    private int DreadDamage => 13 + base.Creature.GetPowerAmount<DexterityPower>();

    /// <summary>`WaterfallGiant.CurrentPressureGunDamage`: a plain mutable counter.</summary>
    private int CurrentPressureGunDamage => 9;

    /// <summary>`SingleAttackIntent(() =&gt; DreadDamage)`.</summary>
    public Func<decimal> DamageScaledByOwnDexterity() => () => DreadDamage;

    /// <summary>`SingleAttackIntent(() =&gt; CurrentPressureGunDamage)`: no power read.</summary>
    public Func<decimal> DamageFromAMutableCounter() => () => CurrentPressureGunDamage;

    /// <summary>`SingleAttackIntent(int)`, which wraps a captured constant.</summary>
    public Func<decimal> DamageFromAConstant(int damage) => () => damage;

    /// <summary>`Aeonglass`: the insert count is itself a stat getter.</summary>
    public async Task GetterCountInsert(IReadOnlyList<Creature> targets) =>
        await CardPileCmd.AddToCombatAndPreview<Beckon>(targets, PileType.Discard, WitherAmount, null);
}

public class CombatStateStub
{
    public CardModel CreateCard<T>(Player? player) where T : CardModel, new() => new T();

    /// <summary>The player side, which is what `Rocket.TargetingReticleMove` buffs.</summary>
    public IReadOnlyList<Creature> GetOpponentsOf(Creature creature) => Array.Empty<Creature>();
}
