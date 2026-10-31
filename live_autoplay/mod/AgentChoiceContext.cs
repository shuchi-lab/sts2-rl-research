using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public sealed class AgentChoiceSelection
{
    public string Origin { get; init; } = "unknown";
    public string Method { get; init; } = "";
    public string PromptKey { get; init; } = "";
    public int Min { get; init; }
    public int Max { get; init; }
    public bool Optional { get; init; }
    public bool RequireConfirmation { get; init; }
    public int Purpose { get; init; }
    public Dictionary<string, object?> FollowUp { get; init; } = new();
    public volatile bool Finished;
    internal PlayerChoiceContext? ChoiceContext;
    internal Task? SelectorTask;
    internal AgentEventTransition? EventTransition;
}

internal sealed class AgentEventTransition
{
    internal required RunState Run;
    internal required EventRoom Room;
    internal required EventOption Option;
    internal Task? Task;
    internal AgentEventRewardOffer? RewardOffer;
    internal NCrystalSphereScreen? SphereScreen;
    internal CrystalSphereMinigame? Sphere;
    internal Task? SphereCellTask;
}

internal sealed class AgentEventRewardOffer
{
    internal required AgentEventTransition Transition;
    internal required RewardsSet Set;
    internal Task? Task;
    internal NRewardsScreen? Screen;
    // A reward claimed from Set may offer its own set (Lost Coffer inside Neow's Bones);
    // the outer offer resumes ownership only after this exact nested offer completes.
    internal AgentEventRewardOffer? Parent;
}

public static partial class McpMod
{
    internal static AgentChoiceSelection? AgentSelectionContext;
    private static RunState? _agentChoiceRun;
    private static bool _agentChoiceHooks;
    private static bool _agentChoiceHistoryComplete;
    private static string? _agentChoiceHookError;
    private static CombatRoom? _agentChoiceFight;
    private static int _agentChoiceEntryHp, _agentChoiceEntryAct, _agentChoiceEntryFloor;
    private static string _agentChoiceEntryFightId = "";
    private static readonly HashSet<CombatRoom> AgentChoiceClosed = new();
    private static readonly List<Dictionary<string, object?>> AgentChoiceFights = new();
    private static readonly HashSet<MegaCrit.Sts2.Core.Rewards.Reward> AgentFinalSkippedRewards = new();
    internal static int AgentStrategicDecisions, AgentRestTakenMask;
    private static AbstractRoom? _agentChoiceRest;
    private static NRestSiteRoom? _agentChoiceRestVfxUi;
    private static Task? _agentChoiceRestVfx;
    private static AgentEventTransition? _agentChoiceEventTransition;
    internal static string? AgentLastEventOption;
    private static RelicModel? AgentAcquiringRelic;

