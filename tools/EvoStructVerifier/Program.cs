/// <summary>
/// Standalone AC EVO shared memory struct verifier.
///
/// Opens the game's physics MMF (Local\acpmf_physics), reads the raw buffer,
/// and dumps every field at the struct offset used by SPageFilePhysicsEvo.
///
/// Run from a built Tests project: dotnet run --project src/AcEvoFfbTuner.Tests
/// or copy into a standalone console app.
/// </summary>
using System.Runtime.InteropServices;
using System.IO.MemoryMappedFiles;
using System.Text;

// ─────────────────────────────────────────────────────────────────
// Mirror of the EVO physics struct — keep in sync with AcEvoPhysicsStruct.cs
// ─────────────────────────────────────────────────────────────────
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SPageFilePhysicsEvo
{
    public int PacketId;
    public float Gas;
    public float Brake;
    public float Fuel;
    public int Gear;
    public int Rpms;
    public float SteerAngle;
    public float SpeedKmh;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] Velocity;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] AccG;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] WheelSlip;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] WheelLoad;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] WheelsPressure;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] WheelAngularSpeed;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreWear;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreDirtyLevel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreCoreTemperature;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] CamberRad;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] SuspensionTravel;
    public float Drs;
    public float Tc;
    public float Heading;
    public float Pitch;
    public float Roll;
    public float CgHeight;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] public float[] CarDamage;
    public int NumberOfTyresOut;
    public int PitLimiterOn;
    public float Abs;
    public float KersCharge;
    public float KersInput;
    public int AutoShifterOn;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] RideHeight;
    public float TurboBoost;
    public float Ballast;
    public float AirDensity;
    public float AirTemp;
    public float RoadTemp;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] LocalAngularVel;
    public float FinalFf;
    public float PerformanceMeter;
    public int EngineBrake;
    public int ErsRecoveryLevel;
    public int ErsPowerLevel;
    public int ErsHeatCharging;
    public int ErsIsCharging;
    public float KersCurrentKj;
    public int DrsAvailable;
    public int DrsEnabled;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] BrakeTemp;
    public float Clutch;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreTempI;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreTempM;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreTempO;
    public int IsAiControlled;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public StructVector3[] TyreContactPoint;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public StructVector3[] TyreContactNormal;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public StructVector3[] TyreContactHeading;
    public float BrakeBias;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] LocalVelocity;
    public int P2pActivations;
    public int P2pStatus;
    public int CurrentMaxRpm;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] Mz;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] Fx;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] Fy;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] SlipRatio;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] SlipAngle;
    public int TcinAction;
    public int AbsInAction;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] SuspensionDamage;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreTemp;
    public float WaterTemp;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] BrakeTorque;
    public int FrontBrakeCompound;
    public int RearBrakeCompound;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] PadLife;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] DiscLife;
    public int IgnitionOn;
    public int StarterEngineOn;
    public int IsEngineRunning;
    public float KerbVibration;
    public float SlipVibrations;
    public float RoadVibrations;
    public float AbsVibrations;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct StructVector3
{
    public float X;
    public float Y;
    public float Z;
}

// ─────────────────────────────────────────────────────────────────
// Field descriptor for offset-by-offset decoding
// ─────────────────────────────────────────────────────────────────
struct FieldDef
{
    public string Name;
    public int Offset;
    public int Size;  // bytes
    public string Type; // "int", "float", "float[4]", "vec3", "int[4]"

    public FieldDef(string name, int offset, int size, string type)
    {
        Name = name;
        Offset = offset;
        Size = size;
        Type = type;
    }
}

public sealed class Program
{
    private const string PhysicsMapName = @"Local\acevo_pmf_physics";
    private const int StructSize = 800;

