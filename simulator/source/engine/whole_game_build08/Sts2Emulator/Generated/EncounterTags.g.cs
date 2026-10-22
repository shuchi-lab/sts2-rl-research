// AUTO-GENERATED — do not edit. Re-run scripts/generate_encounter_tags.py.
namespace Sts2Emulator.GeneratedData;

/// <summary>
/// Each encounter's <c>EncounterTag</c>s, which <c>AddWithoutRepeatingTags</c>
/// avoids repeating back to back.
/// </summary>
/// <remarks>
/// Not cosmetic: <c>GrabBag.GrabIndex</c> rejection-samples, so a missing tag
/// changes how many draws a grab COSTS and moves every draw after it — the boss,
/// the ancient, the next act's whole generation. See E66, and E81 for the four
/// entries the hand-written table had lost.
/// </remarks>
internal static class EncounterTags
{
    private static readonly string[] None = [];

    /// <summary>The tags for an encounter id, empty when it declares none.</summary>
    public static string[] For(int encounterId) =>
        encounterId switch
        {
            1 => ["Chomper"], // Chompers
            2 => ["Nibbit"], // NibbitsWeak
            3 => ["Slimes"], // SlimesWeak
            4 => ["Exoskeletons"], // Exoskeletons
            8 => ["Crawler"], // FuzzyWurmCrawler
            9 => ["Slugs"], // CorpseSlugs
            11 => ["Shrinker"], // ShrinkerBeetle
            12 => ["Seapunk"], // Seapunk
            16 => ["Slimes"], // SlimesNormal
            17 => ["Mushroom", "Slimes"], // FlyconidNormal
            18 => ["Mushroom", "Jaxfruit"], // SnappingJaxfruitNormal
            21 => ["Shrinker", "Crawler"], // OvergrowthCrawlers
            27 => ["Jaxfruit", "Slimes"], // SlitheringStrangler
            31 => ["Workers"], // BowlbugsWeak
            32 => ["Workers"], // Bowlbugs
            33 => ["Burrower"], // Tunneler
            34 => ["Burrower", "Chomper"], // TunnelerAndChomper
            35 => ["Thieves"], // ThievingHopper
            37 => ["Workers"], // SlumberingBeetle
            49 => ["Scrolls"], // ScrollsWeak
            50 => ["Scrolls"], // Scrolls
            70 => ["Knights"], // Knights
            87 => ["Exoskeletons"], // ExoskeletonsNormal
            _ => None,
        };

    /// <summary>
    /// The <c>Id.Entry</c> of every encounter whose GenerateMonsters draws.
    /// </summary>
    /// <remarks>
    /// Generated because the hand-kept version of this table was the SILENT half
    /// of the plumbing: a builder can be given its seed and still fall back to the
    /// combat rng, because the seed only exists if the encounter is listed here.
    /// Nothing errors — the roster just comes out of the wrong stream. See E90.
    ///
    /// Keyed by MODEL name rather than encounter id: two models can share one
    /// emulator id (CorpseSlugs weak and normal do) and they have different
    /// entries, so the id alone cannot answer.
    /// </remarks>
    public static string? EntryForModel(string model) =>
        model switch
        {
            "BowlbugsNormal" => "BOWLBUGS_NORMAL",
            "BowlbugsWeak" => "BOWLBUGS_WEAK",
            "CorpseSlugsNormal" => "CORPSE_SLUGS_NORMAL",
            "CorpseSlugsWeak" => "CORPSE_SLUGS_WEAK",
            "DecimillipedeElite" => "DECIMILLIPEDE_ELITE",
            "FlyconidNormal" => "FLYCONID_NORMAL",
            "PunchOffEventEncounter" => "PUNCH_OFF_EVENT_ENCOUNTER",
            "RubyRaidersNormal" => "RUBY_RAIDERS_NORMAL",
            "ScrollsOfBitingNormal" => "SCROLLS_OF_BITING_NORMAL",
            "ScrollsOfBitingWeak" => "SCROLLS_OF_BITING_WEAK",
            "SlimesNormal" => "SLIMES_NORMAL",
            "SlimesWeak" => "SLIMES_WEAK",
            "SlitheringStranglerNormal" => "SLITHERING_STRANGLER_NORMAL",
            "TwoTailedRatsNormal" => "TWO_TAILED_RATS_NORMAL",
            _ => null,
        };

