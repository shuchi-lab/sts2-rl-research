using System.Text.Json;
using CombatWorker;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Run;

namespace FullRunWorker;

/// <summary>
/// The strategy agent's observation: an explicit allow-list of what the player can see. It never
/// serializes <c>RunState</c>: no seed string, RNG stream, <c>RunMapNode.EncounterId</c>,
/// <c>MapChoices</c>, encounter/event sequences, relic bags, reward odds, unopened card offers,
/// pending treasure, hidden Crystal Sphere cells, draw order or latent event values. Collections
/// are variable length and complete; nothing is truncated to a fixed width.
/// </summary>
public static class PublicProjection
{
    public const string Schema = "rs1/public_run_observation/v2";

    /// <summary>CardInstance properties a permanent deck card may carry; anything else is unrepresented.</summary>
    private static readonly HashSet<string> DeckCardFields =
        ["DefId", "Upgraded", "Enchantment", "EnchantAmount", "TinkerType", "TinkerRider", "DeckOrigin"];

    private static readonly System.Reflection.PropertyInfo[] CardProperties = typeof(CardInstance).GetProperties();

    public static byte[] Write(RunSession rs, IReadOnlyList<Candidate> offered, IReadOnlyList<Candidate> declined) =>
        Import.Serialize(w => WriteObject(w, rs, offered, declined, withIds: true));

    /// <summary>The same projection without run/decision ids or counters (for repeated-state detection).</summary>
    public static byte[] Signature(RunSession rs, IReadOnlyList<Candidate> offered) =>
        Import.Serialize(w => WriteObject(w, rs, offered, [], withIds: false));

    public static void CheckDeckCard(CardInstance c)
    {
        var plain = new CardInstance(c.DefId, c.Upgraded, Enchantment: c.Enchantment, EnchantAmount: c.EnchantAmount,
            TinkerType: c.TinkerType, TinkerRider: c.TinkerRider) with { DeckOrigin = c.DeckOrigin };
        if (plain == c)
            return;
        var differing = CardProperties
            .Where(p => !DeckCardFields.Contains(p.Name) && !Equals(p.GetValue(c), p.GetValue(plain)))
            .Select(p => $"{p.Name}={p.GetValue(c)}");
        throw new UnsupportedRunState("unrepresented_deck_card_modifier",
            $"deck card {c.DefId} carries {string.Join(", ", differing)}");
    }

