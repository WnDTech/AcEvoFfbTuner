using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core;

public sealed class FfbLiveServer : IDisposable
{
    private TcpListener? _tcpListener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private readonly ConcurrentBag<SseClient> _clients = new();

    private const int MaxHistory = 600;
    private readonly float[] _hForce = new float[MaxHistory];
    private readonly float[] _hSteer = new float[MaxHistory];
    private readonly float[] _hSpeed = new float[MaxHistory];
    private readonly float[] _hMz = new float[MaxHistory];
    private readonly float[] _hFx = new float[MaxHistory];
    private readonly float[] _hFy = new float[MaxHistory];
    private readonly float[] _hGas = new float[MaxHistory];
    private readonly float[] _hBrake = new float[MaxHistory];
    private readonly int[] _hGear = new int[MaxHistory];
    private readonly float[] _hRpm = new float[MaxHistory];
    private readonly int[] _hLap = new int[MaxHistory];
    private int _hIdx;
    private int _hCount;
    private readonly object _dataLock = new();

    private int _sendCounter;
    private const int SendEveryNth = 2;

    private readonly StringBuilder _sb = new(2048);

    public int Port { get; }
    public bool IsRunning => _tcpListener != null;
    public bool IsNetworkEnabled { get; private set; }
    public List<string> NetworkAddresses { get; private set; } = new();

    public FfbLiveServer(int port = 8321)
    {
        Port = port;
    }

    public void Start()
    {
        if (_tcpListener != null) return;
        NetworkAddresses = GetLocalNetworkAddresses();
        _tcpListener = new TcpListener(IPAddress.Any, Port);
        try
        {
            _tcpListener.Start();
            IsNetworkEnabled = NetworkAddresses.Count > 0;
        }
        catch (SocketException)
        {
            _tcpListener = null;
            return;
        }
        _listenTask = Task.Run(() => AcceptLoop(_cts.Token));
    }

