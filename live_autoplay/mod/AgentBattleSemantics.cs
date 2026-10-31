using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

internal static class BattleRunningRoster
{
    // Native CreatureCmd.Escape drops the creature from Enemies at once. The selected
    // worker's FatGremlin FLEE zeroes its HP with Escaped, and the enemy side's
    // HandleEnemyDeaths -> RemoveDeadEnemies drops that row before the player decides,
    // leaving the same survivors in the same order. Other native escapers (Thieving Hopper,
    // Battleworn Dummy time limit) beside live foes remain uncertified.
    private static readonly string[] CertifiedPlayerSideEscapes = { "FatGremlin" };
    internal static void RequireComplete(IEnumerable<string?> escaped, bool hasLiving, bool playerSide, bool rosterHoldsEscaped)
    {
        if (rosterHoldsEscaped)
            throw new BattleProjectionUnsupported("Escaped creature remains in the native enemy roster");
        var names = escaped.ToArray();
        if (names.Length == 0 || !hasLiving) return;
        if (!playerSide)
            throw new BattleProjectionUnsupported("Escaped enemy rows alongside live foes are certified only at player-side decisions");
        if (names.Any(n => n == null || !CertifiedPlayerSideEscapes.Contains(n)))
            throw new BattleProjectionUnsupported("Escaped enemy rows alongside live foes are not certified: " +
                string.Join(",", names.Select(n => n ?? "unknown")));
    }
}

internal sealed class BattleProjectionUnsupported : Exception
{
    internal BattleProjectionUnsupported(string message) : base(message) { }
}

// This class contains only knowledge acquired from observable placements. The native draw
// order is accepted as a membership set; it never decides which card is known to be next.
internal sealed class BattleDrawKnowledge
{
    private readonly List<int> _top = new();
    private readonly List<int> _bottom = new();
    internal void Place(int card, string position)
    {
        Remove(card);
        if (position == "top") _top.Insert(0, card);
        else if (position == "bottom") _bottom.Add(card);
        else if (position != "random") throw new BattleProjectionUnsupported("unknown placement " + position);
    }
    // A played card leaves the Play pile. Rebound and self-topdecking cards move it
    // there to Draw/Top, which the frozen engine records with TopDeck (known top).
    internal void Moved(int card, string previous, string destination, string position, bool shuffle)
    {
        Remove(card);
        if (destination == "Draw" && !shuffle && previous is "Hand" or "Discard" or "Play")
            Place(card, position.ToLowerInvariant());
    }
    internal void Remove(int card) { _top.Remove(card); _bottom.Remove(card); }
    internal void Shuffle() { _top.Clear(); _bottom.Clear(); }
    internal (int[] Top, int[] Bottom, int[] Unknown) Project(IEnumerable<int> drawMembership)
    {
        var present = drawMembership.ToHashSet();
        var top = _top.Where(present.Contains).ToArray();
        var bottom = _bottom.Where(present.Contains).ToArray();
        var known = top.Concat(bottom).ToHashSet();
        return (top, bottom, present.Where(x => !known.Contains(x)).OrderBy(x => x).ToArray());
    }
}

