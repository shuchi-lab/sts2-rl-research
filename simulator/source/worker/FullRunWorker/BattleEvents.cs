using System.Text.Json;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Run;
using Sts2Emulator.Training;

namespace FullRunWorker;

/// <summary>A visible violation of the bridge's own contract: the run is excluded, with evidence.</summary>
public sealed class ContractViolation(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
}

/// <summary>An unchanged-engine state the bridge or frozen policy cannot represent honestly.</summary>
public sealed class UnsupportedRunState(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
}

/// <summary>One fight of a run, opened exactly once by <c>CombatEntryCapture.Observer</c>.</summary>
public sealed class FightRecord
{
    public required int Ordinal { get; init; }
    public required string FightId { get; init; }
    public required BattleInput Entry { get; init; }
    public required int ActOrdinal { get; init; }
    public required int ActRegion { get; init; }
    public required string Room { get; init; }
    public required int Floor { get; init; }
    public required int OpenedAtEngineStep { get; init; }
    public CombatState? Combat { get; set; }
    public CountingRandom? CombatRng { get; set; }
    public CombatFacade.FightView? View { get; set; }
    public int BattleSteps { get; set; }
    public int BattleDecisions { get; set; }
    public bool Closed { get; private set; }
    public string? Outcome { get; private set; }
    public bool EnteredTerminal { get; set; }
    public int HpTerminal { get; private set; }
    public int MaxHpTerminal { get; private set; }
    public bool DummyEscaped { get; private set; }
    public int ClosedAtEngineStep { get; private set; }
    public string? Truncation { get; set; }
    /// <summary>The original Session terminal reply (null for a fight that ended on entry: E never acted).</summary>
    public byte[]? TerminalReply { get; private set; }

    public void Close(string outcome, CombatState retained, int engineStep, bool playerWon)
    {
        if (Closed)
            throw new ContractViolation("fight_closed_twice", FightId);
        if (View is not null)
        {
            if (!ReferenceEquals(View.Combat, retained))
                throw new ContractViolation("terminal_reply_for_another_combat", FightId);
            TerminalReply = View.WriteTerminal(playerWon, BattleSteps);
        }
        Closed = true;
        Outcome = outcome;
        HpTerminal = retained.PlayerHp;
        MaxHpTerminal = retained.PlayerMaxHp;
        DummyEscaped = retained.BattlewornDummyRanOutOfTime;
        ClosedAtEngineStep = engineStep;
        View = null;
    }

    public void Write(Utf8JsonWriter w, bool withTerminalReply = true)
    {
        w.WriteStartObject();
        w.WriteString("fight_id", FightId);
        w.WriteNumber("fight_n", Ordinal);
        w.WriteNumber("act_ordinal", ActOrdinal);
        w.WriteNumber("act_region", ActRegion);
        w.WriteString("room", Room);
        w.WriteNumber("floor", Floor);
        w.WriteNumber("encounter_id", Entry.EncounterId);
        w.WriteNumber("entry_hp", Entry.PlayerHp);
        w.WriteNumber("entry_max_hp", Entry.PlayerMaxHp);
        w.WriteBoolean("closed", Closed);
        w.WriteString("outcome", Outcome);
        w.WriteBoolean("entered_terminal", EnteredTerminal);
        if (Closed)
        {
            w.WriteNumber("hp_terminal", HpTerminal);
            w.WriteNumber("max_hp_terminal", MaxHpTerminal);
            w.WriteBoolean("dummy_escaped", DummyEscaped);
        }
        w.WriteNumber("battle_steps", BattleSteps);
        w.WriteNumber("battle_decisions", BattleDecisions);
        w.WriteString("truncation", Truncation);
        if (withTerminalReply && TerminalReply is not null)
        {
            w.WritePropertyName("terminal_reply");
            w.WriteRawValue(TerminalReply, skipInputValidation: true);
        }
        w.WriteEndObject();
    }
}

/// <summary>
/// Explicit fight lifecycle. A fight opens at its Observer call and closes when the run's phase
/// leaves Combat or the live combat's identity changes -- never when <c>ActiveCombat</c> becomes
/// null, because the build08 <c>SyncAfterCombat</c> leaves it set. Its terminal HP and the
/// Battleworn Dummy escape flag are read from the retained combat object; its result is the
/// <c>LastPlayerWon</c> the very same <c>RunEngine.Step</c> left, reconciled before any later
/// step. Anything ambiguous is a visible contract violation, never an inferred win.
/// </summary>
public sealed class FightTracker(string runId)
{
    public List<FightRecord> All { get; } = [];
    public FightRecord? Open { get; private set; }
    private CombatState? _lastClosedCombat;

