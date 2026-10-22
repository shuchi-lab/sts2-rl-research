using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Run;

namespace Sts2Emulator.Interop;

public static class RunNativeExports
{
    // v13: the observation carries the shop's whole board, priced.
    // v15: the map offers a whole row, not four children -- Winged Boots' free travel.
    // v16: state list 17 reports an open card-offer grid.
    // v17: state list 19 names the enemies in the active combat, and 20 says what an open
    // card-select screen is FOR.
    // v18: state list 21 reports where on the map the run is standing.
    // v19: the observation no longer carries the encounter behind a map choice, so the
    // layout is one number shorter and every offset after the map's node types moves.
    // v20: reset accepts an explicit ascension; the legacy entry retains its A8 default.
    // v21: reset can use a recorded v0.111.0 progress save, including act discovery.
    // v22: state list 22 exposes every populated potion reward in display order.
    // v23: reward actions 253-255 discard potion slots 3-5 without row collisions.
    // v24: combat observation grows by the full declared-intent block.
    // v25: state list 23 exposes exact float bits for the two reward odds.
    // v26: state list 24 exposes separately claimable special card rewards.
    // v27: lists 25-29 expose ordered combat piles and hand rules on demand.
    // v28: list 33 exposes every independently claimable relic reward in order.
    // List 32 is reserved by the separate, not yet integrated AnyTime potion work.
    // v29: list 34 exposes every combat selection candidate, beyond the compact prefix.
    // v30: list 35 exposes Sea Glass's retained character for exact offer diagnostics.
    // v31: shop actions 250-255 discard potion slots 0-5 (DiscardPotionGameAction).
    // v32: shop actions 244-249 throw a Foul Potion from slots 0-5 at the merchant;
    //      list 37 exposes every player power as (BuffId, display amount) pairs.
    // v33: Sts2Run_ConfirmRestUpgradeSelection confirms a Smith screen with an explicit
    //      list of ticked deck indices as one smith use; Sts2Run_Step is unchanged.
    // v34: Sts2Run_UsePotionOutsideCombat supports Fruit Juice and Ambergris without
    //      consuming a phase-specific scalar action or changing observation layout.
    // v35: character-aware reset and diagnostic list 39; existing layouts stay unchanged.
    public const int RUN_NATIVE_API_VERSION = 35;
    private static readonly RunEngine?[] _pool = new RunEngine?[256];

    public static int Sts2Run_NativeApiVersion() => RUN_NATIVE_API_VERSION;

    public static int Sts2Run_ObsSize() => RunConstants.RunObsSize;

    public static int Sts2Run_MaxActions() => RunConstants.MaxActions;

    public static int Sts2Run_InfoSize() => RunConstants.RunInfoSize;

    /// <summary>How many numbers <see cref="Sts2Run_ObsLayout"/> writes.</summary>
    public const int RUN_OBS_LAYOUT_SIZE = 13;

    /// <summary>
    /// Where the run observation's variable-length blocks sit, so a consumer does not have
    /// to hard-code offsets that move whenever a block grows:
    ///
    /// <c>[scalars, deck offset, deck slots, ints per card, relic offset, relic slots,
    /// ints per relic, shop offset, shop slots, ints per shop slot, map choices, map node
    /// type offset, Lasting Candy's extra-option offset]</c>
    ///
    /// APPENDED, never inserted -- a consumer reading by index must keep reading the same
    /// thing across builds, which is the whole reason this export exists.
    ///
    /// Offsets are relative to the start of the run block, which itself begins at the
    /// combat observation's own size.
    /// </summary>
    /// <returns>How many numbers were written, or -1 if the buffer is too small.</returns>
    public static unsafe int Sts2Run_ObsLayout(int* buf, int len)
    {
        if (len < RUN_OBS_LAYOUT_SIZE)
        {
            return -1;
        }

        var layout = new Span<int>(buf, len);
        layout[0] = RunConstants.RunScalarObsSize;
        layout[1] = RunConstants.DeckObsOffset;
        layout[2] = RunConstants.MaxObservedDeck;
        layout[3] = RunConstants.DeckSlotSize;
        layout[4] = RunConstants.RelicObsOffset;
        layout[5] = RunConstants.MaxObservedRelics;
        layout[6] = RunConstants.RelicSlotSize;
        layout[7] = RunConstants.ShopObsOffset;
        layout[8] = RunConstants.ShopSlots;
        layout[9] = RunConstants.ShopSlotSize;
        layout[10] = RunConstants.MapChoices;
        layout[11] = RunConstants.MapNodeTypeObsOffset;
        layout[12] = RunConstants.FourthRewardObsOffset;
        return RUN_OBS_LAYOUT_SIZE;
    }

