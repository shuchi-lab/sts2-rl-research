using System.Text.Json.Nodes;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Effects;
using Sts2Emulator.Core.Rng;
using Sts2Emulator.Core.Run;

namespace FullRunWorker;

/// <summary>One engine entry point invocation: <c>RunEngine.Step</c> or <c>UsePotionOutsideCombat</c>.</summary>
public sealed record EngineOp(bool OutsideCombatPotion, int Action, int Target = -1)
{
    public static EngineOp Step(int action) => new(false, action);
    public static EngineOp DrinkOutsideCombat(int slot) => new(true, slot);
}

/// <summary>
/// A displayed, meaningful strategic choice. Its id is its stable meaning (what it is, not where
/// it sits); <see cref="Ops" /> are the exact engine calls that commit it, executed atomically.
/// </summary>
public sealed class Candidate
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required List<EngineOp> Ops { get; init; }
    public JsonObject Public { get; init; } = [];
    /// <summary>Not offered: why (compatibility restriction, loop suppression).</summary>
    public string? Declined { get; set; }
    /// <summary>The displayed relic this choice hands over; checked against the engine result after the step.</summary>
    public int? ExpectRelicGain { get; init; }
    /// <summary>A deterministic compatible continuation (executed without a strategy decision).</summary>
    public string? Continuation { get; init; }
    /// <summary>
    /// Re-validated against the CURRENT state and a fresh mask before the first op: a non-null answer
    /// rejects the commit before any mutation (composite potion choices).
    /// </summary>
    public Func<RunState, int[], string?>? Precheck { get; init; }
    /// <summary>The slot that must hold this potion once every op has run (a potion actually taken).</summary>
    public (int Slot, int Potion)? ExpectSlotPotion { get; init; }
    /// <summary>A bridge-only answer (no engine call): this reward row is final-skipped on its screen.</summary>
    public RewardSuppression? Suppress { get; init; }
}

/// <summary>One reward row the bridge never opens or claims again on its screen (a skipped potion/card).</summary>
public sealed record RewardSuppression(string Kind, string Key);

/// <summary>
/// Meaningful strategic actions for every supported noncombat phase. Uses only the engine's own
/// public action mask and source-grounded event/page semantics; no candidate is fabricated, and an
/// empty supported list is an explicit unsupported state rather than a fallback action.
/// </summary>
public static class StrategicActions
{
    public static JsonObject CardJson(CardInstance c) => new()
    {
        ["def_id"] = Math.Abs(c.DefId),
        ["upgraded"] = c.Upgraded,
        ["enchantment"] = (int)c.Enchantment,
        ["enchant_amount"] = c.EnchantAmount,
        ["tinker_type"] = (int)c.TinkerType,
        ["tinker_rider"] = (int)c.TinkerRider,
    };

    private static string CardKey(CardInstance c) =>
        $"{Math.Abs(c.DefId)}.{(c.Upgraded ? 1 : 0)}.{(int)c.Enchantment}.{c.EnchantAmount}";

    public static List<Candidate> Generate(RunSession rs, int[] mask)
    {
        var s = rs.State;
        var list = new List<Candidate>();
        switch (s.Phase)
        {
            case RunPhase.Map:
                Map(rs, mask, list);
                break;
            case RunPhase.CardReward:
                CardReward(rs, mask, list);
                break;
            case RunPhase.RelicReward:
                RewardScreen(rs, mask, list);
                break;
            case RunPhase.Shop:
                Shop(rs, mask, list);
                break;
            case RunPhase.Rest:
                Rest(rs, mask, list);
                break;
            case RunPhase.Event:
                Event(rs, mask, list);
                break;
            case RunPhase.Ancient:
                Ancient(rs, mask, list);
                break;
            case RunPhase.TransformSelect:
                Select(rs, mask, list);
                break;
            case RunPhase.Treasure:
                Treasure(rs, mask, list);
                break;
            case RunPhase.CrystalSphere:
                Sphere(rs, mask, list);
                break;
            case RunPhase.BundleSelect:
                Bundle(rs, mask, list);
                break;
            case RunPhase.FakeMerchant:
                FakeMerchant(rs, mask, list);
                break;
            default:
                throw new UnsupportedRunState("unsupported_phase", $"no strategic action table for phase {s.Phase}");
        }
        // Out-of-combat drinking joins an already real non-reward choice where native use is legal.
        // It never turns a deterministic step into a decision, and never joins reward/card screens: a
        // full-belt reward carries its own legal use-and-accept choice.
        if (s.Phase is not (RunPhase.Shop or RunPhase.RelicReward or RunPhase.CardReward or RunPhase.Treasure)
            && list.Count(c => c.Declined is null) >= 2 && !list.Any(c => c.Continuation is not null))
            PotionsOutsideCombat(rs, list);
        return list;
    }

    /// <summary>Phases a reward screen (or a pickup follow-up over it) can be open in.</summary>
    public static bool IsRewardPhase(RunPhase p) =>
        p is RunPhase.RelicReward or RunPhase.CardReward or RunPhase.BundleSelect or RunPhase.TransformSelect;

    /// <summary>
    /// A card offer's identity on its reward screen. The engine copies offers (nested-screen capture and
    /// restore, the skip's reinsertion) and replaces them on relic updates, so reference identity is not
    /// stable; a card reward's subscription order is (it survives rerolls, copies and relic updates), and
    /// an offer that is not a card reward never receives updates, so its exact contents identify it.
    /// </summary>
    public static string OfferKey(CardRewardOffer o) =>
        o.SubscriptionOrder != 0
            ? $"s{o.SubscriptionOrder}"
            : "f" + string.Join(",", o.Cards.Select(CardKey)) + "+" + CardKey(o.BonusPowerCard);

    public static string SpecialKey(CardInstance c) => "special:" + CardKey(c);