    /// <summary>
    /// Each act's four encounter pools, in the act's own declaration order.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing and is NOT the act's <c>BossDiscoveryOrder</c>:
    /// the game filters <c>GenerateAllEncounters()</c>, which is declared
    /// alphabetically, and the grab bags are dealt in that order. Generated for
    /// every act because GLORY's were a placeholder reusing Hive's — reproducing
    /// act 1's and Hive's exactly is what makes Glory's trustworthy.
    /// </remarks>
    public static int[] Pool(int actId, string kind) =>
        (actId, kind) switch
        {
            (1, "Weak") => [8, 2, 11, 3],
            // Overgrowth Weak: FuzzyWurmCrawlerWeak, NibbitsWeak, ShrinkerBeetleWeak, SlimesWeak
            (1, "Normal") => [19, 17, 29, 5, 14, 15, 21, 28, 16, 27, 18, 20],
            // Overgrowth Normal: CubexConstructNormal, FlyconidNormal, FogmogNormal, InkletsNormal, MawlerNormal, NibbitsNormal, OvergrowthCrawlers, RubyRaidersNormal, SlimesNormal, SlitheringStranglerNormal, SnappingJaxfruitNormal, VineShamblerNormal
            (1, "Elite") => [62, 68, 65],
            // Overgrowth Elite: BygoneEffigyElite, ByrdonisElite, PhrogParasiteElite
            (1, "Boss") => [74, 82, 83],
            // Overgrowth Boss: CeremonialBeastBoss, TheKinBoss, VantomBoss
            (2, "Weak") => [9, 12, 10, 13],
            // Underdocks Weak: CorpseSlugsWeak, SeapunkWeak, SludgeSpinnerWeak, ToadpolesWeak
            (2, "Normal") => [9, 0, 23, 7, 26, 30, 24, 12, 25, 6],
            // Underdocks Normal: CorpseSlugsNormal, CultistsNormal, FossilStalkerNormal, GremlinMercNormal, HauntedShipNormal, LivingFogNormal, PunchConstructNormal, SeapunkNormal, SewerClamNormal, TwoTailedRatsNormal
            (2, "Elite") => [72, 86, 67],
            // Underdocks Elite: PhantasmalGardenersElite, SkulkingColonyElite, TerrorEelElite
            (2, "Boss") => [77, 79, 84],
            // Underdocks Boss: LagavulinMatriarchBoss, SoulFyshBoss, WaterfallGiantBoss
            (3, "Weak") => [31, 4, 35, 33],
            // Hive Weak: BowlbugsWeak, ExoskeletonsWeak, ThievingHopperWeak, TunnelerWeak
            (3, "Normal") => [32, 1, 87, 41, 40, 36, 39, 37, 38, 53],
            // Hive Normal: BowlbugsNormal, ChompersNormal, ExoskeletonsNormal, HunterKillerNormal, LouseProgenitorNormal, MytesNormal, OvicopterNormal, SlumberingBeetleNormal, SpinyToadNormal, TheObscuraNormal
            (3, "Elite") => [69, 63, 64],
            // Hive Elite: DecimillipedeElite, EntomancerElite, InfestedPrismsElite
            (3, "Boss") => [75, 76, 81],
            // Hive Boss: KaiserCrabBoss, KnowledgeDemonBoss, TheInsatiableBoss
            (4, "Weak") => [43, 49, 47],
            // Glory Weak: DevotedSculptorWeak, ScrollsOfBitingWeak, TurretOperatorWeak
            (4, "Normal") => [42, 54, 44, 45, 46, 48, 50, 51, 52],
            // Glory Normal: AxebotsNormal, ConstructMenagerieNormal, FabricatorNormal, FrogKnightNormal, GlobeHeadNormal, OwlMagistrateNormal, ScrollsOfBitingNormal, SlimedBerserkerNormal, TheLostAndForgottenNormal
            (4, "Elite") => [70, 71, 66],
            // Glory Elite: KnightsElite, MechaKnightElite, SoulNexusElite
            (4, "Boss") => [73, 78, 80],
            // Glory Boss: AeonglassBoss, QueenBoss, TestSubjectBoss
            _ => [],
        };
}
