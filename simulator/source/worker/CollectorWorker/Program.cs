using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sts2Emulator.Core;
using Sts2Emulator.Core.Rng;
using Sts2Emulator.Training;

namespace CombatWorker;

/// <summary>
/// Persistent worker speaking JSON lines: one command per stdin line, one or more JSON
/// records per stdout line, each command closed by {"type":"ack"}.
/// </summary>
public static class Program
{
    public const string ProtocolVersion = "cds.worker_protocol/v1";
    private static readonly Dictionary<string, string> Templates = [];
    private static Stream _out = null!;

    private static void Emit(Action<Utf8JsonWriter> write)
    {
        var bytes = Import.Serialize(write);
        _out.Write(bytes);
        _out.WriteByte((byte)'\n');
        // Flushed per record so the records of an episode survive a hard crash of this
        // process (stack overflow, fail-fast), which no handler can intercept.
        _out.Flush();
    }

    public static int Main(string[] args)
    {
        _out = new BufferedStream(Console.OpenStandardOutput(), 1 << 20);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false, 1 << 20);
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (line.Length == 0)
                continue;
            string cmdName = "?";
            string? id = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var cmd = doc.RootElement;
                cmdName = cmd.GetProperty("cmd").GetString()!;
                id = cmd.TryGetProperty("id", out var idv) ? idv.ToString() : null;
                if (cmdName == "quit")
                {
                    Ack(cmdName, id, true);
                    _out.Flush();
                    return 0;
                }
                Dispatch(cmdName, cmd);
                Ack(cmdName, id, true);
            }
            catch (Exception ex)
            {
                Emit(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("type", "command_error");
                    w.WriteString("cmd", cmdName);
                    w.WriteString("error_type", ex.GetType().FullName);
                    w.WriteString("message", ex.Message);
                    w.WriteString("stack", ex.StackTrace);
                    w.WriteEndObject();
                });
                Ack(cmdName, id, false);
            }
            _out.Flush();
        }
        _out.Flush();
        return 0;
    }

    private static void Ack(string cmd, string? id, bool ok) =>
        Emit(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "ack");
            w.WriteString("cmd", cmd);
            w.WriteString("id", id);
            w.WriteBoolean("ok", ok);
            w.WriteEndObject();
        });

    private static void Dispatch(string name, JsonElement cmd)
    {
        switch (name)
        {
            case "hello":
                Hello();
                break;
            case "selftest":
                SelfTest.Run(Emit);
                break;
            case "rng_vector":
                SelfTest.Vector(cmd.GetProperty("seed").GetUInt64(), cmd.GetProperty("count").GetInt32(), Emit);
                break;
            case "import_run":
                Import.Run(cmd, Emit);
                break;
            case "add_template":
            {
                string tid = cmd.GetProperty("template_id").GetString()!;
                string raw = cmd.GetProperty("engine_input").GetRawText();
                TemplateCodec.ReadBattleInput(JsonDocument.Parse(raw).RootElement); // validate now
                Templates[tid] = raw;
                break;
            }
            case "episode":
            {
                string tid = cmd.GetProperty("template_id").GetString()!;
                if (!Templates.TryGetValue(tid, out var raw))
                    throw new KeyNotFoundException($"template {tid} not loaded");
                var battle = TemplateCodec.ReadBattleInput(JsonDocument.Parse(raw).RootElement);
                var req = new EpisodeRequest(
                    cmd.GetProperty("episode_id").GetString()!,
                    tid,
                    battle,
                    cmd.GetProperty("env_seed").GetUInt64(),
                    cmd.GetProperty("policy_seed").GetUInt64(),
                    cmd.GetProperty("max_steps").GetInt32(),
                    cmd.GetProperty("max_turns").GetInt32(),
                    cmd.GetProperty("allow_potions").GetBoolean(),
                    cmd.TryGetProperty("policy", out var pol) ? pol.GetString()! : Episode.PolicyHierarchical,
                    !cmd.TryGetProperty("include_engine_state", out var inc) || inc.GetBoolean(),
                    (cmd.TryGetProperty("rng_mode", out var mode) ? mode.GetString() : "training") switch
                    {
                        "training" => EnvironmentRngMode.Training,
                        "game" => EnvironmentRngMode.Game,
                        var m => throw new ArgumentException($"rng_mode must be 'game' or 'training', not '{m}'"),
                    },
                    cmd.TryGetProperty("debug_throw_at_step", out var dt) ? dt.GetInt32() : -1,
                    cmd.TryGetProperty("debug_exit_at_step", out var de) ? de.GetInt32() : -1,
                    cmd.TryGetProperty("debug_sleep_at_step", out var ds) ? ds.GetInt32() : -1
                );
                if (req.Policy is not (Episode.PolicyHierarchical or Episode.PolicyFlat))
                    throw new ArgumentException($"unknown policy {req.Policy}");
                Episode.Run(req, Emit);
                break;
            }
            case "type_schema":
                Emit(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("type", "type_schema");
                    w.WritePropertyName("types");
                    Type[] seen;
                    lock (EngineDumper.TypesSeen)
                        seen = [.. EngineDumper.TypesSeen];
                    EngineDumper.WriteTypeSchema(w, seen);
                    w.WriteEndObject();
                });
                break;
            case "type_schema_all":
                Emit(w =>
                {
                    w.WriteStartObject();
                    w.WriteString("type", "type_schema");
                    w.WritePropertyName("types");
                    var all = typeof(CombatEngine).Assembly.GetTypes()
                        .Where(t => !t.IsGenericTypeDefinition && !t.IsEnum && !t.IsInterface && !typeof(Exception).IsAssignableFrom(t)
                            && !(t.IsAbstract && t.IsSealed)
                            && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)
                            && !t.Name.Contains('<') && t.Namespace is not null && t.Namespace.StartsWith("Sts2Emulator")
                            && EngineDumper.Fields(t).Length > 0)
                        .ToArray();
                    EngineDumper.WriteTypeSchema(w, all);
                    w.WriteEndObject();
                });
                break;
            case "catalog":
                Catalog();
                break;
            default:
                throw new ArgumentException($"unknown command {name}");
        }
    }

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void Hello()
    {
        var engineAsm = typeof(CombatEngine).Assembly;
        var workerAsm = typeof(Program).Assembly;
        Emit(w =>
        {
            w.WriteStartObject();
            w.WriteString("type", "hello");
            w.WriteString("protocol", ProtocolVersion);
            w.WriteString("engine_input_schema", TemplateCodec.EngineInputSchema);
            w.WriteString("engine_assembly_sha256", Sha256File(engineAsm.Location));
            w.WriteString("worker_assembly_sha256", Sha256File(workerAsm.Location));
            w.WriteString("engine_assembly_mvid", engineAsm.ManifestModule.ModuleVersionId.ToString());
            w.WriteString("rng_training_algorithm", RngAlgorithm.TrainingPcg64XslRr.ToString());
            w.WriteString("rng_training_version", RngAlgorithmContext.TrainingAlgorithmVersion);
            w.WriteString("rng_game_algorithm", RngAlgorithm.GameXoshiro256StarStar.ToString());
            w.WriteString("rng_game_version", Episode.GameAlgorithmVersion);
            w.WriteString("seed_derivation_game", Episode.GameSeedDerivation);
            w.WriteString("terminal_boundary", Episode.TerminalBoundary);
            w.WriteString("seed_derivation", TrainingSeeds.Describe());
            w.WritePropertyName("training_stream_names");
            JsonSerializer.Serialize(w, TrainingCombatFactory.StreamNames);
            w.WriteString("policy_default", Episode.PolicyHierarchical);
            w.WriteString("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            w.WriteNumber("pid", Environment.ProcessId);
            w.WriteEndObject();
        });
    }

    private static void Catalog()
    {
        Emit(w =>
        {
            var d = new EngineDumper { OmitDefaults = false };
            w.WriteStartObject();
            w.WriteString("type", "catalog");
            w.WritePropertyName("cards");
            w.WriteStartArray();
            foreach (var c in Sts2Emulator.GeneratedData.Cards.All)
                d.Write(w, c);
            w.WriteEndArray();
            w.WritePropertyName("relics");
            w.WriteStartArray();
            foreach (var r in Sts2Emulator.GeneratedData.Relics.All)
                d.Write(w, r);
            w.WriteEndArray();
            w.WritePropertyName("potions");
            w.WriteStartArray();
            for (int id = 1; id < 1000; id++)
            {
                PotionDef p;
                try { p = Sts2Emulator.GeneratedData.Potions.Get(id); } catch { break; }
                if (p.Id != id) break;
                d.Write(w, p);
            }
            w.WriteEndArray();
            w.WritePropertyName("enemies");
            w.WriteStartArray();
            for (int id = 1; id < 2000; id++)
            {
                object e;
                try { e = Sts2Emulator.GeneratedData.Enemies.Get(id); } catch { break; }
                d.Write(w, e);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }
}
