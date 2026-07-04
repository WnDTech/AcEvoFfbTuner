using System.IO.MemoryMappedFiles;
using System.Text;
using AcEvoFfbTuner.Core.SharedMemory;
using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace TelemetryBrowser.Services;

public sealed class RFactor2SharedMemoryReader : ISharedMemoryReader
{
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private bool _disposed;
    private const int MMF_SIZE = 65536;

    public event Action? GameConnected;
    public event Action? GameDisconnected;

    private static readonly string[] MMF_NAMES = [
        @"Local\$rFactor2Telemetry$",
        @"$rFactor2Telemetry$",
        @"Local\rFactor2Telemetry",
        @"rFactor2Telemetry",
    ];

    public bool IsConnected => _view != null;

    public bool TryConnect()
    {
        foreach (var name in MMF_NAMES)
        {
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                _view = _mmf.CreateViewAccessor(0, MMF_SIZE, MemoryMappedFileAccess.Read);
                GameConnected?.Invoke();
                return true;
            }
            catch { }
        }
        return false;
    }

    public void Disconnect()
    {
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        GameDisconnected?.Invoke();
    }

    public bool TryReadPhysics(out SPageFilePhysicsEvo physics)
    {
        physics = default;
        if (_view == null) return false;
        try
        {
            var buf = new byte[MMF_SIZE];
            _view.ReadArray(0, buf, 0, MMF_SIZE);
            physics.PacketId = 1;
            physics.Gas = BitConverter.ToSingle(buf, 260);
            physics.Brake = BitConverter.ToSingle(buf, 264);
            physics.Fuel = BitConverter.ToSingle(buf, 312);
            physics.Gear = BitConverter.ToInt32(buf, 240);
            physics.Rpms = BitConverter.ToInt32(buf, 244);
            physics.SteerAngle = BitConverter.ToSingle(buf, 268);
            double vx = BitConverter.ToDouble(buf, 168), vy = BitConverter.ToDouble(buf, 176), vz = BitConverter.ToDouble(buf, 184);
            physics.SpeedKmh = (float)(Math.Sqrt(vx*vx + vy*vy + vz*vz) * 3.6);
            physics.Velocity = new float[3] { (float)vx, (float)vy, (float)vz };
            physics.FinalFf = BitConverter.ToSingle(buf, 276);
            physics.TurboBoost = BitConverter.ToSingle(buf, 296);
            physics.WaterTemp = BitConverter.ToSingle(buf, 248);
            physics.RideHeight = new float[2] { BitConverter.ToSingle(buf, 288), BitConverter.ToSingle(buf, 292) };
            return true;
        }
        catch { return false; }
    }

    public bool TryReadGraphics(out SPageFileGraphicEvo graphics)
    {
        graphics = default;
        return false;
    }

    public bool TryReadStatic(out SPageFileStaticEvo staticData)
    {
        staticData = default;
        if (_view == null) return false;
        try
        {
            var buf = new byte[MMF_SIZE];
            _view.ReadArray(0, buf, 0, MMF_SIZE);
            staticData.Track = Pad(ReadFixedString(buf, 80, 64), 64);
            staticData.TrackConfiguration = new byte[64];
            staticData.SmVersion = Pad("rF2", 15);
            staticData.AcEvoVersion = Pad("1.0", 15);
            return true;
        }
        catch { return false; }
    }

    private static byte[] Pad(string s, int len) => Encoding.UTF8.GetBytes((s + new string('\0', len)).Substring(0, len));

    private static string ReadFixedString(byte[] buf, int offset, int maxLen)
    {
        int end = Math.Min(offset + maxLen, buf.Length);
        int nullIdx = Array.IndexOf(buf, (byte)0, offset, end - offset);
        int len = nullIdx < 0 ? end - offset : nullIdx - offset;
        return Encoding.UTF8.GetString(buf, offset, len);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Disconnect();
        _disposed = true;
    }
}
