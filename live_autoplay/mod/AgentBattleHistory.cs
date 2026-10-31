using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace STS2_MCP;

public static partial class McpMod
{
    private static IEnumerable<BattleHistoryEvent> BattleHistoryEntries(CombatState combat,Player player,CardPlay[] pending)
    {
        foreach (var e in CombatManager.Instance.History.Entries)
        {
            bool local = e.Actor == player.Creature, turn = BattleThisTurn(e,combat);
            yield return e switch {
                CardPlayStartedEntry s => new("start",local,turn,s.CardPlay.Card.Type.ToString(),false,0),
                CardPlayFinishedEntry f => new("finish",local,turn,f.CardPlay.Card.Type.ToString(),false,0),
                CardExhaustedEntry => new("exhaust",local,turn,"",false,0),
                DamageReceivedEntry d => new("damage",local,turn,"",false,d.Result.UnblockedDamage),
                BlockGainedEntry b => BattleBlockHistory.Row(local,turn,b.CardPlay != null,b.CardPlay != null && b.CardPlay.Player.Creature == player.Creature,
                    b.Props.IsCardOrMonsterMove(),b.CardPlay != null && pending.Contains(b.CardPlay),b.Amount),
                _ => new("other",local,turn,"",false,0)
            };
        }
    }

    // Past card-play boundaries and player-created generations, in native history order.
    private static IEnumerable<BattleGenerationEvent> BattleGenerationHistory(IEnumerable<CombatHistoryEntry> entries,Player player)
    {
        foreach (var e in entries)
        {
            if (e is CardPlayStartedEntry s) yield return new(BattleGenerationKind.Started,s.CardPlay,s.CardPlay.Card.Id.Entry,false);
            else if (e is CardPlayFinishedEntry f) yield return new(BattleGenerationKind.Finished,f.CardPlay,f.CardPlay.Card.Id.Entry,false);
            else if (e is CardGeneratedEntry g) yield return new(BattleGenerationKind.Generated,null,"",g.Creator == player);
        }
    }

    // The player's TurnNumber the native entry snapshotted when it was recorded.
    private static int BattleEntryPlayerTurn(CombatHistoryEntry entry,Player player) =>
        BattleRead(entry,"_playerTurnNumbers") is Dictionary<ulong,int> turns && turns.TryGetValue(player.NetId,out var turn)
            ? turn : throw new BattleProjectionUnsupported("History entry without the local player's turn number");

