using System.Text.Json;
using Sts2Emulator.Core.Run;

namespace FullRunWorker;

/// <summary>How the engine's generic event skip (action 3) relates to the game's own options.</summary>
public enum EventSkipRule
{
    /// <summary>The game shows no such option: action 3 is an emulator-only skip and is never offered.</summary>
    Never,

    /// <summary>Action 3 is the game's own LEAVE option (Welcome to Wongos).</summary>
    Always,

    /// <summary>Action 3 is the game's lone option when every other one is absent (Self-Help Book NO_OPTIONS).</summary>
    WhenNoOtherOption,
}

/// <summary>One audited event: its displayed options page by page, from the decompiled event model.</summary>
public sealed record EventSpec(
    int Id,
    string Entry,
    EventSkipRule Skip,
    string Pages,
    // (page, option) -> a fight the option starts (encounter id), when the game says so on the option.
    (int Page, int Option, int Encounter)[] Fights,
    // (page, option) -> named cards the option adds to the deck unconditionally.
    (int Page, int Option, string[] Cards)[] Grants,
    // (page, option) -> unblockable HP or Max HP the option costs, as its text displays.
    (int Page, int Option, int Hp, int MaxHp)[] HpCosts,
    // (page, option) -> fixed gold price (locked below it).
    (int Page, int Option, int Gold)[] GoldCosts,
    bool Audited = true,
    string? Note = null
)
{
    public string DecompiledSource => $"decompiled/MegaCrit.Sts2.Core.Models.Events/{Entry}.cs";
}

/// <summary>
/// The owned event/page option table. Built from the unchanged build08 <c>RunEngine.StepEvent</c>
/// and <c>WriteEventActionMask</c> together with each event's decompiled
/// <c>GenerateInitialOptions</c>/page methods. The engine mask marks action 3 legal for every
/// event; only two events actually display a matching option, so every other action 3 is removed.
/// Pages whose mask offers more than the handler accepts (Dense Vegetation/Punch Off/Lantern Key
/// page 1) are left with their single real option by the same rule.
/// </summary>
public static class EventOptions
{
    public const string Schema = "rs1/event_page_options/v1";

    private static EventSpec E(int id, string entry, string pages,
        (int, int, int)[]? fights = null, (int, int, string[])[]? grants = null,
        (int, int, int, int)[]? hp = null, (int, int, int)[]? gold = null,
        EventSkipRule skip = EventSkipRule.Never, bool audited = true, string? note = null) =>
        new(id, entry, skip, pages, fights ?? [], grants ?? [], hp ?? [], gold ?? [], audited, note);

