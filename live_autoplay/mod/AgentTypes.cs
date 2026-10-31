using System;
using System.Collections.Generic;
using Godot;

namespace STS2_MCP;

// Shared transport structures. Native bindings never enter the policy payload.
public sealed class AgentCandidate
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public Dictionary<string, object?> Public { get; set; } = new();
    public Dictionary<string, object?> Action { get; set; } = new();
    public Control? Anchor { get; set; }
    public Func<Dictionary<string, object?>>? Execute { get; set; }
}

public sealed class AgentDecision
{
    public string Owner { get; set; } = "none";
    public string Phase { get; set; } = "Waiting";
    public Dictionary<string, object?> Payload { get; set; } = new();
    public List<AgentCandidate> Candidates { get; set; } = new();
    public List<string> Unsupported { get; set; } = new();
    public bool Ready { get; set; }
}