    public static int Sts2Run_Create()
    {
        var run = new RunEngine();
        for (int i = 0; i < _pool.Length; i++)
        {
            if (_pool[i] is null)
            {
                _pool[i] = run;
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Fork a run into a new handle. <paramref name="resampleHidden" /> non-zero
    /// resamples everything the agent has not been shown -- future rewards, shop stock,
    /// encounter composition, and the unseen part of the draw pile -- off
    /// <paramref name="resampleSeed" />. A clone taken without it is a faithful copy,
    /// which for a tree search is an oracle. See docs/agent-interface.md.
    /// </summary>
    /// <returns>The new handle, or -1 if the source is unknown or the pool is full.</returns>
    public static unsafe int Sts2Run_Clone(
        int handle,
        int resampleHidden,
        int resampleSeed,
        int* obsBuf
    )
    {
        if (!TryGet(handle, out var run))
        {
            return -1;
        }

        var copy = run.Clone(resampleHidden != 0 ? resampleSeed : null);
        for (int i = 0; i < _pool.Length; i++)
        {
            if (_pool[i] is null)
            {
                _pool[i] = copy;
                copy.WriteObservation(new Span<int>(obsBuf, RunConstants.RunObsSize));
                return i;
            }
        }

        return -1;
    }

    public static unsafe int Sts2Run_Reset(int handle, byte* seedPtr, int seedLen, int* obsBuf) =>
        Sts2Run_ResetWithAscension(handle, seedPtr, seedLen, Ascension.DefaultLevel, obsBuf);

    public static unsafe int Sts2Run_ResetWithAscension(
        int handle,
        byte* seedPtr,
        int seedLen,
        int ascension,
        int* obsBuf
    )
    {
        if (!TryGet(handle, out var run) || ascension < 0 || ascension > Ascension.DoubleBoss)
        {
            return -1;
        }

        string seed = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(seedPtr, seedLen));
        run.Reset(seed, ProgressionContext.AllUnlocked, ascension);
        run.WriteObservation(new Span<int>(obsBuf, RunConstants.RunObsSize));
        return 0;
    }

    public static unsafe int Sts2Run_ResetWithProgressSave(
        int handle,
        byte* seedPtr,
        int seedLen,
        int ascension,
        byte* progressPtr,
        int progressLen,
        int* obsBuf
    )
    {
        if (
            !TryGet(handle, out var run)
            || ascension < 0
            || ascension > Ascension.DoubleBoss
            || seedLen < 0
            || progressLen < 0
        )
            return -1;
        ProgressionContext progression;
        try
        {
            string progress = Encoding.UTF8.GetString(
                new ReadOnlySpan<byte>(progressPtr, progressLen)
            );
            progression = ProgressionContext.FromProgressSaveJson(progress);
        }
        catch (Exception error)
            when (error
                    is JsonException
                        or ArgumentException
                        or KeyNotFoundException
                        or InvalidOperationException
            )
        {
            return -2;
        }
        string seed = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(seedPtr, seedLen));
        run.Reset(seed, progression, ascension);
        run.WriteObservation(new Span<int>(obsBuf, RunConstants.RunObsSize));
        return 0;
    }

    public static unsafe int Sts2Run_ResetWithCharacter(
        int handle,
        byte* seedPtr,
        int seedLen,
        int ascension,
        int character,
        byte* progressPtr,
        int progressLen,
        int* obsBuf
    )
    {
        if (
            !TryGet(handle, out var run)
            || ascension < 0
            || ascension > Ascension.DoubleBoss
            || character < 0
            || character > (int)CharacterId.Silent
            || seedLen < 0
            || progressLen < 0
            || (seedLen > 0 && seedPtr == null)
            || (progressLen > 0 && progressPtr == null)
            || obsBuf == null
        )
            return -1;
        ProgressionContext progression = ProgressionContext.AllUnlocked;
        if (progressLen > 0)
        {
            try
            {
                progression = ProgressionContext.FromProgressSaveJson(
                    Encoding.UTF8.GetString(new ReadOnlySpan<byte>(progressPtr, progressLen))
                );
            }
            catch (Exception error)
                when (error
                        is JsonException
                            or ArgumentException
                            or KeyNotFoundException
                            or InvalidOperationException
                )
            {
                return -2;
            }
        }
        string seed = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(seedPtr, seedLen));
        run.Reset(seed, progression, ascension, (CharacterId)character);
        run.WriteObservation(new Span<int>(obsBuf, RunConstants.RunObsSize));
        return 0;
    }

