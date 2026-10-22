using System.Runtime.InteropServices;
using System.Text;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Rng;
using Sts2Emulator.Core.Run;

namespace Sts2Emulator.Interop;

// CombatObservation owns the vector layout; clients obtain sizes and offsets
// through Sts2_Obs* exports. It contains player scalars, cards, potions, powers,
// enemies, legacy secondary intents, gold, card selection, orbs, incoming cards,
// then the complete ordered intents. The last block holds three intents per
// enemy: (type + 1, damage per hit, hit count), with zero for an absent intent.

public static class NativeExports
{
    public const int OBS_SIZE = CombatObservation.ObsSize;
    public const int MAX_HAND = 10;
    public const int MAX_ENEMIES = 6;
    public const int MAX_PLAYER_BUFFS = 10;
    public const int MAX_ENEMY_BUFFS = 5;

    // v17: observation carries an open card selection (kind, count, candidates).
    // v23: the selection block's own offsets are exported, so a reader can decode it.
    // v28: Sts2_Clone forks a combat, determinizing it when asked. Combat had no clone at
    //      all, so a combat search had to restart and replay to reach a node -- and could
    //      only determinize at the root, which is foresight.
    // v29: Sts2_GetRelics reads the relic list back. Sts2_ResetArena could always PASS
    //      relics in, and the combat observation still does not carry them, so a combat
    //      with relics was conditioned on state nothing could read.
    // v30: full ordered enemy intents, including type, damage per hit and hit count.
    // v31: standard resets honor Ascension without a floor or forced encounter and
    //      use the post-Neow Ironclad baseline. Negative floor means unspecified.
    // v32: full-width combat seeds, canonical C# text conversion, and read-only
    //      combat RNG snapshots. The old signed-int Create entry point is retained.
    public const int NATIVE_API_VERSION = 32;

    private sealed class NativeCombat
    {
        public readonly ulong Seed;
        public readonly CombatState State = new();
        public CountingRandom Rng { get; private set; }

        /// <summary>
        /// A fork of <paramref name="source" />, for tree search.
        /// </summary>
        /// <remarks>
        /// With a <paramref name="resampleSeed" /> this is a DETERMINIZATION and needs both
        /// halves: <c>Clone</c> reseeds every stream so the rolls still to come differ, and
        /// <c>ReshuffleUnknownDrawOrder</c> re-orders the draw pile, which a reseed cannot
        /// reach because that order was already materialised. Doing only the first leaves a
        /// search that reads its own draw order and plays with foresight.
        ///
        /// Without one it is a faithful copy, which for a search is an oracle. It exists
        /// because a test wanting "the same fight twice" is a real need, and because the
        /// difference between the two is the thing worth asserting on.
        /// </remarks>
        public NativeCombat(NativeCombat source, int? resampleSeed)
        {
            Seed = source.Seed;
            State = source.State.Clone(resampleSeed);
            Rng = source.Rng.Reseeded(resampleSeed)!;
            LastPlayerWon = source.LastPlayerWon;
            if (resampleSeed is not null)
            {
                State.ReshuffleUnknownDrawOrder(State.ShuffleRng ?? Rng);
            }
        }

        public bool LastPlayerWon { get; set; }

        public NativeCombat(ulong seed)
        {
            Seed = seed;
            Rng = new CountingRandom(seed);
            Reset();
        }

        /// <summary>
        /// Re-seeds every per-subsystem stream from the handle's seed and clears the
        /// win flag. Every Reset overload below opens with exactly this, so it lives in
        /// one place: six copies is six chances for a new stream to be added to five of
        /// them.
        /// </summary>
        private void SeedStreams(int ascension = Ascension.DefaultLevel)
        {
            State.AscensionLevel = ascension;
            State.MaxPotionSlots = Ascension.Value(ascension, Ascension.TightBelt, 2, 3);
            Rng = new CountingRandom(Seed);
            State.NicheHpRng = new CountingRandom(new GameRng(Seed, "niche").RawSeed);
            State.ShuffleRng = new CountingRandom(new GameRng(Seed, "shuffle").RawSeed);
            // Enemy intent rolls come off the run's "monster_ai" stream. Leaving this
            // unset silently fell back to the combat rng, so every enemy whose opening
            // move is a random branch (LeafSlimeS, SludgeSpinner, Exoskeleton...) drew
            // from the wrong generator — invisible for the many enemies whose opening
            // move is deterministic, wrong for the ones that roll.
            State.AiRng = new CountingRandom(new GameRng(Seed, "monster_ai").RawSeed);
            // Which enemy a random-target effect hits comes off "combat_targets"
            // (JuggernautPower, Volley, Sword Boomerang). Same failure mode as AiRng
            // above: unset, it silently drew from the combat rng.
            State.TargetRng = new CountingRandom(new GameRng(Seed, "combat_targets").RawSeed);
            // Picking WHICH card to exhaust or transform comes off
            // "combat_card_selection" (Cinder, Thrash, True Grit, Entropy).
            State.CardSelectionRng = new CountingRandom(
                new GameRng(Seed, "combat_card_selection").RawSeed
            );
            // Rolling up a NEW card comes off "combat_card_generation".
            State.CardGenerationRng = new CountingRandom(
                new GameRng(Seed, "combat_card_generation").RawSeed
            );
            // Alchemize rolls its potion off "combat_potion_generation".
            State.PotionGenerationRng = new CountingRandom(
                new GameRng(Seed, "combat_potion_generation").RawSeed
            );
            State.EnergyCostRng = new CountingRandom(
                new GameRng(Seed, "combat_energy_costs").RawSeed
            );
            State.OrbGenerationRng = new CountingRandom(new GameRng(Seed, "combat_orbs").RawSeed);
            LastPlayerWon = false;
        }

