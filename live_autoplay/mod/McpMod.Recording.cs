using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using STS2_MCP.Recording;

namespace STS2_MCP;

public static partial class McpMod
{
    private static RecordingJournal? _recording;
    private static RecordingJournal? _lastRecording;
    private static readonly List<string> _recordingHookErrors = new();
    private static readonly List<string> _recordingHooks = new();
    private static readonly HashSet<string> _recordedFileHashes = new();
    private static long _recordingOperation;
    private static long _snapshotErrors;
    private static bool _startedAtMenu;
    private static bool _recordedRunStarted;
    private static bool _recordedRunEnded;
    private static bool _abandonedRun;
    private static bool _bridgeUsed;
    private static int _selectorScopes;
    private static bool _recorderBusy;
    private static string? _recordingControlError;
    private static string? _lastObservedState;
    private static double _lastObserveAt;
    private static double? _gameOverAt;
    private static Label? _recordingBadge;
    private static bool _f8Down;
    private static readonly List<(long Id, string Kind, Task Task)> _observedTasks = new();

    private static string RecordingsRoot => Path.Combine(ProjectSettings.GlobalizePath("user://"), "manual-recordings");
    private static bool IsRecording => _recording != null;

    private static void InitializeRecording()
    {
        InstallRecordingHooks();
        RunManager.Instance.RunStarted += _ => RecorderSafe(() =>
        {
            if (!IsRecording) return;
            if (_recordedRunStarted)
            {
                _recording!.MarkFailed("A second run began in the same recording. Start a fresh recording per run.");
                return;
            }
            _recordedRunStarted = true;
            if (IsMultiplayerRun() || RunManager.Instance.NetService.Type.ToString() == "Replay")
                _recording!.MarkFailed("Only single-player play is supported.");
            RecordBoundary("run_started", null);
            SnapshotGameFiles("run_started");
        });
        RunManager.Instance.RoomExited += () => RecorderSafe(() => RecordBoundary("room_exited", null));
        RunManager.Instance.ActEntered += () => RecorderSafe(() => RecordBoundary("act_entered", null));
        RunManager.Instance.RoomEntered += () => RecorderSafe(() =>
        {
            RecordBoundary("room_entered", null);
            if (IsRecording) SnapshotGameFiles("room_entered");
        });
    }