    public static unsafe int Sts2Run_Step(
        int handle,
        int action,
        int targetEnemyIndex,
        int* obsBuf,
        float* rewardOut,
        int* terminalOut,
        int* truncatedOut
    )
    {
        if (!TryGet(handle, out var run))
        {
            return -1;
        }

        int status = run.Step(
            action,
            targetEnemyIndex,
            out float reward,
            out bool terminal,
            out bool truncated
        );
        run.WriteObservation(new Span<int>(obsBuf, RunConstants.RunObsSize));
        *rewardOut = reward;
        *terminalOut = terminal ? 1 : 0;
        *truncatedOut = truncated ? 1 : 0;
        return status;
    }

    /// <summary>
    /// <see cref="RunEngine.ConfirmRestUpgradeSelection" />: -1 (run untouched) unless a
    /// Smith screen is open and every index is a distinct upgradable deck card.
    /// </summary>
    public static unsafe int Sts2Run_ConfirmRestUpgradeSelection(
        int handle,
        int* deckIndices,
        int count,
        int* obsBuf
    )
    {
        if (!TryGet(handle, out var run) || count <= 0 || deckIndices == null)
        {
            return -1;
        }

        int status = run.ConfirmRestUpgradeSelection(new ReadOnlySpan<int>(deckIndices, count));
        run.WriteObservation(new Span<int>(obsBuf, RunConstants.RunObsSize));
        return status;
    }

    public static unsafe int Sts2Run_UsePotionOutsideCombat(int handle, int slot, int* obsBuf)
    {
        if (!TryGet(handle, out var run) || obsBuf == null)
            return -1;
        int status = run.UsePotionOutsideCombat(slot);
        run.WriteObservation(new Span<int>(obsBuf, RunConstants.RunObsSize));
        return status;
    }

    public static unsafe int Sts2Run_StartCombat(
        int handle,
        int* deckIds,
        int deckLen,
        int encounterId,
        int* relicIds,
        int relicLen,
        int playerHp,
        int playerMaxHp,
        int* potionIds,
        int potionLen,
        int playerGold,
        int completedCombatRoomsBeforeCurrent,
        int* obsBuf
    )
    {
        if (!TryGet(handle, out var run))
        {
            return -1;
        }

        int status = run.StartCombat(
            new ReadOnlySpan<int>(deckIds, deckLen),
            encounterId,
            new ReadOnlySpan<int>(relicIds, relicLen),
            playerHp,
            playerMaxHp,
            new ReadOnlySpan<int>(potionIds, potionLen),
            playerGold,
            completedCombatRoomsBeforeCurrent
        );
        run.WriteObservation(new Span<int>(obsBuf, RunConstants.RunObsSize));
        return status;
    }

    public static unsafe int Sts2Run_ActionMask(int handle, int* maskBuf, int maskLen)
    {
        if (!TryGet(handle, out var run))
        {
            return -1;
        }

        run.WriteActionMask(new Span<int>(maskBuf, maskLen));
        return 0;
    }

    public static unsafe int Sts2Run_GetInfo(int handle, int* infoBuf, int infoLen)
    {
        if (!TryGet(handle, out var run))
        {
            return -1;
        }

        run.WriteInfo(new Span<int>(infoBuf, infoLen));
        return 0;
    }