internal sealed record BattleLocalModifier(int Amount, string Type, int Expiration, bool ReduceOnly);
internal sealed record BattleStompCostContext(string NativeType, string NativeEntry, int FrozenCardId,
    int CompletedOwnedAttacks, bool MutableOwnedCombat)
{
    internal int CertifiedParkedOwnedAttacks { get; init; }
}
internal sealed record BattleStompCostRules(bool FreeAttack, int Tangled = 0);
internal sealed record BattleLocalCosts(int CostBump, int CostForCombat, bool FreeThisTurn,
    int CostThisTurn, bool FreeUntilPlayed, int Override, int OverrideBaseline, bool OverrideSetUpgraded)
{
    internal int StompObservedPendingEcho { get; init; }
    internal static BattleLocalCosts Project(IEnumerable<BattleLocalModifier> native, bool upgraded,
        int overrideBaseline = 0, bool? overrideSetUpgraded = null, bool sourceFreeThisTurn = false,
        BattleStompCostContext? stomp = null)
    {
        var modifiers = native.ToArray();
        int pendingEcho = stomp == null ? 0 : ValidateStomp(modifiers, stomp, overrideBaseline, sourceFreeThisTurn);
        int bump = 0, combat = int.MinValue, turn = int.MinValue, over = int.MinValue;
        bool free = false, until = false;
        int echoSoFar = 0, rollBaseline = -1;
        foreach (var m in modifiers)
        {
            // Native Stomp records this discount in local modifiers. The frozen
            // engine derives the same discount from completed owned Attack history.
            if (stomp != null && m.Type == "Relative" && m.Expiration == 2 && !m.ReduceOnly) { echoSoFar -= m.Amount; continue; }
            if (m.Type == "Relative" && m.Expiration == 0 && !m.ReduceOnly) bump += m.Amount;
            else if (m.Type == "Absolute" && m.Expiration == 0)
            {
                combat = m.Amount;
                // Confused's SetThisCombat on a mid-turn draw erases the echoes before
                // it. This turn the frozen override with that captured counter baseline
                // prices it exactly; the combat field alone carries it into later turns.
                if (stomp != null && echoSoFar > 0 && !m.ReduceOnly)
                { over = m.Amount; rollBaseline = echoSoFar; free = false; }
            }
            else if (m.Type == "Absolute" && (m.Expiration == 2 || m.Expiration == 6) && m.Amount == 0 && m.ReduceOnly) free = true;
            else if (m.Type == "Absolute" && m.Expiration == 6 && m.ReduceOnly) turn = m.Amount;
            else if (m.Type == "Absolute" && m.Expiration == 4 && m.Amount == 0) until = true;
            else if (m.Type == "Absolute" && m.Expiration == 6 && !m.ReduceOnly && m.Amount == 0 && sourceFreeThisTurn) free = true;
            else if (m.Type == "Absolute" && m.Expiration == 6 && !m.ReduceOnly) { over = m.Amount; free = false; rollBaseline = -1; }
            else throw new BattleProjectionUnsupported($"Unmapped local cost {m.Type}/{m.Expiration}/{m.Amount}/reduceOnly={m.ReduceOnly}");
        }
        bool roll = rollBaseline >= 0;
        return new(bump, combat, free, turn, until, over, over == int.MinValue ? 0 : roll ? rollBaseline : overrideBaseline,
            over != int.MinValue && (roll ? upgraded : overrideSetUpgraded ?? upgraded)) { StompObservedPendingEcho = pendingEcho };
    }

    private static int ValidateStomp(BattleLocalModifier[] modifiers, BattleStompCostContext stomp,
        int overrideBaseline, bool sourceFreeThisTurn)
    {
        if (stomp.NativeType != "Stomp" || stomp.NativeEntry != "STOMP" || stomp.FrozenCardId != 465 ||
            !stomp.MutableOwnedCombat || stomp.CompletedOwnedAttacks < 0 ||
            stomp.CertifiedParkedOwnedAttacks is < 0 or > 1)
            throw new BattleProjectionUnsupported("Unverified Stomp cost owner/history certificate");
        long echo = 0;
        foreach (var m in modifiers.Where(m => m.Type == "Relative" && m.Expiration == 2))
        {
            if (m.ReduceOnly || m.Amount >= 0)
                throw new BattleProjectionUnsupported("Unverified Stomp turn-relative modifier");
            echo += m.Amount;
        }
        long completedEcho = -(long)stomp.CompletedOwnedAttacks;
        if (echo != completedEcho && echo != completedEcho - stomp.CertifiedParkedOwnedAttacks)
            throw new BattleProjectionUnsupported("Stomp local discount differs from completed owned Attack history");
        // A parked Attack's echo is the last one appended (its BeforeCardPlayed is the latest
        // Attack start). The projected fields depend only on modifier order, never on the
        // counter, so the parked frozen view is the settled one with that Attack uncounted.
        // Only a non-free setter after the pending echo erases it natively; the frozen
        // counter would still subtract it on completion, so that order stays unrepresented.
        if (echo != completedEcho && modifiers.Skip(Array.FindLastIndex(modifiers, m => m.Type == "Relative" && m.Expiration == 2) + 1)
            .Any(m => m.Type == "Absolute" &&
            !((m.Amount == 0 && m.ReduceOnly && m.Expiration is 2 or 6) ||
              (m.Amount == 0 && m.Expiration == 4) ||
              (m.Amount == 0 && !m.ReduceOnly && m.Expiration == 6 && sourceFreeThisTurn))))
            throw new BattleProjectionUnsupported("Stomp parked Attack has an uncertified absolute cost combination");

        // Absolute modifiers are ordered in the native game. Certify only orders
        // represented by the existing frozen fields; a cap applied after an echo,
        // for example, cannot be moved before that echo without changing its cost.
        bool relativeSeen = false, overriding = false, freeSetterSeen = false, bumpSeen = false, ledgerOverride = false;
        long priorEcho = 0;
        foreach (var m in modifiers)
        {
            if (m.Type == "Relative" && m.Expiration == 2 && !m.ReduceOnly)
            { relativeSeen = true; priorEcho += m.Amount; continue; }
            if (m.Type == "Relative" && m.Expiration == 0 && !m.ReduceOnly)
            {
                if (overriding || (freeSetterSeen && m.Amount > 0))
                    throw new BattleProjectionUnsupported("Unverified Stomp modifier order after absolute setter");
                relativeSeen = true; bumpSeen = true;
                continue;
            }
            if (m.Type != "Absolute") continue; // The ordinary projector rejects unknown forms.
            if (m.Expiration == 0 && !m.ReduceOnly && !relativeSeen && !overriding && !freeSetterSeen)
                continue;
            // A combat setter after only echoes (Confused draw) is the frozen override
            // with its own preceding discount as baseline. An earlier bump, free setter
            // or ledger override would be erased natively but survive in frozen fields.
            if (m.Expiration == 0 && !m.ReduceOnly && priorEcho < 0 && !bumpSeen && !freeSetterSeen && !ledgerOverride)
            { overriding = true; continue; }
            if (((m.Expiration == 2 || m.Expiration == 6) && m.ReduceOnly && m.Amount == 0) ||
                (m.Expiration == 4 && m.Amount == 0) ||
                (m.Expiration == 6 && !m.ReduceOnly && m.Amount == 0 && sourceFreeThisTurn))
            {
                if (m.Expiration == 4 && overriding)
                    throw new BattleProjectionUnsupported("Stomp until-played free setter follows an absolute override");
                freeSetterSeen = true; continue;
            }
            if (m.Expiration == 6 && !m.ReduceOnly)
            {
                // Only the last override survives. Its baseline is the source-
                // captured completed discount at the exact native setter boundary.
                overriding = true; freeSetterSeen = false; ledgerOverride = true;
                if (!modifiers.SkipWhile(x => !ReferenceEquals(x, m)).Skip(1)
                    .Any(x => x.Type == "Absolute" && x.Expiration == 6 && !x.ReduceOnly) &&
                    priorEcho != -(long)overrideBaseline)
                    throw new BattleProjectionUnsupported("Stomp absolute override baseline differs from preceding discount");
                continue;
            }
            throw new BattleProjectionUnsupported("Unverified Stomp absolute modifier order/type");
        }
        return (int)(completedEcho - echo);
    }

    // Exact selected frozen EffectiveCost branch for Stomp without global cost
    // hooks. Its derived view keeps the raw negative result; only payment clamps.
    internal int StompFrozenEffectiveCost(int completedOwnedAttacks, bool ember, bool freeAttack = false, int tangled = 0)
    {
        if (tangled < 0) throw new BattleProjectionUnsupported("Negative Stomp Tangled surcharge");
        if (completedOwnedAttacks < 0) throw new BattleProjectionUnsupported("Negative Stomp view history");
        bool overriding = Override != int.MinValue;
        if (ember && !overriding) return 0;
        int cost = CostForCombat == int.MinValue ? 3 : CostForCombat;
        if (CostThisTurn != int.MinValue) cost = Math.Min(cost, CostThisTurn);
        // Native Stomp upgrades damage only, so its frozen UpgradeCost is zero.
        if (overriding)
        {
            cost = Override;
            if (CostThisTurn != int.MinValue) cost = Math.Min(cost, CostThisTurn);
            cost += OverrideBaseline;
        }
        if (FreeThisTurn) cost = 0;
        if (!FreeThisTurn) cost -= completedOwnedAttacks;
        if (FreeUntilPlayed && !overriding) return 0;
        if (!overriding && !FreeThisTurn) cost += CostBump;
        // Frozen Tangled is an Attack add after the bump, behind the Ember and
        // until-played returns. Native skips it for a negative local price; the
        // hand guard refuses every order where the two clamped prices differ.
        cost += tangled;
        // Selected frozen FreeAttackPower is an Attack-only Late zero. Its
        // current positive public stack is consumed before the played effect.
        return freeAttack ? 0 : cost;
    }

    // Native TangledPower adds its Amount only to its owner's Entangled cards. The
    // frozen player Tangled buff adds to every Attack; certify only that overlap.
    // A foreign stack never touches this card natively and is absent from frozen buffs.
    internal static int StompTangledSurcharge(bool owned, bool listedLocally, int ownedStacks,
        bool attack, bool entangled, int amount)
    {
        if (!owned)
        {
            if (listedLocally) throw new BattleProjectionUnsupported("Foreign Tangled hook is attached to the local encoded power list");
            return 0;
        }
        if (ownedStacks != 1 || !listedLocally) throw new BattleProjectionUnsupported("Tangled hook lacks one matching encoded local power stack");
        if (!attack || !entangled || amount <= 0)
            throw new BattleProjectionUnsupported("Tangled cost rule lacks positive owned Entangled Attack certificate");
        return amount;
    }

    internal static bool StompViewPlayable(int nativeReasonBits, int frozenRawCost, int energy)
        => (nativeReasonBits & ~16) == 0 && Math.Max(0, frozenRawCost) <= energy;
}

