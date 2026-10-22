using System.Text;
using System.Text.Json;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Rng;
using Sts2Emulator.Core.Run;
using Sts2Emulator.Training;

namespace CombatWorker;

/// <summary>
/// Replays one certified run (its report's native action stream, game generator) and
/// captures every combat entry as a battle input.
/// </summary>
public static class Import
{
    public static void Run(JsonElement cmd, Action<Action<Utf8JsonWriter>> emit)
    {
        string runId = cmd.GetProperty("run_id").GetString()!;
        string seed = cmd.GetProperty("seed").GetString()!;
        int ascension = cmd.GetProperty("ascension").GetInt32();
        string profilePath = cmd.GetProperty("profile_path").GetString()!;
        var actions = cmd.GetProperty("actions").EnumerateArray().ToList();
        var progression = ProgressionContext.FromProgressSaveJson(File.ReadAllText(profilePath, Encoding.UTF8));

        // The accepted replays run the game's generator; make that explicit.
        using var scope = RngAlgorithmContext.Use(RngAlgorithm.GameXoshiro256StarStar);
        var run = new RunEngine();
        run.Reset(seed, progression, ascension);
        var obs = new int[RunConstants.RunObsSize];
        run.WriteObservation(obs);

        BattleInput? captured = null;
        CombatEntryCapture.Observer = b => captured = b;
        var errors = new List<string>();
        int templates = 0;
        int applied = 0;

        // The open fight: captured input, where it began, and the game-RNG start state.
        BattleInput? open = null;
        int openIndex = -1;
        string? openRef = null;
        byte[]? openStart = null;
        CombatState? openCombat = null;
        int openOrdinal = -1;
        int ordinal = 0;

        void Close(int closingIndex, string closure)
        {
            if (open is null)
                return;
            var input = open;
            var flags = new List<string>();
            // Round-trip check: the stored form must rebuild an identical input.
            byte[] first = Serialize(w => TemplateCodec.WriteBattleInput(w, input, flags));
            string? roundTripError = null;
            string? buildError = null;
            try
            {
                using var doc = JsonDocument.Parse(first);
                var back = TemplateCodec.ReadBattleInput(doc.RootElement);
                byte[] second = Serialize(w => TemplateCodec.WriteBattleInput(w, back, []));
                if (!first.AsSpan().SequenceEqual(second))
                    roundTripError = "engine_input JSON does not round-trip byte-identically";
                try
                {
                    TrainingCombatFactory.Build(back, 0);
                }
                catch (Exception ex)
                {
                    buildError = ex.GetType().Name + ": " + ex.Message;
                }
            }
            catch (Exception ex)
            {
                roundTripError = ex.GetType().Name + ": " + ex.Message;
            }
            bool inCombat = run.State.Phase == RunPhase.Combat && ReferenceEquals(run.State.ActiveCombat, openCombat);
            var startBytes = openStart;
            int ord = openOrdinal;
            int startIndex = openIndex;
            string? startRef = openRef;
            templates++;
            emit(w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "template");
                w.WriteString("run_id", runId);
                w.WriteNumber("combat_ordinal", ord);
                w.WriteNumber("entry_action_index", startIndex);
                w.WriteString("entry_ref", startRef);
                w.WriteNumber("closing_action_index", closingIndex);
                w.WriteString("closure", closure);
                w.WritePropertyName("source_outcome");
                w.WriteStartObject();
                w.WriteBoolean("combat_finished_in_source", !inCombat && (closure is "left_combat" or "combat_ended_on_entry"));
                w.WriteBoolean("player_won", run.State.LastPlayerWon);
                w.WriteNumber("run_player_hp_after", run.State.PlayerHp);
                w.WriteString("run_phase_after", run.State.Phase.ToString());
                w.WriteNumber("source_actions_in_combat", closingIndex - startIndex);
                w.WriteEndObject();
                w.WritePropertyName("engine_input");
                w.WriteRawValue(first);
                w.WritePropertyName("flags");
                JsonSerializer.Serialize(w, flags);
                w.WriteString("round_trip_error", roundTripError);
                w.WriteString("training_build_error", buildError);
                w.WritePropertyName("replay_combat_start_engine_state");
                if (startBytes is null)
                    w.WriteNullValue();
                else
                    w.WriteRawValue(startBytes);
                w.WriteEndObject();
            });
            open = null;
            openCombat = null;
            openStart = null;
        }

        try
        {
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                string kind = a.GetProperty("kind").GetString()!;
                string? aref = a.TryGetProperty("ref", out var r) ? r.ToString() : null;
                captured = null;
                int status;
                if (kind == "step")
                {
                    status = run.Step(a.GetProperty("action").GetInt32(), a.GetProperty("target").GetInt32(), out _, out _, out _);
                }
                else if (kind == "confirm_rest_upgrades")
                {
                    var idx = a.GetProperty("deck_indices").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    status = run.ConfirmRestUpgradeSelection(idx);
                }
                else
                {
                    errors.Add($"action {i}: unsupported kind {kind}");
                    break;
                }
                run.WriteObservation(obs);
                if (status != 0)
                {
                    errors.Add($"action {i} ({aref}): engine status {status}");
                    break;
                }
                applied++;
                // A fight that ended (or was replaced) closes before a new one opens.
                if (open is not null && !(run.State.Phase == RunPhase.Combat && ReferenceEquals(run.State.ActiveCombat, openCombat)))
                    Close(i, "left_combat");
                if (captured is not null)
                {
                    if (open is not null)
                        Close(i, "superseded_by_new_combat_entry");
                    open = captured;
                    openIndex = i;
                    openRef = aref;
                    openOrdinal = ordinal++;
                    openCombat = run.State.Phase == RunPhase.Combat ? run.State.ActiveCombat : null;
                    openStart = openCombat is null
                        ? null
                        : Serialize(w =>
                        {
                            var d = new EngineDumper { ElideReference = openCombat.Progression, ElideNote = "progression: see engine_input.Progression" };
                            d.Write(w, openCombat, "$");
                        });
                    if (openCombat is null)
                        Close(i, "combat_ended_on_entry");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            CombatEntryCapture.Observer = null;
        }
        Close(actions.Count, "source_actions_ended_in_combat");

        emit(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "import_done");
            w.WriteString("run_id", runId);
            w.WriteNumber("actions_total", actions.Count);
            w.WriteNumber("actions_applied", applied);
            w.WriteNumber("templates", templates);
            w.WriteString("final_phase", run.State.Phase.ToString());
            w.WriteNumber("final_floor", run.State.Floor);
            w.WriteNumber("final_hp", run.State.PlayerHp);
            w.WritePropertyName("errors");
            JsonSerializer.Serialize(w, errors);
            w.WriteEndObject();
        });
    }

    public static byte[] Serialize(Action<Utf8JsonWriter> write)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer))
            write(w);
        return buffer.WrittenSpan.ToArray();
    }
}