    public static unsafe int Sts2Run_GetStateList(int handle, int listId, int* outBuf, int outLen)
    {
        if (!TryGet(handle, out var run))
        {
            return -1;
        }

        if (outLen < 0)
        {
            return -2;
        }

        Span<int> output = new(outBuf, outLen);
        return listId switch
        {
            0 => WriteCardList(run.State.Deck, output),
            1 => WriteRelicList(run.State.Relics, output),
            // The ACTIVE COMBAT's belt while there is one, exactly as the observation does
            // (`RunEngine`: `activeCombat?.PotionSlots ?? State.PotionSlots`). A combat is
            // handed a COPY of the run's slots and only syncs back when it ends, so reading
            // the run's here reported the belt as it stood when the fight started -- every
            // potion drunk, auto-fired or picked up mid-combat was invisible until the
            // fight was over. Three of the four known run-trace divergences were this one
            // line. See E492.
            2 => WriteIntArray(ActivePotionSlots(run.State), output),
            3 => WriteIntArray(run.State.NeowOptions, output),
            4 => WriteIntArray(run.State.ShopCosts, output),
            5 => WriteBoolArray(run.State.RewardUpgraded, output),
            6 => WriteRewardList(run.State, output),
            7 => WriteMapOptionCoords(run.State, output),
            8 => WriteIntArray(run.State.ShopCards, output),
            9 => WriteIntArray(run.State.ShopRelics, output),
            10 => WriteIntArray(run.State.ShopPotions, output),
            // 11-14: run-generation data, for differential testing against a live
            // save's acts[].rooms (see scripts/verify_run_generation.py).
            11 => WriteIntArray(run.State.NormalEncounterSequence, output),
            12 => WriteIntArray(run.State.EliteEncounterSequence, output),
            13 => WriteIntArray(run.State.EventSequence, output),
            14 => WriteIntArray(
                [run.State.Act, run.State.BossEncounterId, run.State.MapNodes.Count],
                output
            ),
            // 15: the whole map as (col, row, nodeType) triples, ordered so the
            // readout is stable. Lets the differential test compare map *structure*
            // against a live save's saved_map.points, not just a node count.
            15 => WriteIntArray(
                [
                    .. run
                        .State.MapNodes.Values.OrderBy(n => n.Row)
                        .ThenBy(n => n.Col)
                        .SelectMany(n => new[] { n.Col, n.Row, n.NodeType }),
                ],
                output
            ),
            // 16: every edge as a (col, row, childCol, childRow) quadruple. Node
            // positions alone do not pin a map — the same 62 dots can be wired
            // differently — and the live save records each point's `children`, so the
            // differential test can check connectivity rather than assume it.
            16 => WriteIntArray(
                [
                    .. run
                        .State.MapNodes.Values.OrderBy(n => n.Row)
                        .ThenBy(n => n.Col)
                        .SelectMany(n =>
                            n.Children.OrderBy(c => c.Row)
                                .ThenBy(c => c.Col)
                                .SelectMany(c => new[] { n.Col, n.Row, c.Col, c.Row })
                        ),
                ],
                output
            ),
            // 17: the cards on an OFFER grid, if one is open. A card-select phase is two
            // different screens wearing one phase -- a grid of cards the run has rolled
            // and is offering (CardSelectCmd.FromChooseACardScreen, which the game calls
            // `card_select` and resolves on the click) versus a selection over the deck
            // (which toggles and needs a confirm). A replay has to tell them apart to know
            // whether an answer needs a confirm after it, and guessing from a screen
            // message is not the same as asking the run.
            17 => WriteIntArray(run.State.PendingOfferCards, output),
            // 18: Scroll Boxes' bundles, flat -- bundle 0's three cards then bundle 1's.
            // The screen offers a WHOLE bundle, so an agent needs all six to choose
            // between them, and a replay needs them to map the live screen's indexes.
            18 => WriteIntArray(run.State.BundleOffer, output),
            // 19: the DefId of every enemy in the active combat, in the engine's own enemy
            // order -- the DEAD included, because the enemy list keeps its dead where the
            // game removes them, and slot i here has to name the same creature as enemy
            // slot i of the observation. The observation carries an enemy's hp, block,
            // intent and buffs but never says WHICH enemy it is, so nothing outside the
            // engine could put a name to what it was fighting.
            19 => WriteIntArray(
                run.State.ActiveCombat is { } combat
                    ? [.. combat.Enemies.Select(enemy => enemy.DefId)]
                    : [],
                output
            ),
            // 20: what an open card-select screen is FOR, as
            // (DeckSelection, its argument, whether it is a rest-site upgrade). The
            // card-select phase offers a list of the deck and says nothing about what
            // answering it does -- removal, upgrade, transform and Dolly's Mirror are one
            // screen and four different decisions, and the difference is not recoverable
            // from the cards on offer.
            20 => WriteIntArray(
                [
                    (int)run.State.PendingSelectionKind,
                    run.State.PendingSelectionArg,
                    run.State.PendingRestUpgrade ? 1 : 0,
                ],
                output
            ),
            // 21: where the run is standing, as (col, row). Lists 15 and 16 give the
            // map's nodes and its edges but not the position they are read from, and
            // without it a drawn map has no "you are here" -- which is most of what
            // looking at a map is for. Before selecting a new act's ancient there
            // is no current position; the internal (3, 0) is only a travel anchor.
            21 => WriteIntArray(
                run.State.AwaitingActStartNode
                    ? []
                    : [run.State.CurrentMapCoord.Col, run.State.CurrentMapCoord.Row],
                output
            ),
            22 => WritePotionRewardList(run.State, output),
            // Diagnostic state, not a new observation feature. Preserve the original
            // float bits through the integer-list ABI; reading consumes no RNG.
            // Order: card-rarity offset, potion-reward odds.
            23 => WriteIntArray(
                [
                    BitConverter.SingleToInt32Bits((float)run.State.CardRarityOffset),
                    BitConverter.SingleToInt32Bits(run.State.PotionRewardOdds),
                ],
                output
            ),
            // Specific cards returned as separately claimable SpecialCardRewards.
            24 => WriteIntArray(
                [
                    .. run.State.PendingSpecialCardRewards.SelectMany(card =>
                        new[] { card.DefId, card.Upgraded ? 1 : 0 }
                    ),
                ],
                output
            ),
            25 => WriteCardList(run.State.ActiveCombat?.DrawPile ?? [], output),
            26 => WriteCardList(run.State.ActiveCombat?.DiscardPile ?? [], output),
            27 => WriteCardList(run.State.ActiveCombat?.ExhaustPile ?? [], output),
            28 => WriteCombatDiagnostics(run.State.ActiveCombat, output),
            29 => WriteHandRules(run.State.ActiveCombat, output),
            // Every gold row on the reward screen, in screen order: list 6 carries only the
            // primary pile, and a Heist's stolen-back gold queues behind it as its own row.
            30 => WriteGoldRewardList(run.State, output),
            // Six stable Fake Merchant slots: (relic id or 0, retained rolled price).
            31 => WriteFakeMerchantStock(run.State, output),
            33 => WriteRelicRewardList(run.State, output),
            34 => WriteCombatSelection(run.State.ActiveCombat, output),
            // Sea Glass's character affects both its displayed title and offered cards.
            // Read the retained draw; do not draw again to reconstruct the display.
            35 => WriteIntArray([run.State.SeaGlassCharacter], output),
            // 36: 1 if the offer-grid card at the same index as list 17 is upgraded.
            36 => WriteOfferCardUpgradedList(run.State, output),
            // 37: every player power as (BuffId, displayed amount) pairs, untruncated.
            37 => WritePlayerPowerList(run.State, output),
            // Crystal Sphere's visible board only: count, tool, 121 hidden flags,
            // then touched items as (kind, x, y, width, height). No concealed items.
            38 => WriteCrystalSphereList(run.State.CrystalSphere, output),
            39 => WriteIntArray([(int)run.State.Character], output),
            _ => -3,
        };
    }