    private static double[] BattleCounters(RunState run,Player player,CombatState combat,BattleHistoryEvent[] history,CardPlay? parked,CardPlay[] nested,bool selecting)
    {
        var pc = player.PlayerCombatState!;
        var all = CombatManager.Instance.History.Entries.Where(e => e.Actor == player.Creature).ToArray();
        var turn = all.Where(e => BattleThisTurn(e,combat)).ToArray();
        var started = turn.OfType<CardPlayStartedEntry>().ToArray();
        var finished = turn.OfType<CardPlayFinishedEntry>().ToArray();
        var drawn = turn.OfType<CardDrawnEntry>().ToArray();
        var damages = turn.OfType<DamageReceivedEntry>().ToArray();
        var previous = CombatManager.Instance.History.Entries.ToArray().Skip(_battlePreviousTurnStart)
            .Take(Math.Max(0,_battleTurnEntryStart-_battlePreviousTurnStart)).Where(e => e.Actor == player.Creature).ToArray();
        double PowerData(string type,string member) => BattlePower(player.Creature,type) is { } p ? BattleNumber(BattleData(p),member) : 0;
        // The innermost started play: a parked Havoc child, else the chosen root.
        var innermost = nested.Length > 0 ? nested[0] : parked;
        var stack = BattleNestedAutoSelection.StackCounters(parked != null,nested.Length);
        double CapturedAmount(string type) {
            if (innermost == null || BattlePower(player.Creature,type) is not { } p) return 0;
            var d = (IDictionary)BattleRead(BattleData(p),"amountsForPlayedCards")!;
            return d.Contains(innermost.Card) ? Convert.ToDouble(d[innermost.Card]) : 0;
        }
        // These fields are engine execution-stack state. Their zero is certified at a settled
        // chosen decision and at a parked Havoc stack (no batch, no Rupture/played-card state).
        var zeros = new HashSet<string>(StringComparer.Ordinal) {
            "autoplay_batch_pending","autoplay_batch_resumes_turn_start","autoplay_batch_resumes_end_turn","stampede_remaining",
            "defer_card_rupture","card_rupture_strength","completed_card_rupture_strength","resolving_defend_card",
            "played_card_cost_for_combat_set","played_card_cost_for_combat","played_card_bonus_damage","played_card_momentum_damage","played_card_bonus_block",
            "played_card_enchant_spent","played_card_enchant_grew","played_card_cost_bump","played_card_stars_spent","lethal_attack_play","end_turn_after_play",
            "ethereal_exhausts_this_turn","ethereal_exhaust_count","lightning_orbs_channeled_this_combat","osty_attacks_this_turn"
        };
        if (parked != null && BattlePower(player.Creature,"RupturePower") != null)
            throw new BattleProjectionUnsupported("A Rupture play paused before its deferred reaction was projected");
        double Get(string key) => key switch {
            "ascension_level" => run.AscensionLevel, "max_energy" => pc.MaxEnergy, "max_potion_slots" => player.MaxPotionCount,
            "is_elite_combat" => (combat.Encounter?.RoomType.ToString() == "Elite") ? 1 : 0,
            "is_elite_room" => BattleRoom(run) == "elite" ? 1 : 0,
            "fur_coat_bites" => BattleRelic(player,"FurCoat") is MegaCrit.Sts2.Core.Models.Relics.FurCoat fur
                && fur.GetMarkedCoords()?.Contains(run.CurrentMapPoint!.coord) == true ? 1 : 0,
            "end_turn_awaiting_selection" => selecting && pc.Phase.ToString() != "Play" ? 1 : 0,
            "pending_card_play" => stack.PendingCardPlay, "nested_autoplay_depth" => stack.NestedDepth,
            "parked_play_extra_plays" => parked == null ? 0 : parked.PlayCount-1,
            "parked_play_index" => parked?.PlayIndex ?? 0,
            "parked_play_energy_spent" => parked?.Resources.EnergySpent ?? 0,
            "afterimage_before_play" => CapturedAmount("AfterimagePower"), "storm_before_play" => CapturedAmount("StormPower"),
            "subroutine_before_play" => CapturedAmount("SubroutinePower"),
            "serpent_form_snapshot_count" => stack.SerpentSnapshots,
            "serpent_form_snapshot_innermost" => CapturedAmount("SerpentFormPower"),
            "status_cards_drawn_this_turn" => drawn.Count(e => e.Card.Type == CardType.Status),
            "energy_spent_this_turn" => turn.OfType<EnergySpentEntry>().Sum(e => e.Amount),
            "orbit_energy_spent" => PowerData("OrbitPower","energySpent"),
            "void_form_cards_played_this_turn" => PowerData("VoidFormPower","cardsPlayedThisTurn"),
            "cards_played_this_turn" => started.Count(e => !e.CardPlay.IsAutoPlay),
            "attack_or_skill_cards_played_this_turn" => finished.Count(e => e.CardPlay.Card.Type is CardType.Attack or CardType.Skill),
            "card_plays_this_turn" => finished.Length, "skill_cards_played_this_turn" => finished.Count(e => e.CardPlay.Card.Type == CardType.Skill),
            "shivs_played_this_turn" => finished.Count(e => e.CardPlay.Card.Id.Entry == "SHIV"),
            "card_plays_last_turn" => previous.OfType<CardPlayFinishedEntry>().Count(),
            "cards_drawn_this_combat" => BattleDrawCounter.CardsDrawnThisCombat(all.OfType<CardDrawnEntry>()
                .Select(e => new BattleDrawnCard(e.FromHandDraw,BattleEntryPlayerTurn(e,player)))),
            "extra_cards_drawn_this_turn" => drawn.Count(e => !e.FromHandDraw),
            "cards_generated_this_combat" => BattleGeneratedCounter.CardsGeneratedThisCombat(BattleGenerationHistory(all,player)),
            "cards_exhausted_this_turn" => turn.OfType<CardExhaustedEntry>().Count(),
            "cards_discarded_this_turn" => turn.OfType<CardDiscardedEntry>().Count() + _battleSelectionDiscarded.Length,
            "ethereal_card_plays_this_combat" => all.OfType<CardPlayFinishedEntry>().Count(e => e.WasEthereal),
            "player_hp_lost_this_turn" => damages.Sum(e => e.Result.UnblockedDamage),
            "unblocked_damage_this_turn" => BattleDamageHistory.UnblockedDamageThisTurn(damages.Select(e => e.Result)),
            "took_unblocked_damage" => BattleDamageHistory.TookUnblockedDamage(all.OfType<DamageReceivedEntry>().Select(e => e.Result)) ? 1 : 0,
            "doom_applied_this_turn" => CombatManager.Instance.History.Entries.OfType<PowerReceivedEntry>().Any(e => BattleThisTurn(e,combat) && e.Power.GetType().Name == "DoomPower" && e.Applier == player.Creature) ? 1 : 0,
            "stars_gained_this_turn" => turn.OfType<StarsModifiedEntry>().Where(e => e.Amount > 0).Sum(e => e.Amount),
            "skill_played_while_smoggy" => _battleSmoggyPlayed ? 1 : 0,
            "pen_nib_armed" => Convert.ToBoolean(BattleRelicState(player,"PenNib","IsActivating")) ? 1 : 0,
            "vambrace_spent" => Convert.ToBoolean(BattleRelicState(player,"Vambrace","BlockGainedThisCombat")) ? 1 : 0,
            "vambrace_claimed_by_play" => BattleRelicState(player,"Vambrace","TriggeringCard") == null ? 0 : 1,
            "paels_legion_cooldown" => Math.Max(0, BattleRelicNumber(player,"PaelsLegion","Cooldown")),
            "paels_legion_claimed_by_play" => BattleRelicState(player,"PaelsLegion","AffectedCardPlay") == null ? 0 : 1,
            "rainbow_ring_attacks" => BattleRelicNumber(player,"RainbowRing","AttacksPlayedThisTurn"),
            "rainbow_ring_skills" => BattleRelicNumber(player,"RainbowRing","SkillsPlayedThisTurn"),
            "rainbow_ring_powers" => BattleRelicNumber(player,"RainbowRing","PowersPlayedThisTurn"),
            "rainbow_ring_paid_this_turn" => BattleRelicNumber(player,"RainbowRing","ActivationCountThisTurn") > 0 ? 1 : 0,
            "unsettling_lamp_spent" => Convert.ToBoolean(BattleRelicState(player,"UnsettlingLamp","IsFinishedTriggering")) ? 1 : 0,
            "unsettling_lamp_card" => BattleRelicState(player,"UnsettlingLamp","TriggeringCard") == null ? 0 : 1,
            "gambling_chip_discarded" => _battleSelectionDiscarded.Length,
            // Frozen GamblingChipOwed is set only while another screen blocks the Chip's and is
            // cleared before the Chip screen opens, so it is 0 while that screen is up.
            "gambling_chip_owed" => 0,
            "burning_sticks_used" => Convert.ToBoolean(BattleRelicState(player,"BurningSticks","WasUsedThisCombat")) ? 1 : 0,
            "belt_buckle_applied" => Convert.ToBoolean(BattleRelicState(player,"BeltBuckle","DexterityApplied")) ? 1 : 0,
            "whispering_earring_spent" => BattleRelic(player,"WhisperingEarring") != null && all.OfType<CardPlayFinishedEntry>().Any(e => e.CardPlay.IsAutoPlay) ? 1 : 0,
            "music_box_used_this_turn" => Convert.ToBoolean(BattleRelicState(player,"MusicBox","WasUsedThisTurn")) ? 1 : 0,
            _ when zeros.Contains(key) => 0,
            _ => throw new BattleProjectionUnsupported("Counter lacks explicit native semantics: " + key)
        };
        return BattleProjectionSchema.Counters.Select(Get).ToArray();
    }
}
