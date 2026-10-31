using System;
using System.Collections.Generic;
using System.Linq;

namespace STS2_MCP;

public sealed record AgentRequestResult(string Status, string RequestId, string? Message = null);

/// <summary>Decision-bound request admission. Used by the native main-thread executor.</summary>
public sealed class AgentGuard
{
    private string _run = "", _decision = "";
    private long _revision;
    private HashSet<string> _candidates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentRequestResult> _requests = new(StringComparer.Ordinal);
    private readonly Queue<string> _closed = new();
    private string? _pending;
    public bool Paused { get; set; }
    public string? Pending => _pending;

    public void Publish(string run, string decision, long revision, IEnumerable<string> candidates)
    {
        _run = run; _decision = decision; _revision = revision;
        _candidates = new HashSet<string>(candidates, StringComparer.Ordinal);
    }

    public AgentRequestResult Begin(string request, string run, string decision, long revision, string candidate)
    {
        if (string.IsNullOrWhiteSpace(request)) return new("invalid", request, "Missing request ID.");
        if (_requests.TryGetValue(request, out var previous)) return previous;
        if (Paused) return new("paused", request, "Autoplay is paused.");
        if (run != _run || decision != _decision || revision != _revision)
            return new("stale", request, "The game decision changed; score the current observation.");
        if (!_candidates.Contains(candidate)) return new("invalid", request, "Candidate is not available.");
        if (_pending != null) return new("busy", request, "Another logical action is pending.");
        _pending = request;
        _requests[request] = new("pending", request);
        return new("accepted", request);
    }

    public AgentRequestResult? Get(string request) => _requests.GetValueOrDefault(request);

    public void Finish(string request, string status, string? message = null)
    {
        if (_pending != request) throw new InvalidOperationException("Completion does not own the pending request.");
        if (status is not ("completed" or "failed" or "cancelled" or "stale"))
            throw new ArgumentException("A request needs an explicit terminal status.", nameof(status));
        _requests[request] = new(status, request, message);
        _pending = null;
        _closed.Enqueue(request);
        while (_closed.Count > 2048) _requests.Remove(_closed.Dequeue());
    }

    public static double[] Preferences(IReadOnlyList<double> scores, double temperature)
    {
        if (!double.IsFinite(temperature) || temperature <= 0 || scores.Count == 0 || scores.Any(q => !double.IsFinite(q)))
            throw new ArgumentException("Preference needs finite scores and positive temperature.");
        double top = scores.Max();
        var weights = scores.Select(q => Math.Exp((q - top) / temperature)).ToArray();
        double sum = weights.Sum();
        return weights.Select(w => 100.0 * w / sum).ToArray();
    }
}