    public static int Sts2Run_GetPhase(int handle)
    {
        return TryGet(handle, out var run) ? (int)run.State.Phase : -1;
    }

    /// <summary>
    /// Hand a run extra HP, for soaking only. A random or greedy policy dies around
    /// floor six and never exercises the back half of the act, so scripts/soak_act_one.py
    /// uses this to reach the boss. It is a DEBUG hook and not a game rule: anything a
    /// boosted soak turns up has to be reproduced on an untouched run before it counts.
    /// </summary>
    public static unsafe int Sts2Run_DebugSetHp(int handle, int hp, int maxHp, int* obsBuf)
    {
        if (!TryGet(handle, out var run))
        {
            return -1;
        }

        run.State.PlayerMaxHp = Math.Max(1, maxHp);
        run.State.PlayerHp = Math.Clamp(hp, 1, run.State.PlayerMaxHp);
        run.WriteObservation(new Span<int>(obsBuf, RunConstants.RunObsSize));
        return 0;
    }

    /// <summary>
    /// <c>CreatureCmd.GainMaxHp</c>: raise the maximum and heal by the same amount.
    /// </summary>
    /// <remarks>
    /// The mirror of the mod's <c>debug_gain_max_hp</c>, and it exists so a BUFFED live
    /// capture can be replayed. Not the same as <c>DebugSetHp</c>, which sets absolutes:
    /// the game's command heals as it raises, so a replay that only moved the maximum
    /// would diverge on HP one step after the buff. It routes through the same
    /// <c>RunNonCombatEffects.GainMaxHp</c> that every relic uses, which is the version
    /// captures have already checked.
    /// </remarks>
    public static unsafe int Sts2Run_DebugGainMaxHp(int handle, int amount, int* obsBuf)
    {
        if (!TryGet(handle, out var run))
        {
            return -1;
        }

        Core.Run.RunNonCombatEffects.GainMaxHp(run.State, amount);
        run.WriteObservation(new Span<int>(obsBuf, RunConstants.RunObsSize));
        return 0;
    }

