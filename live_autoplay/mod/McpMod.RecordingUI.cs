using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Godot;

namespace STS2_MCP;

public static partial class McpMod
{
    private static void HandleRecorderRequest(HttpListenerRequest request, HttpListenerResponse response, bool page)
    {
        try
        {
            // The recorder is a same-origin local page, not a cross-origin control API.
            response.Headers.Remove("Access-Control-Allow-Origin");
            response.Headers.Remove("Access-Control-Allow-Methods");
            response.Headers.Remove("Access-Control-Allow-Headers");
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["X-Frame-Options"] = "DENY";

            var remote = request.RemoteEndPoint?.Address;
            var url = request.Url;
            if (remote == null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)
                || url == null || !IsRecorderLocalUri(url))
            {
                SendRecorderError(response, 403, "Recorder controls are available only on localhost:15526.");
                return;
            }

            string? originHeader = request.Headers["Origin"];
            if (originHeader != null
                && (!Uri.TryCreate(originHeader, UriKind.Absolute, out var origin)
                    || !IsRecorderLocalUri(origin)
                    || !string.Equals(origin.Host, url.Host, StringComparison.OrdinalIgnoreCase)
                    || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0))
            {
                SendRecorderError(response, 403, "Open the recorder page on the same localhost address to use these controls.");
                return;
            }

            if (page)
            {
                if (request.HttpMethod != "GET")
                {
                    response.Headers["Allow"] = "GET";
                    SendRecorderError(response, 405, "The recorder page accepts GET only.");
                    return;
                }
                response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
                SendText(response, RecorderPageHtml, "text/html");
                return;
            }

            if (request.HttpMethod == "GET")
            {
                SendJson(response, RunOnMainThread(() => RecordingStatus()).GetAwaiter().GetResult());
                return;
            }
            if (request.HttpMethod != "POST")
            {
                response.Headers["Allow"] = "GET, POST";
                SendRecorderError(response, 405, "Recorder API accepts GET and POST only.");
                return;
            }
            string mediaType = (request.ContentType ?? "").Split(';')[0].Trim();
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                SendRecorderError(response, 415, "Send recorder actions as application/json.");
                return;
            }

