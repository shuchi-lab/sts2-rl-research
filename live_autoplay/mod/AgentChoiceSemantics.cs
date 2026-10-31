using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

// Pure, source-grounded mappings. Native enum ordinals differ from the frozen emulator's ordinals.
public static class AgentChoiceSemantics
{
    public static string EventPageName(string entry, string descriptionKey, IEnumerable<string> optionKeys)
    {
        string prefix = entry + ".pages.";
        string? Page(string key, string separator)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) return null;
            string tail = key.Substring(prefix.Length);
            int end = tail.IndexOf(separator, StringComparison.Ordinal);
            return end > 0 ? tail.Substring(0, end) : null;
        }
        var descriptionPage = Page(descriptionKey, ".");
        if (descriptionPage != null) return descriptionPage;
        // Formatted descriptions (e.g. TRIAL.trialFormat) reuse one text template.
        // Current public option identities retain the already-displayed page;
        // they do not require localized text parsing or reading event RNG.
        var pages = optionKeys.Select(key => Page(key, ".options.")).Distinct().ToArray();
        if (pages.Length == 1 && pages[0] != null) return pages[0]!;
        throw new InvalidOperationException("unsupported:event_page_key:" + descriptionKey);
    }

    // Object identity binds a rating to the same native entry as its action. UI order and definition IDs may repeat.
    public static TControl? ReferenceAnchor<TEntry,TControl>(TEntry entry, IEnumerable<TControl> controls,
        Func<TControl,TEntry?> getEntry, Func<TControl,bool> visible) where TEntry : class where TControl : class
        => controls.Where(c => visible(c) && ReferenceEquals(getEntry(c), entry)).SingleOrDefault();

    // Ordered by the audited frozen engine menu, independent of localized text and UI order.
    private static readonly Dictionary<string, string[]> InitialEventOptions = new()
    {
        ["UNREST_SITE"] = new[] {"REST","KILL"}, ["AROMA_OF_CHAOS"] = new[] {"LET_GO","MAINTAIN_CONTROL"},
        ["JUNGLE_MAZE_ADVENTURE"] = new[] {"SOLO_QUEST","JOIN_FORCES"}, ["MORPHIC_GROVE"] = new[] {"GROUP","LONER"},
        ["BRAIN_LEECH"] = new[] {"SHARE_KNOWLEDGE","RIP"}, ["THE_LEGENDS_WERE_TRUE"] = new[] {"NAB_THE_MAP","SLOWLY_FIND_AN_EXIT"},
        ["DOORS_OF_LIGHT_AND_DARK"] = new[] {"LIGHT","DARK"}, ["SUNKEN_TREASURY"] = new[] {"FIRST_CHEST","SECOND_CHEST"},
        ["BYRDONIS_NEST"] = new[] {"EAT","TAKE"}, ["SELF_HELP_BOOK"] = new[] {"READ_THE_BACK","READ_PASSAGE","READ_ENTIRE_BOOK","NO_OPTIONS"},
        ["DENSE_VEGETATION"] = new[] {"TRUDGE_ON","REST"}, ["LUMINOUS_CHOIR"] = new[] {"REACH_INTO_THE_FLESH","OFFER_TRIBUTE"},
        ["SAPPHIRE_SEED"] = new[] {"EAT","PLANT"}, ["SUNKEN_STATUE"] = new[] {"GRAB_SWORD","DIVE_INTO_WATER"},
        ["TABLET_OF_TRUTH"] = new[] {"DECIPHER","SMASH","GIVE_UP"}, ["WELLSPRING"] = new[] {"BOTTLE","BATHE"},
        ["WHISPERING_HOLLOW"] = new[] {"GOLD","HUG"}, ["WOOD_CARVINGS"] = new[] {"BIRD","SNAKE","TORUS"},
        ["ABYSSAL_BATHS"] = new[] {"IMMERSE","ABSTAIN","LINGER","EXIT_BATHS"},
        ["DROWNING_BEACON"] = new[] {"BOTTLE","CLIMB"}, ["ENDLESS_CONVEYOR"] = new[] {"GRAB","OBSERVE_CHEF","LEAVE"},
        ["PUNCH_OFF"] = new[] {"NAB","I_CAN_TAKE_THEM"}, ["SPIRALING_WHIRLPOOL"] = new[] {"OBSERVE","DRINK"},
        ["TRASH_HEAP"] = new[] {"DIVE_IN","GRAB"}, ["WATERLOGGED_SCRIPTORIUM"] = new[] {"BLOODY_INK","TENTACLE_QUILL","PRICKLY_SPONGE"},
        ["CRYSTAL_SPHERE"] = new[] {"UNCOVER_FUTURE","PAYMENT_PLAN"}, ["DOLL_ROOM"] = new[] {"RANDOM","TAKE_SOME_TIME","EXAMINE"},
        ["POTION_COURIER"] = new[] {"GRAB_POTIONS","RANSACK"}, ["RANWID_THE_ELDER"] = new[] {"POTION","GOLD","RELIC"},
        ["RELIC_TRADER"] = new[] {"TOP","MIDDLE","BOTTOM"}, ["ROOM_FULL_OF_CHEESE"] = new[] {"GORGE","SEARCH"},
        ["SLIPPERY_BRIDGE"] = new[] {"OVERCOME","HOLD_ON"}, ["STONE_OF_ALL_TIME"] = new[] {"LIFT","PUSH"},
        ["SYMBIOTE"] = new[] {"APPROACH","KILL_WITH_FIRE"}, ["TEA_MASTER"] = new[] {"BONE_TEA","EMBER_TEA","TEA_OF_DISCOURTESY"},
        ["THIS_OR_THAT"] = new[] {"PLAIN","ORNATE"}, ["WAR_HISTORIAN_REPY"] = new[] {"UNLOCK_CAGE","UNLOCK_CHEST"},
        ["WELCOME_TO_WONGOS"] = new[] {"BARGAIN_BIN","FEATURED_ITEM","MYSTERY_BOX","LEAVE"},
        ["AMALGAMATOR"] = new[] {"COMBINE_STRIKES","COMBINE_DEFENDS"}, ["BUGSLAYER"] = new[] {"EXTERMINATION","SQUASH"},
        ["COLOSSAL_FLOWER"] = new[] {"EXTRACT_CURRENT_PRIZE","REACH_DEEPER"},
        ["FIELD_OF_MAN_SIZED_HOLES"] = new[] {"RESIST","ENTER_YOUR_HOLE"}, ["INFESTED_AUTOMATON"] = new[] {"STUDY","TOUCH_CORE"},
        ["LOST_WISP"] = new[] {"CLAIM","SEARCH"}, ["SPIRIT_GRAFTER"] = new[] {"LET_IT_IN","REJECTION"},
        ["THE_LANTERN_KEY"] = new[] {"RETURN_THE_KEY","KEEP_THE_KEY"}, ["ZEN_WEAVER"] = new[] {"BREATHING_TECHNIQUES","EMOTIONAL_AWARENESS","ARACHNID_ACUPUNCTURE"},
        ["BATTLEWORN_DUMMY"] = new[] {"SETTING_1","SETTING_2","SETTING_3"}, ["GRAVE_OF_THE_FORGOTTEN"] = new[] {"CONFRONT","ACCEPT"},
        ["HUNGRY_FOR_MUSHROOMS"] = new[] {"BIG_MUSHROOM","FRAGRANT_MUSHROOM"}, ["REFLECTIONS"] = new[] {"TOUCH_A_MIRROR","SHATTER"},
        ["ROUND_TEA_PARTY"] = new[] {"ENJOY_TEA","PICK_FIGHT"}, ["TRIAL"] = new[] {"ACCEPT","REJECT"},
        ["TINKER_TIME"] = new[] {"CHOOSE_CARD_TYPE"}
    };
    public static int EventOption(string entry, string leaf, int page)
    {
        if (page > 0 && entry is "PUNCH_OFF" or "DENSE_VEGETATION" or "THE_LANTERN_KEY" or "ROUND_TEA_PARTY") return leaf is "FIGHT" or "CONTINUE_FIGHT" ? 0 : throw new InvalidOperationException("unsupported:event_followup:" + leaf);
        if (entry == "ABYSSAL_BATHS" && page > 0) return leaf == "LINGER" ? 0 : leaf == "EXIT_BATHS" ? 1 : throw new InvalidOperationException("unsupported:baths_option");
        if (entry == "TABLET_OF_TRUTH") return leaf.StartsWith("DECIPHER") ? 0 : leaf is "SMASH" or "GIVE_UP" ? 1 : throw new InvalidOperationException("unsupported:tablet_option");
        if (entry == "SLIPPERY_BRIDGE") return leaf == "OVERCOME" ? 0 : leaf.StartsWith("HOLD_ON") ? 1 : throw new InvalidOperationException("unsupported:bridge_option");
        if (entry == "COLOSSAL_FLOWER") return leaf.StartsWith("EXTRACT") ? 0 : leaf.StartsWith("REACH_DEEPER") || leaf == "POLLINOUS_CORE" ? 1 : throw new InvalidOperationException("unsupported:flower_option");
        if (entry == "TRIAL" && page > 0) return leaf is "GUILTY" or "ACCEPT" ? 0 : leaf is "INNOCENT" or "DOUBLE_DOWN" ? 1 : throw new InvalidOperationException("unsupported:trial_option");
        if (!InitialEventOptions.TryGetValue(entry, out var choices)) throw new InvalidOperationException("unsupported:event_semantics:" + entry);
        int index = Array.IndexOf(choices, leaf);
        if (index < 0) throw new InvalidOperationException("unsupported:event_option_semantics:" + entry + ":" + leaf);
        return index;
    }
    public static string AlternativeKind(string optionId) => optionId.ToUpperInvariant() switch
    {
        "SKIP" => "card_skip", "REROLL" => "card_reroll", "SACRIFICE" => "card_sacrifice",
        _ => throw new InvalidOperationException("unsupported:card_reward_alternative:" + optionId)
    };
    public static int NodeType(string nativeType) => nativeType switch
    {
        "Monster" => 1, "Elite" => 2, "RestSite" => 3, "Shop" => 4, "Treasure" => 5,
        "Boss" => 6, "Unknown" => 7, "Ancient" => 8,
        _ => throw new InvalidOperationException("unsupported:map_node_type:" + nativeType)
    };
    public static int ActRegion(string entry) => entry.ToUpperInvariant() switch
    {
        "OVERGROWTH" => 1, "UNDERDOCKS" => 2, "HIVE" => 3, "GLORY" => 4,
        _ => throw new InvalidOperationException("unsupported:act_region:" + entry)
    };
    public static int CardType(string type) => type switch
    {
        "Attack" => 0, "Curse" => 1, "Power" => 2, "Quest" => 3, "Skill" => 4,
        "Status" => 5, "None" => 6,
        _ => throw new InvalidOperationException("unsupported:card_type:" + type)
    };
    public static int Rider(string rider) => rider switch
    {
        "None" => 0, "Sapping" => 1, "Violence" => 2, "Choking" => 3, "Energized" => 4,
        "Wisdom" => 5, "Chaos" => 6, "Expertise" => 7, "Curious" => 8, "Improvement" => 9,
        _ => throw new InvalidOperationException("unsupported:tinker_rider:" + rider)
    };
    public static string SelectionOwner(string origin, bool combatActive) => origin switch
    {
        "combat" => "battle", "reward" => "strategy", "deck" => "strategy",
        "hand" => "battle", "unknown" when combatActive => "battle",
        _ => throw new InvalidOperationException("unsupported:selection_origin:" + origin)
    };
    public static int SelectionRemaining(int maximum, int selected)
    {
        if (maximum < 1 || selected < 0 || selected > maximum)
            throw new InvalidOperationException("unsupported:selection_cardinality");
        return maximum - selected;
    }
    public static bool CanObserveFreshRun(int visited, int floor, bool atStartingAncient, bool finished, bool chosen)
        => floor <= 1 && (visited == 0 || visited == 1 && atStartingAncient && !finished && !chosen);
    // Frozen rs1 history counts map transitions. Act1's initial Ancient is entered automatically at Reset.
    public static bool CountedMapVisit(int actOrdinal, int nodeType, bool startingPoint)
        => !(actOrdinal == 1 && nodeType == 8 && startingPoint);
    public static bool EventContextReady(int localSlot, int eventCount, bool matchesCurrentEvent)
        => localSlot >= 0 && localSlot < eventCount && matchesCurrentEvent;
    public static int SelectionMinimum(string method, int requested, bool canSkip)
        => method == "FromChooseACardScreen" && canSkip ? 0 : requested;
    public static (int Decisions,int RestMask) RecordChoiceCommit(int decisions,int restMask,string kind,bool countedStrategyDecision)
    {
        string[] rests = { "rest_heal", "rest_smith", "rest_clone", "rest_lift", "rest_dig", "rest_hatch", "rest_cook", "rest_kindle", "rest_leave" };
        int bit = Array.IndexOf(rests,kind);
        if (bit >= 0) restMask |= 1 << bit;
        return (decisions + (countedStrategyDecision ? 1 : 0),restMask);
    }
    public static Dictionary<string, object?> CardRow(int id, int instance, bool upgraded, int enchantment,
        int amount, string tinkerType, string rider, int served)
    {
        if (id <= 0 || instance <= 0 || enchantment < 0 || enchantment >= 22 || served < 0)
            throw new InvalidOperationException("unsupported:public_card_row");
        return new() { ["def_id"] = id, ["instance_id"] = instance, ["upgraded"] = upgraded,
            ["enchantment"] = enchantment, ["enchant_amount"] = amount,
            ["tinker_type"] = CardType(tinkerType), ["tinker_rider"] = Rider(rider), ["served"] = served };
    }
}
