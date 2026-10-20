using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AcEvoFfbTuner.Core.Config;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using AcEvoFfbTuner.Core.PedalHaptics;

namespace AcEvoFfbTuner.Core;

public sealed class FfbLiveServer : IDisposable
{
    private TcpListener? _tcpListener;
    private CancellationTokenSource _cts = new();
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

    /// <summary>Optional: set by TelemetryLoop to expose pedal state via API.</summary>
    public PedalInput.PedalInputManager? PedalInputManager { get; set; }

    /// <summary>Optional: set by TelemetryLoop to read raw wheelbase axes.</summary>
    public DirectInput.FfbDeviceManager? FfbDeviceManager { get; set; }

    /// <summary>Latest pedal haptic signal values, updated by TelemetryLoop each tick.</summary>
    public volatile float LiveAbsModulation;
    public volatile float LiveScrubModulation;
    public volatile float LiveRearSlipModulation;
    public volatile float LiveRoadForceModulation;
    public volatile float LiveOfftrackModulation;
    public volatile float LiveTcRumble;
    public volatile float LiveBrakePressure;
    public volatile float LiveClutchPosition;

    /// <summary>Display steering angle in degrees (per-game convention), set by MainViewModel each tick.</summary>
    public volatile float LiveSteerDegrees;

    /// <summary>Active game key ("evo", "r3e", "lmu", "acc", or empty), set by TelemetryLoop on connect.</summary>
    public volatile string LiveGame = "";

    /// <summary>Pedal haptic routing gains, settable via API from PedalTest GUI.</summary>
    public PedalHapticRouteConfig HapticRouteConfig { get; set; } = new();

    public FfbLiveServer(int port = 8321)
    {
        Port = port;
    }