    private static void Map(RunSession rs, int[] mask, List<Candidate> list)
    {
        var s = rs.State;
        for (int i = 0; i < RunConstants.MapChoices; i++)
        {
            if (mask[i] == 0)
                continue;
            var coord = s.MapOptionCoords[i] ?? throw new ContractViolation("map_option_without_coord", $"option {i}");
            var node = s.MapNodes[coord];
            list.Add(new Candidate
            {
                Id = $"map:c{coord.Col}r{coord.Row}",
                Kind = "map_node",
                Ops = [EngineOp.Step(i)],
                Public = new() { ["col"] = coord.Col, ["row"] = coord.Row, ["node_type"] = node.NodeType },
            });
        }
    }

    private static void CardReward(RunSession rs, int[] mask, List<Candidate> list)
    {
        var s = rs.State;
        var offer = rs.DisplayedOffer();
        for (int i = 0; i < RunConstants.CardRewardOptionCount; i++)
        {
            if (mask[i] == 0 || s.RewardCards[i] == 0)
                continue;
            var card = offer.Cards[i];
            if (Math.Abs(card.DefId) != Math.Abs(s.RewardCards[i]))
                throw new ContractViolation("card_reward_offer_mismatch", $"slot {i}: {card.DefId} vs {s.RewardCards[i]}");
            list.Add(new Candidate
            {
                Id = $"card:take:{i}:{CardKey(card)}",
                Kind = "card_take",
                Ops = [EngineOp.Step(i)],
                Public = new() { ["slot"] = i, ["card"] = CardJson(card) },
                Declined = rs.Compat.CardRestriction(card.DefId),
            });
        }
        if (mask[RunConstants.RewardFourthCardAction] != 0 && s.LastingCandyRewardCard != 0)
        {
            var card = offer.BonusPowerCard;
            list.Add(new Candidate
            {
                Id = $"card:fourth:{CardKey(card)}",
                Kind = "card_take",
                Ops = [EngineOp.Step(RunConstants.RewardFourthCardAction)],
                Public = new() { ["slot"] = 3, ["card"] = CardJson(card) },
                Declined = rs.Compat.CardRestriction(card.DefId),
            });
        }
        if (mask[RunConstants.RewardSacrificeAction] != 0)
            list.Add(new Candidate { Id = "card:sacrifice", Kind = "card_sacrifice", Ops = [EngineOp.Step(RunConstants.RewardSacrificeAction)] });
        if (mask[RunConstants.RewardRerollAction] != 0)
            list.Add(new Candidate { Id = "card:reroll", Kind = "card_reroll", Ops = [EngineOp.Step(RunConstants.RewardRerollAction)] });
        if (mask[RunConstants.RewardSkipAction] != 0)
            list.Add(new Candidate { Id = "card:skip", Kind = "card_skip", Ops = [EngineOp.Step(RunConstants.RewardSkipAction)] });
    }

    /// <summary>
    /// One populated reward row in the engine's claim order (<c>ClaimRewardAtIndex</c>: gold, potion,
    /// relic, special card, card offers). Rows reindex after every claim, so they are recomputed from the
    /// state at every step.
    /// </summary>
    public sealed record RewardRow(string Kind, int Row, int Action, int Ordinal, int Value, CardInstance Card,
        CardRewardOffer? Offer);

    public static List<RewardRow> RewardRows(RunSession rs, int[] mask)
    {
        var s = rs.State;
        var rows = new List<RewardRow>();
        int index = 0;
        void Add(string kind, int ordinal, int value = 0, CardInstance card = default, CardRewardOffer? offer = null)
        {
            int action = RunConstants.RewardItemAction(index);
            if (action < 0 || mask[action] == 0)
                throw new ContractViolation("reward_row_not_in_mask", $"{kind} {ordinal} (row {index}, action {action})");
            rows.Add(new RewardRow(kind, index, action, ordinal, value, card, offer));
            index++;
        }
        int n = 0;
        if (s.RewardGold != 0)
            Add("gold", n++, s.RewardGold);
        foreach (int g in s.PendingGoldRewards)
            Add("gold", n++, g);
        n = 0;
        if (s.RewardPotion != 0)
            Add("potion", n++, s.RewardPotion);
        foreach (int p in s.PendingPotionRewards)
            Add("potion", n++, p);
        n = 0;
        if (s.RelicReward != 0)
            Add("relic", n++, s.RelicReward);
        foreach (int r in s.PendingBonusRelicRewards)
            Add("relic", n++, r);
        for (int i = 0; i < s.PendingSpecialCardRewards.Count; i++)
            Add("special", i, card: s.PendingSpecialCardRewards[i]);
        n = 0;
        if (s.RewardCardPending)
            Add("cards", n++, offer: rs.DisplayedOffer());
        foreach (var o in s.PendingCardOffers)
            Add("cards", n++, offer: o);
        return rows;
    }

