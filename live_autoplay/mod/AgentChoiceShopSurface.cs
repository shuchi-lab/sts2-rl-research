using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;

namespace STS2_MCP;

public static partial class McpMod
{
    // BEGIN SHOP SURFACE METHODS
    private static bool AgentChoiceShopVisible(AgentDecision result, MerchantInventory inventory, Player player,
        Dictionary<string, object?> context)
    {
        NMerchantInventory? surface;
        NMerchantButton? button;
        Action? open;
        if (player.RunState.CurrentRoom is MerchantRoom)
        {
            var room = NMerchantRoom.Instance;
            surface = room?.Inventory; button = room?.MerchantButton;
            open = room == null ? null : room.OpenInventory;
        }
        else
        {
            var eventRoom = NEventRoom.Instance;
            var room = eventRoom == null ? null : FindFirst<NFakeMerchant>(eventRoom);
            surface = room?.Inventory; button = room?.MerchantButton;
            open = button == null ? null : button.ForceClick;
        }
        if (surface == null || !GodotObject.IsInstanceValid(surface) || !surface.IsVisibleInTree()
            || !ReferenceEquals(surface.Inventory, inventory))
        { result.Owner = "none"; result.Phase = "ShopOpening"; return false; }
        context["inventory_open"] = surface.IsOpen;
        if (!surface.IsOpen)
        {
            result.Phase = "ShopOpening";
            if (open == null || button == null || !GodotObject.IsInstanceValid(button)
                || !button.IsVisibleInTree() || !button.IsEnabled)
            { result.Owner = "none"; return false; }
            result.Owner = "automatic";
            AgentChoiceAdd(result, "shop:open", "shop_open", "Open shop", new(),
                AgentChoiceDictionary(("action", "agent_open_shop")), button,
                () => { open(); return AgentChoiceOk(); });
            return false;
        }
        // IsOpen becomes true before the shop's one-second slide/fade completes.
        // Borrow the actual native tween; do not dispose a scene-owned object.
        if (GetInstanceFieldValue(surface, "_inventoryTween") is Tween tween
            && GodotObject.IsInstanceValid(tween) && tween.IsRunning())
        { result.Owner = "none"; result.Phase = "ShopOpening"; return false; }
        return true;
    }
    // END SHOP SURFACE METHODS
}
