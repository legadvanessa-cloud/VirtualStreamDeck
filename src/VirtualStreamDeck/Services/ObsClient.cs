using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.Services;

public enum ObsStatus { Disabled, Disconnected, Connecting, Connected, AuthFailed }

/// <summary>
/// Optional OBS Studio integration over obs-websocket v5 (built into OBS 28+). Nothing else in the
/// app depends on it: when OBS is closed the client just reports Disconnected and retries quietly.
/// </summary>
public sealed class ObsClient : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private System.Threading.Timer? _retry;
    private string _host = "127.0.0.1";
    private int _port = 4455;
    private string _password = "";
    private bool _enabled;

    public ObsStatus Status { get; private set; } = ObsStatus.Disabled;
    public string StatusText { get; private set; } = "OBS integration is off";
    public bool Streaming { get; private set; }
    public bool Recording { get; private set; }
    public bool RecordingPaused { get; private set; }
    public string CurrentScene { get; private set; } = "";
    public event Action? Changed;

    public void Configure(bool enabled, string host, int port, string password)
    {
        var changed = enabled != _enabled || host != _host || port != _port || password != _password;
        _enabled = enabled; _host = host; _port = port; _password = password;
        if (!changed) return;
        _ = DisconnectAsync();
        if (!enabled)
        {
            _retry?.Dispose();
            _retry = null;
            SetStatus(ObsStatus.Disabled, "OBS integration is off");
            return;
        }
        _retry ??= new System.Threading.Timer(_ => { if (_enabled && Status is ObsStatus.Disconnected) _ = ConnectAsync(); }, null, 15000, 15000);
        _ = ConnectAsync();
    }

    private void SetStatus(ObsStatus s, string text)
    {
        Status = s;
        StatusText = text;
        Changed?.Invoke();
    }

    public async Task<bool> ConnectAsync()
    {
        if (!_enabled) return false;
        await _connectLock.WaitAsync();
        try
        {
            if (Status == ObsStatus.Connected && _ws?.State == WebSocketState.Open) return true;
            SetStatus(ObsStatus.Connecting, "Connecting to OBS…");
            var ws = new ClientWebSocket();
            var cts = new CancellationTokenSource();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            timeout.CancelAfter(4000);
            await ws.ConnectAsync(new Uri($"ws://{_host}:{_port}"), timeout.Token);

            var hello = await ReceiveAsync(ws, timeout.Token) ?? throw new IOException("OBS closed the connection.");
            if (hello["op"]?.GetValue<int>() != 0) throw new IOException("Unexpected greeting from OBS.");
            var identify = new JsonObject
            {
                ["rpcVersion"] = 1,
                ["eventSubscriptions"] = 1 | 4 | 64, // General | Scenes | Outputs
            };
            var auth = hello["d"]?["authentication"];
            if (auth != null)
            {
                if (string.IsNullOrEmpty(_password))
                {
                    SetStatus(ObsStatus.AuthFailed, "OBS requires a password - enter it in Settings > Integrations.");
                    ws.Dispose();
                    return false;
                }
                identify["authentication"] = AuthString(_password, auth["salt"]!.GetValue<string>(), auth["challenge"]!.GetValue<string>());
            }
            await SendRawAsync(ws, new JsonObject { ["op"] = 1, ["d"] = identify }, timeout.Token);
            var identified = await ReceiveAsync(ws, timeout.Token);
            if (identified == null || identified["op"]?.GetValue<int>() != 2)
            {
                SetStatus(ObsStatus.AuthFailed, ws.CloseStatus == (WebSocketCloseStatus)4009
                    ? "OBS rejected the password."
                    : "OBS refused the connection.");
                ws.Dispose();
                return false;
            }

            _ws = ws;
            _cts = cts;
            _ = Task.Run(() => ReadLoop(ws, cts.Token));
            SetStatus(ObsStatus.Connected, $"Connected to OBS at {_host}:{_port}");
            await RefreshStateAsync();
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or JsonException)
        {
            SetStatus(ObsStatus.Disconnected, "OBS is not reachable (is it running with WebSocket server enabled?)");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("OBS connect failed", ex);
            SetStatus(ObsStatus.Disconnected, "OBS connection failed: " + ex.Message);
            return false;
        }
        finally { _connectLock.Release(); }
    }

    /// <summary>obs-websocket v5 auth: base64(sha256(base64(sha256(password + salt)) + challenge)).</summary>
    public static string AuthString(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    private async Task RefreshStateAsync()
    {
        try
        {
            var s = await RequestAsync("GetStreamStatus");
            Streaming = s["outputActive"]?.GetValue<bool>() ?? false;
            var r = await RequestAsync("GetRecordStatus");
            Recording = r["outputActive"]?.GetValue<bool>() ?? false;
            RecordingPaused = r["outputPaused"]?.GetValue<bool>() ?? false;
            var sc = await RequestAsync("GetCurrentProgramScene");
            CurrentScene = sc["currentProgramSceneName"]?.GetValue<string>() ?? sc["sceneName"]?.GetValue<string>() ?? "";
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Warn("OBS state refresh failed: " + ex.Message); }
    }

    private async Task ReadLoop(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var msg = await ReceiveAsync(ws, ct);
                if (msg == null) break;
                var op = msg["op"]?.GetValue<int>();
                var d = msg["d"] as JsonObject;
                if (d == null) continue;
                if (op == 7)
                {
                    var id = d["requestId"]?.GetValue<string>();
                    if (id != null && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(d);
                }
                else if (op == 5) OnEvent(d);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
        catch (Exception ex) { Log.Warn("OBS read loop: " + ex.Message); }
        foreach (var p in _pending) p.Value.TrySetException(new IOException("Connection to OBS was lost."));
        _pending.Clear();
        if (_ws == ws)
        {
            _ws = null;
            Streaming = Recording = RecordingPaused = false;
            if (_enabled) SetStatus(ObsStatus.Disconnected, "OBS disconnected");
        }
    }

    private void OnEvent(JsonObject d)
    {
        var type = d["eventType"]?.GetValue<string>();
        var data = d["eventData"] as JsonObject;
        switch (type)
        {
            case "StreamStateChanged":
                Streaming = data?["outputActive"]?.GetValue<bool>() ?? Streaming;
                break;
            case "RecordStateChanged":
                Recording = data?["outputActive"]?.GetValue<bool>() ?? Recording;
                var state = data?["outputState"]?.GetValue<string>();
                RecordingPaused = state == "OBS_WEBSOCKET_OUTPUT_PAUSED" || (state != "OBS_WEBSOCKET_OUTPUT_RESUMED" && RecordingPaused && Recording);
                if (!Recording) RecordingPaused = false;
                break;
            case "CurrentProgramSceneChanged":
                CurrentScene = data?["sceneName"]?.GetValue<string>() ?? CurrentScene;
                break;
            case "ExitStarted":
                break;
            default:
                return;
        }
        Changed?.Invoke();
    }

    public async Task<JsonObject> RequestAsync(string type, JsonObject? data = null)
    {
        if (_ws?.State != WebSocketState.Open && !await ConnectAsync())
            throw new InvalidOperationException(_enabled
                ? StatusText
                : "OBS integration is turned off. Enable it in Settings > Integrations.");
        var ws = _ws!;
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var req = new JsonObject { ["op"] = 6, ["d"] = new JsonObject { ["requestType"] = type, ["requestId"] = id, ["requestData"] = data ?? new JsonObject() } };
        await SendRawAsync(ws, req, CancellationToken.None);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        if (done != tcs.Task) { _pending.TryRemove(id, out _); throw new TimeoutException("OBS did not answer in time."); }
        var resp = await tcs.Task;
        var status = resp["requestStatus"];
        if (status?["result"]?.GetValue<bool>() != true)
        {
            var comment = status?["comment"]?.GetValue<string>();
            var code = status?["code"]?.GetValue<int>() ?? 0;
            throw new InvalidOperationException(comment ?? code switch
            {
                500 => "OBS: that output is already running.",
                501 => "OBS: that output is not running.",
                600 => "OBS: no scene or source with that name.",
                _ => $"OBS rejected the request (code {code}).",
            });
        }
        return resp["responseData"] as JsonObject ?? new JsonObject();
    }

    // ---- commands ----------------------------------------------------------------------------

    public async Task<string> ExecuteAsync(string command, string param, string param2)
    {
        switch (command)
        {
            case ObsCommands.StartStream: await RequestAsync("StartStream"); return "Streaming started";
            case ObsCommands.StopStream: await RequestAsync("StopStream"); return "Streaming stopped";
            case ObsCommands.ToggleStream:
                var s = await RequestAsync("ToggleStream");
                return s["outputActive"]?.GetValue<bool>() == true ? "Streaming started" : "Streaming stopped";
            case ObsCommands.StartRecord: await RequestAsync("StartRecord"); return "Recording started";
            case ObsCommands.StopRecord: await RequestAsync("StopRecord"); return "Recording stopped";
            case ObsCommands.ToggleRecord:
                var r = await RequestAsync("ToggleRecord");
                return r["outputActive"]?.GetValue<bool>() == true ? "Recording started" : "Recording stopped";
            case ObsCommands.PauseRecord:
                await RequestAsync("ToggleRecordPause");
                return RecordingPaused ? "Recording resumed" : "Recording paused";
            case ObsCommands.Scene:
                if (string.IsNullOrWhiteSpace(param)) throw new InvalidOperationException("No scene name is configured for this button.");
                await RequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = param });
                return $"Scene: {param}";
            case ObsCommands.ToggleSource:
                if (string.IsNullOrWhiteSpace(param) || string.IsNullOrWhiteSpace(param2))
                    throw new InvalidOperationException("Configure both the scene and the source name.");
                var item = await RequestAsync("GetSceneItemId", new JsonObject { ["sceneName"] = param, ["sourceName"] = param2 });
                var itemId = item["sceneItemId"]!.GetValue<int>();
                var en = await RequestAsync("GetSceneItemEnabled", new JsonObject { ["sceneName"] = param, ["sceneItemId"] = itemId });
                var next = !(en["sceneItemEnabled"]?.GetValue<bool>() ?? false);
                await RequestAsync("SetSceneItemEnabled", new JsonObject { ["sceneName"] = param, ["sceneItemId"] = itemId, ["sceneItemEnabled"] = next });
                return $"{param2} {(next ? "shown" : "hidden")}";
            case ObsCommands.ToggleMute:
                if (string.IsNullOrWhiteSpace(param)) throw new InvalidOperationException("No audio source name is configured.");
                var m = await RequestAsync("ToggleInputMute", new JsonObject { ["inputName"] = param });
                return $"{param} {(m["inputMuted"]?.GetValue<bool>() == true ? "muted" : "unmuted")}";
            default:
                throw new InvalidOperationException($"Unknown OBS command '{command}'.");
        }
    }

    public async Task<List<string>> SceneNamesAsync()
    {
        var r = await RequestAsync("GetSceneList");
        return (r["scenes"] as JsonArray)?.Select(n => n?["sceneName"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).Reverse().ToList() ?? new();
    }

    // ---- transport ---------------------------------------------------------------------------

    private async Task SendRawAsync(ClientWebSocket ws, JsonObject msg, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync(ct);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _sendLock.Release(); }
    }

    private static async Task<JsonObject?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16384];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return JsonNode.Parse(ms.ToArray()) as JsonObject;
    }

    public async Task DisconnectAsync()
    {
        var ws = _ws;
        _ws = null;
        _cts?.Cancel();
        if (ws == null) return;
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
        ws.Dispose();
    }

    public void Dispose()
    {
        _enabled = false;
        _retry?.Dispose();
        _ = DisconnectAsync();
    }
}