    /// <summary>
    /// The reward screen, normalized: never a model menu. In order -- every native gold row, every
    /// compatible relic row (a pickup's follow-up screen is the next real decision), each incoming
    /// compatible potion (automatic into a free usable slot; a full belt is ONE complete decision), a
    /// stolen-back special card (take / skip), then each card offer opened automatically -- and finally
    /// the screen's own native leave. Each automatic step is a single candidate carrying its
    /// <see cref="Candidate.Continuation" /> reason; declined rows are listed for the evidence log.
    /// </summary>
    private static void RewardScreen(RunSession rs, int[] mask, List<Candidate> list)
    {
        var s = rs.State;
        var rows = RewardRows(rs, mask);
        Candidate Auto(string id, string kind, int action, string why, JsonObject pub, (int, int)? expect = null) => new()
        {
            Id = id, Kind = kind, Ops = [EngineOp.Step(action)], Public = pub, Continuation = why, ExpectSlotPotion = expect,
        };

        foreach (var g in rows.Where(r => r.Kind == "gold"))
        {
            list.Add(Auto($"reward:gold:{g.Ordinal}:{g.Value}", "reward_gold", g.Action, "gold_reward", new() { ["gold"] = g.Value }));
            return;
        }

        foreach (var r in rows.Where(r => r.Kind == "relic"))
        {
            string id = $"reward:relic:{r.Ordinal}:{r.Value}";
            string? why = rs.Compat.RelicRestriction(s, r.Value, ancient: false) ?? rs.Compat.NeowsBonesRowRestriction(s, r.Value);
            if (why is not null)
            {
                list.Add(new Candidate { Id = id, Kind = "reward_relic", Ops = [EngineOp.Step(r.Action)], Declined = why,
                    Public = new() { ["relic_id"] = r.Value } });
                continue;
            }
            list.Add(Auto(id, "reward_relic", r.Action, "compatible_relic_reward", new() { ["relic_id"] = r.Value }));
            return;
        }

        var skippedPotions = rs.SuppressedNow("potion");
        foreach (var r in rows.Where(r => r.Kind == "potion"))
        {
            int p = r.Value;
            string key = p.ToString();
            if (skippedPotions.GetValueOrDefault(key) > 0)
            {
                skippedPotions[key]--;
                continue;
            }
            string? why = rs.Compat.PotionRestriction(p);
            if (why is not null)
            {
                list.Add(new Candidate { Id = $"reward:potion:{r.Ordinal}:{p}", Kind = "reward_potion", Ops = [EngineOp.Step(r.Action)],
                    Public = new() { ["potion_id"] = p }, Declined = why });
                continue;
            }
            PotionReward(rs, mask, r, list);
            return;
        }

        var skippedCards = rs.SuppressedNow("card");
        foreach (var r in rows.Where(r => r.Kind == "special"))
        {
            string key = SpecialKey(r.Card);
            if (skippedCards.GetValueOrDefault(key) > 0)
            {
                skippedCards[key]--;
                continue;
            }
            string? why = rs.Compat.CardRestriction(r.Card.DefId);
            var take = new Candidate
            {
                Id = $"reward:special:{r.Ordinal}:{CardKey(r.Card)}", Kind = "reward_special_card", Ops = [EngineOp.Step(r.Action)],
                Public = new() { ["card"] = CardJson(r.Card) }, Declined = why,
            };
            list.Add(take);
            var skip = new Candidate
            {
                Id = $"special:skip:{CardKey(r.Card)}", Kind = "card_skip", Ops = [], Public = new() { ["card"] = CardJson(r.Card) },
                Suppress = new RewardSuppression("card", key), Continuation = why is null ? null : "special_card_outside_roster",
            };
            list.Add(skip);
            return;
        }

        foreach (var r in rows.Where(r => r.Kind == "cards"))
        {
            string key = OfferKey(r.Offer!);
            if (skippedCards.GetValueOrDefault(key) > 0)
            {
                skippedCards[key]--;
                continue;
            }
            list.Add(Auto($"reward:cards:{r.Ordinal}:{key}", "reward_open_cards", r.Action, "open_card_offer", new() { ["row"] = r.Ordinal }));
            return;
        }

        if (mask[RunConstants.RewardSkipAction] != 0)
            list.Add(new Candidate
            {
                Id = "reward:leave", Kind = "reward_leave", Ops = [EngineOp.Step(RunConstants.RewardSkipAction)],
                Continuation = "reward_screen_answered",
            });
    }

    /// <summary>
    /// One incoming compatible potion row. Sozu: the native claim consumes the row without a gain. A free
    /// USABLE slot (within <c>MaxPotionSlots</c>): taken automatically. A full belt: ONE decision -- replace
    /// any legal occupied slot (native discard then claim), skip only this incoming potion (bridge row
    /// suppression; the native skip would leave the whole screen), or drink a held potion that has a
    /// native out-of-combat use and accept (native use then claim). A locked belt can only skip.
    /// </summary>
    private static void PotionReward(RunSession rs, int[] mask, RewardRow r, List<Candidate> list)
    {
        var s = rs.State;
        int p = r.Value;
        string rowId = $"reward:potion:{r.Ordinal}:{p}";
        if (s.Relics.Any(x => x.DefId == RelicEffects.Sozu))
        {
            list.Add(new Candidate { Id = rowId, Kind = "reward_potion", Ops = [EngineOp.Step(r.Action)],
                Public = new() { ["potion_id"] = p }, Continuation = "sozu_consumes_row_without_gain" });
            return;
        }
        int usable = Math.Min(s.MaxPotionSlots, s.PotionSlots.Length);
        int free = Array.IndexOf(s.PotionSlots, 0, 0, usable);
        if (free >= 0)
        {
            list.Add(new Candidate { Id = rowId, Kind = "reward_potion", Ops = [EngineOp.Step(r.Action)],
                Public = new() { ["potion_id"] = p, ["slot"] = free }, Continuation = "free_usable_slot",
                ExpectSlotPotion = (free, p) });
            return;
        }
        var skip = new Candidate
        {
            Id = $"potion:skip:{p}", Kind = "potion_skip_incoming", Ops = [], Public = new() { ["potion_id"] = p },
            Suppress = new RewardSuppression("potion", p.ToString()),
            Continuation = s.CanUseOrRemovePotions ? null : "belt_locked",
        };
        if (!s.CanUseOrRemovePotions)
        {
            list.Add(skip);
            return;
        }
        int row = r.Row;
        string? SameRow(RunState st, int[] m, int slot, int held, int action)
        {
            var now = RewardRows(rs, m);
            if (row >= now.Count || now[row].Kind != "potion" || now[row].Value != p || now[row].Action != r.Action)
                return $"potion row {row} is no longer {p}";
            if (st.PotionSlots[slot] != held)
                return $"slot {slot} no longer holds {held}";
            return action >= 0 && m[action] == 0 ? $"action {action} not in mask" : null;
        }
        for (int slot = 0; slot < usable; slot++)
        {
            int held = s.PotionSlots[slot];
            int discard = RunConstants.RewardPotionDiscardAction(slot);
            int k = slot;
            if (discard >= 0 && mask[discard] != 0)
                list.Add(new Candidate
                {
                    Id = $"potion:replace:{slot}:{held}:{p}", Kind = "potion_replace",
                    Ops = [EngineOp.Step(discard), EngineOp.Step(r.Action)],
                    Public = new() { ["slot"] = slot, ["potion_id"] = p, ["held_potion_id"] = held },
                    Precheck = (st, m) => SameRow(st, m, k, held, discard),
                    ExpectSlotPotion = (slot, p),
                });
            if (CanDrinkOutsideCombat(s, slot))
                list.Add(new Candidate
                {
                    Id = $"potion:use_accept:{slot}:{held}:{p}", Kind = "potion_use_accept",
                    Ops = [EngineOp.DrinkOutsideCombat(slot), EngineOp.Step(r.Action)],
                    Public = new() { ["slot"] = slot, ["potion_id"] = p, ["held_potion_id"] = held },
                    Precheck = (st, m) => SameRow(st, m, k, held, -1) ?? (CanDrinkOutsideCombat(st, k) ? null : $"slot {k} not drinkable"),
                    ExpectSlotPotion = (slot, p),
                });
        }
        list.Add(skip);
    }

