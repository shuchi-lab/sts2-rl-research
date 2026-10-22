using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Rng;
using Sts2Emulator.Core.Run;
using Sts2Emulator.Training;

namespace FullRunWorker;

public sealed record RunLimits(int MaxBattleSteps, int MaxTurns, int MaxStrategicDecisions, int MaxAutoActions,
    int MaxRepeatedPublicState, bool PurityCheck);

/// <summary>A stale or malformed client request: rejected without touching the run.</summary>
public sealed class StaleRequest(string message) : Exception(message);

/// <summary>
/// One persistent three-act run. Owns its <see cref="RunEngine" />, which owns every resource;
/// the session only calls <c>RunEngine.Step</c> and <c>RunEngine.UsePotionOutsideCombat</c>,
/// observes combat entries through the thread-local <c>CombatEntryCapture.Observer</c> (set on
/// this thread around each engine call and restored afterwards), and reconciles the fight
/// lifecycle after every call.
/// </summary>
public sealed class RunSession
{
    public string RunId { get; }
    public int Slot { get; }
    public int Ascension { get; }
    public RunEngine Engine { get; private init; } = new();
    public RunState State => Engine.State;
    public Compatibility Compat { get; }
    public RunLimits Limits { get; }
    public DeckInstances Deck { get; } = new();
    public FightTracker Fights { get; }

    public string Status { get; private set; } = "running"; // running | complete | unsupported | error | limit | abandoned
    public string? OutcomeKind { get; private set; }
    public string? OutcomeReason { get; private set; }
    public string? OutcomeDetail { get; private set; }
    public bool RunWon { get; private set; }

    public int EngineCalls { get; private set; }
    public int StrategicDecisions { get; private set; }
    public int AutoActions { get; private set; }
    public int BattleDecisions { get; private set; }
    public int MaskLazyRolls { get; private set; }
    public int Seq { get; private set; }
    public string Owner { get; private set; } = "none";
    public string DecisionId => $"{RunId}:d{Seq}";

    public List<Candidate> Offered { get; private set; } = [];
    public List<Candidate> DeclinedNow { get; private set; } = [];
    public byte[]? StrategyObservation { get; private set; }
    public byte[]? BattleReply { get; private set; }
    public List<(string Id, string Kind, string Reason, List<(string Id, string Reason)> Declined)> AutoLog { get; } = [];
    public Dictionary<string, int> DeclinedCounts { get; } = [];
    public List<(int Act, int Col, int Row, int NodeType, int Resolved, int Floor)> Visited { get; } = [];

    /// <summary>
    /// Final-skipped reward rows, one level per open reward screen (index = the engine's
    /// <c>SuspendedRewardScreens</c> depth): "potion|id" and "card|offer key" multiset counts. A nested
    /// pickup screen gets its own level; its parent's survives underneath (and the engine's copies of
    /// it), and every level is dropped once no reward screen is open.
    /// </summary>
    private List<Dictionary<string, int>> _suppressed = [];
    private readonly Dictionary<string, int> _publicStateCounts = [];
    private HashSet<int>? _selectionEligible;
    private (DeckSelection, int, string?, SelectionReturn, int)? _selectionKey;
    private int _selectionCount;
    private bool _architectWin;

    public RunSession(string runId, int slot, string seed, int ascension, Compatibility compat, RunLimits limits)
    {
        RunId = runId;
        Slot = slot;
        Ascension = ascension;
        Compat = compat;
        Limits = limits;
        Fights = new FightTracker(runId);
        Guard(() =>
        {
            Engine.Reset(seed, ProgressionContext.AllUnlocked, ascension, CharacterId.Ironclad);
            Deck.Sync(State.Deck);
            Advance();
        });
    }

    public bool Running => Status == "running";

