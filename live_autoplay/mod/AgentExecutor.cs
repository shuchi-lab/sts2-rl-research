using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private sealed class AgentExecution
    {
        public required string Request;
        public required AgentCandidate Candidate;
        public required string Owner;
        public required JsonElement[] Stages;
        public required string BeforeSignature;
        public required double Started;
        public int Stage;
        public bool Dispatched;
        public double DispatchedAt, LastChangedAt;
        public string StageBefore = "", Observed = "";
        public Dictionary<int, bool> DeckBefore = new();
        public bool VerifyOneUpgrade;
        public int ExpectedCard;
        public int[]? ExpectedSelected;
        public int SelectionMaximum;
        public int ExpectedPotionSlot = -1, ExpectedPotionId = -1;
        public bool CancellationRequested;
        public bool ChoiceDecision;
        public bool HadManualSignal;
        public ulong StartedFrame;
        public AgentSettleCounter Settle = new();
    }
    private static AgentExecution? _agentExecution;
    private static object? _agentLastAction;

    private static object? AgentExecutionStatus() => _agentExecution == null ? _agentLastAction : new
    {
        request_id = _agentExecution.Request, status = "pending", stage = _agentExecution.Stage,
        stages = _agentExecution.Stages.Length, candidate_id = _agentExecution.Candidate.Id,
        dispatched = _agentExecution.Dispatched
    };

    private static object StartAgentExecution(JsonElement request)
    {
        string rid = request.GetProperty("request_id").GetString()!;
        var old = _agentGuard.Get(rid);
        if (old != null) return old;
        RefreshAgentDecision();
        if (_agentMode is not ("auto" or "step")) return new AgentRequestResult("paused", rid, "Enable Step or Auto inside the game.");
        if (!_agentDecision.Ready || _agentDecision.Unsupported.Count != 0) return new AgentRequestResult("waiting", rid, "Game is not at a supported ready decision.");
        string cid = request.GetProperty("candidate_id").GetString()!;
        if (_agentDecision.Owner != "automatic" && (_agentChosen != cid || !_agentScores.ContainsKey(cid)))
            return new AgentRequestResult("invalid", rid, "No current frozen-agent score for this candidate.");
        var admission = _agentGuard.Begin(rid, request.GetProperty("run_id").GetString()!,
            request.GetProperty("decision_id").GetString()!, request.GetProperty("revision").GetInt64(), cid);
        if (admission.Status != "accepted") return admission;
        var candidate = _agentDecision.Candidates.Single(c => c.Id == cid);
        var action = JsonSerializer.SerializeToElement(candidate.Action, _jsonOptions);
        JsonElement[] stages = action.TryGetProperty("stages", out var compound)
            ? compound.EnumerateArray().Select(x => x.Clone()).ToArray() : new[] { action };
        if (stages.Length == 0) { _agentGuard.Finish(rid, "failed", "Empty logical action"); return _agentGuard.Get(rid)!; }
        bool upgrade = action.TryGetProperty("integrity", out var integrity) && integrity.GetString() == "one_card_upgrade";
        _agentExecution = new()
        {
            Request = rid, Candidate = candidate, Owner = _agentDecision.Owner, Stages = stages,
            ChoiceDecision = _agentDecision.Payload.GetValueOrDefault("schema")?.ToString() == "rs1/public_run_observation/v2",
            HadManualSignal = _agentManualStrategySignature != null,
            BeforeSignature = AgentNativeSignature(), Started = Time.GetTicksMsec() / 1000.0,
            StartedFrame = AgentFrame(),
            VerifyOneUpgrade = upgrade,
            ExpectedCard = action.TryGetProperty("expected_card_instance", out var expected) ? expected.GetInt32() : -1,
            ExpectedSelected = action.TryGetProperty("expected_selected_instances", out var selected)
                ? selected.EnumerateArray().Select(x => x.GetInt32()).ToArray()
                : upgrade ? new[] { action.GetProperty("expected_card_instance").GetInt32() } : null,
            SelectionMaximum = action.TryGetProperty("selection_max", out var maximum) ? maximum.GetInt32() : 0,
            ExpectedPotionSlot = action.TryGetProperty("expected_slot", out var expectedSlot) ? expectedSlot.GetInt32() : -1,
            ExpectedPotionId = action.TryGetProperty("expected_potion_id", out var expectedPotion) ? expectedPotion.GetInt32() : -1,
            DeckBefore = upgrade ? AgentDeckUpgradeState() : new()
        };
        _agentManualStrategySignature = null;
        _recording?.Append("agent_action_begin", new { request_id = rid, candidate_id = cid, owner = _agentDecision.Owner,
            decision_id = _agentDecisionId, revision = _agentRevision, scores = _agentScores.Select(x => new { id = x.Key, q = x.Value.Q }).ToArray(), before = _agentDecision.Payload });
        RecorderSafe(() => { RecordBoundary("agent_before", new { request_id = rid }); SnapshotGameFiles("agent_before", false); });
        _agentStepRequested = false;
        return admission;
    }

    private static string AgentNativeSignature()
    {
        var state = JsonSerializer.SerializeToElement(BuildGameState(), _jsonOptions);
        var run = RunManager.Instance.IsInProgress ? RunManager.Instance.DebugOnlyGetState() : null;
        var player = run == null ? null : LocalContext.GetMe((IEnumerable<Player>)run.Players);
        var relics = player?.Relics.Select(AgentRelicExecutionWitness).ToArray()
            ?? Array.Empty<AgentRelicWitness>();
        return JsonSerializer.Serialize(new { game = AgentExecutionWitness.Normalize(state, relics),
            choice = AgentChoiceExecutionWitness(), selection_preview = AgentOpenSelectionPreview(), policy = _agentSignature }, _jsonOptions);
    }

    private static AgentRelicWitness AgentRelicExecutionWitness(RelicModel relic)
    {
        try {
            bool includeHidden = AgentMechanicalRelicCounterIncludesHiddenCounter(relic);
            return new AgentRelicWitness(relic.Id.Entry,
                (relic.ShowCounter || includeHidden) && TryAgentMechanicalRelicCounter(relic, out int counter) ? counter : null,
                includeHidden);
        } catch (BattleProjectionUnsupported) {
            // Execution also observes unsettled queue transitions (for example
            // combat turn0). An unavailable certificate leaves the observer row
            // raw and strict; Battle projection retains its own refusal gate.
            return new AgentRelicWitness(relic.Id.Entry, null);
        }
    }

    private static Dictionary<int, bool> AgentDeckUpgradeState()
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        var player = run == null ? null : LocalContext.GetMe(run);
        return player == null ? new() : player.Deck.Cards.ToDictionary(c => AgentObjectIds.Card(c.DeckVersion ?? c), c => c.IsUpgraded);
    }

    private static bool AgentNativeSettled(JsonElement state)
    {
        if (AgentCurrentQueueState(AgentSupportedSelectionSurface()) != AgentQueueState.Ready) return false;
        string type = state.TryGetProperty("state_type", out var t) ? t.GetString()! : "unknown";
        if (type is "hand_select" or "card_select" or "choose_card" or "bundle_select") return true;
        if (state.TryGetProperty("battle", out var battle) && battle.TryGetProperty("is_play_phase", out var play))
            return play.GetBoolean() && (!battle.TryGetProperty("turn", out var turn) || turn.GetString() == "player");
        return type != "unknown" && (RunManager.Instance.ActionQueueSet?.IsEmpty ?? true);
    }

    private static void ProcessAgentExecution(double now)
    {
        var pending = _agentExecution;
        if (pending == null) return;
        if (now - pending.Started > 45) { FinishAgentExecution("failed", "Logical action did not settle within45 seconds; inspect the game."); return; }
        var queueState = AgentCurrentQueueState(AgentSupportedSelectionSurface());
        if (queueState is AgentQueueState.Faulted or AgentQueueState.Cancelled)
        {
            FinishAgentExecution("failed", "Native action queue did not finish successfully: " + queueState);
            SetAgentMode("paused");
            return;
        }
        if (queueState != AgentQueueState.Ready) { pending.Settle.Reset(); return; }
        string stateText = AgentNativeSignature();
        using var doc = JsonDocument.Parse(stateText);
        var state = doc.RootElement.GetProperty("game");
        if (!pending.Dispatched)
        {
            if (_agentMode is not ("auto" or "step")) return;
            if (now - pending.Started < _agentDelay) return;
            // Turbo permits zero delay but still dispatches on a later main frame than admission.
            if (_agentTurbo.Enabled && AgentFrame() == pending.StartedFrame) return;
            if (pending.Stage == 0 && stateText != pending.BeforeSignature)
            {
                if (pending.HadManualSignal)
                {
                    if (pending.ChoiceDecision) NotifyAgentManualChoiceChange();
                    FinishAgentExecution("cancelled", "Game changed after manual input; autoplay paused for manual control.");
                    SetAgentMode("paused");
                }
                else
                {
                    // No stage executed. Reward removal and other native UI
                    // transitions can finish after admission; discard the old
                    // request and let the service score the fresh decision.
                    FinishAgentExecution("stale", "Game advanced before execution; refreshing the decision.");
                }
                return;
            }
            JsonElement stage = pending.Stages[pending.Stage];
            if (!AgentStageReady(stage, state)) return;
            if (pending.Stage > 0 && AgentStageChecks.RewardClaimAfterBeltRefill(pending.Stages[pending.Stage - 1], stage, AgentPotionBeltFull()))
            {
                // The use really happened; the bound incoming potion must still be offered, unclaimed,
                // for the next decision on the actual refilled belt. Nothing is clicked.
                if (AgentBoundRewardPotion(stage.GetProperty("reward_handle").GetInt32()) != stage.GetProperty("expected_reward_potion_id").GetInt32())
                { FinishAgentExecution("failed", "The bound incoming potion changed after the native belt refill."); return; }
                _recording?.Append("agent_action_stage", new { request_id = pending.Request, stage = pending.Stage, action = stage,
                    response = new { status = "not_dispatched", reason = "belt_refilled_by_potion_use" } });
                FinishAgentExecution("completed", "Potion used; native refilled the belt, so the bound incoming potion stays offered.");
                return;
            }
            if (stage.GetProperty("action").GetString() == "confirm_selection" && pending.ExpectedSelected != null
                && !AgentSelectionIntegrity(pending))
            { FinishAgentExecution("failed", "Selected card identities differ from the bound logical choice."); return; }
            pending.StageBefore = stateText; pending.Observed = stateText;
            pending.LastChangedAt = now;
            RecorderSafe(() => RecordBoundary("agent_stage_before", new { request_id = pending.Request, stage = pending.Stage }));
            Dictionary<string, object?> result;
            try
            {
                result = pending.Stages.Length == 1 && pending.Candidate.Execute != null
                    ? pending.Candidate.Execute() : ExecuteAgentStage(stage, state);
            }
            catch (Exception ex) { FinishAgentExecution("failed", ex.Message); return; }
            if (result.TryGetValue("status", out var status) && status?.ToString() == "error")
            { FinishAgentExecution("failed", result.GetValueOrDefault("error")?.ToString() ?? result.GetValueOrDefault("message")?.ToString() ?? "Native action refused."); return; }
            pending.Dispatched = true; pending.DispatchedAt = now;
            _recording?.Append("agent_action_stage", new { request_id = pending.Request, stage = pending.Stage, action = stage, response = result });
            return;
        }
        if (stateText != pending.Observed) { pending.Observed = stateText; pending.LastChangedAt = now; pending.Settle.Reset(); }
        if (!AgentStageSettled(pending, now, AgentStageChecks.Witnessed(pending.Stages[pending.Stage], pending.StageBefore, stateText, state)
            && AgentNativeSettled(state))) return;
        pending.Stage++;
        RecorderSafe(() => RecordBoundary("agent_stage_after", new { request_id = pending.Request, stage = pending.Stage - 1 }));
        pending.Dispatched = false;
        if (pending.Stage < pending.Stages.Length)
        {
            if (pending.CancellationRequested)
            {
                if (pending.ChoiceDecision) NotifyAgentManualChoiceChange();
                FinishAgentExecution("cancelled", "Paused after an executed compound stage; remaining stages cancelled. Inspect the open game selection.");
            }
            return;
        }
        if (pending.VerifyOneUpgrade)
        {
            var after = AgentDeckUpgradeState();
            if (!AgentIntegrity.OneUpgrade(pending.DeckBefore, after, pending.ExpectedCard))
            { FinishAgentExecution("failed", "One-card upgrade integrity failed; stop and preserve the recording."); return; }
        }
        if (pending.ExpectedPotionSlot >= 0 && pending.ExpectedPotionId >= 0
            && AgentPotionAt(pending.ExpectedPotionSlot) != pending.ExpectedPotionId)
        { FinishAgentExecution("failed", "The bound potion replacement did not reach its expected slot."); return; }
        FinishAgentExecution("completed", "Selected action completed.");
    }

    // Normal: .3s of unchanged wall-clock state. Turbo: the same witnessed,
    // queue-ready, settled state on consecutive distinct main frames.
    private static bool AgentStageSettled(AgentExecution pending, double now, bool settled) => _agentTurbo.Enabled
        ? pending.Settle.Observe(AgentFrame(), settled, _agentTurbo.StableFrames)
        : settled && now - pending.LastChangedAt >= AgentTurboSettings.NormalSettleSeconds;

    private static bool AgentStageReady(JsonElement stage, JsonElement state)
    {
        string action = stage.TryGetProperty("action", out var a) ? a.GetString()! : "";
        return AgentStageChecks.SelectionReady(action, state) ?? AgentNativeSettled(state);
    }

    private static Dictionary<string, object?> ExecuteAgentStage(JsonElement stage, JsonElement state)
    {
        var data = stage.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());
        string action = data["action"].GetString()!;
        if (action == "choose_rest_option" && data.TryGetValue("option_id", out var restId))
        {
            var buttons = FindAll<NRestSiteButton>(((SceneTree)Engine.GetMainLoop()).Root);
            var button = buttons.SingleOrDefault(b => b.Option.OptionId == restId.GetString());
            if (button == null || !button.IsEnabled || !button.IsVisibleInTree()) return Error("The bound rest option is no longer available.");
            button.ForceClick(); return new() { ["status"] = "ok" };
        }
        if (action is "discard_potion" or "use_potion" && data.TryGetValue("expected_potion_id", out var expectedPotion)
            && AgentPotionAt(data["slot"].GetInt32()) != expectedPotion.GetInt32())
            return Error("The bound potion slot changed before execution.");
        if (action == "claim_reward" && data.TryGetValue("reward_handle", out var rewardHandle))
        {
            if (NOverlayStack.Instance?.Peek() is not NRewardsScreen rewards) return Error("The bound reward screen is not open.");
            var button = FindAll<NRewardButton>(rewards).SingleOrDefault(b => b.Reward != null
                && AgentObjectIds.Get(b.Reward) == rewardHandle.GetInt32());
            if (button == null || !button.IsEnabled) return Error("The bound reward is no longer available.");
            if (data.TryGetValue("expected_reward_potion_id", out var incoming)
                && (button.Reward is not PotionReward potion || potion.Potion == null
                    || AgentIds.Id("potions", potion.Potion.Id.Entry) != incoming.GetInt32()))
                return Error("The bound incoming potion changed.");
            button.ForceClick();
            return new() { ["status"] = "ok" };
        }
        if (action == "select_card")
        {
            if (state.TryGetProperty("card_select", out var selection) && selection.TryGetProperty("preview_showing", out var preview) && preview.GetBoolean())
                return Error("A selection preview is open. Confirm or cancel before selecting again.");
            if (data.TryGetValue("card_instance", out var cardId))
                return TryAgentSelectCardInstance(cardId.GetInt32());
        }
        // An open preview completes only through its own Confirm; NDeckEnchantSelectScreen's main Confirm re-runs PreviewSelection.
        if (action == "confirm_selection" && NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen grid
            && AgentSelectionPreview.ConfirmOpen<Control, NConfirmButton>(n => grid.GetNodeOrNull<Control>(n), c => c.Visible,
                (c, n) => c.GetNodeOrNull<NConfirmButton>(n), b => b.IsEnabled && b.IsVisibleInTree(), b => b.ForceClick()) is { } previewed)
            return previewed;
        return ExecuteAction(action, data);
    }

    private static int AgentPotionAt(int slot)
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        var player = run == null ? null : LocalContext.GetMe(run);
        var potion = player?.PotionSlots.ElementAtOrDefault(slot);
        return potion == null ? 0 : AgentIds.Id("potions", potion.Id.Entry);
    }

    // PotionCmd.TryToProcure needs an open slot within MaxPotionCount (the candidate builder's test).
    private static bool AgentPotionBeltFull()
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        var player = run == null ? null : LocalContext.GetMe(run);
        return player != null && player.PotionSlots.Take(player.MaxPotionCount).All(p => p != null);
    }

    // The incoming potion id of the claimable bound reward row, or -1.
    private static int AgentBoundRewardPotion(int handle)
    {
        if (NOverlayStack.Instance?.Peek() is not NRewardsScreen rewards) return -1;
        var button = FindAll<NRewardButton>(rewards).SingleOrDefault(b => b.Reward != null && AgentObjectIds.Get(b.Reward) == handle);
        return button is { IsEnabled: true, Reward: PotionReward { Potion: { } potion } } && !AgentFinalSkippedRewards.Contains(button.Reward)
            ? AgentIds.Id("potions", potion.Id.Entry) : -1;
    }

    private static bool AgentSelectionIntegrity(AgentExecution execution)
    {
        if (NOverlayStack.Instance?.Peek() is not NCardGridSelectionScreen screen) return false;
        var selected = ((System.Collections.IEnumerable)AgentChoiceField(screen, "_selectedCards"))
            .Cast<CardModel>().Select(c => AgentObjectIds.Card(c.DeckVersion ?? c));
        return AgentIntegrity.SelectionMatches(execution.ExpectedSelected!, selected, execution.SelectionMaximum);
    }

    private static void CancelQueuedAgentExecution()
    {
        var pending = _agentExecution;
        if (pending == null) return;
        if (pending.Dispatched) { pending.CancellationRequested = true; return; }
        if (pending.Stage > 0 && pending.ChoiceDecision) NotifyAgentManualChoiceChange();
        FinishAgentExecution("cancelled", pending.Stage > 0
            ? "Paused after an executed compound stage; remaining stages cancelled. Inspect the open game selection."
            : "Paused before native execution.");
    }

    private static void FinishAgentExecution(string status, string message)
    {
        var execution = _agentExecution;
        if (execution == null) return;
        if (status == "completed" && execution.ChoiceDecision) NotifyAgentChoiceCommit(execution.Candidate, execution.Owner == "strategy");
        _agentGuard.Finish(execution.Request, status, message);
        _agentLastAction = new { request_id = execution.Request, candidate_id = execution.Candidate.Id, owner = execution.Owner, status, message };
        _recording?.Append("agent_action_end", new { request_id = execution.Request, status, message, after = BuildGameState() });
        RecorderSafe(() => { RecordBoundary("agent_after", new { request_id = execution.Request, status }); SnapshotGameFiles("agent_after", false); });
        _agentExecution = null;
        _agentMessage = message;
        if (status == "failed") { SetAgentMode("paused"); _agentMessage = message; }
        else if (_agentMode == "step") SetAgentMode("advice", consumeStep: true);
        RefreshAgentDecision();
    }
}
