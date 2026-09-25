using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using VirtualStreamDeck.Core;

namespace VirtualStreamDeck.Services;

/// <summary>What the remote surface needs from the app. Implemented by the UI controller.</summary>
public interface IRemoteHost
{
    Task<string> LayoutJsonAsync();
    Task<byte[]?> IconPngAsync(string buttonId);
    Task<(bool Ok, string Message)> PressAsync(string buttonId);
    Task SwitchPageAsync(int page);
    Task SwitchProfileAsync(string profileId);
}

public sealed record HttpRequest(string Method, string Path, string Query, Dictionary<string, string> Headers);

/// <summary>
/// Optional tablet / phone control surface on the local network. Deliberately tiny: a raw
/// TcpListener speaking just enough HTTP/1.1 for one embedded page and a JSON API. TcpListener
/// needs no URL ACL (unlike HttpListener), so it works without administrator rights; Windows
/// Firewall asks once when it is first enabled. Every API call requires the pairing PIN.
/// Off by default.
/// </summary>
public sealed class RemoteServer : IDisposable
{
    private readonly IRemoteHost _host;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _pin = "";
    private readonly Dictionary<IPAddress, (int Fails, DateTime Until)> _lockout = new();

    public bool Running => _listener != null;
    public int Port { get; private set; }
    public string? Error { get; private set; }
    public int Requests { get; private set; }

    public RemoteServer(IRemoteHost host) => _host = host;

    public static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("000000");

    public bool Start(int port, string pin)
    {
        Stop();
        _pin = pin;
        Port = port;
        try
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start(16);
            _listener = l;
            _cts = new CancellationTokenSource();
            _ = AcceptLoop(l, _cts.Token);
            Error = null;
            Log.Info($"Remote surface listening on port {port}");
            return true;
        }
        catch (SocketException ex)
        {
            Error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Port {port} is already in use by another program. Choose another port."
                : $"Could not open port {port}: {ex.Message}";
            _listener = null;
            return false;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    public static List<string> LocalUrls(int port)
    {
        var urls = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254."))
                        urls.Add($"http://{a.Address}:{port}/");
            }
        }
        catch { }
        return urls.Distinct().ToList();
    }

    private async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    internal static HttpRequest? Parse(string head)
    {
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 3) return null;
        var target = first[1];
        var q = target.IndexOf('?');
        var path = Uri.UnescapeDataString(q >= 0 ? target[..q] : target);
        var query = q >= 0 ? target[(q + 1)..] : "";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var c = line.IndexOf(':');
            if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        return new HttpRequest(first[0].ToUpperInvariant(), path, query, headers);
    }

    private bool PinOk(HttpRequest req, IPAddress ip)
    {
        lock (_lockout)
        {
            if (_lockout.TryGetValue(ip, out var lo) && lo.Until > DateTime.UtcNow) return false;
        }
        req.Headers.TryGetValue("X-Pin", out var pin);
        var ok = !string.IsNullOrEmpty(_pin) && pin != null &&
                 CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pin), Encoding.UTF8.GetBytes(_pin));
        lock (_lockout)
        {
            if (ok) { _lockout.Remove(ip); return true; }
            var f = _lockout.TryGetValue(ip, out var cur) ? cur.Fails + 1 : 1;
            _lockout[ip] = (f, f >= 8 ? DateTime.UtcNow.AddMinutes(1) : DateTime.MinValue);
        }
        return false;
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
        var ip = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        try
        {
            var stream = client.GetStream();
            var buf = new byte[8192];
            var total = 0;
            string head;
            while (true)
            {
                var n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct);
                if (n == 0) return;
                total += n;
                var s = Encoding.ASCII.GetString(buf, 0, total);
                var end = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0) { head = s[..end]; break; }
                if (total >= buf.Length) { await Write(stream, 431, "text/plain", "Request too large"u8.ToArray()); return; }
            }
            var req = Parse(head);
            if (req == null) { await Write(stream, 400, "text/plain", "Bad request"u8.ToArray()); return; }
            Requests++;

            if (req.Method == "GET" && (req.Path == "/" || req.Path == "/index.html"))
            {
                await Write(stream, 200, "text/html; charset=utf-8", Page());
                return;
            }
            if (!req.Path.StartsWith("/api/", StringComparison.Ordinal)) { await Write(stream, 404, "text/plain", "Not found"u8.ToArray()); return; }
            if (!PinOk(req, ip)) { await Write(stream, 401, "application/json", "{\"error\":\"pin\"}"u8.ToArray()); return; }

            var parts = req.Path.Split('/', StringSplitOptions.RemoveEmptyEntries); // api, x, y
            switch (req.Method, parts.Length > 1 ? parts[1] : "")
            {
                case ("GET", "layout"):
                    await Write(stream, 200, "application/json", Encoding.UTF8.GetBytes(await _host.LayoutJsonAsync()));
                    break;
                case ("GET", "icon") when parts.Length > 2:
                    var png = await _host.IconPngAsync(Path.GetFileNameWithoutExtension(parts[2]));
                    if (png == null) await Write(stream, 404, "text/plain", "Not found"u8.ToArray());
                    else await Write(stream, 200, "image/png", png, cache: true);
                    break;
                case ("POST", "press") when parts.Length > 2:
                    var (ok, msg) = await _host.PressAsync(parts[2]);
                    await Write(stream, 200, "application/json", Encoding.UTF8.GetBytes(Json.Serialize(new { ok, message = msg })));
                    break;
                case ("POST", "page") when parts.Length > 2 && int.TryParse(parts[2], out var pg):
                    await _host.SwitchPageAsync(pg);
                    await Write(stream, 200, "application/json", "{\"ok\":true}"u8.ToArray());
                    break;
                case ("POST", "profile") when parts.Length > 2:
                    await _host.SwitchProfileAsync(parts[2]);
                    await Write(stream, 200, "application/json", "{\"ok\":true}"u8.ToArray());
                    break;
                default:
                    await Write(stream, 404, "text/plain", "Not found"u8.ToArray());
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) { Log.Warn("Remote request failed: " + ex.Message); }
    }

    private static byte[]? _page;
    private static byte[] Page()
    {
        if (_page != null) return _page;
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("VirtualStreamDeck.remote.html");
        if (s == null) return _page = "Remote page missing from build"u8.ToArray();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return _page = ms.ToArray();
    }

    private static async Task Write(NetworkStream s, int code, string type, byte[] body, bool cache = false)
    {
        var reason = code switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 404 => "Not Found", 431 => "Request Header Fields Too Large", _ => "Error" };
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
                   $"Cache-Control: {(cache ? "max-age=86400" : "no-store")}\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(body);
    }

    public void Dispose() => Stop();
}