    public static List<string> GetLocalNetworkAddresses()
    {
        var ips = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var ipProps = ni.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        ips.Add(addr.Address.ToString());
                    }
                }
            }
            if (ips.Count == 0)
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var addr in host.AddressList)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
                    {
                        ips.Add(addr.ToString());
                    }
                }
            }
        }
        catch { }
        return ips;
    }

    public void Stop()
    {
        _cts.Cancel();
        foreach (var c in _clients)
        {
            try { c.TcpClient.Close(); } catch { }
        }
        _clients.Clear();
        try { _tcpListener?.Stop(); } catch { }
        _tcpListener = null;
    }

    public void OnData(FfbRawData raw, FfbProcessedData proc)
    {
        lock (_dataLock)
        {
            _hForce[_hIdx] = proc.MainForce;
            _hSteer[_hIdx] = raw.SteerAngle;
            _hSpeed[_hIdx] = raw.SpeedKmh;
            _hMz[_hIdx] = proc.ChannelMzFront;
            _hFx[_hIdx] = proc.ChannelFxFront;
            _hFy[_hIdx] = proc.ChannelFyFront;
            _hGas[_hIdx] = raw.GasInput;
            _hBrake[_hIdx] = raw.BrakeInput;
            _hGear[_hIdx] = raw.Gear;
            _hRpm[_hIdx] = raw.RpmPercent;
            _hLap[_hIdx] = raw.CurrentLap;
            _hIdx = (_hIdx + 1) % MaxHistory;
            if (_hCount < MaxHistory) _hCount++;
        }

        _sendCounter++;
        if (_sendCounter < SendEveryNth) return;
        _sendCounter = 0;

        lock (_sb)
        {
            _sb.Clear();
            _sb.Append("{\"t\":");
            _sb.Append((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 10000000).ToString());
            _sb.Append(",\"fo\":");
            _sb.Append(proc.MainForce.ToString("F5"));
            _sb.Append(",\"st\":");
            _sb.Append(raw.SteerAngle.ToString("F4"));
            _sb.Append(",\"sp\":");
            _sb.Append(raw.SpeedKmh.ToString("F1"));
            _sb.Append(",\"mz\":");
            _sb.Append(proc.ChannelMzFront.ToString("F6"));
            _sb.Append(",\"fx\":");
            _sb.Append(proc.ChannelFxFront.ToString("F6"));
            _sb.Append(",\"fy\":");
            _sb.Append(proc.ChannelFyFront.ToString("F6"));
            _sb.Append(",\"ga\":");
            _sb.Append(raw.GasInput.ToString("F3"));
            _sb.Append(",\"br\":");
            _sb.Append(raw.BrakeInput.ToString("F3"));
            _sb.Append(",\"cp\":");
            _sb.Append(proc.PostCompressionForce.ToString("F6"));
            _sb.Append(",\"dm\":");
            _sb.Append(proc.PostDampingForce.ToString("F6"));
            _sb.Append(",\"go\":");
            _sb.Append(proc.PostOutputGainForce.ToString("F6"));
            _sb.Append(",\"lu\":");
            _sb.Append(proc.PostLutForce.ToString("F6"));
            _sb.Append(",\"dy\":");
            _sb.Append(proc.PostDynamicForce.ToString("F6"));
            _sb.Append(",\"cl\":");
            _sb.Append(proc.IsClipping ? "1" : "0");
            _sb.Append(",\"wl0\":");
            _sb.Append(raw.WheelLoad[0].ToString("F0"));
            _sb.Append(",\"wl1\":");
            _sb.Append(raw.WheelLoad[1].ToString("F0"));
            _sb.Append(",\"sr0\":");
            _sb.Append(raw.SlipRatio[0].ToString("F4"));
            _sb.Append(",\"sr1\":");
            _sb.Append(raw.SlipRatio[1].ToString("F4"));
            _sb.Append(",\"sa0\":");
            _sb.Append(raw.SlipAngle[0].ToString("F4"));
            _sb.Append(",\"sa1\":");
            _sb.Append(raw.SlipAngle[1].ToString("F4"));
            _sb.Append(",\"rmz0\":");
            _sb.Append(raw.Mz[0].ToString("F4"));
            _sb.Append(",\"rmz1\":");
            _sb.Append(raw.Mz[1].ToString("F4"));
            _sb.Append(",\"rfx0\":");
            _sb.Append(raw.Fx[0].ToString("F2"));
            _sb.Append(",\"rfx1\":");
            _sb.Append(raw.Fx[1].ToString("F2"));
            _sb.Append(",\"rfy0\":");
            _sb.Append(raw.Fy[0].ToString("F2"));
            _sb.Append(",\"rfy1\":");
            _sb.Append(raw.Fy[1].ToString("F2"));
            _sb.Append(",\"ge\":");
            _sb.Append(raw.Gear.ToString());
            _sb.Append(",\"rp\":");
            _sb.Append(raw.RpmPercent.ToString("F3"));
            _sb.Append(",\"la\":");
            _sb.Append(raw.CurrentLap.ToString());
            _sb.Append(",\"np\":");
            _sb.Append(raw.Npos.ToString("F4"));
            _sb.Append('}');

            var payload = Encoding.UTF8.GetBytes("data: " + _sb.ToString() + "\n\n");
            Broadcast(payload);
        }
    }

    public void TriggerSnapshot()
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        var payload = Encoding.UTF8.GetBytes($"event: snapshot\ndata: {{\"ts\":\"{ts}\"}}\n\n");
        Broadcast(payload);
    }

    private void Broadcast(byte[] payload)
    {
        var dead = new List<SseClient>();
        foreach (var c in _clients)
        {
            try
            {
                c.Queue.Enqueue(payload);
                c.Signal.Set();
            }
            catch
            {
                dead.Add(c);
            }
        }
        foreach (var d in dead)
        {
            _clients.TryTake(out _);
        }
    }

    private async void AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _tcpListener != null)
        {
            try
            {
                var tcpClient = await _tcpListener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClient(tcpClient, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
        }
    }

    private async void HandleClient(TcpClient tcpClient, CancellationToken ct)
    {
        try
        {
            var stream = tcpClient.GetStream();
            var (method, path, query) = await ReadHttpRequest(stream, ct);
            if (method != "GET") return;

            if (path == "/stream")
            {
                var sseClient = new SseClient(tcpClient);
                _clients.Add(sseClient);
                await SseWriter(sseClient, ct);
            }
            else
            {
                if (path == "/history")
                    await ServeHistory(stream);
                else if (path == "/overlay")
                    await ServeOverlay(stream, query);
                else
                    await ServeHtml(stream, query);
            }
        }
        catch { }
        finally
        {
            try { tcpClient.Close(); } catch { }
        }
    }

    private static async Task<(string method, string path, string query)> ReadHttpRequest(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct);
            if (requestLine == null) return ("", "", "");

            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return ("", "", "");

            string? line;
            do
            {
                line = await reader.ReadLineAsync(ct);
            } while (line != null && line != "");

            var pathAndQuery = parts[1];
            var qIdx = pathAndQuery.IndexOf('?');
            var path = qIdx >= 0 ? pathAndQuery[..qIdx] : pathAndQuery;
            var query = qIdx >= 0 ? pathAndQuery[qIdx..] : "";

            return (parts[0], path, query);
        }
        catch { return ("", "", ""); }
    }

    private static async Task WriteResponse(NetworkStream stream, int statusCode, string statusText, string contentType, byte[] body)
    {
        var header = $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(header);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
        if (body.Length > 0)
            await stream.WriteAsync(body, 0, body.Length);
    }

    private static async Task WriteSseHeaders(NetworkStream stream)
    {
        var header = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nAccess-Control-Allow-Origin: *\r\nConnection: keep-alive\r\n\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(header);
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
        await stream.FlushAsync();
    }

    private async Task SseWriter(SseClient client, CancellationToken ct)
    {
        try
        {
            await WriteSseHeaders(client.Stream);

            string initJson;
            lock (_dataLock)
            {
                _sb.Clear();
                _sb.Append("{\"force\":[");
                AppendHistoryArray(_hForce);
                _sb.Append("],\"steer\":[");
                AppendHistoryArray(_hSteer);
                _sb.Append("],\"speed\":[");
                AppendHistoryArray(_hSpeed);
                _sb.Append("]}");
                initJson = _sb.ToString();
            }

            var init = Encoding.UTF8.GetBytes("data: " + initJson + "\n\n");
            await client.Stream.WriteAsync(init, 0, init.Length, ct);
            await client.Stream.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                client.Signal.Wait(500, ct);
                client.Signal.Reset();

                while (client.Queue.TryDequeue(out var payload))
                {
                    await client.Stream.WriteAsync(payload, 0, payload.Length, ct);
                }
                await client.Stream.FlushAsync(ct);
            }
        }
        catch { }
        finally
        {
            _clients.TryTake(out _);
            try { client.TcpClient.Close(); } catch { }
        }
    }

    private async Task ServeHistory(NetworkStream stream)
    {
        string json;
        lock (_dataLock)
        {
            _sb.Clear();
            _sb.Append("{\"force\":[");
            AppendHistoryArray(_hForce);
            _sb.Append("],\"steer\":[");
            AppendHistoryArray(_hSteer);
            _sb.Append("],\"speed\":[");
            AppendHistoryArray(_hSpeed);
            _sb.Append("],\"mz\":[");
            AppendHistoryArray(_hMz);
            _sb.Append("],\"fx\":[");
            AppendHistoryArray(_hFx);
            _sb.Append("],\"fy\":[");
            AppendHistoryArray(_hFy);
            _sb.Append("],\"gas\":[");
            AppendHistoryArray(_hGas);
            _sb.Append("],\"brake\":[");
            AppendHistoryArray(_hBrake);
            _sb.Append("]}");
            json = _sb.ToString();
        }
        var buf = Encoding.UTF8.GetBytes(json);
        await WriteResponse(stream, 200, "OK", "application/json", buf);
    }

    private void AppendHistoryArray(float[] arr)
    {
        if (_hCount == 0) { _sb.Append(']'); return; }
        int start = _hCount < MaxHistory ? 0 : _hIdx;
        for (int i = 0; i < _hCount; i++)
        {
            if (i > 0) _sb.Append(',');
            _sb.Append(arr[(start + i) % MaxHistory].ToString("F5"));
        }
        _sb.Append(']');
    }

    // ── OBS Overlay Options ──────────────────────────────────────────────

    public struct OverlayOptions
    {
        public string Theme { get; set; }  // "dark", "transparent", "compact", "clipping"
        public string Charts { get; set; } // "all", "none"
        public string Stats { get; set; }  // "all", "none"
        public float Opacity { get; set; }
        public bool ShowTrack { get; set; }
        public bool ShowWaveform { get; set; }
    }

    private static OverlayOptions ParseOptions(string query)
    {
        var opts = new OverlayOptions { Theme = "dark", Charts = "all", Stats = "all", Opacity = 1.0f, ShowTrack = true, ShowWaveform = true };
        if (string.IsNullOrEmpty(query)) return opts;

        var q = query.TrimStart('?');
        foreach (var part in q.Split('&'))
        {
            var kv = part.Split('=');
            if (kv.Length != 2) continue;
            switch (kv[0].ToLowerInvariant())
            {
                case "theme": opts.Theme = kv[1].ToLowerInvariant(); break;
                case "charts": opts.Charts = kv[1].ToLowerInvariant(); break;
                case "stats": opts.Stats = kv[1].ToLowerInvariant(); break;
                case "opacity": float.TryParse(kv[1], out var o); opts.Opacity = Math.Clamp(o, 0.1f, 1.0f); break;
                case "showtrack": opts.ShowTrack = kv[1] != "false"; break;
                case "showwaveform": opts.ShowWaveform = kv[1] != "false"; break;
            }
        }
        return opts;
    }

    // ── HTML Serving ─────────────────────────────────────────────────────

    private async Task ServeHtml(NetworkStream stream, string query)
    {
        var opts = ParseOptions(query);
        var html = Encoding.UTF8.GetBytes(GetHtml(opts));
        await WriteResponse(stream, 200, "OK", "text/html; charset=utf-8", html);
    }

    private static string GetHtml() => GetHtml(default);

    private static string GetHtml(OverlayOptions opts)
    {
        var isTransparent = opts.Theme == "transparent";
        var isCompact = opts.Theme == "compact";
        var isClipping = opts.Theme == "clipping";
        var showCharts = !isCompact && !isClipping && opts.Charts != "none";
        var showStats = !isClipping && opts.Stats != "none";
        var bodyClass = $"theme-{opts.Theme} charts-{opts.Charts} stats-{opts.Stats}";

        var extraCss = new StringBuilder();
        if (isTransparent)
        {
            extraCss.Append(@"
body.theme-transparent{background:transparent!important}
body.theme-transparent .header{background:rgba(17,17,17,0.5);backdrop-filter:blur(4px);-webkit-backdrop-filter:blur(4px)}
body.theme-transparent .pane{border-color:rgba(26,26,46,0.3)}
body.theme-transparent canvas{background:transparent!important}
body.theme-transparent #freezeOverlay.active{background:rgba(0,0,0,0.4)}
");
        }
        if (isCompact)
        {
            extraCss.Append(@"
body.theme-compact .pane{display:none}
body.theme-compact .header h1{display:none}
body.theme-compact .header{border-bottom:none;padding:3px 8px}
body.theme-compact .stats{font-size:10px}
body.theme-compact .live{gap:4px;padding:2px 8px}
body.theme-compact .live .g{min-width:40px}
body.theme-compact .live .g .vl{font-size:11px}
body.theme-compact .live .g .lb{font-size:8px}
");
        }
        if (isClipping)
        {
            extraCss.Append(@"
body.theme-clipping .header{display:none}
body.theme-clipping .live{display:none}
body.theme-clipping .row{display:none}
body.theme-clipping #freezeOverlay{display:none!important}
body.theme-clipping #frozenData{display:none!important}
#clippingIndicator{display:flex!important}
");
        }
        if (!showCharts)
        {
            extraCss.Append(@"
body.charts-none .pane,.charts-none .row{display:none}
");
        }
        var optsJson = JsonSerializer.Serialize(new
        {
            theme = opts.Theme,
            charts = opts.Charts,
            stats = opts.Stats,
            showStats,
            showCharts
        });

        return @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>FFB Live Telemetry</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:#0a0a0f;color:#ccc;font-family:'Consolas','Courier New',monospace;font-size:12px;overflow:hidden;height:100vh;display:flex;flex-direction:column}
.header{background:#111;padding:6px 12px;display:flex;align-items:center;gap:12px;border-bottom:1px solid #333;flex-shrink:0}
.header h1{font-size:14px;color:#4fc3f7}
.status{color:#4caf50;font-size:11px}
.status.err{color:#f44336}
.stats{display:flex;gap:16px;margin-left:auto;font-size:11px;color:#888}
.stats .v{color:#fff;font-weight:bold}
.row{display:flex;flex:1;min-height:0}
.col{flex:1;display:flex;flex-direction:column;min-width:0}
.pane{border:1px solid #1a1a2e;display:flex;flex-direction:column}
.pane .title{background:#111827;padding:2px 8px;font-size:10px;color:#607d8b;text-transform:uppercase;letter-spacing:1px;flex-shrink:0}
canvas{width:100%;flex:1;display:block}
.live{display:flex;gap:8px;padding:4px 12px;background:#0d1117;border-bottom:1px solid #1a1a2e;flex-shrink:0;flex-wrap:wrap}
.live .g{display:flex;flex-direction:column;align-items:center;min-width:55px}
.live .g .lb{font-size:9px;color:#607d8b}
.live .g .vl{font-size:14px;font-weight:bold;color:#fff}
.vl.red{color:#f44336}.vl.yel{color:#ffc107}.vl.grn{color:#4caf50}.vl.cyn{color:#4fc3f7}
.pedals{display:flex;gap:4px;align-items:flex-end}
.pedal{width:18px;height:50px;background:#1a1a2e;border-radius:3px;position:relative;overflow:hidden}
.pedal .fill{position:absolute;bottom:0;left:0;right:0;background:#4caf50;transition:height 40ms}
.pedal .fill.brake{background:#f44336}
#freezeOverlay{display:none;position:fixed;top:0;left:0;right:0;bottom:0;pointer-events:none;z-index:100}
#freezeOverlay.active{display:flex;flex-direction:column;align-items:center;justify-content:center;background:rgba(0,0,0,0.6)}
#freezeOverlay h2{font-size:48px;color:#f44336;text-shadow:0 0 20px #f44336;margin:0}
#freezeOverlay .ts{font-size:24px;color:#fff;margin-top:8px}
#freezeOverlay .hint{font-size:14px;color:#888;margin-top:16px}
#clippingIndicator{display:none;position:fixed;top:0;left:0;right:0;bottom:0;z-index:200;flex-direction:column;align-items:center;justify-content:center;background:rgba(0,0,0,0.7)}
#clippingIndicator .big{font-size:120px;font-weight:bold;margin:0}
#clippingIndicator .sub{font-size:20px;color:#888;margin-top:12px}
#frozenData{display:none;position:fixed;top:50px;left:0;right:0;bottom:0;z-index:99;overflow:auto;background:#0a0a0f;padding:16px;font-size:13px}
#frozenData.active{display:block}
#frozenData table{border-collapse:collapse;width:100%}
#frozenData td,#frozenData th{border:1px solid #333;padding:4px 10px;text-align:left;white-space:nowrap}
#frozenData th{background:#111;color:#607d8b;font-size:11px;text-transform:uppercase}
#frozenData td{color:#ccc}
#frozenData .val{color:#fff;font-family:monospace}
#frozenData .section{color:#4fc3f7;font-size:14px;font-weight:bold;padding:12px 0 6px 0}
#frozenData .warn{color:#f44336}
#frozenData .ok{color:#4caf50}
" + extraCss + @"
</style></head><body class=""" + bodyClass + @""">
<div id=""freezeOverlay""><h2>&#x23F8; FROZEN</h2><div class=""ts"" id=""freezeTs""></div><div class=""hint"">Press SPACE to resume</div></div>
<div id=""frozenData""></div>
<div id=""clippingIndicator""><div class=""big"" id=""clipText"">OK</div><div class=""sub"">FFB CLIPPING INDICATOR</div></div>
<div class=""header"">
  <h1>FFB Live Telemetry</h1>
  <span class=""status"" id=""conn"">Connecting...</span>
  <div class=""stats"">
    <span>Pkts:<span class=""v"" id=""pkts"">0</span></span>
    <span>FPS:<span class=""v"" id=""fps"">0</span></span>
    <span>Clip:<span class=""v"" id=""clip"">0</span></span>
  </div>
</div>
<div class=""live"">
  <div class=""g""><span class=""lb"">SPEED</span><span class=""vl cyn"" id=""vSpd"">0</span></div>
  <div class=""g""><span class=""lb"">FORCE</span><span class=""vl"" id=""vFo"">0.000</span></div>
  <div class=""g""><span class=""lb"">STEER</span><span class=""vl"" id=""vSt"">0.000</span></div>
  <div class=""g""><span class=""lb"">Mz Ch</span><span class=""vl grn"" id=""vMz"">0</span></div>
  <div class=""g""><span class=""lb"">Fx Ch</span><span class=""vl yel"" id=""vFx"">0</span></div>
  <div class=""g""><span class=""lb"">Fy Ch</span><span class=""vl"" id=""vFy"">0</span></div>
  <div class=""g""><span class=""lb"">LOAD L/R</span><span class=""vl"" id=""vWl"">0/0</span></div>
  <div class=""g""><span class=""lb"">SLIP L/R</span><span class=""vl"" id=""vSr"">0/0</span></div>
  <div class=""pedals"">
    <div class=""pedal""><div class=""fill"" id=""pGas""></div></div>
    <div class=""pedal""><div class=""fill brake"" id=""pBrk""></div></div>
  </div>
  <div class=""g""><span class=""lb"">GAS</span><span class=""vl grn"" id=""vGA"">0</span></div>
  <div class=""g""><span class=""lb"">BRAKE</span><span class=""vl red"" id=""vBR"">0</span></div>
  <div class=""g""><span class=""lb"">RAW Mz FL/FR</span><span class=""vl"" id=""vRMz"">0/0</span></div>
  <div class=""g""><span class=""lb"">RAW Fx FL/FR</span><span class=""vl"" id=""vRFx"">0/0</span></div>
  <div class=""g""><span class=""lb"">RAW Fy FL/FR</span><span class=""vl"" id=""vRFy"">0/0</span></div>
  <div class=""g""><span class=""lb"">OSC GUARD</span><span class=""vl"" id=""vOG"">OFF</span></div>
</div>
<div class=""row"">
  <div class=""col"">
    <div class=""pane""><div class=""title"">Force Output (white) + Steer x5 (yellow) + Speed/250 (cyan)</div><canvas id=""cMain""></canvas></div>
    <div class=""pane""><div class=""title"">Raw Tire: Mz FL/FR (green) Fx FL/FR /5000 (red) Fy FL/FR /5000 (blue)</div><canvas id=""cRaw""></canvas></div>
  </div>
  <div class=""col"">
    <div class=""pane""><div class=""title"">Channel Mixer: Mz(green) Fx(red) Fy(blue) Compress/2(white)</div><canvas id=""cChan""></canvas></div>
    <div class=""pane""><div class=""title"">Pipeline: Compress(white) LUT(purple) Slip(cyan) Damping(orange) Dynamic(pink)</div><canvas id=""cPipe""></canvas></div>
  </div>
</div>
<script>
        " + "\nconst OPTS=" + optsJson + ";\n" + @"
const N=600;
let D={fo:new Float32Array(N),st:new Float32Array(N),sp:new Float32Array(N),mz:new Float32Array(N),fx:new Float32Array(N),fy:new Float32Array(N),ga:new Float32Array(N),br:new Float32Array(N),cp:new Float32Array(N),sl:new Float32Array(N),dm:new Float32Array(N),dy:new Float32Array(N),lu:new Float32Array(N),rm0:new Float32Array(N),rm1:new Float32Array(N),rx0:new Float32Array(N),rx1:new Float32Array(N),ry0:new Float32Array(N),ry1:new Float32Array(N)};
let idx=0,pkts=0,clips=0,conn=false,fpsT=performance.now(),fpsC=0,dFps=0;
let frozen=false;
function push(d,v){d[idx%N]=v;}
function toggleFreeze(ts){
  frozen=!frozen;
  const ov=document.getElementById('freezeOverlay');
  const fd=document.getElementById('frozenData');
  if(frozen){
    ov.classList.add('active');
    document.getElementById('freezeTs').textContent=ts||new Date().toLocaleTimeString();
    document.title='FROZEN - FFB Live';
    fd.classList.add('active');
    buildFrozenTable();
  }else{
    ov.classList.remove('active');
    fd.classList.remove('active');
    document.title='FFB Live Telemetry';
  }
}
function buildFrozenTable(){
  const n=Math.min(idx,N);
  const si=idx>=N?idx%N:0;
  const last=(arr,cnt)=>{let r=[];for(let i=Math.max(0,cnt-50);i<cnt;i++)r.push(arr[(si+i)%N]);return r;};
  const stats=(arr)=>{
    let mn=Infinity,mx=-Infinity,sum=0,zc=0;
    let samples=last(arr,n);
    for(let i=0;i<samples.length;i++){let v=samples[i];if(v<mn)mn=v;if(v>mx)mx=v;sum+=Math.abs(v);if(i>0&&(samples[i-1]>=0)!==(v>=0))zc++;}
    return{min:mn,max:mx,avg:sum/samples.length,zc:zc,last:samples.length?samples[samples.length-1]:0};
  };
  const fo=stats(D.fo),st=stats(D.st),sp=stats(D.sp),mz=stats(D.mz),fx=stats(D.fx),fy=stats(D.fy);
  const cp=stats(D.cp),sl=stats(D.sl),dm=stats(D.dm),dy=stats(D.dy),lu=stats(D.lu);
  const rm0=stats(D.rm0),rm1=stats(D.rm1),rx0=stats(D.rx0),rx1=stats(D.rx1),ry0=stats(D.ry0),ry1=stats(D.ry1);
  const gaf=last(D.ga,n);
  const brf=last(D.br,n);
  const brk=brf.length?brf[brf.length-1]:0;
  const gas=gaf.length?gaf[gaf.length-1]:0;
  let h='<div class=""section"">CURRENT STATE</div><table>';
  h+='<tr><th></th><th>Value</th></tr>';
  h+=`<tr><td>Speed</td><td class=""val"">${sp.last.toFixed(1)} km/h</td></tr>`;
  h+=`<tr><td>Force Output</td><td class=""val ${Math.abs(fo.last)>0.9?&apos;warn&apos;:&apos;&apos;}"">${fo.last.toFixed(5)}</td></tr>`;
  h+=`<tr><td>Steer Angle</td><td class=""val"">${st.last.toFixed(4)}</td></tr>`;
  h+=`<tr><td>Gas</td><td class=""val"">${gas.toFixed(3)}</td></tr>`;
  h+=`<tr><td>Brake</td><td class=""val ${brk>0.01?&apos;warn&apos;:&apos;&apos;}"">${brk.toFixed(3)}</td></tr>`;
  h+='</table>';
  h+='<div class=""section"">FORCE OUTPUT STATS (last 50 frames)</div><table>';
  h+='<tr><th>Metric</th><th>Min</th><th>Max</th><th>Avg |F|</th><th>Zero-Cross</th><th>Status</th></tr>';
  const frow=(label,s,threshold)=>{
    const oscillating=s.zc>10;
    const clipping=Math.abs(s.max)>0.95;
    let status=oscillating?'<span class=""warn"">OSCILLATING</span>':'<span class=""ok"">OK</span>';
    if(clipping)status+='<br><span class=""warn"">CLIPPING</span>';
    return`<tr><td>${label}</td><td class=""val"">${s.min.toFixed(5)}</td><td class=""val"">${s.max.toFixed(5)}</td><td class=""val"">${s.avg.toFixed(5)}</td><td class=""val ${oscillating?&apos;warn&apos;:&apos;&apos;}"">${s.zc}</td><td>${status}</td></tr>`;
  };
  h+=frow('Force Out',fo);
  h+=frow('Steer Angle',st);
  h+=frow('Mz Channel',mz);
  h+=frow('Fx Channel',fx);
  h+=frow('Fy Channel',fy);
  h+=frow('Compress',cp);
  h+=frow('LUT',lu);
  h+=frow('Slip',sl);
  h+=frow('Damping',dm);
  h+=frow('Dynamic',dy);
  h+='</table>';
  h+='<div class=""section"">RAW TIRE FORCES (from shared memory)</div><table>';
  h+='<tr><th>Channel</th><th>Min</th><th>Max</th><th>Avg |F|</th><th>Zero-Cross</th></tr>';
  h+=`<tr><td>Mz FL</td><td class=""val"">${rm0.min.toFixed(4)}</td><td class=""val"">${rm0.max.toFixed(4)}</td><td class=""val"">${rm0.avg.toFixed(4)}</td><td>${rm0.zc}</td></tr>`;
  h+=`<tr><td>Mz FR</td><td class=""val"">${rm1.min.toFixed(4)}</td><td class=""val"">${rm1.max.toFixed(4)}</td><td class=""val"">${rm1.avg.toFixed(4)}</td><td>${rm1.zc}</td></tr>`;
  h+=`<tr><td>Fx FL</td><td class=""val"">${rx0.min.toFixed(2)}</td><td class=""val"">${rx0.max.toFixed(2)}</td><td class=""val"">${rx0.avg.toFixed(2)}</td><td>${rx0.zc}</td></tr>`;
  h+=`<tr><td>Fx FR</td><td class=""val"">${rx1.min.toFixed(2)}</td><td class=""val"">${rx1.max.toFixed(2)}</td><td class=""val"">${rx1.avg.toFixed(2)}</td><td>${rx1.zc}</td></tr>`;
  h+=`<tr><td>Fy FL</td><td class=""val"">${ry0.min.toFixed(2)}</td><td class=""val"">${ry0.max.toFixed(2)}</td><td class=""val"">${ry0.avg.toFixed(2)}</td><td>${ry0.zc}</td></tr>`;
  h+=`<tr><td>Fy FR</td><td class=""val"">${ry1.min.toFixed(2)}</td><td class=""val"">${ry1.max.toFixed(2)}</td><td class=""val"">${ry1.avg.toFixed(2)}</td><td>${ry1.zc}</td></tr>`;
  h+='</table>';
  h+='<div class=""section"">LAST 80 FRAMES RAW DATA</div>';
  h+='<table><tr><th>#</th><th>Speed</th><th>Steer</th><th>ForceOut</th><th>Mz</th><th>Fx</th><th>Fy</th><th>Compress</th><th>Gas</th><th>Brake</th></tr>';
  let cnt=Math.min(80,fof.length);
  for(let i=fof.length-cnt;i<fof.length;i++){
    const fi=(si+i)%N;
    const foAbs=Math.abs(fof[i]);
    const cls=foAbs>0.9?&apos;warn&apos;:&apos;&apos;;
    h+=`<tr><td>${i-(fof.length-cnt)}</td><td>${D.sp[fi].toFixed(1)}</td><td>${D.st[fi].toFixed(4)}</td><td class=""${cls}"">${D.fo[fi].toFixed(5)}</td><td>${D.mz[fi].toFixed(5)}</td><td>${D.fx[fi].toFixed(5)}</td><td>${D.fy[fi].toFixed(5)}</td><td>${D.cp[fi].toFixed(5)}</td><td>${D.ga[fi].toFixed(3)}</td><td>${D.br[fi].toFixed(3)}</td></tr>`;
  }
  h+='</table>';
  document.getElementById('frozenData').innerHTML=h;
}
const es=new EventSource('/stream');
es.onopen=()=>{conn=true;document.getElementById('conn').textContent='Connected';document.getElementById('conn').className='status';};
es.onerror=()=>{conn=false;document.getElementById('conn').textContent='Disconnected';document.getElementById('conn').className='status err';};
es.addEventListener('snapshot',(e)=>{const d=JSON.parse(e.data);toggleFreeze(d.ts);});
es.onmessage=(e)=>{
  if(frozen)return;
  const d=JSON.parse(e.data);
  if(d.force){for(let k in D){if(d[k])for(let i=0;i<d[k].length;i++)D[k][i]=d[k][i];}pkts=d.force.length;drawAll();return;}
  pkts++;fpsC++;clips+=d.cl||0;
  push(D.fo,d.fo||0);push(D.st,d.st||0);push(D.sp,d.sp||0);
  push(D.mz,d.mz||0);push(D.fx,d.fx||0);push(D.fy,d.fy||0);
  push(D.ga,d.ga||0);push(D.br,d.br||0);
  push(D.cp,d.cp||0);push(D.sl,d.sl||0);push(D.dm,d.dm||0);push(D.dy,d.dy||0);push(D.lu,d.lu||0);
  push(D.rm0,d.rmz0||0);push(D.rm1,d.rmz1||0);
  push(D.rx0,d.rfx0||0);push(D.rx1,d.rfx1||0);
  push(D.ry0,d.rfy0||0);push(D.ry1,d.rfy1||0);
  idx++;
  const $=id=>document.getElementById(id);

  // Clipping indicator mode
  if(OPTS.theme==='clipping'){
    const ct=$('clipText');
    if(ct){
      const isClip=d.cl>0;
      ct.textContent=isClip?'CLIPPING':'OK';
      ct.style.color=isClip?'#f44336':'#4caf50';
      ct.style.textShadow=isClip?'0 0 40px #f44336':'0 0 40px #4caf50';
    }
    return;
  }

  // Stats updates
  if(OPTS.showStats){
    $('vSpd').textContent=(d.sp||0).toFixed(0);
    $('vFo').textContent=(d.fo||0).toFixed(4);
    $('vFo').style.color=Math.abs(d.fo||0)>0.9?'#f44336':'#fff';
    $('vSt').textContent=(d.st||0).toFixed(4);
    $('vMz').textContent=(d.mz||0).toFixed(5);
    $('vFx').textContent=(d.fx||0).toFixed(5);
    $('vFy').textContent=(d.fy||0).toFixed(5);
    $('vWl').textContent=(d.wl0||0).toFixed(0)+'/'+(d.wl1||0).toFixed(0);
    $('vSr').textContent=(d.sr0||0).toFixed(3)+'/'+(d.sr1||0).toFixed(3);
    $('vGA').textContent=(d.ga||0).toFixed(2);
    $('vBR').textContent=(d.br||0).toFixed(2);
    $('vRMz').textContent=(d.rmz0||0).toFixed(2)+'/'+(d.rmz1||0).toFixed(2);
    $('vRFx').textContent=(d.rfx0||0).toFixed(0)+'/'+(d.rfx1||0).toFixed(0);
    $('vRFy').textContent=(d.rfy0||0).toFixed(0)+'/'+(d.rfy1||0).toFixed(0);
    const ogEl=$('vOG');
    if(d.og!==undefined){
      ogEl.textContent=d.og?'OSCILLATING':(d.osf!==undefined?(d.osf<0.95?'CALMING':'STABLE'):'OFF');
      ogEl.style.color=d.og?'#f44336':(d.osf<0.95?'#ffc107':'#4caf50');
      document.body.style.borderTop=d.og?'3px solid #f44336':'';
      document.body.style.boxShadow=d.og?'0 0 20px #f44336 inset':'';
    } else { ogEl.textContent='OFF'; ogEl.style.color='#888'; }
    $('pGas').style.height=((d.ga||0)*100)+'%';
    $('pBrk').style.height=((d.br||0)*100)+'%';
  }
  $('pkts').textContent=pkts;
  const now=performance.now();
  if(now-fpsT>1000){dFps=fpsC;fpsC=0;fpsT=now;$('fps').textContent=dFps;$('clip').textContent=clips;clips=0;}
  if(OPTS.showCharts) drawAll();
};
function drawAll(){
  dc('cMain',[{d:D.fo,c:'#fff',w:1.5},{d:D.st,c:'#ffc107',w:1,s:5},{d:D.sp,c:'#4fc3f7',w:1,div:250}]);
  dc('cRaw',[{d:D.rm0,c:'#4caf50',w:1},{d:D.rm1,c:'#2e7d32',w:1},{d:D.rx0,c:'#f44336',w:1,div:5000},{d:D.rx1,c:'#b71c1c',w:1,div:5000},{d:D.ry0,c:'#2196f3',w:1,div:5000},{d:D.ry1,c:'#0d47a1',w:1,div:5000}]);
  dc('cChan',[{d:D.mz,c:'#4caf50',w:1.5},{d:D.fx,c:'#f44336',w:1.5},{d:D.fy,c:'#2196f3',w:1.5},{d:D.cp,c:'#fff',w:1,div:2}]);
  dc('cPipe',[{d:D.cp,c:'#fff',w:1},{d:D.lu,c:'#ce93d8',w:1},{d:D.sl,c:'#4fc3f7',w:1},{d:D.dm,c:'#ff9800',w:1},{d:D.dy,c:'#e91e63',w:1}]);
}
function dc(id,trs){
  const c=document.getElementById(id);
  if(!c)return;
  const w=c.clientWidth,h=c.clientHeight;
  if(c.width!==w*devicePixelRatio||c.height!==h*devicePixelRatio){c.width=w*devicePixelRatio;c.height=h*devicePixelRatio;}
  const x=c.getContext('2d');x.setTransform(devicePixelRatio,0,0,devicePixelRatio,0,0);
  if(OPTS.theme==='transparent'){x.clearRect(0,0,w,h);}else{x.fillStyle='#0a0a0f';x.fillRect(0,0,w,h);}
  x.strokeStyle='#1a1a2e';x.lineWidth=0.5;x.beginPath();x.moveTo(0,h/2);x.lineTo(w,h/2);x.stroke();
  x.strokeStyle='#111';x.setLineDash([2,4]);x.beginPath();x.moveTo(0,h*.25);x.lineTo(w,h*.25);x.moveTo(0,h*.75);x.lineTo(w,h*.75);x.stroke();x.setLineDash([]);
  const n=Math.min(idx,N);if(n<2)return;
  const si=idx>=N?idx%N:0;
  for(const t of trs){
    x.strokeStyle=t.c;x.lineWidth=t.w||1;x.beginPath();
    for(let i=0;i<n;i++){
      let v=t.d[(si+i)%N];if(t.s)v*=t.s;if(t.div)v/=t.div;
      const px=i/(n-1)*w,py=h/2-v*(h*.45);
      i===0?x.moveTo(px,py):x.lineTo(px,py);
    }
    x.stroke();
  }
}
addEventListener('resize',drawAll);
addEventListener('keydown',(e)=>{if(e.code==='Space'){e.preventDefault();toggleFreeze();}});
</script></body></html>";
    }

    private async Task ServeOverlay(NetworkStream stream, string query)
    {
        var opts = ParseOptions(query);
        var html = Encoding.UTF8.GetBytes(GetOverlayHtml(opts));
        await WriteResponse(stream, 200, "OK", "text/html; charset=utf-8", html);
    }

    private static string GetOverlayHtml(OverlayOptions opts)
    {
        var opacity = opts.Opacity.ToString("F2");
        return @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>FFB Overlay</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:transparent;color:#fff;font-family:'Inter','Segoe UI',Arial,sans-serif;overflow:hidden;height:100vh;width:100vw;display:flex;align-items:center;justify-content:center}
#p{background:rgba(0,0,0,0.45);backdrop-filter:blur(6px);-webkit-backdrop-filter:blur(6px);border:1px solid rgba(255,255,255,0.06);border-radius:clamp(12px,1.2vw,24px);padding:clamp(12px,2vw,32px);width:96vw;max-width:1400px;display:flex;flex-direction:column;gap:clamp(8px,1.2vh,20px);opacity:" + opacity + @"}
#tr{display:flex;flex-wrap:wrap;align-items:center;gap:clamp(8px,1.5vw,24px)}
#sg{display:flex;align-items:baseline;gap:clamp(6px,0.8vw,14px);flex-shrink:0}
#sp{font-size:clamp(24px,6vw,96px);font-weight:700;line-height:1;letter-spacing:-0.03em}
#sp .u{font-size:clamp(10px,1.5vw,28px);font-weight:400;color:rgba(255,255,255,0.4);margin-left:clamp(2px,0.3vw,8px)}
#ge{display:inline-flex;align-items:center;justify-content:center;min-width:clamp(26px,2.8vw,56px);height:clamp(26px,2.8vw,56px);border-radius:clamp(6px,0.6vw,12px);font-size:clamp(15px,2.5vw,48px);font-weight:700;background:rgba(255,255,255,0.08);color:#fff}
#rs{flex:1;min-width:clamp(60px,10vw,300px);display:flex;flex-direction:column;gap:clamp(2px,0.2vh,4px)}
#rl{font-size:clamp(7px,0.65vw,13px);color:rgba(255,255,255,0.35);text-transform:uppercase;letter-spacing:0.12em}
#rb{width:100%;height:clamp(4px,0.6vh,12px);background:rgba(255,255,255,0.06);border-radius:clamp(2px,0.3vw,6px);overflow:hidden}
#rf{height:100%;border-radius:clamp(2px,0.3vw,6px);transition:width 40ms ease;background:linear-gradient(90deg,#22c55e,#eab308 45%,#ef4444 75%,#dc2626)}
#lc{display:flex;flex-direction:column;align-items:flex-end;flex-shrink:0}
#ll{font-size:clamp(7px,0.65vw,13px);color:rgba(255,255,255,0.35);text-transform:uppercase;letter-spacing:0.12em}
#lv{font-size:clamp(12px,1.4vw,26px);font-weight:600}
#fa{display:flex;flex-direction:column;gap:clamp(4px,0.5vh,10px)}
#fv{font-size:clamp(14px,2.5vw,44px);font-weight:400;letter-spacing:-0.01em}
#fv .n{font-size:clamp(9px,1vw,18px);font-weight:400;color:rgba(255,255,255,0.35);margin-left:clamp(4px,0.4vw,10px)}
#fbw{width:100%;height:clamp(8px,1.2vh,24px);background:rgba(255,255,255,0.05);border-radius:clamp(4px,0.5vw,10px);overflow:hidden;position:relative}
#fb{height:100%;border-radius:clamp(4px,0.5vw,10px);transition:width 40ms ease;position:absolute;top:0}
#fb.p{right:50%;background:linear-gradient(90deg,#0ea5e9,#22d3ee)}
#fb.n{left:50%;background:linear-gradient(270deg,#f97316,#fb923c)}
#cp{display:none;font-size:clamp(10px,1vw,20px);font-weight:700;color:#ef4444;text-shadow:0 0 clamp(6px,0.8vw,16px) rgba(239,68,68,0.5);animation:p 0.7s ease-in-out infinite}
@keyframes p{0%,100%{opacity:1}50%{opacity:0.4}}
#ch{display:flex;flex-wrap:wrap;gap:clamp(10px,1.8vw,36px);align-items:center}
.cg{display:flex;flex-direction:column;gap:0}
.cl{font-size:clamp(7px,0.6vw,12px);color:rgba(255,255,255,0.35);text-transform:uppercase;letter-spacing:0.1em}
.cv{font-size:clamp(10px,1.1vw,22px);font-weight:600}
.cv.mz{color:#fbbf24}.cv.fx{color:#f87171}.cv.fy{color:#34d399}.cv.ga{color:#fff}.cv.br{color:#f87171}
#btr{display:flex;flex-wrap:wrap;gap:clamp(8px,1vw,20px);align-items:flex-end;min-height:0;flex:1}
#ww{flex:1;min-width:clamp(60px,20vw,400px);height:clamp(24px,4vh,70px);display:" + (opts.ShowWaveform ? "block" : "none") + @"}
#ww canvas{width:100%;height:100%;border-radius:clamp(4px,0.4vw,10px);background:rgba(0,0,0,0.15);display:block}
#tw{width:clamp(50px,7vw,140px);height:clamp(50px,7vw,140px);flex-shrink:0;display:" + (opts.ShowTrack ? "block" : "none") + @"}
#tw canvas{width:100%;height:100%;border-radius:clamp(6px,0.6vw,12px);background:rgba(0,0,0,0.2);display:block}
</style></head><body>
<div id='p'>
<div id='tr'>
<div id='sg'><div id='sp'>0<span class='u'>km/h</span></div><div id='ge'>N</div></div>
<div id='rs'><div id='rl'>RPM</div><div id='rb'><div id='rf' style='width:0%'></div></div></div>
<div id='lc'><div id='ll'>LAP</div><div id='lv'>0</div></div>
</div>
<div id='fa'>
<div id='fv'>0.000<span class='n'>Nm</span></div>
<div id='fbw'><div id='fb' class='p' style='width:0%'></div></div>
<div id='cp'>CLIPPING</div>
<div id='ch'>
<div class='cg'><span class='cl'>Mz</span><span class='cv mz' id='vMz'>0.000</span></div>
<div class='cg'><span class='cl'>Fx</span><span class='cv fx' id='vFx'>0.000</span></div>
<div class='cg'><span class='cl'>Fy</span><span class='cv fy' id='vFy'>0.000</span></div>
<div class='cg'><span class='cl'>Gas</span><span class='cv ga' id='vGa'>0</span></div>
<div class='cg'><span class='cl'>Brk</span><span class='cv br' id='vBr'>0</span></div>
</div>
</div>
<div id='btr'>
<div id='ww'><canvas id='cWave'></canvas></div>
<div id='tw'><canvas id='cTrack'></canvas></div>
</div>
</div>
<script>
const N=300;
let D={fo:new Float32Array(N),st:new Float32Array(N),sp:new Float32Array(N)};
let idx=0;
const es=new EventSource('/stream');
es.onmessage=(e)=>{
const d=JSON.parse(e.data);
if(d.force){for(let k in D)if(d[k])for(let i=0;i<d[k].length;i++)D[k][i]=d[k][i];return;}
const $=id=>document.getElementById(id);
(idx>=N?null:null);
if(idx<N){D.fo[idx]=d.fo||0;D.st[idx]=d.st||0;D.sp[idx]=d.sp||0;}
else{D.fo[idx%N]=d.fo||0;D.st[idx%N]=d.st||0;D.sp[idx%N]=d.sp||0;}
idx++;
const sp=d.sp||0;
$('sp').innerHTML=sp.toFixed(0)+'<span class=""u"">km/h</span>';
const gr=d.ge;
const ge=$('ge');
if(gr===0){ge.textContent='R';ge.style.background='rgba(239,68,68,0.25)';}
else if(gr<=1){ge.textContent='N';ge.style.background='rgba(255,255,255,0.06)';}
else{ge.textContent=gr;ge.style.background='rgba(255,255,255,0.08)';}
const rp=(d.rp||0);
$('rf').style.width=Math.min(rp*100,100)+'%';
const fo=Math.abs(d.fo||0);
$('fv').innerHTML=fo.toFixed(3)+'<span class=""n"">Nm</span>';
const barPct=Math.min(fo*100,100);
const fb=$('fb');
fb.style.width=barPct+'%';
fb.className=(d.fo||0)>=0?'p':'n';
const clip=d.cl||0;
const ce=$('cp');
ce.style.display=clip?'block':'none';
$('vMz').textContent=(d.mz||0).toFixed(3);
$('vFx').textContent=(d.fx||0).toFixed(3);
$('vFy').textContent=(d.fy||0).toFixed(3);
$('vGa').textContent=(d.ga||0).toFixed(2);
$('vBr').textContent=(d.br||0).toFixed(2);
$('lv').textContent=d.la||0;
drawWave();
drawTrack(d);
};
window.addEventListener('resize',()=>{drawWave();});
function drawWave(){
const c=document.getElementById('cWave');
if(!c)return;
const w=c.clientWidth,h=c.clientHeight;
if(c.width!==w||c.height!==h){c.width=w;c.height=h;}
const x=c.getContext('2d');
x.clearRect(0,0,w,h);
const n=Math.min(idx,N);if(n<2)return;
const si=idx>=N?idx%N:0;
const mid=h/2;
x.strokeStyle='rgba(255,255,255,0.05)';x.lineWidth=1;x.setLineDash([3,6]);
x.beginPath();x.moveTo(0,mid);x.lineTo(w,mid);x.stroke();x.setLineDash([]);
x.strokeStyle='#22d3ee';x.lineWidth=Math.max(1,h*0.1);
x.beginPath();
for(let i=0;i<n;i++){
let v=D.fo[(si+i)%N]||0;
const px=i/(n-1)*w,py=mid-v*mid*0.75;
i===0?x.moveTo(px,py):x.lineTo(px,py);
}
x.stroke();
}
function drawTrack(d){
const c=document.getElementById('cTrack');
if(!c||d.np===undefined)return;
const w=c.clientWidth,h=c.clientHeight;
if(c.width!==w||c.height!==h){c.width=w;c.height=h;}
const x=c.getContext('2d');
x.clearRect(0,0,w,h);
const cx=w/2,cy=h/2,r=Math.min(w,h)*0.35;
x.beginPath();x.arc(cx,cy,r,0,Math.PI*2);
x.strokeStyle='rgba(255,255,255,0.1)';x.lineWidth=1.5;x.stroke();
const np=d.np||0;
const angle=np*Math.PI*2-Math.PI/2;
const px=cx+Math.cos(angle)*r;
const py=cy+Math.sin(angle)*r;
x.beginPath();x.arc(px,py,Math.max(3,w*0.04),0,Math.PI*2);
x.fillStyle='rgba(34,211,238,0.8)';x.fill();
x.beginPath();x.arc(px,py,Math.max(1.5,w*0.018),0,Math.PI*2);
x.fillStyle='#fff';x.fill();
}
</script></body></html>";
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }

    private sealed class SseClient
    {
        public TcpClient TcpClient { get; }
        public NetworkStream Stream { get; }
        public ConcurrentQueue<byte[]> Queue { get; } = new();
        public ManualResetEventSlim Signal { get; } = new(false);

        public SseClient(TcpClient tcpClient)
        {
            TcpClient = tcpClient;
            Stream = tcpClient.GetStream();
        }
    }
}