    private RunSession(RunSession source, string runId, RunEngine engine)
    {
        RunId = runId;
        Slot = source.Slot;
        Ascension = source.Ascension;
        Compat = source.Compat;
        Limits = source.Limits;
        Engine = engine;
        Fights = new FightTracker(runId);
        Fights.All.AddRange(source.Fights.All.Where(f => f.Closed));
        Deck.CopyFrom(source.Deck);
        Visited.AddRange(source.Visited);
        _selectionEligible = source._selectionEligible is null ? null : [.. source._selectionEligible];
        _selectionKey = source._selectionKey;
        _selectionCount = source._selectionCount;
        _suppressed = [.. source._suppressed.Select(l => new Dictionary<string, int>(l))];
        StrategicDecisions = source.StrategicDecisions;
        AutoActions = source.AutoActions;
        BattleDecisions = source.BattleDecisions;
        EngineCalls = source.EngineCalls;
    }

    /// <summary>
    /// Test seam only: a fork whose every not-yet-revealed quantity (future rewards, shop stock,
    /// encounters, shuffles, fog) is resampled by <c>RunEngine.Clone(resampleSeed)</c>, positioned at
    /// the same strategic decision. Its projection must equal this run's.
    /// </summary>
    public RunSession ResampledFork(string runId, int resampleSeed)
    {
        if (Owner != "strategy" || Fights.Open is not null)
            throw new StaleRequest("a resampled fork is only taken at a strategic decision");
        var fork = new RunSession(this, runId, Engine.Clone(resampleSeed));
        fork.Guard(fork.Advance);
        return fork;
    }

    /// <summary>
    /// Test fixtures only (worker started with RS1_WORKER_DEBUG=1, refused by every trainer): mutate the
    /// run at a strategic decision through engine APIs, then recompute the decision. Diagnostic
    /// injected states are never production or performance evidence.
    /// </summary>
    public void DebugMutate(Action<RunEngine> mutate)
    {
        if (!Running || Owner != "strategy" || Fights.Open is not null)
            throw new StaleRequest("debug mutation only at a strategic decision");
        Guard(() =>
        {
            mutate(Engine);
            Deck.Sync(State.Deck);
            TrackSelection();
            Advance();
        });
    }