    /// <summary>Exactly <c>RunEngine.UsePotionOutsideCombat</c>'s acceptance test (it returns -1 otherwise).</summary>
    public static bool CanDrinkOutsideCombat(RunState s, int slot) =>
        s.Phase is not (RunPhase.Combat or RunPhase.Complete) && s.PlayerHp > 0 && s.CanUseOrRemovePotions
        && (uint)slot < (uint)Math.Min(s.MaxPotionSlots, s.PotionSlots.Length)
        && RunNonCombatEffects.HasOutOfCombatUse(s.PotionSlots[slot]);

    private static void Shop(RunSession rs, int[] mask, List<Candidate> list)
    {
        var s = rs.State;
        for (int i = 0; i < s.ShopCards.Length; i++)
        {
            if (mask[i] == 0)
                continue;
            list.Add(new Candidate
            {
                Id = $"shop:card:{i}:{s.ShopCards[i]}",
                Kind = "shop_card",
                Ops = [EngineOp.Step(i)],
                Public = new() { ["slot"] = i, ["card"] = CardJson(new CardInstance(s.ShopCards[i], false)), ["cost"] = s.ShopCosts[i] },
                Declined = rs.Compat.CardRestriction(s.ShopCards[i]),
            });
        }
        for (int action = 7; action < 10; action++)
        {
            if (mask[action] == 0)
                continue;
            int relic = s.ShopRelics[action - 7];
            list.Add(new Candidate
            {
                Id = $"shop:relic:{action - 7}:{relic}",
                Kind = "shop_relic",
                Ops = [EngineOp.Step(action)],
                Public = new() { ["slot"] = action - 7, ["relic_id"] = relic, ["cost"] = s.ShopCosts[action] },
                Declined = rs.Compat.RelicRestriction(s, relic, ancient: false),
            });
        }
        // The native mask admits a potion buy on stock, gold and a free usable slot, but the purchase is
        // RunRewardGenerator.AddPotion, which refuses every potion while Sozu is held (Step returns -1).
        string? procure = RelicEffects.Has(s.Relics, RelicEffects.Sozu) ? "sozu_refuses_potion_procure" : null;
        for (int action = 10; action < 13; action++)
        {
            if (mask[action] == 0)
                continue;
            int potion = s.ShopPotions[action - 10];
            list.Add(new Candidate
            {
                Id = $"shop:potion:{action - 10}:{potion}",
                Kind = "shop_potion",
                Ops = [EngineOp.Step(action)],
                Public = new() { ["slot"] = action - 10, ["potion_id"] = potion, ["cost"] = s.ShopCosts[action] },
                Declined = rs.Compat.PotionRestriction(potion) ?? procure,
            });
        }
        if (mask[RunConstants.ShopRemoveAction] != 0)
            list.Add(new Candidate
            {
                Id = "shop:remove",
                Kind = "shop_remove",
                Ops = [EngineOp.Step(RunConstants.ShopRemoveAction)],
                Public = new() { ["cost"] = s.ShopCosts[RunConstants.ShopRemoveAction] },
            });
        for (int slot = 0; slot < RunConstants.PotionSlotCapacity; slot++)
        {
            int use = RunConstants.ShopPotionUseAction(slot);
            if (mask[use] != 0)
                list.Add(new Candidate
                {
                    Id = $"potion:use:{slot}:{s.PotionSlots[slot]}",
                    Kind = "potion_use",
                    Ops = [EngineOp.Step(use)],
                    Public = new() { ["slot"] = slot, ["potion_id"] = s.PotionSlots[slot] },
                });
            int discard = RunConstants.ShopPotionDiscardAction(slot);
            if (mask[discard] != 0)
                list.Add(new Candidate
                {
                    Id = $"potion:discard:{slot}:{s.PotionSlots[slot]}",
                    Kind = "potion_discard",
                    Ops = [EngineOp.Step(discard)],
                    Public = new() { ["slot"] = slot, ["potion_id"] = s.PotionSlots[slot] },
                });
        }
        if (mask[RunConstants.ShopSkipAction] != 0)
            list.Add(new Candidate { Id = "shop:leave", Kind = "shop_leave", Ops = [EngineOp.Step(RunConstants.ShopSkipAction)] });
    }