// The selected frozen worker view writes raw, unclamped CombatEngine.EffectiveCost.
// Installed GetAmountToSpend clamps the -1 unplayable sentinel (Status/Curse/Quest)
// to 0 and returns the owner's energy for X cards, while frozen keeps the printed
// cost (-1 for sentinels and Cascade, 0 for other X cards) plus its local/global terms.
internal sealed record BattleFrozenGlobalCost(bool Zero = false, int SpikedGauntlets = 0,
    int BorrowedTime = 0, int Curious = 0, int Tangled = 0);

internal static class BattleFrozenViewCost
{
    // Installed GetWithModifiers returns a negative base untouched; X skips modifiers.
    internal static bool Applies(bool costsX, int nativeRawCost) => costsX || nativeRawCost < 0;

    // Card type each installed v0.111 cost hook's first predicate requires; null for
    // hooks that price every card (Void Form, Brilliant Scarf, Borrowed Time, Veilpiercer).
    internal static string? HookCardTypeGate(string hookType) => hookType switch
    {
        "CorruptionPower" or "FreeSkillPower" => "Skill",
        "SpikedGauntlets" or "CuriousPower" or "FreePowerPower" => "Power",
        "TangledPower" or "FreeAttackPower" => "Attack",
        _ => null
    };

    // Installed v0.111 cost-hook inventory: Late hooks that return 0, additive ones.
    internal static bool IsZeroHook(string hookType) => hookType is "CorruptionPower" or "FreeSkillPower" or
        "FreePowerPower" or "FreeAttackPower" or "VoidFormPower" or "BrilliantScarf" or "VeilpiercerPower";
    internal static bool IsAdditiveHook(string hookType) => hookType is "SpikedGauntlets" or "CuriousPower" or
        "BorrowedTimePower" or "TangledPower";

