using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

// Debug actions used by the emulator's differential-test harness
// (validate_real_game_trace.py): jump straight into a chosen encounter, and
// force the combat into the player play phase. These mirror capabilities that
// existed only in an unpublished local mod; reconstructed here from the
// decompiled game's CombatRoom / RunManager / CombatManager APIs.
public static partial class McpMod
{
    // {"action":"debug_start_encounter","encounter":"CorpseSlugsWeak"}
    // Builds a CombatRoom for the named encounter against the current run state
    // and enters it, replacing whatever room the run is currently in.
    private static Dictionary<string, object?> ExecuteDebugStartEncounter(
        Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("encounter", out var encEl)
            || encEl.ValueKind != JsonValueKind.String)
        {
            return Error("debug_start_encounter requires a string 'encounter' field");
        }

        var name = encEl.GetString()!;
        // AllEncounters is the union of each ACT's pools, so it cannot see an encounter
        // that only an event starts -- the Fake Merchant, the Architect, the Battleworn
        // Dummies and the rest. Those are registered models like any other, so fall back
        // to the registry by ModelId: the category slug for EncounterModel plus the
        // class name slugified the way ModelDb.GetEntry does it.
        var encounter = ModelDb.AllEncounters
            .FirstOrDefault(e => e.GetType().Name == name)
            ?? ModelDb.GetByIdOrNull<EncounterModel>(
                new ModelId(ModelId.SlugifyCategory<EncounterModel>(), StringHelper.Slugify(name)));
        if (encounter == null)
            return Error($"Unknown encounter: {name}");

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return Error("No run state available");

        // Fresh, mutable copy so we don't scribble on the shared registry model.
        var room = new CombatRoom(encounter.ToMutable(), runState);

