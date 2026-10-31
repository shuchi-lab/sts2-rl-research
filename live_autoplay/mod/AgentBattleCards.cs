using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.ValueProps;

namespace STS2_MCP;

public static partial class McpMod
{
    private static readonly Dictionary<Creature,bool> _battleSecondaryObserved=new();
    private static bool BattleHasSecondary(Creature enemy)
    {
        var monster=enemy.Monster ?? throw new BattleProjectionUnsupported("Enemy has no model");
        string type=monster.GetType().Name;
        string move=monster.NextMove?.Id ?? throw new BattleProjectionUnsupported("Enemy has no public move");
        if(move=="STUNNED" && BattleNativeIntentMap.NeedsSecondaryHistory(type))
            return _battleSecondaryObserved.TryGetValue(enemy,out bool prior) ? prior :
                throw new BattleProjectionUnsupported("Stunned enemy has no previously observed secondary-intent context: "+type);
        bool result=BattleNativeIntentMap.HasSecondary(type,move);
        _battleSecondaryObserved[enemy]=result;
        return result;
    }
    private static double[] BattleCardRow(CardModel card, CombatState combat)
    {
        if (card.CurrentUpgradeLevel > 1) throw new BattleProjectionUnsupported("Multiple upgrade levels: " + card.Id.Entry);
        if (card.IsDupe) throw new BattleProjectionUnsupported("History Course duplicate card identity is not encoded");
        var canonical = ModelDb.GetById<CardModel>(card.Id).ToMutable();
        if (card.IsUpgraded) canonical.UpgradeInternal();
        var (costs, _) = BattleCardCosts(card, combat);
        double Delta(string key) => card.DynamicVars.TryGetValue(key,out var a) && canonical.DynamicVars.TryGetValue(key,out var b)
            ? (double)(a.BaseValue - b.BaseValue) : 0;
        string? encName = card.Enchantment?.GetType().Name;
        int enchantment = card.Enchantment == null ? 0 : AgentIds.Id("enchantments", card.Enchantment.Id.Entry);
        int momentum = encName == "Momentum" ? Convert.ToInt32(BattleRead(card.Enchantment!, "_extraDamage")) : 0;
        bool dampened = BattlePower(card.Owner.Creature,"DampenPower") is { } dampen &&
            ((IDictionary)BattleRead(BattleData(dampen),"downgradedCardsToOldUpgradeLevels")!).Contains(card);
        bool turnPlayed = CombatManager.Instance.History.CardPlaysFinished.Any(e => e.CardPlay.Card == card && BattleThisTurn(e,combat));
        // Frozen per-copy flags describe local mutations; global powers such as Hex are encoded separately.
        bool alteredSly = card.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Sly) && !canonical.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Sly);
        bool alteredRetain = card.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Retain) && !canonical.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Retain);
        bool alteredEthereal = card.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Ethereal) && !canonical.GetKeywordsWithSources(KeywordSources.Local).Contains(CardKeyword.Ethereal);
        int tinkerType = card.GetType().Name == "MadScience" ? Convert.ToInt32(BattleRead(card,"TinkerTimeType")) : 6;
        int tinkerRider = card.GetType().Name == "MadScience" ? Convert.ToInt32(BattleRead(card,"TinkerTimeRider")) : 0;
        bool spent = card.Enchantment?.Status.ToString() == "Disabled";
        return new double[] {
            AgentIds.Id("cards",card.Id.Entry), card.IsUpgraded ? 1 : 0, costs.FreeThisTurn ? 1 : 0, 0,
            enchantment, card.Enchantment?.Amount ?? 0, costs.CostBump, card.BaseReplayCount, spent ? 1 : 0,
            costs.CostForCombat, Delta("Damage"), momentum, tinkerType, tinkerRider, turnPlayed ? 1 : 0,
            Convert.ToBoolean(BattleRead(card,"_hasSingleTurnSly")) ? 1 : 0, alteredSly ? 1 : 0,
            alteredEthereal ? 1 : 0, alteredRetain ? 1 : 0, dampened ? 1 : 0,
            card.Affliction?.GetType().Name == "Bound" ? 1 : 0,
            Convert.ToBoolean(BattleRead(card,"_hasSingleTurnRetain")) ? 1 : 0,
            costs.CostThisTurn, costs.FreeUntilPlayed ? 1 : 0, Delta("Block"), costs.Override,
            costs.OverrideBaseline, costs.OverrideSetUpgraded ? 1 : 0
        };
    }

    private static (BattleLocalCosts Costs, BattleStompCostContext? Stomp) BattleCardCosts(CardModel card, CombatState combat)
    {
        var mods = ((IEnumerable)BattleRead(card.EnergyCost, "_localModifiers")!).Cast<LocalCostModifier>()
            .Select(m => new BattleLocalModifier(m.Amount, m.Type.ToString(), (int)m.Expiration, m.IsReduceOnly)).ToArray();
        var over = _battleOverrides.GetValueOrDefault(card);
        if (mods.Any(m => m.Type == "Absolute" && m.Expiration == 6 && !m.ReduceOnly) && over == null)
            throw new BattleProjectionUnsupported("Cost override predates live ledger: " + card.Id.Entry);
        BattleStompCostContext? stomp = null;
        if (card.GetType() == typeof(MegaCrit.Sts2.Core.Models.Cards.Stomp) &&
            (card.Pile?.IsCombatPile == true || mods.Any(m => m.Type == "Relative" && m.Expiration == 2)))
        {
            int attacks = CombatManager.Instance.History.CardPlaysFinished.Count(e =>
                e.CardPlay.Card.Type == CardType.Attack && e.CardPlay.Player == card.Owner && BattleThisTurn(e, combat));
            stomp = new("Stomp", card.Id.Entry, AgentIds.Id("cards", card.Id.Entry), attacks,
                !card.IsCanonical && card.Owner.PlayerCombatState != null && ReferenceEquals(card.Owner.Creature.CombatState, combat))
                { CertifiedParkedOwnedAttacks = BattleStompCertifiedParkedAttack(card, combat) };
        }
        var costs = BattleLocalCosts.Project(mods, card.IsUpgraded, over?.Baseline ?? 0, over?.Upgraded,
            over?.FromFreeSetter ?? false, stomp);
        return (costs, stomp);
    }

    // Installed v0.111 CardModel.OnPlayWrapper runs Hook.BeforeCardPlayed (Stomp's echo)
    // before CardPlayStarted, and CardPlayFinished only after OnPlay, for chosen and
    // autoplayed plays alike. The selected frozen engine suspends any play whose effect
    // leaves a PendingSelection before AfterCardPlayEffect counts the Attack, for the
    // chosen root and for a Havoc child. So a started, unfinished owned Attack that is
    // waiting on its own reviewed combat-pile selector has echoed once and is uncounted.
    private static int BattleStompCertifiedParkedAttack(CardModel stomp, CombatState combat)
    {
        var trace = _battleSelection;
        if (trace is not { Task.IsCompleted: false } || trace.Source is not CardModel source ||
            source.Type != CardType.Attack || trace.Method != "FromCombatPile" ||
            source.Id.Entry != ModelDb.GetEntry(source.GetType()) ||
            !BattleSelectionSources.TryGetValue(source.GetType().Name, out string? kind) ||
            BattleStompParkedPile(kind) is not PileType parkedPile ||
            !ReferenceEquals(trace.Player, stomp.Owner) || !ReferenceEquals(source.Owner, stomp.Owner) ||
            trace.Pile == null || trace.Pile.Type != parkedPile ||
            !ReferenceEquals(trace.Pile, parkedPile.GetPile(stomp.Owner)) ||
            trace.Prefs is not { } prefs || prefs.MinSelect < 0 || prefs.MaxSelect < Math.Max(1, prefs.MinSelect) ||
            NOverlayStack.Instance?.Peek() is not NCombatPileCardSelectScreen screen) return 0;
        BattleGridCards(screen, trace, stomp.Owner, out _, out _);
        var history = CombatManager.Instance.History;
        var finished = history.CardPlaysFinished.Select(e => e.CardPlay).ToHashSet();
        var pending = history.CardPlaysStarted.Where(e => e.Actor == stomp.Owner.Creature && BattleThisTurn(e, combat) &&
            !finished.Contains(e.CardPlay)).Select(e => e.CardPlay).ToArray();
        // The chosen source alone, or a Havoc chain whose innermost autoplayed child raised it.
        if (pending.Length == 0 || pending[0].IsAutoPlay || !ReferenceEquals(pending[^1].Card, source) ||
            pending.Any(p => !ReferenceEquals(p.Player, stomp.Owner)) ||
            pending.Skip(1).Any(p => !p.IsAutoPlay) || pending.Count(p => p.Card.Type == CardType.Attack) != 1 ||
            (pending.Length > 1 && BattleNestedAutoSelection.Classify(pending.Select(p => (p.Card.GetType().Name, p.IsAutoPlay)).ToArray())
                != BattleNestedAutoSelection.Shape.ParkedForPlayer)) return 0;
        var action = AgentNativeChoiceAction(trace.ChoiceContext) as PlayCardAction;
        if (action == null || !ReferenceEquals(action.Player, stomp.Owner) ||
            !ReferenceEquals(action, RunManager.Instance.ActionExecutor?.CurrentlyRunningAction) ||
            action.State.ToString() != "GatheringPlayerChoice" ||
            !ReferenceEquals(action.NetCombatCard.ToCardModel(), pending[0].Card) ||
            AgentCurrentQueueState(true) != AgentQueueState.Ready ||
            !ReferenceEquals(source.Owner.Creature.CombatState, combat)) return 0;
        if (stomp.IsClone) throw new BattleProjectionUnsupported("Stomp clone at parked Attack selector is not certified");
        return 1;
    }

    // Pile of each reviewed combat-pile selection kind; other kinds are not certified here.
    private static PileType? BattleStompParkedPile(string kind) => kind switch
    {
        "DiscardToDrawPileTop" or "DiscardToHand" => PileType.Discard,
        "DrawPileToHand" => PileType.Draw,
        _ => null
    };

    private static BattleStompCostRules BattleStompCollectCostRules(CardModel card, CombatState combat)
    {
        bool freeAttack = false;
        int tangled = 0;
        foreach (var model in ((ICombatState)combat).IterateHookListeners())
            foreach (string method in new[] { "TryModifyEnergyCostInCombat", "TryModifyEnergyCostInCombatLate" })
            {
                var hook = model.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public);
                if (hook?.DeclaringType == typeof(AbstractModel)) continue;
                // These exact encoded powers share source-proven type-specific
                // Late hooks. Skill/Power variants never apply to a Stomp Attack.
                if (method == "TryModifyEnergyCostInCombatLate" &&
                    ((model.GetType() == typeof(MegaCrit.Sts2.Core.Models.Powers.FreeSkillPower) &&
                        model.Id.Entry == "FREE_SKILL_POWER" && AgentIds.Id("buffs", model.Id.Entry) == 152) ||
                     (model.GetType() == typeof(MegaCrit.Sts2.Core.Models.Powers.FreePowerPower) &&
                        model.Id.Entry == "FREE_POWER_POWER" && AgentIds.Id("buffs", model.Id.Entry) == 170)))
                {
                    if (card.Type != CardType.Attack) throw new BattleProjectionUnsupported("Stomp cost rule has a non-Attack card");
                    continue;
                }
                // Installed v0.111 hooks whose first predicate is the card's
                // Power (SpikedGauntlets, Curious) or Skill (Corruption) type.
                if ((method == "TryModifyEnergyCostInCombat" &&
                     ((model.GetType() == typeof(MegaCrit.Sts2.Core.Models.Relics.SpikedGauntlets) && model.Id.Entry == "SPIKED_GAUNTLETS") ||
                      (model.GetType() == typeof(MegaCrit.Sts2.Core.Models.Powers.CuriousPower) && model.Id.Entry == "CURIOUS_POWER"))) ||
                    (method == "TryModifyEnergyCostInCombatLate" &&
                     model.GetType() == typeof(MegaCrit.Sts2.Core.Models.Powers.CorruptionPower) && model.Id.Entry == "CORRUPTION_POWER"))
                {
                    if (card.Type != CardType.Attack) throw new BattleProjectionUnsupported("Stomp type-only cost rule has a non-Attack card");
                    continue;
                }
                if (method == "TryModifyEnergyCostInCombatLate" &&
                    model.GetType() == typeof(MegaCrit.Sts2.Core.Models.Powers.FreeAttackPower) &&
                    model.Id.Entry == "FREE_ATTACK_POWER" && AgentIds.Id("buffs", model.Id.Entry) == 151)
                {
                    var power = (PowerModel)model;
                    // Native same-creature ownership excludes foreign stacks;
                    // only player buffs enter the frozen player cost rule.
                    if (!ReferenceEquals(power.Owner, card.Owner.Creature))
                    {
                        if (card.Owner.Creature.Powers.Contains(power))
                            throw new BattleProjectionUnsupported("Foreign Free Attack hook is attached to the local encoded power list");
                        continue;
                    }
                    var ownedStacks = card.Owner.Creature.Powers.Where(p => p.GetType() == typeof(MegaCrit.Sts2.Core.Models.Powers.FreeAttackPower)).ToArray();
                    if (ownedStacks.Length != 1 || !ReferenceEquals(ownedStacks[0], power))
                        throw new BattleProjectionUnsupported("Free Attack hook lacks one matching encoded local power stack");
                    if (card.Type != CardType.Attack || card.Pile?.Type is not (PileType.Hand or PileType.Play) || power.Amount <= 0)
                        throw new BattleProjectionUnsupported("Free Attack cost rule lacks positive owned Attack/Hand-or-Play certificate");
                    freeAttack = true;
                    continue;
                }
                if (method == "TryModifyEnergyCostInCombat" &&
                    model.GetType() == typeof(MegaCrit.Sts2.Core.Models.Powers.TangledPower) &&
                    model.Id.Entry == "TANGLED_POWER" && AgentIds.Id("buffs", model.Id.Entry) == 133)
                {
                    var power = (PowerModel)model;
                    tangled += BattleLocalCosts.StompTangledSurcharge(ReferenceEquals(power.Owner, card.Owner.Creature),
                        card.Owner.Creature.Powers.Contains(power),
                        card.Owner.Creature.Powers.Count(p => p.GetType() == typeof(MegaCrit.Sts2.Core.Models.Powers.TangledPower)),
                        card.Type == CardType.Attack, card.Affliction is MegaCrit.Sts2.Core.Models.Afflictions.Entangled, power.Amount);
                    continue;
                }
                throw new BattleProjectionUnsupported("Stomp frozen hand view has an uncertified global cost hook: " + model.Id.Entry + "/" + method);
            }
        return new(freeAttack, tangled);
    }

    private static Dictionary<string,object?> BattleHandView(CardModel card, CombatState combat)
    {
        int cost = card.EnergyCost.GetAmountToSpend();
        bool playable = card.CanPlay(out var reason, out _);
        // Input view only: native playability and spend stay untouched.
        if (BattleFrozenSentinelViewCost(card, combat) is int frozenView) cost = frozenView;
        if (card.GetType() == typeof(MegaCrit.Sts2.Core.Models.Cards.Stomp))
        {
            var (costs, context) = BattleCardCosts(card, combat);
            if (context == null) throw new BattleProjectionUnsupported("Stomp hand view has no active combat cost certificate");
            var rules = BattleStompCollectCostRules(card, combat);
            bool ember = card.Enchantment?.GetType().Name == "TezcatarasEmber";
            int nativeExpected = costs.StompFrozenEffectiveCost(context.CompletedOwnedAttacks + costs.StompObservedPendingEcho, ember, rules.FreeAttack, rules.Tangled);
            if (cost != Math.Max(0, nativeExpected))
                throw new BattleProjectionUnsupported("Stomp native hand price differs from certified local cost formula");
            cost = costs.StompFrozenEffectiveCost(context.CompletedOwnedAttacks, ember, rules.FreeAttack, rules.Tangled);
            playable = BattleLocalCosts.StompViewPlayable((int)reason, cost, card.Owner.PlayerCombatState!.Energy);
        }
        return new() { ["def_id"] = AgentIds.Id("cards", card.Id.Entry), ["effective_cost"] = cost, ["playable"] = playable };
    }

    private static BattleDecimillipedeView BattleDecimillipedePublicView(Creature creature)
    {
        var monster=creature.Monster;
        var move=monster?.NextMove;
        return BattleDecimillipedePublic.Project(monster?.GetType().Name ?? "",
            Convert.ToDouble(creature.CurrentHp),creature.IsHittable,Convert.ToDouble(creature.Block),
            creature.Powers.Select(p=>new BattleDecimillipedePower(p.GetType().Name,p.Amount)).ToArray(),
            move?.Id,move?.Intents.Select(i=>i.IntentType.ToString()).ToArray() ?? Array.Empty<string>());
    }

    // Frozen view cost of a -1 sentinel or X card; null for ordinary cards. Local
    // fields are the BattleCardCosts projection the raw card row already uses; each
    // installed cost listener is asked its own pure owner/pile/counter predicate.
    private static int? BattleFrozenSentinelViewCost(CardModel card, CombatState combat)
    {
        if (!BattleFrozenViewCost.Applies(card.EnergyCost.CostsX, card.EnergyCost.GetWithModifiers(CostModifiers.All))) return null;
        bool zero = false;
        int borrowed = 0, tangled = 0;
        foreach (var model in ((ICombatState)combat).IterateHookListeners())
            foreach (string method in new[] { "TryModifyEnergyCostInCombat", "TryModifyEnergyCostInCombatLate" })
            {
                if (model.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public)?.DeclaringType == typeof(AbstractModel)) continue;
                string name = model.GetType().Name;
                var gate = BattleFrozenViewCost.HookCardTypeGate(name);
                if (model.GetType().Namespace is not ("MegaCrit.Sts2.Core.Models.Powers" or "MegaCrit.Sts2.Core.Models.Relics") ||
                    !(method.EndsWith("Late") ? BattleFrozenViewCost.IsZeroHook(name) : BattleFrozenViewCost.IsAdditiveHook(name)))
                    throw new BattleProjectionUnsupported("Sentinel/X hand view has an unknown global cost hook: " + model.Id.Entry + "/" + method);
                // Frozen gates these by card type alone; another type is a frozen no-op.
                if (gate != null && gate != card.Type.ToString()) continue;
                if (name == "TangledPower")
                {
                    var power = (PowerModel)model;
                    tangled += BattleLocalCosts.StompTangledSurcharge(ReferenceEquals(power.Owner, card.Owner.Creature),
                        card.Owner.Creature.Powers.Contains(power),
                        card.Owner.Creature.Powers.Count(p => p.GetType() == typeof(MegaCrit.Sts2.Core.Models.Powers.TangledPower)),
                        true, card.Affliction is MegaCrit.Sts2.Core.Models.Afflictions.Entangled, power.Amount);
                    continue;
                }
                if (method.EndsWith("Late"))
                {
                    if (model.TryModifyEnergyCostInCombatLate(card, 1m, out decimal late))
                    {
                        if (late != 0m) throw new BattleProjectionUnsupported("Late cost hook did not zero: " + name);
                        zero = true;
                    }
                    continue;
                }
                // Census: no sentinel/X card is a Power, so Spiked Gauntlets/Curious never reach here.
                if (gate == "Power") throw new BattleProjectionUnsupported("Power sentinel/X card is outside the installed census");
                // Borrowed Time adds its Amount for the owner's cards, independent of price.
                if (model.TryModifyEnergyCostInCombat(card, 1m, out decimal added)) borrowed += (int)(added - 1m);
            }
        var (costs, _) = BattleCardCosts(card, combat);
        int printed = (int)typeof(CardModel).GetProperty("CanonicalEnergyCost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(card)!;
        return BattleFrozenViewCost.Project(card.EnergyCost.CostsX, printed, card.EnergyCost.GetWithModifiers(CostModifiers.All),
            card.Type.ToString(), card.Enchantment?.GetType().Name == "TezcatarasEmber", costs,
            new BattleFrozenGlobalCost(zero, BorrowedTime: borrowed, Tangled: tangled));
    }

    private static BattleTestSubjectView BattleTestSubjectPublicView(Creature creature)
    {
        var monster=creature.Monster;
        if(monster==null || (monster.GetType().Name!="TestSubject" && monster.Id.Entry!="TEST_SUBJECT")) return new(0);
        if(monster.GetType()!=typeof(MegaCrit.Sts2.Core.Models.Monsters.TestSubject) || monster.Id.Entry!="TEST_SUBJECT")
            throw new BattleProjectionUnsupported("Test Subject is not the certified sealed native model");
        var move=monster.NextMove;
        return BattleTestSubjectPublic.Project(monster.GetType().Name,monster.Id.Entry,AgentIds.Id("enemies",monster.Id.Entry),
            Convert.ToDouble(creature.CurrentHp),Convert.ToDouble(creature.MaxHp),creature.IsHittable,Convert.ToDouble(creature.Block),
            creature.Powers.Select(p=>new BattleTestSubjectPower(p.GetType().Name,p.Id.Entry,AgentIds.Id("buffs",p.Id.Entry),p.Amount)).ToArray(),
            move?.Id,move?.Intents.Select(i=>i.IntentType.ToString()).ToArray() ?? Array.Empty<string>(),
            (creature.CombatState as CombatState)?.RunState.AscensionLevel ??
                throw new BattleProjectionUnsupported("Test Subject has no current combat ascension"));
    }

    internal static bool AgentBattleCertifiedTestSubjectRevivalOnly(Creature[] enemies) =>
        enemies.Length==1 && BattleTestSubjectPublicView(enemies[0]).RevivingAmount==1;

    private static List<int[]> BattlePowerRows(Creature creature)
    {
        var revival=BattleDecimillipedePublicView(creature);
        var subject=BattleTestSubjectPublicView(creature);
        var synthetic = BattleSyntheticPowerLedger.Current;
        var nextMove = creature.Monster?.NextMove;
        var placed = new List<(object Power,List<int[]> Rows)>();
        var born = new List<(int[] Row,BattleSyntheticBirth Birth)>();
        foreach (var p in creature.Powers)
        {
            var own = new List<int[]>();
            placed.Add((p,own));
            string type = p.GetType().Name;
            int amount = p.Amount;
            if (type == "SurroundedPower") amount = Convert.ToInt32(BattleRead(p,"Facing")) + 1;
            own.Add(new[] {AgentIds.Id("buffs",p.Id.Entry), amount});
            string? helper = type switch { "InfernoPower" => "InfernoSelfDamage", "CrimsonMantlePower" => "CrimsonMantleSelfDamage", "TheBombPower" => "TheBombDamage", _ => null };
            if (helper != null)
            {
                string key = type == "TheBombPower" ? "Damage" : "SelfDamage";
                own.Add(new[] {AgentIds.Id("buffs",helper), p.DynamicVars[key].IntValue});
            }
            if (type == "PanachePower") own.Add(new[] {AgentIds.Id("buffs","PanacheArmed"),
                Convert.ToBoolean(BattleRead(BattleData(p),"alreadyApplied")) ? 1 : 0});
            if (type == "SkittishPower" && Convert.ToBoolean(BattleRead(p,"HasGainedBlockThisTurn")))
            {
                var spent = new[] {AgentIds.Id("buffs","SkittishSpent"),1};
                if (synthetic.Birth(creature,"SkittishSpent") is { } birth) born.Add((spent,birth));
                else own.Add(spent);
            }
        }
        if (BattleTerrorLedger.Current.Birth(creature,nextMove?.Id) is { } terror)
            born.Add((new[] {AgentIds.Id("buffs","TerrorQueued"),1},terror));
        var markers = BattleEnemyPowerMarkerRows(creature);
        // Both frozen rat factories create SummonCooldown2 before any later applied power.
        // It only decreases to removal; its birth is creature creation, not snapshot time.
        if (creature.Monster?.GetType().Name == "TwoTailedRat")
        {
            int cooldown = markers.FindIndex(r => r[0] == AgentIds.Id("buffs","SummonCooldown"));
            if (cooldown >= 0)
            {
                born.Add((markers[cooldown],new BattleSyntheticBirth(0,Array.Empty<object>(),null)));
                markers.RemoveAt(cooldown);
            }
        }
        // An observed stun's marker belongs at its birth while its STUNNED move is still current.
        if (nextMove != null && synthetic.Birth(creature,"Stunned") is { } stun && ReferenceEquals(stun.Move,nextMove))
        {
            int stunned = AgentIds.Id("buffs","Stunned"), at = markers.FindIndex(r => r[0] == stunned);
            if (at >= 0) { born.Add((markers[at],stun)); markers.RemoveAt(at); }
        }
        var rows = BattleSyntheticPowerLedger.Place(placed,born);
        rows.AddRange(markers);
        rows = BattlePowerFold.TemporaryCounters(rows);
        // Illusion is a shared native lifecycle (EyeWithTeeth, Parafright, ...).
        // Its public IsReviving flag is the frozen worker's synthetic Reviving1
        // state. It is not a distinct Heal magnitude or an enemy-name exception.
        if (creature.Powers.OfType<IllusionPower>().Any(p => p.IsReviving))
            rows.Add(new[] {AgentIds.Id("buffs","Reviving"),1});
        if(subject.RevivingAmount>0) return subject.AppendReviving(rows);
        return revival.RevivingAmount>0
            ? revival.AppendReviving(rows,AgentIds.Id("buffs","REATTACH_POWER"),AgentIds.Id("buffs","Reviving"))
            : rows;
    }

    // Installed v0.111.0 ChainsOfBindingPower.Data holds only boundCardPlayed. Its AfterCardDrawn
    // cap counts this turn's Bound afflictions by its owner in combat history, which is the frozen
    // CombatState.BoundCardsThisTurn (incremented per bind, reset at the player turn boundary).
    private static int BattleBoundCardsThisTurn(PowerModel chains) =>
        CombatManager.Instance.History.Entries.OfType<MegaCrit.Sts2.Core.Combat.History.Entries.CardAfflictedEntry>()
            .Count(e => e.HappenedThisTurn(chains.CombatState) && e.Actor == chains.Owner
                && e.Affliction is MegaCrit.Sts2.Core.Models.Afflictions.Bound);

    private static Dictionary<string,object?> BattleEffectAux(Player player, Creature[] enemies)
    {
        var pc = player.Creature;
        double Var(string type,string key) => BattlePower(pc,type) is { } p ? p.DynamicVars[key].IntValue : 0;
        double Field(string type,string key) => BattlePower(pc,type) is { } p ? BattleNumber(p,key) : 0;
        int Applier(string type) => BattlePower(pc,type)?.Applier is { } c ? Array.IndexOf(enemies,c) : -1;
        bool LivingApplier(string type) => BattlePower(pc,type)?.Applier is {IsAlive:true};
        var dampen = BattlePower(pc,"DampenPower");
        int downgraded = dampen == null ? 0 : ((IDictionary)BattleRead(BattleData(dampen),"downgradedCardsToOldUpgradeLevels")!).Count;
        return new() {
            ["toric_toughness_block"] = Var("ToricToughnessPower","Block"),
            ["sloth_cards_played_this_turn"] = Field("SlothPower","_cardsPlayedThisTurn"),
            ["tender_cards_played_this_turn"] = Field("TenderPower","_cardsPlayedThisTurn"),
            ["bound_cards_this_turn"] = BattlePower(pc,"ChainsOfBindingPower") is { } chains
                ? BattleBoundCardsThisTurn(chains) : 0,
            ["bound_card_played_this_turn"] = BattlePower(pc,"ChainsOfBindingPower") is { } chains2 && Convert.ToBoolean(BattleRead(BattleData(chains2),"boundCardPlayed")),
            ["cards_played_since_panache_proc"] = BattlePower(pc,"PanachePower") is { } panache ? 5 - panache.DynamicVars["CardsLeft"].IntValue : 0,
            ["drawn_cards_since_automation_proc"] = BattlePower(pc,"AutomationPower") is { } automation ? 10 - automation.DisplayAmount : 0,
            ["hex_applier"] = Applier("HexPower"), ["hex_applier_alive"] = LivingApplier("HexPower"),
            ["shrink_applier"] = Applier("ShrinkPower"), ["shrink_applier_alive"] = LivingApplier("ShrinkPower"),
            ["orbit_energy_spent"] = BattlePower(pc,"OrbitPower") is { } orbit ? BattleNumber(BattleData(orbit),"energySpent") : 0,
            ["void_form_cards_played_this_turn"] = BattlePower(pc,"VoidFormPower") is { } vf ? BattleNumber(BattleData(vf),"cardsPlayedThisTurn") : 0,
            ["dampen_caster_alive"] = dampen != null && enemies.Any(e => e.IsAlive && e.Monster?.GetType().Name == "MagiKnight"),
            ["dampened_cards"] = downgraded
        };
    }

    private static Dictionary<string,object?>[] BattleIntents(Creature enemy, CombatState combat)
    {
        // Exact shared rule in the selected Episode.WriteDerivedView: zero-HP
        // creatures remain in the roster, but contribute no declared intents.
        // Apply before reading their retained move, regardless of enemy identity.
        if (enemy.CurrentHp <= 0) return Array.Empty<Dictionary<string,object?>>();
        var intents = enemy.Monster?.NextMove?.Intents ?? throw new BattleProjectionUnsupported("Enemy has no announced move");
        return intents.Select((i,ordinal) => {
            string type = i.IntentType.ToString();
            if (!BattleProjectionSchema.Intents.Contains(type)) throw new BattleProjectionUnsupported("Unknown announced intent " + type);
            var row = new Dictionary<string,object?> { ["type"] = type,
                ["magnitude"] = i is AttackIntent ? 0 : i is StatusIntent status && ordinal == 0 ? status.CardCount :
                    BattleNativeIntentMap.Magnitude(enemy.Monster!.GetType().Name,type,ordinal,enemy.Monster.NextMove.Id,combat.RunState.AscensionLevel), ["hits"] = 1,
                ["announced_total_damage"] = null, ["announced_damage_per_hit"] = null };
            if (i is AttackIntent attack)
            {
                int single = attack.GetSingleDamage(combat.PlayerCreatures,enemy);
                int total = attack.GetTotalDamage(combat.PlayerCreatures,enemy);
                row["magnitude"] = (int)attack.DamageCalc!();
                row["hits"] = attack.Repeats == 0 ? 1 : attack.Repeats;
                row["announced_total_damage"] = total; row["announced_damage_per_hit"] = single;
            }
            return row;
        }).ToArray();
    }

    internal static bool TryAgentMechanicalRelicCounter(RelicModel relic,out int counter)
    {
        counter=0;
        string nativeType=relic.GetType().Name;
        if(nativeType=="RedSkull")
            return BattleRelicCounterRules.TryRedSkull(relic,relic.Id.Entry,AgentIds.Id("relics",relic.Id.Entry),out counter);
        if(BattleRelicCounterRules.IsLatch(nativeType))
            return BattleRelicCounterRules.TryLatch(relic,relic.Id.Entry,AgentIds.Id("relics",relic.Id.Entry),out counter);
        if(nativeType=="StoneCalendar")
        {
            if(relic.GetType()!=typeof(MegaCrit.Sts2.Core.Models.Relics.StoneCalendar))
                throw new BattleProjectionUnsupported("Stone Calendar native type is not the certified sealed model");
            bool inCombat=CombatManager.Instance.IsInProgress;
            var pc=!inCombat || relic.IsCanonical ? null : relic.Owner.PlayerCombatState;
            int damageTurn=relic.DynamicVars.TryGetValue("DamageTurn",out var threshold)?threshold.IntValue:0;
            return BattleRelicCounterRules.TryStoneCalendar(relic.Id.Entry,AgentIds.Id("relics",relic.Id.Entry),
                inCombat,relic.IsCanonical,pc!=null,damageTurn,pc?.TurnNumber??0,out counter);
        }
        if(!BattleRelicCounterRules.IsKnown(nativeType)) return false;
        if(!BattleRelicCounterRules.TryRule(nativeType,relic.Id.Entry,AgentIds.Id("relics",relic.Id.Entry),out var rule)) return false;
        // Source-certified getters contain only executed owned-card/turn progress,
        // with explicit reset boundaries. They do not contain RNG or future state.
        // Reading the exact getter preserves hook timing; CardPlayFinished is
        // recorded before AfterCardPlayed, so history alone can run ahead here.
        var getter=relic.GetType().GetProperty(rule!.Member,
            BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);
        if(getter==null || getter.PropertyType!=typeof(int) || getter.GetIndexParameters().Length!=0 ||
            getter.GetMethod==null || getter.GetMethod.IsStatic)
            throw new BattleProjectionUnsupported("Source-certified relic past-count getter is unavailable: "+nativeType+"."+rule.Member);
        int period=rule.PeriodVariable==null ? rule.Period : relic.DynamicVars.TryGetValue(rule.PeriodVariable,out var variable)
            ? variable.IntValue : throw new BattleProjectionUnsupported("Source-certified relic period is unavailable: "+nativeType);
        counter=BattleRelicCounterRules.Counter(rule,period,getter.GetValue(relic));
        return true;
    }
    internal static bool AgentMechanicalRelicCounterIncludesHiddenCounter(RelicModel relic) =>
        relic.GetType()==typeof(MegaCrit.Sts2.Core.Models.Relics.StoneCalendar) &&
        TryAgentMechanicalRelicCounter(relic,out _);
    private static object[] BattleRelicRows(Player player) => player.Relics.Select(r => (object)new[] {
        AgentIds.Id("relics",r.Id.Entry),TryAgentMechanicalRelicCounter(r,out int mechanical)?mechanical:r.ShowCounter?r.DisplayAmount:0
    }).ToArray();
    private static Dictionary<string,object?> BattleResources(Player player)
    {
        var pc = player.PlayerCombatState!;
        if (pc.OrbQueue != null && pc.OrbQueue.Capacity > 0) throw new BattleProjectionUnsupported("Orb public type/resource projection not yet certified for Ironclad foreign orb effects");
        // These native allies are the visual bodies of relic/card mechanics.
        // Their 9999 placeholder HP is not Osty's resource; Pael's cooldown and
        // affected play are already exported by BattleCounters.
        foreach (var pet in pc.Pets)
        {
            bool relicPet = pet.PetOwner == player && (pet.Monster switch {
                MegaCrit.Sts2.Core.Models.Monsters.PaelsLegion => BattleRelic(player,"PaelsLegion") is MegaCrit.Sts2.Core.Models.Relics.PaelsLegion,
                MegaCrit.Sts2.Core.Models.Monsters.Byrdpip => BattleRelic(player,"Byrdpip") is MegaCrit.Sts2.Core.Models.Relics.Byrdpip,
                _ => false
            });
            if (!relicPet) throw new BattleProjectionUnsupported("Pet resource projection unavailable: " + pet.Monster?.GetType().Name);
        }
        return new() { ["stars"] = pc.Stars, ["orb_capacity"] = 0, ["base_orb_slots"] = 0,
            ["osty_hp"] = 0, ["osty_max_hp"] = 0, ["orbs"] = Array.Empty<int[]>() };
    }
    // Frozen incoming is ReturnToHandBeforeDraw+CopiesToHandBeforeDraw. Rebound is
    // only a buff row: its redirected play lands on Draw/Top (BattleDrawKnowledge).
    private static double[][] BattleIncoming(Player player) => Array.Empty<double[]>();
    private static bool BattleHistoryCoursePending(Player player, CombatState combat) => BattleRelic(player,"HistoryCourse") != null &&
        CombatManager.Instance.History.CardPlaysFinished.Any(e => e.Actor == player.Creature && e.CardPlay.Card.Type == CardType.Attack && e.HappenedLastPlayerTurn(player));

    private static (double[], double[]?) BattleEnemyExtras(Creature enemy,Player player,Creature[] enemies,CardPlay? parked,CardPlay[] nested,CombatState combat)
    {
        double Possession(string type,string field) => BattlePower(enemy,type) is { } p
            ? ((IDictionary)BattleRead(p,field)!).Contains(player.Creature) ? Convert.ToDouble(((IDictionary)BattleRead(p,field)!)[player.Creature]) : 0 : 0;
        var facing = BattlePower(player.Creature,"SurroundedPower");
        bool back = facing != null && ((Convert.ToInt32(BattleRead(facing,"Facing")) == 0 && BattlePower(enemy,"BackAttackLeftPower") != null)
            || (Convert.ToInt32(BattleRead(facing,"Facing")) == 1 && BattlePower(enemy,"BackAttackRightPower") != null));
        var steam = BattleSteamEruption(enemy);
        var swipes=enemy.Powers.OfType<SwipePower>().ToArray();
        if(swipes.Any(p=>p.Owner!=enemy) || (swipes.Length>0 && enemy.Monster?.GetType().Name!="ThievingHopper"))
            throw new BattleProjectionUnsupported("Swipe is not owned by the certified Hopper creature");
        var stolen=_battleStolenCards.Project(player.Creature,swipes.Select(p=>
            new BattleSwipeView(p,p.Target,p.StolenCard?.Owner.Creature,p.StolenCard)).ToArray());
        int slot = BattleEnemySlot.Project(combat.Encounter?.GetType().Name,combat.Encounter?.Slots,enemy.SlotName);
        var strangle = BattlePower(enemy,"StranglePower");
        double strangleBefore = 0;
        if (parked != null && strangle != null)
        {
            var amounts = (IDictionary)BattleRead(BattleData(strangle),"amountsForPlayedCards")!;
            if (amounts.Contains(parked.Card)) strangleBefore = Convert.ToDouble(amounts[parked.Card]);
        }
        return (new double[] {
            BattlePower(enemy,"CurlUpPower") is { } curl && BattleRead(BattleData(curl),"playedCard") != null ? 1 : 0,
            Possession("PossessStrengthPower","StolenStrength"), Possession("PossessSpeedPower","StolenDexterity"),
            steam.Phase,steam.Damage, BattlePower(enemy,"NemesisPower") is { } nemesis && Convert.ToBoolean(BattleRead(nemesis,"_shouldApplyIntangible")) ? 1 : 0,
            back ? 1 : 0, BattlePower(player.Creature,"DampenPower") != null && enemy.IsAlive && enemy.Monster?.GetType().Name == "MagiKnight" ? 1 : 0,
            BattlePower(enemy,"ThieveryPower") is { } thievery ? thievery.DynamicVars.Gold.IntValue : 0,
            stolen!=null ? 1 : 0, combat.EscapedCreatures.Contains(enemy) ? 1 : 0,
            BattlePower(enemy,"OffBalancePower") != null ? 1 : 0,
            CombatManager.Instance.History.Entries.OfType<MegaCrit.Sts2.Core.Combat.History.Entries.DamageReceivedEntry>()
                .Count(e => e.Receiver == enemy && BattleThisTurn(e,combat) && e.Result.Props.IsPoweredAttack()),
            slot,strangleBefore, BattleHasSecondary(enemy) ? 1 : 0,
            parked?.Target == enemy ? 1 : 0,
            // is_parked_autoplay_target: a parked Havoc child's rolled target (its started play's).
            nested.Any(p => p.Target == enemy) ? 1 : 0
        },stolen);
    }
    private static (double Phase, double Damage) BattleSteamEruption(Creature enemy)
    {
        if (enemy.Monster is not MegaCrit.Sts2.Core.Models.Monsters.WaterfallGiant giant) return (0,0);
        // These getters record completed mechanical transitions. Death sets the
        // warning phase; its move snapshots the visible power and removes it.
        // Keep that stored damage for the explosion even after the power is gone.
        bool blowing = Convert.ToBoolean(BattleRead(giant,"IsAboutToBlow"));
        return (blowing ? BattlePower(enemy,"SteamEruptionPower") != null ? 1 : 2 : 0,
            Convert.ToInt32(BattleRead(giant,"SteamEruptionDamage")));
    }
    private static int BattleHardenedShell(Creature enemy) => BattlePower(enemy,"HardenedShellPower") is { } p ? Convert.ToInt32(BattleRead(BattleData(p),"damageReceivedThisTurn")) : 0;
}
