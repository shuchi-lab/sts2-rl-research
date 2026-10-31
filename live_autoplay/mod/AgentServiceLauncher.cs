using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace STS2_MCP;

internal sealed record AgentServiceCommand(string Python, string Entry, string Endpoint, bool AutoStart)
{
    internal static AgentServiceCommand Load(string config, int port)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(config));
        string ReadPath(string key)
        {
            if (!doc.RootElement.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String
                || value.GetString() is not string path || !Path.IsPathFullyQualified(path))
                throw new InvalidDataException("Missing absolute " + key + " path. Reinstall the autoplay mod.");
            return path;
        }
        return new(ReadPath("agent_python"), ReadPath("agent_service"),
            "http://127.0.0.1:" + port + "/api/v1/agent",
            doc.RootElement.TryGetProperty("agent_auto_start", out var auto) && auto.ValueKind == JsonValueKind.True);
    }
}

internal interface IAgentServiceProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
}

internal sealed class AgentServiceProcess(Process process) : IAgentServiceProcess
{
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;
    public void Dispose() => process.Dispose();
}

/// <summary>Start the configured interpreter once; never launch a shell or kill a live owner.</summary>
internal sealed class AgentServiceLauncher(AgentServiceCommand command, Func<double> clock,
    Func<ProcessStartInfo, IAgentServiceProcess>? spawn = null)
{
    private IAgentServiceProcess? _child;
    private double _started, _nextPoll;
    private int? _lastPid, _lastExitCode;
    internal string Message { get; private set; } = "";
    internal object Snapshot => new { pid = _lastPid, running = _child != null, exit_code = _lastExitCode };
    internal bool CanOpen(bool connected) => !connected && _child == null;
    internal string Text(bool connected) => connected ? "Connected" : _child == null ? "Open" : "Starting…";

    internal bool Open(bool connected)
    {
        if (!CanOpen(connected)) return false;
        try
        {
            if (!File.Exists(command.Python) || !File.Exists(command.Entry))
                throw new FileNotFoundException("Python or the autoplay service was not found. Reinstall the autoplay mod.");
            var info = new ProcessStartInfo(command.Python)
            {
                WorkingDirectory = Path.GetDirectoryName(command.Entry)!,
                UseShellExecute = false, CreateNoWindow = true
            };
            foreach (string argument in new[] { "-B", command.Entry, "--endpoint", command.Endpoint })
                info.ArgumentList.Add(argument);
            _child = spawn == null ? new AgentServiceProcess(Process.Start(info)
                ?? throw new IOException("Python did not start.")) : spawn(info);
            _lastPid = _child.Id; _lastExitCode = null;
            _started = clock(); _nextPoll = _started;
            Message = "Starting the frozen Python agents…";
            return true;
        }
        catch (Exception error)
        {
            Message = "Could not open agents: " + error.GetBaseException().Message;
            return false;
        }
    }

    internal void Poll(bool connected)
    {
        if (connected) Message = "";
        if (_child == null || clock() < _nextPoll) return;
        _nextPoll = clock() + 1;
        if (_child.HasExited)
        {
            int code = _child.ExitCode;
            _lastExitCode = code;
            _child.Dispose(); _child = null;
            if (!connected) Message = code == 0 ? "Agents stopped. Open starts them again."
                : "Python agents exited with code " + code + ". Open retries startup.";
        }
        else if (!connected && clock() - _started > 30)
            Message = "Python is running, waiting to connect. Check the service log if this continues.";
    }
}