    public static readonly IReadOnlyDictionary<int, EventSpec> Table = new Dictionary<int, EventSpec>
    {
        [RunConstants.EventUnrestSite] = E(1, "UnrestSite", "p0: REST (full heal, Poor Sleep) / KILL (-8 Max HP, relic)",
            grants: [(0, 0, ["PoorSleep"])], hp: [(0, 1, 0, 8)]),
        [RunConstants.EventAromaOfChaos] = E(2, "AromaOfChaos", "p0: transform a card / upgrade a card"),
        [RunConstants.EventSimpleReward] = E(3, "SimpleReward", "emulator placeholder handler; no decompiled event model",
            audited: false, note: "default StepEvent branch is not a game event; a visit is an explicit unsupported state"),
        [RunConstants.EventJungleMazeAdventure] = E(4, "JungleMazeAdventure", "p0: SOLO (18 damage, gold) / JOIN FORCES (gold)",
            hp: [(0, 0, 18, 0)]),
        [RunConstants.EventMorphicGrove] = E(5, "MorphicGrove", "p0: GROUP (lose all gold, transform two) / LONER (+5 Max HP)"),
        [RunConstants.EventBrainLeech] = E(6, "BrainLeech", "p0: SHARE KNOWLEDGE (keep 1 of 5 own-pool cards) / RIP (5 damage, colorless card reward)",
            hp: [(0, 1, 5, 0)]),
        [RunConstants.EventTheLegendsWereTrue] = E(7, "TheLegendsWereTrue", "p0: Spoils Map / 8 damage potion reward (locked at HP<=8 or full belt)",
            grants: [(0, 0, ["SpoilsMap"])], hp: [(0, 1, 8, 0)]),
        [RunConstants.EventDoorsOfLightAndDark] = E(8, "DoorsOfLightAndDark", "p0: LIGHT (upgrade two random) / DARK (remove one)"),
        [RunConstants.EventSunkenTreasury] = E(9, "SunkenTreasury", "p0: small chest gold / large chest gold + Greed",
            grants: [(0, 1, ["Greed"])]),
        [RunConstants.EventByrdonisNest] = E(10, "ByrdonisNest", "p0: EAT (+7 Max HP) / TAKE THE EGG (Byrdonis Egg)",
            grants: [(0, 1, ["ByrdonisEgg"])]),
        [RunConstants.EventSelfHelpBook] = E(11, "SelfHelpBook", "p0: three enchant options (locked variants absent) or the lone NO_OPTIONS (engine action 3)",
            skip: EventSkipRule.WhenNoOtherOption),
        [RunConstants.EventDenseVegetation] = E(12, "DenseVegetation", "p0: TRUDGE ON (8 damage, gold) / REST (rest heal -> p1); p1: FIGHT (only option)",
            fights: [(1, 0, RunConstants.DenseVegetationEncounterId)], hp: [(0, 0, 8, 0)]),
        [RunConstants.EventLuminousChoir] = E(13, "LuminousChoir", "p0: remove two + Spore Mind / tribute (rolled, displayed price) for a relic",
            grants: [(0, 0, ["SporeMind"])]),
        [RunConstants.EventSapphireSeed] = E(14, "SapphireSeed", "p0: CONSUME (heal 9, upgrade one) / PLANT (Sown enchant)"),
        [RunConstants.EventSunkenStatue] = E(15, "SunkenStatue", "p0: GRAB SWORD (Sword of Stone) / DIVE (gold, 7 damage)",
            hp: [(0, 1, 7, 0)]),
        [RunConstants.EventTabletOfTruth] = E(16, "TabletOfTruth", "p0..4: DECIPHER (Max HP 3/6/12/24/all-but-1) / SMASH (p0 heal 20) or GIVE UP"),
        [RunConstants.EventWellspring] = E(17, "Wellspring", "p0: BOTTLE (potion reward) / BATHE (remove one + Guilty)",
            grants: [(0, 1, ["Guilty"])]),
        [RunConstants.EventWhisperingHollow] = E(18, "WhisperingHollow", "p0: EXCHANGE GOLD (displayed price, two potions) / HUG (transform one, 9 damage)",
            hp: [(0, 1, 9, 0)]),
        [RunConstants.EventWoodCarvings] = E(19, "WoodCarvings", "p0: BIRD (Basic -> Peck) / SNAKE (Slither enchant) / TORUS (Basic -> Toric Toughness)"),
        [RunConstants.EventAbyssalBaths] = E(20, "AbyssalBaths", "p0: IMMERSE (+2 Max HP, 3 damage) / ABSTAIN (heal 10); p>0: LINGER (damage 3+page) / EXIT"),
        [RunConstants.EventDrowningBeacon] = E(21, "DrowningBeacon", "p0: BOTTLE (Glowwater potion reward) / CLIMB (-13 Max HP, Fresnel Lens)",
            hp: [(0, 1, 0, 13)]),
        [RunConstants.EventEndlessConveyor] = E(22, "EndlessConveyor", "p0: GRAB (40 gold) / OBSERVE THE CHEF; p>0: GRAB / LEAVE",
            gold: [(-1, 0, RunConstants.ConveyorGrabCost)]),
        [RunConstants.EventPunchOff] = E(23, "PunchOff", "p0: NAB (Injury, relic reward) / I CAN TAKE THEM (-> p1); p1: FIGHT (only option)",
            fights: [(1, 0, RunConstants.PunchOffEncounterId)], grants: [(0, 0, ["Injury"])]),
        [RunConstants.EventSpiralingWhirlpool] = E(24, "SpiralingWhirlpool", "p0: OBSERVE (Spiral enchant) / DRINK (heal 33%)"),
        [RunConstants.EventTrashHeap] = E(25, "TrashHeap", "p0: DIVE IN (8 damage, relic) / GRAB (100 gold, card)",
            hp: [(0, 0, 8, 0)]),
        [RunConstants.EventWaterloggedScriptorium] = E(26, "WaterloggedScriptorium", "p0: BLOODY INK (+6 Max HP) / QUILL (55 gold, Steady one) / SPONGE (99 gold, Steady two)",
            gold: [(0, 1, RunConstants.ScriptoriumQuillCost), (0, 2, RunConstants.ScriptoriumSpongeCost)]),
        [RunConstants.EventCrystalSphere] = E(27, "CrystalSphere", "p0: UNCOVER FUTURE (displayed price, 3 divinations) / PAYMENT PLAN (Debt, 6 divinations)",
            grants: [(0, 1, ["Debt"])]),
        [RunConstants.EventDollRoom] = E(28, "DollRoom", "p0: RANDOM DOLL / 5 damage choose of two / 15 damage choose of three; p1: two dolls; p2: three dolls",
            hp: [(0, 1, 5, 0), (0, 2, 15, 0)]),
        [RunConstants.EventFakeMerchant] = E(29, "FakeMerchant", "custom merchant layout (RunPhase.FakeMerchant), no event options",
            note: "handled by the FakeMerchant phase; an Event-phase visit is unsupported"),
        [RunConstants.EventPotionCourier] = E(30, "PotionCourier", "p0: three Foul Potions reward / one Uncommon potion reward"),
        [RunConstants.EventRanwidTheElder] = E(31, "RanwidTheElder", "p0: give potion / give 100 gold / give relic (belt locked)",
            gold: [(0, 1, 0)]),
        [RunConstants.EventRelicTrader] = E(32, "RelicTrader", "p0: up to three trades of owned tradable relics, else lone PROCEED"),
        [RunConstants.EventRoomFullOfCheese] = E(33, "RoomFullOfCheese", "p0: GORGE (keep 2 of 8 commons) / SEARCH (14 damage, Chosen Cheese)",
            hp: [(0, 1, 14, 0)]),
        [RunConstants.EventSlipperyBridge] = E(34, "SlipperyBridge", "p*: OVERCOME (lose the shown card) / HOLD ON (3+holds damage)"),
        [RunConstants.EventStoneOfAllTime] = E(35, "StoneOfAllTime", "p0: LIFT (drink a potion, +10 Max HP) / PUSH (6 damage, Vigorous enchant) (belt locked)",
            hp: [(0, 1, 6, 0)]),
        [RunConstants.EventSymbiote] = E(36, "Symbiote", "p0: APPROACH (Corrupted enchant) / KILL WITH FIRE (transform)"),
        [RunConstants.EventTeaMaster] = E(37, "TeaMaster", "p0: BONE TEA (50) / EMBER TEA (150) / TEA OF DISCOURTESY (free)",
            gold: [(0, 0, RunConstants.BoneTeaCost), (0, 1, RunConstants.EmberTeaCost)]),
        [RunConstants.EventTheFutureOfPotions] = E(38, "TheFutureOfPotions", "p0: up to three held-potion trades for upgraded card rewards, else lone PROCEED (belt locked)"),
        [RunConstants.EventThisOrThat] = E(39, "ThisOrThat", "p0: PLAIN (6 damage, gold) / ORNATE (relic, Clumsy)",
            grants: [(0, 1, ["Clumsy"])], hp: [(0, 0, 6, 0)]),
        [RunConstants.EventWarHistorianRepy] = E(40, "WarHistorianRepy", "p0: UNLOCK CAGE (History Course) / UNLOCK CHEST (potion+relic); p1: the other door only"),
        [RunConstants.EventWelcomeToWongos] = E(41, "WelcomeToWongos", "p0: BARGAIN BIN (100) / FEATURED ITEM (200, named rare) / MYSTERY BOX (300) / LEAVE (engine action 3)",
            gold: [(0, 0, RunConstants.WongosBargainBinCost), (0, 1, RunConstants.WongosFeaturedItemCost), (0, 2, RunConstants.WongosMysteryBoxCost)],
            skip: EventSkipRule.Always),
        [RunConstants.EventAmalgamator] = E(42, "Amalgamator", "p0: two Basic Strikes -> Ultimate Strike / two Basic Defends -> Ultimate Defend",
            grants: [(0, 0, ["UltimateStrike"]), (0, 1, ["UltimateDefend"])]),
        [RunConstants.EventBugslayer] = E(43, "Bugslayer", "p0: Exterminate / Squash",
            grants: [(0, 0, ["Exterminate"]), (0, 1, ["Squash"])]),
        [RunConstants.EventColorfulPhilosophers] = E(44, "ColorfulPhilosophers", "p0: one option per other character's pool (no leave); each opens three skippable card rewards",
            note: "compatible continuation: open the first displayed pool and decline its foreign card rewards (zero reward, recorded)"),
        [RunConstants.EventColossalFlower] = E(45, "ColossalFlower", "p0..2: EXTRACT (35/75/135 gold) / REACH DEEPER (5/6/7 damage; p2 Pollinous Core)"),
        [RunConstants.EventFieldOfManSizedHoles] = E(46, "FieldOfManSizedHoles", "p0: RESIST (remove two + Normality) / ENTER YOUR HOLE (Perfect Fit enchant)",
            grants: [(0, 0, ["Normality"])]),
        [RunConstants.EventInfestedAutomaton] = E(47, "InfestedAutomaton", "p0: STUDY (random own-pool Power) / TOUCH CORE (random own-pool 0-cost card)"),
        [RunConstants.EventLostWisp] = E(48, "LostWisp", "p0: CLAIM (Lost Wisp + Decay) / SEARCH (gold)",
            grants: [(0, 0, ["Decay"])]),
        [RunConstants.EventSpiritGrafter] = E(49, "SpiritGrafter", "p0: LET IT IN (heal 25, Metamorphosis) / REJECTION (upgrade one, 10 damage)",
            grants: [(0, 0, ["Metamorphosis"])], hp: [(0, 1, 10, 0)]),
        [RunConstants.EventTheLanternKey] = E(50, "TheLanternKey", "p0: RETURN THE KEY (100 gold) / KEEP THE KEY (-> p1); p1: FIGHT the Mysterious Knight (only option)",
            fights: [(1, 0, RunConstants.MysteriousKnightEncounterId)]),
        [RunConstants.EventZenWeaver] = E(51, "ZenWeaver", "p0: BREATHING TECHNIQUES (50, two Enlightenment) / EMOTIONAL AWARENESS (125, remove one) / ARACHNID ACUPUNCTURE (250, remove two)",
            gold: [(0, 0, RunConstants.ZenWeaverBreathingCost), (0, 1, RunConstants.ZenWeaverEmotionalCost), (0, 2, RunConstants.ZenWeaverAcupunctureCost)]),
        [RunConstants.EventBattlewornDummy] = E(52, "BattlewornDummy", "p0: three settings, each a timed fight; an escape shows DEFEAT and pays nothing",
            fights: [(0, 0, RunConstants.BattlewornDummyEncounterIds0), (0, 1, RunConstants.BattlewornDummyEncounterIds0 + 1),
                (0, 2, RunConstants.BattlewornDummyEncounterIds0 + 2)]),
        [RunConstants.EventGraveOfTheForgotten] = E(53, "GraveOfTheForgotten", "p0: CONFRONT (Decay, Souls Power enchant) / ACCEPT (Forgotten Soul)",
            grants: [(0, 0, ["Decay"])]),
        [RunConstants.EventHungryForMushrooms] = E(54, "HungryForMushrooms", "p0: BIG MUSHROOM / FRAGRANT MUSHROOM (15 damage label)"),
        [RunConstants.EventReflections] = E(55, "Reflections", "p0: TOUCH A MIRROR / SHATTER (copy deck, Bad Luck)"),
        [RunConstants.EventRoundTeaParty] = E(56, "RoundTeaParty", "p0: ENJOY TEA (Royal Poison, full heal) / PICK A FIGHT (-> p1); p1: CONTINUE (11 damage, relic)",
            hp: [(1, 0, 11, 0)]),
        [RunConstants.EventTrial] = E(57, "Trial", "p0: ACCEPT (draw a defendant page) / REJECT (-> p4); p1-3: GUILTY / INNOCENT; p4: ACCEPT / DOUBLE DOWN (ends the run)"),
        [RunConstants.EventTinkerTime] = E(58, "TinkerTime", "p0: one option; p1: two of three card types; p2: two of three riders (Mad Science)"),
        [RunConstants.EventTheArchitect] = E(59, "TheArchitect", "dialogue lines, then WIN RUN"),
    };