    // Selected frozen CombatEngine.EffectiveCost order for a card without a named
    // branch (no sentinel/X card is Stomp, Banshee's Cry, Flatten or Pinpoint). Every
    // early `return 0` (Ember, Corruption, Void Form, Brilliant Scarf, Rocket Punch
    // until-played, Veilpiercer, Free Attack/Power/Skill) yields the same view, so
    // the global zero is one flag; additive terms keep their frozen positions.
    internal static int Project(bool costsX, int printedCost, int nativeRawCost, string cardType,
        bool ember, BattleLocalCosts local, BattleFrozenGlobalCost global)
    {
        if (!Applies(costsX, nativeRawCost)) throw new BattleProjectionUnsupported("Ordinary card entered the frozen sentinel/X cost view");
        if (costsX ? printedCost is not (0 or -1) : printedCost != -1 || nativeRawCost != -1)
            throw new BattleProjectionUnsupported("Sentinel/X card has an unreviewed printed cost");
        // Frozen Snecko Oil and random-cost rolls skip X and negative-cost cards, so a
        // native absolute override on one has no selected frozen counterpart.
        if (local.Override != int.MinValue) throw new BattleProjectionUnsupported("Sentinel/X card carries an absolute cost override frozen never sets");
        if (global.Tangled < 0 || global.BorrowedTime < 0 || global.Curious < 0 || global.SpikedGauntlets < 0)
            throw new BattleProjectionUnsupported("Negative global cost term");
        if (ember || global.Zero || local.FreeUntilPlayed) return 0;
        int cost = local.CostForCombat == int.MinValue ? printedCost : local.CostForCombat;
        if (local.CostThisTurn != int.MinValue) cost = Math.Min(cost, local.CostThisTurn);
        if (local.FreeThisTurn) cost = 0;
        if (cardType == "Power") cost += global.SpikedGauntlets;
        if (!local.FreeThisTurn) cost += local.CostBump;
        cost += global.BorrowedTime;
        if (global.Curious > 0 && cardType == "Power" && cost > 0) cost = Math.Max(0, cost - global.Curious);
        if (cardType == "Attack") cost += global.Tangled;
        return cost;
    }
}