    private static readonly FieldDef[] Fields = new FieldDef[]
    {
        new("PacketId",         0, 4, "int"),
        new("Gas",              4, 4, "float"),
        new("Brake",            8, 4, "float"),
        new("Fuel",            12, 4, "float"),
        new("Gear",            16, 4, "int"),
        new("Rpms",            20, 4, "int"),
        new("SteerAngle",      24, 4, "float"),
        new("SpeedKmh",        28, 4, "float"),
        new("Velocity",        32, 12, "vec3"),
        new("AccG",            44, 12, "vec3"),
        new("WheelSlip",       56, 16, "float[4]"),
        new("WheelLoad",       72, 16, "float[4]"),
        new("WheelsPressure",  88, 16, "float[4]"),
        new("WheelAngularSpeed", 104, 16, "float[4]"),
        new("TyreWear",       120, 16, "float[4]"),
        new("TyreDirtyLevel", 136, 16, "float[4]"),
        new("TyreCoreTemperature", 152, 16, "float[4]"),
        new("CamberRad",      168, 16, "float[4]"),
        new("SuspensionTravel", 184, 16, "float[4]"),
        new("Drs",            200, 4, "float"),
        new("Tc",             204, 4, "float"),
        new("Heading",        208, 4, "float"),
        new("Pitch",          212, 4, "float"),
        new("Roll",           216, 4, "float"),
        new("CgHeight",       220, 4, "float"),
        new("CarDamage",      224, 20, "float[5]"),
        new("NumberOfTyresOut", 244, 4, "int"),
        new("PitLimiterOn",   248, 4, "int"),
        new("Abs",            252, 4, "float"),
        new("KersCharge",     256, 4, "float"),
        new("KersInput",      260, 4, "float"),
        new("AutoShifterOn",  264, 4, "int"),
        new("RideHeight",     268, 8, "float[2]"),
        new("TurboBoost",     276, 4, "float"),
        new("Ballast",        280, 4, "float"),
        new("AirDensity",     284, 4, "float"),
        new("AirTemp",        288, 4, "float"),
        new("RoadTemp",       292, 4, "float"),
        new("LocalAngularVel", 296, 12, "vec3"),
        new("FinalFf",        308, 4, "float"),
        new("PerformanceMeter", 312, 4, "float"),
        new("EngineBrake",    316, 4, "int"),
        new("ErsRecoveryLevel", 320, 4, "int"),
        new("ErsPowerLevel",  324, 4, "int"),
        new("ErsHeatCharging", 328, 4, "int"),
        new("ErsIsCharging",  332, 4, "int"),
        new("KersCurrentKj",  336, 4, "float"),
        new("DrsAvailable",   340, 4, "int"),
        new("DrsEnabled",     344, 4, "int"),
        new("BrakeTemp",      348, 16, "float[4]"),
        new("Clutch",         364, 4, "float"),
        new("TyreTempI",      368, 16, "float[4]"),
        new("TyreTempM",      384, 16, "float[4]"),
        new("TyreTempO",      400, 16, "float[4]"),
        new("IsAiControlled", 416, 4, "int"),
        new("TyreContactPoint", 420, 48, "vec3[4]"),
        new("TyreContactNormal", 468, 48, "vec3[4]"),
        new("TyreContactHeading", 516, 48, "vec3[4]"),
        new("BrakeBias",      564, 4, "float"),
        new("LocalVelocity",  568, 12, "vec3"),
        new("P2pActivations", 580, 4, "int"),
        new("P2pStatus",      584, 4, "int"),
        new("CurrentMaxRpm",  588, 4, "int"),
        new("Mz",             592, 16, "float[4]"),
        new("Fx",             608, 16, "float[4]"),
        new("Fy",             624, 16, "float[4]"),
        new("SlipRatio",      640, 16, "float[4]"),
        new("SlipAngle",      656, 16, "float[4]"),
        new("TcinAction",     672, 4, "int"),
        new("AbsInAction",    676, 4, "int"),
        new("SuspensionDamage", 680, 16, "float[4]"),
        new("TyreTemp",       696, 16, "float[4]"),
        new("WaterTemp",      712, 4, "float"),
        new("BrakeTorque",    716, 16, "float[4]"),
        new("FrontBrakeCompound", 732, 4, "int"),
        new("RearBrakeCompound", 736, 4, "int"),
        new("PadLife",        740, 16, "float[4]"),
        new("DiscLife",       756, 16, "float[4]"),
        new("IgnitionOn",     772, 4, "int"),
        new("StarterEngineOn", 776, 4, "int"),
        new("IsEngineRunning", 780, 4, "int"),
        new("KerbVibration",  784, 4, "float"),
        new("SlipVibrations", 788, 4, "float"),
        new("RoadVibrations", 792, 4, "float"),
        new("AbsVibrations",  796, 4, "float"),
    };

    public static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== AC EVO Shared Memory Struct Dumper ===");
        Console.WriteLine($"Struct size: {Marshal.SizeOf<SPageFilePhysicsEvo>()} bytes");
        Console.WriteLine($"MMF: {PhysicsMapName}");
        Console.WriteLine();

        // ── Read raw buffer ──────────────────────────────────────
        byte[] buffer;
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(PhysicsMapName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            int size = Marshal.SizeOf<SPageFilePhysicsEvo>();
            buffer = new byte[size];
            view.ReadArray(0, buffer, 0, size);
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine("ERROR: AC EVO shared memory not found.");
            Console.WriteLine("Make sure AC EVO is running.");
            Console.WriteLine($"Expected MMF: {PhysicsMapName}");
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR opening shared memory: {ex.Message}");
            return;
        }