    /// <summary>
    /// The real displayed options of the current Event page: the engine mask minus every
    /// artificial generic skip. Locked options are already absent from the mask.
    /// </summary>
    public static List<int> RealOptions(RunState s, ReadOnlySpan<int> mask)
    {
        var options = new List<int>();
        if (s.EventId == RunConstants.EventResultPending)
        {
            // Any action proceeds from a finished page; the game shows one PROCEED.
            if (mask[0] != 0)
                options.Add(0);
            return options;
        }
        if (!Table.TryGetValue(s.EventId, out var spec))
            throw new UnsupportedRunState("unknown_event", $"event id {s.EventId} is not in the audited table");
        if (!spec.Audited)
            throw new UnsupportedRunState("unaudited_event", $"event {spec.Entry} ({spec.Id}): {spec.Note}");
        if (s.EventId == RunConstants.EventFakeMerchant)
            throw new UnsupportedRunState("fake_merchant_in_event_phase", "custom merchant reached the Event phase");
        for (int a = 0; a < RunConstants.EventSkipAction; a++)
        {
            if (mask[a] != 0)
                options.Add(a);
        }
        bool skipLegal = mask[RunConstants.EventSkipAction] != 0;
        switch (spec.Skip)
        {
            case EventSkipRule.Always when skipLegal:
                options.Add(RunConstants.EventSkipAction);
                break;
            case EventSkipRule.WhenNoOtherOption when skipLegal && options.Count == 0:
                options.Add(RunConstants.EventSkipAction);
                break;
        }
        return options;
    }

