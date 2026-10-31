using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace STS2_MCP;

public static partial class McpMod
{
    private static readonly AgentGuard _agentGuard = new();
    private static AgentDecision _agentDecision = new();
    private static RunState? _agentRun;
    private static string _agentRunId = "menu", _agentDecisionId = "menu:0", _agentSignature = "";
    private static long _agentRevision;
    private static long _agentAuthorizationRevision;
    private static double _agentLastFrame, _agentLastContact;
    private static string _agentMode = "advice", _agentMessage = "Start the frozen Python service to display ratings.";
    private static double _agentDelay = 1.0;
    private static bool _agentStepRequested;
    private static readonly AgentTurboSettings _agentTurbo = new();
    private static object? _agentEngineApplied;
    private static DisplayServer.VSyncMode? _agentVsyncOriginal;
    private static readonly AgentEngineGuard _agentEngineGuard = new();
    private static readonly GodotAgentEngine _agentEngine = new();

    private sealed class GodotAgentEngine : IAgentEngine
    {
        public int MaxFps { get => Engine.MaxFps; set => Engine.MaxFps = value; }
        public double TimeScale { get => Engine.TimeScale; set => Engine.TimeScale = value; }
        public bool Focused => DisplayServer.WindowIsFocused();
    }
    private static Dictionary<string, (double Q, double Preference)> _agentScores = new();
    private static HashSet<string> _agentExcluded = new();
    private static string? _agentChosen;
    private static string _agentModel = "Choice65k · Wide+deep Battle177.5k";
    private static string? _agentError;
    private static string? _agentManualStrategySignature;
    private static Dictionary<string,object?>? _agentRunTerminal;
    private static string? _agentInitializationError;

    private static void InitializeAgent()
    {
        AgentIds.Load();
        try
        {
            InstallAgentBattleHooks();
            InstallAgentChoiceHooks();
            new Harmony("com.sts2mcp.live-agent-input").Patch(AccessTools.Method(typeof(NGame), "_Input"),
                prefix: new HarmonyMethod(typeof(McpMod), nameof(AgentManualInput)));
            new Harmony("com.sts2mcp.live-agent-run-end").Patch(AccessTools.Method(typeof(RunManager), "OnEnded"),
                postfix: new HarmonyMethod(typeof(McpMod), nameof(AgentRunEnded)));
        }
        catch (Exception error)
        {
            _agentInitializationError = "Agent hooks failed to initialize: " + error.GetBaseException().Message;
            GD.PrintErr("[STS2 Agent] " + _agentInitializationError);
        }
    }

