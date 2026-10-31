using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace STS2_MCP;

internal static class AgentShopPurchase
{
    // MerchantPotionEntry.OnTryPurchase -> PotionCmd.TryToProcure: Hook.ShouldProcurePotion (Sozu refuses
    // its owner) runs before AddPotionInternal needs an open slot within MaxPotionCount. Either refusal
    // returns (false,0) without spending gold or restocking, so such an entry is not a purchasable choice.
    internal static bool PotionPurchasable(bool procureAllowed, IEnumerable<object?> slots, int maxPotionCount)
        => procureAllowed && slots.Take(maxPotionCount).Any(v => v == null);

    // OnTryPurchaseWrapper reports a refusal only through its result; nothing in the observed state changes,
    // so a purchase that already completed false must fail immediately instead of awaiting settlement.
    internal static string? Refusal(Task<bool> purchase)
    {
        if (purchase.IsFaulted)
            return "Native merchant purchase faulted: " + (purchase.Exception?.GetBaseException().Message ?? "unknown");
        if (purchase.IsCanceled) return "Native merchant purchase was cancelled.";
        return purchase.IsCompletedSuccessfully && !purchase.Result
            ? "Native merchant refused the purchase; no gold was spent and the item stays stocked." : null;
    }
}
