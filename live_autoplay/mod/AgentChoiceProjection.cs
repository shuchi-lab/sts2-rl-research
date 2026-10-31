using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private static readonly HashSet<string> AgentChoiceAutomaticKinds = new()
    { "reward_gold", "reward_potion", "reward_relic", "reward_open_cards", "reward_leave",
        "treasure_open", "treasure_relic", "treasure_leave" };

    internal static AgentDecision BuildAgentStrategy(RunState run, Player player)
    {
        if (!AgentEventReady(run)) return new AgentDecision { Phase = "Waiting" };
        var result = new AgentDecision { Owner = "strategy" };
        try
        {
            AgentChoiceEnsureRun(run);
            if (_agentChoiceHookError != null) throw new InvalidOperationException(_agentChoiceHookError);
            if (!_agentChoiceHistoryComplete) throw new InvalidOperationException("unsupported:requires_fresh_observed_run");
            if (!AgentDeploymentRun.Allowed(player.Character.Id.Entry, run.AscensionLevel, run.Players.Count, run.Modifiers.Count, IsMultiplayerRun()))
                throw new InvalidOperationException(AgentDeploymentRun.Unsupported);
            var screen = new Dictionary<string, object?>();
            var overlay = NOverlayStack.Instance?.Peek();
            if (overlay is NCardGridSelectionScreen or NChooseACardSelectionScreen)
                AgentChoiceSelectionScreen(result, (Control)overlay, player, screen);
            else if (overlay is NChooseABundleSelectionScreen bundle) AgentChoiceBundle(result, bundle, screen);
            else if (!IsMapScreenOpenOrVisible() && overlay is NCardRewardSelectionScreen reward)
                AgentChoiceCardReward(result, reward, screen);
            else if (!IsMapScreenOpenOrVisible() && overlay is NRewardsScreen rewards)
                AgentChoiceRewards(result, rewards, run, player, screen);
            else if (!IsMapScreenOpenOrVisible() && overlay is NCrystalSphereScreen sphere)
                AgentChoiceSphere(result, sphere, run, screen);
            else if (IsMapScreenOpenOrVisible()) AgentChoiceMap(result, run);
            else if (overlay != null)
                throw new InvalidOperationException("unsupported:strategy_overlay:" + overlay.GetType().Name);
            else if (run.CurrentRoom is MerchantRoom merchant) AgentChoiceShop(result, merchant.GetLocalInventory(), player, screen);
            else if (run.CurrentRoom is RestSiteRoom rest) AgentChoiceRest(result, rest, player, screen);
            else if (run.CurrentRoom is EventRoom ev) AgentChoiceEvent(result, ev, run, player, screen);
            else if (run.CurrentRoom is TreasureRoom treasure) AgentChoiceTreasure(result, treasure, player, screen);
            else if (CombatManager.Instance.IsInProgress) { result.Owner = "battle"; result.Phase = "Combat"; return result; }
            else { result.Owner = "none"; result.Phase = "Waiting"; return result; }
            if (result.Owner == "battle" || result.Owner == "none") return result;
            if (result.Candidates.Count >= 2 && result.Phase is not ("Shop" or "FakeMerchant" or "RelicReward" or "CardReward" or "Treasure"))
                AgentChoiceOutsidePotions(result, player);
            if (result.Candidates.Count == 0)
                throw new InvalidOperationException("unsupported:no_supported_candidate:" + result.Phase);
            if (result.Candidates.Count == 1 || result.Candidates.Any(c => AgentChoiceAutomaticKinds.Contains(c.Kind)))
                result.Owner = "automatic";
            result.Payload = AgentChoicePayload(result, run, player, screen);
            result.Ready = true;
        }
        catch (Exception e)
        {
            result.Ready = false; result.Unsupported.Add(e.Message);
        }
        return result;
    }

    internal static Dictionary<string, object?> AgentChoiceDictionary(params (string Key, object? Value)[] fields)
        => fields.ToDictionary(x => x.Key, x => x.Value);

    private static Dictionary<string, object?> AgentChoiceCard(CardModel card)
    {
        int enchant = card.Enchantment == null ? 0 : AgentIds.Id("enchantments", card.Enchantment.Id.Entry);
        if (card.CurrentUpgradeLevel > 1) throw new InvalidOperationException("unsupported:card_upgrade_level:" + card.Id.Entry);
        return AgentChoiceSemantics.CardRow(AgentIds.Id("cards", card.Id.Entry), AgentObjectIds.Card(card.DeckVersion ?? card),
            card.IsUpgraded, enchant, card.Enchantment?.Amount ?? 0,
            card is MadScience mad ? mad.TinkerTimeType.ToString() : "None",
            card is MadScience mad2 ? mad2.TinkerTimeRider.ToString() : "None", card is Guilty g ? g.CombatsSeen : 0);
    }

    private static int AgentChoiceRelicCounter(RelicModel relic)
    {
        string nativeType = relic.GetType().Name;
        if (BattleRelicCounterRules.IsKnown(nativeType)
            && BattleRelicCounterRules.TryRule(nativeType, relic.Id.Entry, AgentIds.Id("relics", relic.Id.Entry), out var rule)
            && !rule!.RunPersistent) return 0;
        // Combat-local frozen latches are not carried back; native may keep the latch
        // (Permafrost until the next CombatRoom, Lizard Tail for the run), so Choice reads 0.
        if (BattleRelicCounterRules.IsCombatLocalLatch(nativeType)) return 0;
        if (BattleRelicCounterRules.TryChoiceRunZero(nativeType, relic.Id.Entry, AgentIds.Id("relics", relic.Id.Entry))) return 0;
        return TryAgentMechanicalRelicCounter(relic, out int counter) ? counter : relic.DisplayAmount;
    }

    // Frozen UsedUpRelics is populated only by CollectUsedUpRelics for Lizard Tail.
    // Other native IsUsedUp flags (for example Silken Tress) describe UI exhaustion;
    // their frozen lifecycle is represented by their own Counter instead.
    private static bool AgentChoiceRelicUsedUp(RelicModel relic) =>
        relic is MegaCrit.Sts2.Core.Models.Relics.LizardTail && relic.IsUsedUp;

    private static Dictionary<string, object?> AgentChoicePayload(AgentDecision decision, RunState run, Player player,
        Dictionary<string, object?> screen)
    {
        if (player.MaxPotionCount > 6) throw new InvalidOperationException("unsupported:potion_capacity_above_six");
        var deck = PileType.Deck.GetPile(player).Cards.Select(AgentChoiceCard).ToList();
        var relics = player.Relics.Select(r => AgentChoiceDictionary(("def_id", AgentIds.Id("relics", r.Id.Entry)),
            ("counter", AgentChoiceRelicCounter(r)), ("used_up", AgentChoiceRelicUsedUp(r)), ("wax", r.IsWax))).ToList();
        var potions = player.PotionSlots.Take(player.MaxPotionCount).Select((p, i) => AgentChoiceDictionary(
            ("slot", i), ("potion_id", p == null ? 0 : AgentIds.Id("potions", p.Id.Entry)))).ToList();
        if (potions.Count != player.MaxPotionCount) throw new InvalidOperationException("unsupported:incomplete_potion_slots");
        int wins = AgentChoiceFights.Count(f => (string)f["outcome"]! == "victory");
        var visits = new List<Dictionary<string, object?>>(); int floor = 0;
        for (int act = 0; act < run.MapPointHistory.Count; act++)
        {
            int pointIndex = 0;
            foreach (var point in run.MapPointHistory[act])
            {
                floor++;
                int nodeType = AgentChoiceSemantics.NodeType(point.MapPointType.ToString());
                bool counted = AgentChoiceSemantics.CountedMapVisit(act + 1,nodeType,pointIndex++ == 0);
                if (!counted) continue;
                visits.Add(AgentChoiceDictionary(("act_ordinal", act + 1), ("floor", floor),
                    ("node_type", nodeType)));
            }
        }
        return AgentChoiceDictionary(("schema", "rs1/public_run_observation/v2"), ("run_id", "live"),
            ("decision_id", "pending"), ("phase", decision.Phase),
            ("scalars", AgentChoiceDictionary(("hp", player.Creature.CurrentHp), ("max_hp", player.Creature.MaxHp),
                ("gold", player.Gold), ("floor", run.TotalFloor), ("act_ordinal", run.CurrentActIndex + 1),
                ("act_region", AgentChoiceSemantics.ActRegion(run.Act.Id.Entry)), ("ascension", run.AscensionLevel),
                ("potion_capacity", player.MaxPotionCount), ("fights_won", wins), ("strategic_decisions", AgentStrategicDecisions))),
            ("deck", deck), ("relics", relics), ("potions", potions), ("map", AgentChoicePublicMap(run, decision)),
            ("history", AgentChoiceDictionary(("visited", visits), ("fights", AgentChoiceFights.ToArray()))),
            ("screen", screen), ("candidates", decision.Candidates.Select(c => AgentChoiceDictionary(
                ("id", c.Id), ("kind", c.Kind), ("public", c.Public))).ToList()));
    }

    private static Dictionary<string, object?> AgentChoicePublicMap(RunState run, AgentDecision decision)
    {
        var map = run.Map;
        var points = new[] { map.StartingMapPoint, map.BossMapPoint }
            .Concat(map.GetAllMapPoints()).Distinct().OrderBy(n => n.coord.row).ThenBy(n => n.coord.col).ToList();
        if (map.SecondBossMapPoint != null) throw new InvalidOperationException("unsupported:second_boss_map");
        var indices = points.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var options = decision.Phase == "Map" ? FindAll<NMapPoint>(NMapScreen.Instance ?? throw new InvalidOperationException("map UI unavailable"))
            .Where(p => p.State == MapPointState.Travelable && p.Point != null).OrderBy(p => p.Point!.coord.col)
            .Select((p, i) => (p.Point!, i)).ToDictionary(x => x.Item1, x => x.i) : new Dictionary<MapPoint, int>();
        var nodes = points.Select(p => AgentChoiceDictionary(("col", p.coord.col), ("row", p.coord.row),
            ("node_type", AgentChoiceSemantics.NodeType(p.PointType.ToString())),
            ("visited", run.VisitedMapCoords.Contains(p.coord) && AgentChoiceSemantics.CountedMapVisit(run.CurrentActIndex + 1,
                AgentChoiceSemantics.NodeType(p.PointType.ToString()), ReferenceEquals(p,map.StartingMapPoint))),
            ("current", run.CurrentMapCoord == p.coord),
            ("option", options.GetValueOrDefault(p, -1)), ("fur_coat", p.Quests.Any(q => q is FurCoat)),
            ("quest", p.Quests.Any(q => q is SpoilsMap)))).ToList();
        var edges = points.SelectMany(p => p.Children.Select(c => new[] { indices[p], indices[c] })).ToList();
        return AgentChoiceDictionary(("act_ordinal", run.CurrentActIndex + 1),
            ("act_region", AgentChoiceSemantics.ActRegion(run.Act.Id.Entry)),
            ("boss_encounter_id", AgentIds.Id("encounters", run.Act.BossEncounter.Id.Entry)),
            ("current", run.CurrentMapPoint == null ? -1 : indices[run.CurrentMapPoint]),
            ("awaiting_act_start", run.CurrentMapCoord == null), ("nodes", nodes), ("edges", edges));
    }

    private static void AgentChoiceMap(AgentDecision result, RunState run)
    {
        result.Phase = "Map";
        var map = NMapScreen.Instance ?? throw new InvalidOperationException("map UI unavailable");
        if (map.IsTraveling || !map.IsTravelEnabled) { result.Owner = "none"; return; }
        foreach (var point in FindAll<NMapPoint>(map).Where(p => p.State == MapPointState.Travelable && p.Point != null)
                     .OrderBy(p => p.Point!.coord.col))
        {
            var p = point.Point!;
            AgentChoiceAdd(result, $"map:c{p.coord.col}r{p.coord.row}", "map_node", p.PointType.ToString(),
                AgentChoiceDictionary(("col", p.coord.col), ("row", p.coord.row),
                    ("node_type", AgentChoiceSemantics.NodeType(p.PointType.ToString()))),
                AgentChoiceDictionary(("action", "choose_map_node"), ("index", result.Candidates.Count)), point,
                () => { map.OnMapPointSelectedLocally(point); return AgentChoiceOk(); });
        }
    }

    private static AgentCandidate AgentChoiceAdd(AgentDecision result, string id, string kind, string label,
        Dictionary<string, object?> pub, Dictionary<string, object?> action, Control? anchor = null,
        Func<Dictionary<string, object?>>? execute = null)
    {
        var c = new AgentCandidate { Id = id, Kind = kind, Label = label, Public = pub, Action = action,
            Anchor = anchor, Execute = execute };
        result.Candidates.Add(c); return c;
    }
    private static Dictionary<string, object?> AgentChoiceOk() => AgentChoiceDictionary(("status", "ok"));
    private static object AgentChoiceField(object o, string name)
    {
        for (var type = o.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(o) ?? throw new InvalidOperationException("unsupported:null_native_field:" + name);
            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null) return prop.GetValue(o) ?? throw new InvalidOperationException("unsupported:null_native_property:" + name);
        }
        throw new InvalidOperationException("unsupported:missing_native_field:" + o.GetType().Name + "." + name);
    }

    private static void AgentChoiceOutsidePotions(AgentDecision result, Player player)
    {
        if (!player.CanUseOrRemovePotions) return;
        if (result.Phase == "Event" && player.RunState.CurrentRoom is EventRoom ev
            && ev.CanonicalEvent.Id.Entry is "RANWID_THE_ELDER" or "STONE_OF_ALL_TIME" or "THE_FUTURE_OF_POTIONS") return;
        for (int i = 0; i < player.PotionSlots.Count; i++)
        {
            var p = player.PotionSlots[i];
            if (p == null || p.Usage != PotionUsage.AnyTime || !p.PassesCustomUsabilityCheck || p.IsQueued) continue;
            int slot = i;
            AgentChoiceAdd(result, $"potion:use:{slot}", "potion_use", p.Id.Entry,
                AgentChoiceDictionary(("slot", slot), ("potion_id", AgentIds.Id("potions", p.Id.Entry))),
                AgentChoiceDictionary(("action", "use_potion"), ("slot", slot)), AgentChoicePotionAnchor(p));
        }
    }
}