        // ── Hex dump ──────────────────────────────────────────
        Console.WriteLine($"Buffer size: {buffer.Length} bytes");
        Console.WriteLine();
        for (int row = 0; row < buffer.Length; row += 16)
        {
            Console.Write($"Offset {row,4}: ");
            for (int col = 0; col < 16 && row + col < buffer.Length; col++)
                Console.Write($"{buffer[row + col]:X2} ");
            Console.Write("  ");
            for (int col = 0; col < 16 && row + col < buffer.Length; col++)
            {
                char c = (char)buffer[row + col];
                Console.Write(char.IsControl(c) ? '.' : c);
            }
            Console.WriteLine();
        }

        // ── Field interpreter ─────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("=== FIELD INTERPRETATION ===");
        foreach (var f in Fields)
        {
            if (f.Offset + f.Size > buffer.Length) continue;
            string value = DecodeField(buffer, f);
            Console.WriteLine($"  [{f.Offset,4}-{f.Offset + f.Size - 1,4}] {f.Name,-24} {value}");
        }

        // ── Marshal.PtrToStructure comparison ─────────────────
        Console.WriteLine();
        Console.WriteLine("=== MARSHAL vs RAW COMPARISON (key fields) ===");
        try
        {
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            var phys = Marshal.PtrToStructure<SPageFilePhysicsEvo>(handle.AddrOfPinnedObject());
            handle.Free();

            Console.WriteLine($"  PacketId:           Marshaled={phys.PacketId}  Raw={BitConverter.ToInt32(buffer, 0)}");
            Console.WriteLine($"  Gas:                Marshaled={phys.Gas:F4}  Raw={BitConverter.ToSingle(buffer, 4):F4}");
            Console.WriteLine($"  SteerAngle:         Marshaled={phys.SteerAngle:F4}  Raw={BitConverter.ToSingle(buffer, 24):F4}");
            Console.WriteLine($"  SpeedKmh:           Marshaled={phys.SpeedKmh:F2}  Raw={BitConverter.ToSingle(buffer, 28):F2}");
            Console.WriteLine($"  FinalFf:            Marshaled={phys.FinalFf:F4}  Raw={BitConverter.ToSingle(buffer, 308):F4}");
            Console.WriteLine($"  CurrentMaxRpm:      Marshaled={phys.CurrentMaxRpm}  Raw={BitConverter.ToInt32(buffer, 588)}");
            Console.WriteLine($"  Mz[0] (FL):         Marshaled={phys.Mz?[0]:F4}  Raw={BitConverter.ToSingle(buffer, 592):F4}");
            Console.WriteLine($"  Mz[1] (FR):         Marshaled={phys.Mz?[1]:F4}  Raw={BitConverter.ToSingle(buffer, 596):F4}");
            Console.WriteLine($"  Mz[2] (RL):         Marshaled={phys.Mz?[2]:F4}  Raw={BitConverter.ToSingle(buffer, 600):F4}");
            Console.WriteLine($"  Mz[3] (RR):         Marshaled={phys.Mz?[3]:F4}  Raw={BitConverter.ToSingle(buffer, 604):F4}");
            Console.WriteLine($"  Fx[0] (FL):         Marshaled={phys.Fx?[0]:F4}  Raw={BitConverter.ToSingle(buffer, 608):F4}");
            Console.WriteLine($"  Fx[1] (FR):         Marshaled={phys.Fx?[1]:F4}  Raw={BitConverter.ToSingle(buffer, 612):F4}");
            Console.WriteLine($"  Fy[0] (FL):         Marshaled={phys.Fy?[0]:F4}  Raw={BitConverter.ToSingle(buffer, 624):F4}");
            Console.WriteLine($"  Fy[1] (FR):         Marshaled={phys.Fy?[1]:F4}  Raw={BitConverter.ToSingle(buffer, 628):F4}");
            Console.WriteLine($"  WheelLoad[0] (FL):  Marshaled={phys.WheelLoad?[0]:F1}  Raw={BitConverter.ToSingle(buffer, 72):F1}");
            Console.WriteLine($"  KerbVibration:      Marshaled={phys.KerbVibration:F4}  Raw={BitConverter.ToSingle(buffer, 784):F4}");
            Console.WriteLine($"  AbsVibration:       Marshaled={phys.AbsVibrations:F4}  Raw={BitConverter.ToSingle(buffer, 796):F4}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR during marshal: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("=== MZ OFFSET SCAN (570-650) ===");
        Console.WriteLine("Scanning every 4 bytes for float values in Mz region:");
        for (int off = 570; off < 650; off += 4)
        {
            float val = BitConverter.ToSingle(buffer, off);
            string field = off switch
            {
                580 => "  ← P2pActivations",
                584 => "  ← P2pStatus",
                588 => "  ← CurrentMaxRpm",
                592 => "  ← Mz[0] FL",
                596 => "  ← Mz[1] FR",
                600 => "  ← Mz[2] RL",
                604 => "  ← Mz[3] RR",
                608 => "  ← Fx[0] FL",
                612 => "  ← Fx[1] FR",
                616 => "  ← Fx[2] RL",
                620 => "  ← Fx[3] RR",
                624 => "  ← Fy[0] FL",
                628 => "  ← Fy[1] FR",
                632 => "  ← Fy[2] RL",
                636 => "  ← Fy[3] RR",
                _ => ""
            };
            Console.WriteLine($"  Offset {off,3}: {val,12:F6}{field}");
        }

        Console.WriteLine();
        Console.WriteLine("=== SESSION INFO ===");
        float speedMs = BitConverter.ToSingle(buffer, 28);
        Console.WriteLine($"  Speed:      {speedMs * 3.6f:F1} km/h");
        Console.WriteLine($"  Steer:      {BitConverter.ToSingle(buffer, 24):F4} rad ({(BitConverter.ToSingle(buffer, 24) * 57.2958f):F1}°)");
        Console.WriteLine($"  PacketId:   {BitConverter.ToInt32(buffer, 0)}");
        Console.WriteLine($"  Rpms:       {BitConverter.ToInt32(buffer, 20)}");
        Console.WriteLine($"  Gear:       {BitConverter.ToInt32(buffer, 16)}");
        Console.WriteLine($"  Gas:        {BitConverter.ToSingle(buffer, 4):F3}");
        Console.WriteLine($"  Brake:      {BitConverter.ToSingle(buffer, 8):F3}");
        Console.WriteLine();
        Console.WriteLine("Done.");
    }

    private static string DecodeField(byte[] buffer, FieldDef f)
    {
        try
        {
            switch (f.Type)
            {
                case "int":
                    return BitConverter.ToInt32(buffer, f.Offset).ToString();

                case "float":
                    return $"{BitConverter.ToSingle(buffer, f.Offset):F6}";

                case "float[4]":
                    var f0 = BitConverter.ToSingle(buffer, f.Offset);
                    var f1 = BitConverter.ToSingle(buffer, f.Offset + 4);
                    var f2 = BitConverter.ToSingle(buffer, f.Offset + 8);
                    var f3 = BitConverter.ToSingle(buffer, f.Offset + 12);
                    return $"FL:{f0:F4} FR:{f1:F4} RL:{f2:F4} RR:{f3:F4}";

                case "float[5]":
                    var d0 = BitConverter.ToSingle(buffer, f.Offset);
                    var d1 = BitConverter.ToSingle(buffer, f.Offset + 4);
                    var d2 = BitConverter.ToSingle(buffer, f.Offset + 8);
                    var d3 = BitConverter.ToSingle(buffer, f.Offset + 12);
                    var d4 = BitConverter.ToSingle(buffer, f.Offset + 16);
                    return $"{d0:F4} {d1:F4} {d2:F4} {d3:F4} {d4:F4}";

                case "float[2]":
                    var r0 = BitConverter.ToSingle(buffer, f.Offset);
                    var r1 = BitConverter.ToSingle(buffer, f.Offset + 4);
                    return $"{r0:F4} {r1:F4}";

                case "vec3":
                    var x = BitConverter.ToSingle(buffer, f.Offset);
                    var y = BitConverter.ToSingle(buffer, f.Offset + 4);
                    var z = BitConverter.ToSingle(buffer, f.Offset + 8);
                    return $"X:{x:F4} Y:{y:F4} Z:{z:F4}";

                case "vec3[4]":
                    var sb = new StringBuilder();
                    for (int i = 0; i < 4; i++)
                    {
                        int off = f.Offset + i * 12;
                        var vx = BitConverter.ToSingle(buffer, off);
                        var vy = BitConverter.ToSingle(buffer, off + 4);
                        var vz = BitConverter.ToSingle(buffer, off + 8);
                        if (i > 0) sb.Append("  ");
                        sb.Append($"W{i}:({vx:F2},{vy:F2},{vz:F2})");
                    }
                    return sb.ToString();

                default:
                    return "?";
            }
        }
        catch
        {
            return "ERR";
        }
    }
}
