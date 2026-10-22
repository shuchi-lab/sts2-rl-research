using System.Text.Json;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Run;

namespace FullRunWorker;

/// <summary>
/// The acquisition restrictions that keep a run inside frozen policy E's representation. They are
/// supplied by the client from the versioned manifest (<c>run_strategy_v1/acquisition_restrictions.json</c>)
/// together with E's exact 214-card identity set, so the policy-side manifest is the single source;
/// this class only applies them to displayed candidates. A restriction never inspects a hidden
/// outcome: it applies to the identity the screen itself shows.
/// </summary>
public sealed class Compatibility
{
    public required HashSet<int> RosterCards { get; init; }
    public required HashSet<int> DeclinedPotions { get; init; }
    public required HashSet<int> DeclinedAncientRelics { get; init; }
    public required HashSet<(int Event, int Page, int Option)> DeclinedEventOptions { get; init; }
    public required Dictionary<int, int> CapacityRelics { get; init; }
    public required HashSet<int> NeowsBonesDeadEndRelics { get; init; }
    public required int PotionSlotLimit { get; init; }
    public required string ManifestDigest { get; init; }

    public static Compatibility FromJson(JsonElement e)
    {
        var events = new HashSet<(int, int, int)>();
        foreach (var x in e.GetProperty("declined_event_options").EnumerateArray())
            events.Add((x.GetProperty("event_id").GetInt32(), x.GetProperty("page").GetInt32(), x.GetProperty("option").GetInt32()));
        var capacity = new Dictionary<int, int>();
        foreach (var x in e.GetProperty("capacity_relics").EnumerateArray())
            capacity[x.GetProperty("relic_id").GetInt32()] = x.GetProperty("slots").GetInt32();
        var c = new Compatibility
        {
            RosterCards = [.. e.GetProperty("roster_card_ids").EnumerateArray().Select(x => x.GetInt32())],
            DeclinedPotions = [.. e.GetProperty("declined_potion_ids").EnumerateArray().Select(x => x.GetInt32())],
            DeclinedAncientRelics = [.. e.GetProperty("declined_ancient_relic_ids").EnumerateArray().Select(x => x.GetInt32())],
            DeclinedEventOptions = events,
            CapacityRelics = capacity,
            NeowsBonesDeadEndRelics = [.. e.GetProperty("neows_bones_dead_end_relic_ids").EnumerateArray().Select(x => x.GetInt32())],
            PotionSlotLimit = e.GetProperty("potion_slot_limit").GetInt32(),
            ManifestDigest = e.GetProperty("manifest_digest").GetString()!,
        };
        if (c.RosterCards.Count == 0)
            throw new ArgumentException("compatibility: empty roster");
        if (c.PotionSlotLimit != RunConstants.PotionSlotCapacity)
            throw new ArgumentException($"compatibility: potion_slot_limit {c.PotionSlotLimit} != engine slot array {RunConstants.PotionSlotCapacity}");
        return c;
    }

    public bool CardSupported(CardInstance card) => RosterCards.Contains(Math.Abs(card.DefId));

    public bool CardSupported(int defId) => RosterCards.Contains(Math.Abs(defId));

    /// <summary>Why acquiring this relic now would leave E's representation, or null.</summary>
    public string? RelicRestriction(RunState s, int relicId, bool ancient)
    {
        if (ancient && DeclinedAncientRelics.Contains(relicId))
            return "ancient_relic_grants_foreign_character_cards";
        if (CapacityRelics.TryGetValue(relicId, out int slots) && s.MaxPotionSlots + slots > PotionSlotLimit)
            return "potion_capacity_beyond_six_slot_representation";
        return null;
    }

    /// <summary>
    /// A Neow's Bones row whose pickup opens a grid/selection, claimed while the OTHER Bones row is still
    /// on the screen: the screen returns straight to Neow, abandoning that row with the curse unsettled (an
    /// engine dead end). Claimed last, <c>OfferNextBonusRelic</c> settles the curse first and it is safe.
    /// </summary>
    public string? NeowsBonesRowRestriction(RunState s, int relicId)
    {
        int bonesRows = (s.RelicReward != 0 ? 1 : 0) + s.PendingBonusRelicRewards.Count;
        return s.PendingNeowsBonesCurse && bonesRows >= 2 && NeowsBonesDeadEndRelics.Contains(relicId)
            ? "neows_bones_screen_pickup_before_other_row_engine_dead_end"
            : null;
    }

    public string? PotionRestriction(int potionId) =>
        DeclinedPotions.Contains(potionId) ? "unmodelled_manual_combat_potion" : null;

    public string? CardRestriction(int defId) => CardSupported(defId) ? null : "card_outside_frozen_E_roster";

    public string? EventOptionRestriction(int eventId, int page, int option) =>
        DeclinedEventOptions.Contains((eventId, page, option)) ? "event_option_declined_by_manifest" : null;
}