    public void Start()
    {
        if (_tcpListener != null) return;
        _cts = new CancellationTokenSource();
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

    public static string? GetActiveNetworkAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var ipProps = ni.GetIPProperties();
                if (ipProps.GatewayAddresses.Count == 0) continue;
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }
        }
        catch { }
        return null;
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
            _sb.Append(",\"gapA\":");
            _sb.Append(raw.GapAhead.ToString("F3"));
            _sb.Append(",\"gapB\":");
            _sb.Append(raw.GapBehind.ToString("F3"));
            _sb.Append(",\"pos\":");
            _sb.Append(raw.RacePosition.ToString());
            _sb.Append(",\"tot\":");
            _sb.Append(raw.TotalDrivers.ToString());
            _sb.Append(",\"latG\":");
            _sb.Append(raw.AccG.Length > 0 ? raw.AccG[0].ToString("F3") : "0");
            _sb.Append(",\"lonG\":");
            _sb.Append(raw.AccG.Length > 1 ? raw.AccG[1].ToString("F3") : "0");

            // Display G (lateral X / longitudinal Z) for the G-meter overlay.
            // Per-game sources: EVO/R3E AccG is already in g; LMU AccG is raw m/s².
            // R3E/LMU may report real accel in DisplayAccG (m/s²) when AccG is a zeroed placeholder.
            float gx = raw.AccG.Length > 0 ? raw.AccG[0] : 0f;
            float gz = raw.AccG.Length > 2 ? raw.AccG[2] : 0f;
            bool gFromFallback = MathF.Abs(gx) < 0.01f && MathF.Abs(gz) < 0.01f && raw.DisplayAccG.Length > 2;
            if (gFromFallback)
            {
                gx = raw.DisplayAccG[0];
                gz = raw.DisplayAccG[2];
            }
            if (LiveGame == "lmu" || (gFromFallback && LiveGame == "r3e"))
            {
                gx /= 9.80665f;
                gz /= 9.80665f;
            }
            _sb.Append(",\"gx\":");
            _sb.Append(gx.ToString("F3"));
            _sb.Append(",\"gy\":");
            _sb.Append(gz.ToString("F3"));
            _sb.Append(",\"stD\":");
            _sb.Append(LiveSteerDegrees.ToString("F1"));
            _sb.Append(",\"tt0\":");
            _sb.Append(raw.TyreTemp[0].ToString("F1"));
            _sb.Append(",\"tt1\":");
            _sb.Append(raw.TyreTemp[1].ToString("F1"));
            _sb.Append(",\"tt2\":");
            _sb.Append(raw.TyreTemp[2].ToString("F1"));
            _sb.Append(",\"tt3\":");
            _sb.Append(raw.TyreTemp[3].ToString("F1"));
            _sb.Append(",\"tp0\":");
            _sb.Append(raw.WheelsPressure[0].ToString("F1"));
            _sb.Append(",\"tp1\":");
            _sb.Append(raw.WheelsPressure[1].ToString("F1"));
            _sb.Append(",\"tp2\":");
            _sb.Append(raw.WheelsPressure[2].ToString("F1"));
            _sb.Append(",\"tp3\":");
            _sb.Append(raw.WheelsPressure[3].ToString("F1"));
            _sb.Append(",\"tg0\":");
            _sb.Append(raw.TyreGrip[0].ToString("F3"));
            _sb.Append(",\"tg1\":");
            _sb.Append(raw.TyreGrip[1].ToString("F3"));
            _sb.Append(",\"tg2\":");
            _sb.Append(raw.TyreGrip[2].ToString("F3"));
            _sb.Append(",\"tg3\":");
            _sb.Append(raw.TyreGrip[3].ToString("F3"));
            _sb.Append(",\"tca\":");
            _sb.Append(raw.TcActiveGfx ? "1" : "0");
            _sb.Append(",\"aba\":");
            _sb.Append(raw.AbsActiveGfx ? "1" : "0");
            _sb.Append(",\"fl\":");
            _sb.Append(raw.Flag.ToString());
            _sb.Append(",\"pit\":");
            _sb.Append(raw.IsPitLimiterOn ? "1" : "0");
            _sb.Append(",\"lim\":");
            _sb.Append(raw.IsRpmLimiterOn ? "1" : "0");
            _sb.Append(",\"up\":");
            _sb.Append(raw.IsChangeUpRpm ? "1" : "0");
            _sb.Append(",\"bp0\":");
            _sb.Append(raw.BrakePressure.Length > 0 ? raw.BrakePressure[0].ToString("F0") : "0");
            _sb.Append(",\"bp1\":");
            _sb.Append(raw.BrakePressure.Length > 1 ? raw.BrakePressure[1].ToString("F0") : "0");
            _sb.Append(",\"bp2\":");
            _sb.Append(raw.BrakePressure.Length > 2 ? raw.BrakePressure[2].ToString("F0") : "0");
            _sb.Append(",\"bp3\":");
            _sb.Append(raw.BrakePressure.Length > 3 ? raw.BrakePressure[3].ToString("F0") : "0");
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
            catch (SocketException) { continue; }
        }
    }

    private async void HandleClient(TcpClient tcpClient, CancellationToken ct)
    {
        try
        {
            var stream = tcpClient.GetStream();
            var (method, path, query) = await ReadHttpRequest(stream, ct);
            if (string.IsNullOrEmpty(method) || method != "GET") return;

            if (path == "/stream")
            {
                var sseClient = new SseClient(tcpClient);
                _clients.Add(sseClient);
                await SseWriter(sseClient, ct);
            }
            else if (path == "/ping")
            {
                var body = Encoding.UTF8.GetBytes("pong");
                await WriteResponse(stream, 200, "OK", "text/plain", body);
            }
            else if (path == "/history")
            {
                await ServeHistory(stream);
            }
            else if (path == "/api/pedal-status")
            {
                LogApi($"GET /api/pedal-status from {tcpClient.Client.RemoteEndPoint}");
                await ServePedalApi(stream);
            }
            else if (path == "/api/pedal-select")
            {
                LogApi($"GET /api/pedal-select?{query}");
                await ServePedalSelect(stream, query);
            }
            else if (path == "/api/pedal-mapping")
            {
                LogApi($"GET /api/pedal-mapping?{query}");
                await ServePedalMapping(stream, query);
            }
            else if (path == "/api/pedal-haptic")
            {
                LogApi($"GET /api/pedal-haptic?{query}");
                await ServePedalHaptic(stream, query);
            }
            else if (path == "/overlay")
            {
                await ServeOverlay(stream, query);
            }
            else if (path == "/gt" || path == "/dash")
            {
                await ServeOverlay(stream, CombineQuery(query, "layout=gt"));
            }
            else if (path == "/tower" || path == "/relative")
            {
                await ServeOverlay(stream, CombineQuery(query, "layout=tower"));
            }
            else if (path == "/inputs" || path == "/pedals")
            {
                await ServeOverlay(stream, CombineQuery(query, "layout=inputs"));
            }
            else if (path == "/tires")
            {
                await ServeOverlay(stream, CombineQuery(query, "layout=tires"));
            }
            else if (path == "/ffb")
            {
                await ServeOverlay(stream, CombineQuery(query, "layout=ffb"));
            }
            else if (path == "/minimal" || path == "/shift")
            {
                await ServeOverlay(stream, CombineQuery(query, "layout=minimal"));
            }
            else
            {
                await ServeHtml(stream, query);
            }
        }
        catch (Exception ex)
        {
            try
            {
                var err = Encoding.UTF8.GetBytes("500 Internal Error: " + ex.Message);
                var header = $"HTTP/1.1 500 Internal Server Error\r\nContent-Type: text/plain\r\nContent-Length: {err.Length}\r\nConnection: close\r\n\r\n";
                var headerBytes = Encoding.UTF8.GetBytes(header);
                await tcpClient.GetStream().WriteAsync(headerBytes, 0, headerBytes.Length);
                await tcpClient.GetStream().WriteAsync(err, 0, err.Length);
            }
            catch { }
        }
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw; // rethrow to HandleClient for 500 response
        }
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
        if (_hCount == 0) return; // caller emits the closing bracket
        int start = _hCount < MaxHistory ? 0 : _hIdx;
        for (int i = 0; i < _hCount; i++)
        {
            if (i > 0) _sb.Append(',');
            _sb.Append(arr[(start + i) % MaxHistory].ToString("F5"));
        }
    }

    // ── Pedal API ─────────────────────────────────────────────────────────

    private void LogApi(string msg)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        System.Diagnostics.Debug.WriteLine($"[LIVEAPI] {msg}");
        // Also write to diagnostic log if available
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "pedal_api.log");
            File.AppendAllText(logPath, $"{ts} {msg}\n");
        }
        catch { }
    }

    private async Task ServePedalApi(NetworkStream stream)
    {
        if (PedalInputManager == null)
        {
            LogApi("  → no pedal input manager");
            var err = Encoding.UTF8.GetBytes("{\"error\":\"no pedal input manager\"}");
            await WriteResponse(stream, 200, "OK", "application/json", err);
            return;
        }

        var sb = new StringBuilder(1024);
        sb.Append("{\"pedalInputEnabled\":");
        sb.Append(PedalConfigManager.Instance.Config.Enabled ? "true" : "false");
        sb.Append(",\"hasPedalInput\":");
        sb.Append(PedalInputManager.TryGetState(out var state) ? "true" : "false");

        if (state.Source != 0)
        {
            sb.Append(",\"source\":\"");
            sb.Append(state.Source.ToString());
            sb.Append("\",\"gasInput\":");
            sb.Append(state.GasInput.ToString("F4"));
            sb.Append(",\"brakeInput\":");
            sb.Append(state.BrakeInput.ToString("F4"));
            sb.Append(",\"clutchInput\":");
            sb.Append(state.ClutchInput.ToString("F4"));
        }

        // Device info from DI source
        PedalInput.Sources.DirectInputPedalSource? diSource = null;
        foreach (var src in PedalInputManager.Sources)
        {
            if (src is PedalInput.Sources.DirectInputPedalSource di)
                diSource = di;
        }

        sb.Append(",\"devices\":[");
        if (diSource != null)
        {
            bool first = true;
            foreach (var dev in diSource.Devices)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"index\":");
                sb.Append(dev.Index);
                sb.Append(",\"name\":\"");
                sb.Append(        EscapeJson(dev.ProductName));
                sb.Append("\",\"axisCount\":");
                sb.Append(dev.AxisCount);
                sb.Append(",\"isFfbCapable\":");
                sb.Append(dev.IsFfbCapable ? "true" : "false");
                sb.Append('}');
            }
        }
        sb.Append("],\"activeDeviceName\":\"");
        sb.Append(        EscapeJson(diSource?.DeviceName ?? ""));
        sb.Append("\",\"diAvailable\":");
        sb.Append(diSource?.IsAvailable == true ? "true" : "false");

        // Current axis values from active DI device
        if (diSource != null)
        {
            var axes = diSource.ReadAllDeviceAxes();
            sb.Append(",\"axisSnapshots\":{");
            bool first = true;
            foreach (var kv in axes)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"');
                sb.Append(        EscapeJson(kv.Key));
                sb.Append("\":{\"x\":");
                sb.Append(kv.Value.X.ToString("F3"));
                sb.Append(",\"y\":");
                sb.Append(kv.Value.Y.ToString("F3"));
                sb.Append(",\"z\":");
                sb.Append(kv.Value.Z.ToString("F3"));
                sb.Append(",\"rx\":");
                sb.Append(kv.Value.RotationX.ToString("F3"));
                sb.Append(",\"ry\":");
                sb.Append(kv.Value.RotationY.ToString("F3"));
                sb.Append(",\"rz\":");
                sb.Append(kv.Value.RotationZ.ToString("F3"));
                sb.Append(",\"sl0\":");
                sb.Append(kv.Value.Sliders.Length > 0 ? kv.Value.Sliders[0].ToString("F3") : "null");
                sb.Append(",\"sl1\":");
                sb.Append(kv.Value.Sliders.Length > 1 ? kv.Value.Sliders[1].ToString("F3") : "null");
                sb.Append(",\"axisCount\":");
                sb.Append(kv.Value.AxisCount);
                sb.Append(",\"isFfb\":");
                sb.Append(kv.Value.IsFfbCapable ? "true" : "false");
                sb.Append('}');
            }
            sb.Append('}');

            // Axis mapping
            sb.Append(",\"mapping\":{\"gasAxis\":\"");
            sb.Append(diSource.Mapping.GasAxis);
            sb.Append("\",\"brakeAxis\":\"");
            sb.Append(diSource.Mapping.BrakeAxis);
            sb.Append("\",\"clutchAxis\":\"");
            sb.Append(diSource.Mapping.ClutchAxis);
            sb.Append("\",\"gasInvert\":");
            sb.Append(diSource.Mapping.GasInvert ? "true" : "false");
            sb.Append(",\"brakeInvert\":");
            sb.Append(diSource.Mapping.BrakeInvert ? "true" : "false");
            sb.Append(",\"clutchInvert\":");
            sb.Append(diSource.Mapping.ClutchInvert ? "true" : "false");
            sb.Append('}');
        }

        // Available axis names
        sb.Append(",\"availableAxes\":[");
        var axesArr = PedalInput.Sources.DirectInputPedalSource.AxisMap.AvailableAxes;
        for (int i = 0; i < axesArr.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"');
            sb.Append(axesArr[i]);
            sb.Append('"');
        }
        sb.Append(']');

        // Calibration from global config
        var cfg = PedalConfigManager.Instance.Config;
        sb.Append(",\"calibration\":{");
        sb.Append("\"gasDeadzone\":").Append(cfg.Gas.Deadzone.ToString("F3"));
        sb.Append(",\"brakeDeadzone\":").Append(cfg.Brake.Deadzone.ToString("F3"));
        sb.Append(",\"gasMin\":").Append(cfg.Gas.Min.ToString("F3"));
        sb.Append(",\"gasMax\":").Append(cfg.Gas.Max.ToString("F3"));
        sb.Append(",\"brakeMin\":").Append(cfg.Brake.Min.ToString("F3"));
        sb.Append(",\"brakeMax\":").Append(cfg.Brake.Max.ToString("F3"));
        sb.Append(",\"gasInvert\":").Append(cfg.Gas.Invert ? "true" : "false");
        sb.Append(",\"brakeInvert\":").Append(cfg.Brake.Invert ? "true" : "false");
        sb.Append(",\"gasSmoothing\":").Append(cfg.Gas.Smoothing.ToString("F2"));
        sb.Append(",\"brakeSmoothing\":").Append(cfg.Brake.Smoothing.ToString("F2"));
        sb.Append('}');

        // Wheelbase RAW axis snapshot (from FfbDeviceManaged — always works).
        // Returns unmapped X/Y/Z/Rx/Ry/Rz so the client can apply its own mapping.
        foreach (var src in PedalInputManager.Sources)
        {
            if (src is PedalInput.Sources.FfbDevicePedalSource fbSrc)
            {
                var snapshot = FfbDeviceManager?.ReadAllAxes();
                if (snapshot != null)
                {
                    sb.Append(",\"wheelbaseAxes\":{\"x\":");
                    sb.Append(snapshot.X.ToString("F3"));
                    sb.Append(",\"y\":");
                    sb.Append(snapshot.Y.ToString("F3"));
                    sb.Append(",\"z\":");
                    sb.Append(snapshot.Z.ToString("F3"));
                    sb.Append(",\"rx\":");
                    sb.Append(snapshot.RotationX.ToString("F3"));
                    sb.Append(",\"ry\":");
                    sb.Append(snapshot.RotationY.ToString("F3"));
                    sb.Append(",\"rz\":");
                    sb.Append(snapshot.RotationZ.ToString("F3"));
                    sb.Append(",\"sl0\":");
                    sb.Append(snapshot.Sliders.Length > 0 ? snapshot.Sliders[0].ToString("F3") : "null");
                    sb.Append(",\"sl1\":");
                    sb.Append(snapshot.Sliders.Length > 1 ? snapshot.Sliders[1].ToString("F3") : "null");
                    sb.Append("}");
                    LogApi("  → wheelbaseAxes OK");
                }
                else
                {
                    LogApi("  → wheelbaseAxes: ReadAllAxes returned null");
                }

                // Also report FfbDevicePedalSource mapping
                sb.Append(",\"wheelbaseMapping\":{\"gasAxis\":\"");
                sb.Append(fbSrc.Mapping.GasAxis);
                sb.Append("\",\"brakeAxis\":\"");
                sb.Append(fbSrc.Mapping.BrakeAxis);
                sb.Append("\",\"clutchAxis\":\"");
                sb.Append(fbSrc.Mapping.ClutchAxis);
                sb.Append("\",\"gasInvert\":");
                sb.Append(fbSrc.Mapping.GasInvert ? "true" : "false");
                sb.Append(",\"brakeInvert\":");
                sb.Append(fbSrc.Mapping.BrakeInvert ? "true" : "false");
                sb.Append(",\"clutchInvert\":");
                sb.Append(fbSrc.Mapping.ClutchInvert ? "true" : "false");
                sb.Append('}');

                break;
            }
        }

        // Simulated pedal haptics (from FfbVibrationMixer live signals)
        sb.Append(",\"pedalHaptics\":{");
        sb.Append("\"absModulation\":").Append(LiveAbsModulation.ToString("F4"));
        sb.Append(",\"scrubModulation\":").Append(LiveScrubModulation.ToString("F4"));
        sb.Append(",\"rearSlipModulation\":").Append(LiveRearSlipModulation.ToString("F4"));
        sb.Append(",\"roadForceModulation\":").Append(LiveRoadForceModulation.ToString("F4"));
        sb.Append(",\"curbModulation\":").Append(LiveOfftrackModulation.ToString("F4"));
        sb.Append(",\"tcRumble\":").Append(LiveTcRumble.ToString("F4"));
        sb.Append(",\"brakePressure\":").Append(LiveBrakePressure.ToString("F4"));
        sb.Append(",\"clutchPosition\":").Append(LiveClutchPosition.ToString("F4"));

        // Routed haptic values (raw × per-signal gain × master gain)
        var rh = HapticRouteConfig;
        float GetRouteGain(string signal)
        {
            foreach (var r in rh.Routes)
                if (r.Signal == signal) return r.Gain;
            return 1f;
        }
        float routedAbs = LiveAbsModulation * GetRouteGain("abs") * rh.BrakeHapticGain;
        float routedTc = LiveTcRumble * GetRouteGain("tc") * rh.GasHapticGain;
        float routedCurb = LiveOfftrackModulation * GetRouteGain("curb") * Math.Max(rh.BrakeHapticGain, rh.GasHapticGain);
        float routedRoad = LiveRoadForceModulation * GetRouteGain("road") * rh.BrakeHapticGain;
        float routedScrub = LiveScrubModulation * GetRouteGain("scrub") * rh.GasHapticGain;
        float routedBrakePress = LiveBrakePressure * rh.BrakeHapticGain;
        float routedClutch = LiveClutchPosition * GetRouteGain("clutchpos") * rh.ClutchHapticGain;

        sb.Append(",\"routedAbs\":").Append(routedAbs.ToString("F4"));
        sb.Append(",\"routedTc\":").Append(routedTc.ToString("F4"));
        sb.Append(",\"routedCurb\":").Append(routedCurb.ToString("F4"));
        sb.Append(",\"routedRoad\":").Append(routedRoad.ToString("F4"));
        sb.Append(",\"routedScrub\":").Append(routedScrub.ToString("F4"));
        sb.Append(",\"routedBrakePressure\":").Append(routedBrakePress.ToString("F4"));
        sb.Append(",\"routedClutch\":").Append(routedClutch.ToString("F4"));

        // Haptic routing gains (settable via /api/pedal-haptic)
        sb.Append(",\"routing\":{");
        sb.Append("\"brakeHapticGain\":").Append(rh.BrakeHapticGain.ToString("F2"));
        sb.Append(",\"gasHapticGain\":").Append(rh.GasHapticGain.ToString("F2"));
        sb.Append(",\"clutchHapticGain\":").Append(rh.ClutchHapticGain.ToString("F2"));
        sb.Append(",\"routes\":[");
        for (int i = 0; i < rh.Routes.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var r = rh.Routes[i];
            sb.Append("{\"signal\":\"").Append(r.Signal);
            sb.Append("\",\"targetPedal\":\"").Append(r.TargetPedal);
            sb.Append("\",\"gain\":").Append(r.Gain.ToString("F2"));
            sb.Append(",\"mode\":\"").Append(r.Mode).Append("\"}");
        }
        sb.Append(']');
        sb.Append('}');

        sb.Append('}');

        sb.Append('}');

        var json = sb.ToString();
        var snippet = json.Length > 120 ? json[..120] : json;
        LogApi($"  → {json.Length} bytes: {snippet}...");

        var buf = Encoding.UTF8.GetBytes(json);
        try { System.Text.Json.JsonDocument.Parse(json); }
        catch (Exception jex)
        {
            try { File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "pedal_json_error.log"),
                $"{DateTime.Now:HH:mm:ss.fff} JSON ERROR: {jex.Message}\nFull JSON ({json.Length} chars):\n{json}\n\n");
            } catch { }
        }
        await WriteResponse(stream, 200, "OK", "application/json", buf);
    }

    private async Task ServePedalSelect(NetworkStream stream, string query)
    {
        var body = Encoding.UTF8.GetBytes("ok");

        if (PedalInputManager == null)
        {
            body = Encoding.UTF8.GetBytes("no pedal manager");
            await WriteResponse(stream, 200, "OK", "text/plain", body);
            return;
        }

        // Parse ?index=X from query
        var qParts = query.TrimStart('?').Split('&');
        foreach (var part in qParts)
        {
            var kv = part.Split('=');
            if (kv.Length == 2 && kv[0] == "index" && int.TryParse(kv[1], out var idx))
            {
                // Find the DI source and set its active device
                foreach (var src in PedalInputManager.Sources)
                {
                    if (src is PedalInput.Sources.DirectInputPedalSource di)
                    {
                        di.ActiveDeviceIndex = idx - 1; // -1 = auto
                        body = Encoding.UTF8.GetBytes($"selected={idx}");
                        break;
                    }
                }
                break;
            }
        }

        await WriteResponse(stream, 200, "OK", "text/plain", body);
    }

    private async Task ServePedalMapping(NetworkStream stream, string query)
    {
        var body = Encoding.UTF8.GetBytes("ok");
        if (PedalInputManager == null)
        {
            body = Encoding.UTF8.GetBytes("no pedal manager");
            await WriteResponse(stream, 200, "OK", "text/plain", body);
            return;
        }

        foreach (var src in PedalInputManager.Sources)
        {
            if (src is PedalInput.Sources.FfbDevicePedalSource fbSrc)
            {
                var parts = query.TrimStart('?').Split('&');
                foreach (var p in parts)
                {
                    var kv = p.Split('=');
                    if (kv.Length != 2) continue;
                    switch (kv[0])
                    {
                        case "gasAxis": fbSrc.Mapping.GasAxis = Uri.UnescapeDataString(kv[1]); break;
                        case "brakeAxis": fbSrc.Mapping.BrakeAxis = Uri.UnescapeDataString(kv[1]); break;
                        case "clutchAxis": fbSrc.Mapping.ClutchAxis = Uri.UnescapeDataString(kv[1]); break;
                        case "gasInvert": fbSrc.Mapping.GasInvert = kv[1] == "true"; break;
                        case "brakeInvert": fbSrc.Mapping.BrakeInvert = kv[1] == "true"; break;
                        case "clutchInvert": fbSrc.Mapping.ClutchInvert = kv[1] == "true"; break;
                    }
                }
                body = Encoding.UTF8.GetBytes($"mapping updated: {fbSrc.Mapping.GasAxis}→Gas {fbSrc.Mapping.BrakeAxis}→Brake {fbSrc.Mapping.ClutchAxis}→Clutch");
                break;
            }
        }

        await WriteResponse(stream, 200, "OK", "text/plain", body);
    }

    private async Task ServePedalHaptic(NetworkStream stream, string query)
    {
        var parts = query.TrimStart('?').Split('&');
        var rh = HapticRouteConfig;
        foreach (var p in parts)
        {
            var kv = p.Split('=');
            if (kv.Length != 2) continue;
            switch (kv[0])
            {
                case "brakeGain": rh.BrakeHapticGain = float.TryParse(kv[1], out var bg) ? bg : 1f; break;
                case "gasGain": rh.GasHapticGain = float.TryParse(kv[1], out var gg) ? gg : 1f; break;
                case "clutchGain": rh.ClutchHapticGain = float.TryParse(kv[1], out var cg) ? cg : 1f; break;
                case "routeIdx": _hapticRouteIdx = int.TryParse(kv[1], out var ri) ? ri : -1; break;
                case "signal": if (_hapticRouteIdx >= 0 && _hapticRouteIdx < rh.Routes.Count) rh.Routes[_hapticRouteIdx].Signal = Uri.UnescapeDataString(kv[1]); break;
                case "targetPedal": if (_hapticRouteIdx >= 0 && _hapticRouteIdx < rh.Routes.Count) rh.Routes[_hapticRouteIdx].TargetPedal = Uri.UnescapeDataString(kv[1]); break;
                case "gain": if (_hapticRouteIdx >= 0 && _hapticRouteIdx < rh.Routes.Count && float.TryParse(kv[1], out var gn)) rh.Routes[_hapticRouteIdx].Gain = gn; break;
                case "mode": if (_hapticRouteIdx >= 0 && _hapticRouteIdx < rh.Routes.Count) rh.Routes[_hapticRouteIdx].Mode = Uri.UnescapeDataString(kv[1]); break;
            }
        }
        var body = Encoding.UTF8.GetBytes($"haptic routing updated: brakeGain={rh.BrakeHapticGain:F2} gasGain={rh.GasHapticGain:F2} clutchGain={rh.ClutchHapticGain:F2}");
        await WriteResponse(stream, 200, "OK", "text/plain", body);
    }
    private int _hapticRouteIdx = -1;

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
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
        public string Mods { get; set; }   // comma list: speed,force,waveform,track,pedals,tires,gforce (empty = all)
        public string Layout { get; set; } // "gt" (default), "tower", "inputs", "tires", "ffb", "minimal"
    }

    private static OverlayOptions ParseOptions(string query)
    {
        var opts = new OverlayOptions { Theme = "dark", Charts = "all", Stats = "all", Opacity = 1.0f, ShowTrack = true, ShowWaveform = true, Mods = "", Layout = "gt" };
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
                case "mods": opts.Mods = kv[1].ToLowerInvariant(); break;
                case "layout": opts.Layout = kv[1].ToLowerInvariant(); break;
            }
        }
        return opts;
    }

    // ── HTML Serving ─────────────────────────────────────────────────────

    private static string CombineQuery(string query, string extra)
    {
        if (string.IsNullOrEmpty(query)) return "?" + extra;
        var q = query.TrimStart('?');
        // Don't duplicate layout if caller already set one
        if (q.Contains("layout=", StringComparison.OrdinalIgnoreCase)) return query;
        return "?" + q + (q.Length > 0 ? "&" : "") + extra;
    }

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
body{background:#0a0a0f;color:#ccc;font-family:'Rajdhani','Bahnschrift','Segoe UI','Consolas',monospace;font-size:12px;overflow:hidden;height:100vh;display:flex;flex-direction:column}
.header{background:linear-gradient(180deg,#171a1f,#0e1013);padding:6px 12px;display:flex;align-items:center;gap:12px;border-bottom:2px solid #e10600;flex-shrink:0}
.header h1{font-size:15px;color:#f2f5f7;letter-spacing:0.12em;font-weight:700}
.header h1 .ace{color:#e10600}
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
  <h1><span class=""ace"">ACE</span> · FFB Live Telemetry</h1>
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
  const fof=last(D.fo,n);
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
        return opts.Layout switch
        {
            "tower" => GetTowerHtml(opts),
            "inputs" => GetInputsHtml(opts),
            "tires" => GetTiresHtml(opts),
            "ffb" => GetFfbHtml(opts),
            "minimal" => GetMinimalHtml(opts),
            _ => GetGtHtml(opts),
        };
    }

    // ── Shared GT bits ──────────────────────────────────────────────────
    // Bosch DDU / BMW M4 GT3 inspired: carbon weave (pure CSS, no external
    // images so OBS stays sharp at any canvas size and works offline),
    // top RPM LED bar, central gear, flag banner, shift flash.

    private static string GtLedBar(int count, string prefix)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
            sb.Append("<div class='led' id='").Append(prefix).Append(i).Append("'></div>");
        return sb.ToString();
    }

    private const string GtFontStack = "'Rajdhani','Barlow Condensed','Bahnschrift','Arial Narrow','Segoe UI',Arial,sans-serif";

    private static string GetGtHtml(OverlayOptions opts)
    {
        var opacity = opts.Opacity.ToString("F2");

        var mods = string.IsNullOrEmpty(opts.Mods)
            ? new[] { "speed", "force", "waveform", "track", "pedals", "tires", "gforce" }
            : opts.Mods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool showSpeed = mods.Contains("speed");
        bool showForce = mods.Contains("force");
        bool showWaveform = mods.Contains("waveform") && opts.ShowWaveform;
        bool showTrack = mods.Contains("track") && opts.ShowTrack;
        bool showPedals = mods.Contains("pedals");
        bool showTires = mods.Contains("tires");
        bool showGforce = mods.Contains("gforce");
        string Vis(bool on) => on ? "" : "display:none";

        return @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>ACE GT Dash</title>
<link href='https://fonts.googleapis.com/css2?family=Rajdhani:wght@500;600;700&display=swap' rel='stylesheet'>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:transparent;color:#f2f5f7;font-family:" + GtFontStack + @";overflow:hidden;height:100vh;width:100vw;display:flex;align-items:center;justify-content:center}
body.theme-transparent #p{background:rgba(8,10,12,0.55)}
#p{position:relative;width:96vw;max-width:1400px;opacity:" + opacity + @";border-radius:clamp(10px,1vw,20px);padding:clamp(10px,1.4vw,24px) clamp(12px,1.8vw,30px) clamp(10px,1.2vh,18px);display:flex;flex-direction:column;gap:clamp(6px,0.9vh,14px);
background:linear-gradient(160deg,rgba(24,27,31,0.96),rgba(10,11,13,0.96)),repeating-linear-gradient(45deg,rgba(255,255,255,0.022) 0 2px,transparent 2px 4px),repeating-linear-gradient(-45deg,rgba(255,255,255,0.022) 0 2px,transparent 2px 4px);
background-blend-mode:normal;border:1px solid rgba(255,255,255,0.09);box-shadow:0 18px 60px rgba(0,0,0,0.55),inset 0 1px 0 rgba(255,255,255,0.08);overflow:hidden}
#p::before{content:'';position:absolute;inset:0 0 auto 0;height:3px;background:linear-gradient(90deg,#e10600,#ff6a00,#ffd400,#00e676,#00b0ff);opacity:0.9}
body.shift #p{animation:shiftflash 0.45s steps(2) infinite;border-color:rgba(255,30,30,0.8)}
@keyframes shiftflash{50%{box-shadow:0 0 0 3px rgba(255,30,30,0.9),0 0 44px rgba(255,30,30,0.55)}}
#flagbanner{display:none;align-items:center;justify-content:center;gap:10px;border-radius:8px;padding:clamp(4px,0.6vh,8px);font-weight:700;letter-spacing:0.22em;font-size:clamp(11px,1.3vw,24px);text-transform:uppercase}
#flagbanner.show{display:flex}
#flagbanner.yellow{background:repeating-linear-gradient(45deg,#facc15 0 18px,#111 18px 36px);color:#fff;text-shadow:0 2px 6px #000}
#flagbanner.blue{background:#1565c0;color:#fff}
#flagbanner.black{background:#000;color:#fff;border:1px solid #555}
#flagbanner.white{background:#e8e8e8;color:#111}
#flagbanner.checked{background:repeating-conic-gradient(#fff 0 25%,#111 0 50%) 0 0/22px 22px;color:#ffd400;text-shadow:0 2px 4px #000}
#flagbanner.red{background:#b71c1c;color:#fff}
#rpmbar{display:flex;gap:clamp(3px,0.35vw,7px);justify-content:center;padding:clamp(2px,0.3vh,5px) clamp(4px,0.5vw,10px);background:rgba(0,0,0,0.5);border:1px solid rgba(255,255,255,0.07);border-radius:9px}
.led{flex:1;height:clamp(8px,1.5vh,22px);max-width:64px;border-radius:4px;background:rgba(255,255,255,0.08);box-shadow:inset 0 1px 2px rgba(0,0,0,0.7)}
.led.on.g{background:#00e676;box-shadow:0 0 12px #00e676}
.led.on.r{background:#ff1744;box-shadow:0 0 12px #ff1744}
.led.on.b{background:#40c4ff;box-shadow:0 0 14px #40c4ff;animation:ledblink 0.3s steps(2) infinite}
@keyframes ledblink{50%{opacity:0.45}}
#dash{display:grid;grid-template-columns:1fr auto 1fr;align-items:center;gap:clamp(8px,1.4vw,26px)}
#speedbox{display:flex;flex-direction:column;line-height:1}
#sp{font-size:clamp(30px,5.2vw,84px);font-weight:700;letter-spacing:-0.02em;font-variant-numeric:tabular-nums}
#sp .u{font-size:clamp(10px,1.1vw,20px);font-weight:500;color:rgba(255,255,255,0.42);margin-left:6px;letter-spacing:0.14em}
#rpmrow{display:flex;align-items:center;gap:8px;margin-top:4px}
#rpbar{flex:1;height:clamp(5px,0.8vh,10px);background:rgba(255,255,255,0.07);border-radius:5px;overflow:hidden}
#rpfill{height:100%;width:0%;background:linear-gradient(90deg,#00e676,#ffea00 55%,#ff1744 80%);transition:width 50ms linear}
#rptxt{font-size:clamp(10px,1vw,18px);font-weight:600;color:rgba(255,255,255,0.6);font-variant-numeric:tabular-nums}
#gearbox{display:flex;flex-direction:column;align-items:center;line-height:1}
#ge{font-size:clamp(52px,8.5vw,150px);font-weight:700;text-shadow:0 4px 24px rgba(0,0,0,0.7);font-variant-numeric:tabular-nums}
#ge.shift{color:#ff1744;animation:gearblink 0.4s steps(2) infinite}
@keyframes gearblink{50%{opacity:0.5}}
#glb{font-size:clamp(8px,0.7vw,13px);letter-spacing:0.34em;color:rgba(255,255,255,0.4);font-weight:600}
#racebox{display:flex;flex-direction:column;align-items:flex-end;gap:clamp(2px,0.4vh,6px)}
#pos{font-size:clamp(20px,2.6vw,46px);font-weight:700;font-variant-numeric:tabular-nums}
#pos small{font-size:0.5em;color:rgba(255,255,255,0.45);font-weight:600}
#lap{font-size:clamp(11px,1.2vw,20px);font-weight:600;color:rgba(255,255,255,0.75);font-variant-numeric:tabular-nums}
#gaps{display:flex;gap:12px;font-size:clamp(10px,1.05vw,18px);font-weight:600;font-variant-numeric:tabular-nums}
#gA{color:#00e676}#gB{color:#ff8a65}
#chips{display:flex;gap:6px}
.chip{display:none;align-items:center;padding:2px 10px;border-radius:6px;font-size:clamp(9px,0.85vw,15px);font-weight:700;letter-spacing:0.1em}
.chip.tc{background:rgba(234,179,8,0.18);color:#ffd400;border:1px solid rgba(234,179,8,0.5)}
.chip.abs{background:rgba(239,68,68,0.18);color:#ff5252;border:1px solid rgba(239,68,68,0.5)}
.chip.pit{background:rgba(59,130,246,0.18);color:#64b5f6;border:1px solid rgba(59,130,246,0.5)}
.chip.lim{background:rgba(255,23,68,0.2);color:#ff1744;border:1px solid #ff1744;animation:ledblink 0.4s steps(2) infinite}
#ffbsec{background:rgba(0,0,0,0.42);border:1px solid rgba(255,255,255,0.07);border-radius:9px;padding:clamp(6px,0.8vh,10px) clamp(8px,1vw,16px)}
#ffbtop{display:flex;align-items:baseline;gap:12px}
#ffv{font-size:clamp(18px,2.4vw,40px);font-weight:700;font-variant-numeric:tabular-nums}
#ffv .n{font-size:0.45em;font-weight:500;color:rgba(255,255,255,0.4);margin-left:6px}
#cp{display:none;margin-left:auto;font-weight:700;color:#ff1744;font-size:clamp(11px,1.2vw,22px);letter-spacing:0.14em;animation:ledblink 0.7s steps(2) infinite}
#ffbarw{position:relative;height:clamp(9px,1.4vh,20px);background:rgba(255,255,255,0.06);border-radius:7px;overflow:hidden;margin-top:6px}
#ffbar{position:absolute;top:0;bottom:0;left:50%;width:0%;background:linear-gradient(90deg,#00b0ff,#00e676);border-radius:7px;transition:width 40ms linear}
#ffbar.neg{background:linear-gradient(270deg,#ff9100,#ff3d00)}
#ffbar.clip{background:#ff1744!important;box-shadow:0 0 16px #ff1744}
#chrow{display:flex;flex-wrap:wrap;gap:clamp(8px,1.4vw,26px);margin-top:6px}
.cg{display:flex;flex-direction:column;min-width:52px}
.cl{font-size:clamp(8px,0.65vw,12px);color:rgba(255,255,255,0.38);letter-spacing:0.16em;font-weight:600}
.cv{font-size:clamp(12px,1.25vw,22px);font-weight:700;font-variant-numeric:tabular-nums}
.cv.mz{color:#ffd54f}.cv.fx{color:#ff8a80}.cv.fy{color:#69f0ae}.cv.pb{color:#90caf9}.cv.gl{color:#ce93d8}
#btr{display:flex;gap:clamp(8px,1vw,18px);align-items:stretch}
#ww{flex:1;min-height:clamp(30px,5vh,76px);background:rgba(0,0,0,0.35);border:1px solid rgba(255,255,255,0.06);border-radius:9px;overflow:hidden}
#ww canvas{width:100%;height:100%;display:block}
#tw{width:clamp(64px,8vw,150px);background:rgba(0,0,0,0.35);border:1px solid rgba(255,255,255,0.06);border-radius:9px}
#tw canvas{width:100%;height:100%;display:block}
#mr{display:flex;flex-wrap:wrap;gap:clamp(12px,1.8vw,34px);align-items:stretch;justify-content:space-between}
#md-pedals{display:flex;gap:clamp(10px,1.2vw,22px);align-items:flex-end}
.pedal-box{display:flex;flex-direction:column;align-items:center;gap:4px}
.pedal-track{width:clamp(20px,2.2vw,42px);height:clamp(52px,7vh,120px);background:rgba(255,255,255,0.06);border:1px solid rgba(255,255,255,0.08);border-radius:7px;position:relative;overflow:hidden}
.pedal-fill{position:absolute;bottom:0;left:0;right:0;transition:height 40ms linear}
.pedal-fill.gas{background:linear-gradient(0deg,#00897b,#00e676)}
.pedal-fill.brake{background:linear-gradient(0deg,#b71c1c,#ff5252)}
.pedal-num{font-size:clamp(9px,0.9vw,15px);font-weight:700;font-variant-numeric:tabular-nums}
.pedal-lb{font-size:clamp(8px,0.65vw,12px);color:rgba(255,255,255,0.38);letter-spacing:0.18em;font-weight:600}
#cWheel{width:clamp(72px,7.5vw,140px);height:clamp(72px,7.5vw,140px);display:block}
#md-tires{display:flex;flex-direction:column;gap:5px}
#tires-row{display:flex;gap:clamp(8px,1vw,20px)}
.tire{display:flex;flex-direction:column;align-items:center;gap:3px;background:rgba(0,0,0,0.35);border:1px solid rgba(255,255,255,0.07);border-radius:8px;padding:clamp(4px,0.5vh,8px) clamp(6px,0.7vw,12px)}
.tire-bar{width:clamp(22px,2.4vw,44px);height:clamp(52px,7vh,118px);background:rgba(255,255,255,0.06);border-radius:6px;position:relative;overflow:hidden}
.tire-fill{position:absolute;bottom:0;left:0;right:0;transition:height 120ms linear}
.tire-lb{font-size:clamp(8px,0.7vw,13px);color:rgba(255,255,255,0.45);font-weight:700;letter-spacing:0.12em}
.tire-t{font-size:clamp(11px,1.1vw,19px);font-weight:700;font-variant-numeric:tabular-nums}
.tire-p{font-size:clamp(8px,0.75vw,13px);color:rgba(255,255,255,0.5);font-variant-numeric:tabular-nums}
#md-gforce{display:flex;flex-direction:column;align-items:center;gap:3px;background:rgba(0,0,0,0.35);border:1px solid rgba(255,255,255,0.07);border-radius:8px;padding:clamp(4px,0.5vh,8px) clamp(8px,0.9vw,14px)}
#cG{width:clamp(92px,9.5vw,180px);height:clamp(92px,9.5vw,180px);display:block}
#gvals{display:flex;gap:12px;font-size:clamp(9px,0.9vw,15px);font-weight:700;font-variant-numeric:tabular-nums}
#gvals .lat{color:#69f0ae}#gvals .lon{color:#ffd54f}
.g-lb{font-size:clamp(8px,0.65vw,12px);color:rgba(255,255,255,0.38);letter-spacing:0.2em;font-weight:600}
.sec-lb{font-size:clamp(8px,0.65vw,12px);color:rgba(255,255,255,0.38);letter-spacing:0.2em;font-weight:600}
</style></head><body class='theme-" + opts.Theme + @"'>
<div id='p'>
<div id='flagbanner'></div>
<div id='rpmbar'>" + GtLedBar(15, "led") + @"</div>
<div id='md-speed' style='" + Vis(showSpeed) + @"'>
<div id='dash'>
<div id='speedbox'><div id='sp'>0<span class='u'>KM/H</span></div>
<div id='rpmrow'><div id='rpbar'><div id='rpfill'></div></div><div id='rptxt'>0%</div></div></div>
<div id='gearbox'><div id='ge'>N</div><div id='glb'>GEAR</div></div>
<div id='racebox'><div id='pos'>P0<small>/0</small></div><div id='lap'>LAP 0</div>
<div id='gaps'><span id='gA'>+0.000</span><span id='gB'>+0.000</span></div>
<div id='chips'><div class='chip tc' id='chTc'>TC</div><div class='chip abs' id='chAbs'>ABS</div><div class='chip pit' id='chPit'>PIT</div><div class='chip lim' id='chLim'>LIMIT</div></div></div>
</div>
</div>
<div id='md-force' style='" + Vis(showForce) + @"'>
<div id='ffbsec'>
<div id='ffbtop'><span class='sec-lb'>FFB</span><span id='ffv'>0.000<span class='n'>Nm</span></span><span id='cp'>CLIPPING</span></div>
<div id='ffbarw'><div id='ffbar'></div></div>
<div id='chrow'>
<div class='cg'><span class='cl'>Mz</span><span class='cv mz' id='vMz'>0.000</span></div>
<div class='cg'><span class='cl'>Fx</span><span class='cv fx' id='vFx'>0.000</span></div>
<div class='cg'><span class='cl'>Fy</span><span class='cv fy' id='vFy'>0.000</span></div>
<div class='cg'><span class='cl'>THR</span><span class='cv pb' id='vGa'>0%</span></div>
<div class='cg'><span class='cl'>BRK</span><span class='cv fx' id='vBr'>0%</span></div>
<div class='cg'><span class='cl'>LAT-G</span><span class='cv gl' id='vLt'>0.00</span></div>
<div class='cg'><span class='cl'>LON-G</span><span class='cv mz' id='vLn'>0.00</span></div>
</div>
</div>
</div>
<div id='btr'>
<div id='md-waveform' style='" + Vis(showWaveform) + @"'><div id='ww' style='flex:1;display:flex;min-height:inherit'><canvas id='cWave'></canvas></div></div>
<div id='md-track' style='" + Vis(showTrack) + @"'><div id='tw'><canvas id='cTrack'></canvas></div></div>
</div>
<div id='mr'>
<div id='md-pedals' style='" + Vis(showPedals) + @"'>
<div class='pedal-box'><div class='pedal-track'><div class='pedal-fill gas' id='pfG'></div></div><div class='pedal-num' id='pfGn'>0</div><div class='pedal-lb'>THR</div></div>
<div class='pedal-box'><div class='pedal-track'><div class='pedal-fill brake' id='pfB'></div></div><div class='pedal-num' id='pfBn'>0</div><div class='pedal-lb'>BRK</div></div>
<div class='pedal-box'><canvas id='cWheel'></canvas><div class='pedal-num' id='stNum'>0&deg;</div><div class='pedal-lb'>STEER</div></div>
</div>
<div id='md-tires' style='" + Vis(showTires) + @"'>
<span class='sec-lb'>TYRES &deg;C / kPa / GRIP</span>
<div id='tires-row'>
<div class='tire'><div class='tire-lb'>FL</div><div class='tire-bar'><div class='tire-fill' id='tf0'></div></div><div class='tire-t' id='tt0'>--</div><div class='tire-p' id='tp0'>--</div></div>
<div class='tire'><div class='tire-lb'>FR</div><div class='tire-bar'><div class='tire-fill' id='tf1'></div></div><div class='tire-t' id='tt1'>--</div><div class='tire-p' id='tp1'>--</div></div>
<div class='tire'><div class='tire-lb'>RL</div><div class='tire-bar'><div class='tire-fill' id='tf2'></div></div><div class='tire-t' id='tt2'>--</div><div class='tire-p' id='tp2'>--</div></div>
<div class='tire'><div class='tire-lb'>RR</div><div class='tire-bar'><div class='tire-fill' id='tf3'></div></div><div class='tire-t' id='tt3'>--</div><div class='tire-p' id='tp3'>--</div></div>
</div>
</div>
<div id='md-gforce' style='" + Vis(showGforce) + @"'>
<canvas id='cG'></canvas>
<div id='gvals'><span class='lat' id='gLat'>LAT 0.00</span><span class='lon' id='gLon'>LON 0.00</span></div>
<div class='g-lb'>G-FORCE</div>
</div>
</div>
</div>
<script>
const N=300;
let D={fo:new Float32Array(N)};
let idx=0;
const $=id=>document.getElementById(id);
const es=new EventSource('/stream');
es.onmessage=(e)=>{
const d=JSON.parse(e.data);
if(d.force)return;
idx++;
const rp=d.rp||0;
const rpf=$('rpfill');if(rpf)rpf.style.width=Math.min(rp*100,100)+'%';
const rpt=$('rptxt');if(rpt)rpt.textContent=Math.round(rp*100)+'%';
const lit=Math.round(Math.min(Math.max(rp,0),1)*15);
for(let i=0;i<15;i++){
const l=$('led'+i);if(!l)continue;
l.className='led'+(i<lit?(' on '+(i<5?'g':(i<10?'r':'b'))):'');
}
const shift=rp>0.95||d.lim===1||d.up===1;
document.body.classList.toggle('shift',!!shift);
const sp=d.sp||0;
const spe=$('sp');if(spe)spe.innerHTML=sp.toFixed(0)+'<span class=""u"">KM/H</span>';
const gr=d.ge,ge=$('ge');
if(ge){
if(gr===0||gr===1){ge.textContent=gr===0?'R':'N';ge.classList.remove('shift');ge.style.background='';}
else{ge.textContent=gr;ge.classList.toggle('shift',!!shift);}
}
const pos=$('pos');if(pos)pos.innerHTML='P'+(d.pos||0)+'<small>/'+(d.tot||0)+'</small>';
const lap=$('lv');if(lap)lap.textContent=d.la||0;
const lap2=$('lap');if(lap2)lap2.textContent='LAP '+(d.la||0);
const gA=$('gA');if(gA)gA.textContent='+'+(d.gapA||0).toFixed(3);
const gB=$('gB');if(gB)gB.textContent='+'+(d.gapB||0).toFixed(3);
setChips(d);
const fo=d.fo||0;
const ffv=$('fv');if(ffv)ffv.innerHTML=Math.abs(fo).toFixed(3)+'<span class=""n"">Nm</span>';
const ff2=$('ffv');if(ff2)ff2.innerHTML=Math.abs(fo).toFixed(3)+'<span class=""n"">Nm</span>';
const fb=$('fb');
if(fb){const pct=Math.min(Math.abs(fo)*100,100);fb.style.width='0%';}
const ffb=$('ffbar');
if(ffb){
const pct=Math.min(Math.abs(fo)*50,50);
ffb.style.left=fo>=0?'50%':(50-pct)+'%';
ffb.style.width=pct+'%';
ffb.className=fo<0?'neg':'';
if(d.cl)ffb.classList.add('clip');
}
const ce=$('cp');if(ce)ce.style.display=d.cl?'block':'none';
setT('vMz',(d.mz||0).toFixed(3));setT('vFx',(d.fx||0).toFixed(3));setT('vFy',(d.fy||0).toFixed(3));
setT('vGa',Math.round((d.ga||0)*100)+'%');setT('vBr',Math.round((d.br||0)*100)+'%');
setT('vLt',(d.latG||0).toFixed(2));setT('vLn',(d.lonG||0).toFixed(2));
setT('lv',d.la||0);
D.fo[idx%N]=fo;
updatePedals(d);updateTires(d);drawG(d);drawWave();drawTrack(d);
};
function setT(id,txt){const e=$(id);if(e)e.textContent=txt;}
function setChips(d){
const tc=$('chTc');if(tc)tc.style.display=d.tca?'flex':'none';
const ab=$('chAbs');if(ab)ab.style.display=d.aba?'flex':'none';
const pit=$('chPit');if(pit)pit.style.display=d.pit?'flex':'none';
const lim=$('chLim');if(lim)lim.style.display=d.lim?'flex':'none';
const fl=d.fl;
const bn=$('flagbanner');
if(bn){
const map={1:['BLUE','blue'],2:['YELLOW','yellow'],3:['BLACK — DRIVE THROUGH','black'],4:['WHITE — SLOW CAR','white'],5:['CHEQUERED','checked'],6:['PENALTY','red'],8:['ORANGE — TRACK LIMITS','red']};
if(map[fl]){bn.textContent=map[fl][0];bn.className='show '+map[fl][1];}
else{bn.className='';bn.textContent='';}
}
}
function updatePedals(d){
const gas=Math.max(0,Math.min(1,d.ga||0)),brk=Math.max(0,Math.min(1,d.br||0));
const g=$('pfG');if(g)g.style.height=(gas*100)+'%';
const b=$('pfB');if(b)b.style.height=(brk*100)+'%';
setT('pfGn',Math.round(gas*100));setT('pfBn',Math.round(brk*100));
drawWheel(d);
const st=$('stNum');if(st){const deg=d.stD!==undefined?d.stD:(d.st||0)*60;st.innerHTML=Math.round(deg)+'&deg;';}
}
function drawWheel(d){
const c=$('cWheel');if(!c||!c.clientWidth)return;
const w=c.clientWidth,h=c.clientHeight;
if(c.width!==w||c.height!==h){c.width=w;c.height=h;}
const x=c.getContext('2d');x.clearRect(0,0,w,h);
const cx=w/2,cy=h/2,r=Math.min(w,h)*0.42;
let deg=d.stD!==undefined&&Math.abs(d.stD)>0.1?Math.max(-540,Math.min(540,d.stD)):Math.max(-180,Math.min(180,(d.st||0)*90));
x.strokeStyle='rgba(255,255,255,0.14)';x.lineWidth=2;
x.beginPath();x.arc(cx,cy,r,0,Math.PI*2);x.stroke();
x.save();x.translate(cx,cy);x.rotate(deg*Math.PI/180);
x.strokeStyle='#f2f5f7';x.lineWidth=Math.max(4,r*0.2);
x.beginPath();x.arc(0,0,r*0.82,Math.PI*0.15,Math.PI*0.85);x.stroke();
x.beginPath();x.arc(0,0,r*0.82,Math.PI*1.15,Math.PI*1.85);x.stroke();
x.strokeStyle='rgba(255,255,255,0.55)';x.lineWidth=Math.max(2,r*0.1);
x.beginPath();x.moveTo(-r*0.5,0);x.lineTo(r*0.5,0);x.stroke();
x.fillStyle='#e10600';x.beginPath();x.arc(0,-r*0.82,Math.max(2.5,r*0.09),0,Math.PI*2);x.fill();
x.restore();
x.fillStyle='#e10600';x.beginPath();x.arc(cx,cy- r*0.82 - 4,2,0,Math.PI*2);x.fill();
}
function updateTires(d){
const temps=[d.tt0||0,d.tt1||0,d.tt2||0,d.tt3||0];
const pres=[d.tp0||0,d.tp1||0,d.tp2||0,d.tp3||0];
const grip=[d.tg0||0,d.tg1||0,d.tg2||0,d.tg3||0];
for(let i=0;i<4;i++){
const t=temps[i],f=$('tf'+i);if(!f)continue;
if(t<=0.5){f.style.height='3%';f.style.background='rgba(255,255,255,0.1)';}
else{const pct=Math.max(0,Math.min(1,(t-20)/90));f.style.height=(pct*100)+'%';f.style.background='hsl('+(215-pct*215)+',90%,55%)';}
setT('tt'+i,t<=0.5?'--':t.toFixed(0)+'°');
setT('tp'+i,(pres[i]<=0.5?'--':pres[i].toFixed(0))+(grip[i]>0.01?' · '+Math.round(grip[i]*100)+'%':''));
}
}
function drawG(d){
const c=$('cG');if(!c||!c.clientWidth)return;
const w=c.clientWidth,h=c.clientHeight;
if(c.width!==w||c.height!==h){c.width=w;c.height=h;}
const x=c.getContext('2d');x.clearRect(0,0,w,h);
const cx=w/2,cy=h/2,r=Math.min(w,h)*0.44;
x.strokeStyle='rgba(255,255,255,0.22)';x.lineWidth=1.5;
x.beginPath();x.arc(cx,cy,r,0,Math.PI*2);x.stroke();
x.strokeStyle='rgba(255,255,255,0.1)';
x.beginPath();x.arc(cx,cy,r*0.5,0,Math.PI*2);x.stroke();
x.beginPath();x.moveTo(cx-r,cy);x.lineTo(cx+r,cy);x.moveTo(cx,cy-r);x.lineTo(cx,cy+r);x.stroke();
const gx=(d.gx!==undefined?d.gx:0),gy=(d.gy!==undefined?d.gy:0);
const bx=cx+Math.max(-r,Math.min(r,gx*r/2.2)),by=cy+Math.max(-r,Math.min(r,gy*r/2.2));
const grd=x.createRadialGradient(bx,by,1,bx,by,r*0.16);
grd.addColorStop(0,'#40c4ff');grd.addColorStop(1,'rgba(64,196,255,0.25)');
x.fillStyle=grd;x.beginPath();x.arc(bx,by,Math.max(4,r*0.14),0,Math.PI*2);x.fill();
x.strokeStyle='#fff';x.lineWidth=1.2;x.stroke();
setT('gLat','LAT '+gx.toFixed(2));setT('gLon','LON '+gy.toFixed(2));
}
function drawWave(){
const c=$('cWave');if(!c||!c.clientWidth)return;
const w=c.clientWidth,h=c.clientHeight;
if(c.width!==w||c.height!==h){c.width=w;c.height=h;}
const x=c.getContext('2d');x.clearRect(0,0,w,h);
const n=Math.min(idx,N);if(n<2)return;
const si=idx>=N?idx%N:0,mid=h/2;
x.strokeStyle='rgba(255,255,255,0.12)';x.setLineDash([3,6]);
x.beginPath();x.moveTo(0,mid);x.lineTo(w,mid);x.stroke();x.setLineDash([]);
const grad=x.createLinearGradient(0,0,w,0);grad.addColorStop(0,'rgba(0,176,255,0.25)');grad.addColorStop(1,'#00e676');
x.strokeStyle=grad;x.lineWidth=Math.max(1.5,h*0.12);x.lineJoin='round';
x.beginPath();
for(let i=0;i<n;i++){const v=D.fo[(si+i)%N]||0;const px=i/(n-1)*w,py=mid-v*mid*0.8;i===0?x.moveTo(px,py):x.lineTo(px,py);}
x.stroke();
}
function drawTrack(d){
const c=$('cTrack');if(!c||!c.clientWidth||d.np===undefined)return;
const w=c.clientWidth,h=c.clientHeight;
if(c.width!==w||c.height!==h){c.width=w;c.height=h;}
const x=c.getContext('2d');x.clearRect(0,0,w,h);
const cx=w/2,cy=h/2,r=Math.min(w,h)*0.35;
x.strokeStyle='rgba(255,255,255,0.18)';x.lineWidth=2;
x.beginPath();x.arc(cx,cy,r,0,Math.PI*2);x.stroke();
const a=(d.np||0)*Math.PI*2-Math.PI/2,px=cx+Math.cos(a)*r,py=cy+Math.sin(a)*r;
x.fillStyle='#00e676';x.beginPath();x.arc(px,py,Math.max(3.5,w*0.045),0,Math.PI*2);x.fill();
x.fillStyle='#fff';x.beginPath();x.arc(px,py,Math.max(1.5,w*0.02),0,Math.PI*2);x.fill();
}
</script></body></html>";
    }

    // ── Relative tower: racecraft at a glance ────────────────────────────
    private static string GetTowerHtml(OverlayOptions opts)
    {
        var opacity = opts.Opacity.ToString("F2");
        return @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>Relative Tower</title>
<link href='https://fonts.googleapis.com/css2?family=Rajdhani:wght@500;600;700&display=swap' rel='stylesheet'>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:transparent;color:#f2f5f7;font-family:" + GtFontStack + @";display:flex;align-items:flex-start;justify-content:flex-start;height:100vh}
#t{opacity:" + opacity + @";background:linear-gradient(165deg,rgba(22,25,29,0.95),rgba(9,10,12,0.95));border:1px solid rgba(255,255,255,0.1);border-left:3px solid #e10600;border-radius:0 12px 12px 0;padding:14px 18px;min-width:230px;box-shadow:0 12px 40px rgba(0,0,0,0.5)}
#tpos{font-size:clamp(38px,5vw,72px);font-weight:700;line-height:1;font-variant-numeric:tabular-nums}
#tpos small{font-size:0.42em;color:rgba(255,255,255,0.5)}
#tlap{font-size:clamp(12px,1.2vw,19px);color:rgba(255,255,255,0.65);letter-spacing:0.2em;font-weight:600;margin-top:2px}
.gap{display:flex;justify-content:space-between;align-items:baseline;margin-top:10px;padding:8px 10px;background:rgba(0,0,0,0.4);border-radius:8px}
.gap .n{font-size:11px;letter-spacing:0.2em;color:rgba(255,255,255,0.45);font-weight:600}
.gap .v{font-size:clamp(18px,2vw,30px);font-weight:700;font-variant-numeric:tabular-nums}
#gAv{color:#00e676}#gBv{color:#ff8a65}
#tflag{display:none;margin-top:10px;text-align:center;font-weight:700;letter-spacing:0.2em;padding:6px;border-radius:8px;font-size:14px}
#tflag.show{display:block}
#tflag.yellow{background:#facc15;color:#111}#tflag.blue{background:#1565c0;color:#fff}#tflag.red{background:#b71c1c;color:#fff}#tflag.white{background:#eee;color:#111}
#tsp{margin-top:8px;font-size:13px;color:rgba(255,255,255,0.55);font-variant-numeric:tabular-nums;letter-spacing:0.1em}
#tleds{display:flex;gap:3px;margin-top:8px}
#tleds .led{flex:1;height:10px;border-radius:3px;background:rgba(255,255,255,0.08)}
#tleds .led.on{background:#00e676;box-shadow:0 0 8px #00e676}
#tleds .led.on.r{background:#ff1744;box-shadow:0 0 8px #ff1744}
</style></head><body>
<div id='t'>
<div id='tpos'>P0<small>/0</small></div>
<div id='tlap'>LAP 0</div>
<div class='gap'><span class='n'>AHEAD</span><span class='v' id='gAv'>+0.000</span></div>
<div class='gap'><span class='n'>BEHIND</span><span class='v' id='gBv'>+0.000</span></div>
<div id='tflag'></div>
<div id='tsp'>0 km/h · N</div>
<div id='tleds'>" + GtLedBar(10, "tled") + @"</div>
</div>
<script>
const $=id=>document.getElementById(id);
const es=new EventSource('/stream');
es.onmessage=(e)=>{
const d=JSON.parse(e.data);if(d.force)return;
$('tpos').innerHTML='P'+(d.pos||0)+'<small>/'+(d.tot||0)+'</small>';
$('tlap').textContent='LAP '+(d.la||0);
$('gAv').textContent='+'+(d.gapA||0).toFixed(3);
$('gBv').textContent='+'+(d.gapB||0).toFixed(3);
const gr=d.ge;$('tsp').textContent=(d.sp||0).toFixed(0)+' km/h · '+(gr===0?'R':(gr<=1?'N':gr));
const rp=d.rp||0,lit=Math.round(rp*10);
for(let i=0;i<10;i++){const l=$('tled'+i);if(l)l.className='led'+(i<lit?(' on'+(i>6?' r':'')):'');}
const fl=d.fl,f=$('tflag');
const map={1:['BLUE','blue'],2:['YELLOW','yellow'],3:['BLACK FLAG','red'],4:['WHITE FLAG','white'],5:['FINISH','white'],6:['PENALTY','red'],8:['TRACK LIMITS','red']};
if(map[fl]){f.textContent=map[fl][0];f.className='show '+map[fl][1];}else{f.className='';f.textContent='';}
};
</script></body></html>";
    }

    // ── Inputs: pedals + steering + FFB trace for coaching ───────────────
    private static string GetInputsHtml(OverlayOptions opts)
    {
        var opacity = opts.Opacity.ToString("F2");
        return @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>Inputs</title>
<link href='https://fonts.googleapis.com/css2?family=Rajdhani:wght@500;600;700&display=swap' rel='stylesheet'>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:transparent;color:#f2f5f7;font-family:" + GtFontStack + @";display:flex;align-items:center;justify-content:center;height:100vh}
#i{opacity:" + opacity + @";display:flex;gap:clamp(12px,1.6vw,28px);align-items:stretch;background:linear-gradient(165deg,rgba(22,25,29,0.95),rgba(9,10,12,0.95));border:1px solid rgba(255,255,255,0.1);border-radius:14px;padding:clamp(12px,1.5vw,24px)}
.col{display:flex;flex-direction:column;align-items:center;gap:6px}
.bar{width:clamp(44px,5vw,90px);height:clamp(150px,28vh,340px);background:rgba(255,255,255,0.06);border:1px solid rgba(255,255,255,0.1);border-radius:10px;position:relative;overflow:hidden}
.fill{position:absolute;bottom:0;left:0;right:0;transition:height 35ms linear}
.fill.gas{background:linear-gradient(0deg,#00897b,#00e676)}
.fill.brake{background:linear-gradient(0deg,#b71c1c,#ff5252)}
.num{font-size:clamp(20px,2.4vw,40px);font-weight:700;font-variant-numeric:tabular-nums}
.lb{font-size:11px;letter-spacing:0.24em;color:rgba(255,255,255,0.45);font-weight:600}
#cW{width:clamp(130px,15vw,260px);height:clamp(130px,15vw,260px)}
#trace{width:clamp(220px,30vw,560px);display:flex;flex-direction:column;gap:6px}
#cT{width:100%;height:clamp(150px,28vh,340px);background:rgba(0,0,0,0.45);border:1px solid rgba(255,255,255,0.08);border-radius:10px}
#ffn{font-size:clamp(18px,2vw,34px);font-weight:700;font-variant-numeric:tabular-nums}
#clipn{display:none;color:#ff1744;font-weight:700;letter-spacing:0.2em;animation:bl 0.6s steps(2) infinite}
@keyframes bl{50%{opacity:0.4}}
</style></head><body>
<div id='i'>
<div class='col'><div class='bar'><div class='fill gas' id='ig'></div></div><div class='num' id='ign'>0</div><div class='lb'>THROTTLE</div></div>
<div class='col'><div class='bar'><div class='fill brake' id='ib'></div></div><div class='num' id='ibn'>0</div><div class='lb'>BRAKE</div></div>
<div class='col'><canvas id='cW'></canvas><div class='num' id='stn'>0&deg;</div><div class='lb'>STEERING</div></div>
<div id='trace'><canvas id='cT'></canvas><div><span id='ffn'>0.000 Nm</span> <span id='clipn'>CLIP</span></div><div class='lb'>FFB TRACE</div></div>
</div>
<script>
const N=400;let buf=new Float32Array(N),idx=0;
const $=id=>document.getElementById(id);
const es=new EventSource('/stream');
es.onmessage=(e)=>{
const d=JSON.parse(e.data);if(d.force)return;
const g=d.ga||0,b=d.br||0;
$('ig').style.height=(g*100)+'%';$('ib').style.height=(b*100)+'%';
$('ign').textContent=Math.round(g*100);$('ibn').textContent=Math.round(b*100);
const deg=d.stD!==undefined?d.stD:(d.st||0)*90;
$('stn').innerHTML=Math.round(deg)+'&deg;';
drawWheel(deg);
buf[idx%N]=d.fo||0;idx++;drawTrace();
$('ffn').textContent=Math.abs(d.fo||0).toFixed(3)+' Nm';
$('clipn').style.display=d.cl?'inline':'none';
};
function drawWheel(deg){
const c=$('cW'),w=c.clientWidth,h=c.clientHeight;
if(c.width!==w||c.height!==h){c.width=w;c.height=h;}
const x=c.getContext('2d');x.clearRect(0,0,w,h);
const cx=w/2,cy=h/2,r=Math.min(w,h)*0.4;
x.strokeStyle='rgba(255,255,255,0.15)';x.lineWidth=3;
x.beginPath();x.arc(cx,cy,r,0,Math.PI*2);x.stroke();
x.save();x.translate(cx,cy);x.rotate(Math.max(-540,Math.min(540,deg))*Math.PI/180);
x.strokeStyle='#fff';x.lineWidth=7;
x.beginPath();x.arc(0,0,r*0.8,Math.PI*0.15,Math.PI*0.85);x.stroke();
x.beginPath();x.arc(0,0,r*0.8,Math.PI*1.15,Math.PI*1.85);x.stroke();
x.fillStyle='#e10600';x.beginPath();x.arc(0,-r*0.8,6,0,Math.PI*2);x.fill();
x.restore();
}
function drawTrace(){
const c=$('cT'),w=c.clientWidth,h=c.clientHeight;
if(c.width!==w||c.height!==h){c.width=w;c.height=h;}
const x=c.getContext('2d');x.clearRect(0,0,w,h);
x.strokeStyle='rgba(255,255,255,0.15)';x.setLineDash([4,6]);
x.beginPath();x.moveTo(0,h/2);x.lineTo(w,h/2);x.stroke();x.setLineDash([]);
const n=Math.min(idx,N),si=idx>=N?idx%N:0;
x.strokeStyle='#00e676';x.lineWidth=2.5;x.lineJoin='round';x.beginPath();
for(let i=0;i<n;i++){const v=buf[(si+i)%N];const px=i/(n-1)*w,py=h/2-v*h*0.42;i===0?x.moveTo(px,py):x.lineTo(px,py);}
x.stroke();
}
</script></body></html>";
    }

    // ── Tires & brakes: 4-corner detail ──────────────────────────────────
    private static string GetTiresHtml(OverlayOptions opts)
    {
        var opacity = opts.Opacity.ToString("F2");
        return @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>Tires</title>
<link href='https://fonts.googleapis.com/css2?family=Rajdhani:wght@500;600;700&display=swap' rel='stylesheet'>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:transparent;color:#f2f5f7;font-family:" + GtFontStack + @";display:flex;align-items:center;justify-content:center;height:100vh}
#tt{opacity:" + opacity + @";background:linear-gradient(165deg,rgba(22,25,29,0.95),rgba(9,10,12,0.95));border:1px solid rgba(255,255,255,0.1);border-radius:14px;padding:clamp(12px,1.5vw,24px);display:flex;flex-direction:column;gap:10px}
#tt h2{font-size:12px;letter-spacing:0.3em;color:rgba(255,255,255,0.45);font-weight:600}
#row{display:flex;gap:clamp(10px,1.2vw,22px)}
.t{display:flex;flex-direction:column;align-items:center;gap:4px;background:rgba(0,0,0,0.4);border:1px solid rgba(255,255,255,0.08);border-radius:10px;padding:10px 14px;min-width:clamp(90px,10vw,170px)}
.t .n{font-weight:700;letter-spacing:0.15em;color:rgba(255,255,255,0.55)}
.t .tmp{font-size:clamp(26px,3vw,52px);font-weight:700;font-variant-numeric:tabular-nums}
.t .pr{font-size:clamp(12px,1.2vw,19px);color:rgba(255,255,255,0.65);font-variant-numeric:tabular-nums}
.t .bp{width:100%;height:8px;background:rgba(255,255,255,0.07);border-radius:4px;overflow:hidden}
.t .bp>div{height:100%;background:linear-gradient(90deg,#ff9100,#ff3d00);width:0%}
.t .gr{width:100%;height:5px;background:rgba(255,255,255,0.07);border-radius:3px;overflow:hidden}
.t .gr>div{height:100%;background:#00e676;width:0%}
#cG2{width:120px;height:120px}
#bot{display:flex;gap:18px;align-items:center;justify-content:center}
.gv{font-size:14px;font-weight:700;font-variant-numeric:tabular-nums}
</style></head><body>
<div id='tt'><h2>TYRES · °C / kPa / BRAKE N / GRIP</h2>
<div id='row'>
<div class='t'><span class='n'>FL</span><span class='tmp' id='t0'>--</span><span class='pr' id='p0'>--</span><div class='bp'><div id='b0'></div></div><div class='gr'><div id='g0'></div></div></div>
<div class='t'><span class='n'>FR</span><span class='tmp' id='t1'>--</span><span class='pr' id='p1'>--</span><div class='bp'><div id='b1'></div></div><div class='gr'><div id='g1'></div></div></div>
<div class='t'><span class='n'>RL</span><span class='tmp' id='t2'>--</span><span class='pr' id='p2'>--</span><div class='bp'><div id='b2'></div></div><div class='gr'><div id='g2'></div></div></div>
<div class='t'><span class='n'>RR</span><span class='tmp' id='t3'>--</span><span class='pr' id='p3'>--</span><div class='bp'><div id='b3'></div></div><div class='gr'><div id='g3'></div></div></div>
</div>
<div id='bot'><canvas id='cG2'></canvas><span class='gv' id='gv'>LAT 0.00 · LON 0.00</span></div>
</div>
<script>
const $=id=>document.getElementById(id);
const es=new EventSource('/stream');
es.onmessage=(e)=>{
const d=JSON.parse(e.data);if(d.force)return;
const T=[d.tt0||0,d.tt1||0,d.tt2||0,d.tt3||0],P=[d.tp0||0,d.tp1||0,d.tp2||0,d.tp3||0];
const B=[d.bp0||0,d.bp1||0,d.bp2||0,d.bp3||0],G=[d.tg0||0,d.tg1||0,d.tg2||0,d.tg3||0];
for(let i=0;i<4;i++){
const t=T[i],te=$('t'+i);
if(t<=0.5){te.textContent='--';te.style.color='#888';}
else{te.textContent=t.toFixed(0)+'°';const pct=Math.max(0,Math.min(1,(t-20)/90));te.style.color='hsl('+(215-pct*215)+',90%,62%)';}
$('p'+i).textContent=(P[i]<=0.5?'--':P[i].toFixed(0)+' kPa');
$('b'+i).style.width=Math.min(B[i]/5000*100,100)+'%';
$('g'+i).style.width=Math.min(G[i]*100,100)+'%';
}
const gx=d.gx||0,gy=d.gy||0;
$('gv').textContent='LAT '+gx.toFixed(2)+' · LON '+gy.toFixed(2);
const c=$('cG2'),w=c.clientWidth||120,h=c.clientHeight||120;
if(c.width!==w||c.height!==h){c.width=w;c.height=h;}
const x=c.getContext('2d');x.clearRect(0,0,w,h);
const cx=w/2,cy=h/2,r=Math.min(w,h)*0.42;
x.strokeStyle='rgba(255,255,255,0.2)';x.beginPath();x.arc(cx,cy,r,0,Math.PI*2);x.stroke();
x.fillStyle='#40c4ff';x.beginPath();x.arc(cx+Math.max(-r,Math.min(r,gx*r/2.2)),cy+Math.max(-r,Math.min(r,gy*r/2.2)),6,0,Math.PI*2);x.fill();
};
</script></body></html>";
    }

    // ── FFB health: tuning view ──────────────────────────────────────────
    private static string GetFfbHtml(OverlayOptions opts)
    {
        var opacity = opts.Opacity.ToString("F2");
        return @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>FFB Health</title>
<link href='https://fonts.googleapis.com/css2?family=Rajdhani:wght@500;600;700&display=swap' rel='stylesheet'>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:transparent;color:#f2f5f7;font-family:" + GtFontStack + @";display:flex;align-items:center;justify-content:center;height:100vh}
#f{opacity:" + opacity + @";background:linear-gradient(165deg,rgba(22,25,29,0.95),rgba(9,10,12,0.95));border:1px solid rgba(255,255,255,0.1);border-radius:14px;padding:clamp(12px,1.5vw,24px);width:min(92vw,760px)}
#f h2{font-size:12px;letter-spacing:0.3em;color:rgba(255,255,255,0.45);font-weight:600}
#fv2{font-size:clamp(40px,5vw,84px);font-weight:700;font-variant-numeric:tabular-nums;line-height:1}
#fv2 small{font-size:0.35em;color:rgba(255,255,255,0.4);font-weight:500}
#ok{display:inline-block;margin-left:12px;padding:4px 14px;border-radius:8px;font-weight:700;letter-spacing:0.15em;background:rgba(0,230,118,0.15);color:#00e676;border:1px solid rgba(0,230,118,0.5)}
#ok.bad{background:rgba(255,23,68,0.18);color:#ff1744;border-color:#ff1744;animation:bl 0.6s steps(2) infinite}
@keyframes bl{50%{opacity:0.45}}
#cW2{width:100%;height:clamp(90px,14vh,170px);background:rgba(0,0,0,0.45);border:1px solid rgba(255,255,255,0.08);border-radius:10px;margin-top:10px}
.ch{display:flex;align-items:center;gap:10px;margin-top:8px}
.ch .n{width:34px;font-weight:700;color:rgba(255,255,255,0.55)}
.ch .tr{flex:1;height:10px;background:rgba(255,255,255,0.07);border-radius:5px;position:relative;overflow:hidden}
.ch .tr>div{position:absolute;top:0;bottom:0;left:50%;width:0%}
.ch .v{width:70px;text-align:right;font-variant-numeric:tabular-nums;font-weight:600}
</style></head><body>
<div id='f'><h2>FFB HEALTH</h2>
<div><span id='fv2'>0.000 <small>Nm</small></span><span id='ok'>OK</span></div>
<canvas id='cW2'></canvas>
<div class='ch'><span class='n'>Mz</span><div class='tr'><div id='fmz' style='background:#ffd54f'></div></div><span class='v' id='fmzv'>0</span></div>
<div class='ch'><span class='n'>Fx</span><div class='tr'><div id='ffx' style='background:#ff8a80'></div></div><span class='v' id='ffxv'>0</span></div>
<div class='ch'><span class='n'>Fy</span><div class='tr'><div id='ffy' style='background:#69f0ae'></div></div><span class='v' id='ffyv'>0</span></div>
</div>
<script>
const N=400;let buf=new Float32Array(N),idx=0;
const $=id=>document.getElementById(id);
const es=new EventSource('/stream');
es.onmessage=(e)=>{
const d=JSON.parse(e.data);if(d.force)return;
const fo=d.fo||0;
$('fv2').innerHTML=Math.abs(fo).toFixed(3)+' <small>Nm</small>';
const ok=$('ok'),clip=d.cl?true:false;
ok.textContent=clip?'CLIPPING':'OK';ok.className=clip?'bad':'';
if(clip)ok.id='ok';ok.classList.toggle('bad',clip);
buf[idx%N]=fo;idx++;draw();
bar('fmz','fmzv',d.mz||0);bar('ffx','ffxv',d.fx||0);bar('ffy','ffyv',d.fy||0);
};
function bar(id,vid,v){
const b=$(id);const pct=Math.min(Math.abs(v)*50,50);
b.style.left=v>=0?'50%':(50-pct)+'%';b.style.width=pct+'%';
$(vid).textContent=v.toFixed(3);
}
function draw(){
const c=$('cW2'),w=c.clientWidth,h=c.clientHeight;
if(c.width!==w||c.height!==h){c.width=w;c.height=h;}
const x=c.getContext('2d');x.clearRect(0,0,w,h);
x.strokeStyle='rgba(255,23,68,0.5)';x.setLineDash([5,5]);
x.beginPath();x.moveTo(0,h*0.05);x.lineTo(w,h*0.05);x.moveTo(0,h*0.95);x.lineTo(w,h*0.95);x.stroke();x.setLineDash([]);
x.strokeStyle='rgba(255,255,255,0.15)';
x.beginPath();x.moveTo(0,h/2);x.lineTo(w,h/2);x.stroke();
const n=Math.min(idx,N),si=idx>=N?idx%N:0;
x.strokeStyle='#fff';x.lineWidth=2;x.beginPath();
for(let i=0;i<n;i++){const v=buf[(si+i)%N];const px=i/(n-1)*w,py=h/2-v*h*0.45;i===0?x.moveTo(px,py):x.lineTo(px,py);}
x.stroke();
}
</script></body></html>";
    }

    // ── Minimal: shift + gear + flag dot for VR / small window ───────────
    private static string GetMinimalHtml(OverlayOptions opts)
    {
        var opacity = opts.Opacity.ToString("F2");
        return @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>Shift</title>
<link href='https://fonts.googleapis.com/css2?family=Rajdhani:wght@600;700&display=swap' rel='stylesheet'>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:transparent;color:#fff;font-family:" + GtFontStack + @";display:flex;align-items:center;justify-content:center;height:100vh}
#m{opacity:" + opacity + @";display:flex;align-items:center;gap:12px;background:rgba(8,10,12,0.72);border:1px solid rgba(255,255,255,0.1);border-radius:12px;padding:8px 16px}
#mleds{display:flex;gap:4px}
#mleds .led{width:clamp(10px,1.4vw,20px);height:clamp(14px,2.4vh,30px);border-radius:4px;background:rgba(255,255,255,0.1)}
#mleds .led.on{background:#00e676;box-shadow:0 0 10px #00e676}
#mleds .led.on.r{background:#ff1744;box-shadow:0 0 10px #ff1744}
#mg{font-size:clamp(28px,4vw,58px);font-weight:700;font-variant-numeric:tabular-nums;line-height:1}
#mg.shift{color:#ff1744;animation:bl 0.35s steps(2) infinite}
@keyframes bl{50%{opacity:0.5}}
#ms{font-size:clamp(13px,1.4vw,22px);color:rgba(255,255,255,0.6);font-variant-numeric:tabular-nums}
#flagdot{width:14px;height:14px;border-radius:50%;background:transparent}
#clipdot{width:14px;height:14px;border-radius:50%;background:rgba(0,230,118,0.9);box-shadow:0 0 8px rgba(0,230,118,0.8)}
#clipdot.bad{background:#ff1744;box-shadow:0 0 10px #ff1744}
</style></head><body>
<div id='m'><div id='mleds'>" + GtLedBar(7, "mled") + @"</div><div id='mg'>N</div><div id='ms'>0</div><div id='flagdot'></div><div id='clipdot'></div></div>
<script>
const $=id=>document.getElementById(id);
const es=new EventSource('/stream');
es.onmessage=(e)=>{
const d=JSON.parse(e.data);if(d.force)return;
const rp=d.rp||0,lit=Math.round(rp*7);
for(let i=0;i<7;i++){const l=$('mled'+i);if(l)l.className='led'+(i<lit?(' on'+(i>4?' r':'')):'');}
const gr=d.ge,g=$('mg');
g.textContent=gr===0?'R':(gr<=1?'N':gr);
g.classList.toggle('shift',rp>0.95||d.lim===1);
$('ms').textContent=(d.sp||0).toFixed(0);
const fl=d.fl,fd=$('flagdot');
const cols={1:'#2196f3',2:'#facc15',3:'#000',4:'#fff',5:'#fff',6:'#ff1744',8:'#ff9100'};
fd.style.background=cols[fl]||'transparent';
fd.style.boxShadow=cols[fl]?('0 0 10px '+cols[fl]):'none';
$('clipdot').className=d.cl?'bad':'';
};
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