    public static int? FightOf(EventSpec spec, int page, int option) =>
        spec.Fights.Where(f => f.Page == page && f.Option == option).Select(f => (int?)f.Encounter).FirstOrDefault();

    public static string[] GrantsOf(EventSpec spec, int page, int option) =>
        spec.Grants.Where(g => g.Page == page && g.Option == option).SelectMany(g => g.Cards).ToArray();

    public static (int Hp, int MaxHp) HpCostOf(EventSpec spec, int page, int option)
    {
        foreach (var h in spec.HpCosts)
            if (h.Page == page && h.Option == option)
                return (h.Hp, h.MaxHp);
        return (0, 0);
    }

    public static int GoldCostOf(EventSpec spec, int page, int option)
    {
        foreach (var g in spec.GoldCosts)
            if ((g.Page == page || g.Page == -1) && g.Option == option)
                return g.Gold;
        return 0;
    }

    public static void WriteTable(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("schema", Schema);
        w.WriteString("engine_source", "engine/whole_game_build08/Sts2Emulator/Core/Run/RunEngine.cs StepEvent + WriteEventActionMask");
        w.WriteString("generic_skip_action", RunConstants.EventSkipAction.ToString());
        w.WritePropertyName("events");
        w.WriteStartArray();
        foreach (var spec in Table.Values.OrderBy(x => x.Id))
        {
            w.WriteStartObject();
            w.WriteNumber("event_id", spec.Id);
            w.WriteString("entry", spec.Entry);
            w.WriteString("skip_rule", spec.Skip.ToString());
            w.WriteBoolean("audited", spec.Audited);
            w.WriteString("pages", spec.Pages);
            w.WriteString("decompiled_source", spec.DecompiledSource);
            w.WriteString("note", spec.Note);
            w.WritePropertyName("fights");
            w.WriteStartArray();
            foreach (var f in spec.Fights)
            {
                w.WriteStartObject();
                w.WriteNumber("page", f.Page);
                w.WriteNumber("option", f.Option);
                w.WriteNumber("encounter_id", f.Encounter);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("grants");
            w.WriteStartArray();
            foreach (var g in spec.Grants)
            {
                w.WriteStartObject();
                w.WriteNumber("page", g.Page);
                w.WriteNumber("option", g.Option);
                w.WritePropertyName("cards");
                w.WriteStartArray();
                foreach (var c in g.Cards)
                {
                    w.WriteStartObject();
                    w.WriteString("entry", c);
                    w.WriteNumber("card_id", RunNonCombatEffects.NamedCard(c));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("hp_costs");
            w.WriteStartArray();
            foreach (var h in spec.HpCosts)
            {
                w.WriteStartObject();
                w.WriteNumber("page", h.Page);
                w.WriteNumber("option", h.Option);
                w.WriteNumber("hp", h.Hp);
                w.WriteNumber("max_hp", h.MaxHp);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("gold_costs");
            w.WriteStartArray();
            foreach (var g in spec.GoldCosts)
            {
                w.WriteStartObject();
                w.WriteNumber("page", g.Page);
                w.WriteNumber("option", g.Option);
                w.WriteNumber("gold", g.Gold);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }
}
