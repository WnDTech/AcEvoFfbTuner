using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace AcEvoFfbTuner.Core.SharedMemory;

public sealed class SharedMemoryReader : IDisposable
{
    private const string PhysicsMapName = @"Local\acevo_pmf_physics";
    private const string GraphicsMapName = @"Local\acevo_pmf_graphics";
    private const string StaticMapName = @"Local\acevo_pmf_static";

    private MemoryMappedFile? _physicsMmf;
    private MemoryMappedFile? _graphicsMmf;
    private MemoryMappedFile? _staticMmf;

    private MemoryMappedViewAccessor? _physicsView;
    private MemoryMappedViewAccessor? _graphicsView;
    private MemoryMappedViewAccessor? _staticView;

    private int _lastPhysicsPacketId = -1;
    private int _lastGraphicsPacketId = -1;

    public bool IsConnected => _physicsMmf != null;

    public event Action? GameConnected;
    public event Action? GameDisconnected;

    public bool TryConnect()
    {
        if (_physicsMmf != null) return true;

        try
        {
            _physicsMmf = MemoryMappedFile.OpenExisting(PhysicsMapName, MemoryMappedFileRights.Read);
            _graphicsMmf = MemoryMappedFile.OpenExisting(GraphicsMapName, MemoryMappedFileRights.Read);
            _staticMmf = MemoryMappedFile.OpenExisting(StaticMapName, MemoryMappedFileRights.Read);

            _physicsView = _physicsMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _graphicsView = _graphicsMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _staticView = _staticMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            _lastPhysicsPacketId = -1;
            _lastGraphicsPacketId = -1;

            GameConnected?.Invoke();
            return true;
        }
        catch (Exception)
        {
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        _physicsView?.Dispose();
        _graphicsView?.Dispose();
        _staticView?.Dispose();
        _physicsMmf?.Dispose();
        _graphicsMmf?.Dispose();
        _staticMmf?.Dispose();

        _physicsView = null;
        _graphicsView = null;
        _staticView = null;
        _physicsMmf = null;
        _graphicsMmf = null;
        _staticMmf = null;

        _lastPhysicsPacketId = -1;
        _lastGraphicsPacketId = -1;

        GameDisconnected?.Invoke();
    }

    public bool TryReadPhysics(out SPageFilePhysicsEvo physics)
    {
        physics = default;
        if (_physicsView == null) return false;

        try
        {
            int size = Marshal.SizeOf<SPageFilePhysicsEvo>();
            byte[] buffer = new byte[size];
            _physicsView.ReadArray(0, buffer, 0, size);

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                physics = Marshal.PtrToStructure<SPageFilePhysicsEvo>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }

            if (physics.PacketId == _lastPhysicsPacketId)
                return false;

            if (buffer.Length >= 32)
                physics.SpeedKmh = BitConverter.ToSingle(buffer, 28);

            if (physics.PacketId % 200 == 0 || physics.PacketId <= 1)
                DumpPhysicsBytes(buffer, physics);

            _lastPhysicsPacketId = physics.PacketId;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DumpPhysicsBytes(byte[] buffer, SPageFilePhysicsEvo physics)
    {
        try
        {
            var dumpPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "physics_hex_dump.log");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Physics Hex Dump @ {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} (PacketId={BitConverter.ToInt32(buffer, 0)}) ===");
            sb.AppendLine($"Buffer size: {buffer.Length} bytes");
            sb.AppendLine();

            for (int row = 0; row < buffer.Length; row += 16)
            {
                sb.Append($"Offset {row,4}: ");
                for (int col = 0; col < 16 && row + col < buffer.Length; col++)
                    sb.Append($"{buffer[row + col]:X2} ");

                sb.Append(" | ");
                for (int col = 0; col < 16 && row + col < buffer.Length; col += 4)
                {
                    if (row + col + 4 <= buffer.Length)
                    {
                        float f = BitConverter.ToSingle(buffer, row + col);
                        int i = BitConverter.ToInt32(buffer, row + col);
                        sb.Append($"[{i,11} / {f,12:F4}] ");
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine("=== Field interpretation (corrected offsets) ===");
            int offset = 0;
            sb.AppendLine($"Offset {offset,4}: PacketId     = {BitConverter.ToInt32(buffer, offset)}"); offset += 4;
            sb.AppendLine($"Offset {offset,4}: Gas          = {BitConverter.ToSingle(buffer, offset):F4}"); offset += 4;
            sb.AppendLine($"Offset {offset,4}: Brake        = {BitConverter.ToSingle(buffer, offset):F4}"); offset += 4;
            sb.AppendLine($"Offset {offset,4}: Fuel         = {BitConverter.ToSingle(buffer, offset):F4}"); offset += 4;
            sb.AppendLine($"Offset {offset,4}: Gear         = {BitConverter.ToInt32(buffer, offset)}"); offset += 4;
            sb.AppendLine($"Offset {offset,4}: Rpms         = {BitConverter.ToInt32(buffer, offset)}"); offset += 4;
            sb.AppendLine($"Offset {offset,4}: SteerAngle   = {BitConverter.ToSingle(buffer, offset):F6}"); offset += 4;
            sb.AppendLine($"Offset {offset,4}: Speed(m/s)   = {BitConverter.ToSingle(buffer, offset):F2} => {BitConverter.ToSingle(buffer, offset) * 3.6f:F1} km/h"); offset += 4;
            sb.AppendLine($"Offset {offset,4}: Velocity     = X:{BitConverter.ToSingle(buffer, offset):F2} Y:{BitConverter.ToSingle(buffer, offset+4):F2} Z:{BitConverter.ToSingle(buffer, offset+8):F2}"); offset += 12;
            sb.AppendLine($"Offset {offset,4}: AccG@struct  = X:{BitConverter.ToSingle(buffer, offset):F3} Y:{BitConverter.ToSingle(buffer, offset+4):F3} Z:{BitConverter.ToSingle(buffer, offset+8):F3}"); offset += 12;
            sb.AppendLine($"Offset {offset,4}: WheelSlip    = FL:{BitConverter.ToSingle(buffer, offset):F4} FR:{BitConverter.ToSingle(buffer, offset+4):F4} RL:{BitConverter.ToSingle(buffer, offset+8):F4} RR:{BitConverter.ToSingle(buffer, offset+12):F4}"); offset += 16;
            sb.AppendLine($"Offset {offset,4}: WheelLoad    = FL:{BitConverter.ToSingle(buffer, offset):F1} FR:{BitConverter.ToSingle(buffer, offset+4):F1} RL:{BitConverter.ToSingle(buffer, offset+8):F1} RR:{BitConverter.ToSingle(buffer, offset+12):F1}"); offset += 16;
            sb.AppendLine($"Offset    168: AccG@override = X:{BitConverter.ToSingle(buffer, 168):F3} Y:{BitConverter.ToSingle(buffer, 172):F3} Z:{BitConverter.ToSingle(buffer, 176):F3}");
            sb.AppendLine($"Offset    168: CamberRad    = FL:{BitConverter.ToSingle(buffer, 168):F4} FR:{BitConverter.ToSingle(buffer, 172):F4} RL:{BitConverter.ToSingle(buffer, 176):F4} RR:{BitConverter.ToSingle(buffer, 180):F4}");
            sb.AppendLine($"Offset   308: FinalFf       = {BitConverter.ToSingle(buffer, 308):F6}");
            sb.AppendLine($"Offset   580: P2pActivations= {BitConverter.ToInt32(buffer, 580)}");
            sb.AppendLine($"Offset   588: P2pStatus     = {BitConverter.ToInt32(buffer, 588)}");
            sb.AppendLine($"Offset   592: Mz            = FL:{BitConverter.ToSingle(buffer, 592):F4} FR:{BitConverter.ToSingle(buffer, 596):F4} RL:{BitConverter.ToSingle(buffer, 600):F4} RR:{BitConverter.ToSingle(buffer, 604):F4}");
            sb.AppendLine($"Offset   608: Fx            = FL:{BitConverter.ToSingle(buffer, 608):F4} FR:{BitConverter.ToSingle(buffer, 612):F4} RL:{BitConverter.ToSingle(buffer, 616):F4} RR:{BitConverter.ToSingle(buffer, 620):F4}");
            sb.AppendLine($"Offset   624: Fy            = FL:{BitConverter.ToSingle(buffer, 624):F4} FR:{BitConverter.ToSingle(buffer, 628):F4} RL:{BitConverter.ToSingle(buffer, 632):F4} RR:{BitConverter.ToSingle(buffer, 636):F4}");
            sb.AppendLine($"Offset   788: KerbVibration = {BitConverter.ToSingle(buffer, 788):F4}");
            sb.AppendLine($"Offset   792: SlipVibrations= {BitConverter.ToSingle(buffer, 792):F4}");
            sb.AppendLine($"Offset   796: RoadVibrations= {BitConverter.ToSingle(buffer, 796):F4}");
            sb.AppendLine($"Offset   800: AbsVibrations = {BitConverter.ToSingle(buffer, 800):F4}");
            sb.AppendLine($"");
            sb.AppendLine($"=== Struct vs Marshal comparison ===");
            sb.AppendLine($"Marshaled SteerAngle: {physics.SteerAngle:F6}");
            sb.AppendLine($"Marshaled SpeedKmh:   {physics.SpeedKmh:F2}");
            sb.AppendLine($"Marshaled FinalFf:    {physics.FinalFf:F6}");
            sb.AppendLine($"Marshaled Mz[0]:      {physics.Mz[0]:F4}  Mz[1]: {physics.Mz[1]:F4}");
            sb.AppendLine($"Marshaled Fx[0]:      {physics.Fx[0]:F4}  Fx[1]: {physics.Fx[1]:F4}");
            sb.AppendLine($"Marshaled Fy[0]:      {physics.Fy[0]:F4}  Fy[1]: {physics.Fy[1]:F4}");
            sb.AppendLine($"Marshaled KerbVib:    {physics.KerbVibration:F4}");
            sb.AppendLine($"Marshaled SlipVib:    {physics.SlipVibrations:F4}");
            sb.AppendLine($"Marshaled AccG[0]:    {physics.AccG[0]:F3}  [1]: {physics.AccG[1]:F3}  [2]: {physics.AccG[2]:F3}");
            sb.AppendLine($"Marshaled CamberRad:  FL:{physics.CamberRad[0]:F4} FR:{physics.CamberRad[1]:F4}");
            sb.AppendLine($"");
            sb.AppendLine($"=== POSITION DATA ===");
            sb.AppendLine($"Marshaled Heading:    {physics.Heading:F6}");
            sb.AppendLine($"Marshaled Pitch:      {physics.Pitch:F6}");
            sb.AppendLine($"Marshaled Roll:       {physics.Roll:F6}");
            sb.AppendLine($"Marshaled Velocity:   X:{physics.Velocity[0]:F2} Y:{physics.Velocity[1]:F2} Z:{physics.Velocity[2]:F2}");
            sb.AppendLine($"Marshaled TyreContactPoint[0]: X:{physics.TyreContactPoint[0].X:F2} Y:{physics.TyreContactPoint[0].Y:F2} Z:{physics.TyreContactPoint[0].Z:F2}");
            sb.AppendLine($"Marshaled TyreContactPoint[1]: X:{physics.TyreContactPoint[1].X:F2} Y:{physics.TyreContactPoint[1].Y:F2} Z:{physics.TyreContactPoint[1].Z:F2}");
            sb.AppendLine($"Marshaled TyreContactPoint[2]: X:{physics.TyreContactPoint[2].X:F2} Y:{physics.TyreContactPoint[2].Y:F2} Z:{physics.TyreContactPoint[2].Z:F2}");
            sb.AppendLine($"Marshaled TyreContactPoint[3]: X:{physics.TyreContactPoint[3].X:F2} Y:{physics.TyreContactPoint[3].Y:F2} Z:{physics.TyreContactPoint[3].Z:F2}");
            sb.AppendLine($"Marshaled LocalVelocity: X:{physics.LocalVelocity[0]:F2} Y:{physics.LocalVelocity[1]:F2} Z:{physics.LocalVelocity[2]:F2}");

            sb.AppendLine();
            sb.AppendLine("=== COORDINATE SCAN (float triples with |val| in [1, 10000] for at least 2 of 3) ===");
            ScanForCoordinates(sb, buffer, "Physics");

            Directory.CreateDirectory(Path.GetDirectoryName(dumpPath)!);
            File.WriteAllText(dumpPath, sb.ToString());
        }
        catch { }
    }

    public bool TryReadGraphics(out SPageFileGraphicEvo graphics)
    {
        graphics = default;
        if (_graphicsView == null) return false;

        try
        {
            int size = Marshal.SizeOf<SPageFileGraphicEvo>();
            byte[] buffer = new byte[size];
            _graphicsView.ReadArray(0, buffer, 0, size);

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                graphics = Marshal.PtrToStructure<SPageFileGraphicEvo>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }

            if (graphics.PacketId == _lastGraphicsPacketId)
                return false;

            if (graphics.PacketId % 100 == 0)
                DumpGraphicsBytes(buffer);

            if (buffer.Length >= 104)
            {
                graphics.FfbStrength = BitConverter.ToSingle(buffer, 96);
                graphics.CarFfbMultiplier = BitConverter.ToSingle(buffer, 100);
            }

            if (graphics.CarCoordinates == null || graphics.CarCoordinates.Length < 60)
                graphics.CarCoordinates = new StructVector3[60];

            int coordsBase = -1;
            for (int probe = 3120; probe <= 3128; probe += 4)
            {
                if (probe + 12 <= buffer.Length)
                {
                    float tX = BitConverter.ToSingle(buffer, probe);
                    float tY = BitConverter.ToSingle(buffer, probe + 4);
                    float tZ = BitConverter.ToSingle(buffer, probe + 8);
                    if (!float.IsNaN(tX) && !float.IsInfinity(tX) &&
                        !float.IsNaN(tZ) && !float.IsInfinity(tZ) &&
                        MathF.Abs(tX) > 10f && MathF.Abs(tX) < 10000f &&
                        MathF.Abs(tZ) > 10f && MathF.Abs(tZ) < 10000f)
                    {
                        coordsBase = probe;
                        break;
                    }
                }
            }

            if (coordsBase > 0)
            {
                for (int i = 0; i < Math.Min(60, (buffer.Length - coordsBase) / 12); i++)
                {
                    int off = coordsBase + i * 12;
                    if (off + 12 > buffer.Length) break;
                    float cx = BitConverter.ToSingle(buffer, off);
                    float cy = BitConverter.ToSingle(buffer, off + 4);
                    float cz = BitConverter.ToSingle(buffer, off + 8);
                    if (float.IsNaN(cx) || float.IsInfinity(cx)) break;
                    graphics.CarCoordinates[i] = new StructVector3 { X = cx, Y = cy, Z = cz };
                }
            }

            _lastGraphicsPacketId = graphics.PacketId;

            if (_lastGraphicsPacketId % 200 == 0)
            {
                try
                {
                    var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "AcEvoFfbTuner", "coords_debug.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:HH:mm:ss.fff}] Pkt={graphics.PacketId} coordsBase={coordsBase} " +
                        $"CarCoords[0]=({graphics.CarCoordinates[0].X:F2}, {graphics.CarCoordinates[0].Y:F2}, {graphics.CarCoordinates[0].Z:F2}) " +
                        $"Npos={graphics.Npos:F4} CarLoc={graphics.CarLocation} BufLen={buffer.Length}\n");
                }
                catch { }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DumpGraphicsBytes(byte[] buffer)
    {
        try
        {
            var dumpPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "graphics_hex_dump.log");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Graphics Hex Dump @ {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} (PacketId={BitConverter.ToInt32(buffer, 0)}) ===");
            sb.AppendLine($"Buffer size: {buffer.Length} bytes");
            sb.AppendLine();

            for (int row = 0; row < buffer.Length; row += 16)
            {
                sb.Append($"Offset {row,4}: ");
                for (int col = 0; col < 16 && row + col < buffer.Length; col++)
                    sb.Append($"{buffer[row + col]:X2} ");

                sb.Append(" | ");
                for (int col = 0; col < 16 && row + col < buffer.Length; col += 4)
                {
                    if (row + col + 4 <= buffer.Length)
                    {
                        float f = BitConverter.ToSingle(buffer, row + col);
                        int i = BitConverter.ToInt32(buffer, row + col);
                        sb.Append($"[{i,11} / {f,12:F4}] ");
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine("=== Field interpretation (struct layout) ===");
            int offset = 0;
            sb.AppendLine($"Offset {offset,4}: PacketId      = {BitConverter.ToInt32(buffer, offset)}"); offset += 4;
            sb.AppendLine($"Offset {offset,4}: Status        = {BitConverter.ToInt32(buffer, offset)}"); offset += 4;
            sb.AppendLine($"Offset {offset,4}: FocusedCarIdA = {BitConverter.ToUInt64(buffer, offset)}"); offset += 8;
            sb.AppendLine($"Offset {offset,4}: FocusedCarIdB = {BitConverter.ToUInt64(buffer, offset)}"); offset += 8;
            sb.AppendLine($"Offset {offset,4}: PlayerCarIdA  = {BitConverter.ToUInt64(buffer, offset)}"); offset += 8;
            sb.AppendLine($"Offset {offset,4}: PlayerCarIdB  = {BitConverter.ToUInt64(buffer, offset)}"); offset += 8;
            sb.AppendLine($"Offset {offset,4}: Rpm(ushort)   = {BitConverter.ToInt16(buffer, offset)}"); offset += 2;

            sb.AppendLine();
            sb.AppendLine("=== Scanning for SteerDegrees (int, expected 900) ===");
            for (int scan = 0; scan < Math.Min(buffer.Length - 4, 3860); scan++)
            {
                int val = BitConverter.ToInt32(buffer, scan);
                if (val >= 180 && val <= 1440 && val % 2 == 0)
                    sb.AppendLine($"  ** Offset {scan,4}: int = {val}");
            }

            sb.AppendLine();
            sb.AppendLine("=== Scanning for FfbStrength (float near 0.5-2.0) ===");
            for (int scan = 0; scan < Math.Min(buffer.Length - 4, 3860); scan += 4)
            {
                float val = BitConverter.ToSingle(buffer, scan);
                if (val >= 0.1f && val <= 3.0f && Math.Abs(val - Math.Round(val * 10) / 10) < 0.005f)
                    sb.AppendLine($"  ** Offset {scan,4}: float = {val:F4}  (possible FfbStrength/CarMult)");
            }

            sb.AppendLine();
            sb.AppendLine("=== Scanning for DisplaySpeedKmh (short, > 0) ===");
            for (int scan = 0; scan < Math.Min(buffer.Length - 2, 3860); scan++)
            {
                short val = BitConverter.ToInt16(buffer, scan);
                if (val > 10 && val < 400)
                    sb.AppendLine($"  ** Offset {scan,4}: short = {val}  (possible DisplaySpeedKmh)");
            }

            sb.AppendLine();
            sb.AppendLine("=== CarCoordinates Analysis ===");
            sb.AppendLine($"  Struct size:         {buffer.Length} bytes");
            sb.AppendLine($"  Marshal offset of CarCoordinates: {Marshal.OffsetOf<SPageFileGraphicEvo>("CarCoordinates")}");
            sb.AppendLine($"  Marshal size of StructVector3:    {Marshal.SizeOf<StructVector3>()}");

            long carCoordsOffset = Marshal.OffsetOf<SPageFileGraphicEvo>("CarCoordinates");
            if (carCoordsOffset + 12 <= buffer.Length)
            {
                sb.AppendLine($"  Reading CarCoordinates[0] from struct offset {carCoordsOffset}:");
                sb.AppendLine($"    X={BitConverter.ToSingle(buffer, (int)carCoordsOffset):F2}  " +
                              $"Y={BitConverter.ToSingle(buffer, (int)carCoordsOffset + 4):F2}  " +
                              $"Z={BitConverter.ToSingle(buffer, (int)carCoordsOffset + 8):F2}");
            }

            sb.AppendLine();
            sb.AppendLine("=== Scanning for CarCoordinates[0] (float triples with values > 1.0) ===");
            for (int scan = 0; scan < buffer.Length - 12; scan += 4)
            {
                float x = BitConverter.ToSingle(buffer, scan);
                float y = BitConverter.ToSingle(buffer, scan + 4);
                float z = BitConverter.ToSingle(buffer, scan + 8);
                if (Math.Abs(x) > 1f && Math.Abs(y) < 500f && Math.Abs(z) > 1f &&
                    !float.IsNaN(x) && !float.IsNaN(y) && !float.IsNaN(z) &&
                    !float.IsInfinity(x) && !float.IsInfinity(y) && !float.IsInfinity(z))
                {
                    float x2 = BitConverter.ToSingle(buffer, scan + 12);
                    float y2 = BitConverter.ToSingle(buffer, scan + 16);
                    float z2 = BitConverter.ToSingle(buffer, scan + 20);
                    if (Math.Abs(x2 - x) < 100f && Math.Abs(z2 - z) < 100f)
                    {
                        sb.AppendLine($"  ** Offset {scan,4}: [{x:F2}, {y:F2}, {z:F2}]  next=[{x2:F2}, {y2:F2}, {z2:F2}]");
                        if (sb.Length > 50000) break;
                    }
                }
            }

            sb.AppendLine();
            ScanForCoordinates(sb, buffer, "Graphics");

            Directory.CreateDirectory(Path.GetDirectoryName(dumpPath)!);
            File.WriteAllText(dumpPath, sb.ToString());
        }
        catch { }
    }

    private static readonly string StaticDumpPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "static_dump.log");

    public bool TryReadStatic(out SPageFileStaticEvo staticData)
    {
        staticData = default;
        if (_staticView == null) return false;

        try
        {
            int bufferSize = 4096;
            byte[] buffer = new byte[bufferSize];
            _staticView.ReadArray(0, buffer, 0, bufferSize);

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                staticData = Marshal.PtrToStructure<SPageFileStaticEvo>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }

            string trackName = StaticFieldReader.GetTrack(buffer);
            if (!string.IsNullOrEmpty(trackName))
            {
                var trackBytes = System.Text.Encoding.ASCII.GetBytes(trackName);
                Array.Clear(staticData.Track, 0, staticData.Track.Length);
                Array.Copy(trackBytes, staticData.Track, Math.Min(trackBytes.Length, staticData.Track.Length));
            }

            string trackConfig = StaticFieldReader.GetTrackConfiguration(buffer);
            if (!string.IsNullOrEmpty(trackConfig))
            {
                var configBytes = System.Text.Encoding.ASCII.GetBytes(trackConfig);
                Array.Clear(staticData.TrackConfiguration, 0, staticData.TrackConfiguration.Length);
                Array.Copy(configBytes, staticData.TrackConfiguration, Math.Min(configBytes.Length, staticData.TrackConfiguration.Length));
            }

            string sessionName = StaticFieldReader.GetSessionName(buffer);
            if (!string.IsNullOrEmpty(sessionName))
            {
                var sessBytes = System.Text.Encoding.ASCII.GetBytes(sessionName);
                Array.Clear(staticData.SessionName, 0, staticData.SessionName.Length);
                Array.Copy(sessBytes, staticData.SessionName, Math.Min(sessBytes.Length, staticData.SessionName.Length));
            }

            string nation = StaticFieldReader.GetNation(buffer);
            if (!string.IsNullOrEmpty(nation))
            {
                var natBytes = System.Text.Encoding.ASCII.GetBytes(nation);
                Array.Clear(staticData.Nation, 0, staticData.Nation.Length);
                Array.Copy(natBytes, staticData.Nation, Math.Min(natBytes.Length, staticData.Nation.Length));
            }

            staticData.TrackLengthM = StaticFieldReader.GetTrackLengthM(buffer);
            if (staticData.TrackLengthM == 0f)
                staticData.TrackLengthM = BitConverter.ToSingle(buffer, 195);

            DumpStaticBuffer(buffer, Marshal.SizeOf<SPageFileStaticEvo>());

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DumpStaticBuffer(byte[] buffer, int structSize)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Static Memory Dump ({buffer.Length} bytes, struct expects {structSize}) ===");
            sb.AppendLine($"Time: {DateTime.Now:HH:mm:ss.fff}");
            sb.AppendLine();

            int dumpLen = Math.Min(buffer.Length, 512);
            for (int offset = 0; offset < dumpLen; offset += 16)
            {
                sb.Append($"{offset:X4}: ");
                for (int i = 0; i < 16 && offset + i < dumpLen; i++)
                {
                    sb.Append($"{buffer[offset + i]:X2} ");
                }
                int hexLen = Math.Min(16, dumpLen - offset);
                sb.Append(' ', (16 - hexLen) * 3 + 2);
                for (int i = 0; i < 16 && offset + i < dumpLen; i++)
                {
                    byte b = buffer[offset + i];
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine("--- Decoded fields (corrected offsets from hex analysis) ---");
            sb.AppendLine($"  SmVersion:          '{DecodeField(buffer, 0, 15)}'");
            sb.AppendLine($"  AcEvoVersion:       '{DecodeField(buffer, 15, 15)}'");
            sb.AppendLine($"  Session:            {BitConverter.ToInt32(buffer, 30)}");
            sb.AppendLine($"  SessionName:        '{StaticFieldReader.GetSessionName(buffer)}'");
            sb.AppendLine($"  EventId:            {buffer[69]}");
            sb.AppendLine($"  SessionId:          {buffer[70]}");
            sb.AppendLine($"  NumberOfSessions:   {BitConverter.ToInt32(buffer, 84)}");
            sb.AppendLine($"  Nation:             '{StaticFieldReader.GetNation(buffer)}'");
            sb.AppendLine($"  Longitude:          {StaticFieldReader.GetLongitude(buffer):F4}");
            sb.AppendLine($"  Latitude:           {StaticFieldReader.GetLatitude(buffer):F4}");
            sb.AppendLine($"  Track:              '{StaticFieldReader.GetTrack(buffer)}'");
            sb.AppendLine($"  TrackConfig:        '{StaticFieldReader.GetTrackConfiguration(buffer)}'");
            sb.AppendLine($"  TrackLengthM:       {StaticFieldReader.GetTrackLengthM(buffer):F1}");

            sb.AppendLine();
            sb.AppendLine("--- String scan ---");
            for (int i = 0; i < dumpLen; i++)
            {
                if (buffer[i] >= 32 && buffer[i] < 127)
                {
                    int start = i;
                    while (i < dumpLen && buffer[i] >= 32 && buffer[i] < 127)
                        i++;
                    int len = i - start;
                    if (len >= 3)
                    {
                        string s = System.Text.Encoding.ASCII.GetString(buffer, start, len);
                        sb.AppendLine($"  Offset {start:X4} ({start:D4}): \"{s}\"");
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(StaticDumpPath)!);
            File.WriteAllText(StaticDumpPath, sb.ToString());
        }
        catch { }
    }

    private static string DecodeField(byte[] buffer, int offset, int len)
    {
        int end = Math.Min(offset + len, buffer.Length);
        int nullIdx = offset;
        while (nullIdx < end && buffer[nullIdx] != 0) nullIdx++;
        return System.Text.Encoding.ASCII.GetString(buffer, offset, nullIdx - offset);
    }

    private static void ScanForCoordinates(System.Text.StringBuilder sb, byte[] buffer, string label)
    {
        int count = 0;
        for (int i = 0; i <= buffer.Length - 12; i += 4)
        {
            float x = BitConverter.ToSingle(buffer, i);
            float y = BitConverter.ToSingle(buffer, i + 4);
            float z = BitConverter.ToSingle(buffer, i + 8);

            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) continue;
            if (float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z)) continue;

            int inRange = 0;
            if (MathF.Abs(x) is >= 1f and <= 50000f) inRange++;
            if (MathF.Abs(y) is >= 0.01f and <= 50000f) inRange++;
            if (MathF.Abs(z) is >= 1f and <= 50000f) inRange++;

            if (inRange >= 2)
            {
                sb.AppendLine($"  {label} offset {i,5} (0x{i:X4}): X={x,12:F2} Y={y,12:F2} Z={z,12:F2}");
                count++;
                if (count >= 60) break;
            }
        }
        if (count == 0)
            sb.AppendLine($"  {label}: No coordinate-like triples found");
    }

    public void Dispose()
    {
        Disconnect();
    }
}
