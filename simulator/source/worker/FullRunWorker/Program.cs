using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CombatWorker;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Run;

namespace FullRunWorker;

/// <summary>
/// rs1 full-run worker: JSON lines over stdin/stdout, one response per command. Each process
/// hosts any number of persistent runs (slots); <c>step_many</c> steps them sequentially.
/// </summary>
public static class Program
{
    public const string Protocol = "rs1/worker_protocol/v2";
    /// <summary>
    /// The decision flow this bridge exposes. v2 ("auto-reward"): reward gold/relic pickup, incoming potions
    /// with a free usable slot and card-offer opening are automatic; only a full belt, a special card and
    /// each card offer are decisions; a skip is final for its offer. v1 (the 2026-10-05 pilot) offered the
    /// reward screen as a model menu. Checkpoints/replay of one flow are never valid for the other.
    /// </summary>
    public const string DecisionFlow = "rs1/decision_flow/v2-auto-reward";
    public const string ReplySchema = "rs1/step_reply/v1";

    private static readonly Dictionary<string, RunSession> Runs = [];
    private static Compatibility? _compat;
    private static RunLimits? _limits;
    private static Stream _out = null!;
    public static readonly bool DebugEnabled = Environment.GetEnvironmentVariable("RS1_WORKER_DEBUG") == "1";

