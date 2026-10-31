using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private static CardReward? AgentOpenCardOffer;
    private static void AgentChoiceCardReward(AgentDecision result, NCardRewardSelectionScreen screen,
        Dictionary<string, object?> context)
    {
        result.Phase = "CardReward";
        var extra = ((IEnumerable)AgentChoiceField(screen, "_extraOptions")).Cast<CardRewardAlternative>().ToList();
        context["reroll_available"] = extra.Any(a => a.OptionId.ToUpperInvariant() == "REROLL");
        var offered = ((IEnumerable)AgentChoiceField(screen,"_options")).Cast<CardCreationResult>().Select(r => r.Card).ToList();
        if (offered.Count > 4) throw new InvalidOperationException("unsupported:more_than_four_reward_cards");
        var holders = offered.Select(screen.GetCardHolder).ToList();
        // Native card holders are disabled during the opening/reroll animation.
        if (holders.Any(h => h == null || !(bool)AgentChoiceField(h,"_isClickable"))) { result.Owner = "none"; return; }
        context["fourth_card"] = offered.Count > 3 ? AgentChoiceCard(offered[3]) : null;
        context["returns_to_reward_screen"] = AgentOpenCardOffer != null;
        context["remaining_card_offers"] = NOverlayStack.Instance == null ? 0 : AgentLiveCardOffers();
        for (int i = 0; i < holders.Count; i++)
        {
            var holder = holders[i]; var card = offered[i]; int slot = i;
            int id = AgentIds.Id("cards", card.Id.Entry);
            if (!AgentIds.CardSupported(id)) continue;
            AgentChoiceAdd(result, $"card:take:{i}:{id}", "card_take", card.Id.Entry,
                AgentChoiceDictionary(("slot", i), ("card", AgentChoiceCard(card))),
                AgentChoiceDictionary(("action", "select_card_reward"), ("card_index", i)), holder,
                () => { holder.EmitSignal(NCardHolder.SignalName.Pressed, holder); return AgentChoiceOk(); });
        }
        var buttons = FindAll<NCardRewardAlternativeButton>(screen);
        if (buttons.Count != extra.Count) throw new InvalidOperationException("unsupported:reward_alternative_button_binding");
        for (int i = 0; i < extra.Count; i++)
        {
            var button = buttons[i]; var alternative = extra[i];
            string kind = AgentChoiceSemantics.AlternativeKind(alternative.OptionId);
            AgentChoiceAdd(result, "card:" + alternative.OptionId.ToLowerInvariant(), kind, alternative.OptionId,
                new(), AgentChoiceDictionary(("action", "agent_alternative"), ("option_id", alternative.OptionId)), button,
                () => {
                    // Native Skip dismisses but keeps the reward row. The normalized flow never reopens it.
                    if (kind == "card_skip" && AgentOpenCardOffer != null) AgentFinalSkippedRewards.Add(AgentOpenCardOffer);
                    button.ForceClick(); return AgentChoiceOk(); });
        }
    }

    private static int AgentLiveCardOffers()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        return FindAll<NRewardButton>(tree.Root).Count(b => b.Reward is CardReward && b.IsEnabled
            && !AgentFinalSkippedRewards.Contains(b.Reward));
    }

    private static void AgentChoiceRewards(AgentDecision result, NRewardsScreen screen, RunState run, Player player,
        Dictionary<string, object?> context)
    {
        result.Phase = "RelicReward";
        var all = FindAll<NRewardButton>(screen).Where(b => b.Reward != null && b.IsEnabled).ToList();
        var live = all.Where(b => !AgentFinalSkippedRewards.Contains(b.Reward!) &&
            (b.Reward is not RelicReward relic || AgentChoiceRelicAllowed(relic.Relic!, player, false))).ToList();
        context["context"] = run.BaseRoom is EventRoom ev ? ev.CanonicalEvent is AncientEventModel ? "ancient" : "event" : "room";
        context["can_leave"] = FindFirst<NProceedButton>(screen)?.IsEnabled == true;
        context["potion_rows"] = live.Count(b => b.Reward is PotionReward);
        context["card_offer_rows"] = live.Count(b => b.Reward is CardReward);
        context["card_removals_owed"] = live.Count(b => b.Reward is CardRemovalReward);
        // Same normalization order as v2-auto-reward: gold, relics, potions, card offers.
        var next = live.OrderBy(b => b.Reward switch { GoldReward => 0, RelicReward => 1, PotionReward => 2,
            SpecialCardReward => 3, CardReward => 4, CardRemovalReward => 5, _ => 6 }).FirstOrDefault();
        if (next == null)
        {
            if (FindFirst<NProceedButton>(screen)?.IsEnabled != true) { result.Owner = "none"; return; }
            AgentChoiceAdd(result, "reward:leave", "reward_leave", "Proceed", new(),
                AgentChoiceDictionary(("action", "proceed")), FindFirst<NProceedButton>(screen));
            return;
        }
        int row = all.IndexOf(next); var reward = next.Reward!;
        if (reward is PotionReward p && p.Potion != null)
        {
            int incoming = AgentIds.Id("potions", p.Potion.Id.Entry);
            if (!AgentIds.PotionSupported(incoming))
            {
                AgentChoiceSkipIncoming(result, p, incoming, row, next); return;
            }
            int free = player.PotionSlots.Take(player.MaxPotionCount).Select((v, i) => (v, i)).Where(x => x.v == null).Select(x => x.i).DefaultIfEmpty(-1).First();
            if (free >= 0 || player.Relics.Any(r => r.Id.Entry == "SOZU"))
            {
                AgentChoiceAdd(result, "reward:potion:" + incoming, "reward_potion", p.Potion.Id.Entry,
                    new(), AgentChoiceDictionary(("action", "claim_reward"), ("index", row)), next,
                    () => { next.ForceClick(); return AgentChoiceOk(); });
                return;
            }
            context["decision"] = "potion_full_belt";
            if (!player.CanUseOrRemovePotions) { AgentChoiceSkipIncoming(result,p,incoming,row,next); return; }
            for (int i = 0; i < player.MaxPotionCount; i++)
            {
                var held = player.PotionSlots[i]; if (held == null) continue;
                int slot = i, id = AgentIds.Id("potions", held.Id.Entry);
                var pub = AgentChoiceDictionary(("potion_id", incoming), ("held_potion_id", id), ("slot", slot));
                AgentChoiceAdd(result, $"potion:replace:{slot}:{incoming}", "potion_replace", "Replace " + held.Id.Entry, pub,
                    AgentChoiceDictionary(("action", "agent_compound"), ("stages", new[] {
                        AgentChoiceDictionary(("action", "discard_potion"), ("slot", slot), ("expected_potion_id", id)),
                        AgentChoiceDictionary(("action", "claim_reward"), ("index", row), ("reward_handle", AgentObjectIds.Get(p)), ("expected_reward_potion_id", incoming)) }),
                        ("expected_slot", slot), ("expected_potion_id", incoming)), AgentChoicePotionAnchor(held));
                if (held.Usage == PotionUsage.AnyTime && held.PassesCustomUsabilityCheck && !held.IsQueued)
                    AgentChoiceAdd(result, $"potion:use_accept:{slot}:{incoming}", "potion_use_accept", "Use " + held.Id.Entry,
                        new(pub), AgentChoiceDictionary(("action", "agent_compound"), ("stages", new[] {
                            AgentChoiceDictionary(("action", "use_potion"), ("slot", slot), ("expected_potion_id", id)),
                            AgentChoiceDictionary(("action", "claim_reward"), ("index", row), ("reward_handle", AgentObjectIds.Get(p)), ("expected_reward_potion_id", incoming)) }),
                            ("expected_slot", slot), ("expected_potion_id", incoming)), AgentChoicePotionAnchor(held));
            }
            AgentChoiceSkipIncoming(result, p, incoming, row, next);
            return;
        }
        if (reward is SpecialCardReward special)
        {
            context["decision"] = "special_card";
            var specialCard = (CardModel)AgentChoiceField(special,"_card");
            var pub = AgentChoiceDictionary(("card",AgentChoiceCard(specialCard)));
            if (AgentIds.CardSupported(AgentIds.Id("cards",specialCard.Id.Entry)))
                AgentChoiceAdd(result,"reward:special:"+row,"reward_special_card",specialCard.Id.Entry,new(pub),
                    AgentChoiceDictionary(("action","claim_reward"),("index",row)),next,
                    () => { next.ForceClick(); return AgentChoiceOk(); });
            AgentChoiceAdd(result,"special:skip:"+row,"card_skip","Skip special card",new(pub),
                AgentChoiceDictionary(("action","agent_skip_special")),next,
                () => { AgentFinalSkippedRewards.Add(special); return AgentChoiceOk(); });
        }
        else if (reward is CardRemovalReward)
            AgentChoiceAdd(result,"reward:open_removal:"+row,"proceed","Open card removal",new(),
                AgentChoiceDictionary(("action","claim_reward"),("index",row)),next,
                () => { next.ForceClick(); return AgentChoiceOk(); });
        else if (reward is CardReward card)
            AgentChoiceAdd(result, "reward:open_cards:" + row, "reward_open_cards", "Open card offer", new(),
                AgentChoiceDictionary(("action", "claim_reward"), ("index", row)), next,
                () => { AgentOpenCardOffer = card; next.ForceClick(); return AgentChoiceOk(); });
        else if (reward is GoldReward or RelicReward)
        {
            string kind = reward is GoldReward ? "reward_gold" : "reward_relic";
            if (reward is RelicReward relic) AgentChoiceCheckRelic(relic.Relic!, player, false);
            // The bound reward object enters the decision signature only. Ectoplasm zeroes gold
            // gains and Amethyst Aubergine adds a second GoldReward, so claiming one gold row can
            // leave an identical public payload; the new front row must still be a new decision.
            AgentChoiceAdd(result, "reward:" + kind + ":" + row, kind, kind, new(),
                AgentChoiceDictionary(("action", "claim_reward"), ("index", row), ("reward_handle", AgentObjectIds.Get(reward))), next,
                () => { next.ForceClick(); return AgentChoiceOk(); });
        }
        else throw new InvalidOperationException("unsupported:reward_kind:" + reward.GetType().Name);
    }

    private static void AgentChoiceSkipIncoming(AgentDecision result, PotionReward reward, int id, int row, NRewardButton button)
        => AgentChoiceAdd(result, "potion:skip_incoming:" + row, "potion_skip_incoming", "Skip incoming potion",
            AgentChoiceDictionary(("potion_id", id)), AgentChoiceDictionary(("action", "agent_skip_incoming")), button,
            () => { AgentFinalSkippedRewards.Add(reward); return AgentChoiceOk(); });

    internal static Dictionary<string, object?> TryAgentClaimRewardHandle(int handle)
    {
        if (NOverlayStack.Instance?.Peek() is not NRewardsScreen screen)
            return AgentChoiceDictionary(("status","error"),("message","Bound reward screen is not open"));
        var button = FindAll<NRewardButton>(screen).SingleOrDefault(b => b.Reward != null && AgentObjectIds.Get(b.Reward) == handle);
        if (button == null || !button.IsEnabled || AgentFinalSkippedRewards.Contains(button.Reward!))
            return AgentChoiceDictionary(("status","error"),("message","Bound reward is no longer claimable"));
        button.ForceClick(); return AgentChoiceOk();
    }

    private static void AgentChoiceRest(AgentDecision result, RestSiteRoom room, Player player, Dictionary<string, object?> context)
    {
        result.Phase = "Rest"; context["options_taken_mask"] = AgentRestTakenMask;
        var ui = NRestSiteRoom.Instance;
        if (ui == null || !AgentChoiceRestReady(room, ui)) { result.Owner = "none"; result.Phase = "Waiting"; return; }
        var buttons = FindAll<NRestSiteButton>(ui).ToList();
        // The synchronizer removes chosen options before old buttons are rebuilt. Those old
        // buttons still have enabled Option models while their native controls are disabled.
        bool pending = room.Options.All(o => !o.IsEnabled);
        context["result_pending"] = pending;
        if (pending)
        {
            if (ui.ProceedButton?.IsEnabled != true || !ui.ProceedButton.IsNodeReady() || !ui.ProceedButton.IsVisibleInTree())
            { result.Owner = "none"; result.Phase = "Waiting"; return; }
            AgentChoiceAdd(result, "rest:proceed", "proceed", "Proceed", new(), AgentChoiceDictionary(("action", "proceed")), ui.ProceedButton);
            return;
        }
        foreach (var option in room.Options.Where(o => o.IsEnabled))
        {
            var binding = buttons.SingleOrDefault(b => ReferenceEquals(b.Option, option));
            if (binding == null || !binding.IsNodeReady() || !binding.IsVisibleInTree() || !binding.IsEnabled)
            { result.Owner = "none"; result.Phase = "Waiting"; return; }
        }
        for (int i = 0; i < room.Options.Count; i++)
        {
            var option = room.Options[i]; if (!option.IsEnabled) continue;
            string name = option.OptionId.ToLowerInvariant(); int index = i;
            var anchor = buttons.Single(b => ReferenceEquals(b.Option, option));
            if (name == "smith")
            {
                foreach (var card in PileType.Deck.GetPile(player).Cards.Where(c => c.IsUpgradable))
                {
                    int instance = AgentObjectIds.Card(card);
                    AgentChoiceAdd(result, "rest:smith:" + instance, "rest_smith", "Smith " + card.Id.Entry,
                        AgentChoiceDictionary(("instance_id", instance), ("card", AgentChoiceCard(card))),
                        AgentChoiceDictionary(("action", "agent_compound"), ("selection_max", 1), ("expected_card_instance", instance),
                            ("integrity", "one_card_upgrade"),
                            ("stages", new[] { AgentChoiceDictionary(("action", "choose_rest_option"), ("index", index), ("option_id",option.OptionId)),
                                AgentChoiceDictionary(("action", "select_card"), ("card_instance", instance)),
                                AgentChoiceDictionary(("action", "confirm_selection")) })), anchor);
                }
            }
            else if (name is "heal" or "clone" or "lift" or "dig" or "hatch" or "cook" or "kindle")
            {
                var pub = new Dictionary<string, object?>();
                if (name == "heal") pub["heal"] = (int)HealRestSiteOption.GetHealAmount(player);
                AgentChoiceAdd(result, "rest:" + name, "rest_" + name, option.OptionId, pub,
                    AgentChoiceDictionary(("action", "choose_rest_option"), ("index", i), ("option_id",option.OptionId)), anchor,
                    () => { anchor.ForceClick(); return AgentChoiceOk(); });
            }
            else throw new InvalidOperationException("unsupported:rest_option:" + option.OptionId);
        }
        if (ui?.ProceedButton.IsEnabled == true)
            AgentChoiceAdd(result, "rest:leave", "rest_leave", "Leave", new(), AgentChoiceDictionary(("action", "proceed")), ui.ProceedButton);
    }

    private static bool AgentChoiceRestReady(RestSiteRoom room, NRestSiteRoom ui)
    {
        // RunState.CurrentRoom changes before the async Enter finishes loading/replacing its scene.
        if (!GodotObject.IsInstanceValid(ui) || !ui.IsInsideTree() || !ui.IsNodeReady() || !ui.IsVisibleInTree()
            || !ReferenceEquals(AgentChoiceField(ui, "_room"), room)) return false;
        if (ReferenceEquals(_agentChoiceRestVfxUi, ui) && _agentChoiceRestVfx != null)
        {
            // This native task covers HideChoices, the rest option's VFX, rebuilding buttons,
            // enabling Proceed and ShowChoices. A duration guess cannot represent completion.
            if (!_agentChoiceRestVfx.IsCompleted) return false;
            if (!_agentChoiceRestVfx.IsCompletedSuccessfully)
                throw new InvalidOperationException("unsupported:native_rest_transition_failed");
        }
        return true;
    }

    private static void AgentChoiceSelectionScreen(AgentDecision result, Control screen, Player player, Dictionary<string, object?> context)
    {
        result.Phase = "TransformSelect";
        var origin = AgentSelectionContext ?? throw new InvalidOperationException("unsupported:uncaptured_selection_origin");
        result.Owner = AgentChoiceSemantics.SelectionOwner(origin.Origin, MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress);
        if (result.Owner == "battle") return;
        if (origin.Finished || screen is NChooseACardSelectionScreen && (bool)AgentChoiceField(screen,"_screenComplete"))
        { result.Owner = "none"; return; }
        if (screen is NChooseACardSelectionScreen choosing &&
            Time.GetTicksMsec() - (ulong)AgentChoiceField(choosing,"_openedTicks") <= 350)
        { result.Owner = "none"; return; }
        var data = screen is NCardGridSelectionScreen grid ? BuildCardSelectState(grid, (RunState)player.RunState) :
            BuildChooseCardState((NChooseACardSelectionScreen)screen, (RunState)player.RunState);
        if ((bool)data["preview_showing"]! || AgentOpenSelectionPreview(screen) != null) { result.Owner = "automatic"; AgentChoiceAdd(result, "select:confirm", "proceed", "Confirm", new(), AgentChoiceDictionary(("action", "confirm_selection"))); return; }
        var selected = screen is NChooseACardSelectionScreen ? new HashSet<CardModel>() :
            ((IEnumerable)AgentChoiceField(screen, "_selectedCards")).Cast<CardModel>().ToHashSet();
        int remaining = AgentChoiceSemantics.SelectionRemaining(origin.Max, selected.Count);
        bool offer = origin.Origin == "reward";
        bool optional = screen is NChooseACardSelectionScreen ? (bool)AgentChoiceField(screen,"_canSkip") : origin.Optional;
        int minimum = AgentChoiceSemantics.SelectionMinimum(screen is NChooseACardSelectionScreen ? "FromChooseACardScreen" : origin.Method,origin.Min,optional);
        NChoiceSelectionSkipButton? skip = screen is NChooseACardSelectionScreen && optional
            ? (NChoiceSelectionSkipButton)AgentChoiceField(screen,"_skipButton") : null;
        if (skip != null && (!skip.IsEnabled || !skip.IsVisibleInTree())) { result.Owner = "none"; return; }
        bool smith = player.RunState.CurrentRoom is RestSiteRoom && origin.Method == "FromDeckForUpgrade";
        context["mode"] = offer ? "grid" : smith ? "smith" : "deck_selection";
        if (offer) { context["picks_left"] = remaining; context["optional"] = optional; }
        else if (!smith)
        {
            context["purpose_id"] = AgentChoiceSelectionPurpose(origin);
            context["remaining"] = remaining;
            foreach (var f in origin.FollowUp) context[f.Key] = f.Value;
        }
        var holders = FindAllSortedByPosition<NGridCardHolder>(screen).Where(h => h.CardModel != null).ToList();
        // Native grids pool visible holders. The public offered collection, not materialized nodes, owns every candidate.
        var cards = ((IEnumerable)AgentChoiceField(screen, "_cards")).Cast<CardModel>().ToList();
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i]; if (selected.Contains(card)) continue;
            var holder = holders.FirstOrDefault(h => ReferenceEquals(h.CardModel, card));
            int id = AgentIds.Id("cards", card.Id.Entry), instance = AgentObjectIds.Card(card.DeckVersion ?? card);
            if (offer && !AgentIds.CardSupported(id)) continue;
            var pub = AgentChoiceDictionary(("card", AgentChoiceCard(card)));
            if (offer) pub["slot"] = i; else pub["instance_id"] = instance;
            var select = AgentChoiceDictionary(("action", "select_card"), ("index", i), ("card_instance", instance));
            bool confirms = remaining == 1 && screen is not NChooseACardSelectionScreen &&
                (screen is not NSimpleCardSelectScreen || origin.RequireConfirmation);
            var action = !confirms ? select : AgentChoiceDictionary(("action", "agent_compound"),
                ("selection_max", origin.Max), ("expected_card_instance", instance),
                ("expected_selected_instances", selected.Select(c => AgentObjectIds.Card(c.DeckVersion ?? c)).Append(instance).ToArray()),
                ("stages", new[] { select, AgentChoiceDictionary(("action", "confirm_selection")) }));
            AgentChoiceAdd(result, (offer ? "grid:" : "select:") + instance, offer ? "grid_card" : "select_card", card.Id.Entry, pub, action, holder);
        }
        if (offer && optional && selected.Count >= minimum)
            AgentChoiceAdd(result, "grid:done", "grid_done", skip == null ? "Done" : "Skip", new(), AgentChoiceDictionary(
                ("action", selected.Count == 0 ? "cancel_selection" : "confirm_selection")), skip,
                skip == null ? null : () => { skip.ForceClick(); return AgentChoiceOk(); });
    }

    internal static Dictionary<string, object?> TryAgentSelectCardInstance(int instance)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is NChooseACardSelectionScreen choice)
        {
            if (Time.GetTicksMsec() - (ulong)AgentChoiceField(choice,"_openedTicks") <= 350)
                return AgentChoiceDictionary(("status", "error"), ("message", "Choose-card opening animation is not complete"));
            var holder = FindAll<NCardHolder>(choice).SingleOrDefault(h => h.CardModel != null &&
                AgentObjectIds.Card(h.CardModel.DeckVersion ?? h.CardModel) == instance);
            if (holder == null) return AgentChoiceDictionary(("status", "error"), ("message", "Bound choose-card holder is unavailable"));
            holder.EmitSignal(NCardHolder.SignalName.Pressed,holder); return AgentChoiceOk();
        }
        if (overlay is not NCardGridSelectionScreen screen)
            return AgentChoiceDictionary(("status", "error"), ("message", "Bound card grid is not open"));
        var state = BuildCardSelectState(screen, RunManager.Instance.DebugOnlyGetState()!);
        if (state["preview_showing"] is true || AgentOpenSelectionPreview(screen) != null)
            return AgentChoiceDictionary(("status", "error"), ("message", "Selection preview is already open"));
        var cards = ((IEnumerable)AgentChoiceField(screen, "_cards")).Cast<CardModel>();
        var card = cards.SingleOrDefault(c => AgentObjectIds.Card(c.DeckVersion ?? c) == instance);
        if (card == null) return AgentChoiceDictionary(("status", "error"), ("message", "Bound card is no longer offered"));
        var selected = ((IEnumerable)AgentChoiceField(screen,"_selectedCards")).Cast<CardModel>();
        if (selected.Contains(card)) return AgentChoiceDictionary(("status", "error"), ("message", "Bound card is already selected"));
        screen.GetType().GetMethod("OnCardClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(screen, new object[] { card });
        return AgentChoiceOk();
    }

    // The visible native preview overlay of an open card-grid selection (BuildCardSelectState misses the enchant pair).
    internal static string? AgentOpenSelectionPreview(Control? screen = null)
        => (screen ?? NOverlayStack.Instance?.Peek() as Control) is NCardGridSelectionScreen grid
            ? AgentSelectionPreview.OpenContainer(n => grid.GetNodeOrNull<Control>(n), c => c.Visible) : null;

    private static int AgentChoiceSelectionPurpose(AgentChoiceSelection origin) => origin.Purpose >= 0 ? origin.Purpose :
        throw new InvalidOperationException("unsupported:selection_purpose:" + origin.Method + ":" + origin.PromptKey);
}