    /// <summary>
    /// <c>RunManager.EnterNextAct</c> on demand — the mod's <c>debug_enter_next_act</c>.
    /// </summary>
    /// <remarks>
    /// Not a shortcut around the rules: it calls the same <c>RunEngine.EnterNextAct</c>
    /// the boss reward does. What it skips is having to WIN act 1 first, which is several
    /// minutes of buffed run per act-2 data point and a boss fight that can lose.
    /// Returns 1 when an act was entered and 0 when the run was already in its last.
    /// </remarks>
    public static unsafe int Sts2Run_DebugEnterNextAct(int handle, int* obsBuf)
    {
        if (!TryGet(handle, out var run))
        {
            return -1;
        }

        bool entered = run.EnterNextAct();
        run.WriteObservation(new Span<int>(obsBuf, RunConstants.RunObsSize));
        return entered ? 1 : 0;
    }

    /// <summary>Upgrade every upgradable card in the deck. Debug hook, as above.</summary>
    /// <remarks>
    /// Every mutating export refreshes the observation before returning, and these three
    /// did not — which is invisible for a soak, because it steps immediately afterwards
    /// and the next step rewrites it anyway, and fatal for a replay, whose snapshot for
    /// the buff step IS the buffed state. The deck is read out of the observation buffer
    /// while HP is read from the live info struct, so an unrefreshed buffer showed the
    /// max HP moving and the upgrades not happening at all.
    /// </remarks>
    public static unsafe int Sts2Run_DebugUpgradeDeck(int handle, int* obsBuf)
    {
        if (!TryGet(handle, out var run))
        {
            return -1;
        }

        for (int i = 0; i < run.State.Deck.Count; i++)
        {
            if (Core.Run.RunConstants.IsRunCardUpgradable(run.State.Deck[i]))
            {
                run.State.Deck[i] = run.State.Deck[i] with { Upgraded = true };
            }
        }

        run.WriteObservation(new Span<int>(obsBuf, RunConstants.RunObsSize));
        return 0;
    }

    public static int Sts2Run_PlayerWon(int handle)
    {
        return TryGet(handle, out var run) && run.State.LastPlayerWon ? 1 : 0;
    }

    public static int Sts2Run_EncounterId(int handle)
    {
        return TryGet(handle, out var run) ? run.ActiveEncounterId : -1;
    }

    public static int Sts2Run_GetShuffleRngCallCount(int handle)
    {
        return TryGet(handle, out var run) ? run.ActiveShuffleRngCallCount : 0;
    }

    public static int Sts2Run_GetNicheRngCallCount(int handle)
    {
        return TryGet(handle, out var run) ? run.ActiveNicheRngCallCount : 0;
    }

    public static void Sts2Run_Destroy(int handle)
    {
        if ((uint)handle < _pool.Length)
        {
            _pool[handle] = null;
        }
    }

    /// <summary>
    /// List 28: (round, player side, potion slots). The first is the game's
    /// `CombatState.RoundNumber`, which STS2MCP reports as `battle.round`.
    /// </summary>
    internal static int WriteCombatDiagnostics(CombatState? combat, Span<int> output) =>
        WriteIntArray(
            combat is { } active
                ? [active.RoundNumber, active.PlayerTurn ? 1 : 0, active.MaxPotionSlots]
                : [],
            output
        );