    private static readonly (int Action, string Name)[] RestOptions =
    [
        (RunConstants.RestHealAction, "heal"), (RunConstants.RestCloneAction, "clone"), (RunConstants.RestLiftAction, "lift"),
        (RunConstants.RestDigAction, "dig"), (RunConstants.RestHatchAction, "hatch"), (RunConstants.RestCookAction, "cook"),
        (RunConstants.RestKindleAction, "kindle"),
    ];

    private static void Rest(RunSession rs, int[] mask, List<Candidate> list)
    {
        var s = rs.State;
        if (s.RestResultPending)
        {
            // The option was taken and the visit is over; the screen's one remaining button proceeds.
            list.Add(new Candidate { Id = "rest:proceed", Kind = "proceed", Ops = [EngineOp.Step(RunConstants.RewardSkipAction)] });
            return;
        }
        foreach (var (action, name) in RestOptions)
        {
            if (mask[action] != 0)
                list.Add(new Candidate
                {
                    Id = $"rest:{name}",
                    Kind = $"rest_{name}",
                    Ops = [EngineOp.Step(action)],
                    Public = name == "heal" ? new() { ["heal"] = RestHeal(s) } : [],
                });
        }
        if (mask[RunConstants.RestUpgradeAction] != 0)
        {
            // Smith is one committed choice: open the smith screen and upgrade this card. The
            // screen's cancel (back to an unspent rest site) is never offered.
            var ids = rs.Deck.Ids;
            for (int i = 0; i < s.Deck.Count; i++)
            {
                if (!RunConstants.IsRunCardUpgradable(s.Deck[i]))
                    continue;
                list.Add(new Candidate
                {
                    Id = $"rest:smith:{ids[i]}",
                    Kind = "rest_smith",
                    Ops = [EngineOp.Step(RunConstants.RestUpgradeAction), EngineOp.Step(i)],
                    Public = new() { ["instance_id"] = ids[i], ["card"] = CardJson(s.Deck[i]) },
                });
            }
        }
        if (mask[RunConstants.RewardSkipAction] != 0)
            list.Add(new Candidate { Id = "rest:leave", Kind = "rest_leave", Ops = [EngineOp.Step(RunConstants.RewardSkipAction)] });
    }

    private static int RestHeal(RunState s) =>
        (int)(s.PlayerMaxHp * 0.3m + (RelicEffects.Has(s.Relics, RelicEffects.RegalPillow) ? RelicEffects.RegalPillowRestHeal : 0));

    private static void Event(RunSession rs, int[] mask, List<Candidate> list)
    {
        var s = rs.State;
        var options = EventOptions.RealOptions(s, mask);
        if (s.EventId == RunConstants.EventResultPending)
        {
            foreach (int a in options)
                list.Add(new Candidate { Id = "event:proceed", Kind = "proceed", Ops = [EngineOp.Step(a)] });
            return;
        }
        var spec = EventOptions.Table[s.EventId];
        foreach (int option in options)
        {
            var pub = new JsonObject { ["event_id"] = s.EventId, ["page"] = s.EventPage, ["option"] = option };
            int? fight = EventOptions.FightOf(spec, s.EventPage, option);
            if (fight is not null)
                pub["starts_fight"] = true;
            var (hp, maxHp) = EventOptions.HpCostOf(spec, s.EventPage, option);
            int gold = EventOptions.GoldCostOf(spec, s.EventPage, option);
            gold = DisplayedEventGold(s, option) ?? gold;
            if (hp != 0)
                pub["hp_cost"] = hp;
            if (maxHp != 0)
                pub["max_hp_cost"] = maxHp;
            if (gold != 0)
                pub["gold_cost"] = gold;
            AddDisplayedMeaning(rs, option, pub);
            string? declined = rs.Compat.EventOptionRestriction(s.EventId, s.EventPage, option);
            var grants = EventOptions.GrantsOf(spec, s.EventPage, option);
            if (grants.Length > 0)
            {
                var arr = new JsonArray();
                foreach (var g in grants)
                {
                    int id = RunNonCombatEffects.NamedCard(g);
                    arr.Add(id);
                    declined ??= rs.Compat.CardSupported(id) ? null : $"forced_grant_outside_roster:{g}";
                }
                pub["grants_cards"] = arr;
            }
            string? continuation = s.EventId == RunConstants.EventColorfulPhilosophers && option == options[0]
                ? "colorful_philosophers_open_first_pool_then_decline_foreign_rewards"
                : null;
            if (s.EventId == RunConstants.EventColorfulPhilosophers && continuation is null)
                declined ??= "colorful_philosophers_pool_alternative_not_needed";
            int? doll = s.EventId == RunConstants.EventDollRoom && s.EventPage > 0 ? (int?)pub["relic_id"] : null;
            if (s.EventId == RunConstants.EventDollRoom && s.EventPage > 0 && doll is null)
                throw new ContractViolation("doll_room_choice_without_displayed_doll", $"page {s.EventPage} option {option}");
            if (doll is int d)
                declined ??= rs.Compat.RelicRestriction(s, d, ancient: false);
            list.Add(new Candidate
            {
                Id = doll is int dd ? $"event:{s.EventId}:p{s.EventPage}:o{option}:r{dd}" : $"event:{s.EventId}:p{s.EventPage}:o{option}",
                Kind = s.EventId == RunConstants.EventTheArchitect ? "proceed" : "event_option",
                Ops = [EngineOp.Step(option)],
                Public = pub,
                Declined = declined,
                Continuation = continuation,
                ExpectRelicGain = doll,
            });
        }
    }