        // Fire-and-forget: the game processes the room transition on its own loop;
        // the caller polls /api/v1/singleplayer until the battle appears.
        _ = RunManager.Instance.EnterRoom(room);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Starting encounter {name}",
        };
    }

    // {"action":"debug_start_event","event":"SelfHelpBook"}
    // Enters the named event's room against the current run state, replacing whatever
    // room the run is in. The sibling of debug_start_encounter, and the reason it
    // exists: without it there is no way to reach a chosen event on demand, so the
    // emulator's per-event tests have no way to take ground truth from the game.
    //
    // EventModel seeds its own Rng from the run seed, the current TotalFloor and a hash
    // of the event's id, so what the event rolls depends on where in the run it is
    // started -- capture the floor alongside the readout.
    private static Dictionary<string, object?> ExecuteDebugStartEvent(
        Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("event", out var evEl)
            || evEl.ValueKind != JsonValueKind.String)
        {
            return Error("debug_start_event requires a string 'event' field");
        }

        var name = evEl.GetString()!;
        // AllEvents already folds in AllSharedEvents.
        var eventModel = ModelDb.AllEvents
            .FirstOrDefault(e => e.GetType().Name == name);
        if (eventModel == null)
            return Error($"Unknown event: {name}");

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return Error("No run state available");

        // EventRoom asserts its model is canonical, so unlike CombatRoom this takes
        // the registry instance rather than a mutable copy.
        var room = new EventRoom(eventModel);

        // Fire-and-forget: the game processes the room transition on its own loop;
        // the caller polls /api/v1/singleplayer until the event appears.
        _ = RunManager.Instance.EnterRoom(room);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Starting event {name}",
        };
    }

    // {"action":"debug_start_shop"}
    // Enters a merchant room against the current run state. The third of the
    // debug_start_* family, and it exists for the same reason as the other two: a shop's
    // stock is rolled from the run's own streams, so checking the emulator generates the
    // same stock means being able to reach a shop without playing to one.
    //
    // MerchantRoom builds its inventories in EnterInternal, one per player, so there is
    // nothing to pass in -- unlike an encounter or an event, the room is the whole
    // request.
    private static Dictionary<string, object?> ExecuteDebugStartShop()
    {
        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return Error("No run state available");

        _ = RunManager.Instance.EnterRoom(new MerchantRoom());

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Starting merchant room",
        };
    }

    // {"action":"debug_force_play_phase"}
    // After debug_start_encounter the combat may still be in its start sequence
    // (banner / setup) rather than the player play phase. Report readiness; the
    // natural StartTurn transition drives IsPlayPhase, which the caller polls.
    private static Dictionary<string, object?> ExecuteDebugForcePlayPhase(Player player)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = IsPlayPhase(player)
                ? "Already in play phase"
                : "Combat in progress; awaiting play phase",
        };
    }

    // {"action":"return_to_main_menu"}
    // Save-and-quit to the main menu, leaving the run in progress (the pause menu's
    // "Save and Quit" path: NPauseMenu.CloseToMenu -> NGame.Instance.ReturnToMainMenu).
    // v0.107 removed the equivalent action; the differential harness's
    // abandon_existing_run() depends on it.
    private static Dictionary<string, object?> ExecuteReturnToMainMenu()
    {
        var game = NGame.Instance;
        if (game == null)
            return Error("NGame not available");

        _ = game.ReturnToMainMenu();
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Returning to main menu",
        };
    }

    // {"action":"debug_add_card","card":"MOLTEN_FIST","upgraded":true,"pile":"hand"}
    // Puts a chosen card into a chosen combat pile, which is what lets the emulator's
    // card-capture harness observe one card in isolation instead of waiting for the
    // deck to deal it. Without this, per-card ground truth is limited to whatever a
    // run happens to offer.
    //
    // The instance comes from ModelDb, never `new MoltenFist()`: a hand-constructed
    // model never has InitId() called on it, so its Id.Entry is empty and the game
    // cannot render or resolve it (the trap documented in BuildCardWikiCandidates).
    //
    // CombatState.CreateCard does the clone AND the owner wiring. Cloning it here with
    // ToMutable() instead produces an ownerless card, and AddGeneratedCardToCombat
    // dereferences `card.Owner.Creature` — which, from a fire-and-forget task, fails
    // silently: the action answers "ok" and the card never appears.
    private static Dictionary<string, object?> ExecuteDebugAddCard(
        Player player,
        Dictionary<string, JsonElement> data)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");

        if (!data.TryGetValue("card", out var cardEl) || cardEl.ValueKind != JsonValueKind.String)
            return Error("debug_add_card requires a string 'card' field (the card's entry id, e.g. MOLTEN_FIST)");

        var wanted = cardEl.GetString()!;
        var canonical = ModelDb.AllCards.FirstOrDefault(c =>
            string.Equals(SafeGetText(() => c.Id.Entry), wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.GetType().Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (canonical == null)
            return Error($"Unknown card: {wanted}");

        var pileName = data.TryGetValue("pile", out var pileEl) && pileEl.ValueKind == JsonValueKind.String
            ? pileEl.GetString()!
            : "hand";
        if (!Enum.TryParse<PileType>(pileName, ignoreCase: true, out var pile)
            || pile is PileType.None or PileType.Play)
        {
            return Error($"Unknown pile: {pileName} (use hand, draw, discard, exhaust or deck)");
        }

        var combatState = player.Creature.CombatState;
        if (combatState == null)
            return Error("No combat state");

        var card = combatState.CreateCard(canonical, player);
        if (data.TryGetValue("upgraded", out var upEl) && upEl.ValueKind == JsonValueKind.True)
            CardCmd.Upgrade(card);

        // Fire-and-forget, like debug_start_encounter: the game resolves the add on its
        // own loop and the caller polls the state until the card shows up.
        _ = CardPileCmd.AddGeneratedCardToCombat(card, pile, player, CardPilePosition.Top);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Adding {SafeGetText(() => card.Id.Entry)} to {pile}",
        };
    }

    // {"action":"debug_set_energy","amount":9}
    // The Regent's second resource. Star cost gates a play exactly as energy does
    // (PlayerCombatState.HasEnoughResourcesFor checks both), so a capture of a card that
    // costs more stars than Divine Right's opening three cannot be taken without this --
    // Comet wants five, and the run has three.
    private static Dictionary<string, object?> ExecuteDebugSetStars(
        Player player,
        Dictionary<string, JsonElement> data)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");

        if (!data.TryGetValue("amount", out var amountEl)
            || !amountEl.TryGetDecimal(out var amount)
            || amount < 0)
        {
            return Error("debug_set_stars requires a non-negative numeric 'amount' field");
        }

        _ = PlayerCmd.SetStars(amount, player);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Setting stars to {amount}",
        };
    }

    // Guarantees the card under capture is affordable. A capture that silently failed
    // to play the card would otherwise look like a passing no-op diff.
    private static Dictionary<string, object?> ExecuteDebugSetEnergy(
        Player player,
        Dictionary<string, JsonElement> data)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");

        if (!data.TryGetValue("amount", out var amountEl)
            || !amountEl.TryGetDecimal(out var amount)
            || amount < 0)
        {
            return Error("debug_set_energy requires a non-negative numeric 'amount' field");
        }

        _ = PlayerCmd.SetEnergy(amount, player);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Setting energy to {amount}",
        };
    }

    // {"action":"debug_enchant_card","enchantment":"Sharp","index":0,"pile":"hand","amount":5}
    // Enchant a card that is already in a combat pile, so a capture can stage the one
    // thing decompiled/ cannot answer: whether an enchantment reaches a card whose damage
    // is a CALCULATION rather than a printed number, and whether a multi-hit attack pays
    // it once or per hit. `debug_add_card` puts the card there; this decorates it.
    //
    // CardCmd.Enchant THROWS on a card the enchantment refuses and on a card that already
    // carries a different one, so both are checked here and returned as errors -- a debug
    // action that takes down the game is worse than one that says no.
    private static Dictionary<string, object?> ExecuteDebugEnchantCard(
        Player player,
        Dictionary<string, JsonElement> data)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");

        if (!data.TryGetValue("enchantment", out var enchEl) || enchEl.ValueKind != JsonValueKind.String)
            return Error("debug_enchant_card requires a string 'enchantment' field (e.g. Sharp)");

        var wanted = enchEl.GetString()!;
        var canonical = ModelDb.DebugEnchantments.FirstOrDefault(e =>
            string.Equals(SafeGetText(() => e.Id.Entry), wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.GetType().Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (canonical == null)
            return Error($"Unknown enchantment: {wanted}");

        var pileName = data.TryGetValue("pile", out var pileEl) && pileEl.ValueKind == JsonValueKind.String
            ? pileEl.GetString()!
            : "hand";
        if (!Enum.TryParse<PileType>(pileName, ignoreCase: true, out var pileType)
            || pileType is PileType.None or PileType.Play)
        {
            return Error($"Unknown pile: {pileName} (use hand, draw, discard, exhaust or deck)");
        }

        var pile = pileType.GetPile(player);
        if (pile == null)
            return Error($"No {pileName} pile");

        int index = data.TryGetValue("index", out var idxEl) && idxEl.TryGetInt32(out var i) ? i : 0;
        var cards = pile.Cards.ToList();
        if (index < 0 || index >= cards.Count)
            return Error($"index {index} out of range for {pileName} ({cards.Count} cards)");

        var card = cards[index];
        // The amount is the enchantment's own default unless the caller overrides it --
        // Inky and Swift read vars on themselves and ignore it, which is worth seeing.
        decimal amount = data.TryGetValue("amount", out var amtEl) && amtEl.TryGetDecimal(out var a)
            ? a
            : 1m;

        var enchantment = canonical.ToMutable() as EnchantmentModel;
        if (enchantment == null)
            return Error($"Could not make {wanted} mutable");

        if (!enchantment.CanEnchant(card))
            return Error($"{SafeGetText(() => card.Id.Entry)} cannot take {wanted}");

        if (card.Enchantment != null && card.Enchantment.GetType() != enchantment.GetType())
        {
            return Error(
                $"{SafeGetText(() => card.Id.Entry)} already carries "
                + $"{SafeGetText(() => card.Enchantment!.Id.Entry)}");
        }

        CardCmd.Enchant(enchantment, card, amount);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] =
                $"Enchanted {SafeGetText(() => card.Id.Entry)} in {pileName}[{index}] "
                + $"with {amount} {SafeGetText(() => canonical.Id.Entry)}",
        };
    }

    // {"action":"debug_enter_next_act"}
    // Jump straight to the next act. RunManager.EnterNextAct is exactly what proceeding
    // from the act's boss reward calls, so this is the real transition and not a
    // shortcut around it -- what it skips is having to WIN the act first, which costs a
    // heavily buffed run and several minutes per act-2 data point, and can still lose the
    // boss fight. In the last act EnterNextAct goes to The Architect instead.
    private static Dictionary<string, object?> ExecuteDebugEnterNextAct()
    {
        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress");

        int from = RunManager.Instance.DebugOnlyGetState()!.CurrentActIndex;
        _ = RunManager.Instance.EnterNextAct();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Entering act after index {from}",
        };
    }

    // {"action":"debug_gain_max_hp","amount":50}
    // Run-scoped, unlike the rest of this file: no combat required. The auto-player dies
    // on floors 5-17 and has never once finished act 1, so the boss reward, the act
    // transition and everything past them are uncovered by any capture. This buys the
    // depth. CreatureCmd.GainMaxHp raises the maximum and heals by the same amount, and
    // rolls nothing -- which is what makes it safe to mirror in the emulator.
    private static Dictionary<string, object?> ExecuteDebugGainMaxHp(
        Player player,
        Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("amount", out var amountEl)
            || !amountEl.TryGetDecimal(out var amount)
            || amount < 0)
        {
            return Error("debug_gain_max_hp requires a non-negative numeric 'amount' field");
        }

        _ = CreatureCmd.GainMaxHp(player.Creature, amount);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Gaining {amount} max HP",
        };
    }

    // {"action":"debug_upgrade_deck"}
    // Upgrades every upgradable card in the run's deck. The sibling of the above and for
    // the same reason: a deck of upgraded Strikes and Defends is what gets a scripted
    // player past the act 1 boss. CardCmd.Upgrade rolls nothing.
    private static Dictionary<string, object?> ExecuteDebugUpgradeDeck(Player player)
    {
        var deck = CardPile.Get(PileType.Deck, player);
        if (deck == null)
            return Error("No deck");

        var upgradable = deck.Cards.Where(c => c != null && c.IsUpgradable).ToList();
        foreach (var card in upgradable)
            CardCmd.Upgrade(card);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Upgrading {upgradable.Count} cards",
        };
    }

    // {"action":"debug_add_power","power":"VULNERABLE_POWER","amount":2,"target":"CORPSE_SLUG_0"}
    // Stages a power on an enemy (or on the player, with "target":"player"), so a card
    // capture can observe the conditional half of an effect -- Molten Fist reapplying
    // the target's Vulnerable, Spite scaling off HP lost -- instead of only the plain
    // path a fresh combat happens to offer.
    private static Dictionary<string, object?> ExecuteDebugAddPower(
        Player player,
        Dictionary<string, JsonElement> data)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");

        if (!data.TryGetValue("power", out var powerEl) || powerEl.ValueKind != JsonValueKind.String)
            return Error("debug_add_power requires a string 'power' field (e.g. VULNERABLE_POWER)");
        if (!data.TryGetValue("amount", out var amountEl) || !amountEl.TryGetDecimal(out var amount))
            return Error("debug_add_power requires a numeric 'amount' field");

        var wanted = powerEl.GetString()!;
        var canonical = ModelDb.AllPowers.FirstOrDefault(p =>
            string.Equals(SafeGetText(() => p.Id.Entry), wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.GetType().Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (canonical == null)
            return Error($"Unknown power: {wanted}");

        var combatState = player.Creature.CombatState;
        if (combatState == null)
            return Error("No combat state");

        var targetId = data.TryGetValue("target", out var targetEl) && targetEl.ValueKind == JsonValueKind.String
            ? targetEl.GetString()!
            : "player";
        var target = string.Equals(targetId, "player", StringComparison.OrdinalIgnoreCase)
            ? player.Creature
            : ResolveTarget(combatState, targetId);
        if (target == null)
            return Error($"Target '{targetId}' not found among alive enemies");

        // PowerCmd.Apply needs a choice context and asserts the power is mutable, and it
        // stalls unless the task is driven the way CombatManager drives its own hooks.
        _ = ApplyPowerAsync(player, canonical.ToMutable(), target, amount);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Applying {amount} {SafeGetText(() => canonical.Id.Entry)} to {targetId}",
        };
    }

    private static async Task ApplyPowerAsync(
        Player player,
        PowerModel power,
        Creature target,
        decimal amount)
    {
        var context = new HookPlayerChoiceContext(
            player,
            LocalContext.NetId!.Value,
            GameActionType.CombatPlayPhaseOnly);
        var task = PowerCmd.Apply(context, power, target, amount, applier: null, cardSource: null);
        await context.AssignTaskAndWaitForPauseOrCompletion(task);
    }

    // Counts room entries that have RUN TO COMPLETION.
    //
    // `RunManager.EnterRoomInternal` is async — it pushes the room, awaits
    // `room.Enter` (which preloads that room's assets), and fires `RoomEntered` as its
    // very last statement. The HTTP state reports a non-menu state as soon as the run
    // exists, which is *during* that await, and anything that tears the run down in
    // that window (our save-and-quit between captures) makes the in-flight task NRE:
    // the "internal error!" popup, ~1 embark in 5.
    //
    // Polling proxies for "the room looks ready" narrowed the window but could not
    // close it — the event model answers before its assets finish loading. This is the
    // game's own completion signal, so a harness that waits for it cannot lose the
    // race. Monotonic on purpose: callers read it before embarking and wait for it to
    // move, which needs no reset semantics.
    private static int _roomsEntered;

    internal static int RoomsEntered => _roomsEntered;

    internal static void TrackRoomEntries()
    {
        RunManager.Instance.RoomEntered += () => _roomsEntered++;
    }

}
