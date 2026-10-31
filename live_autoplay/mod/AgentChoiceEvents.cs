using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private static readonly Dictionary<(int Event, int Option), string[]> AgentEventGrants = new()
    {
        [(1,0)] = new[]{"POOR_SLEEP"}, [(7,0)] = new[]{"SPOILS_MAP"}, [(9,1)] = new[]{"GREED"},
        [(10,1)] = new[]{"BYRDONIS_EGG"}, [(13,0)] = new[]{"SPORE_MIND"}, [(17,1)] = new[]{"GUILTY"},
        [(23,0)] = new[]{"INJURY"}, [(27,1)] = new[]{"DEBT"}, [(39,1)] = new[]{"CLUMSY"},
        [(42,0)] = new[]{"ULTIMATE_STRIKE"}, [(42,1)] = new[]{"ULTIMATE_DEFEND"},
        [(43,0)] = new[]{"EXTERMINATE"}, [(43,1)] = new[]{"SQUASH"}, [(46,0)] = new[]{"NORMALITY"},
        [(48,0)] = new[]{"DECAY"}, [(49,0)] = new[]{"METAMORPHOSIS"}, [(53,0)] = new[]{"DECAY"}
    };

    private static void AgentChoiceEvent(AgentDecision result, EventRoom room, RunState run, Player player,
        Dictionary<string, object?> context)
    {
        var transition = AgentChoiceEventTransitionState(run, false, false);
        if (transition is AgentQueueState.Faulted or AgentQueueState.Cancelled)
            throw new InvalidOperationException("unsupported:native_event_transition_" + transition.ToString().ToLowerInvariant());
        if (transition != AgentQueueState.Ready)
        { result.Owner = "none"; result.Phase = "Waiting"; return; }
        if (AgentCurrentLocalEvent(run, room) is not { } model)
        { result.Owner = "none"; result.Phase = "Waiting"; return; }
        var ui = NEventRoom.Instance;
        if (model is TheArchitect || model.Id.Entry == "THE_ARCHITECT")
        {
            AgentChoiceArchitect(result, model, ui, context);
            return;
        }
        if (model is FakeMerchant fake)
        {
            if (fake.StartedFight)
            {
                result.Phase = "FakeMerchant";
                AgentChoiceAdd(result, "fake_merchant:leave", "shop_leave", "Proceed", new(), AgentChoiceDictionary(("action", "proceed"))); return;
            }
            AgentChoiceShop(result, fake.Inventory, player, context); return;
        }
        bool ancient = model is AncientEventModel;
        result.Phase = ancient ? "Ancient" : "Event";
        if (ancient) { context["ancient"] = model.Id.Entry; context["awaiting_proceed"] = model.IsFinished; }
        else context["event_id"] = model.IsFinished ? -1 : AgentIds.Id("events", model.Id.Entry);
        if (model.Description == null || ui == null) { result.Owner = "none"; return; }
        var buttons = FindAll<NEventOptionButton>(ui).ToList();
        if (ancient && buttons.Count == 0)
        {
            var data = BuildEventState(room, run);
            if (data["in_dialogue"] is true)
                AgentChoiceAdd(result, "ancient:dialogue", "proceed", "Continue dialogue", new(), AgentChoiceDictionary(("action", "advance_dialogue")));
            else result.Owner = "none";
            return;
        }
        // Entering/reloading an event builds and animates its buttons after the
        // model exists. No candidate yet is ordinary UI readiness, not an
        // unsupported event. Keep polling until every offered button is usable.
        if (buttons.Count == 0 || buttons.Any(button => !button.Option.IsLocked
            && !button.Option.WasChosen && (!button.IsVisibleInTree() || !button.IsEnabled)))
        { result.Owner = "none"; result.Phase = "Waiting"; return; }
        int eventId = ancient ? 0 : AgentIds.Id("events", model.Id.Entry);
        int page = ancient || model.IsFinished ? 0 : AgentChoiceEventPage(model);
        if (!ancient) context["page"] = page;
        for (int nativeIndex = 0; nativeIndex < buttons.Count; nativeIndex++)
        {
            var button = buttons[nativeIndex]; var option = button.Option;
            if (option.IsLocked || option.WasChosen) continue;
            if (option.IsProceed || model.IsFinished)
            {
                AgentChoiceAdd(result, "event:proceed", "proceed", "Proceed", new(),
                    AgentChoiceDictionary(("action", "choose_event_option"), ("index", nativeIndex)), button,
                    () => { button.ForceClick(); return AgentChoiceOk(); }); continue;
            }
            if (ancient)
            {
                var relic = option.Relic ?? throw new InvalidOperationException("unsupported:ancient_option_without_relic_identity");
                int rid = AgentIds.Id("relics", relic.Id.Entry);
                if (!AgentChoiceRelicAllowed(relic, player, true)) continue;
                AgentChoiceAdd(result, "ancient:relic:" + rid, "ancient_relic", relic.Id.Entry,
                    AgentChoiceDictionary(("relic_id", rid), ("slot", nativeIndex)),
                    AgentChoiceDictionary(("action", "choose_event_option"), ("index", nativeIndex)), button,
                    () => { button.ForceClick(); return AgentChoiceOk(); }); continue;
            }
            string leaf = option.TextKey.Contains(".options.") ? option.TextKey.Split(".options.").Last() : option.TextKey;
            int logical = eventId is 38 or 44 or 58 || eventId == 28 && page > 0 ? nativeIndex :
                eventId == 22 ? leaf is "OBSERVE_CHEF" or "LEAVE" ? 1 : 0 :
                AgentChoiceSemantics.EventOption(model.Id.Entry, leaf, page);
            if (AgentIds.EventDeclined(eventId, page, logical)) continue;
            var pub = AgentChoiceDictionary(("event_id", eventId), ("page", page), ("option", logical));
            AgentChoiceEventMeaning(model, option, eventId, page, logical, pub, player);
            if (page == 0 && AgentEventGrants.TryGetValue((eventId, logical), out var grants))
            {
                var ids = grants.Select(entry => AgentIds.Id("cards", entry)).ToArray();
                if (ids.Any(id => !AgentIds.CardSupported(id))) continue;
                pub["grants_cards"] = ids;
            }
            if (eventId == 44 && result.Candidates.Count > 0) continue; // source-compatible first-pool continuation
            AgentChoiceAdd(result, $"event:{eventId}:p{page}:o{logical}", eventId == 59 ? "proceed" : "event_option",
                option.TextKey, pub, AgentChoiceDictionary(("action", "choose_event_option"), ("index", nativeIndex)), button,
                () => { button.ForceClick(); return AgentChoiceOk(); });
        }
    }

    private static void AgentChoiceArchitect(AgentDecision result, EventModel model, NEventRoom? ui,
        Dictionary<string, object?> context)
    {
        result.Phase = "Event";
        if (model.Description == null || ui == null)
        { result.Owner = "none"; result.Phase = "Waiting"; return; }
        int page = AgentChoiceEventPage(model);
        context["event_id"] = 59; context["page"] = page;
        // OnRoomEnter clears the public options while the first line animates.
        // Never choose a stale UI button before the current option is displayed.
        var options = model.CurrentOptions;
        if (options.Count == 0)
        { result.Owner = "none"; result.Phase = "Waiting"; return; }
        if (options.Count != 1) throw new InvalidOperationException("unsupported:architect_current_option_shape");
        var option = options[0];
        if (option.IsLocked || option.WasChosen)
        { result.Owner = "none"; result.Phase = "Waiting"; return; }
        bool dialogue = page <= 1 && option.TextKey == "THE_ARCHITECT.dialogue." + page;
        bool final = page is 1 or 2 && option.TextKey == "PROCEED";
        if ((!dialogue && !final) || option.IsProceed)
            throw new InvalidOperationException("unsupported:architect_public_option_mismatch");
        var buttons = FindAll<NEventOptionButton>(ui).ToList();
        var bound = buttons.Where(b => ReferenceEquals(b.Option, option) && b.IsVisibleInTree()).ToList();
        if (bound.Count == 0)
        { result.Owner = "none"; result.Phase = "Waiting"; return; }
        if (bound.Count != 1) throw new InvalidOperationException("unsupported:architect_rendered_option_shape");
        var button = bound[0];
        if (!button.IsEnabled)
        { result.Owner = "none"; result.Phase = "Waiting"; return; }
        AgentChoiceAdd(result, $"event:59:p{page}:o0", "proceed", option.TextKey,
            AgentChoiceDictionary(("event_id", 59), ("page", page), ("option", 0)),
            AgentChoiceDictionary(("action", "choose_event_option"), ("index", buttons.IndexOf(button))), button,
            () => { button.ForceClick(); return AgentChoiceOk(); });
    }

    private static int AgentChoiceEventPage(EventModel model)
    {
        string key = model.Description!.LocEntryKey;
        if (model is TheArchitect || model.Id.Entry == "THE_ARCHITECT")
        {
            if (model.GetType() != typeof(TheArchitect) || model.Id.Entry != "THE_ARCHITECT"
                || AgentIds.Id("events", model.Id.Entry) != 59 || key != "PROCEED.description"
                || AgentChoiceField(model, "_currentLineIndex") is not int index || index < 0 || index > 2)
                throw new InvalidOperationException("unsupported:architect_public_page_identity");
            // This past displayed-line index is initialized0 and advances only
            // after a native dialogue choice. No future Dialogue/RNG is read.
            return index;
        }
        string page = AgentChoiceSemantics.EventPageName(model.Id.Entry, key,
            model.CurrentOptions.Select(option => option.TextKey));
        if (page == "INITIAL") return 0;
        return model.Id.Entry switch
        {
            "ABYSSAL_BATHS" => 1 + (int)AgentChoiceField(model, "_lingerCount"),
            "TABLET_OF_TRUTH" => (int)AgentChoiceField(model, "_decipherCount"),
            "COLOSSAL_FLOWER" => (int)AgentChoiceField(model, "_numberOfDigs"),
            "SLIPPERY_BRIDGE" => (int)AgentChoiceField(model, "_numberOfHoldOns"),
            "DOLL_ROOM" => page == "TAKE_SOME_TIME" ? 1 : page == "EXAMINE" ? 2 : throw new InvalidOperationException("unsupported:doll_page"),
            "TRIAL" => page switch { "MERCHANT" => 1, "NOBLE" => 2, "NONDESCRIPT" => 3, "REJECT" => 4,
                _ => throw new InvalidOperationException("unsupported:trial_page") },
            "TINKER_TIME" => page switch { "CHOOSE_CARD_TYPE" => 1, "CHOOSE_RIDER" => 2,
                _ => throw new InvalidOperationException("unsupported:tinker_page") },
            "DENSE_VEGETATION" or "PUNCH_OFF" or "THE_LANTERN_KEY" or "ROUND_TEA_PARTY" or "WAR_HISTORIAN_REPY" => 1,
            "ENDLESS_CONVEYOR" => (int)AgentChoiceField(model, "NumOfGrabs"),
            _ => throw new InvalidOperationException("unsupported:event_page:" + key)
        };
    }

    private static int AgentChoiceVar(EventModel model, string key) => model.DynamicVars[key].IntValue;

    private static void AgentChoiceEventMeaning(EventModel model, EventOption option, int eventId, int page, int index,
        Dictionary<string, object?> pub, Player player)
    {
        // Fixed public costs from the audited option semantics. Values with native displayed vars below override these.
        (int hp, int max) = (eventId, page, index) switch
        {
            (1,0,1) => (0,8), (4,0,0) => (18,0), (6,0,1) => (5,0), (7,0,1) => (8,0),
            (12,0,0) => (8,0), (15,0,1) => (7,0), (18,0,1) => (9,0), (21,0,1) => (0,13),
            (25,0,0) => (8,0), (28,0,1) => (5,0), (28,0,2) => (15,0), (33,0,1) => (14,0),
            (35,0,1) => (6,0), (39,0,0) => (6,0), (49,0,1) => (10,0), (56,1,0) => (11,0), _ => (0,0)
        };
        int gold = (eventId, index) switch
        {
            (22,0) => 40, (26,1) => 55, (26,2) => 99, (31,1) => 100, (37,0) => 50, (37,1) => 150,
            (41,0) => 100, (41,1) => 200, (41,2) => 300, (51,0) => 50, (51,1) => 125, (51,2) => 250, _ => 0
        };
        switch (eventId)
        {
            case 1 when index == 1: max = AgentChoiceVar(model,"MaxHpLoss"); break;
            case 4: if (index == 0) hp = AgentChoiceVar(model,"SoloHp"); pub["gold_gain"] = AgentChoiceVar(model,index == 0 ? "SoloGold" : "JoinForcesGold"); break;
            case 6 when index == 1: hp = AgentChoiceVar(model,"RipHpLoss"); break;
            case 7 when index == 1: hp = AgentChoiceVar(model,"Damage"); break;
            case 9: pub["gold_gain"] = AgentChoiceVar(model,index == 0 ? "SmallChestGold" : "LargeChestGold"); break;
            case 12 when page == 0 && index == 0: hp = AgentChoiceVar(model,"HpLoss"); break;
            case 13 when index == 1: gold = AgentChoiceVar(model,"Gold"); break;
            case 15 when index == 1: hp = AgentChoiceVar(model,"HpLoss"); pub["gold_gain"] = AgentChoiceVar(model,"Gold"); break;
            case 16 when index == 0: max = AgentChoiceVar(model,"DecipherMaxHpLoss"); break;
            case 18: if (index == 0) gold = AgentChoiceVar(model,"Gold"); else hp = AgentChoiceVar(model,"HpLoss"); break;
            case 20 when index == 0: hp = AgentChoiceVar(model,"Damage"); break;
            case 21 when index == 1: max = AgentChoiceVar(model,"HpLoss"); break;
            case 22 when index == 0:
                var dish = AgentChoiceField(model,"_currentDish");
                string dishId = (string)AgentChoiceField(dish,"id");
                pub["dish"] = dishId switch { "CAVIAR" => 1,"SPICY_SNAPPY" => 2,"JELLY_LIVER" => 3,
                    "FRIED_EEL" => 4,"SUSPICIOUS_CONDIMENT" => 5,"CLAM_ROLL" => 6,"GOLDEN_FYSH" => 7,"SEAPUNK_SALAD" => 8,
                    _ => throw new InvalidOperationException("unsupported:conveyor_dish:"+dishId) };
                gold = dishId == "GOLDEN_FYSH" ? 0 : AgentChoiceVar(model,"Gold"); break;
            case 25 when index == 0: hp = AgentChoiceVar(model,"HpLoss"); break;
            case 26 when index is 1 or 2: gold = AgentChoiceVar(model,index == 1 ? "Gold" : "PricklySpongeGold"); break;
            case 27 when index == 0: gold = AgentChoiceVar(model,"UncoverFutureCost"); break;
            case 28 when page == 0 && index is 1 or 2: hp = AgentChoiceVar(model,index == 1 ? "TakeTimeHpLoss" : "ExamineHpLoss"); break;
            case 28 when page > 0:
                var doll = option.HoverTips.Select(h => h.CanonicalModel).OfType<RelicModel>().Single();
                pub["relic_id"] = AgentIds.Id("relics",doll.Id.Entry); break;
            case 31:
                var displayed = option.HoverTips.Select(h => h.CanonicalModel).ToList();
                if (index == 0) pub["potion_id"] = AgentIds.Id("potions",displayed.OfType<PotionModel>().Single().Id.Entry);
                if (index == 2) pub["gives_relic_id"] = AgentIds.Id("relics",displayed.OfType<RelicModel>().Single().Id.Entry);
                if (index == 1) gold = AgentChoiceVar(model,"Gold");
                break;
            case 32:
                // Read only already-populated arrays whose two members are displayed by this option.
                var owned = ((IEnumerable)AgentChoiceField(model,"_ownedRelics")).Cast<RelicModel>().ToList();
                var incoming = ((IEnumerable)AgentChoiceField(model,"_newRelics")).Cast<RelicModel>().ToList();
                pub["gives_relic_id"] = AgentIds.Id("relics",owned[index].Id.Entry);
                pub["relic_id"] = AgentIds.Id("relics",incoming[index].Id.Entry); break;
            case 34:
                if (index == 0)
                {
                    var card = (CardModel)AgentChoiceField(model,"_randomCardToLose");
                    pub["card"] = AgentChoiceCard(card); pub["instance_id"] = AgentObjectIds.Card(card); pub["removes_card"] = true;
                }
                else hp = AgentChoiceVar(model,"HpLoss"); break;
            case 33 when index == 1: hp = AgentChoiceVar(model,"Damage"); break;
            case 35 when index == 1: hp = AgentChoiceVar(model,"PushHpLoss"); break;
            case 37 when index is 0 or 1: gold = AgentChoiceVar(model,index == 0 ? "BoneTeaCost" : "EmberTeaCost"); break;
            case 38:
                var potion = player.Potions.Take(3).ElementAt(index);
                var types = (IDictionary)AgentChoiceField(model,"_cardTypes");
                pub["potion_id"] = AgentIds.Id("potions",potion.Id.Entry);
                pub["card_type"] = AgentChoiceSemantics.CardType(types[potion]!.ToString()!); break;
            case 39 when index == 0: hp = AgentChoiceVar(model,"HpLoss"); pub["gold_gain"] = AgentChoiceVar(model,"Gold"); break;
            case 41:
                if (index < 3) gold = AgentChoiceVar(model,new[]{"BargainBinCost","FeaturedItemCost","MysteryBoxCost"}[index]);
                if (index == 1) pub["relic_id"] = AgentIds.Id("relics",option.HoverTips.Select(h => h.CanonicalModel).OfType<RelicModel>().Single().Id.Entry); break;
            case 45:
                if (index == 0) pub["gold_gain"] = AgentChoiceVar(model,"Prize" + (page + 1));
                else hp = new[]{5,6,7}[page]; break;
            case 49 when index == 1: hp = AgentChoiceVar(model,"RejectionHpLoss"); break;
            case 51: gold = AgentChoiceVar(model,new[]{"BreathingTechniquesCost","EmotionalAwarenessCost","ArachnidAcupunctureCost"}[index]); break;
            case 56 when page == 1 && index == 0: hp = AgentChoiceVar(model,"Damage"); break;
            case 58 when page > 0:
                var mad = option.HoverTips.OfType<CardHoverTip>().Select(h => h.Card).OfType<MadScience>().Single();
                if (page == 1) pub["card_type"] = AgentChoiceSemantics.CardType(mad.TinkerTimeType.ToString());
                else { pub["tinker_rider"] = AgentChoiceSemantics.Rider(mad.TinkerTimeRider.ToString());
                    pub["chosen_card_type"] = AgentChoiceSemantics.CardType(mad.TinkerTimeType.ToString()); }
                break;
        }
        if (hp != 0) pub["hp_cost"] = hp; if (max != 0) pub["max_hp_cost"] = max;
        if (gold != 0) pub["gold_cost"] = gold;
        if (eventId == 52 || page == 1 && eventId is 12 or 23 or 50) pub["starts_fight"] = true;
    }
}