    private static int WriteHandRules(CombatState? combat, Span<int> output)
    {
        if (combat is null)
        {
            return 0;
        }

        int count = Math.Min(combat.Hand.Count, output.Length / 2);
        for (int i = 0; i < count; i++)
        {
            var card = combat.Hand[i];
            var def = GeneratedData.Cards.Get(card.DefId);
            // The game displays X separately and clamps ordinary energy costs at zero.
            output[2 * i] = def.HasEnergyCostX
                ? -1
                : Math.Max(0, CombatEngine.EffectiveCost(card, combat));
            output[2 * i + 1] = CombatEngine.CanPlayFromHand(combat, card) ? 1 : 0;
        }

        return combat.Hand.Count * 2;
    }

    private static int WriteCardList(IReadOnlyList<CardInstance> cards, Span<int> output)
    {
        int count = Math.Min(cards.Count, output.Length);
        for (int i = 0; i < count; i++)
        {
            output[i] = cards[i].Upgraded ? -cards[i].DefId : cards[i].DefId;
        }

        return cards.Count;
    }

    internal static int WriteCombatSelection(CombatState? combat, Span<int> output)
    {
        if (combat?.PendingSelection is not { } selection)
            return 0;
        for (int i = 0; i < Math.Min(output.Length, selection.Candidates.Count); i++)
        {
            var card = CombatObservation.SelectionCard(combat, selection, i);
            output[i] = card.Upgraded ? -card.DefId : card.DefId;
        }
        return selection.Candidates.Count;
    }

    private static int WriteRelicList(IReadOnlyList<RelicInstance> relics, Span<int> output)
    {
        int count = Math.Min(relics.Count, output.Length);
        for (int i = 0; i < count; i++)
        {
            output[i] = relics[i].DefId;
        }

        return relics.Count;
    }

    internal static int WriteRewardList(RunState state, Span<int> output)
    {
        Span<int> rewards = stackalloc int[4];
        rewards[0] = state.RewardGold;
        rewards[1] = state.RewardPotion;
        rewards[2] = state.RelicReward;
        // How many CARD items the screen holds, not whether it holds one: Kaleidoscope
        // puts two there at once.
        rewards[3] = (state.RewardCardPending ? 1 : 0) + state.PendingCardOffers.Count;
        int count = Math.Min(rewards.Length, output.Length);
        rewards[..count].CopyTo(output);
        return rewards.Length;
    }

    internal static int WriteGoldRewardList(RunState state, Span<int> output) =>
        WriteIntArray(
            [
                .. state.RewardGold != 0 ? new[] { state.RewardGold } : [],
                .. state.PendingGoldRewards,
            ],
            output
        );

    internal static int WriteRelicRewardList(RunState state, Span<int> output) =>
        WriteIntArray(
            [
                .. state.RelicReward != 0 ? new[] { state.RelicReward } : [],
                .. state.PendingBonusRelicRewards,
            ],
            output
        );

    internal static int WritePotionRewardList(RunState state, Span<int> output) =>
        WriteIntArray(
            [
                .. state.RewardPotion != 0 ? new[] { state.RewardPotion } : [],
                .. state.PendingPotionRewards,
            ],
            output
        );

    internal static int WriteFakeMerchantStock(RunState state, Span<int> output)
    {
        if (
            state.Phase != RunPhase.FakeMerchant
            || state.EventRelicStock is not { } stock
            || state.FakeMerchantPrices is not { } prices
        )
            return 0;

        int required = stock.Count * 2;
        for (int i = 0; i < Math.Min(required, output.Length); i++)
            output[i] = (i & 1) == 0 ? stock[i / 2] : prices[i / 2];
        return required;
    }

    private static int WriteMapOptionCoords(RunState state, Span<int> output)
    {
        int required = RunConstants.MapChoices * 2;
        int count = Math.Min(required, output.Length);
        for (int i = 0; i < count / 2; i++)
        {
            var coord = state.MapOptionCoords[i];
            output[i * 2] = coord?.Col ?? -1;
            output[i * 2 + 1] = coord?.Row ?? -1;
        }
        return required;
    }

