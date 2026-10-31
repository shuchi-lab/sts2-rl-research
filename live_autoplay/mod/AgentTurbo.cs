using System;
using System.Text.Json;

namespace STS2_MCP;

// Opt-in native turbo. It changes only bridge wall-clock pacing (run delay,
// refresh interval, post-stage debounce) and optional engine presentation
// settings; witness, queue, settlement and integrity checks are unchanged.
internal sealed class AgentTurboSettings
{
    internal const double NormalMinimumDelay = .1, NormalRefreshSeconds = .15, NormalSettleSeconds = .3;
    public bool Enabled { get; private set; }
    // Consecutive distinct main frames with the same witnessed, queue-ready, settled state.
    public int StableFrames { get; private set; } = 3;
    public double RefreshSeconds { get; private set; } = .05;
    // -1 leaves Engine.MaxFps untouched; 0 is uncapped.
    public int MaxFps { get; private set; } = -1;
    public double TimeScale { get; private set; } = 1;

    public double MinimumDelay => Enabled ? 0 : NormalMinimumDelay;
    public double RefreshInterval => Enabled ? RefreshSeconds : NormalRefreshSeconds;

    public double ApplyControl(JsonElement body, double delay)
    {
        if (body.TryGetProperty("turbo", out var turbo)) Enabled = turbo.GetBoolean();
        if (body.TryGetProperty("turbo_stable_frames", out var frames)) StableFrames = Math.Clamp(frames.GetInt32(), 1, 60);
        if (body.TryGetProperty("turbo_refresh_seconds", out var refresh)) RefreshSeconds = Math.Clamp(refresh.GetDouble(), 0, NormalRefreshSeconds);
        if (body.TryGetProperty("engine_max_fps", out var fps)) MaxFps = Math.Clamp(fps.GetInt32(), -1, 1000);
        // Native frame-based visual lerps diverge at higher clocks (observed card-trail OOM at 16x).
        if (body.TryGetProperty("engine_time_scale", out var scale)) TimeScale = Math.Clamp(scale.GetDouble(), 1, 4);
        if (body.TryGetProperty("delay_seconds", out var requested)) delay = requested.GetDouble();
        return Math.Clamp(delay, MinimumDelay, 10);
    }
}

internal sealed class AgentSettleCounter
{
    private ulong _frame = ulong.MaxValue;
    private int _count;

    public void Reset() { _frame = ulong.MaxValue; _count = 0; }

    // Callers reset on any witnessed-state change; a frame that is not settled
    // also resets. Repeated observations within one frame count once.
    public bool Observe(ulong frame, bool settled, int required)
    {
        if (!settled) { Reset(); return false; }
        if (frame != _frame) { _frame = frame; _count++; }
        return _count >= required;
    }
}

internal interface IAgentEngine
{
    int MaxFps { get; set; }
    double TimeScale { get; set; }
    bool Focused { get; }
}

// Keeps explicit turbo engine values against native presentation writers:
// NBackgroundModeHandler caps MaxFps on focus-out and restores its own saved
// copy on focus-in; NHitStop ends every hit-stop with Engine.SetTimeScale(1).
// Per frame it only compares two engine properties and writes on mismatch.
// Focus is read only after a mismatch, to classify a foreground write (player
// FPS setting / pre-turbo restore: becomes the restore value) from a
// background cap (kept so a backgrounded disable leaves the cap in place).
internal sealed class AgentEngineGuard
{
    private (int MaxFps, double TimeScale)? _original;
    private int _fps = -1;
    private double _scale = 1;
    private int? _backgroundCap;
    // After a backgrounded disable the native handler still holds our turbo
    // fps as its saved value; its next focus-in write is replaced once.
    private int? _staleFps, _staleRestore, _staleSeen;
    public int FpsReasserts { get; private set; }
    public int ScaleReasserts { get; private set; }
    public int ForegroundAdopts { get; private set; }
    public int StaleRestores { get; private set; }
    public bool Active => _original != null;
    public int? OriginalMaxFps => _original?.MaxFps;
    public double? OriginalTimeScale => _original?.TimeScale;
    public int? BackgroundCap => _backgroundCap;
    public bool StaleWatch => _staleFps != null;

    // Returns whether vsync should be disabled (true), restored (false), or left (null).
    public bool? Apply(AgentTurboSettings s, IAgentEngine e)
    {
        bool engine = s.Enabled && (s.MaxFps >= 0 || s.TimeScale != 1);
        if (engine)
        {
            _staleFps = null;
            if (_original == null)
            {
                _original = (e.MaxFps, e.TimeScale);
                _backgroundCap = e.Focused ? null : e.MaxFps;
            }
            _fps = s.MaxFps; _scale = s.TimeScale;
            if (e.TimeScale != _scale) e.TimeScale = _scale;
            int fps = _fps >= 0 ? _fps : _original.Value.MaxFps;
            if (e.MaxFps != fps) e.MaxFps = fps;
            return _fps >= 0;
        }
        if (_original is not { } o) return null;
        if (_fps >= 0 && _backgroundCap is int cap && !e.Focused)
        {
            e.MaxFps = cap;
            _staleFps = _fps; _staleRestore = o.MaxFps; _staleSeen = cap;
        }
        else e.MaxFps = o.MaxFps;
        e.TimeScale = o.TimeScale;
        _original = null; _backgroundCap = null; _fps = -1; _scale = 1;
        return false;
    }

    public void Tick(IAgentEngine e)
    {
        if (_original is not { } o)
        {
            if (_staleFps is not int stale) return;
            int now = e.MaxFps;
            if (now == _staleSeen) return;
            if (now == stale) { e.MaxFps = _staleRestore!.Value; StaleRestores++; }
            _staleFps = null;
            return;
        }
        if (_fps >= 0 && e.MaxFps != _fps)
        {
            int external = e.MaxFps;
            if (e.Focused) { _original = (external, o.TimeScale); _backgroundCap = null; ForegroundAdopts++; }
            else _backgroundCap = external;
            e.MaxFps = _fps; FpsReasserts++;
        }
        if (e.TimeScale != _scale) { e.TimeScale = _scale; ScaleReasserts++; }
    }
}