        public void Reset()
        {
            ResetStarter(-1, -1, -1, Ascension.DefaultLevel, []);
        }

        public void Reset(ReadOnlySpan<int> deckIds)
        {
            SeedStreams();
            CombatFactory.Reset(State, Rng, deckIds);
            CombatEngine.ResolvePrePlayPhase(State, Rng);
        }

        public void Reset(ReadOnlySpan<int> deckIds, int encounterId)
        {
            SeedStreams();
            CombatFactory.Reset(State, Rng, deckIds, encounterId);
            CombatEngine.ResolvePrePlayPhase(State, Rng);
        }

        // v0.111.0 Ironclad inventory after Neow's opening heal, before any blessing.
        // This is a component-test baseline: neither a floor nor a seed reconstructs
        // prior run decisions or consumed positions of the named RNG streams.
        public void ResetStarter(
            int encounterId,
            int completedCombatRooms,
            int totalFloor,
            int ascension,
            ReadOnlySpan<int> extraCardIds
        )
        {
            SeedStreams(ascension);
            var deckIds = RunConstants
                .StarterDeckIds.ToArray()
                .Where(id =>
                    id != RunConstants.CardAscendersBane
                    || Ascension.Has(ascension, Ascension.AscendersBane)
                )
                .Concat(extraCardIds.ToArray())
                .ToArray();
            // Select once before seeding its composition. Random encounter mode is
            // a training sampler, not the run's upfront encounter queue.
            if (encounterId < 0)
            {
                encounterId = (int)CombatFactory.SelectFirstCombatEncounter(Rng);
            }
            ulong? encounterRngSeed =
                totalFloor < 0
                    ? null
                    : EncounterRng.SeedFor(
                        Seed,
                        totalFloor,
                        encounterId,
                        completedCombatRooms is >= 0 and < 3
                    );
            CombatFactory.Reset(
                State,
                Rng,
                deckIds,
                encounterId,
                [RunConstants.RelicBurningBlood],
                Ascension.Value(ascension, Ascension.WearyTraveler, 64, 80),
                80,
                [],
                99,
                encounterRngSeed: encounterRngSeed,
                completedCombatRoomsBeforeCurrent: completedCombatRooms
            );
            CombatEngine.ResolvePrePlayPhase(State, Rng);
        }

        public void Reset(ReadOnlySpan<int> deckIds, int encounterId, ReadOnlySpan<int> relicIds)
        {
            SeedStreams();
            CombatFactory.Reset(State, Rng, deckIds, encounterId, relicIds);
            CombatEngine.ResolvePrePlayPhase(State, Rng);
        }

        /// <summary>
        /// The full combat entry point: an arbitrary deck, relic set, potion set, HP and
        /// ascension against a chosen encounter. Unlike the standard Ironclad reset,
        /// the caller supplies the complete inventory and current HP.
        /// </summary>
        /// <remarks>
        /// A negative card id means UPGRADED, matching <c>CombatFactory.Reset</c>'s own
        /// <c>new CardInstance(Math.Abs(id), id &lt; 0)</c>. Enchantments are not
        /// expressible in a flat id list and are not carried here; a deck sampled for
        /// training therefore has none, which is honest for act 1 and a gap in acts 2-3
        /// where the ancients hand them out.
        /// </remarks>
        public void ResetArena(
            ReadOnlySpan<int> deckIds,
            ReadOnlySpan<int> enchantIds,
            ReadOnlySpan<int> enchantAmounts,
            int encounterId,
            ReadOnlySpan<int> relicIds,
            ReadOnlySpan<int> potionIds,
            int playerHp,
            int playerMaxHp,
            int playerGold,
            int ascension,
            int totalFloor,
            int completedCombatRooms
        )
        {
            // The enchantment arrays run parallel to the deck and may be empty, which
            // reads as Enchantment.None on every card. An id list alone cannot carry
            // them, and ACT 1 hands them out: Self-Help Book, Stone of All Time and
            // Symbiote sit in both act-1 event pools, Sapphire Seed and Wood Carvings in
            // Underdocks', and six Shop-rarity relics grant one apiece.
            var deck = new CardInstance[deckIds.Length];
            for (int i = 0; i < deckIds.Length; i++)
            {
                int cardId = deckIds[i];
                deck[i] = new CardInstance(
                    Math.Abs(cardId),
                    cardId < 0,
                    Enchantment: i < enchantIds.Length
                        ? (Enchantment)enchantIds[i]
                        : Enchantment.None,
                    EnchantAmount: i < enchantAmounts.Length ? enchantAmounts[i] : 0
                );
            }

            SeedStreams(ascension);
            // -1 means "no forced encounter": the factory rolls the seeded first combat.
            // It has to become null rather than travel as -1, which would be looked up as
            // an encounter id and found missing.
            int? encounter = encounterId >= 0 ? encounterId : null;
            ulong? encounterRngSeed = encounter is { } id
                ? Sts2Emulator.Core.Run.EncounterRng.SeedFor(
                    Seed,
                    totalFloor,
                    id,
                    completedCombatRooms is >= 0 and < 3
                )
                : null;
            CombatFactory.Reset(
                State,
                Rng,
                (ReadOnlySpan<CardInstance>)deck,
                encounter,
                relicIds,
                playerHp,
                playerMaxHp,
                potionIds,
                playerGold,
                deckPreShuffled: false,
                shuffleRng: null,
                encounterRngSeed: encounterRngSeed,
                nicheSkipCount: 0,
                aiRng: null,
                completedCombatRoomsBeforeCurrent: completedCombatRooms
            );
            CombatEngine.ResolvePrePlayPhase(State, Rng);
        }
    }