internal sealed record BattleHistoryEvent(string Kind, bool LocalPlayer, bool ThisTurn,
    string CardType, bool ExcludeEligibleBlock, int Amount);

internal static class BattlePublicHistory
{
    internal static int[] Project(IEnumerable<BattleHistoryEvent> source)
    {
        var all = source.Where(x => x.LocalPlayer).ToArray();
        var turn = all.Where(x => x.ThisTurn).ToArray();
        return new[] {
            turn.Count(x => x.Kind == "start"),
            turn.Count(x => x.Kind == "start" && x.CardType == "Attack"),
            turn.Count(x => x.Kind == "start" && x.CardType is "Attack" or "Skill"),
            turn.Count(x => x.Kind == "finish" && x.CardType == "Attack"),
            turn.Any(x => x.Kind == "exhaust") ? 1 : 0,
            turn.Any(x => x.Kind == "damage" && x.Amount > 0) ? 1 : 0,
            all.Count(x => x.Kind == "damage" && x.Amount > 0),
            // Every native block row is a positive decimal gain; its int Amount may truncate to 0.
            turn.Count(x => x.Kind == "block" && !x.ExcludeEligibleBlock),
            all.Count(x => x.Kind == "finish"),
        };
    }
}

// Frozen CardBlockHistory counts Move gains with a CardPlay, minus the active play's rows:
// native UnmovablePower's predicate (CardPlay != null, owner's play, Props Move, != cardPlay).
internal static class BattleBlockHistory
{
    internal static BattleHistoryEvent Row(bool local, bool thisTurn, bool hasCardPlay, bool localPlay, bool move,
        bool activePlay, int amount) =>
        new("block", local, thisTurn, "", !(hasCardPlay && localPlay && move) || activePlay, amount);
}

internal static class BattleProjectionNames
{
    internal static string OptionId(string kind, int action, int target, int? potionSlot) =>
        $"{kind}:{action}:{target}" + (potionSlot is int p ? $":{p}" : "");
    internal static string SelectionConfirmAction(bool handSelection) =>
        handSelection ? "combat_confirm_selection" : "confirm_selection";
}

