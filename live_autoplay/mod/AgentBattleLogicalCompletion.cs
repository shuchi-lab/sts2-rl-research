using System;
using System.Collections.Generic;

namespace STS2_MCP;

internal static class BattleLogicalCompletion
{
    internal static Dictionary<string,object?> Execute(string candidateId,int expectedDecision,
        ref int decision,ref int steps,string runId,string fightId,
        Func<Dictionary<string,object?>> invoke,Func<Dictionary<string,object?>?> terminal)
    {
        if(string.IsNullOrEmpty(candidateId) || expectedDecision!=decision)
            throw new BattleProjectionUnsupported("Stale or missing Battle logical binding: "+candidateId);
        int beforeDecision=decision,beforeSteps=steps;
        var result=invoke();
        // Count only a native callback that returned explicit acceptance. A throw may
        // follow partial effects; preserve uncertainty rather than guessing acceptance.
        if((string?)result.GetValueOrDefault("status")=="ok") {
            if(decision!=beforeDecision || steps!=beforeSteps)
                throw new BattleProjectionUnsupported("Battle ledger changed inside native callback: "+candidateId);
            decision=checked(beforeDecision+1); steps=checked(beforeSteps+1);
            FinalizeRetainedTerminal(terminal(),runId,fightId,beforeDecision,beforeSteps,decision,steps);
        }
        return result;
    }
    internal static void FinalizeRetainedTerminal(Dictionary<string,object?>? terminal,string runId,string fightId,
        int beforeDecision,int beforeSteps,int decision,int steps)
    {
        if(terminal==null || (string?)terminal.GetValueOrDefault("run_id")!=runId ||
            (string?)terminal.GetValueOrDefault("fight_id")!=fightId) return;
        if(terminal.GetValueOrDefault("raw") is not Dictionary<string,object?> raw ||
            raw.GetValueOrDefault("status") is not string status || status is not ("win" or "loss") ||
            raw.GetValueOrDefault("decision") is not int retainedDecision || raw.GetValueOrDefault("steps") is not int retainedSteps)
            throw new BattleProjectionUnsupported("Matching Battle terminal has an invalid logical counter contract");
        bool before=retainedDecision==beforeDecision && retainedSteps==beforeSteps;
        bool after=retainedDecision==decision && retainedSteps==steps;
        if(!before && !after)
            throw new BattleProjectionUnsupported("Matching Battle terminal counters differ from the accepted callback boundary");
        // The terminal may have been captured synchronously before invoke returned.
        // Never refresh its pre-heal HP, potion inventory, history or other game fields.
        raw["decision"]=decision; raw["steps"]=steps;
    }
}