    /// <summary>
    /// What an option displays when that varies from run to run, read from the values
    /// <c>CalculateEventVars</c> fixed when the event opened (cached; never a lazy roll -- the projection's RNG
    /// witness would catch one). Page-specific; latent values on unrelated pages are never read. Doll Room's
    /// page-1/2 dolls come from <see cref="DollRoomDisplayedDolls" /> (a pure preview on a cloned stream).
    /// </summary>
    private static void AddDisplayedMeaning(RunSession rs, int option, JsonObject pub)
    {
        var s = rs.State;
        switch (s.EventId)
        {
            case RunConstants.EventDollRoom when s.EventPage > 0:
            {
                var dolls = DollRoomDisplayedDolls(s);
                if ((uint)option < (uint)dolls.Count)
                    pub["relic_id"] = dolls[option];
                break;
            }
            case RunConstants.EventSlipperyBridge:
                if (option == 0 && s.EventValue0 is int bridge && (uint)bridge < (uint)s.Deck.Count)
                {
                    pub["card"] = CardJson(s.Deck[bridge]);
                    pub["instance_id"] = rs.Deck.Ids[bridge];
                    pub["removes_card"] = true;
                }
                if (option == 1)
                    pub["hp_cost"] = RunNonCombatEffects.SlipperyBridgeHpLoss(s);
                break;
            case RunConstants.EventRanwidTheElder:
                if (option == 0 && s.EventValue1 is int pslot && (uint)pslot < (uint)s.PotionSlots.Length)
                    pub["potion_id"] = s.PotionSlots[pslot];
                if (option == 2 && s.EventValue0 is int rix && (uint)rix < (uint)s.Relics.Count)
                    pub["gives_relic_id"] = s.Relics[rix].DefId;
                break;
            case RunConstants.EventRelicTrader:
                if (s.EventRelicStock is { } stock && s.EventRelicOffers is { } offers && option < stock.Count
                    && option < offers.Count && (uint)stock[option] < (uint)s.Relics.Count)
                {
                    pub["gives_relic_id"] = s.Relics[stock[option]].DefId;
                    pub["relic_id"] = offers[option];
                }
                break;
            case RunConstants.EventWelcomeToWongos:
                if (option == 1 && s.EventValue1 is int featured)
                    pub["relic_id"] = featured;
                break;
            case RunConstants.EventTheFutureOfPotions:
            {
                int seen = -1;
                for (int i = 0; i < s.PotionSlots.Length; i++)
                {
                    if (s.PotionSlots[i] == 0 || ++seen != option)
                        continue;
                    pub["potion_id"] = s.PotionSlots[i];
                    if (s.EventRandomOffer.Length == s.PotionSlots.Length)
                        pub["card_type"] = s.EventRandomOffer[i];
                    break;
                }
                break;
            }
            case RunConstants.EventTinkerTime when s.EventPage is 1 or 2:
                if ((uint)option < (uint)s.EventRandomOffer.Length)
                    pub[s.EventPage == 1 ? "card_type" : "tinker_rider"] = s.EventRandomOffer[option];
                if (s.EventPage == 2)
                    pub["chosen_card_type"] = (int)s.TinkerCardType;
                break;
            case RunConstants.EventEndlessConveyor:
                if (option == 0 && s.EventValue0 is int dish)
                    pub["dish"] = dish;
                break;
            case RunConstants.EventColossalFlower:
                if ((uint)s.EventPage < (uint)RunConstants.ColossalFlowerPrizes.Length)
                {
                    if (option == 0)
                        pub["gold_gain"] = RunConstants.ColossalFlowerPrizes[s.EventPage];
                    if (option == 1)
                        pub["hp_cost"] = RunConstants.ColossalFlowerDigDamage[s.EventPage];
                }
                break;
            case RunConstants.EventAbyssalBaths:
                if (option == 0)
                    pub["hp_cost"] = RunNonCombatEffects.AbyssalBathsDamage(s);
                break;
            case RunConstants.EventTabletOfTruth:
                if (option == 0)
                    pub["max_hp_cost"] = RunNonCombatEffects.TabletOfTruthCost(s);
                break;
            case RunConstants.EventSunkenTreasury when s.EventValue0 is not null && s.EventValue1 is not null:
                pub["gold_gain"] = option == 0 ? RunNonCombatEffects.SunkenTreasurySmallChestGold(s)
                    : RunNonCombatEffects.SunkenTreasuryLargeChestGold(s);
                break;
            case RunConstants.EventSunkenStatue when option == 1 && s.EventValue0 is not null:
                pub["gold_gain"] = RunNonCombatEffects.SunkenStatueGold(s);
                break;
            case RunConstants.EventDenseVegetation when option == 0 && s.EventPage == 0 && s.EventValue0 is not null:
                pub["gold_gain"] = RunNonCombatEffects.DenseVegetationGold(s);
                break;
            case RunConstants.EventThisOrThat when option == 0 && s.EventValue0 is not null:
                pub["gold_gain"] = RunNonCombatEffects.ThisOrThatGold(s);
                break;
            case RunConstants.EventJungleMazeAdventure when s.EventValue0 is not null && s.EventValue1 is not null:
                pub["gold_gain"] = option == 0 ? RunNonCombatEffects.JungleMazeSoloGold(s)
                    : RunNonCombatEffects.JungleMazeJoinForcesGold(s);
                break;
        }
    }