    private static void WriteObject(Utf8JsonWriter w, RunSession rs, IReadOnlyList<Candidate> offered,
        IReadOnlyList<Candidate> declined, bool withIds)
    {
        var s = rs.State;
        w.WriteStartObject();
        w.WriteString("schema", Schema);
        if (withIds)
        {
            w.WriteString("run_id", rs.RunId);
            w.WriteString("decision_id", rs.DecisionId);
        }
        w.WriteString("phase", s.Phase.ToString());

        w.WritePropertyName("scalars");
        w.WriteStartObject();
        w.WriteNumber("hp", s.PlayerHp);
        w.WriteNumber("max_hp", s.PlayerMaxHp);
        w.WriteNumber("gold", s.Gold);
        w.WriteNumber("floor", s.Floor);
        w.WriteNumber("act_ordinal", s.CurrentActIndex + 1);
        w.WriteNumber("act_region", s.Act);
        w.WriteNumber("ascension", s.AscensionLevel);
        w.WriteNumber("potion_capacity", s.MaxPotionSlots);
        w.WriteNumber("deck_size", s.Deck.Count);
        w.WriteNumber("relic_count", s.Relics.Count);
        w.WriteNumber("fights_won", rs.Fights.All.Count(f => f.Outcome == "victory"));
        if (withIds)
            w.WriteNumber("strategic_decisions", rs.StrategicDecisions);
        w.WriteEndObject();

        w.WritePropertyName("deck");
        w.WriteStartArray();
        var ids = rs.Deck.Ids;
        // Guilty's per-copy [SavedProperty] served-combat count (displayed on the card). The engine keeps
        // RunState.GuiltyCombatsServed parallel to the Guilty copies in deck order and, at each victory,
        // pads it with 0 / truncates it to the copy count (ServeGuiltySentences): entry i is the i-th copy's
        // count, and a copy without an entry yet is a 0 -- exactly what the engine will treat it as.
        int guilty = RunNonCombatEffects.NamedCard("Guilty");
        int g = 0;
        for (int i = 0; i < s.Deck.Count; i++)
        {
            var c = s.Deck[i];
            CheckDeckCard(c);
            w.WriteStartObject();
            w.WriteNumber("instance_id", ids[i]);
            WriteCardFields(w, c);
            if (Math.Abs(c.DefId) == guilty)
            {
                w.WriteNumber("served", g < s.GuiltyCombatsServed.Count ? s.GuiltyCombatsServed[g] : 0);
                g++;
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WritePropertyName("relics");
        w.WriteStartArray();
        foreach (var r in s.Relics)
        {
            w.WriteStartObject();
            w.WriteNumber("def_id", r.DefId);
            w.WriteNumber("counter", r.Counter);
            w.WriteBoolean("used_up", s.UsedUpRelics.Contains(r.DefId));
            w.WriteBoolean("wax", s.WaxRelics.Contains(r.DefId));
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Outside combat the run belt is authoritative (a combat's own belt is E's to see).
        w.WritePropertyName("potions");
        w.WriteStartArray();
        int capacity = Math.Min(s.MaxPotionSlots, s.PotionSlots.Length);
        if (s.MaxPotionSlots > s.PotionSlots.Length)
            throw new UnsupportedRunState("potion_capacity_beyond_slot_array", $"capacity {s.MaxPotionSlots}");
        for (int slot = 0; slot < capacity; slot++)
        {
            w.WriteStartObject();
            w.WriteNumber("slot", slot);
            w.WriteNumber("potion_id", s.PotionSlots[slot]);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        WriteMap(w, rs);
        WriteHistory(w, rs);
        WriteScreen(w, rs, offered);

        w.WritePropertyName("candidates");
        w.WriteStartArray();
        foreach (var c in offered)
            WriteCandidate(w, c);
        w.WriteEndArray();
        if (withIds)
        {
            w.WritePropertyName("declined");
            w.WriteStartArray();
            foreach (var c in declined)
            {
                w.WriteStartObject();
                w.WriteString("id", c.Id);
                w.WriteString("kind", c.Kind);
                w.WriteString("reason", c.Declined);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteCardFields(Utf8JsonWriter w, CardInstance c)
    {
        w.WriteNumber("def_id", Math.Abs(c.DefId));
        w.WriteBoolean("upgraded", c.Upgraded);
        w.WriteNumber("enchantment", (int)c.Enchantment);
        w.WriteNumber("enchant_amount", c.EnchantAmount);
        w.WriteNumber("tinker_type", (int)c.TinkerType);
        w.WriteNumber("tinker_rider", (int)c.TinkerRider);
    }

    private static void WriteCard(Utf8JsonWriter w, CardInstance c)
    {
        w.WriteStartObject();
        WriteCardFields(w, c);
        w.WriteEndObject();
    }

    private static void WriteCandidate(Utf8JsonWriter w, Candidate c)
    {
        w.WriteStartObject();
        w.WriteString("id", c.Id);
        w.WriteString("kind", c.Kind);
        w.WritePropertyName("public");
        c.Public.WriteTo(w);
        w.WriteEndObject();
    }

    private static void WriteMap(Utf8JsonWriter w, RunSession rs)
    {
        var s = rs.State;
        var nodes = s.MapNodes.Values.OrderBy(n => n.Row).ThenBy(n => n.Col).ToList();
        var index = new Dictionary<(int, int), int>();
        for (int i = 0; i < nodes.Count; i++)
            index[(nodes[i].Col, nodes[i].Row)] = i;
        var visited = new HashSet<(int, int)>(rs.Visited.Where(v => v.Act == s.CurrentActIndex).Select(v => (v.Col, v.Row)));
        var options = new Dictionary<(int, int), int>();
        if (s.Phase == RunPhase.Map)
            for (int i = 0; i < s.MapOptionCoords.Length; i++)
                if (s.MapOptionCoords[i] is { } c && s.MapNodeTypes[i] != RunConstants.NodeNone)
                    options[c] = i;

        w.WritePropertyName("map");
        w.WriteStartObject();
        w.WriteNumber("act_ordinal", s.CurrentActIndex + 1);
        w.WriteNumber("act_region", s.Act);
        // The act's boss is drawn on its map (the boss icon); later acts' bosses are not shown.
        w.WriteNumber("boss_encounter_id", s.BossEncounterId);
        w.WriteBoolean("awaiting_act_start", s.AwaitingActStartNode);
        w.WriteNumber("current", !s.AwaitingActStartNode && index.TryGetValue(s.CurrentMapCoord, out int cur) ? cur : -1);
        w.WritePropertyName("nodes");
        w.WriteStartArray();
        foreach (var n in nodes)
        {
            var coord = (n.Col, n.Row);
            w.WriteStartObject();
            w.WriteNumber("col", n.Col);
            w.WriteNumber("row", n.Row);
            w.WriteNumber("node_type", n.NodeType);
            w.WriteBoolean("visited", visited.Contains(coord));
            w.WriteBoolean("current", !s.AwaitingActStartNode && coord == s.CurrentMapCoord);
            w.WriteNumber("option", options.TryGetValue(coord, out int o) ? o : -1);
            w.WriteBoolean("fur_coat", s.FurCoatAct == s.CurrentActIndex && s.FurCoatRooms.Contains(coord));
            w.WriteBoolean("quest", s.SpoilsMapQuestCoord == coord);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("edges");
        w.WriteStartArray();
        foreach (var n in nodes)
        {
            int from = index[(n.Col, n.Row)];
            foreach (var child in n.Children)
            {
                if (!index.TryGetValue(child, out int to))
                    throw new ContractViolation("map_edge_to_missing_node", $"({n.Col},{n.Row}) -> {child}");
                w.WriteStartArray();
                w.WriteNumberValue(from);
                w.WriteNumberValue(to);
                w.WriteEndArray();
            }
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteHistory(Utf8JsonWriter w, RunSession rs)
    {
        w.WritePropertyName("history");
        w.WriteStartObject();
        w.WritePropertyName("visited");
        w.WriteStartArray();
        foreach (var v in rs.Visited)
        {
            w.WriteStartObject();
            w.WriteNumber("act_ordinal", v.Act + 1);
            w.WriteNumber("col", v.Col);
            w.WriteNumber("row", v.Row);
            w.WriteNumber("node_type", v.NodeType);
            w.WriteNumber("resolved_room", v.Resolved);
            w.WriteNumber("floor", v.Floor);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("fights");
        w.WriteStartArray();
        foreach (var f in rs.Fights.All.Where(f => f.Closed))
        {
            w.WriteStartObject();
            w.WriteNumber("act_ordinal", f.ActOrdinal);
            w.WriteString("room", f.Room);
            w.WriteString("outcome", f.Outcome);
            w.WriteNumber("floor", f.Floor);
            w.WriteNumber("entry_hp", f.Entry.PlayerHp);
            w.WriteNumber("hp_terminal", f.HpTerminal);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteScreen(Utf8JsonWriter w, RunSession rs, IReadOnlyList<Candidate> offered)
    {
        var s = rs.State;
        w.WritePropertyName("screen");
        w.WriteStartObject();
        switch (s.Phase)
        {
            case RunPhase.CardReward:
            {
                var offer = rs.DisplayedOffer();
                w.WritePropertyName("cards");
                w.WriteStartArray();
                for (int i = 0; i < RunConstants.CardRewardOptionCount; i++)
                    if (s.RewardCards[i] != 0)
                        WriteCard(w, offer.Cards[i]);
                w.WriteEndArray();
                w.WriteBoolean("reroll_available", s.CardRewardRerollAvailable);
                w.WritePropertyName("fourth_card");
                if (s.LastingCandyRewardCard != 0)
                    WriteCard(w, offer.BonusPowerCard);
                else
                    w.WriteNullValue();
                w.WriteBoolean("returns_to_reward_screen", s.ReturnToRewardScreenAfterCardReward);
                // other card offers this reward screen will still show (final-skipped ones never return)
                w.WriteNumber("remaining_card_offers", s.ReturnToRewardScreenAfterCardReward ? rs.LiveCardOffers() : 0);
                break;
            }
            case RunPhase.RelicReward:
                w.WriteString("context", s.NeowAwaitingProceed ? "ancient" : s.EventAwaitingProceed ? "event" : "room");
                w.WriteBoolean("can_leave", !s.PendingNeowsBonesCurse);
                // The only reward-screen decisions of the normalized flow: a full belt's incoming potion,
                // or a stolen-back special card. Everything else on the screen is bookkeeping.
                w.WriteString("decision", offered.Any(c => c.Kind is "potion_replace" or "potion_use_accept" or "potion_skip_incoming")
                    ? "potion_full_belt" : offered.Any(c => c.Kind == "reward_special_card") ? "special_card" : "none");
                w.WriteNumber("potion_rows", rs.LivePotionRows());
                w.WriteNumber("card_offer_rows", rs.LiveCardOffers());
                w.WriteNumber("card_removals_owed", s.CardRemovalsOwed);
                break;
            case RunPhase.Shop:
                w.WritePropertyName("cards");
                w.WriteStartArray();
                for (int i = 0; i < s.ShopCards.Length; i++)
                    if (s.ShopCards[i] != 0)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("slot", i);
                        w.WriteNumber("def_id", s.ShopCards[i]);
                        w.WriteNumber("cost", s.ShopCosts[i]);
                        w.WriteEndObject();
                    }
                w.WriteEndArray();
                w.WritePropertyName("relics");
                w.WriteStartArray();
                for (int i = 0; i < 3; i++)
                    if (s.ShopRelics[i] != 0)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("slot", i);
                        w.WriteNumber("relic_id", s.ShopRelics[i]);
                        w.WriteNumber("cost", s.ShopCosts[7 + i]);
                        w.WriteEndObject();
                    }
                w.WriteEndArray();
                w.WritePropertyName("potions");
                w.WriteStartArray();
                for (int i = 0; i < 3; i++)
                    if (s.ShopPotions[i] != 0)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("slot", i);
                        w.WriteNumber("potion_id", s.ShopPotions[i]);
                        w.WriteNumber("cost", s.ShopCosts[10 + i]);
                        w.WriteEndObject();
                    }
                w.WriteEndArray();
                w.WriteNumber("removal_cost", s.ShopCosts[RunConstants.ShopRemoveAction]);
                w.WriteBoolean("removal_used", s.ShopRemovalUsedThisVisit);
                break;
            case RunPhase.Rest:
                w.WriteNumber("options_taken_mask", s.RestOptionsTaken);
                w.WriteBoolean("result_pending", s.RestResultPending);
                break;
            case RunPhase.Event:
                w.WriteNumber("event_id", s.EventId);
                w.WriteNumber("page", s.EventId == RunConstants.EventResultPending ? 0 : s.EventPage);
                break;
            case RunPhase.Ancient:
                w.WriteString("ancient", s.Ancient);
                w.WriteBoolean("awaiting_proceed", s.NeowAwaitingProceed);
                break;
            case RunPhase.TransformSelect:
                WriteSelection(w, s);
                break;
            case RunPhase.Treasure:
                w.WriteBoolean("opened", s.TreasureChestOpened);
                if (s.TreasureChestOpened)
                    w.WriteNumber("relic_id", s.RelicReward);
                break;
            case RunPhase.CrystalSphere:
                WriteSphere(w, s);
                break;
            case RunPhase.BundleSelect:
                if (s.BundleOffer.Length != 6)
                    throw new ContractViolation("bundle_offer_shape", $"BundleOffer has {s.BundleOffer.Length} cards, expected 2x3");
                w.WritePropertyName("bundles");
                w.WriteStartArray();
                for (int b = 0; b < 2; b++)
                {
                    w.WriteStartArray();
                    for (int i = 0; i < 3; i++)
                        WriteCard(w, new CardInstance(s.BundleOffer[b * 3 + i], false));
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteNumber("selected", s.SelectedBundle);
                break;
            case RunPhase.FakeMerchant:
                w.WritePropertyName("relics");
                w.WriteStartArray();
                var stall = s.EventRelicStock ?? [];
                for (int i = 0; i < stall.Count; i++)
                {
                    w.WriteStartObject();
                    w.WriteNumber("slot", i);
                    w.WriteNumber("relic_id", stall[i]);
                    w.WriteNumber("cost", s.FakeMerchantPrices![i]);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                break;
        }
        w.WriteEndObject();
    }

    private static void WriteSelection(Utf8JsonWriter w, RunState s)
    {
        if (s.PendingOfferCards.Length > 0)
        {
            w.WriteString("mode", "grid");
            w.WriteNumber("picks_left", s.PendingOfferPicks);
            w.WriteBoolean("optional", s.PendingOfferOptional);
            return;
        }
        if (s.PendingRestUpgrade)
        {
            w.WriteString("mode", "smith");
            return;
        }
        if (s.PendingSelectionKind == DeckSelection.None)
        {
            w.WriteString("mode", "continue");
            return;
        }
        w.WriteString("mode", "deck_selection");
        w.WriteString("purpose", s.PendingSelectionKind.ToString());
        w.WriteNumber("purpose_id", (int)s.PendingSelectionKind);
        w.WriteNumber("arg", s.PendingSelectionArg);
        w.WriteNumber("remaining", s.PendingSelectionCount);
        w.WriteString("source_event", s.PendingSelectionEventEntry);
        w.WriteString("returns_to", s.PendingSelectionReturn.ToString());
        w.WriteNumber("follow_up_card", s.PendingSelectionFollowUpCard);
        w.WriteNumber("follow_up_count", s.PendingSelectionFollowUpCount);
        w.WriteNumber("follow_up_hp_loss", s.PendingSelectionFollowUpHpLoss);
        w.WriteNumber("follow_up_gold_loss", s.PendingSelectionFollowUpGoldLoss);
        w.WriteNumber("follow_up_max_hp_gain", s.PendingSelectionFollowUpMaxHpGain);
        w.WriteBoolean("shop_removal", s.PendingShopRemovalCost is not null);
        w.WriteNumber("shop_removal_cost", s.PendingShopRemovalCost ?? 0);
    }

    private static void WriteSphere(Utf8JsonWriter w, RunState s)
    {
        var g = s.CrystalSphere ?? throw new ContractViolation("sphere_phase_without_board", "CrystalSphere null");
        w.WriteNumber("divinations", g.Divinations);
        w.WritePropertyName("cells");
        w.WriteStartArray();
        for (int x = 0; x < RunConstants.CrystalSphereSize; x++)
            for (int y = 0; y < RunConstants.CrystalSphereSize; y++)
            {
                bool hidden = g.IsHidden(x, y);
                w.WriteStartObject();
                w.WriteNumber("x", x);
                w.WriteNumber("y", y);
                w.WriteBoolean("hidden", hidden);
                if (!hidden)
                {
                    // An uncovered cell shows the piece of whatever lies under it, and nothing more.
                    int item = g.ItemAt(x, y);
                    if (item >= 0 && item < g.Items.Count)
                    {
                        var it = g.Items[item];
                        w.WriteNumber("item_kind", (int)it.Kind);
                        w.WriteNumber("item_rarity", it.Rarity);
                        w.WriteBoolean("big_gold", it.IsBigGold);
                    }
                }
                w.WriteEndObject();
            }
        w.WriteEndArray();
        w.WriteNumber("revealed_items", g.Revealed.Count);
    }
}
