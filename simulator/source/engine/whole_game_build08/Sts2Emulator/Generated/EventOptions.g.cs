// AUTO-GENERATED — do not edit. Re-run scripts/generate_event_options.py to update.
namespace Sts2Emulator.GeneratedData;

/// <summary>
/// How many options each event offers, read off the fixed-size EventOption array
/// in the game's own GenerateInitialOptions. An option the run cannot take still
/// occupies its slot -- the game swaps in a locked variant rather than dropping it
/// -- so this count does not move with run state.
///
/// Whether an option is *takeable* is a separate question, and one this table says
/// nothing about: that lives in RunEngine.WriteEventActionMask, per event.
///
/// Events missing here build their options into a List&lt;EventOption&gt;, so their
/// count is run state and cannot be read off the source:
/// ColorfulPhilosophers, FakeMerchant, LuminousChoir, RanwidTheElder, RelicTrader, SelfHelpBook, Symbiote, TeaMaster, TheFutureOfPotions, TinkerTime, WaterloggedScriptorium, WelcomeToWongos.
/// </summary>
internal static class EventOptions
{
    /// <summary>Option count by event id, or 0 when the event is not in the table.</summary>
    public static int CountFor(int eventId) =>
        eventId switch
        {
            1 => 2,   // UnrestSite
            2 => 2,   // AromaOfChaos
            4 => 2,   // JungleMazeAdventure
            5 => 2,   // MorphicGrove
            6 => 2,   // BrainLeech
            7 => 2,   // TheLegendsWereTrue
            8 => 2,   // DoorsOfLightAndDark
            9 => 2,   // SunkenTreasury
            10 => 2,   // ByrdonisNest
            12 => 2,   // DenseVegetation
            14 => 2,   // SapphireSeed
            15 => 2,   // SunkenStatue
            16 => 2,   // TabletOfTruth
            17 => 2,   // Wellspring
            18 => 2,   // WhisperingHollow
            19 => 3,  // has a locked variant   // WoodCarvings
            20 => 2,   // AbyssalBaths
            21 => 2,   // DrowningBeacon
            22 => 2,   // EndlessConveyor
            23 => 2,   // PunchOff
            24 => 2,   // SpiralingWhirlpool
            25 => 2,   // TrashHeap
            27 => 2,   // CrystalSphere
            28 => 3,   // DollRoom
            30 => 2,   // PotionCourier
            33 => 2,   // RoomFullOfCheese
            34 => 2,   // SlipperyBridge
            35 => 2,  // has a locked variant   // StoneOfAllTime
            39 => 2,   // ThisOrThat
            40 => 2,   // WarHistorianRepy
            42 => 2,   // Amalgamator
            43 => 2,   // Bugslayer
            45 => 2,   // ColossalFlower
            46 => 2,   // FieldOfManSizedHoles
            47 => 2,   // InfestedAutomaton
            48 => 2,   // LostWisp
            49 => 2,   // SpiritGrafter
            50 => 2,   // TheLanternKey
            51 => 3,   // ZenWeaver
            52 => 3,   // BattlewornDummy
            53 => 2,  // has a locked variant   // GraveOfTheForgotten
            54 => 2,   // HungryForMushrooms
            55 => 2,   // Reflections
            56 => 2,   // RoundTeaParty
            57 => 2,   // Trial
            _ => 0,
        };
}