    // Every recorder callback is contained: an observation must never interrupt play.
    private static void RecorderSafe(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            _snapshotErrors++;
            _recordingControlError = ex.GetType().Name + ": " + ex.Message;
            _recording?.Append("recorder_error", new { error = _recordingControlError });
            _recording?.MarkFailed("Recorder hook failed: " + _recordingControlError);
            GD.PrintErr("[STS2 Recorder] " + _recordingControlError);
        }
    }

    private static object RecordingStatus()
    {
        var session = _recording ?? _lastRecording;
        return new
        {
            recorder_version = 1,
            active = IsRecording,
            finalizing = !IsRecording && session != null && !session.Finished,
            directory = session?.DirectoryPath,
            recordings_root = RecordingsRoot,
            accepted_records = session?.AcceptedCount ?? 0,
            written_records = session?.WrittenCount ?? 0,
            elapsed_seconds = session?.ElapsedSeconds ?? 0,
            error = session?.Failure ?? _recordingControlError,
            snapshot_errors = _snapshotErrors,
            hook_errors = _recordingHookErrors,
            installed_hooks = _recordingHooks,
            started_at_menu = _startedAtMenu,
            run_started = _recordedRunStarted,
            run_ended = _recordedRunEnded,
            game_in_progress = RunManager.Instance.IsInProgress,
        };
    }

    private static object StartRecording()
    {
        if (IsRecording) throw new InvalidOperationException("A recording is already running.");
        if (_lastRecording != null && !_lastRecording.Finished)
            throw new InvalidOperationException("The previous recording is still being saved.");
        if (IsMultiplayerRun()) throw new InvalidOperationException("This recorder currently supports single-player runs.");
        if (_recordingHookErrors.Count != 0)
            throw new InvalidOperationException("Recorder hooks did not load: " + string.Join("; ", _recordingHookErrors));
        string gameAssembly = typeof(RunManager).Assembly.Location;
        string gameHash = RecordingJournal.Hash(File.ReadAllBytes(gameAssembly));
        if (gameHash is not ("9cb4f1ad8c9f284aa8fec3122ffd6d780bbf543d875c817abdd12ff63fbf12b4" or "71eec3ba5e0c640fc0cf0758517faf5f2ff039bea21966b22b23f2c427c7a09f"))
            throw new InvalidOperationException("The game DLL differs from the verified v0.111.0 build. Revalidate recorder hooks before recording.");
        _snapshotErrors = 0;
        _recordingControlError = null;
        _recordingOperation = 0;
        _lastObservedState = null;
        _recordedFileHashes.Clear();
        _observedTasks.Clear();
        _pausedActions.Clear();
        if (!RunManager.Instance.IsInProgress) _loadedSave = false;
        _gameOverAt = null;
        _startedAtMenu = !RunManager.Instance.IsInProgress;
        _recordedRunStarted = !_startedAtMenu;
        _recordedRunEnded = false;
        _abandonedRun = false;
        _bridgeUsed = false;
        _selectorScopes = 0;
        _reservedChoices.Clear();
        string directory = Path.Combine(RecordingsRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var header = new
        {
            schema_version = 1,
            created_utc = DateTimeOffset.UtcNow,
            game_version = ReleaseInfoManager.Instance.ReleaseInfo?.Version,
            release_commit = ReleaseInfoManager.Instance.ReleaseInfo?.Commit,
            game_dll_sha256 = gameHash,
            mod_dll_sha256 = RecordingJournal.Hash(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location)),
            mod_version = Version,
            started_at_menu = _startedAtMenu,
            partial_start = !_startedAtMenu,
            profile_id = SaveManager.Instance.CurrentProfileId,
            loaded_mod_assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && a.Location.Contains("/mods/", StringComparison.OrdinalIgnoreCase))
                .Select(a => new { path = a.Location, sha256 = RecordingJournal.Hash(File.ReadAllBytes(a.Location)) }).ToArray(),
            installed_hooks = _recordingHooks.ToArray(),
            capture_contract = "Passive game decisions, lifecycle, snapshots, RNG state and game-authored files. Observed frames retain their timing; not every frame is settled.",
            limitations = new[] { "Records accepted gameplay decisions, not mouse motion or every selection toggle.", "Automatic zero/one-candidate choices may reserve an ID without syncing; unpaired IDs are retained.", "Selector scopes are reported as coverage limits. Full-run replay integration and broad gameplay validation are not yet complete." },
        };
        _recording = new RecordingJournal(directory, header);
        RecordBoundary("recording_started", null);
        SnapshotGameFiles("recording_started");
        GD.Print("[STS2 Recorder] Recording to " + directory);
        return RecordingStatus();
    }

    private static object StopRecording(string reason = "user_stopped")
    {
        if (_recording == null) return RecordingStatus();
        FlushObservedTasks();
        RecordBoundary("recording_stopped", new { reason });
        SnapshotGameFiles("recording_stopped");
        var session = _recording;
        _recording = null;
        _lastRecording = session;
        _ = session.FinishAsync(reason, new
        {
            started_at_menu = _startedAtMenu,
            run_started = _recordedRunStarted,
            run_ended = _recordedRunEnded,
            loaded_existing_save = _loadedSave,
            abandoned = _abandonedRun,
            bridge_used = _bridgeUsed,
            selector_scopes = _selectorScopes,
            reserved_without_sync = _reservedChoices.ToArray(),
            snapshot_errors = _snapshotErrors,
            unfinished_operations = _observedTasks.Select(x => x.Id).ToArray(),
            capture_from_start_to_end = _startedAtMenu && !_loadedSave && _recordedRunStarted && _recordedRunEnded
                && session.Failure == null && _snapshotErrors == 0 && !_bridgeUsed && _selectorScopes == 0
                && _observedTasks.Count == 0 && _pausedActions.Count == 0,
            interpretation = "A complete journal is not a guarantee of simulator equivalence. Validate hook coverage and action/state boundaries when replaying.",
        });
        _observedTasks.Clear();
        return RecordingStatus();
    }

    private static object? CaptureSupplement()
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        if (run == null) return null;
        return new
        {
            seed = run.Rng.StringSeed,
            ascension = run.AscensionLevel,
            game_mode = run.GameMode.ToString(),
            act_index = run.CurrentActIndex,
            floor = run.TotalFloor,
            modifiers = run.Modifiers.Select(x => x.Id.ToString()).ToArray(),
            run_rng = run.Rng.ToSerializable(),
            synchronization = new
            {
                choice_ids = RunManager.Instance.PlayerChoiceSynchronizer?.ChoiceIds,
                reward_ids = RunManager.Instance.RewardsSetSynchronizer?.GetNextRewardIds().ToArray(),
                next_action_id = RunManager.Instance.ActionQueueSet?.NextActionId,
                next_hook_id = RunManager.Instance.ActionQueueSynchronizer?.NextHookId,
            },
            players = run.Players.Select(p => new
            {
                player_id = p.NetId,
                character = p.Character.Id.ToString(),
                player_rng = p.PlayerRng.ToSerializable(),
                deck = p.Deck.Cards.Select(RecordCard).ToArray(),
                combat_piles = p.PlayerCombatState?.AllPiles.Select(pile => new
                {
                    pile = pile.Type.ToString(),
                    cards = pile.Cards.Select(RecordCard).ToArray(),
                }).ToArray(),
            }).ToArray(),
        };
    }

    private static object? RecordCard(CardModel? card)
    {
        if (card == null) return null;
        uint? combatId = card.IsMutable && NetCombatCardDb.Instance.TryGetCardId(card, out uint value) ? value : null;
        return new
        {
            id = card.Id.Entry,
            upgrade_level = card.CurrentUpgradeLevel,
            combat_card_id = combatId,
            pile = card.Pile?.Type.ToString(),
            enchantment = card.Enchantment == null ? null : new { id = card.Enchantment.Id.Entry, amount = card.Enchantment.Amount },
        };
    }

    private static void RecordBoundary(string kind, object? detail)
    {
        if (_recording == null) return;
        if (_recorderBusy)
        {
            _recording.MarkFailed("A recorder hook re-entered a snapshot; a boundary could not be captured.");
            return;
        }
        _recorderBusy = true;
        try
        {
            object? state = null;
            object? supplement = null;
            string? error = null;
            try { state = BuildGameState(); }
            catch (Exception ex) { error = "state: " + ex.Message; }
            try { supplement = CaptureSupplement(); }
            catch (Exception ex) { error = (error == null ? "" : error + "; ") + "metadata: " + ex.Message; }
            if (error != null) _snapshotErrors++;
            _recording.Append(kind, new { detail, state, supplement, snapshot_error = error });
        }
        finally { _recorderBusy = false; }
    }

    private static void SnapshotGameFiles(string boundary, bool includeHistory = true)
    {
        if (_recording == null) return;
        string? progressPath = ResolveProfileProgressPath(SaveManager.Instance.CurrentProfileId);
        if (progressPath == null || !Path.IsPathRooted(progressPath) || !File.Exists(progressPath))
        {
            _snapshotErrors++;
            _recording.MarkFailed("Cannot locate the active profile progression file.");
            return;
        }
        string root = Directory.GetParent(Path.GetDirectoryName(progressPath)!)!.FullName;
        var paths = new List<string>();
        if (progressPath != null) paths.Add(progressPath);
        paths.Add(Path.Combine(root, "saves", "current_run.save"));
        paths.Add(Path.Combine(root, "replays", "latest.mcr"));
        if (includeHistory)
            foreach (string history in new[] { Path.Combine(root, "saves", "history"), Path.Combine(root, "history") })
                if (Directory.Exists(history)) paths.AddRange(Directory.EnumerateFiles(history, "*.run"));
        var copies = new List<object>();
        foreach (string path in paths.Distinct())
        {
            if (!File.Exists(path)) continue;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                string hash = RecordingJournal.Hash(bytes);
                string key = hash + "-" + Path.GetFileName(path);
                string? copy = "game-files/" + key;
                if (_recordedFileHashes.Add(key)) copy = _recording.SaveArtifact(Path.GetFileName(path), bytes);
                copies.Add(new { source = path, sha256 = hash, copy });
            }
            catch (Exception ex)
            {
                _snapshotErrors++;
                copies.Add(new { source = path, error = ex.Message });
            }
        }
        _recording.Append("game_files", new { boundary, files = copies });
    }

    private static long BeginObservedOperation(string kind, object? detail)
    {
        if (!IsRecording) return 0;
        FlushObservedTasks();
        long id = ++_recordingOperation;
        RecordBoundary("decision", new { operation_id = id, kind, value = detail });
        return id;
    }

    private static void WatchOperation(long id, string kind, Task? task)
    {
        if (id != 0 && IsRecording && task != null) _observedTasks.Add((id, kind, task));
    }

    private static void FlushObservedTasks()
    {
        if (!IsRecording) return;
        // Task status is observed, never replaced, awaited by the game, or resumed here.
        foreach (var item in _observedTasks.Where(x => x.Task.IsCompleted).ToArray())
        {
            _observedTasks.Remove(item);
            RecordBoundary("operation_completion_observed", new
            {
                operation_id = item.Id, kind = item.Kind,
                canceled = item.Task.IsCanceled,
                error = item.Task.Exception?.GetBaseException().Message,
                result = item.Task is Task<bool> boolean && boolean.IsCompletedSuccessfully ? (object)boolean.Result
                    : item.Task is Task<int> integer && integer.IsCompletedSuccessfully ? integer.Result : null,
            });
        }
    }

    private static void ProcessRecorderFrame()
    {
        RecorderSafe(() =>
        {
            bool pressed = Input.IsPhysicalKeyPressed(Key.F8);
            bool toggled = pressed && !_f8Down;
            _f8Down = pressed;
            if (toggled)
            {
                if (IsRecording) StopRecording(); else StartRecording();
            }
            if (_recordingBadge == null)
            {
                var canvas = new CanvasLayer { Layer = 1000 };
                _recordingBadge = new Label { Position = new Vector2(12, 32), MouseFilter = Control.MouseFilterEnum.Ignore };
                _recordingBadge.AddThemeFontSizeOverride("font_size", 18);
                canvas.AddChild(_recordingBadge);
                ((SceneTree)Engine.GetMainLoop()).Root.AddChild(canvas);
            }
            string? visibleError = _recordingControlError ?? (_recording ?? _lastRecording)?.Failure;
            _recordingBadge.Visible = IsRecording || visibleError != null;
            _recordingBadge.Text = visibleError != null
                ? "REC ERROR — open recorder"
                : $"● REC  {_recording?.AcceptedCount ?? 0} records  · F8 to stop";
            _recordingBadge.Modulate = visibleError != null ? Colors.OrangeRed : Colors.LightGreen;
            if (!IsRecording) return;
            FlushObservedTasks();
            double now = Time.GetTicksMsec() / 1000.0;
            if (now - _lastObserveAt < 0.2) return;
            _lastObserveAt = now;
            try
            {
                var state = BuildGameState();
                string text = JsonSerializer.Serialize(state, RecordingJournal.Json);
                if (text != _lastObservedState)
                {
                    _lastObservedState = text;
                    RecordBoundary("state_observed", null);
                }
                if (_recordedRunStarted && state.TryGetValue("state_type", out var type) && type?.ToString() == "game_over")
                {
                    _gameOverAt ??= now;
                    if (now - _gameOverAt > 2)
                    {
                        StopRecording(_recordedRunEnded ? "run_finished" : "game_over_screen");
                    }
                }
                else _gameOverAt = null;
            }
            catch (Exception ex)
            {
                // Transitions can temporarily lack a UI state. Preserve this explicitly.
                _snapshotErrors++;
                _recording?.Append("observation_error", new { error = ex.Message });
            }
        });
    }
}