    public static int Main(string[] args)
    {
        CombatFacade.Verify();
        _out = new BufferedStream(Console.OpenStandardOutput(), 1 << 20);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false, 1 << 20);
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;
            string name = "?";
            byte[] response;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var cmd = doc.RootElement;
                name = cmd.GetProperty("cmd").GetString()!;
                if (name == "quit")
                {
                    Write(Import.Serialize(w => { w.WriteStartObject(); w.WriteBoolean("ok", true); w.WriteString("type", "quit"); w.WriteEndObject(); }));
                    return 0;
                }
                response = Import.Serialize(w => Dispatch(name, cmd, w));
            }
            catch (Exception ex)
            {
                response = Import.Serialize(w => WriteError(w, name, ex));
            }
            Write(response);
        }
        return 0;
    }

    private static void Write(byte[] bytes)
    {
        _out.Write(bytes);
        _out.WriteByte((byte)'\n');
        _out.Flush();
    }

    private static void WriteError(Utf8JsonWriter w, string cmd, Exception ex)
    {
        w.WriteStartObject();
        w.WriteBoolean("ok", false);
        w.WriteString("type", ex is StaleRequest ? "stale_request" : "command_error");
        w.WriteString("cmd", cmd);
        w.WriteString("error_type", ex.GetType().FullName);
        w.WriteString("message", ex.Message);
        w.WriteString("stack", ex.StackTrace);
        w.WriteEndObject();
    }

    private static void Dispatch(string name, JsonElement cmd, Utf8JsonWriter w)
    {
        switch (name)
        {
            case "hello":
                Hello(w);
                break;
            case "configure":
                Configure(cmd, w);
                break;
            case "reset":
            {
                var compat = _compat ?? throw new InvalidOperationException("configure first");
                string runId = cmd.GetProperty("run_id").GetString()!;
                if (Runs.ContainsKey(runId))
                    throw new InvalidOperationException($"run {runId} already exists");
                var rs = new RunSession(runId, cmd.GetProperty("slot").GetInt32(), cmd.GetProperty("seed").GetString()!,
                    cmd.GetProperty("ascension").GetInt32(), compat, _limits!);
                Runs[runId] = rs;
                WriteReply(w, rs, "reset");
                break;
            }
            case "step":
                Step(cmd, w);
                break;
            case "step_many":
                w.WriteStartObject();
                w.WriteBoolean("ok", true);
                w.WriteString("type", "step_many");
                w.WritePropertyName("results");
                w.WriteStartArray();
                foreach (var e in cmd.GetProperty("steps").EnumerateArray())
                {
                    // Each entry is independent: a stale or failed entry never aborts its siblings.
                    try
                    {
                        if (e.TryGetProperty("cmd", out var sub) && sub.GetString() == "abandon")
                        {
                            var ar = Get(e);
                            ar.Abandon(e.GetProperty("kind").GetString()!, e.GetProperty("reason").GetString()!,
                                e.TryGetProperty("detail", out var dd) ? dd.GetString() : null);
                            WriteReply(w, ar, "abandoned");
                        }
                        else
                        {
                            Step(e, w);
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteError(w, "step", ex);
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
                break;
            case "abandon":
            {
                var rs = Get(cmd);
                rs.Abandon(cmd.GetProperty("kind").GetString()!, cmd.GetProperty("reason").GetString()!,
                    cmd.TryGetProperty("detail", out var d) ? d.GetString() : null);
                WriteReply(w, rs, "abandoned");
                break;
            }
            case "close":
            {
                string runId = cmd.GetProperty("run_id").GetString()!;
                bool existed = Runs.Remove(runId);
                w.WriteStartObject();
                w.WriteBoolean("ok", true);
                w.WriteString("type", "closed");
                w.WriteString("run_id", runId);
                w.WriteBoolean("existed", existed);
                w.WriteEndObject();
                break;
            }
            case "event_table":
                w.WriteStartObject();
                w.WriteBoolean("ok", true);
                w.WriteString("type", "event_table");
                w.WritePropertyName("table");
                EventOptions.WriteTable(w);
                w.WriteEndObject();
                break;
            case "audit_facade":
                FacadeAudit.Write(w);
                break;
            case "parity_probe" when DebugEnabled:
                ParityProbe.Run(cmd, w);
                break;
            case "debug_reproject" when DebugEnabled:
            {
                // Test seam: re-run the projection of the CURRENT decision and report RNG witnesses.
                var rs = Get(cmd);
                string before = rs.RngFingerprint();
                byte[] obs = rs.StrategyObservation is null ? [] : PublicProjection.Write(rs, rs.Offered, rs.DeclinedNow);
                string after = rs.RngFingerprint();
                w.WriteStartObject();
                w.WriteBoolean("ok", true);
                w.WriteString("type", "reprojected");
                w.WriteString("rng_before", before);
                w.WriteString("rng_after", after);
                w.WriteBoolean("identical_to_reply", rs.StrategyObservation is not null && obs.AsSpan().SequenceEqual(rs.StrategyObservation));
                w.WriteEndObject();
                break;
            }
            case "debug_resampled_clone" when DebugEnabled:
            {
                // Test seam (never used by the trainer): fork the run with every not-yet-revealed
                // quantity resampled (RunEngine.Clone(resampleSeed)) and return the fork's projection.
                var rs = Get(cmd);
                var fork = rs.ResampledFork(cmd.GetProperty("new_run_id").GetString()!, cmd.GetProperty("resample_seed").GetInt32());
                Runs[fork.RunId] = fork;
                w.WriteStartObject();
                w.WriteBoolean("ok", true);
                w.WriteString("type", "fork");
                w.WriteString("hidden_digest_original", rs.HiddenDigest());
                w.WriteString("hidden_digest_fork", fork.HiddenDigest());
                w.WritePropertyName("reply");
                WriteReply(w, fork, "fork");
                w.WriteEndObject();
                break;
            }
            case "debug_mutate" when DebugEnabled:
                DebugMutate(cmd, w);
                break;
            default:
                throw new ArgumentException($"unknown command {name}");
        }
    }

    private static RunSession Get(JsonElement cmd)
    {
        string runId = cmd.GetProperty("run_id").GetString()!;
        return Runs.TryGetValue(runId, out var rs) ? rs : throw new StaleRequest($"run {runId} not open");
    }

    private static void Step(JsonElement cmd, Utf8JsonWriter w)
    {
        var rs = Get(cmd);
        string decision = cmd.GetProperty("decision_id").GetString()!;
        string owner = cmd.GetProperty("owner").GetString()!;
        if (owner == "battle")
            rs.StepBattle(decision, cmd.GetProperty("battle_decision").GetInt32(), cmd.GetProperty("option").GetInt32());
        else if (owner == "strategy")
            rs.StepStrategy(decision, cmd.GetProperty("candidate_id").GetString()!);
        else
            throw new StaleRequest($"unknown owner {owner}");
        WriteReply(w, rs, "step");
    }

    private static void Configure(JsonElement cmd, Utf8JsonWriter w)
    {
        if (Runs.Count > 0)
            throw new InvalidOperationException("configure before any reset; a live process's contract is frozen");
        _compat = Compatibility.FromJson(cmd.GetProperty("compatibility"));
        var l = cmd.GetProperty("limits");
        _limits = new RunLimits(
            l.GetProperty("max_battle_steps").GetInt32(), l.GetProperty("max_turns").GetInt32(),
            l.GetProperty("max_strategic_decisions").GetInt32(), l.GetProperty("max_auto_actions").GetInt32(),
            l.GetProperty("max_repeated_public_state").GetInt32(), l.GetProperty("purity_check").GetBoolean());
        w.WriteStartObject();
        w.WriteBoolean("ok", true);
        w.WriteString("type", "configured");
        w.WriteString("manifest_digest", _compat.ManifestDigest);
        w.WriteString("decision_flow", DecisionFlow);
        w.WriteNumber("roster_cards", _compat.RosterCards.Count);
        w.WriteNumber("max_battle_steps", _limits.MaxBattleSteps);
        w.WriteNumber("max_turns", _limits.MaxTurns);
        w.WriteNumber("max_strategic_decisions", _limits.MaxStrategicDecisions);
        w.WriteBoolean("purity_check", _limits.PurityCheck);
        w.WriteEndObject();
    }

    public static void WriteReply(Utf8JsonWriter w, RunSession rs, string type)
    {
        w.WriteStartObject();
        w.WriteBoolean("ok", true);
        w.WriteString("type", type);
        w.WriteString("schema", ReplySchema);
        w.WriteString("run_id", rs.RunId);
        w.WriteString("decision_id", rs.DecisionId);
        w.WriteString("status", rs.Status);
        w.WriteString("owner", rs.Owner);
        w.WritePropertyName("counters");
        w.WriteStartObject();
        w.WriteNumber("engine_calls", rs.EngineCalls);
        w.WriteNumber("strategic_decisions", rs.StrategicDecisions);
        w.WriteNumber("auto_actions", rs.AutoActions);
        w.WriteNumber("battle_decisions", rs.BattleDecisions);
        w.WriteNumber("fights", rs.Fights.All.Count);
        w.WriteNumber("mask_lazy_rolls", rs.MaskLazyRolls);
        w.WriteNumber("floor", rs.State.Floor);
        w.WriteNumber("act_ordinal", rs.State.CurrentActIndex + 1);
        w.WriteEndObject();

        w.WritePropertyName("battle_events");
        w.WriteStartArray();
        foreach (var f in rs.Fights.Events)
            f.Write(w);
        w.WriteEndArray();
        rs.Fights.Events.Clear();

        w.WritePropertyName("auto_actions");
        w.WriteStartArray();
        foreach (var (id, kind, reason, declinedHere) in rs.AutoLog)
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteString("kind", kind);
            w.WriteString("reason", reason);
            w.WritePropertyName("declined");
            w.WriteStartArray();
            foreach (var (did, dreason) in declinedHere)
            {
                w.WriteStartObject();
                w.WriteString("id", did);
                w.WriteString("reason", dreason);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        rs.AutoLog.Clear();

        if (rs.Owner == "battle" && rs.BattleReply is not null)
        {
            var f = rs.Fights.Open!;
            w.WritePropertyName("battle");
            w.WriteStartObject();
            w.WriteString("fight_id", f.FightId);
            w.WriteNumber("fight_n", f.Ordinal);
            w.WriteNumber("act_ordinal", f.ActOrdinal);
            w.WriteString("room", f.Room);
            w.WriteNumber("entry_hp", f.Entry.PlayerHp);
            w.WriteNumber("entry_max_hp", f.Entry.PlayerMaxHp);
            w.WriteNumber("floor", f.Floor);
            w.WriteNumber("battle_decision", f.View!.Decision);
            w.WriteNumber("battle_steps", f.BattleSteps);
            w.WritePropertyName("raw");
            w.WriteRawValue(rs.BattleReply, skipInputValidation: true);
            w.WriteEndObject();
        }
        if (rs.Owner == "strategy" && rs.StrategyObservation is not null)
        {
            w.WritePropertyName("strategy");
            w.WriteRawValue(rs.StrategyObservation, skipInputValidation: true);
        }
        if (!rs.Running)
        {
            w.WritePropertyName("outcome");
            w.WriteStartObject();
            w.WriteString("kind", rs.OutcomeKind);
            w.WriteString("reason", rs.OutcomeReason);
            w.WriteString("detail", rs.OutcomeDetail);
            w.WriteBoolean("won", rs.RunWon);
            w.WriteNumber("hp", rs.State.PlayerHp);
            w.WriteNumber("max_hp", rs.State.PlayerMaxHp);
            w.WriteNumber("floor", rs.State.Floor);
            w.WriteNumber("act_ordinal", rs.State.CurrentActIndex + 1);
            w.WriteString("phase", rs.State.Phase.ToString());
            w.WritePropertyName("fights");
            w.WriteStartArray();
            foreach (var f in rs.Fights.All)
                f.Write(w, withTerminalReply: false);
            w.WriteEndArray();
            w.WritePropertyName("declined_counts");
            w.WriteStartObject();
            foreach (var (k, v) in rs.DeclinedCounts.OrderBy(x => x.Key))
                w.WriteNumber(k, v);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }


    /// <summary>Test fixtures only (see RunSession.DebugMutate).</summary>
    private static void DebugMutate(JsonElement cmd, Utf8JsonWriter w)
    {
        var rs = Get(cmd);
        string op = cmd.GetProperty("op").GetString()!;
        rs.DebugMutate(engine =>
        {
            var s = engine.State;
            switch (op)
            {
                case "grant_relic":
                {
                    // As the shop does with a bought relic: a pickup that opens a screen shows it.
                    var fu = RunNonCombatEffects.ApplyRelicPickup(s, RunNonCombatEffects.NamedRelic(cmd.GetProperty("entry").GetString()!));
                    if (fu == RunFollowUp.BundleSelect)
                        s.Phase = RunPhase.BundleSelect;
                    else if (fu == RunFollowUp.TransformSelect)
                        s.Phase = RunPhase.TransformSelect;
                    break;
                }
                case "add_card":
                    RunNonCombatEffects.AddCardToDeck(s, new CardInstance(RunNonCombatEffects.NamedCard(cmd.GetProperty("entry").GetString()!),
                        cmd.TryGetProperty("upgraded", out var u) && u.GetBoolean()));
                    break;
                case "set_deck":
                    s.Deck = [];
                    foreach (var c in cmd.GetProperty("entries").EnumerateArray())
                        RunNonCombatEffects.AddCardToDeck(s, new CardInstance(RunNonCombatEffects.NamedCard(c.GetString()!),
                            cmd.TryGetProperty("upgraded", out var su) && su.GetBoolean()));
                    break;
                case "set":
                    if (cmd.TryGetProperty("hp", out var hp)) s.PlayerHp = hp.GetInt32();
                    if (cmd.TryGetProperty("max_hp", out var mhp)) s.PlayerMaxHp = mhp.GetInt32();
                    if (cmd.TryGetProperty("gold", out var g)) s.Gold = g.GetInt32();
                    if (cmd.TryGetProperty("potion_slot", out var ps)) s.PotionSlots[ps.GetInt32()] = cmd.GetProperty("potion_id").GetInt32();
                    if (cmd.TryGetProperty("max_potion_slots", out var mps)) s.MaxPotionSlots = mps.GetInt32();
                    break;
                case "begin_event":
                    // As EnterEventRoom: an event room is not a monster room.
                    s.LastResolvedRoomType = RunConstants.NodeEvent;
                    RunNonCombatEffects.BeginEvent(s, cmd.GetProperty("event_id").GetInt32());
                    break;
                case "offer_reward":
                    // A room reward screen showing exactly the given rows (relic and/or potion).
                    RunRewardGenerator.ClearRewardScreen(s);
                    s.PendingPotionRewards.Clear();
                    s.PendingBonusRelicRewards.Clear();
                    if (cmd.TryGetProperty("relic_id", out var rr)) s.RelicReward = rr.GetInt32();
                    if (cmd.TryGetProperty("potion_id", out var pr)) s.RewardPotion = pr.GetInt32();
                    s.Phase = RunPhase.RelicReward;
                    break;
                case "offer_rewards":
                {
                    // A room/event reward screen holding exactly the given rows, populated the way the engine's
                    // own RewardsSet does (every row before the screen is offered), for decision-flow fixtures.
                    RunRewardGenerator.ClearRewardScreen(s);
                    s.PendingPotionRewards.Clear();
                    s.PendingBonusRelicRewards.Clear();
                    s.PendingRelicReward = false;
                    int[] Ints(string name) => cmd.TryGetProperty(name, out var a) ? [.. a.EnumerateArray().Select(x => x.GetInt32())] : [];
                    var golds = Ints("golds");
                    if (golds.Length > 0)
                    {
                        s.RewardGold = golds[0];
                        s.PendingGoldRewards.AddRange(golds.Skip(1));
                    }
                    s.PendingPotionRewards.AddRange(Ints("potions"));
                    RunRewardGenerator.OfferNextPotion(s);
                    var relics = Ints("relics");
                    if (relics.Length > 0)
                    {
                        s.RelicReward = relics[0];
                        s.PendingBonusRelicRewards.AddRange(relics.Skip(1));
                    }
                    if (cmd.TryGetProperty("special_cards", out var sc))
                        foreach (var c in sc.EnumerateArray())
                            s.PendingSpecialCardRewards.Add(new CardInstance(RunNonCombatEffects.NamedCard(c.GetString()!), false));
                    if (cmd.TryGetProperty("card_offers", out var offers))
                    {
                        foreach (var o in offers.EnumerateArray())
                        {
                            var cards = o.GetProperty("cards").EnumerateArray()
                                .Select(c => new CardInstance(RunNonCombatEffects.NamedCard(c.GetString()!), false)).ToArray();
                            var bonus = o.TryGetProperty("bonus", out var b) && b.ValueKind == JsonValueKind.String
                                ? new CardInstance(RunNonCombatEffects.NamedCard(b.GetString()!), false) : default;
                            s.PendingCardOffers.Add(new CardRewardOffer(cards)
                            {
                                BonusPowerCard = bonus,
                                CanReroll = o.TryGetProperty("reroll", out var rr2) && rr2.GetBoolean(),
                                Options = new CardRewardOptions(RunConstants.NodeNormal),
                                SubscriptionOrder = ++s.CardRewardSubscriptionSequence,
                                ReceivesRelicUpdates = true,
                            });
                        }
                        RunRewardGenerator.OfferFirstPendingCardOffer(s);
                        s.RewardCardPending = s.PendingCardOffers.Count > 0 || s.DisplayedCardReward is not null;
                    }
                    s.EventAwaitingProceed = cmd.TryGetProperty("context", out var ctx) && ctx.GetString() == "event";
                    s.Phase = RunPhase.RelicReward;
                    break;
                }
                case "enter_treasure":
                    // The engine's own treasure-room entry (chest closed, relic rolled on the shared stream).
                    RunRewardGenerator.EnterTreasureRoom(s);
                    break;
                case "enter_next_act":
                    if (!engine.EnterNextAct())
                        throw new InvalidOperationException("no next act");
                    break;
                default:
                    throw new ArgumentException($"unknown debug op {op}");
            }
        });
        WriteReply(w, rs, "debug");
    }

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void Hello(Utf8JsonWriter w)
    {
        var self = typeof(Program).Assembly;
        w.WriteStartObject();
        w.WriteBoolean("ok", true);
        w.WriteString("type", "hello");
        w.WriteString("protocol", Protocol);
        w.WriteString("reply_schema", ReplySchema);
        w.WriteString("observation_schema", PublicProjection.Schema);
        w.WriteString("decision_flow", DecisionFlow);
        w.WriteString("event_table_schema", EventOptions.Schema);
        w.WriteBoolean("debug_enabled", DebugEnabled);
        w.WritePropertyName("bridge");
        w.WriteStartObject();
        w.WriteString("assembly", self.GetName().Name);
        w.WriteString("assembly_sha256", Sha256File(self.Location));
        w.WriteString("assembly_mvid", self.ManifestModule.ModuleVersionId.ToString());
        w.WriteString("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        w.WriteNumber("pid", Environment.ProcessId);
        w.WritePropertyName("engine_entry_points");
        w.WriteStartArray();
        w.WriteStringValue("Sts2Emulator.Core.Run.RunEngine.Reset(string,ProgressionContext,int,CharacterId)");
        w.WriteStringValue("Sts2Emulator.Core.Run.RunEngine.Step(int,int,out float,out bool,out bool)");
        w.WriteStringValue("Sts2Emulator.Core.Run.RunEngine.UsePotionOutsideCombat(int)");
        w.WriteEndArray();
        w.WritePropertyName("reflected_members");
        w.WriteStartArray();
        foreach (var m in CombatFacade.ReflectedMembers)
            w.WriteStringValue(m);
        w.WriteEndArray();
        w.WritePropertyName("forbidden_members");
        w.WriteStartArray();
        foreach (var m in CombatFacade.ForbiddenMembers)
            w.WriteStringValue(m);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WritePropertyName("dependencies");
        w.WriteStartObject();
        foreach (var asm in new[] { typeof(RunEngine).Assembly, typeof(Episode).Assembly, CombatFacade.RlAssembly })
            w.WriteString(Path.GetFileName(asm.Location), Sha256File(asm.Location));
        string hashing = Path.Combine(Path.GetDirectoryName(self.Location)!, "System.IO.Hashing.dll");
        if (File.Exists(hashing))
            w.WriteString("System.IO.Hashing.dll", Sha256File(hashing));
        w.WriteEndObject();
        // The ORIGINAL worker's own hello, written by its own private method in this process. It is
        // nested evidence of the dependency bytes, never this bridge's identity.
        w.WritePropertyName("original_hello");
        CombatFacade.WriteOriginalHello(w);
        w.WriteEndObject();
    }
}