    /// <summary>
    /// The dolls Doll Room's page 1 (two) / page 2 (three) puts in front of the player, in option order.
    /// The engine shuffles them inside the step handler (<c>RunNonCombatEffects.DollRoomOffer</c> on the
    /// event's persistent <c>DOLL_ROOM</c> stream), so this runs that same engine function on a CLONE of the
    /// stream (Snapshot/Restore; or the fresh deterministic stream the handler would derive when none is
    /// cached) and puts the state's own stream references back: no draw, no state change, and the result
    /// is exactly what the handler computes for the next step from the same stream. Each pick is checked
    /// against the relic actually gained (<see cref="Candidate.ExpectRelicGain" />).
    /// </summary>
    public static List<int> DollRoomDisplayedDolls(RunState s)
    {
        const string entry = "DOLL_ROOM";
        var stream = s.EventRngStream;
        var name = s.EventRngName;
        GameRng? clone = null;
        if (stream is not null && name == entry)
        {
            clone = new GameRng(stream.RawSeed, stream.Algorithm);
            clone.Restore(stream.Snapshot());
        }
        try
        {
            s.EventRngStream = clone;
            s.EventRngName = clone is null ? null : entry;
            // the algorithm context RunSession.Execute gives the handler (used for a freshly derived stream)
            using var scope = RngAlgorithmContext.Use(RngAlgorithm.GameXoshiro256StarStar);
            return RunNonCombatEffects.DollRoomOffer(s, s.EventPage == 1 ? 2 : 3);
        }
        finally
        {
            s.EventRngStream = stream;
            s.EventRngName = name;
        }
    }

    /// <summary>Prices the option text itself displays (rolled when the event opened, cached in EventValue0/1).</summary>
    private static int? DisplayedEventGold(RunState s, int option) => s.EventId switch
    {
        RunConstants.EventLuminousChoir when option == 1 => s.EventValue0,
        RunConstants.EventCrystalSphere when option == 0 && s.CrystalSphereRng is not null => s.EventValue0,
        RunConstants.EventWhisperingHollow when option == 0 => s.EventValue0,
        RunConstants.EventRanwidTheElder when option == 1 => 100,
        _ => null,
    };

    private static void Ancient(RunSession rs, int[] mask, List<Candidate> list)
    {
        var s = rs.State;
        if (s.NeowAwaitingProceed)
        {
            if (mask[0] != 0)
                list.Add(new Candidate { Id = "ancient:proceed", Kind = "proceed", Ops = [EngineOp.Step(0)] });
            return;
        }
        for (int i = 0; i < s.NeowOptions.Length; i++)
        {
            if (mask[i] == 0)
                continue;
            int relic = s.NeowOptions[i];
            list.Add(new Candidate
            {
                Id = $"ancient:{relic}",
                Kind = "ancient_relic",
                Ops = [EngineOp.Step(i)],
                Public = new() { ["relic_id"] = relic, ["slot"] = i },
                Declined = rs.Compat.RelicRestriction(s, relic, ancient: true),
            });
        }
    }

    private static void Select(RunSession rs, int[] mask, List<Candidate> list)
    {
        var s = rs.State;
        if (s.PendingOfferCards.Length > 0)
        {
            for (int i = 0; i < s.PendingOfferCards.Length; i++)
            {
                if (mask[i] == 0)
                    continue;
                var card = s.PendingOfferInstances.Length == s.PendingOfferCards.Length
                    ? s.PendingOfferInstances[i]
                    : new CardInstance(s.PendingOfferCards[i], false);
                list.Add(new Candidate
                {
                    Id = $"grid:{i}:{CardKey(card)}",
                    Kind = "grid_card",
                    Ops = [EngineOp.Step(i)],
                    Public = new() { ["slot"] = i, ["card"] = CardJson(card) },
                    Declined = rs.Compat.CardRestriction(card.DefId),
                });
            }
            if (s.PendingOfferOptional && mask[s.PendingOfferCards.Length] != 0)
                list.Add(new Candidate { Id = "grid:done", Kind = "grid_done", Ops = [EngineOp.Step(s.PendingOfferCards.Length)] });
            return;
        }
        if (s.PendingRestUpgrade)
        {
            // Reached only by a path other than the composite smith (never by the bridge itself).
            var ids = rs.Deck.Ids;
            for (int i = 0; i < s.Deck.Count; i++)
                if (mask[i] != 0)
                    list.Add(new Candidate
                    {
                        Id = $"select:{ids[i]}",
                        Kind = "select_card",
                        Ops = [EngineOp.Step(i)],
                        Public = new() { ["instance_id"] = ids[i], ["card"] = CardJson(s.Deck[i]) },
                    });
            return;
        }
        if (s.PendingSelectionKind != DeckSelection.None)
        {
            var ids = rs.Deck.Ids;
            var eligible = rs.SelectionEligible(s);
            for (int i = 0; i < s.Deck.Count; i++)
            {
                if (mask[i] == 0)
                    continue;
                bool original = eligible.Contains(ids[i]);
                list.Add(new Candidate
                {
                    Id = $"select:{ids[i]}",
                    Kind = "select_card",
                    Ops = [EngineOp.Step(i)],
                    Public = new() { ["instance_id"] = ids[i], ["card"] = CardJson(s.Deck[i]) },
                    Declined = original ? null : "not_an_original_selection_candidate",
                });
            }
            return;
        }
        if (s.TransformSelectedDeckIndex < 0)
        {
            // Astrolabe / Empty Cage's pickup continuation: any action resumes it; the screen shows one.
            if (mask[0] != 0)
                list.Add(new Candidate { Id = "select:continue", Kind = "proceed", Ops = [EngineOp.Step(0)] });
            return;
        }
        throw new UnsupportedRunState("legacy_transform_select",
            $"TransformSelect with no pending selection/offer/smith (TransformSelectedDeckIndex {s.TransformSelectedDeckIndex})");
    }