    /// <summary>Fights closed since the last drain, in order (the reply's battle events).</summary>
    public List<FightRecord> Events { get; } = [];

    public static string RoomOf(BattleInput b)
    {
        // Elite flag first; then the resolved room: a '?' that rolled a monster resolved to
        // NodeNormal before the fight, an event-started fight carries NodeEvent, a boss node NodeBoss.
        if (b.IsEliteRoom)
            return "elite";
        return b.LastResolvedRoomType switch
        {
            RunConstants.NodeBoss => "boss",
            RunConstants.NodeEvent => "event",
            RunConstants.NodeNormal => "normal",
            var other => throw new UnsupportedRunState("unclassified_fight_room",
                $"combat entry with LastResolvedRoomType {other} (node {b.CurrentNodeType})"),
        };
    }

    /// <summary>Reconcile the lifecycle immediately after one <c>RunEngine.Step</c>.</summary>
    public void AfterEngineStep(RunState s, IReadOnlyList<BattleInput> entries, int engineStep,
        Func<FightRecord, CombatFacade.FightView> makeView)
    {
        if (entries.Count > 1)
            throw new ContractViolation("multiple_combat_entries_in_one_step",
                $"{entries.Count} Observer calls in engine step {engineStep}");

        var live = Open;
        if (live is not null && live.Combat is not null)
        {
            bool stillLive = s.Phase == RunPhase.Combat && ReferenceEquals(s.ActiveCombat, live.Combat);
            if (!stillLive)
            {
                if (entries.Count == 1)
                    throw new ContractViolation("fight_closed_and_new_fight_opened_in_one_step",
                        $"{live.FightId} ended in the step that entered another combat; its outcome is ambiguous");
                CloseFight(live, live.Combat, s, engineStep);
            }
        }

        if (entries.Count == 1)
        {
            if (Open is not null)
                throw new ContractViolation("combat_entry_while_fight_live", $"{Open.FightId} still open");
            var b = entries[0];
            var f = new FightRecord
            {
                Ordinal = All.Count,
                FightId = $"{runId}:f{All.Count:D3}",
                Entry = b,
                ActOrdinal = b.ActIndex + 1,
                ActRegion = s.Act,
                Room = RoomOf(b),
                Floor = b.TotalFloor,
                OpenedAtEngineStep = engineStep,
            };
            if (f.ActOrdinal is < 1 or > 3)
                throw new UnsupportedRunState("act_ordinal_out_of_range", $"act ordinal {f.ActOrdinal}");
            All.Add(f);
            Open = f;
            var combat = s.ActiveCombat
                ?? throw new ContractViolation("combat_entry_without_combat", f.FightId);
            if (ReferenceEquals(combat, _lastClosedCombat))
                throw new ContractViolation("combat_entry_reused_closed_combat", f.FightId);
            f.Combat = combat;
            f.CombatRng = s.ActiveCombatRng;
            if (s.Phase == RunPhase.Combat)
            {
                f.View = makeView(f);
            }
            else
            {
                // Won or lost during StartCombatWithDeck: the fight finished inside this same step.
                f.EnteredTerminal = true;
                CloseFight(f, combat, s, engineStep);
            }
        }

        if (s.Phase == RunPhase.Combat && (Open is null || !ReferenceEquals(s.ActiveCombat, Open.Combat)))
            throw new ContractViolation("combat_phase_without_open_fight", $"engine step {engineStep}");
    }

    private void CloseFight(FightRecord f, CombatState retained, RunState s, int engineStep)
    {
        string outcome;
        bool won = s.LastPlayerWon;  // the engine's verdict for this fight, bound in this same Step
        if (won)
        {
            // Battleworn Dummy timing out is the combat's broad win flag, but the event shows DEFEAT.
            outcome = retained.BattlewornDummyRanOutOfTime ? "event_escape" : "victory";
        }
        else
        {
            if (s.Phase != RunPhase.Complete)
                throw new ContractViolation("fight_lost_without_run_end",
                    $"{f.FightId}: LastPlayerWon false but phase {s.Phase}");
            outcome = "death";
        }
        f.Close(outcome, retained, engineStep, won);
        _lastClosedCombat = retained;
        Open = null;
        Events.Add(f);
    }

    /// <summary>Close an open fight without a natural result (engineering limit or failure).</summary>
    public void Abandon(string reason)
    {
        if (Open is not null)
            Open.Truncation = reason;
    }
}
