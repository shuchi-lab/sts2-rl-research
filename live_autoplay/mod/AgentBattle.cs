using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    internal static AgentDecision BuildAgentBattle(RunState run, Player player)
    {
        var answer = new AgentDecision { Owner = "battle", Phase = "Waiting" };
        var combat = player.Creature.CombatState as CombatState;
        if (combat == null || !CombatManager.Instance.IsInProgress) return answer;
        try
        {
            BattleEnsureLedger(combat, player);
            if (_battleHookErrors.Count > 0) throw new BattleProjectionUnsupported(string.Join("; ", _battleHookErrors));
            if (!string.Equals(player.Character.Id.Entry, "IRONCLAD", StringComparison.Ordinal))
                throw new BattleProjectionUnsupported("Battle checkpoint supports Ironclad only");
            if (!_battleFreshCombat) throw new BattleProjectionUnsupported("Combat was attached after entry; public history/knowledge ledger unavailable");
            var overlay = NOverlayStack.Instance?.Peek();
            var handUi = NCombatRoom.Instance?.Ui?.Hand;
            bool selectionOpen = _battleSelection is { Task.IsCompleted: false } &&
                (overlay is NCardGridSelectionScreen or NChooseACardSelectionScreen || handUi?.IsInCardSelection == true);
            var queueState = AgentCurrentQueueState(selectionOpen);
            if (queueState is AgentQueueState.Faulted or AgentQueueState.Cancelled)
                throw new BattleProjectionUnsupported("Native action queue did not finish successfully: " + queueState);
            if (queueState != AgentQueueState.Ready) return answer;
            if(selectionOpen && overlay is NChooseACardSelectionScreen choosing &&
                !BattleChooseCardTiming.Ready(Time.GetTicksMsec(),(ulong)BattleRead(choosing,"_openedTicks")!)) return answer;
            if (!selectionOpen && (!IsPlayPhase(player) || CombatManager.Instance.PlayerActionsDisabled || handUi?.InCardPlay == true)) return answer;
            if (_battleShuffleTasks.Any(t => !t.IsCompleted)) return answer;
            var enemies = combat.Enemies.ToArray();
            BattleRunningRoster.RequireComplete(combat.EscapedCreatures.Select(e => e.Monster?.GetType().Name),
                enemies.Any(e => e.IsAlive), combat.CurrentSide == CombatSide.Player, enemies.Any(combat.EscapedCreatures.Contains));
            var starts = CombatManager.Instance.History.CardPlaysStarted.Where(e => e.Actor == player.Creature).ToArray();
            var finishes = CombatManager.Instance.History.CardPlaysFinished.Where(e => e.Actor == player.Creature).ToArray();
            var finishedPlays = finishes.Select(e => e.CardPlay).ToHashSet();
            var pending = starts.Where(e => !finishedPlays.Contains(e.CardPlay)).Select(e => e.CardPlay).ToArray();
            if (!selectionOpen && pending.Length != 0) return answer;
            var nested = Array.Empty<CardPlay>();
            if (pending.Any(p => p.IsAutoPlay))
            {
                var chain = pending.Select(p => (p.Card.GetType().Name, p.IsAutoPlay)).ToArray();
                if (BattleNestedAutoSelection.Classify(chain) == BattleNestedAutoSelection.Shape.ImmediateFrozenPick)
                {
                    // Frozen resolves these at once (AutoPlayNested); nothing to encode.
                    BattleNestedAutoContinuation(player, pending, answer);
                    answer.Ready = answer.Candidates.Count == 1;
                    return answer;
                }
                // Frozen Havoc park: the selection is the policy's, raised by the innermost child.
                if (!ReferenceEquals(_battleSelection?.Source, pending[^1].Card))
                    throw new BattleProjectionUnsupported("Parked nested selection was not raised by the innermost automatic play");
                nested = BattleNestedAutoSelection.ParkedChildrenInnermostFirst(pending);
            }
            if (pending.Length - nested.Length > 1) throw new BattleProjectionUnsupported("Multiple pending chosen card plays");
            var parked = pending.FirstOrDefault();
            var pc = player.PlayerCombatState ?? throw new BattleProjectionUnsupported("Missing PlayerCombatState");
            var cardRows = pc.AllCards.ToDictionary(c => AgentObjectIds.Card(c), c => BattleCardRow(c, combat));
            var knowledge = _battleDraw.Project(pc.DrawPile.Cards.Select(AgentObjectIds.Card));
            double[][] Rows(IEnumerable<CardModel> cs) => cs.Select(c => BattleCardRow(c, combat)).ToArray();
            double[][] Known(int[] ids) => ids.Select(i => cardRows[i]).ToArray();
            var unknownRows = knowledge.Unknown.Select(i => cardRows[i]).ToArray();
            Array.Sort(unknownRows, BattleCompareCardRows);
            var historyEvents = BattleHistoryEntries(combat, player, BattleNestedAutoSelection.BlockExcludedPlays(pending)).ToArray();
            var aux = BattleEffectAux(player, enemies);
            var buffs = BattlePowerRows(player.Creature);
            var startDebuffs = player.Creature.Powers.Where(p => p.Type.ToString() == "Debuff" && !p.SkipNextDurationTick)
                .Select(p => new[] {AgentIds.Id("buffs", p.Id.Entry), p.AmountOnTurnStart}).ToArray();
            var enemyRaw = new List<Dictionary<string, object?>>();
            var enemyViews = new List<Dictionary<string, object?>>();
            foreach (var e in enemies)
            {
                var (extra, stolen) = BattleEnemyExtras(e, player, enemies, parked, nested, combat);
                int defId = AgentIds.Id("enemies", e.Monster?.Id.Entry ?? throw new BattleProjectionUnsupported("Enemy without model"));
                enemyRaw.Add(new() { ["def_id"] = defId, ["hp"] = e.CurrentHp, ["max_hp"] = e.MaxHp,
                    ["block"] = e.Block, ["buffs"] = BattlePowerRows(e), ["hardened_shell_hp_lost_this_side"] = BattleHardenedShell(e),
                    ["extra"] = extra, ["stolen_card"] = stolen });
                enemyViews.Add(new() { ["def_id"] = defId, ["targetable"] = e.IsHittable,
                    ["declared_intents"] = BattleIntents(e, combat) });
            }
            _battleSelectionToHand = Array.Empty<CardModel>();
            _battleSelectionDiscarded = Array.Empty<CardModel>();
            var selection = selectionOpen ? BattleSelectionProjection(player, combat, answer) : null;
            if (answer.Owner == "automatic") { answer.Ready = answer.Candidates.Count > 0; return answer; }
            if (!selectionOpen) BattleCombatCandidates(player, combat, enemies, answer);
            var legal = answer.Candidates.Select(c => c.Public).ToArray();
            int[] potions = player.PotionSlots.Select(p => p == null ? 0 : AgentIds.Id("potions", p.Id.Entry)).ToArray();
            if (potions.Length != player.MaxPotionCount) throw new BattleProjectionUnsupported("Potion slot array/capacity differ");
            var selectedToHand = _battleSelectionToHand;
            var (viewHand, viewDiscard) = _battleSelectionDiscarded.Length > 0
                ? BattleSelectionFlow.DiscardView(pc.Hand.Cards,pc.DiscardPile.Cards,_battleSelectionDiscarded)
                : BattleSelectionFlow.ReturnView(pc.Hand.Cards,pc.DiscardPile.Cards,selectedToHand);
            var selectedIds = selectedToHand.Select(AgentObjectIds.Card).ToHashSet();
            var handViews = viewHand.Select(c => BattleHandView(c, combat)).ToArray();
            var observation = new Dictionary<string, object?> {
                ["turn"] = _battleTurn, ["round_number"] = combat.RoundNumber, ["player_turn"] = combat.CurrentSide == CombatSide.Player,
                ["phase"] = selectionOpen ? "selection" : "combat", ["character"] = "Ironclad",
                ["player"] = new Dictionary<string, object?> {
                    ["hp"] = player.Creature.CurrentHp, ["max_hp"] = player.Creature.MaxHp, ["block"] = player.Creature.Block,
                    ["energy"] = pc.Energy, ["max_energy"] = pc.MaxEnergy, ["energy_capacity"] = pc.MaxEnergy,
                    ["gold"] = player.Gold, ["buffs"] = buffs, ["debuffs_at_round_start"] = startDebuffs,
                    ["history"] = BattlePublicHistory.Project(historyEvents), ["effect_aux"] = aux,
                    ["resources"] = BattleResources(player), ["counters"] = BattleCounters(run, player, combat, historyEvents, parked, nested, selectionOpen)
                },
                ["hand"] = Rows(viewHand), ["draw_known_top"] = Known(knowledge.Top.Where(i=>!selectedIds.Contains(i)).ToArray()),
                ["draw_known_bottom"] = Known(knowledge.Bottom.Where(i=>!selectedIds.Contains(i)).ToArray()),
                ["draw_unknown_sorted"] = selectedIds.Count==0 ? unknownRows : knowledge.Unknown
                    .Where(i=>!selectedIds.Contains(i)).Select(i=>cardRows[i]).OrderBy(r=>r,Comparer<double[]>.Create(BattleCompareCardRows)).ToArray(),
                ["discard"] = _battleSelectionDiscarded.Length > 0
                    ? BattleSelectionFlow.StagedDiscardRows(Rows(viewDiscard), pc.DiscardPile.Cards.Count) : Rows(viewDiscard), ["exhaust"] = Rows(pc.ExhaustPile.Cards),
                ["incoming"] = BattleIncoming(player), ["relics"] = BattleRelicRows(player), ["potions"] = potions,
                ["enemies"] = enemyRaw, ["view"] = new Dictionary<string, object?> { ["hand"] = handViews, ["enemies"] = enemyViews },
                ["selection"] = selection, ["legal"] = legal, ["allow_potions"] = true,
                ["potion_capacity"] = player.MaxPotionCount, ["potion_slot_array"] = potions.Length,
                ["excluded_potion_options"] = selectionOpen ? 0 : player.PotionSlots.Count(p => p?.Usage == PotionUsage.Automatic),
                ["autoplay_queue"] = Array.Empty<double[]>(), ["parked_play_card"] = parked == null ? Array.Empty<double[]>() : new[] {BattleCardRow(parked.Card, combat)},
                ["parked_nested_autoplays"] = nested.Select(p => BattleCardRow(p.Card, combat)).ToArray(), ["parked_batch_autoplay"] = Array.Empty<double[]>(),
                ["batch_staged"] = Array.Empty<double[]>(), ["history_course_encore_pending"] = BattleHistoryCoursePending(player, combat),
                ["boundary_violations"] = Array.Empty<string>()
            };
            answer.Payload = new() {
                ["run_id"] = _agentRunId, ["fight_id"] = _battleFightSerial.ToString(),
                ["entry"] = new Dictionary<string, object?> { ["act_ordinal"] = run.CurrentActIndex + 1,
                    ["room"] = BattleRoom(run), ["entry_hp"] = _battleEntryHp, ["entry_max_hp"] = _battleEntryMaxHp,
                    ["fight_id"] = _battleFightSerial.ToString(), ["fight_n"] = _battleFightSerial, ["floor"] = run.TotalFloor },
                ["raw"] = new Dictionary<string, object?> { ["status"] = "running", ["decision"] = _battleDecision,
                    ["entry_hp"] = _battleEntryHp, ["entry_max_hp"] = _battleEntryMaxHp, ["steps"] = _battleSteps, ["obs"] = observation, ["options"] = legal }
            };
            _battleLastPayload = answer.Payload;
            answer.Phase = selectionOpen ? "Selection" : "Combat";
            answer.Ready = answer.Candidates.Count > 0;
        }
        catch (Exception ex)
        {
            answer.Ready = false;
            answer.Phase = "Unsupported";
            answer.Unsupported.Add(ex.GetBaseException().Message);
            answer.Candidates.Clear(); answer.Payload.Clear();
        }
        return answer;
    }

    private static int BattleCompareCardRows(double[] a, double[] b)
    {
        for (int i = 0; i < a.Length; i++) { int c = a[i].CompareTo(b[i]); if (c != 0) return c; }
        return 0;
    }
    private static string BattleRoom(RunState run) => run.BaseRoom is EventRoom ? "event" : run.CurrentRoom is CombatRoom cr
        ? cr.RoomType.ToString().ToLowerInvariant() switch { "elite" => "elite", "boss" => "boss", _ => "normal" } : "normal";
    private static object? BattleRead(object target, string name)
    {
        for (Type? t = target.GetType(); t != null; t = t.BaseType)
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (p != null) return p.GetValue(target);
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (f != null) return f.GetValue(target);
        }
        throw new BattleProjectionUnsupported("Required public semantic member missing: " + target.GetType().Name + "." + name);
    }
    private static object BattleData(PowerModel power) => BattleRead(power, "_internalData") ?? throw new BattleProjectionUnsupported("Missing effect data " + power.Id.Entry);
    private static double BattleNumber(object target, string name) => Convert.ToDouble(BattleRead(target, name));
    private static PowerModel? BattlePower(Creature creature, string type) =>
        BattleInstancedPowers.Select(creature.Powers.Where(p => p.GetType().Name == type),
            p => p.InstanceType == MegaCrit.Sts2.Core.Entities.Powers.PowerInstanceType.Instanced);
    private static RelicModel? BattleRelic(Player player, string type) => player.Relics.SingleOrDefault(r => r.GetType().Name == type);
    private static object? BattleRelicState(Player player, string type, string member) => BattleRelic(player, type) is { } r ? BattleRead(r, member) : null;
    private static double BattleRelicNumber(Player player, string type, string member) => Convert.ToDouble(BattleRelicState(player, type, member));

    private static void BattleCombatCandidates(Player player, CombatState combat, Creature[] enemies, AgentDecision result)
    {
        var hand = player.PlayerCombatState!.Hand.Cards;
        int boundDecision=_battleDecision;
        bool revivalOnly = !enemies.Any(e => e.IsHittable) && AgentBattleCertifiedTestSubjectRevivalOnly(enemies);
        for (int i = 0; i < hand.Count; i++)
        {
            var card = hand[i];
            if (!card.CanPlay(out _, out _)) continue;
            int[] targets = card.TargetType == TargetType.AnyEnemy ? Enumerable.Range(0, enemies.Length).Where(j => enemies[j].IsHittable).ToArray() : new[] {-1};
            if (targets.Length == 0)
            {
                // The frozen worker has a simulator null-target fallback here.
                // Native AnyEnemy cannot bind it during this certified revival.
                if (revivalOnly) continue;
                throw new BattleProjectionUnsupported("Playable targeted card has no native target; automatic target semantics unverified: " + card.Id.Entry);
            }
            if (card.TargetType is TargetType.AnyAlly or TargetType.AnyPlayer)
                throw new BattleProjectionUnsupported("Foreign player/ally target type " + card.TargetType);
            foreach (int t in targets)
            {
                var target = t < 0 ? null : enemies[t];
                var row = new Dictionary<string, object?> { ["o"] = result.Candidates.Count, ["kind"] = "play_card", ["action"] = i,
                    ["target"] = t, ["hand_index"] = i, ["target_type"] = card.TargetType.ToString() };
                var holder = NCombatRoom.Instance?.Ui.Hand.ActiveHolders.FirstOrDefault(h => h.CardModel == card);
                int capturedSlot = i;
                string candidateId=BattleProjectionNames.OptionId("play_card",i,t,null);
                result.Candidates.Add(new AgentCandidate { Id = candidateId, Kind = "play_card",
                    Label = BattleDisplayLabels.Card(card.Title,i,target?.Monster?.Title.GetFormattedText(),t), Public = row, Anchor = holder,
                    Action = new() { ["action"] = "play_card", ["card_handle"] = AgentObjectIds.Card(card), ["target_combat_id"] = target?.CombatId },
                    Execute = () => {
                        if (!player.PlayerCombatState!.Hand.Cards.Contains(card) || !card.CanPlay(out _, out _)) return Error("Bound card is no longer playable");
                        if (target != null && !target.IsHittable) return Error("Bound target is no longer targetable");
                        return BattleExecuteLogical(candidateId,boundDecision,()=> {
                            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
                            return new() { ["status"] = "ok" };
                        });
                    } });
            }
        }
        int end = hand.Count;
        string endId=BattleProjectionNames.OptionId("end_turn",end,-1,null);
        result.Candidates.Add(new AgentCandidate { Id = endId, Kind = "end_turn", Label = "End turn",
            Public = new() { ["o"] = result.Candidates.Count, ["kind"] = "end_turn", ["action"] = end, ["target"] = -1 },
            Action = new() { ["action"] = "end_turn" }, Execute = () => BattleExecuteLogical(endId,boundDecision,()=>ExecuteEndTurn(player)) });
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            var potion = player.PotionSlots[slot]; if (potion == null || potion.Usage == PotionUsage.Automatic) continue;
            int pid = AgentIds.Id("potions", potion.Id.Entry);
            int[] targets = potion.TargetType == TargetType.AnyEnemy ? Enumerable.Range(0,enemies.Length).Where(j => enemies[j].IsHittable).ToArray() : new[] {-1};
            if (targets.Length == 0)
            {
                if (revivalOnly) continue;
                throw new BattleProjectionUnsupported("Targeted potion has no native target; automatic target semantics unverified: " + potion.Id.Entry);
            }
            foreach (int t in targets)
            {
                var target = t < 0 ? null : enemies[t]; int capturedSlot = slot;
                int action = hand.Count + 1 + slot;
                var row = new Dictionary<string,object?> { ["o"] = result.Candidates.Count, ["kind"] = "use_potion", ["action"] = action,
                    ["target"] = t, ["potion_slot"] = slot, ["potion_id"] = pid };
                string candidateId=BattleProjectionNames.OptionId("use_potion",action,t,slot);
                result.Candidates.Add(new AgentCandidate { Id = candidateId, Kind = "use_potion",
                    Label = BattleDisplayLabels.Potion(potion.Title.GetFormattedText(),slot,target?.Monster?.Title.GetFormattedText(),t), Public = row,
                    Anchor=AgentChoicePotionAnchor(potion),
                    Action = new() { ["action"] = "use_potion", ["slot"] = slot, ["target_combat_id"] = target?.CombatId }, Execute = () => {
                        if (player.PotionSlots[capturedSlot] != potion) return Error("Bound potion changed");
                        var data = new Dictionary<string,JsonElement> { ["slot"] = JsonSerializer.SerializeToElement(capturedSlot) };
                        if (target != null) data["target"] = JsonSerializer.SerializeToElement(target.CombatId!.Value.ToString());
                        return BattleExecuteLogical(candidateId,boundDecision,()=>ExecuteUsePotion(player,data));
                    } });
            }
        }
    }
}