    private static void Treasure(RunSession rs, int[] mask, List<Candidate> list)
    {
        var s = rs.State;
        if (!s.TreasureChestOpened)
        {
            if (mask[RunConstants.TreasureOpenAction] != 0)
                list.Add(new Candidate { Id = "treasure:open", Kind = "treasure_open", Ops = [EngineOp.Step(RunConstants.TreasureOpenAction)] });
            return;
        }
        if (mask[0] != 0 && s.RelicReward != 0)
        {
            // A free chest relic is bookkeeping: taken when compatible (its follow-up, if any, is real).
            string? why = rs.Compat.RelicRestriction(s, s.RelicReward, ancient: false);
            list.Add(new Candidate
            {
                Id = $"treasure:relic:{s.RelicReward}",
                Kind = "treasure_relic",
                Ops = [EngineOp.Step(0)],
                Public = new() { ["relic_id"] = s.RelicReward },
                Declined = why,
                Continuation = why is null ? "compatible_treasure_relic" : null,
            });
            if (why is null)
                return;
        }
        if (mask[RunConstants.RewardSkipAction] != 0)
            list.Add(new Candidate { Id = "treasure:leave", Kind = "treasure_leave", Ops = [EngineOp.Step(RunConstants.RewardSkipAction)] });
    }

    private static void Sphere(RunSession rs, int[] mask, List<Candidate> list)
    {
        for (int cell = 0; cell < RunConstants.CrystalSphereCells; cell++)
        {
            int x = cell / RunConstants.CrystalSphereSize, y = cell % RunConstants.CrystalSphereSize;
            if (mask[cell] != 0)
                list.Add(new Candidate
                {
                    Id = $"sphere:big:{x}:{y}",
                    Kind = "sphere_cell",
                    Ops = [EngineOp.Step(cell)],
                    Public = new() { ["x"] = x, ["y"] = y, ["tool"] = "big" },
                });
            int small = RunConstants.CrystalSphereSmallToolAction + cell;
            if (mask[small] != 0)
                list.Add(new Candidate
                {
                    Id = $"sphere:small:{x}:{y}",
                    Kind = "sphere_cell",
                    Ops = [EngineOp.Step(small)],
                    Public = new() { ["x"] = x, ["y"] = y, ["tool"] = "small" },
                });
        }
    }

    private static void Bundle(RunSession rs, int[] mask, List<Candidate> list)
    {
        var s = rs.State;
        // Engine invariant (two bundles of exactly three cards): anything else is never clipped or padded.
        if (s.BundleOffer.Length != 6)
            throw new ContractViolation("bundle_offer_shape", $"BundleOffer has {s.BundleOffer.Length} cards, expected 2x3");
        for (int b = 0; b < 2; b++)
        {
            if (mask[b] == 0)
                continue;
            var cards = new JsonArray();
            string? declined = null;
            for (int i = 0; i < 3; i++)
            {
                int id = s.BundleOffer[b * 3 + i];
                cards.Add(CardJson(new CardInstance(id, false)));
                declined ??= rs.Compat.CardRestriction(id);
            }
            // Highlight then confirm is one committed choice: the highlight changes nothing else.
            list.Add(new Candidate
            {
                Id = $"bundle:{b}",
                Kind = "bundle",
                Ops = [EngineOp.Step(b), EngineOp.Step(RunConstants.BundleConfirmAction)],
                Public = new() { ["bundle"] = b, ["cards"] = cards },
                Declined = declined,
            });
        }
    }

    private static void FakeMerchant(RunSession rs, int[] mask, List<Candidate> list)
    {
        var s = rs.State;
        var stall = s.EventRelicStock ?? throw new ContractViolation("fake_merchant_without_stock", "stall not rolled");
        for (int i = 0; i < stall.Count && i < RunConstants.FakeMerchantThrowAction; i++)
        {
            if (mask[i] == 0)
                continue;
            list.Add(new Candidate
            {
                Id = $"fm:relic:{i}:{stall[i]}",
                Kind = "shop_relic",
                Ops = [EngineOp.Step(i)],
                Public = new() { ["slot"] = i, ["relic_id"] = stall[i], ["cost"] = s.FakeMerchantPrices![i] },
                Declined = rs.Compat.RelicRestriction(s, stall[i], ancient: false),
            });
        }
        if (mask[RunConstants.FakeMerchantThrowAction] != 0)
            list.Add(new Candidate
            {
                Id = "fm:throw",
                Kind = "event_option",
                Ops = [EngineOp.Step(RunConstants.FakeMerchantThrowAction)],
                Public = new() { ["event_id"] = RunConstants.EventFakeMerchant, ["page"] = 0, ["option"] = RunConstants.FakeMerchantThrowAction, ["starts_fight"] = true },
            });
        if (mask[RunConstants.FakeMerchantLeaveAction] != 0)
            list.Add(new Candidate { Id = "fm:leave", Kind = "shop_leave", Ops = [EngineOp.Step(RunConstants.FakeMerchantLeaveAction)] });
    }

    /// <summary>
    /// <c>RunEngine.UsePotionOutsideCombat</c>, offered wherever the engine accepts it: alive, outside
    /// Combat/Complete, belt unlocked, an existing slot holding a potion with a modelled out-of-combat use.
    /// </summary>
    private static void PotionsOutsideCombat(RunSession rs, List<Candidate> list)
    {
        var s = rs.State;
        if (s.Phase is RunPhase.Combat or RunPhase.Complete || s.PlayerHp <= 0 || !s.CanUseOrRemovePotions)
            return;
        for (int slot = 0; slot < Math.Min(s.MaxPotionSlots, s.PotionSlots.Length); slot++)
        {
            if (!CanDrinkOutsideCombat(s, slot))
                continue;
            list.Add(new Candidate
            {
                Id = $"potion:use:{slot}:{s.PotionSlots[slot]}",
                Kind = "potion_use",
                Ops = [EngineOp.DrinkOutsideCombat(slot)],
                Public = new() { ["slot"] = slot, ["potion_id"] = s.PotionSlots[slot] },
            });
        }
    }
}