    private static readonly NativeCombat?[] _pool = new NativeCombat?[256];

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsSize")]
    public static int Sts2_ObsSize() => OBS_SIZE;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_MaxEnemies")]
    public static int Sts2_MaxEnemies() => MAX_ENEMIES;

    // The combat observation's layout, so the Python side can read it rather than
    // restate it. scripts/trace.py used to carry the offsets as magic numbers (8, 34, 54,
    // 15, 144) and went silently wrong the moment a card slot grew from two fields to
    // four -- it decoded zero enemies and 77 live-fixture tests failed at once.
    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsCardSlotSize")]
    public static int Sts2_ObsCardSlotSize() => CombatObservation.CardSlotSize;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsHandOffset")]
    public static int Sts2_ObsHandOffset() => CombatObservation.HandOffset;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsMaxHand")]
    public static int Sts2_ObsMaxHand() => CombatObservation.MaxHand;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsPlayerBuffOffset")]
    public static int Sts2_ObsPlayerBuffOffset() => CombatObservation.PlayerBuffOffset;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsMaxPlayerBuffs")]
    public static int Sts2_ObsMaxPlayerBuffs() => CombatObservation.MaxPlayerBuffs;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsMaxEnemyBuffs")]
    public static int Sts2_ObsMaxEnemyBuffs() => CombatObservation.MaxEnemyBuffs;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsEnemyOffset")]
    public static int Sts2_ObsEnemyOffset() => CombatObservation.EnemyOffset;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsEnemySlotSize")]
    public static int Sts2_ObsEnemySlotSize() => CombatObservation.EnemySlotSize;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsSecondaryIntentOffset")]
    public static int Sts2_ObsSecondaryIntentOffset() => CombatObservation.SecondaryIntentOffset;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsIntentOffset")]
    public static int Sts2_ObsIntentOffset() => CombatObservation.IntentOffset;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsMaxIntentsPerEnemy")]
    public static int Sts2_ObsMaxIntentsPerEnemy() => CombatObservation.MaxIntentsPerEnemy;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsIntentSlotSize")]
    public static int Sts2_ObsIntentSlotSize() => CombatObservation.IntentSlotSize;

    // The open card-selection block. Its offsets sit BETWEEN the exported secondary-intent
    // offset and the exported orb offsets, and the two block sizes that separate them are
    // not exported -- so the selection block is the one part of the observation a reader
    // cannot derive from what is already published. Anything decoding a Burning Pact or a
    // Survivor screen needs all four, and computing them from a remembered layout is the
    // magic-number failure the exports above exist to prevent.
    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsSelectionKindOffset")]
    public static int Sts2_ObsSelectionKindOffset() => CombatObservation.SelectionKindOffset;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsSelectionCountOffset")]
    public static int Sts2_ObsSelectionCountOffset() => CombatObservation.SelectionCountOffset;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsSelectionOffset")]
    public static int Sts2_ObsSelectionOffset() => CombatObservation.SelectionOffset;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsMaxSelectionCandidates")]
    public static int Sts2_ObsMaxSelectionCandidates() => CombatObservation.MaxSelectionCandidates;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsOrbCapacityOffset")]
    public static int Sts2_ObsOrbCapacityOffset() => CombatObservation.OrbCapacityOffset;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsOrbOffset")]
    public static int Sts2_ObsOrbOffset() => CombatObservation.OrbOffset;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsMaxOrbs")]
    public static int Sts2_ObsMaxOrbs() => CombatObservation.MaxOrbs;

    /// <summary>
    /// The incoming-cards block, which E449 appended AFTER the orb ring. Exposed because
    /// the layout test asserted the orbs were last and silently stopped meaning anything
    /// when they were not -- Python could not see this block to check it.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsIncomingOffset")]
    public static int Sts2_ObsIncomingOffset() => CombatObservation.IncomingOffset;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsMaxIncomingCards")]
    public static int Sts2_ObsMaxIncomingCards() => CombatObservation.MaxIncomingCards;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ObsOrbSlotSize")]
    public static int Sts2_ObsOrbSlotSize() => CombatObservation.OrbSlotSize;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_NativeApiVersion")]
    public static int Sts2_NativeApiVersion() => NATIVE_API_VERSION;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_Create")]
    public static int Sts2_Create(int seed) => CreateCombat(unchecked((uint)seed));

    [UnmanagedCallersOnly(EntryPoint = "Sts2_Create64")]
    public static int Sts2_Create64(ulong seed) => CreateCombat(seed);

