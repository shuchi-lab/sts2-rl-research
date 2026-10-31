using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

// The native pile screen stages picks until Confirm. The frozen return-to-hand
// effect moves each pick immediately. Translate that public, deterministic move
// in the model view only; never move a native card before its normal confirmation.
internal static class BattleSelectionFlow
{
    internal static bool ReturnsToHand(string kind, int sourcePotion) =>
        sourcePotion == 0 && kind is "DiscardToHand" or "DrawPileToHand";

    // These frozen potion selectors already retain their selected hand cards
    // until an explicit confirm, exactly like the native hand selector.
    internal static bool StagesHandCards(string kind, int sourcePotion) =>
        (sourcePotion == 30 && kind == "DiscardAnyThenDraw") ||
        (sourcePotion == 1 && kind == "ExhaustAnyFromHand");

    // Native Gambling Chip (FromHandForDiscard, then DiscardAndDraw at confirm) stages its
    // picks in hand. The only frozen potion-0 DiscardAnyThenDraw is Gambling Chip, and it
    // discards each pick at once and reopens over the remaining hand. Translate the native
    // ordered pick list into that public model view; the native cards are not moved.
    internal static bool DiscardsEachPick(string kind, int sourcePotion) =>
        sourcePotion == 0 && kind == "DiscardAnyThenDraw";

    internal static (T[] Hand, T[] Discard) DiscardView<T>(IEnumerable<T> hand,
        IEnumerable<T> discard, IReadOnlyList<T> picks) where T : class
    {
        var staged = new HashSet<T>(picks, ReferenceEqualityComparer.Instance);
        return (hand.Where(c => !staged.Contains(c)).ToArray(), discard.Concat(picks).ToArray());
    }

    // Frozen DiscardMovedCards discards `card with { FreeThisTurn = false }`; nothing else in
    // the row changes. Clear that column on copies of the staged rows only (frozen card_fields[2]).
    internal const int FreeThisTurnColumn = 2;

    internal static double[][] StagedDiscardRows(IReadOnlyList<double[]> rows, int nativeDiscardCount) =>
        rows.Select((row, i) =>
        {
            if (i < nativeDiscardCount) return row;
            var copy = (double[])row.Clone();
            copy[FreeThisTurnColumn] = 0;
            return copy;
        }).ToArray();

    // Frozen staged potions carry Selected; return-to-hand and per-pick discard views carry none.
    internal static int SelectedCount(string kind, int sourcePotion, bool handSelection, int nativeSelected) =>
        ReturnsToHand(kind, sourcePotion) || (handSelection && DiscardsEachPick(kind, sourcePotion)) ? 0 : nativeSelected;

    internal static (T[] Hand, T[] Source) ReturnView<T>(IEnumerable<T> hand,
        IEnumerable<T> source, IReadOnlyList<T> selected) where T : class
    {
        var picks = new HashSet<T>(selected, ReferenceEqualityComparer.Instance);
        return (hand.Concat(selected).ToArray(), source.Where(c => !picks.Contains(c)).ToArray());
    }

    internal static bool Finished(int maximum, int selected, int remainingCandidates) =>
        selected >= maximum || remainingCandidates == 0;

    // Amount is an effect parameter in the training protocol, not generally
    // the number of cards clickable on the native screen.
    internal static int Amount(string source, string kind, bool upgraded, int maximum, int selected) => source switch
    {
        "BurningPact" => upgraded ? 3 : 2,
        "DualWield" => upgraded ? 2 : 1,
        "NeowsFury" => Math.Max(0, maximum - selected),
        "Purity" => Math.Max(0, maximum - selected),
        "GamblersBrew" or "Ashwater" or "GamblingChip" => 0,
        "Headbutt" or "TrueGrit" or "Brand" or "SeekerStrike" or "Wish" or
        "SecretTechnique" or "SecretWeapon" or "Armaments" or "ThinkingAhead" or "Scavenge" => 0,
        _ when kind == "GeneratedCardToHand" => 0,
        _ => maximum
    };

    internal static string FinishAction(bool hand, bool chooseCard) =>
        hand ? "combat_confirm_selection" : chooseCard ? "cancel_selection" : "confirm_selection";
}
