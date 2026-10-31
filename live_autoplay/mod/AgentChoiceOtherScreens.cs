using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private static void AgentChoiceShop(AgentDecision result, MerchantInventory? inventory, Player player,
        Dictionary<string, object?> context)
    {
        result.Phase = player.RunState.CurrentRoom is EventRoom ? "FakeMerchant" : "Shop";
        if (inventory == null) { result.Owner = "none"; return; }
        if (!AgentChoiceShopVisible(result, inventory, player, context)) return;
        var all = inventory.AllEntries.ToList();
        var shopControls = FindAll<NMerchantSlot>(((SceneTree)Engine.GetMainLoop()).Root);
        context["removal_cost"] = inventory.CardRemovalEntry?.Cost ?? 0;
        context["removal_used"] = inventory.CardRemovalEntry?.IsStocked == false;
        foreach (var entry in all.Where(e => e.IsStocked && e.EnoughGold))
        {
            int index = all.IndexOf(entry);
            var pub = AgentChoiceDictionary(("cost", entry.Cost)); string kind, label;
            switch (entry)
            {
                case MerchantCardEntry c when c.CreationResult?.Card is CardModel card:
                    int cardId = AgentIds.Id("cards", card.Id.Entry);
                    if (!AgentIds.CardSupported(cardId)) continue;
                    pub["card"] = AgentChoiceCard(card); pub["slot"] = inventory.CardEntries.ToList().IndexOf(c);
                    kind = "shop_card"; label = card.Id.Entry; break;
                case MerchantRelicEntry r when r.Model != null:
                    if (!AgentChoiceRelicAllowed(r.Model, player, false)) continue;
                    pub["relic_id"] = AgentIds.Id("relics", r.Model.Id.Entry); pub["slot"] = inventory.RelicEntries.ToList().IndexOf(r);
                    kind = "shop_relic"; label = r.Model.Id.Entry; break;
                case MerchantPotionEntry p when p.Model != null:
                    int potionId = AgentIds.Id("potions", p.Model.Id.Entry);
                    if (!AgentIds.PotionSupported(potionId) || !AgentShopPurchase.PotionPurchasable(
                        Hook.ShouldProcurePotion(player.RunState, player.Creature.CombatState, p.Model, player),
                        player.PotionSlots, player.MaxPotionCount)) continue;
                    pub["potion_id"] = potionId; pub["slot"] = inventory.PotionEntries.ToList().IndexOf(p);
                    kind = "shop_potion"; label = p.Model.Id.Entry; break;
                case MerchantCardRemovalEntry:
                    kind = "shop_remove"; label = "Remove card"; break;
                default: throw new InvalidOperationException("unsupported:merchant_entry:" + entry.GetType().Name);
            }
            AgentChoiceAdd(result, "shop:" + kind + ":" + index, kind, label, pub,
                AgentChoiceDictionary(("action", "shop_purchase"), ("index", index), ("expected_cost", entry.Cost)),
                AgentChoiceMerchantAnchor(entry,shopControls),
                execute: () => AgentShopPurchase.Refusal(entry.OnTryPurchaseWrapper(inventory)) is string refused
                    ? AgentChoiceDictionary(("status", "error"), ("error", refused)) : AgentChoiceOk());
        }
        for (int slot = 0; slot < player.MaxPotionCount; slot++)
        {
            var potion = player.PotionSlots[slot]; if (potion == null) continue;
            var pub = AgentChoiceDictionary(("slot", slot), ("potion_id", AgentIds.Id("potions", potion.Id.Entry)));
            if (player.CanUseOrRemovePotions) AgentChoiceAdd(result, "potion:discard:" + slot, "potion_discard", "Discard " + potion.Id.Entry,
                new(pub), AgentChoiceDictionary(("action", "discard_potion"), ("slot", slot)), AgentChoicePotionAnchor(potion));
            if (player.CanUseOrRemovePotions && potion.Usage == PotionUsage.AnyTime && potion.PassesCustomUsabilityCheck && !potion.IsQueued)
                AgentChoiceAdd(result, "potion:use:" + slot, "potion_use", "Use " + potion.Id.Entry,
                    new(pub), AgentChoiceDictionary(("action", "use_potion"), ("slot", slot)), AgentChoicePotionAnchor(potion));
        }
        AgentChoiceAdd(result, "shop:leave", "shop_leave", "Leave shop", new(), AgentChoiceDictionary(("action", "proceed")),
            NMerchantRoom.Instance?.ProceedButton);
    }

    private static void AgentChoiceCheckRelic(RelicModel relic, Player player, bool ancient)
    {
        if (!AgentChoiceRelicAllowed(relic, player, ancient))
            throw new InvalidOperationException("unsupported:relic_acquisition:" + relic.Id.Entry);
    }
    private static bool AgentChoiceRelicAllowed(RelicModel relic, Player player, bool ancient)
    {
        int id = AgentIds.Id("relics", relic.Id.Entry);
        return !(ancient && AgentIds.AncientRelicDeclined(id)) && player.MaxPotionCount + AgentIds.CapacityIncrease(id) <= 6;
    }

    private static void AgentChoiceBundle(AgentDecision result, NChooseABundleSelectionScreen screen,
        Dictionary<string, object?> context)
    {
        result.Phase = "BundleSelect";
        var preview = screen.GetNodeOrNull<Control>("%BundlePreviewContainer");
        context["selected"] = preview?.Visible == true ? 0 : -1;
        if (preview?.Visible == true)
        {
            result.Owner = "automatic";
            AgentChoiceAdd(result, "bundle:confirm", "proceed", "Confirm bundle", new(),
                AgentChoiceDictionary(("action", "confirm_bundle_selection"))); return;
        }
        var bundles = FindAll<NCardBundle>(screen).ToList();
        for (int i = 0; i < bundles.Count; i++)
        {
            var bundle = bundles[i];
            if (bundle.Bundle.Count != 3) throw new InvalidOperationException("unsupported:bundle_card_count");
            if (bundle.Bundle.Any(c => !AgentIds.CardSupported(AgentIds.Id("cards", c.Id.Entry)))) continue;
            AgentChoiceAdd(result, "bundle:" + i, "bundle", "Bundle " + (i + 1),
                AgentChoiceDictionary(("slot", i), ("cards", bundle.Bundle.Select(AgentChoiceCard).ToList())),
                AgentChoiceDictionary(("action", "agent_compound"), ("stages", new[] {
                    AgentChoiceDictionary(("action", "select_bundle"), ("index", i)),
                    AgentChoiceDictionary(("action", "confirm_bundle_selection")) })), bundle);
        }
    }

    private static void AgentChoiceTreasure(AgentDecision result, TreasureRoom room, Player player, Dictionary<string, object?> context)
    {
        result.Phase = "Treasure";
        var ui = FindFirst<NTreasureRoom>(((SceneTree)Engine.GetMainLoop()).Root);
        if (ui == null) { result.Owner = "none"; return; }
        var chest = ui.GetNodeOrNull<NClickableControl>("Chest");
        context["opened"] = chest?.IsEnabled != true;
        if (chest?.IsEnabled == true)
        {
            AgentChoiceAdd(result, "treasure:open", "treasure_open", "Open chest", new(),
                AgentChoiceDictionary(("action", "agent_open_chest")), chest,
                () => { chest.ForceClick(); return AgentChoiceOk(); }); return;
        }
        var holders = FindAll<NTreasureRoomRelicHolder>(ui).Where(h => h.IsEnabled && h.Visible && h.Relic?.Model != null).ToList();
        if (holders.Count > 1) throw new InvalidOperationException("unsupported:multiple_treasure_relic_choices");
        if (holders.Count == 1)
        {
            var holder = holders[0]; var relic = holder.Relic!.Model;
            AgentChoiceCheckRelic(relic, player, false);
            AgentChoiceAdd(result, "treasure:relic:" + relic.Id.Entry, "treasure_relic", relic.Id.Entry, new(),
                AgentChoiceDictionary(("action", "claim_treasure_relic"), ("index", 0)), holder);
        }
        else if (ui.ProceedButton.IsEnabled)
            AgentChoiceAdd(result, "treasure:leave", "treasure_leave", "Proceed", new(), AgentChoiceDictionary(("action", "proceed")), ui.ProceedButton);
        else result.Owner = "none";
    }

    private static void AgentChoiceSphere(AgentDecision result, NCrystalSphereScreen screen, RunState run,
        Dictionary<string, object?> context)
    {
        result.Phase = "CrystalSphere";
        var entity = (CrystalSphereMinigame)AgentChoiceField(screen, "_entity");
        var cells = FindAll<NCrystalSphereCell>(screen).OrderBy(c => c.Entity.Y).ThenBy(c => c.Entity.X).ToList();
        context["divinations"] = entity.DivinationCount;
        context["cells"] = cells.Select(c => AgentChoiceDictionary(("x", c.Entity.X), ("y", c.Entity.Y), ("hidden", c.Entity.IsHidden))).ToList();
        context["revealed_items"] = cells.Where(c => !c.Entity.IsHidden && c.Entity.Item != null).Select(c => c.Entity.Item!).Distinct().Count();
        // Never read entity.Items or hidden cell.Item: only already-revealed public pieces above.
        if (entity.DivinationCount > 0)
        foreach (var cell in cells.Where(c => c.Entity.IsHidden && c.Visible))
        foreach (string tool in new[] { "big", "small" })
            AgentChoiceAdd(result, $"sphere:{tool}:{cell.Entity.X}:{cell.Entity.Y}", "sphere_cell", $"{tool} ({cell.Entity.X},{cell.Entity.Y})",
                AgentChoiceDictionary(("tool", tool), ("x", cell.Entity.X), ("y", cell.Entity.Y)),
                AgentChoiceDictionary(("action", "agent_compound"), ("stages", new[] {
                    AgentChoiceDictionary(("action", "crystal_sphere_set_tool"), ("tool", tool)),
                    AgentChoiceDictionary(("action", "crystal_sphere_click_cell"), ("x", cell.Entity.X), ("y", cell.Entity.Y)) })), cell);
        if (FindCrystalSphereProceedButton(screen) != null)
            AgentChoiceAdd(result, "sphere:proceed", "proceed", "Proceed", new(), AgentChoiceDictionary(("action", "crystal_sphere_proceed")), FindCrystalSphereProceedButton(screen));
    }
}
