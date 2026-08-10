using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AcEvoFfbTuner.Core;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class FfbLiveServerTests : IDisposable
{
    private readonly FfbLiveServer _sut;

    public FfbLiveServerTests()
    {
        _sut = new FfbLiveServer(8731);
        _sut.Start();
    }

    public void Dispose()
    {
        _sut.Stop();
        _sut.Dispose();
    }

    private static FfbRawData MakeRaw()
    {
        var raw = new FfbRawData
        {
            SteerAngle = 0.25f,
            SpeedKmh = 120f,
            Gear = 3,
            RpmPercent = 0.6f,
            CurrentLap = 2,
            RacePosition = 4,
            TotalDrivers = 12,
            GapAhead = 1.5f,
            GapBehind = 2.5f,
            AccG = [0.4f, 0f, 0.8f],
            TyreTemp = [72f, 68f, 55f, 51f],
            WheelsPressure = [180f, 178f, 175f, 174f],
            TyreGrip = [0.95f, 0.93f, 0.9f, 0.88f],
            TcActiveGfx = true,
            AbsActiveGfx = false,
            Flag = 2,
            IsPitLimiterOn = false
        };
        return raw;
    }

    private static FfbProcessedData MakeProc()
    {
        return new FfbProcessedData
        {
            MainForce = 0.45f,
            ChannelMzFront = 0.1f,
            ChannelFxFront = 0.2f,
            ChannelFyFront = 0.3f,
            PostCompressionForce = 0.4f,
            PostDampingForce = 0.35f,
            PostOutputGainForce = 0.45f,
            PostLutForce = 0.42f,
            PostDynamicForce = 0.3f,
            IsClipping = false
        };
    }

    private static async Task<string> HttpGet(int port, string path)
    {
        using var client = new HttpClient();
        var resp = await client.GetStringAsync($"http://localhost:{port}{path}");
        return resp;
    }

    [Fact]
    public async Task Overlay_WithAllModulesDefault_HasNoHiddenModules()
    {
        var html = await HttpGet(8731, "/overlay");
        html.Should().Contain("md-speed' style=''");
        html.Should().Contain("md-force' style=''");
        html.Should().Contain("md-waveform' style=''");
        html.Should().Contain("md-track' style=''");
        html.Should().Contain("md-pedals' style=''");
        html.Should().Contain("md-tires' style=''");
        html.Should().Contain("md-gforce' style=''");
    }

    [Fact]
    public async Task Overlay_WithSelectedMods_HidesUnselectedModules()
    {
        var html = await HttpGet(8731, "/overlay?mods=pedals,tires,gforce");
        html.Should().Contain("md-pedals' style=''");
        html.Should().Contain("md-tires' style=''");
        html.Should().Contain("md-gforce' style=''");
        html.Should().Contain("md-speed' style='display:none'");
        html.Should().Contain("md-force' style='display:none'");
        html.Should().Contain("md-waveform' style='display:none'");
        html.Should().Contain("md-track' style='display:none'");
    }

    [Fact]
    public async Task Overlay_NoModsSelected_ShowsNothing()
    {
        var html = await HttpGet(8731, "/overlay?mods=none");
        html.Should().Contain("md-speed' style='display:none'");
        html.Should().Contain("md-force' style='display:none'");
        html.Should().Contain("md-waveform' style='display:none'");
        html.Should().Contain("md-track' style='display:none'");
        html.Should().Contain("md-pedals' style='display:none'");
        html.Should().Contain("md-tires' style='display:none'");
        html.Should().Contain("md-gforce' style='display:none'");
    }

    [Fact]
    public async Task Overlay_ShowWaveformFalse_HidesWaveform()
    {
        var html = await HttpGet(8731, "/overlay?showwaveform=false");
        html.Should().Contain("md-waveform' style='display:none'");
    }

    private static async Task<(TcpClient tcp, StreamReader reader)> ConnectStream(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("localhost", port);
        var stream = tcp.GetStream();
        var req = Encoding.UTF8.GetBytes("GET /stream HTTP/1.1\r\nHost: localhost\r\n\r\n");
        await stream.WriteAsync(req);
        var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        return (tcp, reader);
    }

    private static async Task<string?> ReadDataLine(StreamReader reader, bool skipInit)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cts.Token)) != null)
            {
                if (line.StartsWith("data: {") && (!skipInit || !line.StartsWith("data: {\"force\"")))
                    return line["data: ".Length..];
            }
        }
        catch (OperationCanceledException)
        {
        }
        return null;
    }

    [Fact]
    public async Task Stream_PushedData_ProducesValidJsonWithNewFields()
    {
        var (tcp, reader) = await ConnectStream(8731);
        try
        {
            var init = await ReadDataLine(reader, skipInit: false);
            init.Should().NotBeNull();
            using (var doc = JsonDocument.Parse(init!))
            {
                doc.RootElement.GetProperty("force").GetArrayLength().Should().Be(0);
            }

            _sut.OnData(MakeRaw(), MakeProc());
            _sut.OnData(MakeRaw(), MakeProc()); // SendEveryNth=2: second call broadcasts

            var dataLine = await ReadDataLine(reader, skipInit: true);
            dataLine.Should().NotBeNullOrEmpty();
            using var doc2 = JsonDocument.Parse(dataLine!);
            var root = doc2.RootElement;
            root.TryGetProperty("tt0", out _).Should().BeTrue();
            root.GetProperty("tt0").GetSingle().Should().BeApproximately(72f, 0.05f);
            root.GetProperty("tt3").GetSingle().Should().BeApproximately(51f, 0.05f);
            root.GetProperty("tp0").GetSingle().Should().BeApproximately(180f, 0.05f);
            root.GetProperty("tg0").GetSingle().Should().BeApproximately(0.95f, 0.001f);
            root.GetProperty("tca").GetInt32().Should().Be(1);
            root.GetProperty("aba").GetInt32().Should().Be(0);
            root.GetProperty("fl").GetInt32().Should().Be(2);
            root.GetProperty("pit").GetInt32().Should().Be(0);
            root.GetProperty("gx").GetSingle().Should().BeApproximately(0.4f, 0.001f);
            root.GetProperty("gy").GetSingle().Should().BeApproximately(0.8f, 0.001f);
            root.GetProperty("stD").GetSingle().Should().BeApproximately(0f, 0.05f);
        }
        finally
        {
            tcp.Dispose();
        }
    }

    [Fact]
    public async Task Stream_NonEmptyHistory_InitParses()
    {
        _sut.OnData(MakeRaw(), MakeProc());
        _sut.OnData(MakeRaw(), MakeProc());

        var (tcp, reader) = await ConnectStream(8731);
        try
        {
            var init = await ReadDataLine(reader, skipInit: false);
            init.Should().NotBeNull();
            using var doc = JsonDocument.Parse(init!);
            doc.RootElement.GetProperty("force").GetArrayLength().Should().Be(2);
            doc.RootElement.GetProperty("steer").GetArrayLength().Should().Be(2);
            doc.RootElement.GetProperty("speed").GetArrayLength().Should().Be(2);
        }
        finally
        {
            tcp.Dispose();
        }
    }

    [Fact]
    public async Task Stream_LmuAccel_NormalizedToG()
    {
        _sut.LiveGame = "lmu";
        var raw = MakeRaw();
        raw.AccG = [2f, 0f, 15f]; // raw m/s² from LMU telemetry

        var (tcp, reader) = await ConnectStream(8731);
        try
        {
            var init = await ReadDataLine(reader, skipInit: false);
            init.Should().NotBeNull();

            _sut.OnData(raw, MakeProc());
            _sut.OnData(raw, MakeProc());

            var dataLine = await ReadDataLine(reader, skipInit: true);
            dataLine.Should().NotBeNullOrEmpty();
            using var doc = JsonDocument.Parse(dataLine!);
            var root = doc.RootElement;
            root.GetProperty("gx").GetSingle().Should().BeApproximately(2f / 9.80665f, 0.001f);
            root.GetProperty("gy").GetSingle().Should().BeApproximately(15f / 9.80665f, 0.001f);
        }
        finally
        {
            tcp.Dispose();
        }
    }

    [Fact]
    public async Task Stream_R3eFallbackAccel_NormalizedToG()
    {
        _sut.LiveGame = "r3e";
        var raw = MakeRaw();
        raw.AccG = [0f, 0f, 0f]; // GForce placeholder at rest
        raw.DisplayAccG = [3f, 0f, 9.80665f]; // LocalAccelG in m/s²

        var (tcp, reader) = await ConnectStream(8731);
        try
        {
            var init = await ReadDataLine(reader, skipInit: false);
            init.Should().NotBeNull();

            _sut.OnData(raw, MakeProc());
            _sut.OnData(raw, MakeProc());

            var dataLine = await ReadDataLine(reader, skipInit: true);
            dataLine.Should().NotBeNullOrEmpty();
            using var doc = JsonDocument.Parse(dataLine!);
            var root = doc.RootElement;
            root.GetProperty("gx").GetSingle().Should().BeApproximately(3f / 9.80665f, 0.001f);
            root.GetProperty("gy").GetSingle().Should().BeApproximately(1f, 0.001f);
        }
        finally
        {
            tcp.Dispose();
        }
    }
}