// Offered native models own the candidate set. UI nodes are pooled optional anchors.
// Use reference equality: two physical copies of the same definition are distinct choices.
internal static class BattleGridSurface
{
    internal sealed record Offer<TCard,TAnchor>(TCard Card,TAnchor? Anchor) where TCard : class where TAnchor : class;
    internal static List<Offer<TCard,TAnchor>> Candidates<TCard,TAnchor>(IEnumerable<TCard> offered,
        IEnumerable<TCard> selected,IEnumerable<(TCard Card,TAnchor Anchor)> materialized)
        where TCard : class where TAnchor : class
    {
        var picks=selected.ToArray(); var holders=materialized.ToArray();
        return offered.Where(c=>!picks.Any(p=>ReferenceEquals(p,c)))
            .Select(c=>new Offer<TCard,TAnchor>(c,holders.FirstOrDefault(h=>ReferenceEquals(h.Card,c)).Anchor)).ToList();
    }
    internal static void Select<TCard>(IEnumerable<TCard> offered,IEnumerable<TCard> selected,TCard bound,Action<TCard> nativeClick)
        where TCard : class
    {
        Validate(offered,selected,bound);
        nativeClick(bound);
    }
    internal static void Validate<TCard>(IEnumerable<TCard> offered,IEnumerable<TCard> selected,TCard bound) where TCard : class
    {
        if(!offered.Any(c=>ReferenceEquals(c,bound))) throw new BattleProjectionUnsupported("Bound grid card is no longer offered");
        if(selected.Any(c=>ReferenceEquals(c,bound))) throw new BattleProjectionUnsupported("Bound grid card is already selected");
    }
}

// A draw selector can expose every eligible card, but its source enumeration must not
// disclose the shuffled pile order. Only frozen public feature rows determine order;
// stable object identity breaks a tie after all features compare equal. Other piles
// retain their visible order, and each projected row retains its exact native binding.
internal static class BattleSelectionRows
{
    internal sealed record Row<T>(T Source,double[] Features,bool HiddenDraw,int StableIdentity);
    internal static List<Row<T>> Canonicalize<T>(IEnumerable<T> offered,Func<T,bool> hiddenDraw,
        Func<T,double[]> features,Func<T,int> stableIdentity)
    {
        var rows=offered.Select(c=>new Row<T>(c,features(c),hiddenDraw(c),stableIdentity(c))).ToList();
        var draw=rows.Where(x=>x.HiddenDraw).OrderBy(x=>x.Features,Comparer<double[]>.Create(Compare))
            .ThenBy(x=>x.StableIdentity).ToArray();
        int at=0;
        for(int i=0;i<rows.Count;i++) if(rows[i].HiddenDraw) rows[i]=draw[at++];
        return rows;
    }
    private static int Compare(double[] a,double[] b)
    {
        if(a.Length!=b.Length) throw new BattleProjectionUnsupported("Selection card feature widths differ");
        for(int i=0;i<a.Length;i++) {int c=a[i].CompareTo(b[i]);if(c!=0) return c;}
        return 0;
    }
}

internal static class BattleChooseCardTiming
{
    // Native NChooseACardSelectionScreen.OnCardSelected rejects elapsed <=350ms.
    internal static bool Ready(ulong now,ulong opened) => now>=opened && now-opened>350;
}

// Display indices are one-based versions of the existing public native slots. They
// never participate in candidate identity, policy rows, action binding or scoring.
internal static class BattleDisplayLabels
{
    internal static string Card(string title,int handIndex,string? targetTitle,int targetIndex) =>
        $"{handIndex+1}: {title}"+Target(targetTitle,targetIndex);
    internal static string Potion(string title,int physicalSlot,string? targetTitle,int targetIndex) =>
        $"Potion {physicalSlot+1}: {title}"+Target(targetTitle,targetIndex);
    private static string Target(string? title,int index) => index<0 ? "" : $" → {index+1}: {title}";
}