    /// <summary>
    /// The belt as it stands RIGHT NOW: the active combat's while a fight is in progress,
    /// the run's otherwise.
    /// </summary>
    /// <remarks>
    /// A combat is handed a COPY of the run's slots at the start and only syncs back when
    /// it ends, so reading the run's during a fight reported the belt as it stood when the
    /// fight began -- every potion drunk, auto-fired or gained mid-combat was invisible
    /// until the fight was over. Three of the four known run-trace divergences were this:
    /// a Fairy in a Bottle that had already revived the player, a Potion Shaped Rock
    /// already spent, and a Fire Potion the run had picked up. See E492.
    ///
    /// The PHASE guard is not optional and is the whole reason this is a method.
    /// `State.ActiveCombat` is only nulled on `Reset`, so a finished combat lingers on the
    /// run; taking its slots outside `RunPhase.Combat` reports the belt from whatever fight
    /// happened last. Written without the guard first, and it cost twenty-four traces --
    /// `RunEngine`'s observation has always spelled it `State.Phase == RunPhase.Combat ?
    /// State.ActiveCombat : null`, which is exactly this.
    /// </remarks>
    private static int[] ActivePotionSlots(Core.Run.RunState state) =>
        state.Phase == Core.Run.RunPhase.Combat && state.ActiveCombat is { } combat
            ? combat.PotionSlots
            : state.PotionSlots;

    private static int WriteIntArray(int[] values, Span<int> output)
    {
        int count = Math.Min(values.Length, output.Length);
        values.AsSpan(0, count).CopyTo(output);
        return values.Length;
    }

    /// <summary>
    /// List 36, index for index with list 17 (`PendingOfferCards`): whether the card a
    /// pick of that slot adds is upgraded. Read through the pick's own resolver, so the
    /// flag is the generated instance's -- Molten Egg's upgraded Attacks read 1 -- and never
    /// an assumed base.
    /// </summary>
    internal static int WriteOfferCardUpgradedList(RunState state, Span<int> output)
    {
        int length = state.PendingOfferCards.Length;
        int count = Math.Min(length, output.Length);
        for (int i = 0; i < count; i++)
        {
            output[i] = state.PendingOfferCardAt(i).Upgraded ? 1 : 0;
        }

        return length;
    }

    /// <summary>
    /// List 37: all of <c>PlayerBuffs</c> as flat (BuffId, displayed amount) pairs, in
    /// order, duplicates and internal entries included. The compact observation stops at
    /// <see cref="CombatObservation.MaxPlayerBuffs" />, and an internal companion entry
    /// (CrimsonMantleSelfDamage) can push a visible power past it. Empty outside combat.
    /// </summary>
    internal static int WritePlayerPowerList(RunState state, Span<int> output) =>
        WritePlayerPowerList(state.Phase == RunPhase.Combat ? state.ActiveCombat : null, output);

    internal static int WritePlayerPowerList(CombatState? combat, Span<int> output)
    {
        if (combat is null)
        {
            return 0;
        }

        var buffs = combat.PlayerBuffs;
        int length = 2 * buffs.Count;
        int count = Math.Min(length, output.Length);
        for (int k = 0; k < count; k++)
        {
            var buff = buffs[k / 2];
            output[k] =
                k % 2 == 0 ? (int)buff.Id : CombatObservation.PlayerBuffDisplayAmount(combat, buff);
        }

        return length;
    }

    internal static int WriteCrystalSphereList(CrystalSphereGame? game, Span<int> output)
    {
        if (game is null)
            return 0;
        var visible = game.Items.Where(item =>
            item.Placed
            && Enumerable
                .Range(0, item.Width)
                .Any(dx =>
                    Enumerable
                        .Range(0, item.Height)
                        .Any(dy => !game.IsHidden(item.X + dx, item.Y + dy))
                )
        );
        return WriteIntArray(
            [
                game.Divinations,
                (int)game.Tool,
                .. game.Hidden.Select(hidden => hidden ? 1 : 0),
                .. visible.SelectMany(item =>
                    new[] { (int)item.Kind, item.X, item.Y, item.Width, item.Height }
                ),
            ],
            output
        );
    }

    private static int WriteBoolArray(bool[] values, Span<int> output)
    {
        int count = Math.Min(values.Length, output.Length);
        for (int i = 0; i < count; i++)
        {
            output[i] = values[i] ? 1 : 0;
        }

        return values.Length;
    }

    private static bool TryGet(
        int handle,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RunEngine? run
    )
    {
        if ((uint)handle < _pool.Length && _pool[handle] is { } existing)
        {
            run = existing;
            return true;
        }

        run = null;
        return false;
    }
}