            const int maxBodyLength = 4096;
            if (request.ContentLength64 > maxBodyLength)
            {
                SendRecorderError(response, 413, "Recorder action is too large.");
                return;
            }
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8, false);
            char[] body = new char[maxBodyLength + 1];
            int length = 0;
            while (length < body.Length)
            {
                int read = reader.Read(body, length, body.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > maxBodyLength)
            {
                SendRecorderError(response, 413, "Recorder action is too large.");
                return;
            }
            using var json = JsonDocument.Parse(new string(body, 0, length), new JsonDocumentOptions { MaxDepth = 8 });
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("action", out var actionValue)
                || actionValue.ValueKind != JsonValueKind.String)
            {
                SendRecorderError(response, 400, "Provide a JSON object with an action: start, stop, or open_folder.");
                return;
            }
            string? action = actionValue.GetString();
            if (action is not ("start" or "stop" or "open_folder"))
            {
                SendRecorderError(response, 400, "Unknown recorder action. Use start, stop, or open_folder.");
                return;
            }
            object result = RunOnMainThread(() => action switch
            {
                "start" => StartRecording(),
                "stop" => StopRecording(),
                _ => OpenRecorderFolder(),
            }).GetAwaiter().GetResult();
            SendJson(response, result);
        }
        catch (JsonException)
        {
            SendRecorderError(response, 400, "Invalid recorder JSON.");
        }
        catch (InvalidOperationException ex)
        {
            SendRecorderError(response, 409, ex.Message);
        }
        catch (DirectoryNotFoundException)
        {
            SendRecorderError(response, 404, "No recording folder exists yet. Start a recording to create one.");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 Recorder] Recorder request failed: {ex.GetType().Name}: {ex.Message}");
            SendRecorderError(response, 500, "The recorder request failed. Check the game log for details.");
        }
    }

    private static bool IsRecorderLocalUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp && uri.Port == DefaultPort && uri.UserInfo.Length == 0
        && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host == "127.0.0.1" || uri.Host == "[::1]" || uri.Host == "::1");

    private static object OpenRecorderFolder()
    {
        string root = Path.GetFullPath(RecordingsRoot);
        string directory = Path.GetFullPath((_recording ?? _lastRecording)?.DirectoryPath ?? root);
        string relative = Path.GetRelativePath(root, directory);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("The recording directory is outside the recordings folder.");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException();
        if (Godot.OS.ShellOpen(directory) != Godot.Error.Ok)
            throw new InvalidOperationException("The recording folder could not be opened. You can copy its path from this page.");
        return RecordingStatus();
    }

    private static void SendRecorderError(HttpListenerResponse response, int statusCode, string message)
    {
        try { SendError(response, statusCode, message); }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
        {
            // A closed browser connection must not propagate into recording or gameplay.
        }
    }

    private const string RecorderPageHtml = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <meta name="color-scheme" content="dark">
          <title>STS2 · Manual recorder</title>
          <style>
            :root{color-scheme:dark;--bg:#11171c;--panel:#1b242b;--line:#34414a;--text:#f4f1e9;--muted:#aebbc1;--gold:#edca86;--green:#97ddb6;--red:#ffb6a5}
            *{box-sizing:border-box}body{margin:0;background:radial-gradient(ellipse at 85% 0%,#28342e 0,transparent 55%),var(--bg);color:var(--text);font:16px/1.55 -apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;min-height:100vh}
            main{max-width:920px;margin:auto;padding:52px 28px 36px}.eyebrow{text-transform:uppercase;letter-spacing:.18em;font-size:12px;color:var(--gold);font-weight:650}.eyebrow span{color:var(--muted);margin-left:12px;letter-spacing:.04em;font-weight:400}
            h1{font-size:clamp(32px,5vw,48px);line-height:1.13;letter-spacing:-.045em;font-weight:650;margin:19px 0 15px}.intro{color:var(--muted);max-width:620px;margin:0 0 30px;font-size:17px}
            .panel{background:var(--panel);border:1px solid var(--line);border-radius:18px;overflow:hidden;box-shadow:0 20px 60px #0002}.panel-body{padding:27px}.status-row{display:flex;align-items:center;justify-content:space-between;gap:16px}.status{display:flex;align-items:center;gap:11px;font-weight:600;font-size:20px}.dot{display:inline-block;width:10px;height:10px;flex-shrink:0;border-radius:50%;background:var(--muted)}.dot.active{background:var(--green);box-shadow:0 0 0 5px #97ddb612}.dot.saving{background:var(--gold)}.dot.error{background:var(--red)}
            .shortcut{color:var(--muted);font-size:13px;white-space:nowrap}kbd{border:1px solid #60717c;border-radius:5px;background:#202d36;color:var(--text);padding:2px 7px;font:12px ui-monospace,monospace;margin-right:5px}.description{color:var(--muted);margin:12px 0 23px;min-height:25px}
            .metrics{display:grid;grid-template-columns:1fr 1fr 1.4fr;gap:18px;margin:0 0 26px}.metric{border-left:1px solid var(--line);padding-left:18px}.metric:first-child{border-left:0;padding-left:0}.metric dt{font-size:12px;text-transform:uppercase;letter-spacing:.08em;color:var(--muted);margin-bottom:5px}.metric dd{font-size:24px;font-weight:550;letter-spacing:-.025em;margin:0;font-variant-numeric:tabular-nums}.metric.scope dd{font-size:18px;line-height:1.9}.detail{font-size:12px;color:var(--muted);min-height:19px}
            .controls{display:flex;flex-wrap:wrap;gap:10px}button{appearance:none;border:1px solid #53636e;border-radius:8px;padding:11px 17px;background:transparent;color:var(--text);font:inherit;font-size:14px;font-weight:600;line-height:1.4;cursor:pointer;transition:background .15s,opacity .15s}button:hover:not(:disabled){background:#2d3b46}button.primary{background:var(--gold);border-color:var(--gold);color:#22271e}button.primary:hover:not(:disabled){background:#f7dba8}button:disabled{opacity:.4;cursor:default}button:focus-visible,summary:focus-visible{outline:3px solid var(--green);outline-offset:4px}.folder-button{margin-left:auto}
            .path-area{padding:19px 27px;background:#141d23;border-top:1px solid var(--line)}.path-label{color:var(--muted);font-size:12px;margin-bottom:7px}.path{font:12px/1.6 ui-monospace,SFMono-Regular,Consolas,monospace;overflow-wrap:anywhere;color:#d0ddd8;user-select:all}.feedback{font-size:13px;color:var(--green);margin:11px 0 0;min-height:20px}
            .notice{border:1px solid #765747;background:#362822;border-radius:10px;color:#f7d5bb;padding:14px 17px;margin:16px 0 0;white-space:pre-wrap;overflow-wrap:anywhere;font-size:14px}.notice.connection{border-color:#4b6477;background:#1d2e3c;color:#d4e7f5}[hidden]{display:none!important}
            .steps{display:grid;grid-template-columns:repeat(3,1fr);gap:24px;margin-top:34px}.step-num{font:12px ui-monospace,monospace;color:var(--gold);display:block;margin-bottom:10px}.steps h2{font-size:15px;margin:0 0 7px;font-weight:600}.steps p{font-size:14px;color:var(--muted);margin:0}footer{margin-top:34px;padding-top:18px;border-top:1px solid #28353d;display:flex;gap:20px;justify-content:space-between;color:#8b9da5;font-size:12px}footer span:last-child{text-align:right}
            @media(max-width:600px){main{padding:30px 18px}.panel-body{padding:21px}.path-area{padding:17px 21px}.status-row{align-items:flex-start}.shortcut{font-size:11px}.metrics{grid-template-columns:1fr 1fr;gap:18px}.metric.scope{grid-column:1/-1;border-left:0;padding-left:0}.metric.scope dd{font-size:17px;line-height:1.5}.folder-button{margin-left:0}.steps{grid-template-columns:1fr;gap:22px}.steps .step-num{float:left;margin:4px 16px 40px 0}footer{display:block}footer span{display:block;margin-top:4px}footer span:last-child{text-align:left}}
          </style>
        </head>
        <body>
          <main>
            <div class="eyebrow">Slay the Spire 2 <span>Manual recorder</span></div>
            <h1>Record your next run.</h1>
            <p class="intro">Play the game with your mouse and keyboard. The recorder saves your decisions and game state for later simulator comparison.</p>
            <section class="panel" aria-label="Recording controls">
              <div class="panel-body">
                <div class="status-row">
                  <div class="status" role="status" aria-live="polite"><span id="dot" class="dot" aria-hidden="true"></span><span id="status-title">Connecting…</span></div>
                  <span class="shortcut"><kbd>F8</kbd> in game to start / stop</span>
                </div>
                <p id="status-description" class="description">Keep STS2 open while using the recorder.</p>
                <dl class="metrics">
                  <div class="metric"><dt>Elapsed</dt><dd id="elapsed">00:00</dd><div class="detail">This recording</div></div>
                  <div class="metric"><dt>Records saved</dt><dd id="written">0</dd><div id="pending" class="detail">Waiting to start</div></div>
                  <div class="metric scope"><dt>Recording start</dt><dd id="scope">Not started</dd><div id="run-state" class="detail">Start before creating a run</div></div>
                </dl>
                <div class="controls">
                  <button id="start" class="primary" type="button" disabled>Start recording</button>
                  <button id="stop" type="button" disabled>Stop &amp; save</button>
                  <button id="open-folder" class="folder-button" type="button" disabled>Open folder ↗</button>
                </div>
                <p id="feedback" class="feedback" role="status" aria-live="polite"></p>
              </div>
              <div class="path-area"><div id="path-label" class="path-label">Recordings are saved locally</div><div id="directory" class="path">Waiting for the game…</div></div>
            </section>
            <div id="connection-error" class="notice connection" role="alert" hidden></div>
            <div id="request-error" class="notice" role="alert" hidden></div>
            <div id="recording-error" class="notice" role="alert" hidden></div>
            <section class="steps" aria-label="How to record a run">
              <div><span class="step-num">01</span><h2>Start at the main menu</h2><p>Click Start recording before choosing New Run or Custom in STS2. This captures the run from its beginning.</p></div>
              <div><span class="step-num">02</span><h2>Play as usual</h2><p>Choose your character and play normally. Keep the game open; recording continues when you close this page.</p></div>
              <div><span class="step-num">03</span><h2>Stop, then open the folder</h2><p>Use Stop &amp; save or F8 in game, and wait for saving to finish. Open folder shows the files from this recording.</p></div>
            </section>
            <footer><span>One recording per run · Saved on this computer</span><span id="connection-state">Connecting to STS2</span></footer>
          </main>
          <script>
            (() => {
              'use strict';
              const byId = id => document.getElementById(id);
              const start = byId('start'), stop = byId('stop'), open = byId('open-folder');
              const number = new Intl.NumberFormat();
              let state = null, connected = false, busy = false, polling = false, epoch = 0;
              function notice(id, message) {
                const node = byId(id);
                node.textContent = message || '';
                node.hidden = !message;
              }
              function duration(value) {
                const seconds = Math.max(0, Math.floor(Number(value) || 0));
                const minutes = Math.floor(seconds / 60), hours = Math.floor(minutes / 60);
                return (hours ? String(hours).padStart(2, '0') + ':' : '') + String(minutes % 60).padStart(2, '0') + ':' + String(seconds % 60).padStart(2, '0');
              }
              function controls() {
                const hookErrors = state && Array.isArray(state.hook_errors) && state.hook_errors.length > 0;
                start.disabled = busy || !connected || !state || state.active || state.finalizing || hookErrors;
                stop.disabled = busy || !connected || !state || !state.active;
                open.disabled = busy || !connected;
              }
              function render(data) {
                state = data;
                connected = true;
                notice('connection-error', '');
                const issues = [];
                if (data.error) issues.push(String(data.error));
                if (Array.isArray(data.hook_errors)) issues.push(...data.hook_errors.map(String));
                if (Number(data.snapshot_errors) > 0) issues.push(number.format(data.snapshot_errors) + ' observations could not be captured. Check this recording before comparing it with the simulator.');
                notice('recording-error', issues.join('\n'));
                let title, description, tone = '';
                if (data.active) {
                  title = issues.length ? 'Recording needs attention' : 'Recording';
                  description = data.run_started ? 'Your play is being recorded. Continue in STS2.' : 'Recording has started. Choose New Run or Custom in STS2 when ready.';
                  tone = issues.length ? 'error' : 'active';
                } else if (data.finalizing) {
                  title = 'Saving recording…'; description = 'The recorder is finishing the files. Keep the game open.'; tone = 'saving';
                } else if (data.directory) {
                  title = issues.length ? 'Stopped · check recording' : 'Recording saved';
                  description = issues.length ? 'The files are available, but the reported errors need review.' : 'Open the folder to inspect the saved files. Start a fresh recording for your next run.';
                  tone = issues.length ? 'error' : '';
                } else {
                  title = issues.length ? 'Recorder unavailable' : 'Ready to record';
                  description = data.game_in_progress ? 'A run is already open. Starting now records only the remainder of this run.' : 'Start recording at the main menu, before choosing New Run or Custom.';
                  tone = issues.length ? 'error' : '';
                }
                byId('status-title').textContent = title;
                byId('status-description').textContent = description;
                byId('dot').className = 'dot' + (tone ? ' ' + tone : '');
                byId('elapsed').textContent = duration(data.elapsed_seconds);
                byId('written').textContent = number.format(data.written_records || 0);
                const pending = Math.max(0, Number(data.accepted_records || 0) - Number(data.written_records || 0));
                byId('pending').textContent = pending ? number.format(pending) + ' waiting to save' : data.directory ? 'No records waiting to save' : 'Waiting to start';
                byId('scope').textContent = data.directory ? (data.started_at_menu ? 'From the menu' : 'Started mid-run') : 'Not started';
                byId('run-state').textContent = !data.directory ? 'Start before creating a run' : !data.started_at_menu ? 'Earlier gameplay is not included' : data.run_ended ? 'Run ended' : data.run_started ? 'Run start recorded' : 'Waiting for a new run';
                byId('path-label').textContent = data.directory ? 'This recording' : 'Recordings folder';
                byId('directory').textContent = data.directory || data.recordings_root || 'No folder reported';
                byId('connection-state').textContent = 'Connected · updates every second';
                controls();
              }
              async function request(action) {
                const controller = new AbortController();
                const timeout = setTimeout(() => controller.abort(), 12000);
                try {
                  const options = { cache: 'no-store', credentials: 'same-origin', signal: controller.signal };
                  if (action) {
                    options.method = 'POST';
                    options.headers = { 'Content-Type': 'application/json' };
                    options.body = JSON.stringify({ action });
                  }
                  const response = await fetch('/api/v1/recorder', options);
                  let data;
                  try { data = await response.json(); } catch { throw new Error('The game returned an unreadable recorder response.'); }
                  if (!response.ok) throw new Error(data.error || 'Recorder request failed (' + response.status + ').');
                  if (!data || typeof data.active !== 'boolean') throw new Error('The game returned an unexpected recorder status.');
                  return data;
                } finally { clearTimeout(timeout); }
              }
              async function poll() {
                if (busy || polling) return;
                polling = true;
                const currentEpoch = epoch;
                try {
                  const data = await request();
                  if (currentEpoch === epoch) render(data);
                } catch (error) {
                  if (currentEpoch === epoch) {
                    connected = false;
                    byId('status-title').textContent = 'Game connection unavailable';
                    byId('dot').className = 'dot error';
                    byId('connection-state').textContent = 'Reconnecting…';
                    notice('connection-error', 'Could not read recorder status. Keep STS2 open with the recorder mod loaded. ' + (error.name === 'AbortError' ? 'The game did not respond in time.' : error.message));
                    controls();
                  }
                } finally { polling = false; }
              }
              async function command(action) {
                if (busy) return;
                busy = true; epoch++;
                controls();
                notice('request-error', '');
                byId('feedback').textContent = action === 'start' ? 'Starting recording…' : action === 'stop' ? 'Stopping recording…' : 'Opening the folder…';
                try {
                  render(await request(action));
                  byId('feedback').textContent = action === 'open_folder' ? 'Recording folder opened.' : action === 'start' ? 'Recording started. Return to STS2 to play.' : 'Recording stopped. Wait for any remaining files to finish saving.';
                } catch (error) {
                  byId('feedback').textContent = '';
                  notice('request-error', error.name === 'AbortError' || error instanceof TypeError ? 'The request could not be confirmed. The command may still complete; check the updated status before trying again.' : error.message);
                } finally {
                  busy = false;
                  controls();
                  void poll();
                }
              }
              start.addEventListener('click', () => void command('start'));
              stop.addEventListener('click', () => void command('stop'));
              open.addEventListener('click', () => void command('open_folder'));
              void poll();
              setInterval(() => void poll(), 1000);
            })();
          </script>
        </body>
        </html>
        """;
}