    private static int CreateCombat(ulong seed)
    {
        var combat = new NativeCombat(seed);
        for (int i = 0; i < _pool.Length; i++)
        {
            if (_pool[i] is null)
            {
                _pool[i] = combat;
                return i;
            }
        }
        return -1; // pool exhausted
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_HashString")]
    public static unsafe ulong Sts2_HashString(byte* text, int length) =>
        DeterministicHash.GetDeterministicHashCode(
            Encoding.UTF8.GetString(new ReadOnlySpan<byte>(text, length))
        );

    [UnmanagedCallersOnly(EntryPoint = "Sts2_DeriveRunSeed")]
    public static unsafe ulong Sts2_DeriveRunSeed(byte* text, int length) =>
        RunRngSet.DeriveSeed(Encoding.UTF8.GetString(new ReadOnlySpan<byte>(text, length)));

    // Return the required UTF-8 byte count. A short output buffer is not written.
    // Explicit lengths preserve embedded NULs, unlike null-terminated strings.
    [UnmanagedCallersOnly(EntryPoint = "Sts2_CanonicalizeSeed")]
    public static unsafe int Sts2_CanonicalizeSeed(
        byte* text,
        int length,
        byte* output,
        int capacity
    )
    {
        string value = SeedHelper.Canonicalize(
            Encoding.UTF8.GetString(new ReadOnlySpan<byte>(text, length))
        );
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        if (capacity >= encoded.Length)
            encoded.CopyTo(new Span<byte>(output, encoded.Length));
        return encoded.Length;
    }

    /// <summary>
    /// Fork a combat. Pass <paramref name="resampleHidden" /> non-zero to determinize the
    /// copy from <paramref name="resampleSeed" />: every stream reseeded and the draw pile
    /// reshuffled. A clone taken without it is a faithful copy, which for a tree search is
    /// an oracle. See docs/agent-interface.md.
    /// </summary>
    /// <returns>The new handle, or -1 if the source is unknown or the pool is full.</returns>
    [UnmanagedCallersOnly(EntryPoint = "Sts2_Clone")]
    public static unsafe int Sts2_Clone(
        int handle,
        int resampleHidden,
        int resampleSeed,
        int* obsBuf
    )
    {
        if (handle < 0 || handle >= _pool.Length || _pool[handle] is not { } source)
        {
            return -1;
        }

        var copy = new NativeCombat(source, resampleHidden != 0 ? resampleSeed : null);
        for (int i = 0; i < _pool.Length; i++)
        {
            if (_pool[i] is null)
            {
                _pool[i] = copy;
                WriteObs(copy.State, obsBuf);
                return i;
            }
        }
        return -1; // pool exhausted
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_Reset")]
    public static unsafe void Sts2_Reset(int handle, int* obsBuf)
    {
        var combat = _pool[handle]!;
        combat.Reset();
        WriteObs(combat.State, obsBuf);
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ResetEncounter")]
    public static unsafe void Sts2_ResetEncounter(int handle, int encounterId, int* obsBuf)
    {
        var combat = _pool[handle]!;
        combat.ResetStarter(encounterId, -1, -1, Ascension.DefaultLevel, []);
        WriteObs(combat.State, obsBuf);
    }

    // Like Sts2_ResetEncounter but with the weak-combat context:
    // completedCombatRooms in [0,3) selects weak encounter variants (fewer/weaker
    // enemies on early floors); -1 keeps the normal variant.
    [UnmanagedCallersOnly(EntryPoint = "Sts2_ResetEncounterWeak")]
    public static unsafe void Sts2_ResetEncounterWeak(
        int handle,
        int encounterId,
        int completedCombatRooms,
        int* obsBuf
    )
    {
        var combat = _pool[handle]!;
        combat.ResetStarter(encounterId, completedCombatRooms, -1, Ascension.DefaultLevel, []);
        WriteObs(combat.State, obsBuf);
    }

    // Like Sts2_ResetEncounterWeak but also passing the run's TotalFloor, which is what
    // seeds the per-encounter RNG (EncounterModel.Rng). Required for any encounter that
    // rolls its own composition — Slimes rosters, Corpse Slug starting moves.
    [UnmanagedCallersOnly(EntryPoint = "Sts2_ResetEncounterAtFloor")]
    public static unsafe void Sts2_ResetEncounterAtFloor(
        int handle,
        int encounterId,
        int completedCombatRooms,
        int totalFloor,
        int ascension,
        int* obsBuf
    )
    {
        var combat = _pool[handle]!;
        combat.ResetStarter(encounterId, completedCombatRooms, totalFloor, ascension, []);
        WriteObs(combat.State, obsBuf);
    }

    // Sts2_ResetEncounterAtFloor with the deck spelled out, for a capture that stacks
    // it. Some states can only be reached by winning — Phrog Parasite's Wrigglers spawn
    // when it dies, Terror Eel's second phase when an unblocked hit drops it to its
    // threshold — and a starter deck cannot get there before the player is dead. The
    // live side adds the same cards with debug_add_card, so both decks must be built the
    // same way: appended in the same order, then shuffled.
    [UnmanagedCallersOnly(EntryPoint = "Sts2_ResetEncounterAtFloorWithExtraCards")]
    public static unsafe void Sts2_ResetEncounterAtFloorWithExtraCards(
        int handle,
        int* extraCardIds,
        int extraLen,
        int encounterId,
        int completedCombatRooms,
        int totalFloor,
        int ascension,
        int* obsBuf
    )
    {
        var combat = _pool[handle]!;
        combat.ResetStarter(
            encounterId,
            completedCombatRooms,
            totalFloor,
            ascension,
            new ReadOnlySpan<int>(extraCardIds, extraLen)
        );
        WriteObs(combat.State, obsBuf);
    }

    // The mod's debug_add_card, mirrored: put a card on top of the hand mid-combat.
    // A differential capture needs this to reach states the starter deck cannot — the
    // Phrog Parasite's Wrigglers only spawn when it dies, and Terror Eel's second phase
    // only when an unblocked hit drops it to its threshold. Adding to the HAND rather
    // than the deck keeps it deterministic: no shuffle is involved, so both sides can
    // place the same card in the same slot without having to agree on a reshuffle.
    [UnmanagedCallersOnly(EntryPoint = "Sts2_DebugAddCardToHand")]
    public static unsafe void Sts2_DebugAddCardToHand(
        int handle,
        int cardId,
        int upgraded,
        int* obsBuf
    )
    {
        var combat = _pool[handle]!;
        // CardPilePosition.Top, which for the hand is index 0.
        combat.State.Hand.Insert(0, new CardInstance(cardId, upgraded != 0));
        WriteObs(combat.State, obsBuf);
    }

    /// <summary>
    /// Raise the player's maximum HP and heal by the same amount, mid-combat.
    /// </summary>
    /// <remarks>
    /// The mod's `debug_gain_max_hp` for a FIGHT rather than a run. A boss capture is
    /// only worth as much as the number of turns it survives: the Kaiser Crab kills a
    /// starter deck on turn four, two moves short of walking either half's table, and a
    /// capture that stops short of a move can never put that move under test.
    /// </remarks>
    /// <summary>
    /// The kind of card selection the combat is waiting on, or 0 for none.
    /// </summary>
    /// <remarks>
    /// A differential capture has to answer the emulator's screen at the same point the
    /// live game answers its own, and the two do not open at the same INSTANT: the live
    /// game acts when `end_turn` is posted, the emulator when `step` is called. Counting
    /// the live screens and replaying that many steps looks equivalent and is not — a
    /// poll can see one screen twice, and the extra step lands with nothing open, where
    /// it means "play card 0". Asking is the only thing that cannot double-count.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Sts2_PendingSelectionKind")]
    public static int Sts2_PendingSelectionKind(int handle) =>
        (int)(_pool[handle]!.State.PendingSelection?.Kind ?? CardSelectionKind.None);

    /// <summary>
    /// The call count of one named RNG stream, so a differential harness can compare
    /// stream POSITIONS instead of inferring them from outcomes.
    /// </summary>
    /// <remarks>
    /// The mod reports `battle.rng.&lt;Stream&gt;.counter` for the live side and
    /// `CountingRandom` has carried `CallCount` all along; this is the half that was
    /// missing. A move table that diverges because one side made a draw the other did not
    /// shows up here as an off-by-one on the exact turn it happens, where the intents only
    /// say that a later roll came out differently.
    ///
    /// Stream ids follow the game's own names: 0 Shuffle, 1 CombatTargets,
    /// 2 CombatCardSelection, 3 CombatCardGeneration, 4 CombatEnergyCosts,
    /// 5 CombatOrbGeneration, 6 Niche, 7 MonsterAi. A stream the combat never built
    /// reports -1, which is not the same as a stream sitting at 0.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Sts2_RngCallCount")]
    public static int Sts2_RngCallCount(int handle, int stream)
    {
        return CombatRng(_pool[handle]!.State, stream)?.CallCount ?? -1;
    }

    private static CountingRandom? CombatRng(CombatState state, int stream) =>
        stream switch
        {
            0 => state.ShuffleRng,
            1 => state.TargetRng,
            2 => state.CardSelectionRng,
            3 => state.CardGenerationRng,
            4 => state.EnergyCostRng,
            5 => state.OrbGenerationRng,
            6 => state.NicheHpRng,
            7 => state.AiRng as CountingRandom,
            8 => state.PotionGenerationRng,
            _ => null,
        };

    // Read-only diagnostic state: counter followed by the four Xoshiro256** words.
    // No restore operation is exposed; parity checks must advance the actual episode.
    [UnmanagedCallersOnly(EntryPoint = "Sts2_RngSnapshot")]
    public static unsafe int Sts2_RngSnapshot(int handle, int stream, ulong* output)
    {
        if (
            handle < 0
            || handle >= _pool.Length
            || _pool[handle] is not { } combat
            || CombatRng(combat.State, stream) is not { } rng
        )
            return -1;
        var snapshot = rng.Snapshot();
        output[0] = (ulong)snapshot.Counter;
        output[1] = snapshot.S0;
        output[2] = snapshot.S1;
        output[3] = snapshot.S2;
        output[4] = snapshot.S3;
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_DebugGainMaxHp")]
    public static unsafe void Sts2_DebugGainMaxHp(int handle, int amount, int* obsBuf)
    {
        var combat = _pool[handle]!;
        combat.State.PlayerMaxHp += amount;
        combat.State.PlayerHp += amount;
        WriteObs(combat.State, obsBuf);
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ResetWithDeck")]
    public static unsafe void Sts2_ResetWithDeck(int handle, int* deckIds, int deckLen, int* obsBuf)
    {
        var combat = _pool[handle]!;
        combat.Reset(new ReadOnlySpan<int>(deckIds, deckLen));
        WriteObs(combat.State, obsBuf);
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ResetWithDeckAndEncounter")]
    public static unsafe void Sts2_ResetWithDeckAndEncounter(
        int handle,
        int* deckIds,
        int deckLen,
        int encounterId,
        int* obsBuf
    )
    {
        var combat = _pool[handle]!;
        combat.Reset(new ReadOnlySpan<int>(deckIds, deckLen), encounterId);
        WriteObs(combat.State, obsBuf);
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ResetWithDeckEncounterAndRelics")]
    public static unsafe void Sts2_ResetWithDeckEncounterAndRelics(
        int handle,
        int* deckIds,
        int deckLen,
        int encounterId,
        int* relicIds,
        int relicLen,
        int* obsBuf
    )
    {
        var combat = _pool[handle]!;
        combat.Reset(
            new ReadOnlySpan<int>(deckIds, deckLen),
            encounterId,
            new ReadOnlySpan<int>(relicIds, relicLen)
        );
        WriteObs(combat.State, obsBuf);
    }

    /// <summary>
    /// Start a combat from an arbitrary run position: any deck, relics, potions, HP and
    /// ascension against any encounter. The deck-conditioned value function trains on
    /// this — nothing else in the native surface can express a deck that is not the
    /// starter deck plus appended cards at full HP with no relics.
    /// </summary>
    /// <param name="encounterId">-1 rolls the seeded first-combat encounter instead.</param>
    [UnmanagedCallersOnly(EntryPoint = "Sts2_ResetArena")]
    public static unsafe void Sts2_ResetArena(
        int handle,
        int* deckIds,
        int deckLen,
        int* enchantIds,
        int enchantLen,
        int* enchantAmounts,
        int enchantAmountLen,
        int encounterId,
        int* relicIds,
        int relicLen,
        int* potionIds,
        int potionLen,
        int playerHp,
        int playerMaxHp,
        int playerGold,
        int ascension,
        int totalFloor,
        int completedCombatRooms,
        int* obsBuf
    )
    {
        var combat = _pool[handle]!;
        combat.ResetArena(
            new ReadOnlySpan<int>(deckIds, deckLen),
            new ReadOnlySpan<int>(enchantIds, enchantLen),
            new ReadOnlySpan<int>(enchantAmounts, enchantAmountLen),
            encounterId,
            new ReadOnlySpan<int>(relicIds, relicLen),
            new ReadOnlySpan<int>(potionIds, potionLen),
            playerHp,
            playerMaxHp,
            playerGold,
            ascension,
            totalFloor,
            completedCombatRooms
        );
        WriteObs(combat.State, obsBuf);
    }

    /// <summary>
    /// Whether <paramref name="enchantment"/> may be applied to <paramref name="cardId"/>,
    /// i.e. <c>Card.CanEnchant</c>.
    /// </summary>
    /// <remarks>
    /// Exposed so a position sampler asks the engine rather than restating the rule. The
    /// arena reset applies whatever enchantment it is handed WITHOUT filtering — it has
    /// to, since it rebuilds a position the run already produced — so nothing downstream
    /// catches an illegal pairing, and a sampler that re-implements the switch drifts
    /// silently the moment an enchantment is added.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "Sts2_CanEnchant")]
    public static int Sts2_CanEnchant(int cardId, int enchantment) =>
        Enchantments.CanEnchant(
            new CardInstance(Math.Abs(cardId), cardId < 0),
            (Enchantment)enchantment
        )
            ? 1
            : 0;

    [UnmanagedCallersOnly(EntryPoint = "Sts2_Step")]
    public static unsafe int Sts2_Step(int handle, int action, int* obsBuf, float* rewardOut)
    {
        var combat = _pool[handle]!;
        var result = CombatEngine.Step(combat.State, action, combat.Rng);
        combat.LastPlayerWon = result.Terminal && result.PlayerWon;
        WriteObs(combat.State, obsBuf);
        *rewardOut = result.Reward;
        return result.Terminal ? 1 : 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_StepTargeted")]
    public static unsafe int Sts2_StepTargeted(
        int handle,
        int action,
        int targetEnemyIdx,
        int* obsBuf,
        float* rewardOut
    )
    {
        var combat = _pool[handle]!;
        var result = CombatEngine.Step(combat.State, action, combat.Rng, targetEnemyIdx);
        combat.LastPlayerWon = result.Terminal && result.PlayerWon;
        WriteObs(combat.State, obsBuf);
        *rewardOut = result.Reward;
        return result.Terminal ? 1 : 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_PlayerWon")]
    public static int Sts2_PlayerWon(int handle)
    {
        return _pool[handle]!.LastPlayerWon ? 1 : 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_EncounterId")]
    public static int Sts2_EncounterId(int handle)
    {
        return _pool[handle]!.State.EncounterId;
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ActionCount")]
    public static int Sts2_ActionCount(int handle)
    {
        return CombatEngine.ValidActions(_pool[handle]!.State).Length;
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_ValidActions")]
    public static unsafe void Sts2_ValidActions(int handle, int* maskBuf, int maxActions)
    {
        var valid = CombatEngine.ValidActions(_pool[handle]!.State);
        for (int i = 0; i < maxActions; i++)
        {
            maskBuf[i] = 0;
        }

        foreach (int a in valid)
        {
            if (a < maxActions)
            {
                maskBuf[a] = 1;
            }
        }
    }

    /// <summary>
    /// Dump one combat pile in true order (index 0 = top of pile), for differential
    /// testing against the live game. The observation vector only carries pile
    /// *counts*, and the STS2MCP mod sorts its `draw_pile` for display, so neither
    /// side exposed an ordered readout before this.
    /// </summary>
    /// <param name="pileId">0 = draw, 1 = hand, 2 = discard, 3 = exhaust.</param>
    /// <param name="buf">Receives 2 ints per card: def id, then upgraded (0/1).</param>
    /// <param name="maxCards">Capacity of <paramref name="buf"/> in cards, not ints.</param>
    /// <returns>
    /// The pile's true card count, which may exceed <paramref name="maxCards"/> — only
    /// the first <paramref name="maxCards"/> are written, so callers can size and retry.
    /// Returns -1 for an unknown <paramref name="pileId"/>.
    /// </returns>
    /// <summary>
    /// The living enemy roster as (DefId, Slot, Hp) triples, in roster order.
    /// </summary>
    /// <remarks>
    /// The observation deliberately carries no def id -- an agent sees HP, block, intent
    /// and buffs, not WHICH creature it faces -- which is right for learning and useless
    /// for a differential sweep. A roster that differs by one is a list of numbers unless
    /// you can name the creatures, and on a fight whose roster changes every turn, like
    /// the Fabricator's, the numbers do not say whether a bot was added, replaced or never
    /// spawned. The live side reports names; this is the other half.
    ///
    /// Living only, matching `living_emu_enemies` on the Python side: the emulator keeps a
    /// corpse at 0 HP so observation indices stay stable, and the game removes it.
    /// </remarks>
    /// <summary>
    /// The def ids whose intents executed during the enemy turn just past, in order.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "Sts2_GetExecuted")]
    public static unsafe int Sts2_GetExecuted(int handle, int* buf, int maxIds)
    {
        var executed = _pool[handle]!.State.ExecutedThisEnemyTurn;
        int written = Math.Min(executed.Count, maxIds);
        for (int i = 0; i < written; i++)
        {
            buf[i] = executed[i];
        }

        return executed.Count;
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_GetEnemies")]
    public static unsafe int Sts2_GetEnemies(int handle, int* buf, int maxEnemies)
    {
        var enemies = _pool[handle]!.State.Enemies.Where(enemy => enemy.Hp > 0).ToList();
        int written = Math.Min(enemies.Count, maxEnemies);
        for (int i = 0; i < written; i++)
        {
            buf[i * 3] = enemies[i].DefId;
            buf[i * 3 + 1] = enemies[i].Slot;
            buf[i * 3 + 2] = enemies[i].Hp;
        }

        return enemies.Count;
    }

    /// <summary>
    /// The player's relics as (DefId, Counter) pairs, in the order the run holds them.
    /// </summary>
    /// <remarks>
    /// The combat observation carries no relics at all -- the RUN observation does, relic
    /// by relic, but a combat entered through <c>Sts2_ResetArena</c> can hold any set and
    /// nothing published it. A value function conditioned on relics it cannot see is
    /// fitting noise, so this is the other half of being able to pass them in.
    ///
    /// The Counter travels too, because it is live state rather than decoration: how many
    /// times a relic has fired is what decides whether it fires again.
    /// </remarks>
    /// <returns>
    /// The true relic count, which may exceed <paramref name="maxRelics" /> -- only the
    /// first that many are written, so a caller can size and retry.
    /// </returns>
    [UnmanagedCallersOnly(EntryPoint = "Sts2_GetRelics")]
    public static unsafe int Sts2_GetRelics(int handle, int* buf, int maxRelics)
    {
        var relics = _pool[handle]!.State.Relics;
        int written = Math.Min(relics.Count, maxRelics);
        for (int i = 0; i < written; i++)
        {
            buf[i * 2] = relics[i].DefId;
            buf[i * 2 + 1] = relics[i].Counter;
        }

        return relics.Count;
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_GetPile")]
    public static unsafe int Sts2_GetPile(int handle, int pileId, int* buf, int maxCards)
    {
        var state = _pool[handle]!.State;
        var pile = pileId switch
        {
            0 => state.DrawPile,
            1 => state.Hand,
            2 => state.DiscardPile,
            3 => state.ExhaustPile,
            _ => null,
        };

        if (pile is null)
        {
            return -1;
        }

        int written = Math.Min(pile.Count, maxCards);
        for (int i = 0; i < written; i++)
        {
            buf[i * 2] = pile[i].DefId;
            buf[i * 2 + 1] = pile[i].Upgraded ? 1 : 0;
        }

        return pile.Count;
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2_Destroy")]
    public static void Sts2_Destroy(int handle)
    {
        _pool[handle] = null;
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_NativeApiVersion")]
    public static int Sts2Run_NativeApiVersion() => RunNativeExports.Sts2Run_NativeApiVersion();

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_ObsSize")]
    public static int Sts2Run_ObsSize() => RunNativeExports.Sts2Run_ObsSize();

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_MaxActions")]
    public static int Sts2Run_MaxActions() => RunNativeExports.Sts2Run_MaxActions();

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_InfoSize")]
    public static int Sts2Run_InfoSize() => RunNativeExports.Sts2Run_InfoSize();

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_ObsLayout")]
    public static unsafe int Sts2Run_ObsLayout(int* buf, int len) =>
        RunNativeExports.Sts2Run_ObsLayout(buf, len);

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_Create")]
    public static int Sts2Run_Create() => RunNativeExports.Sts2Run_Create();

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_Reset")]
    public static unsafe int Sts2Run_Reset(int handle, byte* seedPtr, int seedLen, int* obsBuf)
    {
        return RunNativeExports.Sts2Run_Reset(handle, seedPtr, seedLen, obsBuf);
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_ResetWithAscension")]
    public static unsafe int Sts2Run_ResetWithAscension(
        int handle,
        byte* seedPtr,
        int seedLen,
        int ascension,
        int* obsBuf
    )
    {
        return RunNativeExports.Sts2Run_ResetWithAscension(
            handle,
            seedPtr,
            seedLen,
            ascension,
            obsBuf
        );
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_ResetWithProgressSave")]
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
        return RunNativeExports.Sts2Run_ResetWithProgressSave(
            handle,
            seedPtr,
            seedLen,
            ascension,
            progressPtr,
            progressLen,
            obsBuf
        );
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_ResetWithCharacter")]
    public static unsafe int Sts2Run_ResetWithCharacter(
        int handle,
        byte* seedPtr,
        int seedLen,
        int ascension,
        int character,
        byte* progressPtr,
        int progressLen,
        int* obsBuf
    ) =>
        RunNativeExports.Sts2Run_ResetWithCharacter(
            handle,
            seedPtr,
            seedLen,
            ascension,
            character,
            progressPtr,
            progressLen,
            obsBuf
        );

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_ConfirmRestUpgradeSelection")]
    public static unsafe int Sts2Run_ConfirmRestUpgradeSelection(
        int handle,
        int* deckIndices,
        int count,
        int* obsBuf
    ) => RunNativeExports.Sts2Run_ConfirmRestUpgradeSelection(handle, deckIndices, count, obsBuf);

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_UsePotionOutsideCombat")]
    public static unsafe int Sts2Run_UsePotionOutsideCombat(int handle, int slot, int* obsBuf) =>
        RunNativeExports.Sts2Run_UsePotionOutsideCombat(handle, slot, obsBuf);

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_Step")]
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
        return RunNativeExports.Sts2Run_Step(
            handle,
            action,
            targetEnemyIndex,
            obsBuf,
            rewardOut,
            terminalOut,
            truncatedOut
        );
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_StartCombat")]
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
        return RunNativeExports.Sts2Run_StartCombat(
            handle,
            deckIds,
            deckLen,
            encounterId,
            relicIds,
            relicLen,
            playerHp,
            playerMaxHp,
            potionIds,
            potionLen,
            playerGold,
            completedCombatRoomsBeforeCurrent,
            obsBuf
        );
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_ActionMask")]
    public static unsafe int Sts2Run_ActionMask(int handle, int* maskBuf, int maskLen)
    {
        return RunNativeExports.Sts2Run_ActionMask(handle, maskBuf, maskLen);
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_GetInfo")]
    public static unsafe int Sts2Run_GetInfo(int handle, int* infoBuf, int infoLen)
    {
        return RunNativeExports.Sts2Run_GetInfo(handle, infoBuf, infoLen);
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_GetStateList")]
    public static unsafe int Sts2Run_GetStateList(int handle, int listId, int* outBuf, int outLen)
    {
        return RunNativeExports.Sts2Run_GetStateList(handle, listId, outBuf, outLen);
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_GetPhase")]
    public static int Sts2Run_GetPhase(int handle) => RunNativeExports.Sts2Run_GetPhase(handle);

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_DebugSetHp")]
    public static unsafe int Sts2Run_DebugSetHp(int handle, int hp, int maxHp, int* obsBuf) =>
        RunNativeExports.Sts2Run_DebugSetHp(handle, hp, maxHp, obsBuf);

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_DebugGainMaxHp")]
    public static unsafe int Sts2Run_DebugGainMaxHp(int handle, int amount, int* obsBuf) =>
        RunNativeExports.Sts2Run_DebugGainMaxHp(handle, amount, obsBuf);

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_DebugEnterNextAct")]
    public static unsafe int Sts2Run_DebugEnterNextAct(int handle, int* obsBuf) =>
        RunNativeExports.Sts2Run_DebugEnterNextAct(handle, obsBuf);

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_DebugUpgradeDeck")]
    public static unsafe int Sts2Run_DebugUpgradeDeck(int handle, int* obsBuf) =>
        RunNativeExports.Sts2Run_DebugUpgradeDeck(handle, obsBuf);

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_PlayerWon")]
    public static int Sts2Run_PlayerWon(int handle) => RunNativeExports.Sts2Run_PlayerWon(handle);

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_EncounterId")]
    public static int Sts2Run_EncounterId(int handle) =>
        RunNativeExports.Sts2Run_EncounterId(handle);

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_GetShuffleRngCallCount")]
    public static int Sts2Run_GetShuffleRngCallCount(int handle)
    {
        return RunNativeExports.Sts2Run_GetShuffleRngCallCount(handle);
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_GetNicheRngCallCount")]
    public static int Sts2Run_GetNicheRngCallCount(int handle)
    {
        return RunNativeExports.Sts2Run_GetNicheRngCallCount(handle);
    }

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_Clone")]
    public static unsafe int Sts2Run_Clone(
        int handle,
        int resampleHidden,
        int resampleSeed,
        int* obsBuf
    ) => RunNativeExports.Sts2Run_Clone(handle, resampleHidden, resampleSeed, obsBuf);

    [UnmanagedCallersOnly(EntryPoint = "Sts2Run_Destroy")]
    public static void Sts2Run_Destroy(int handle)
    {
        RunNativeExports.Sts2Run_Destroy(handle);
    }

    // ── observation serialisation ─────────────────────────────────────────────

    private static unsafe void WriteObs(CombatState s, int* o)
    {
        CombatObservation.Write(s, new Span<int>(o, OBS_SIZE));
    }
}
