using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

internal static class BattlePileSelection
{
    internal static List<BattleGridSurface.Offer<TCard,TAnchor>> Candidates<TCard,TAnchor>(
        object capturedPile,object currentPile,IReadOnlyList<TCard> currentCards,Func<TCard,bool>? filter,
        IEnumerable<TCard> selected,IEnumerable<(TCard Card,TAnchor Anchor)> materialized)
        where TCard:class where TAnchor:class
    {
        RequirePile(capturedPile,currentPile);
        var eligible=currentCards.Where(card=>filter==null || filter(card)).ToArray();
        return BattleGridSurface.Candidates(eligible,selected,materialized);
    }
    internal static void Validate<TCard>(object capturedPile,object currentPile,IReadOnlyList<TCard> currentCards,
        Func<TCard,bool>? filter,IEnumerable<TCard> selected,TCard bound) where TCard:class
    {
        RequirePile(capturedPile,currentPile);
        BattleGridSurface.Validate(currentCards.Where(card=>filter==null || filter(card)),selected,bound);
    }
    private static void RequirePile(object capturedPile,object currentPile)
    {
        if(capturedPile==null || !ReferenceEquals(capturedPile,currentPile))
            throw new BattleProjectionUnsupported("Combat pile selection differs from the captured native pile");
    }
    internal static void RequireLiquidMemories(string nativeType,string nativeEntry,int frozenPotionId,
        string pile,int min,int max,bool manual,bool cancelable)
    {
        if(nativeType!="LiquidMemories" || nativeEntry!="LIQUID_MEMORIES" || frozenPotionId!=37 ||
            pile!="Discard" || min!=1 || max!=1 || manual || cancelable)
            throw new BattleProjectionUnsupported("Liquid Memories selection differs from frozen discard-to-hand amount1");
    }
    // Installed DropletOfPrecognition.OnUse: FromCombatPile(Draw, prefs(1)), then Add(card, Hand);
    // frozen PotionEffects.OpenDropletSelection: DrawPileToHand, potion16, amount1, every draw card.
    internal static void RequireDropletOfPrecognition(string nativeType,string nativeEntry,int frozenPotionId,
        string pile,int min,int max,bool manual,bool cancelable)
    {
        if(nativeType!="DropletOfPrecognition" || nativeEntry!="DROPLET_OF_PRECOGNITION" || frozenPotionId!=16 ||
            pile!="Draw" || min!=1 || max!=1 || manual || cancelable)
            throw new BattleProjectionUnsupported("Droplet of Precognition selection differs from frozen draw-pile-to-hand amount1");
    }
}