    /// <summary>Test seam only: a digest of hidden quantities (never part of any observation).</summary>
    public string HiddenDigest()
    {
        var s = State;
        var sb = new StringBuilder();
        foreach (var n in s.MapNodes.Values.OrderBy(n => n.Row).ThenBy(n => n.Col))
            sb.Append(n.EncounterId).Append(',');
        sb.Append('|').Append(string.Join(",", s.NormalEncounterSequence)).Append('|')
            .Append(string.Join(",", s.EliteEncounterSequence)).Append('|').Append(string.Join(",", s.EventSequence))
            .Append('|').Append(s.Rng.Seed).Append('|').Append(string.Join(",", s.RelicBag.Remaining));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // ------------------------------------------------------------------ public steps

    public void StepBattle(string decisionId, int battleDecision, int ordinal)
    {
        RequireDecision(decisionId, "battle");
        var f = Fights.Open ?? throw new ContractViolation("battle_decision_without_fight", DecisionId);
        var view = f.View ?? throw new ContractViolation("battle_decision_without_view", f.FightId);
        if (battleDecision != view.Decision)
            throw new StaleRequest($"{f.FightId}: stale battle decision {battleDecision}, current {view.Decision}");
        var legal = view.Legal;
        if (ordinal < 0 || ordinal >= legal.Count)
            throw new StaleRequest($"{f.FightId}: option {ordinal} outside 0..{legal.Count - 1}");
        var o = legal[ordinal];
        Guard(() =>
        {
            BattleDecisions++;
            f.BattleDecisions++;
            f.BattleSteps++;
            // The confirmed runaway combo (AutoplayLoopGuard: a pure read, no engine call) is a visible limit,
            // never an unanswered step.
            if (AutoplayLoopGuard.Check(f.Combat!, o) is { } runaway)
            {
                Fail("limit", "battle_unbounded_autoplay_loop", $"{DecisionId}: {runaway}");
                return;
            }
            Execute(new EngineOp(false, o.Action, o.Target), partOfComposite: false);
            Advance();
        });
    }

    public void StepStrategy(string decisionId, string candidateId)
    {
        RequireDecision(decisionId, "strategy");
        var c = Offered.FirstOrDefault(x => x.Id == candidateId)
            ?? throw new StaleRequest($"{DecisionId}: candidate {candidateId} is not offered");
        Guard(() =>
        {
            StrategicDecisions++;
            Commit(c);
            Advance();
        });
    }

    /// <summary>The client excluded the run (frozen E could not encode a battle state, etc.).</summary>
    public void Abandon(string kind, string reason, string? detail)
    {
        if (!Running)
            return;
        if (kind is not ("unsupported" or "error" or "limit"))
            throw new StaleRequest($"abandon kind {kind}");
        Fights.Abandon(reason);
        End(kind, reason, detail);
        Status = kind;
    }

    private void RequireDecision(string decisionId, string owner)
    {
        if (!Running)
            throw new StaleRequest($"run {RunId} is {Status}");
        if (decisionId != DecisionId)
            throw new StaleRequest($"stale decision {decisionId}, current {DecisionId}");
        if (owner != Owner)
            throw new StaleRequest($"{DecisionId} is owned by {Owner}, not {owner}");
    }

    // ------------------------------------------------------------------ engine calls

    private void Guard(Action body)
    {
        try
        {
            body();
        }
        catch (ContractViolation e)
        {
            Fail("error", "contract_violation:" + e.Code, e.Message);
        }
        catch (UnsupportedRunState e)
        {
            Fail("unsupported", e.Code, e.Message);
        }
        catch (StaleRequest)
        {
            throw;
        }
        catch (Exception e)
        {
            Fail("error", "engine_exception", $"{e.GetType().FullName}: {e.Message}\n{e.StackTrace}");
        }
    }

    private void Fail(string kind, string reason, string detail)
    {
        Fights.Abandon(reason);
        End(kind, reason, detail);
    }

    private void End(string kind, string reason, string? detail)
    {
        Status = kind == "natural" ? "complete" : kind;
        OutcomeKind = kind;
        OutcomeReason = reason;
        OutcomeDetail = detail;
        Owner = "none";
        Offered = [];
        StrategyObservation = null;
        BattleReply = null;
        Seq++;
    }

    private void Commit(Candidate c)
    {
        var s = State;
        var before = (s.PendingSelectionKind, s.Phase, s.ReturnToRewardScreenAfterCardReward);
        if (c.Precheck is not null)
        {
            // Legality of the WHOLE composite against the current state, before its first mutation.
            var mask = new int[RunConstants.MaxActions];
            string fp = RngFingerprint();
            Engine.WriteActionMask(mask);
            if (RngFingerprint() != fp)
                throw new ContractViolation("precheck_mask_consumed_rng", $"{c.Id} phase {s.Phase}");
            if (c.Precheck(s, mask) is { } why)
                throw new ContractViolation("composite_precheck_failed", $"{c.Id}: {why}");
        }
        // A card offer skipped back onto its reward screen is final there (the engine reinserts it).
        string? skippedOffer = c.Kind == "card_skip" && s.Phase == RunPhase.CardReward && s.ReturnToRewardScreenAfterCardReward
            ? StrategicActions.OfferKey(DisplayedOffer())
            : null;
        bool architect = s.Phase == RunPhase.Event && s.EventId == RunConstants.EventTheArchitect;
        int expectBefore = c.ExpectRelicGain is int want ? s.Relics.Count(r => r.DefId == want) : 0;
        for (int i = 0; i < c.Ops.Count; i++)
        {
            if (!Running)
                throw new ContractViolation("composite_interrupted", $"{c.Id} op {i}");
            Execute(c.Ops[i], partOfComposite: i > 0);
        }
        // A displayed relic (Doll Room's shown dolls) must be exactly what the engine handed over.
        if (c.ExpectRelicGain is int gained && s.Relics.Count(r => r.DefId == gained) != expectBefore + 1)
            throw new ContractViolation("displayed_relic_mismatch",
                $"{c.Id}: displayed relic {gained} was not the one gained (relics now [{string.Join(",", s.Relics.Select(r => r.DefId))}])");
        if (c.ExpectSlotPotion is (int slot, int potion) && s.PotionSlots[slot] != potion)
            throw new ContractViolation("potion_not_taken",
                $"{c.Id}: slot {slot} holds {s.PotionSlots[slot]} (belt [{string.Join(",", s.PotionSlots)}])");
        if (c.Suppress is { } x)
            Suppress(x.Kind, x.Key);
        if (skippedOffer is not null && s.Phase == RunPhase.RelicReward)
            Suppress("card", skippedOffer);
        if (c.Kind == "select_card" && _selectionEligible is not null && c.Public["instance_id"] is { } inst)
            _selectionEligible.Remove((int)inst);
        if (architect && s.Phase == RunPhase.Complete && s.LastPlayerWon)
            _architectWin = true;
        if (before.Phase == RunPhase.Map && c.Kind == "map_node")
            Visited.Add((s.CurrentActIndex, s.CurrentMapCoord.Col, s.CurrentMapCoord.Row,
                s.MapNodes.TryGetValue(s.CurrentMapCoord, out var n) ? n.NodeType : 0, s.LastResolvedRoomType, s.Floor));
    }

    private void Execute(EngineOp op, bool partOfComposite)
    {
        var entries = new List<BattleInput>();
        var previous = CombatEntryCapture.Observer;
        CombatEntryCapture.Observer = b => entries.Add(b);
        int status;
        try
        {
            using var scope = RngAlgorithmContext.Use(RngAlgorithm.GameXoshiro256StarStar);
            status = op.OutsideCombatPotion
                ? Engine.UsePotionOutsideCombat(op.Action)
                : Engine.Step(op.Action, op.Target, out _, out _, out _);
        }
        finally
        {
            CombatEntryCapture.Observer = previous;
        }
        EngineCalls++;
        if (status != 0)
        {
            throw new ContractViolation(partOfComposite ? "composite_partial_failure" : "engine_rejected_offered_action",
                $"{(op.OutsideCombatPotion ? "UsePotionOutsideCombat" : "Step")}({op.Action},{op.Target}) returned {status} in phase {State.Phase}"
                + (State.Phase == RunPhase.Event ? $" event {State.EventId} page {State.EventPage}" : ""));
        }
        Deck.Sync(State.Deck);
        Fights.AfterEngineStep(State, entries, EngineCalls, MakeView);
        TrackSelection();
        SyncRewardLevels();
    }

    private void SyncRewardLevels()
    {
        var s = State;
        if (!StrategicActions.IsRewardPhase(s.Phase))
        {
            _suppressed.Clear();
            return;
        }
        int keep = s.SuspendedRewardScreens.Count + 1;
        if (_suppressed.Count > keep)
            _suppressed.RemoveRange(keep, _suppressed.Count - keep);
    }

    private void Suppress(string kind, string key)
    {
        SyncRewardLevels();
        if (!StrategicActions.IsRewardPhase(State.Phase))
            return; // the screen is already gone: nothing left to suppress
        int depth = State.SuspendedRewardScreens.Count;
        while (_suppressed.Count <= depth)
            _suppressed.Add([]);
        string k = kind + "|" + key;
        _suppressed[depth][k] = _suppressed[depth].GetValueOrDefault(k) + 1;
    }

    /// <summary>The final-skipped rows of the screen open now (a copy: counts the caller may consume).</summary>
    public Dictionary<string, int> SuppressedNow(string kind)
    {
        int depth = State.SuspendedRewardScreens.Count;
        var out_ = new Dictionary<string, int>();
        if (!StrategicActions.IsRewardPhase(State.Phase) || depth >= _suppressed.Count)
            return out_;
        foreach (var (k, v) in _suppressed[depth])
            if (k.StartsWith(kind + "|"))
                out_[k[(kind.Length + 1)..]] = v;
        return out_;
    }

    /// <summary>Card offers still to be shown on the open reward screen (not final-skipped).</summary>
    public int LiveCardOffers()
    {
        var s = State;
        var skipped = SuppressedNow("card");
        int live = 0;
        var offers = new List<CardRewardOffer>();
        if (s.Phase == RunPhase.RelicReward && s.RewardCardPending)
            offers.Add(DisplayedOffer());
        offers.AddRange(s.PendingCardOffers);
        foreach (var o in offers)
        {
            string key = StrategicActions.OfferKey(o);
            if (skipped.GetValueOrDefault(key) > 0)
                skipped[key]--;
            else
                live++;
        }
        return live;
    }

    /// <summary>Incoming potions still to be answered on the open reward screen (not final-skipped).</summary>
    public int LivePotionRows()
    {
        var s = State;
        var skipped = SuppressedNow("potion");
        int live = 0;
        foreach (int p in (s.RewardPotion != 0 ? new[] { s.RewardPotion } : []).Concat(s.PendingPotionRewards))
        {
            string key = p.ToString();
            if (skipped.GetValueOrDefault(key) > 0)
                skipped[key]--;
            else
                live++;
        }
        return live;
    }

    private CombatFacade.FightView MakeView(FightRecord f) =>
        new(Slot, f.FightId, f.Entry, f.Combat!, f.CombatRng ?? throw new ContractViolation("combat_without_rng", f.FightId),
            Limits.MaxBattleSteps, Limits.MaxTurns, Limits.PurityCheck);

    private void TrackSelection()
    {
        var s = State;
        if (s.Phase != RunPhase.TransformSelect || s.PendingSelectionKind == DeckSelection.None)
        {
            _selectionEligible = null;
            _selectionKey = null;
            return;
        }
        var key = (s.PendingSelectionKind, s.PendingSelectionArg, s.PendingSelectionEventEntry, s.PendingSelectionReturn,
            s.PendingSelectionFollowUpCard);
        bool fresh = _selectionEligible is null || _selectionKey != key || s.PendingSelectionCount > _selectionCount;
        _selectionKey = key;
        _selectionCount = s.PendingSelectionCount;
        if (fresh)
        {
            // The selection opened in this call: its candidates are the cards eligible NOW, and a
            // later pick of the same screen may only take another one of these.
            _selectionEligible = [];
            for (int i = 0; i < s.Deck.Count; i++)
                if (RunNonCombatEffects.CanSelectCard(s, i))
                    _selectionEligible.Add(Deck.Ids[i]);
        }
    }

    public HashSet<int> SelectionEligible(RunState s) =>
        _selectionEligible ?? throw new ContractViolation("selection_without_tracked_candidates", $"{s.PendingSelectionKind}");

    public CardRewardOffer DisplayedOffer()
    {
        var s = State;
        if (s.DisplayedCardReward is { } shown)
            return shown;
        // The engine's own reconstruction for a screen populated without a retained record.
        return new CardRewardOffer([
            .. s.RewardCards.Select((id, i) => new CardInstance(id, s.RewardUpgraded[i],
                Enchantment: i == s.RewardEnchantIndex ? s.RewardEnchantment : Enchantment.None,
                EnchantAmount: i == s.RewardEnchantIndex ? 1 : 0)),
        ])
        {
            BonusPowerCard = new CardInstance(s.LastingCandyRewardCard, s.LastingCandyRewardUpgraded),
        };
    }

    // ------------------------------------------------------------------ decisions

    private void Advance()
    {
        while (Running)
        {
            var s = State;
            if (s.Phase == RunPhase.Complete)
            {
                FinishNatural();
                return;
            }
            if (s.Phase == RunPhase.Combat)
            {
                var f = Fights.Open ?? throw new ContractViolation("combat_without_open_fight", DecisionId);
                var view = f.View ?? throw new ContractViolation("combat_without_view", f.FightId);
                view.Refresh(f.BattleSteps);
                switch (view.Status)
                {
                    case "running":
                        Seq++;
                        Owner = "battle";
                        Offered = [];
                        StrategyObservation = null;
                        BattleReply = view.WriteReply("step");
                        return;
                    case "truncated":
                        f.Truncation = view.Reason;
                        Fail("limit", view.Reason == "max_steps" ? "battle_max_steps" : "battle_turn_limit",
                            $"{f.FightId}: steps {f.BattleSteps}, turn {f.Combat!.Turn}");
                        return;
                    default:
                        Fail("error", "battle_" + (view.Reason ?? view.Status), $"{f.FightId}: {view.Error}");
                        return;
                }
            }

            SyncRewardLevels();
            var mask = new int[RunConstants.MaxActions];
            string fp0 = RngFingerprint();
            Engine.WriteActionMask(mask);
            string fp1 = RngFingerprint();
            if (fp0 != fp1)
                MaskLazyRolls++; // the engine's own lazy display roll inside its mask (native flow does the same)
            var all = StrategicActions.Generate(this, mask);
            var offered = all.Where(c => c.Declined is null).ToList();
            var declined = all.Where(c => c.Declined is not null).ToList();
            foreach (var d in declined)
                DeclinedCounts[d.Declined!] = DeclinedCounts.GetValueOrDefault(d.Declined!) + 1;
            CheckDistinct(offered);
            if (offered.Count == 0)
                throw new UnsupportedRunState("no_supported_candidate",
                    $"phase {s.Phase} event {s.EventId} page {s.EventPage}: declined [{string.Join(", ", declined.Select(d => d.Id + "=" + d.Declined))}]"
                    + $"; mask [{string.Join(",", Enumerable.Range(0, mask.Length).Where(i => mask[i] != 0))}]; {EngineEvidence()}");
            var continuation = offered.FirstOrDefault(c => c.Continuation is not null);
            if (offered.Count == 1 || continuation is not null)
            {
                var auto = continuation ?? offered[0];
                AutoActions++;
                if (AutoActions > Limits.MaxAutoActions)
                {
                    Fail("limit", "auto_action_cap", $"{AutoActions} deterministic actions");
                    return;
                }
                AutoLog.Add((auto.Id, auto.Kind, auto.Continuation ?? "sole_displayed_option",
                    declined.Select(d => (d.Id, d.Declined!)).ToList()));
                Commit(auto);
                continue;
            }
            // Reward bookkeeping is never a model decision (decision flow v2): fail closed if it would be.
            if (offered.FirstOrDefault(c => AutomaticOnlyKinds.Contains(c.Kind)) is { } bookkeeping)
                throw new ContractViolation("bookkeeping_offered_as_decision",
                    $"{bookkeeping.Id} among [{string.Join(", ", offered.Select(c => c.Id))}]");
            if (StrategicDecisions >= Limits.MaxStrategicDecisions)
            {
                Fail("limit", "strategy_decision_limit", $"{StrategicDecisions} committed strategic decisions");
                return;
            }
            Seq++;
            Owner = "strategy";
            Offered = offered;
            DeclinedNow = declined;
            BattleReply = null;
            StrategyObservation = PublicProjection.Write(this, offered, declined);
            if (RngFingerprint() != fp1)
                throw new ContractViolation("observation_consumed_rng", $"{DecisionId} phase {s.Phase}");
            string sig = Convert.ToHexString(SHA256.HashData(PublicProjection.Signature(this, offered)));
            int seen = _publicStateCounts.GetValueOrDefault(sig) + 1;
            _publicStateCounts[sig] = seen;
            if (seen > Limits.MaxRepeatedPublicState)
            {
                Fail("limit", "repeated_public_state", $"{seen} identical decisions at phase {s.Phase}");
                return;
            }
            return;
        }
    }

    /// <summary>Kinds the normalized flow only ever executes automatically (they appear in auto_actions only).</summary>
    public static readonly HashSet<string> AutomaticOnlyKinds =
    [
        "reward_gold", "reward_potion", "reward_relic", "reward_open_cards", "reward_leave",
        "treasure_open", "treasure_relic", "treasure_leave",
    ];

    private static void CheckDistinct(List<Candidate> offered)
    {
        var ids = new HashSet<string>();
        foreach (var c in offered)
            if (!ids.Add(c.Id))
                throw new ContractViolation("duplicate_candidate_id", c.Id);
    }

    private void FinishNatural()
    {
        var s = State;
        if (Fights.Open is not null)
            throw new ContractViolation("run_complete_with_open_fight", Fights.Open.FightId);
        if (s.LastPlayerWon)
        {
            if (!_architectWin)
                throw new ContractViolation("ambiguous_run_win", "Complete with LastPlayerWon but not through The Architect");
            RunWon = true;
            End("natural", "run_victory", "The Architect: WinRun");
        }
        else
        {
            RunWon = false;
            var last = Fights.All.LastOrDefault();
            bool combatDeath = last is { Outcome: "death" } && last.ClosedAtEngineStep == EngineCalls;
            End("natural", combatDeath ? "death_in_combat" : "death_outside_combat", $"hp {s.PlayerHp}");
        }
    }

    /// <summary>
    /// Engine-side evidence for an unsupported/contract outcome record (diagnostics only, never an
    /// observation): the screen-state fields that decide which actions the engine offers.
    /// </summary>
    public string EngineEvidence()
    {
        var s = State;
        return $"engine: phase={s.Phase} neow_awaiting_proceed={s.NeowAwaitingProceed} event_awaiting_proceed={s.EventAwaitingProceed} "
            + $"neows_bones_curse_pending={s.PendingNeowsBonesCurse} has_pending_rewards={RunRewardGenerator.HasPendingRewards(s)} "
            + $"reward_gold={s.RewardGold} reward_potion={s.RewardPotion} relic_reward={s.RelicReward} "
            + $"bonus_relics={s.PendingBonusRelicRewards.Count} card_pending={s.RewardCardPending} card_offers={s.PendingCardOffers.Count} "
            + $"selection={s.PendingSelectionKind} offer_grid={s.PendingOfferCards.Length} rest_upgrade={s.PendingRestUpgrade} "
            + $"suspended_screens={s.SuspendedRewardScreens.Count} event={s.EventId}/{s.EventPage}";
    }

    /// <summary>Call counters of every run, player, event and live-combat stream (a purity witness).</summary>
    public string RngFingerprint()
    {
        var s = State;
        var sb = new StringBuilder();
        void G(GameRng? g) => sb.Append(g?.CallCount ?? -1).Append(',');
        void C(CountingRandom? c) => sb.Append(c?.CallCount ?? -1).Append(',');
        var r = s.Rng;
        G(r.UpFront); G(r.Shuffle); G(r.UnknownMapPoint); G(r.CombatCardGeneration); G(r.CombatPotionGeneration);
        G(r.CombatCardSelection); G(r.CombatEnergyCosts); G(r.CombatTargets); G(r.MonsterAi); G(r.Niche); G(r.CombatOrbs);
        G(r.TreasureRoomRelics);
        G(s.PlayerRng.Rewards); G(s.PlayerRng.Shops); G(s.PlayerRng.Transformations);
        G(s.EventRngStream); G(s.CrystalSphereRng);
        sb.Append(s.EventRngName).Append('|');
        if (s.ActiveCombat is { } c)
        {
            C(s.ActiveCombatRng); C(c.ShuffleRng); C(c.NicheHpRng); C(c.TargetRng); C(c.CardSelectionRng); C(c.EnergyCostRng);
            C(c.CardGenerationRng); C(c.PotionGenerationRng); C(c.OrbGenerationRng); C(c.AiRng as CountingRandom);
        }
        sb.Append(s.RelicBag.Remaining.Count()).Append('|').Append(s.SharedRelicBag.Remaining.Count()).Append('|')
            .Append(s.EventValue0).Append('|').Append(s.EventValue1);
        return sb.ToString();
    }
}