    internal static void InstallAgentChoiceHooks()
    {
        if (_agentChoiceHooks) return;
        _agentChoiceHooks = true;
        var harmony = new Harmony("com.sts2mcp.live-choice-context");
        InstallAgentChoiceResumeHooks(harmony);
        foreach (var method in typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .Where(m => m.Name.StartsWith("From", StringComparison.Ordinal)))
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ChoiceSelectionHooks), nameof(ChoiceSelectionHooks.AgentChoiceSelectionStarted)),
                postfix: new HarmonyMethod(typeof(ChoiceSelectionHooks), nameof(ChoiceSelectionHooks.AgentChoiceSelectionTask)));
        harmony.Patch(typeof(EventOption).GetMethod(nameof(EventOption.Chosen)),
            prefix: new HarmonyMethod(typeof(McpMod), nameof(AgentChoiceEventChosen)),
            postfix: new HarmonyMethod(typeof(McpMod), nameof(AgentChoiceEventTask)));
        // Every RewardsCmd.Offer* overload (and relic/event code offering a set directly)
        // awaits this instance funnel; ownership is that exact RewardsSet, not its order.
        harmony.Patch(typeof(RewardsSet).GetMethod(nameof(RewardsSet.Offer), Type.EmptyTypes),
            prefix: new HarmonyMethod(typeof(McpMod), nameof(AgentChoiceEventRewardStarted)),
            postfix: new HarmonyMethod(typeof(McpMod), nameof(AgentChoiceEventRewardTask)));
        harmony.Patch(typeof(NRewardsScreen).GetMethod(nameof(NRewardsScreen.ShowScreen)),
            postfix: new HarmonyMethod(typeof(McpMod), nameof(AgentChoiceEventRewardScreen)));
        harmony.Patch(typeof(NCrystalSphereScreen).GetMethod(nameof(NCrystalSphereScreen.ShowScreen)),
            postfix: new HarmonyMethod(typeof(McpMod), nameof(AgentChoiceEventSphereScreen)));
        harmony.Patch(typeof(CrystalSphereMinigame).GetMethod(nameof(CrystalSphereMinigame.CellClicked)),
            postfix: new HarmonyMethod(typeof(McpMod), nameof(AgentChoiceEventSphereCellTask)));
        harmony.Patch(typeof(RelicCmd).GetMethods().Single(m => m.Name == "Obtain" && !m.IsGenericMethod),
            prefix: new HarmonyMethod(typeof(McpMod), nameof(AgentChoiceRelicStarted)),
            postfix: new HarmonyMethod(typeof(McpMod), nameof(AgentChoiceRelicTask)));
        harmony.Patch(AccessTools.Method(typeof(NRestSiteRoom), "AfterSelectingOptionAsync"),
            postfix: new HarmonyMethod(typeof(McpMod), nameof(AgentChoiceRestVfxTask)));
        CombatManager.Instance.CombatSetUp += _ => AgentChoiceCapture(AgentChoiceCombatStarted);
        CombatManager.Instance.CombatWon += room => AgentChoiceCapture(() => AgentChoiceCombatClosed(room, "victory"));
        CombatManager.Instance.CombatEnded += room => { if (_agentChoiceRun != null)
            AgentChoiceCapture(() => AgentChoiceCombatClosed(room, LocalContext.GetMe(_agentChoiceRun)?.Creature.IsDead == true ? "death" : "event_escape")); };
    }

    internal static void ProcessAgentChoiceFrame()
    {
        if (!RunManager.Instance.IsInProgress) return;
        var run = RunManager.Instance.DebugOnlyGetState(); if (run == null) return;
        if (!AgentEventReady(run)) return;
        AgentChoiceEnsureRun(run);
        if (!ReferenceEquals(run.CurrentRoom, _agentChoiceRest))
        {
            _agentChoiceRest = run.CurrentRoom;
            if (!_agentRestoredRestPending) AgentRestTakenMask = 0;
            _agentRestoredRestPending = false;
        }
        var top = NOverlayStack.Instance?.Peek();
        if (AgentSelectionContext?.Finished == true && top is not NCardGridSelectionScreen
            && top is not NChooseACardSelectionScreen && top is not NChooseABundleSelectionScreen)
            AgentSelectionContext = null;
        if (CombatManager.Instance.IsInProgress && _agentChoiceFight == null) AgentChoiceCombatStarted();
    }

    internal static bool AgentEventReady(RunState run)
    {
        if (run.CurrentRoom is not EventRoom room) return true;
        // EventRoom.Enter awaits asset loading before BeginEvent populates this array.
        // LocalMutableEvent's getter indexes the array and is unsafe during that native stage.
        return AgentCurrentLocalEvent(run, room) != null;
    }

    // The local mutable clone of this room's event, or null while the previous room's events remain.
    internal static EventModel? AgentCurrentLocalEvent(RunState run, EventRoom room)
    {
        var events = RunManager.Instance.EventSynchronizer?.Events;
        int slot = LocalContext.NetId.HasValue ? run.Players.ToList().FindIndex(p => p.NetId == LocalContext.NetId.Value) : -1;
        return AgentEventIdentity.Current(events, slot, room.CanonicalEvent);
    }

    private static void AgentChoiceEnsureRun(RunState run)
    {
        if (ReferenceEquals(_agentChoiceRun, run)) return;
        _agentChoiceRun = run;
        // Observe a fresh run or restore its exact saved Choice memory below.
        var visited = run.MapPointHistory.SelectMany(a => a).ToList();
        var ancient = run.CurrentRoom is EventRoom ev ? ev.LocalMutableEvent ?? ev.CanonicalEvent : null;
        bool startingAncient = visited.Count == 1 && visited[0].MapPointType.ToString() == "Ancient"
            && (run.CurrentRoom == null || ancient is AncientEventModel);
        _agentChoiceHistoryComplete = AgentChoiceSemantics.CanObserveFreshRun(visited.Count, run.TotalFloor,
            startingAncient, ancient?.IsFinished == true, ancient?.CurrentOptions.Any(o => o.WasChosen) == true);
        AgentChoiceFights.Clear(); AgentChoiceClosed.Clear(); AgentFinalSkippedRewards.Clear();
        AgentStrategicDecisions = AgentRestTakenMask = 0; AgentSelectionContext = null;
        AgentOpenCardOffer = null; AgentAcquiringRelic = null;
        _agentChoiceEventTransition = null;
        _agentChoiceHookError = null;
        _agentChoiceFight = null; _agentChoiceEntryFightId = ""; AgentLastEventOption = null;
        _agentRestoredRestPending = false;
        AgentChoiceRestoreHistory(run);
    }

    internal static void NotifyAgentManualChoiceChange()
    {
        if (!RunManager.Instance.IsInProgress || _agentChoiceRun == null) return;
        _agentChoiceHistoryComplete = false;
        _agentChoiceHookError = AgentManualChoice.IncompleteReason;
    }

    internal static void NotifyAgentChoiceCommit(AgentCandidate candidate, bool countedStrategyDecision)
    {
        bool counted = countedStrategyDecision && !AgentChoiceAutomaticKinds.Contains(candidate.Kind);
        (AgentStrategicDecisions,AgentRestTakenMask) = AgentChoiceSemantics.RecordChoiceCommit(
            AgentStrategicDecisions,AgentRestTakenMask,candidate.Kind,counted);
    }

    private static void AgentChoiceCombatStarted()
    {
        if (!RunManager.Instance.IsInProgress) return;
        var run = RunManager.Instance.DebugOnlyGetState(); if (run == null) return; AgentChoiceEnsureRun(run);
        if (run.CurrentRoom is not CombatRoom room || ReferenceEquals(room, _agentChoiceFight)) return;
        if (!ReferenceEquals(_battleCombat, room.CombatState) || _battleFightSerial < 1)
            throw new InvalidOperationException("unsupported:fight_terminal_entry_not_bound");
        _agentChoiceFight = room;
        _agentChoiceEntryFightId = _battleFightSerial.ToString();
        if (LocalContext.GetMe(run) == null) throw new InvalidOperationException("no local player");
        // Battle's stage-aligned entry; before CombatBegan it is -1 and AgentCombatEntryBegan fills it in.
        _agentChoiceEntryHp = _battleEntryHp;
        _agentChoiceEntryAct = run.CurrentActIndex + 1; _agentChoiceEntryFloor = run.TotalFloor;
    }

    private static void AgentChoiceCombatClosed(CombatRoom room, string outcome)
    {
        if (_agentChoiceRun == null || !AgentChoiceClosed.Add(room)) return;
        if (!ReferenceEquals(room, _agentChoiceFight)) { _agentChoiceHistoryComplete = false; return; }
        AgentChoiceFights.Add(AgentChoiceFightRecord(room, outcome));
        _agentChoiceFight = null;
    }

    private static Dictionary<string, object?> AgentChoiceFightRecord(CombatRoom room, string outcome)
    {
        string kind = _agentChoiceRun!.BaseRoom is EventRoom ? "event" : room.RoomType switch
        { RoomType.Monster => "normal", RoomType.Elite => "elite", RoomType.Boss => "boss",
          _ => throw new InvalidOperationException("unsupported:fight_room:" + room.RoomType) };
        int hp = AgentChoiceTerminalHp(room, outcome, kind);
        return new() { ["act_ordinal"] = _agentChoiceEntryAct, ["room"] = kind,
            ["outcome"] = outcome, ["floor"] = _agentChoiceEntryFloor, ["entry_hp"] = _agentChoiceEntryHp,
            ["hp_terminal"] = hp };
    }

    private static int AgentChoiceTerminalHp(CombatRoom room, string outcome, string kind)
    {
        // CombatWon runs after AfterCombatVictory (including Burning Blood). Frozen history
        // records retained combat HP before that healing, so only this fight's terminal is valid.
        if (!ReferenceEquals(_battleCombat, room.CombatState)
            || GetAgentBattleTerminal() is not Dictionary<string, object?> terminal)
            throw new InvalidOperationException("unsupported:fight_terminal_snapshot_unavailable");
        bool Is(Dictionary<string, object?> d, string key, object expected)
            => d.TryGetValue(key, out var actual) && Equals(actual, expected);
        if (!Is(terminal, "run_id", _agentRunId) || !Is(terminal, "fight_id", _agentChoiceEntryFightId)
            || terminal.GetValueOrDefault("entry") is not Dictionary<string, object?> entry
            || !Is(entry, "fight_id", _agentChoiceEntryFightId) || !Is(entry, "act_ordinal", _agentChoiceEntryAct)
            || !Is(entry, "room", kind) || !Is(entry, "floor", _agentChoiceEntryFloor)
            || !Is(entry, "entry_hp", _agentChoiceEntryHp)
            || terminal.GetValueOrDefault("raw") is not Dictionary<string, object?> raw
            || !Is(raw, "entry_hp", _agentChoiceEntryHp)
            || !Is(raw, "status", outcome == "death" ? "loss" : "win")
            || raw.GetValueOrDefault("obs") is not Dictionary<string, object?> obs
            || obs.GetValueOrDefault("player") is not Dictionary<string, object?> player
            || player.GetValueOrDefault("hp") is not int hp)
            throw new InvalidOperationException("unsupported:fight_terminal_snapshot_mismatch");
        return hp;
    }

    private static void AgentChoiceEventChosen(EventOption __instance, out AgentEventTransition? __state)
    {
        __state = null;
        var run = RunManager.Instance.DebugOnlyGetState();
        if (run?.CurrentRoom is not EventRoom room) { AgentLastEventOption = __instance.TextKey; return; }
        AgentChoiceEnsureRun(run);
        AgentLastEventOption = __instance.TextKey;
        if (_agentChoiceEventTransition is { } pending
            && ReferenceEquals(pending.Run, run) && ReferenceEquals(pending.Room, room)
            && ReferenceEquals(pending.Option, __instance) && pending.Task?.IsCompleted != true) return;
        // Capture before Chosen starts: OnChosen can synchronously open a selector
        // or push a CombatRoom before Harmony receives the returned Task.
        __state = new AgentEventTransition { Run = run, Room = room, Option = __instance };
        _agentChoiceEventTransition = __state;
    }

    private static void AgentChoiceEventTask(Task __result, AgentEventTransition? __state)
    {
        if (__state != null) __state.Task = __result;
    }

    private static AgentQueueState AgentChoiceEventTransitionState(RunState? run,
        bool selectionSurface, bool combatSurface, bool rewardSurface = false)
    {
        var transition = _agentChoiceEventTransition;
        if (transition == null || !ReferenceEquals(run, transition.Run)
            || !ReferenceEquals(run?.BaseRoom, transition.Room)) return AgentQueueState.Ready;
        var task = transition.Task;
        if (task?.IsFaulted == true) return AgentQueueState.Faulted;
        if (task?.IsCanceled == true) return AgentQueueState.Cancelled;
        if (task?.IsCompletedSuccessfully == true) return AgentQueueState.Ready;
        // Only the selector captured within this original callback may suspend
        // Chosen. A generic visible screen or a finished selector is insufficient.
        if (selectionSurface && AgentSelectionContext is { Finished: false, SelectorTask.IsCompleted: false } selection
            && ReferenceEquals(selection.EventTransition, transition)) return AgentQueueState.Ready;
        // PlayMinigame awaits player input inside EventOption.Chosen. Yield only to
        // the screen created by that event, while its native input task is pending.
        // The last reveal completes that task before rewards/effects finish; those
        // use the captured reward handoff below instead of another divination.
        if (transition.Sphere is { } sphere && transition.SphereScreen is { } sphereScreen
            && ReferenceEquals(NOverlayStack.Instance?.Peek(), sphereScreen)
            && ReferenceEquals(AgentChoiceField(sphereScreen, "_entity"), sphere))
        {
            if (transition.SphereCellTask?.IsFaulted == true) return AgentQueueState.Faulted;
            if (transition.SphereCellTask?.IsCanceled == true) return AgentQueueState.Cancelled;
            if (transition.SphereCellTask?.IsCompleted != false
                && AgentChoiceField(AgentChoiceField(sphere, "_completionSource"), "Task") is Task { IsCompleted: false })
                return AgentQueueState.Ready;
        }
        if (rewardSurface && AgentChoiceLiveRewardOffer(transition.RewardOffer) is { Task.IsCompleted: false, Screen: not null } offer
            && ReferenceEquals(AgentChoiceField(offer.Screen, "_rewardsSet"), offer.Set))
        {
            var top = NOverlayStack.Instance?.Peek();
            if (ReferenceEquals(top, offer.Screen)) return AgentQueueState.Ready;
            if (top is NCardRewardSelectionScreen && AgentOpenCardOffer != null
                && offer.Set.Rewards.Any(r => ReferenceEquals(r, AgentOpenCardOffer))
                && ReferenceEquals(top, AgentChoiceField(AgentOpenCardOffer, "_currentlyShownScreen"))
                && AgentChoiceField(AgentChoiceField(top, "_completionSource"), "Task") is Task { IsCompleted: false })
                return AgentQueueState.Ready;
        }
        // The same native nested room also owns its terminal reward handoff after
        // CombatManager stops; requiring live combat here would deadlock that receipt.
        if (combatSurface && run?.CurrentRoom is CombatRoom combat
            && Equals(combat.ParentEventId, transition.Room.CanonicalEvent.Id)) return AgentQueueState.Ready;
        return AgentQueueState.Waiting;
    }

    private static void AgentChoiceEventRewardStarted(RewardsSet __instance, out AgentEventRewardOffer? __state)
    {
        __state = null;
        var player = __instance.Player;
        var transition = _agentChoiceEventTransition;
        if (transition == null || transition.Task?.IsCompleted == true
            || !ReferenceEquals(player.RunState, transition.Run)
            || !ReferenceEquals(player.RunState.CurrentRoom, transition.Room)) return;
        __state = new AgentEventRewardOffer { Transition = transition, Set = __instance,
            Parent = AgentChoiceLiveRewardOffer(transition.RewardOffer) is { Task.IsCompleted: false } parent ? parent : null };
        transition.RewardOffer = __state;
    }

    private static AgentEventRewardOffer? AgentChoiceLiveRewardOffer(AgentEventRewardOffer? offer)
    {
        while (offer is { Task.IsCompleted: true, Parent: { } parent }) offer = parent;
        return offer;
    }

    private static void AgentChoiceEventSphereScreen(NCrystalSphereScreen __result, CrystalSphereMinigame grid)
    {
        var transition = _agentChoiceEventTransition;
        if (transition == null || transition.Task?.IsCompleted == true
            || AgentChoiceField(grid, "_owner") is not Player player
            || !ReferenceEquals(player.RunState, transition.Run)
            || !ReferenceEquals(player.RunState.CurrentRoom, transition.Room)) return;
        transition.Sphere = grid;
        transition.SphereScreen = __result;
        transition.SphereCellTask = null;
    }

    private static void AgentChoiceEventSphereCellTask(CrystalSphereMinigame __instance, Task __result)
    {
        if (_agentChoiceEventTransition is { } transition && ReferenceEquals(transition.Sphere, __instance))
            transition.SphereCellTask = __result;
    }

    private static void AgentChoiceEventRewardTask(Task __result, AgentEventRewardOffer? __state)
    {
        if (__state != null) __state.Task = __result;
    }

    private static void AgentChoiceEventRewardScreen(NRewardsScreen __result, RewardsSet set)
    {
        var offer = _agentChoiceEventTransition?.RewardOffer;
        // GenerateWithoutOffering sorts and hooks may alter the rewards before the
        // screen opens; bind the screen only to the exact set this Chosen offered.
        if (offer == null || offer.Task?.IsCompleted == true || !ReferenceEquals(set, offer.Set)) return;
        offer.Screen = __result;
    }
    private static void AgentChoiceRestVfxTask(NRestSiteRoom __instance, Task __result)
    { _agentChoiceRestVfxUi = __instance; _agentChoiceRestVfx = __result; }
    private static void AgentChoiceRelicStarted(object[] __args, out RelicModel? __state)
    { __state = __args.OfType<RelicModel>().SingleOrDefault(); AgentAcquiringRelic = __state; }
    private static void AgentChoiceRelicTask(Task __result, RelicModel? __state)
    { _ = __result.ContinueWith(_ => { if (ReferenceEquals(AgentAcquiringRelic, __state)) AgentAcquiringRelic = null; }, TaskScheduler.Default); }

    private static void AgentChoiceSelectionStarted(MethodBase __originalMethod, object[] __args,
        out AgentChoiceSelection __state)
    {
        var prefs = __args.OfType<CardSelectorPrefs>().ToArray();
        bool combat = CombatManager.Instance.IsInProgress;
        string prompt = prefs.Length > 0 ? prefs[0].Prompt.LocEntryKey : "";
        int canSkipParameter = Array.FindIndex(__originalMethod.GetParameters(),p => p.Name == "canSkip" && p.ParameterType == typeof(bool));
        bool canSkip = canSkipParameter >= 0 && __args[canSkipParameter] is true;
        __state = new AgentChoiceSelection { Origin = combat ? "combat" :
                __originalMethod.Name == "FromSimpleGridForRewards" || __originalMethod.Name == "FromChooseACardScreen" ? "reward" : "deck",
            Method = __originalMethod.Name, PromptKey = prompt,
            ChoiceContext = __args.OfType<PlayerChoiceContext>().FirstOrDefault(),
            EventTransition = _agentChoiceEventTransition,
            Min = AgentChoiceSemantics.SelectionMinimum(__originalMethod.Name,prefs.Length > 0 ? prefs[0].MinSelect : 1,canSkip),
            Max = prefs.Length > 0 ? prefs[0].MaxSelect : 1,
            Optional = prefs.Length > 0 && prefs[0].MinSelect == 0 || canSkip,
            RequireConfirmation = prefs.Length > 0 && prefs[0].RequireManualConfirmation,
            Purpose = AgentChoiceCapturedPurpose(__originalMethod.Name, prompt),
            FollowUp = new() };
        var captured = __state;
        AgentChoiceCapture(() => { foreach (var f in AgentChoiceCapturedFollowUp()) captured.FollowUp[f.Key] = f.Value; });
        AgentSelectionContext = __state;
    }

    // Harmony keys __state by the patch's declaring type, including across Harmony IDs.
    // Keep Choice's captured selector state separate from the Battle observer.
    private static class ChoiceSelectionHooks
    {
        internal static void AgentChoiceSelectionStarted(MethodBase __originalMethod, object[] __args,
            out AgentChoiceSelection __state) => McpMod.AgentChoiceSelectionStarted(__originalMethod, __args, out __state);
        internal static void AgentChoiceSelectionTask(object __result, AgentChoiceSelection __state)
            => McpMod.AgentChoiceSelectionTask(__result, __state);
    }

    private static void AgentChoiceCapture(Action capture)
    {
        try { capture(); }
        catch (Exception e) { _agentChoiceHookError = "unsupported:choice_public_context:" + e.Message; }
    }

    internal static Dictionary<string, object?> AgentChoiceExecutionWitness()
    {
        var top = NOverlayStack.Instance?.Peek();
        var selected = top is NCardGridSelectionScreen grid
            ? ((System.Collections.IEnumerable)AgentChoiceField(grid,"_selectedCards")).Cast<CardModel>()
                .Select(c => AgentObjectIds.Card(c.DeckVersion ?? c)).OrderBy(x => x).ToArray()
            : Array.Empty<int>();
        var run = RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
        var player = run == null ? null : LocalContext.GetMe((IEnumerable<Player>)run.Players);
        var potions = AgentExecutionWitness.PotionSlotObjects((IEnumerable<object?>?)player?.PotionSlots ?? Array.Empty<object?>(), AgentObjectIds.Get);
        return AgentChoiceDictionary(("selected_card_instances", selected), ("potion_slot_objects", potions),
            ("final_skipped_reward_handles", AgentFinalSkippedRewards.Select(r => AgentObjectIds.Get(r)).OrderBy(x => x).ToArray()),
            ("selection_finished", AgentSelectionContext?.Finished ?? true));
    }
    private static void AgentChoiceSelectionTask(object __result, AgentChoiceSelection __state)
    {
        if (__result is Task task)
        {
            __state.SelectorTask = task;
            _ = task.ContinueWith(_ => __state.Finished = true, TaskScheduler.Default);
        }
    }

    private static int AgentChoiceCapturedPurpose(string method, string prompt)
    {
        string? relic = AgentAcquiringRelic?.Id.Entry;
        string eventEntry = _agentChoiceRun?.CurrentRoom is EventRoom ev ? ev.CanonicalEvent.Id.Entry : "";
        if (relic == "DOLLYS_MIRROR") return 10;
        if (relic == "PAELS_TOOTH" && prompt == "TO_REMOVE") return 8;
        if (eventEntry == "AMALGAMATOR" && prompt == "TO_REMOVE") return 9;
        if (eventEntry == "WOOD_CARVINGS" && prompt == "TO_TRANSFORM") return 2;
        if (relic == "CLAWS") return 3;
        if (relic == "ASTROLABE" && prompt == "TO_TRANSFORM") return 6;
        return method switch { "FromDeckForEnchantment" => 1, "FromDeckForUpgrade" => 4,
            "FromDeckForTransformation" => 5, "FromDeckForRemoval" or "FromDeckGeneric" when prompt == "TO_REMOVE" => 7,
            _ => -1 };
    }

    private static Dictionary<string, object?> AgentChoiceCapturedFollowUp()
    {
        var f = new Dictionary<string, object?>();
        if (_agentChoiceRun?.CurrentRoom is MerchantRoom) { f["shop_removal"] = true; return f; }
        if (_agentChoiceRun?.CurrentRoom is not EventRoom room || AgentLastEventOption == null) return f;
        if (AgentCurrentLocalEvent(_agentChoiceRun, room) is not { } ev) return f;
        string leaf = AgentLastEventOption.Split(".options.").Last();
        switch (ev.Id.Entry, leaf)
        {
            case ("LUMINOUS_CHOIR", "REACH_INTO_THE_FLESH"): f["follow_up_card"] = AgentIds.Id("cards","SPORE_MIND"); break;
            case ("WELLSPRING", "BATHE"): f["follow_up_card"] = AgentIds.Id("cards","GUILTY"); break;
            case ("FIELD_OF_MAN_SIZED_HOLES", "RESIST"): f["follow_up_card"] = AgentIds.Id("cards","NORMALITY"); break;
            case ("AMALGAMATOR", "COMBINE_STRIKES"): f["follow_up_card"] = AgentIds.Id("cards","ULTIMATE_STRIKE"); break;
            case ("AMALGAMATOR", "COMBINE_DEFENDS"): f["follow_up_card"] = AgentIds.Id("cards","ULTIMATE_DEFEND"); break;
            case ("WHISPERING_HOLLOW", "HUG"): f["follow_up_hp_loss"] = AgentChoiceVar(ev,"HpLoss"); break;
            case ("SPIRIT_GRAFTER", "REJECTION"): f["follow_up_hp_loss"] = AgentChoiceVar(ev,"RejectionHpLoss"); break;
            case ("DENSE_VEGETATION", "TRUDGE_ON"): f["follow_up_hp_loss"] = AgentChoiceVar(ev,"HpLoss"); break;
        }
        return f;
    }
}
