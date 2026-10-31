using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private sealed record BattleOverride(int Baseline,bool Upgraded,bool FromFreeSetter);
    private sealed class BattleSelector
    {
        internal string Method = "";
        internal AbstractModel? Source;
        internal PlayerChoiceContext? ChoiceContext;
        internal CardSelectorPrefs? Prefs;
        internal CardModel[]? Cards;
        internal CardPile? Pile;
        internal Func<CardModel,bool>? Filter;
        internal Player? Player;
        internal Task Task = Task.CompletedTask;
        internal bool CanSkip;
    }
    private sealed record BattleMove(CardModel[] Cards, PileType[] Previous, CardPile Destination, CardPilePosition Position,bool Shuffle);
    private static CombatState? _battleCombat;
    private static Player? _battlePlayer;
    private static bool _battleFreshCombat;
    private static int _battleEntryHp, _battleEntryMaxHp, _battleTurn = -1, _battleTurnEntryStart, _battlePreviousTurnStart;
    private static int _battleDecision = 1, _battleSteps, _battleFightSerial;
    private static bool _battleSmoggyPlayed;
    private static readonly BattleDrawKnowledge _battleDraw = new();
    private static readonly Dictionary<CardModel,BattleOverride> _battleOverrides = new();
    private static readonly HashSet<CardModel> _battleFreeSetter = new();
    private static readonly BattleStolenCardLedger _battleStolenCards=new();
    private static readonly HashSet<CardModel> _battleStolenSlyUncertified=new();
    private static readonly List<Task> _battleShuffleTasks = new();
    private static int _battleShuffleDepth;
    private static readonly List<(Task Task,BattleMove Move)> _battleMoves = new();
    private static readonly List<string> _battleHookErrors = new();
    private static BattleSelector? _battleSelection;
    private static Dictionary<string,object?>? _battleLastPayload, _battleTerminal;
    private static void BattleObserve(Action action)
    {
        try { action(); }
        catch(Exception ex) { _battleHookErrors.Add("Public Battle observer failed: " + ex.GetBaseException().Message); }
    }

    internal static bool AgentBattleSelectionActive => _battleSelection is { Task.IsCompleted:false };
    internal static object? GetAgentBattleTerminal() => _battleTerminal;
    private static Dictionary<string,object?> BattleExecuteLogical(string candidateId,int expectedDecision,
        Func<Dictionary<string,object?>> invoke)
    {
        string runId=_agentRunId,fightId=_battleFightSerial.ToString();
        return BattleLogicalCompletion.Execute(candidateId,expectedDecision,ref _battleDecision,ref _battleSteps,
            runId,fightId,()=> {
                var result=invoke();
                if(_agentRunId!=runId || _battleFightSerial.ToString()!=fightId)
                    throw new BattleProjectionUnsupported("Battle ownership changed inside native callback: "+candidateId);
                return result;
            },()=>_battleTerminal);
    }

    internal static void InstallAgentBattleHooks()
    {
        var harmony = new Harmony("com.sts2mcp.live-agent-battle");
        void Patch(MethodInfo? method,string? prefix = null,string? postfix = null)
        {
            if (method == null) { _battleHookErrors.Add("Native Battle hook method unavailable"); return; }
            try { harmony.Patch(method, prefix == null ? null : new HarmonyMethod(typeof(McpMod),prefix),
                postfix == null ? null : new HarmonyMethod(typeof(McpMod),postfix)); }
            catch (Exception ex) { _battleHookErrors.Add(method.DeclaringType?.Name+"."+method.Name+": "+ex.Message); }
        }
        Patch(AccessTools.Method(typeof(CombatManager),"SetUpCombat"),nameof(BattleCombatSetUp));
        CombatManager.Instance.CombatBegan += AgentCombatEntryBegan;
        Patch(AccessTools.Method(typeof(CombatManager),"SetupPlayerTurn"),nameof(BattleTurnSetUp));
        Patch(AccessTools.Method(typeof(CombatHistory),"Clear"),nameof(BattleHistoryClearing));
        Patch(AccessTools.Method(typeof(CombatManager),"EndCombatInternal"),nameof(BattleWon));
        Patch(AccessTools.Method(typeof(CombatManager),"LoseCombat"),nameof(BattleLost));
        Patch(AccessTools.Method(typeof(CombatHistory),"CardPlayFinished"),nameof(BattlePlayFinished));
        Patch(AccessTools.Method(typeof(CardPileCmd),"Shuffle"),nameof(BattleShuffleBeginning),nameof(BattleShuffleStarted));
        Patch(typeof(CardPileCmd).GetMethods().SingleOrDefault(m => m.Name == "Add" && m.GetParameters()[0].ParameterType == typeof(IEnumerable<CardModel>)
            && m.GetParameters()[1].ParameterType == typeof(CardPile)),nameof(BattleMoveBeginning),nameof(BattleMoveStarted));
        Patch(AccessTools.Method(typeof(CardEnergyCost),"SetThisTurnOrUntilPlayed"),nameof(BattleCostOverride));
        Patch(AccessTools.Method(typeof(CardEnergyCost),"Clone"),postfix:nameof(BattleCostCloned));
        Patch(AccessTools.Method(typeof(CardModel),"SetToFreeThisTurn"),nameof(BattleFreeBeginning),nameof(BattleFreeFinished));
        Patch(AccessTools.Method(typeof(SwipePower),"Steal"),nameof(BattleSwipeBeginning));
        Patch(AccessTools.Method(typeof(CardModel),"EndOfTurnCleanup"),nameof(BattleCardCleanupBeginning));
        Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature),"StunInternal"),nameof(BattleStunBeginning),nameof(BattleStunned));
        Patch(AccessTools.Method(typeof(RavenousPower),"AfterDeath"),nameof(BattleRavenousBeginning),nameof(BattleRavenousTracking));
        Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Models.Monsters.TerrorEel),"TerrorMove"),nameof(BattleTerrorMove));
        Patch(AccessTools.PropertySetter(typeof(SkittishPower),"HasGainedBlockThisTurn"),postfix:nameof(BattleSkittishBlockChanged));
        foreach (var method in typeof(CardSelectCmd).GetMethods(BindingFlags.Public|BindingFlags.Static).Where(m => m.Name.StartsWith("From",StringComparison.Ordinal)))
            Patch(method,nameof(BattleSelectorBeginning),nameof(BattleSelectorStarted));
    }

    private static void BattleCombatSetUp(CombatState state)
    {
        _battleCombat = state; _battlePlayer = state.Players.SingleOrDefault(); _battleFreshCombat = true;
        // Entry HP/maxHP is not known yet: AgentCombatEntryBegan captures it at CombatBegan.
        _battleEntryHp = _battleEntryMaxHp = -1;
        _battleTurn = -1; _battleTurnEntryStart = _battlePreviousTurnStart = 0;
        _battleDecision = 1; _battleSteps = 0; _battleFightSerial++;
        _battleDraw.Shuffle(); _battleOverrides.Clear(); _battleMoves.Clear(); _battleShuffleTasks.Clear(); _battleShuffleDepth = 0;
        _battleSelection = null; _battleLastPayload = null; _battleTerminal = null;
        _battleSmoggyPlayed = false;
        _battleSecondaryObserved.Clear();
        _battleStolenCards.Clear(); _battleStolenSlyUncertified.Clear();
        BattleTerrorLedger.Current.Clear();
    }
    // Native StunInternal only acts on a live creature in combat, then SetMoveImmediate(STUNNED);
    // ShriekPower passes TerrorState.StateId.
    private static void BattleStunBeginning(MegaCrit.Sts2.Core.Entities.Creatures.Creature __instance,out object? __state) =>
        __state = __instance.Monster?.NextMove;
    private static void BattleStunned(MegaCrit.Sts2.Core.Entities.Creatures.Creature __instance,string? nextMoveId,object? __state) => BattleObserve(() => {
        if (__instance.Monster == null || __instance.CombatState == null || __instance.IsDead) return;
        bool shriek = __instance.Monster is MegaCrit.Sts2.Core.Models.Monsters.TerrorEel eel
            && nextMoveId != null && nextMoveId == eel.TerrorState?.StateId;
        BattleSyntheticPowers.NativeStunned(__instance,__instance.Powers.Cast<object>().ToArray(),__state,__instance.Monster.NextMove,shriek);
    });
    // RavenousPower.AfterDeath stuns, then awaits Apply<Strength>. Its stun is recorded when the
    // returned Task completes; the caller awaits the wrapper, which awaits the original Task on
    // the game's context and rethrows its fault or cancellation unchanged.
    private static void BattleRavenousBeginning(RavenousPower __instance,out object? __state)
    {
        object? span = null;
        BattleObserve(() => span = BattleSyntheticPowers.RavenousBegan(__instance.Owner));
        __state = span;
    }
    private static void BattleRavenousTracking(ref Task __result,object? __state)
    {
        if (__state != null) __result = BattleRavenousTracked(__result,__state);
    }
    private static async Task BattleRavenousTracked(Task inner,object span)
    {
        try { await inner; }
        catch
        {
            BattleObserve(() => BattleSyntheticPowers.RavenousCompleted(span,false,BattleRavenousPowers(span)));
            throw;
        }
        BattleObserve(() => BattleSyntheticPowers.RavenousCompleted(span,true,BattleRavenousPowers(span)));
    }
    private static object[] BattleRavenousPowers(object span) =>
        BattleSyntheticPowers.RavenousOwner(span) is MegaCrit.Sts2.Core.Entities.Creatures.Creature c ? c.Powers.Cast<object>().ToArray() : Array.Empty<object>();
    // The setter runs synchronously in AfterAttack (true) and AfterSideTurnEnd (false).
    private static void BattleSkittishBlockChanged(SkittishPower __instance,bool value) => BattleObserve(() =>
        BattleSyntheticPowers.SkittishBlockChanged(__instance.Owner,__instance.Owner.Powers.Cast<object>().ToArray(),value));
    private static void BattleTerrorMove(MegaCrit.Sts2.Core.Models.Monsters.TerrorEel __instance) =>
        BattleObserve(() => BattleTerrorLedger.Current.TerrorExecuted(__instance.Creature));
    private static void BattleTurnSetUp(Player player)
    {
        if (_battlePlayer != player) return;
        _battleTurn++; _battlePreviousTurnStart = _battleTurnEntryStart;
        _battleSmoggyPlayed = false;
        _battleStolenSlyUncertified.Clear();
        _battleTurnEntryStart = CombatManager.Instance.History.Entries.Count();
    }
    private static void BattleCardCleanupBeginning(CardModel __instance) => BattleObserve(()=> {
        if(__instance.Owner==_battlePlayer && Convert.ToBoolean(BattleRead(__instance,"_hasSingleTurnSly")))
            _battleStolenSlyUncertified.Add(__instance);
    });
    private static void BattleSwipeBeginning(SwipePower __instance,CardModel card) =>
        BattleObserve(()=>BattleCaptureStolenCard(__instance,card));
    private static void BattleCaptureStolenCard(SwipePower power,CardModel card)
    {
        if(_battleCombat is not {} combat || _battlePlayer is not {} player || !_battleFreshCombat) return;
        if(card.Owner!=player) throw new BattleProjectionUnsupported("Theft source belongs to another player");
        var entries=CombatManager.Instance.History.Entries.ToArray();
        if(_battleTurn<0 || _battleTurnEntryStart<0 || _battleTurnEntryStart>entries.Length)
            throw new BattleProjectionUnsupported("Theft lacks a valid observed player-turn interval");
        var finished=CombatManager.Instance.History.CardPlaysFinished.ToHashSet();
        var row=BattleCardRow(card,combat);
        // Theft is on the enemy side. The frozen value keeps its last player-cycle
        // played flag, even after the native card/history context changes later.
        row[14]=entries.Skip(_battleTurnEntryStart).OfType<MegaCrit.Sts2.Core.Combat.History.Entries.CardPlayFinishedEntry>()
            .Any(e=>finished.Contains(e) && e.Actor==player.Creature && ReferenceEquals(e.CardPlay.Card,card)) ? 1 : 0;
        _battleStolenCards.Capture(power,card,row,_battleStolenSlyUncertified.Contains(card));
    }
    private static void BattleEnsureLedger(CombatState combat,Player player)
    {
        ProcessAgentBattleFrame();
        if (_battleCombat == combat && _battlePlayer == player) return;
        _battleCombat = combat; _battlePlayer = player; _battleFreshCombat = false;
    }
    private static void BattleShuffleBeginning() { _battleShuffleDepth++; _battleDraw.Shuffle(); }
    private static void BattleShuffleStarted(Task __result) { _battleShuffleTasks.Add(__result); }
    private static void BattleMoveBeginning(IEnumerable<CardModel> cards,CardPile newPile,CardPilePosition position,out BattleMove __state)
    {
        var values = cards.ToArray();
        __state = new(values,values.Select(c => c.Pile?.Type ?? PileType.None).ToArray(),newPile,position,_battleShuffleDepth > 0);
    }
    private static void BattleMoveStarted(Task __result,BattleMove __state) => _battleMoves.Add((__result,__state));
    private static void BattleCostOverride(CardEnergyCost __instance,int cost,bool reduceOnly)
    {
        if (reduceOnly) return;
        var card = (CardModel)BattleRead(__instance,"_card")!;
        var mods = ((System.Collections.IEnumerable)BattleRead(__instance,"_localModifiers")!).Cast<LocalCostModifier>();
        int baseline = -mods.Where(m => m.Type == LocalCostType.Relative && m.Expiration.HasFlag(LocalCostModifierExpiration.EndOfTurn)).Sum(m => m.Amount);
        _battleOverrides[card] = new(baseline,card.IsUpgraded,_battleFreeSetter.Contains(card));
    }
    // Native CardModel clones/dupes/mutable copies deep-copy _localModifiers
    // here, so an observed override travels with its own setter provenance.
    private static void BattleCostCloned(CardEnergyCost __instance,CardModel newCard)
    {
        if (BattleRead(__instance,"_card") is CardModel source && _battleOverrides.TryGetValue(source,out var over))
            _battleOverrides[newCard] = over;
    }
    private static void BattleFreeBeginning(CardModel __instance) => _battleFreeSetter.Add(__instance);
    private static void BattleFreeFinished(CardModel __instance) => _battleFreeSetter.Remove(__instance);
    private static void BattleSelectorBeginning(MethodBase __originalMethod,object[] __args,out BattleSelector? __state)
    {
        __state = null;
        if (!CombatManager.Instance.IsInProgress) return;
        var context = __args.OfType<PlayerChoiceContext>().FirstOrDefault();
        var source = __args.OfType<AbstractModel>().LastOrDefault() ?? context?.LastInvolvedModel;
        var player = __args.OfType<Player>().FirstOrDefault() ?? (source as CardModel)?.Owner ?? _battlePlayer;
        CardSelectorPrefs? prefs = __args.OfType<CardSelectorPrefs>().Cast<CardSelectorPrefs?>().FirstOrDefault();
        var cards = __args.OfType<IReadOnlyList<CardModel>>().FirstOrDefault();
        __state = new() { Method = __originalMethod.Name, Source = source, ChoiceContext = context, Player = player, Prefs = prefs,
            Cards = cards?.ToArray(), Pile = __args.OfType<CardPile>().FirstOrDefault(),
            Filter = __args.OfType<Func<CardModel,bool>>().FirstOrDefault(), CanSkip = __args.OfType<bool>().FirstOrDefault() };
        _battleSelection = __state;
    }
    private static void BattleSelectorStarted(Task __result,BattleSelector? __state)
    {
        if (__state != null) __state.Task = __result;
    }
    internal static void ProcessAgentBattleFrame()
    {
        foreach (var item in _battleMoves.Where(x => x.Task.IsCompleted).ToArray())
        {
            _battleMoves.Remove(item);
            if (!item.Task.IsCompletedSuccessfully) { _battleHookErrors.Add("Public card movement failed"); continue; }
            var move = item.Move;
            for (int i = 0; i < move.Cards.Length; i++)
            {
                var card = move.Cards[i]; if (card.Owner != _battlePlayer) continue;
                _battleDraw.Moved(AgentObjectIds.Card(card),move.Previous[i].ToString(),move.Destination.Type.ToString(),
                    move.Position.ToString(),move.Shuffle);
            }
        }
        foreach (var task in _battleShuffleTasks.Where(t => t.IsCompleted).ToArray()) { _battleShuffleTasks.Remove(task); _battleShuffleDepth--; }
        if (_battleSelection?.Task.IsCompleted == true) _battleSelection = null;
    }
    private static bool BattleThisTurn(CombatHistoryEntry entry,CombatState combat)
    {
        if (!entry.HappenedThisTurn(combat)) return false;
        var all = CombatManager.Instance.History.Entries.ToList();
        return all.IndexOf(entry) >= _battleTurnEntryStart;
    }
    private static void BattlePlayFinished(CardPlay cardPlay)
    {
        if (cardPlay.Card.Owner == _battlePlayer && cardPlay.Card.Type == CardType.Skill &&
            BattlePower(cardPlay.Card.Owner.Creature,"SmoggyPower") != null) _battleSmoggyPlayed = true;
    }
    private static void BattleHistoryClearing() => BattleObserve(BattleHistoryClearingCore);
    private static void BattleHistoryClearingCore()
    {
        if (_battleCombat == null || _battlePlayer == null || _battleLastPayload == null) return;
        bool terminal = _battlePlayer.Creature.IsDead || !_battleCombat.Enemies.Any(e => e.IsAlive && e.Monster?.GetType().Name != "Minion");
        if (!terminal) return;
        BattleCaptureTerminal(_battlePlayer.Creature.IsDead ? "loss" : "win");
    }
    private static void BattleWon() => BattleObserve(() => BattleCaptureTerminal("win"));
    private static void BattleLost() => BattleObserve(() => BattleCaptureTerminal("loss"));
    private static void BattleCaptureTerminal(string status)
    {
        if (_battleCombat == null || _battlePlayer == null || _battleLastPayload == null) return;
        string fightId = _battleFightSerial.ToString();
        if (_battleLastPayload.GetValueOrDefault("run_id")?.ToString() != _agentRunId
            || _battleLastPayload.GetValueOrDefault("fight_id")?.ToString() != fightId) return;
        if (_battleTerminal?.GetValueOrDefault("run_id")?.ToString() == _agentRunId
            && _battleTerminal.GetValueOrDefault("fight_id")?.ToString() == fightId) return;
        // Retain the first pre-heal terminal. Later history clearing/room cleanup
        // sees postcombat effects and must not refresh this fight's reward input.
        var payload = (Dictionary<string,object?>)BattleTerminalSnapshot(_battleLastPayload)!;
        var raw = (Dictionary<string,object?>)payload["raw"]!;
        var obs = (Dictionary<string,object?>)raw["obs"]!;
        var p = (Dictionary<string,object?>)obs["player"]!;
        p["hp"] = _battlePlayer.Creature.CurrentHp; p["max_hp"] = _battlePlayer.Creature.MaxHp;
        obs["player"] = p; obs["potions"] = _battlePlayer.PotionSlots.Select(v => v == null ? 0 : AgentIds.Id("potions",v.Id.Entry)).ToArray();
        raw["status"] = status; raw["steps"] = _battleSteps; raw["decision"] = _battleDecision;
        raw["obs"] = obs; payload["raw"] = raw; _battleTerminal = payload;
    }
    private static object? BattleTerminalSnapshot(object? value)
    {
        if (value is System.Collections.IDictionary map)
        {
            var copy = new Dictionary<string,object?>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry entry in map)
            {
                if (entry.Key is not string key) throw new BattleProjectionUnsupported("Terminal observation has a non-string object key");
                copy[key] = BattleTerminalSnapshot(entry.Value);
            }
            return copy;
        }
        if (value is System.Collections.IList rows)
            return rows.Cast<object?>().Select(BattleTerminalSnapshot).ToArray();
        return value;
    }
}
