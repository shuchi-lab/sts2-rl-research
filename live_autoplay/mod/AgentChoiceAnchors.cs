using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace STS2_MCP;

public static partial class McpMod
{
    private static Control? AgentChoiceMerchantAnchor(MerchantEntry entry, IEnumerable<NMerchantSlot> controls)
    {
        var slot = AgentChoiceSemantics.ReferenceAnchor(entry, controls, c => c.Entry, c => c.IsVisibleInTree());
        if (slot == null) return null;
        // The native hitbox encloses the displayed item, including card/relic/potion/removal visuals.
        var hitbox = slot.Hitbox;
        return hitbox != null && GodotObject.IsInstanceValid(hitbox) && hitbox.IsVisibleInTree() ? hitbox : slot;
    }

    internal static Control? AgentChoicePotionAnchor(PotionModel potion)
    {
        var container = NRun.Instance?.GlobalUi?.TopBar?.PotionContainer;
        if (container == null) return null;
        // The top-bar container owns every held slot; shop/reward potion visuals belong to other controls.
        return AgentChoiceSemantics.ReferenceAnchor(potion, FindAll<NPotionHolder>(container),
            h => h.Potion?.Model, h => h.IsVisibleInTree());
    }
}