    private static void AgentRunEnded(bool isVictory)
    {
        if (_agentRunTerminal != null) return;
        _agentRunTerminal = new() { ["run_id"] = _agentRunId, ["status"] = isVictory ? "victory" : "defeat",
            ["abandoned"] = _abandonedRun, ["utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
        SetAgentMode("paused");
        _agentMessage = "Run ended: " + (isVictory ? "victory" : "defeat") + ". Autoplay stopped.";
        _recording?.Append("agent_run_ended", _agentRunTerminal);
    }

    private static void AgentManualInput(InputEvent inputEvent)
    {
        // Handle the event itself: a short/synthetic key press can end between game frames,
        // and some platforms supply only the logical Keycode rather than PhysicalKeycode.
        if (inputEvent is InputEventKey hotkey && (hotkey.PhysicalKeycode == Key.F6 || hotkey.Keycode == Key.F6))
        {
            if (hotkey.Pressed && !hotkey.Echo) SetAgentMode(_agentMode == "paused" ? "advice" : "paused");
            return;
        }
        bool gameplay = inputEvent switch
        {
            InputEventMouseButton mouse => mouse.Pressed && mouse.ButtonIndex is MouseButton.Left or MouseButton.Right,
            InputEventKey key => key.Pressed && !key.Echo && key.PhysicalKeycode != Key.F8 && key.Keycode != Key.F8,
            InputEventJoypadButton pad => pad.Pressed,
            _ => false
        };
        bool overlay = inputEvent is InputEventMouseButton click && _agentPanel != null
            && GodotObject.IsInstanceValid(_agentPanel) && _agentPanel.GetGlobalRect().HasPoint(click.Position);
        // Escape on an open shop only closes its inventory panel. It neither
        // buys anything nor leaves the room, so it cannot invalidate Choice
        // history. It still pauses Auto below, like other manual game input.
        bool shopNavigation = inputEvent is InputEventKey navigation
            && (navigation.PhysicalKeycode == Key.Escape || navigation.Keycode == Key.Escape)
            && _agentDecision.Phase is "Shop" or "FakeMerchant";
        bool pendingChoice = _agentExecution?.ChoiceDecision == true;
        _agentManualStrategySignature = AgentManualChoice.Capture(_agentManualStrategySignature, gameplay && !shopNavigation, overlay,
            _agentDecision.Owner, _agentDecision.Payload.GetValueOrDefault("schema")?.ToString(), pendingChoice,
            _agentSignature, pendingChoice ? _agentExecution!.BeforeSignature : null);
        if (!gameplay || overlay) return;
        if (_agentMode is not ("auto" or "step")) return;
        SetAgentMode("paused");
        _agentMessage = "Manual game input detected. Autoplay paused.";
    }

    private static void ProcessAgentFrame()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        _agentEngineGuard.Tick(_agentEngine);
        ProcessAgentBattleFrame();
        ProcessAgentChoiceFrame();
        ProcessAgentExecution(now);
        if (now - _agentLastFrame >= _agentTurbo.RefreshInterval)
        {
            _agentLastFrame = now;
            RefreshAgentDecision();
        }
        if (_agentMode is "auto" or "step" && _agentLastContact > 0 && now - _agentLastContact > 5)
        {
            SetAgentMode("paused");
            _agentMessage = "Inference service disconnected. Autoplay paused.";
        }
        UpdateAgentOverlay();
    }

    private static void RefreshAgentDecision()
    {
        try
        {
            var run = RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
            if (!ReferenceEquals(run, _agentRun))
            {
                _agentRun = run;
                _agentRunId = run == null ? "menu" : "live-" + Guid.NewGuid().ToString("N");
                if (run != null) _agentRunTerminal = null;
                _agentSignature = "";
                _agentManualStrategySignature = null;
                AgentObjectIds.Reset();
                _agentScores.Clear(); _agentExcluded.Clear();
            }
            bool initialized = run != null && AgentStageChecks.RunReady(run.Players.Count, run.Acts.Count,
                run.CurrentActIndex, run.CurrentRoom != null || IsMapScreenOpenOrVisible(), run.Map is not NullActMap);
            var player = run == null || !initialized ? null : LocalContext.GetMe((IEnumerable<Player>)run.Players);
            AgentDecision decision;
            if (_agentInitializationError != null)
                decision = new() { Phase = "Unsupported", Unsupported = new() { _agentInitializationError } };
            else if (run == null)
                decision = new() { Phase = "Menu" };
            else if (!initialized || player == null || !AgentEventReady(run))
                decision = new() { Phase = "Waiting" };
            else if (_agentRunTerminal?.GetValueOrDefault("run_id")?.ToString() == _agentRunId)
                decision = new() { Phase = "GameOver" };
            else if (!AgentDeploymentRun.Allowed(player.Character.Id.Entry, run.AscensionLevel, run.Players.Count, run.Modifiers.Count, IsMultiplayerRun()))
                decision = new() { Phase = "Unsupported", Unsupported = new() { AgentDeploymentRun.Unsupported } };
            else if (!AgentIds.Ready)
                decision = new() { Phase = "Unsupported", Unsupported = new() { "Load agent_contract.json: " + AgentIds.Error } };
            else
                decision = CombatManager.Instance.IsInProgress ? BuildAgentBattle(run, player) : BuildAgentStrategy(run, player);

            string signature = AgentSignature(decision);
            if (AgentManualChoice.Changed(_agentManualStrategySignature, signature, _agentExecution != null,
                decision.Ready && decision.Unsupported.Count == 0, decision.Owner,
                decision.Payload.GetValueOrDefault("schema")?.ToString()))
            {
                _agentManualStrategySignature = null;
                NotifyAgentManualChoiceChange();
            }
            // A manual map choice may lead directly into Battle. Preserve that truthful owner/payload,
            // but keep the incomplete Choice ledger visible until a fresh observed run resets it.
            if (decision.Owner != "none" && _agentChoiceHookError == AgentManualChoice.IncompleteReason)
            {
                decision.Ready = false;
                if (!decision.Unsupported.Contains(AgentManualChoice.IncompleteReason))
                    decision.Unsupported.Add(AgentManualChoice.IncompleteReason);
                signature = AgentSignature(decision);
            }
            if (signature != _agentSignature)
            {
                _agentSignature = signature;
                _agentRevision++;
                _agentDecisionId = _agentRunId + ":d" + _agentRevision;
                _agentScores.Clear(); _agentExcluded.Clear(); _agentChosen = null;
            }
            _agentDecision = decision;
            if (decision.Payload.GetValueOrDefault("schema")?.ToString() == "rs1/public_run_observation/v2")
            {
                decision.Payload["run_id"] = _agentRunId;
                decision.Payload["decision_id"] = _agentDecisionId;
            }
            else if (decision.Owner == "battle") decision.Payload["run_id"] = _agentRunId;
            _agentGuard.Publish(_agentRunId, _agentDecisionId, _agentRevision, decision.Candidates.Select(c => c.Id));
            _agentError = decision.Unsupported.Count == 0 ? null : string.Join("; ", decision.Unsupported);
            if (_agentError != null && _agentExecution == null && _agentMode is "auto" or "step") SetAgentMode("paused");
        }
        catch (Exception ex)
        {
            _agentDecision = new() { Phase = "Unsupported", Unsupported = new() { ex.Message } };
            _agentError = ex.Message;
            _agentScores.Clear(); _agentExcluded.Clear(); _agentChosen = null;
            if (_agentExecution == null && _agentMode is "auto" or "step") SetAgentMode("paused");
        }
    }

    private static string AgentSignature(AgentDecision decision)
    {
        var data = new
        {
            owner = decision.Owner, phase = decision.Phase, ready = decision.Ready,
            payload = decision.Payload, unsupported = decision.Unsupported,
            candidates = decision.Candidates.Select(c => new { c.Id, c.Kind, c.Public, c.Action }).ToArray()
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(data, _jsonOptions));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteAgentCanonical(writer, doc.RootElement);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static void WriteAgentCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var field in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (field.Name is "run_id" or "decision_id" or "decision" or "revision") continue;
                writer.WritePropertyName(field.Name); WriteAgentCanonical(writer, field.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteAgentCanonical(writer, item); writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private static object AgentControl() => new
    {
        mode = _agentMode, delay_seconds = _agentDelay, step_requested = _agentStepRequested,
        authorization_revision = _agentAuthorizationRevision,
        connected = _agentLastContact > 0 && Time.GetTicksMsec() / 1000.0 - _agentLastContact < 5,
        message = _agentMessage, error = _agentError, model = _agentModel, pending_request = _agentGuard.Pending,
        service_process = _agentServiceLauncher?.Snapshot,
        turbo = new { enabled = _agentTurbo.Enabled, stable_frames = _agentTurbo.StableFrames,
            refresh_seconds = _agentTurbo.RefreshInterval, minimum_delay_seconds = _agentTurbo.MinimumDelay,
            engine = _agentEngineApplied, live = AgentEngineLive(),
            card_trail = new { patch = _agentCardTrailPatch, suppressed = AgentCardTrailGuard.Suppressed },
            scene_inventory = new { builds = _sceneInventory.Builds, queries = _sceneInventory.Queries,
                version = _sceneInventory.Version } },
        punch_off = new { patch = _agentPunchOffPatch, skipped = AgentPunchOffGuard.Skipped },
        score_kind = "relative preference; not win probability", temperature = .25
    };

    private static ulong AgentFrame() => Engine.GetProcessFrames();

    // Presentation-only engine pacing. Native FastMode is read, never written
    // (it lives in the player's prefs); mechanics, RNG and saves are untouched.
    private static void ApplyAgentEngineTurbo()
    {
        bool engine = _agentTurbo.Enabled && (_agentTurbo.MaxFps >= 0 || _agentTurbo.TimeScale != 1);
        if (engine) _agentVsyncOriginal ??= DisplayServer.WindowGetVsyncMode();
        bool? disableVsync = _agentEngineGuard.Apply(_agentTurbo, _agentEngine);
        if (_agentVsyncOriginal is { } vsync && disableVsync is { } off)
        {
            var target = off ? DisplayServer.VSyncMode.Disabled : vsync;
            if (DisplayServer.WindowGetVsyncMode() != target) DisplayServer.WindowSetVsyncMode(target);
            if (!engine) _agentVsyncOriginal = null;
        }
        string fastMode;
        try { fastMode = SaveManager.Instance.PrefsSave.FastMode.ToString(); } catch (Exception ex) { fastMode = "unavailable: " + ex.Message; }
        _agentEngineApplied = new { turbo = _agentTurbo.Enabled, max_fps = Engine.MaxFps, time_scale = Engine.TimeScale,
            vsync = DisplayServer.WindowGetVsyncMode().ToString(), physics_ticks_per_second = Engine.PhysicsTicksPerSecond,
            native_fast_mode = fastMode, restored_original = !engine && !_agentEngineGuard.Active };
        _recording?.Append("agent_turbo_applied", new { settings = _agentEngineApplied, stable_frames = _agentTurbo.StableFrames,
            refresh_seconds = _agentTurbo.RefreshInterval, delay_seconds = _agentDelay });
    }

    // Live, not last-applied: GET control runs on the main frame, so these
    // are the values the engine is pacing with right now.
    private static object AgentEngineLive()
    {
        bool? limitBackground = null;
        try { limitBackground = SaveManager.Instance.SettingsSave.LimitFpsInBackground; } catch (Exception) { }
        var g = _agentEngineGuard;
        return new
        {
            frames_per_second = Engine.GetFramesPerSecond(), max_fps = Engine.MaxFps, time_scale = Engine.TimeScale,
            focused = DisplayServer.WindowIsFocused(), low_processor_usage_mode = OS.LowProcessorUsageMode,
            limit_fps_in_background = limitBackground, guard_active = g.Active, original_max_fps = g.OriginalMaxFps,
            original_time_scale = g.OriginalTimeScale, background_cap = g.BackgroundCap, stale_watch = g.StaleWatch,
            fps_reasserts = g.FpsReasserts, time_scale_reasserts = g.ScaleReasserts,
            foreground_adopts = g.ForegroundAdopts, stale_restores = g.StaleRestores, process_frames = Engine.GetProcessFrames()
        };
    }

    private static object AgentObservation()
    {
        RefreshAgentDecision();
        return new
        {
            schema = "sts2.live_agent/v1", run_id = _agentRunId, decision_id = _agentDecisionId, revision = _agentRevision,
            owner = _agentDecision.Owner, phase = _agentDecision.Phase, ready = _agentDecision.Ready && _agentDecision.Unsupported.Count == 0,
            unsupported = _agentDecision.Unsupported, payload = _agentDecision.Payload,
            candidates = _agentDecision.Candidates.Select(c => new { id = c.Id, kind = c.Kind, label = c.Label, @public = c.Public }).ToArray(),
            battle_terminal = GetAgentBattleTerminal(), run_terminal = _agentRunTerminal, control = AgentControl(),
            pending = AgentExecutionStatus()
        };
    }

    private static void SetAgentMode(string mode, bool consumeStep = false)
    {
        if (mode is not ("advice" or "step" or "auto" or "paused")) throw new ArgumentException("Unknown autoplay mode.");
        if (!consumeStep) _agentAuthorizationRevision++;
        _agentMode = mode; _agentGuard.Paused = mode == "paused";
        _agentStepRequested = mode == "step";
        _agentMessage = mode switch { "auto" => "Autoplay enabled", "step" => "One logical action requested", "paused" => "Paused — F6 or Advice to resume ratings", _ => "Advice only — ratings without moves" };
        if (mode == "paused") CancelQueuedAgentExecution();
    }

    private static object AgentReceiveScores(JsonElement body)
    {
        string decision = body.GetProperty("decision_id").GetString()!;
        long revision = body.GetProperty("revision").GetInt64();
        RefreshAgentDecision();
        if (decision != _agentDecisionId || revision != _agentRevision) return new { status = "stale" };
        var scores = body.GetProperty("scores").EnumerateArray().ToArray();
        var ids = scores.Select(x => x.GetProperty("id").GetString()!).ToArray();
        var excluded = body.TryGetProperty("excluded_ids", out var declined)
            ? declined.EnumerateArray().Select(x => x.GetString()!).ToArray() : Array.Empty<string>();
        if (ids.Distinct().Count() != ids.Length || excluded.Distinct().Count() != excluded.Length
            || ids.Intersect(excluded).Any() || !ids.Concat(excluded).ToHashSet().SetEquals(_agentDecision.Candidates.Select(x => x.Id)))
            throw new ArgumentException("Scores and explicit exclusions must identify every current candidate exactly once.");
        var values = scores.Select(x => x.GetProperty("q").GetDouble()).ToArray();
        var percentages = AgentGuard.Preferences(values, .25);
        string chosen = body.GetProperty("chosen_id").GetString()!;
        int best = Array.IndexOf(values, values.Max());
        if (!ids.Contains(chosen) || values[Array.IndexOf(ids, chosen)] != values[best])
            throw new ArgumentException("Selected candidate is not a greedy maximum.");
        _agentScores = ids.Select((id, i) => (id, q: values[i], p: percentages[i])).ToDictionary(x => x.id, x => (x.q, x.p));
        _agentChosen = chosen;
        _agentExcluded = excluded.ToHashSet();
        _agentModel = body.GetProperty("model_id").GetString()!;
        _agentLastContact = Time.GetTicksMsec() / 1000.0;
        return new { status = "ok", score_kind = "relative preference", temperature = .25 };
    }

    private static void HandleAgentRequest(HttpListenerRequest request, HttpListenerResponse response)
    {
        try
        {
            response.Headers.Remove("Access-Control-Allow-Origin");
            response.Headers.Remove("Access-Control-Allow-Methods");
            response.Headers.Remove("Access-Control-Allow-Headers");
            response.Headers["Cache-Control"] = "no-store";
            var remote = request.RemoteEndPoint?.Address;
            if (remote == null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)
                || request.Url == null || !IsRecorderLocalUri(request.Url))
            { SendError(response, 403, "Agent controls are available on localhost only."); return; }
            string? origin = request.Headers["Origin"];
            if (origin != null && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || !IsRecorderLocalUri(originUri)))
            { SendError(response, 403, "Agent controls reject other origins."); return; }
            string route = request.Url!.AbsolutePath["/api/v1/agent/".Length..];
            JsonElement body = default;
            if (request.HttpMethod == "POST")
            {
                if (!string.Equals((request.ContentType ?? "").Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                { SendError(response, 415, "Send agent requests as application/json."); return; }
                if (request.ContentLength64 > 262144) { SendError(response, 413, "Agent request is too large"); return; }
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                string text = reader.ReadToEnd();
                if (text.Length > 262144) { SendError(response, 413, "Agent request is too large"); return; }
                using var parsed = JsonDocument.Parse(text);
                body = parsed.RootElement.Clone();
            }
            object result = RunOnMainThread(() =>
            {
                _agentLastContact = Time.GetTicksMsec() / 1000.0;
                if (request.HttpMethod == "GET") return route switch
                {
                    "contract" => (object)new { schema = "sts2.live_agent/v1", mod_version = Version, contract_ready = AgentIds.Ready, contract_path = AgentIds.ContractPath, contract_error = AgentIds.Error, score_kind = "relative preference", policies = new[] { "n2-half-blue90-original-mac_a065000", "enemy_wide_deep_act3_ft_e032500" } },
                    "observation" => AgentObservation(),
                    "control" => AgentControl(),
                    "action" => (object)(_agentGuard.Get(request.QueryString["request_id"] ?? "") ?? new AgentRequestResult("unknown", request.QueryString["request_id"] ?? "")),
                    _ => new { status = "error", message = "Unknown agent route" }
                };
                if (request.HttpMethod != "POST") throw new ArgumentException("Use GET or POST.");
                if (route == "scores") return AgentReceiveScores(body);
                if (route == "action") return StartAgentExecution(body);
                if (route == "control")
                {
                    if (body.TryGetProperty("reload_contract", out var reload) && reload.GetBoolean()) AgentIds.Load();
                    _agentDelay = _agentTurbo.ApplyControl(body, _agentDelay);
                    ApplyAgentEngineTurbo();
                    if (body.TryGetProperty("mode", out var mode)) SetAgentMode(mode.GetString()!);
                    if (body.TryGetProperty("message", out var message))
                        _agentMessage = message.GetString() ?? "";
                    return AgentControl();
                }
                throw new ArgumentException("Unknown agent route.");
            }).GetAwaiter().GetResult();
            SendJson(response, result);
        }
        catch (Exception ex) { SendError(response, 400, "Agent request failed: " + ex.Message); }
    }
}
