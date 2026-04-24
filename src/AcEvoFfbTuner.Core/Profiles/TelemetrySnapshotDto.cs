using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace AcEvoFfbTuner.Core.Profiles;

public sealed class TelemetrySnapshotDto
{
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;

    public PhysicsSnapshotDto Physics { get; set; } = new();
    public GraphicsSnapshotDto Graphics { get; set; } = new();
    public StaticSnapshotDto Static { get; set; } = new();

    public static TelemetrySnapshotDto Capture(
        SPageFilePhysicsEvo physics,
        SPageFileGraphicEvo graphics,
        SPageFileStaticEvo staticData)
    {
        return new TelemetrySnapshotDto
        {
            CapturedAtUtc = DateTime.UtcNow,
            Physics = PhysicsSnapshotDto.FromStruct(physics),
            Graphics = GraphicsSnapshotDto.FromStruct(graphics),
            Static = StaticSnapshotDto.FromStruct(staticData)
        };
    }
}

public sealed class PhysicsSnapshotDto
{
    public int PacketId { get; set; }
    public float Gas { get; set; }
    public float Brake { get; set; }
    public float Fuel { get; set; }
    public int Gear { get; set; }
    public int Rpms { get; set; }
    public float SteerAngle { get; set; }
    public float SpeedKmh { get; set; }
    public float[] Velocity { get; set; } = [];
    public float[] AccG { get; set; } = [];
    public float[] WheelSlip { get; set; } = [];
    public float[] WheelLoad { get; set; } = [];
    public float[] WheelsPressure { get; set; } = [];
    public float[] WheelAngularSpeed { get; set; } = [];
    public float[] TyreWear { get; set; } = [];
    public float[] TyreDirtyLevel { get; set; } = [];
    public float[] TyreCoreTemperature { get; set; } = [];
    public float[] CamberRad { get; set; } = [];
    public float[] SuspensionTravel { get; set; } = [];
    public float Drs { get; set; }
    public float Tc { get; set; }
    public float Heading { get; set; }
    public float Pitch { get; set; }
    public float Roll { get; set; }
    public float CgHeight { get; set; }
    public float[] CarDamage { get; set; } = [];
    public int NumberOfTyresOut { get; set; }
    public int PitLimiterOn { get; set; }
    public float Abs { get; set; }
    public float KersCharge { get; set; }
    public float KersInput { get; set; }
    public int AutoShifterOn { get; set; }
    public float[] RideHeight { get; set; } = [];
    public float TurboBoost { get; set; }
    public float Ballast { get; set; }
    public float AirDensity { get; set; }
    public float AirTemp { get; set; }
    public float RoadTemp { get; set; }
    public float[] LocalAngularVel { get; set; } = [];
    public float FinalFf { get; set; }
    public float PerformanceMeter { get; set; }
    public int EngineBrake { get; set; }
    public int ErsRecoveryLevel { get; set; }
    public int ErsPowerLevel { get; set; }
    public int ErsHeatCharging { get; set; }
    public int ErsIsCharging { get; set; }
    public float KersCurrentKj { get; set; }
    public int DrsAvailable { get; set; }
    public int DrsEnabled { get; set; }
    public float[] BrakeTemp { get; set; } = [];
    public float Clutch { get; set; }
    public float[] TyreTempI { get; set; } = [];
    public float[] TyreTempM { get; set; } = [];
    public float[] TyreTempO { get; set; } = [];
    public int IsAiControlled { get; set; }
    public Vector3Dto[] TyreContactPoint { get; set; } = [];
    public Vector3Dto[] TyreContactNormal { get; set; } = [];
    public Vector3Dto[] TyreContactHeading { get; set; } = [];
    public float BrakeBias { get; set; }
    public float[] LocalVelocity { get; set; } = [];
    public int P2pActivations { get; set; }
    public int P2pStatus { get; set; }
    public int CurrentMaxRpm { get; set; }
    public float[] Mz { get; set; } = [];
    public float[] Fx { get; set; } = [];
    public float[] Fy { get; set; } = [];
    public float[] SlipRatio { get; set; } = [];
    public float[] SlipAngle { get; set; } = [];
    public int TcInAction { get; set; }
    public int AbsInAction { get; set; }
    public float[] SuspensionDamage { get; set; } = [];
    public float[] TyreTemp { get; set; } = [];
    public float WaterTemp { get; set; }
    public float[] BrakeTorque { get; set; } = [];
    public int FrontBrakeCompound { get; set; }
    public int RearBrakeCompound { get; set; }
    public float[] PadLife { get; set; } = [];
    public float[] DiscLife { get; set; } = [];
    public int IgnitionOn { get; set; }
    public int StarterEngineOn { get; set; }
    public int IsEngineRunning { get; set; }
    public float KerbVibration { get; set; }
    public float SlipVibrations { get; set; }
    public float RoadVibrations { get; set; }
    public float AbsVibrations { get; set; }

    public static PhysicsSnapshotDto FromStruct(SPageFilePhysicsEvo p)
    {
        return new PhysicsSnapshotDto
        {
            PacketId = p.PacketId,
            Gas = p.Gas,
            Brake = p.Brake,
            Fuel = p.Fuel,
            Gear = p.Gear,
            Rpms = p.Rpms,
            SteerAngle = p.SteerAngle,
            SpeedKmh = p.SpeedKmh,
            Velocity = CopyArr(p.Velocity),
            AccG = CopyArr(p.AccG),
            WheelSlip = CopyArr(p.WheelSlip),
            WheelLoad = CopyArr(p.WheelLoad),
            WheelsPressure = CopyArr(p.WheelsPressure),
            WheelAngularSpeed = CopyArr(p.WheelAngularSpeed),
            TyreWear = CopyArr(p.TyreWear),
            TyreDirtyLevel = CopyArr(p.TyreDirtyLevel),
            TyreCoreTemperature = CopyArr(p.TyreCoreTemperature),
            CamberRad = CopyArr(p.CamberRad),
            SuspensionTravel = CopyArr(p.SuspensionTravel),
            Drs = p.Drs,
            Tc = p.Tc,
            Heading = p.Heading,
            Pitch = p.Pitch,
            Roll = p.Roll,
            CgHeight = p.CgHeight,
            CarDamage = CopyArr(p.CarDamage),
            NumberOfTyresOut = p.NumberOfTyresOut,
            PitLimiterOn = p.PitLimiterOn,
            Abs = p.Abs,
            KersCharge = p.KersCharge,
            KersInput = p.KersInput,
            AutoShifterOn = p.AutoShifterOn,
            RideHeight = CopyArr(p.RideHeight),
            TurboBoost = p.TurboBoost,
            Ballast = p.Ballast,
            AirDensity = p.AirDensity,
            AirTemp = p.AirTemp,
            RoadTemp = p.RoadTemp,
            LocalAngularVel = CopyArr(p.LocalAngularVel),
            FinalFf = p.FinalFf,
            PerformanceMeter = p.PerformanceMeter,
            EngineBrake = p.EngineBrake,
            ErsRecoveryLevel = p.ErsRecoveryLevel,
            ErsPowerLevel = p.ErsPowerLevel,
            ErsHeatCharging = p.ErsHeatCharging,
            ErsIsCharging = p.ErsIsCharging,
            KersCurrentKj = p.KersCurrentKj,
            DrsAvailable = p.DrsAvailable,
            DrsEnabled = p.DrsEnabled,
            BrakeTemp = CopyArr(p.BrakeTemp),
            Clutch = p.Clutch,
            TyreTempI = CopyArr(p.TyreTempI),
            TyreTempM = CopyArr(p.TyreTempM),
            TyreTempO = CopyArr(p.TyreTempO),
            IsAiControlled = p.IsAiControlled,
            TyreContactPoint = CopyVecArr(p.TyreContactPoint),
            TyreContactNormal = CopyVecArr(p.TyreContactNormal),
            TyreContactHeading = CopyVecArr(p.TyreContactHeading),
            BrakeBias = p.BrakeBias,
            LocalVelocity = CopyArr(p.LocalVelocity),
            P2pActivations = p.P2pActivations,
            P2pStatus = p.P2pStatus,
            CurrentMaxRpm = p.CurrentMaxRpm,
            Mz = CopyArr(p.Mz),
            Fx = CopyArr(p.Fx),
            Fy = CopyArr(p.Fy),
            SlipRatio = CopyArr(p.SlipRatio),
            SlipAngle = CopyArr(p.SlipAngle),
            TcInAction = p.TcinAction,
            AbsInAction = p.AbsInAction,
            SuspensionDamage = CopyArr(p.SuspensionDamage),
            TyreTemp = CopyArr(p.TyreTemp),
            WaterTemp = p.WaterTemp,
            BrakeTorque = CopyArr(p.BrakeTorque),
            FrontBrakeCompound = p.FrontBrakeCompound,
            RearBrakeCompound = p.RearBrakeCompound,
            PadLife = CopyArr(p.PadLife),
            DiscLife = CopyArr(p.DiscLife),
            IgnitionOn = p.IgnitionOn,
            StarterEngineOn = p.StarterEngineOn,
            IsEngineRunning = p.IsEngineRunning,
            KerbVibration = p.KerbVibration,
            SlipVibrations = p.SlipVibrations,
            RoadVibrations = p.RoadVibrations,
            AbsVibrations = p.AbsVibrations
        };
    }

    private static float[] CopyArr(float[]? src) => src != null ? (float[])src.Clone() : [];
    private static Vector3Dto[] CopyVecArr(StructVector3[]? src) =>
        src != null ? src.Select(v => new Vector3Dto { X = v.X, Y = v.Y, Z = v.Z }).ToArray() : [];
}

public sealed class GraphicsSnapshotDto
{
    public int PacketId { get; set; }
    public string Status { get; set; } = "";
    public ushort Rpm { get; set; }
    public bool IsRpmLimiterOn { get; set; }
    public bool IsChangeUpRpm { get; set; }
    public bool IsChangeDownRpm { get; set; }
    public bool TcActive { get; set; }
    public bool AbsActive { get; set; }
    public bool EscActive { get; set; }
    public bool LaunchActive { get; set; }
    public bool IsIgnitionOn { get; set; }
    public bool IsEngineRunning { get; set; }
    public bool KersIsCharging { get; set; }
    public bool IsWrongWay { get; set; }
    public bool IsDrsAvailable { get; set; }
    public bool BatteryIsCharging { get; set; }
    public short DisplaySpeedKmh { get; set; }
    public short DisplaySpeedMph { get; set; }
    public short DisplaySpeedMs { get; set; }
    public short GearInt { get; set; }
    public float RpmPercent { get; set; }
    public float GasPercent { get; set; }
    public float BrakePercent { get; set; }
    public float HandbrakePercent { get; set; }
    public float ClutchPercent { get; set; }
    public float SteeringPercent { get; set; }
    public float FfbStrength { get; set; }
    public float CarFfbMultiplier { get; set; }
    public float WaterTemperaturePercent { get; set; }
    public float WaterPressureBar { get; set; }
    public float FuelPressureBar { get; set; }
    public sbyte WaterTemperatureC { get; set; }
    public sbyte AirTemperatureC { get; set; }
    public float OilTemperatureC { get; set; }
    public float OilPressureBar { get; set; }
    public float ExhaustTemperatureC { get; set; }
    public float GForcesX { get; set; }
    public float GForcesY { get; set; }
    public float GForcesZ { get; set; }
    public float TurboBoost { get; set; }
    public float TurboBoostLevel { get; set; }
    public float TurboBoostPerc { get; set; }
    public int SteerDegrees { get; set; }
    public float CurrentKm { get; set; }
    public uint TotalKm { get; set; }
    public uint TotalDrivingTimeS { get; set; }
    public int TimeOfDayHours { get; set; }
    public int TimeOfDayMinutes { get; set; }
    public int TimeOfDaySeconds { get; set; }
    public int DeltaTimeMs { get; set; }
    public int CurrentLapTimeMs { get; set; }
    public int PredictedLapTimeMs { get; set; }
    public float FuelLiterCurrentQuantity { get; set; }
    public float FuelLiterCurrentQuantityPercent { get; set; }
    public float FuelLiterPerKm { get; set; }
    public float KmPerFuelLiter { get; set; }
    public float CurrentTorque { get; set; }
    public int CurrentBhp { get; set; }
    public TyreStateDto TyreLf { get; set; } = new();
    public TyreStateDto TyreRf { get; set; } = new();
    public TyreStateDto TyreLr { get; set; } = new();
    public TyreStateDto TyreRr { get; set; } = new();
    public float Npos { get; set; }
    public float KersChargePerc { get; set; }
    public float KersCurrentPerc { get; set; }
    public float ControlLockTime { get; set; }
    public DamageStateDto CarDamage { get; set; } = new();
    public string CarLocation { get; set; } = "";
    public PitInfoDto PitInfo { get; set; } = new();
    public float FuelLiterUsed { get; set; }
    public float FuelLiterPerLap { get; set; }
    public float LapsPossibleWithFuel { get; set; }
    public float BatteryTemperature { get; set; }
    public float BatteryVoltage { get; set; }
    public float InstantaneousFuelLiterPerKm { get; set; }
    public float InstantaneousKmPerFuelLiter { get; set; }
    public float GearRpmWindow { get; set; }
    public InstrumentationDto Instrumentation { get; set; } = new();
    public ElectronicsDto Electronics { get; set; } = new();
    public int TotalLapCount { get; set; }
    public uint CurrentPos { get; set; }
    public uint TotalDrivers { get; set; }
    public int LastLaptimeMs { get; set; }
    public int BestLaptimeMs { get; set; }
    public string Flag { get; set; } = "";
    public string GlobalFlag { get; set; } = "";
    public uint MaxGears { get; set; }
    public string EngineType { get; set; } = "";
    public bool HasKers { get; set; }
    public bool IsLastLap { get; set; }
    public string PerformanceModeName { get; set; } = "";
    public float DiffCoastRawValue { get; set; }
    public float DiffPowerRawValue { get; set; }
    public int RaceCutGainedTimeMs { get; set; }
    public int DistanceToDeadline { get; set; }
    public float RaceCutCurrentDelta { get; set; }
    public SessionStateDto SessionState { get; set; } = new();
    public TimingStateDto TimingState { get; set; } = new();
    public int PlayerPing { get; set; }
    public int PlayerLatency { get; set; }
    public int PlayerCpuUsage { get; set; }
    public int PlayerCpuUsageAvg { get; set; }
    public int PlayerQos { get; set; }
    public int PlayerQosAvg { get; set; }
    public int PlayerFps { get; set; }
    public int PlayerFpsAvg { get; set; }
    public string DriverName { get; set; } = "";
    public string DriverSurname { get; set; } = "";
    public string CarModel { get; set; } = "";
    public bool IsInPitBox { get; set; }
    public bool IsInPitLane { get; set; }
    public bool IsValidLap { get; set; }
    public float GapAhead { get; set; }
    public float GapBehind { get; set; }
    public byte ActiveCars { get; set; }
    public float FuelPerLap { get; set; }
    public float FuelEstimatedLaps { get; set; }
    public AssistsStateDto AssistsState { get; set; } = new();
    public float MaxFuel { get; set; }
    public float MaxTurboBoost { get; set; }
    public bool UseSingleCompound { get; set; }

    public static GraphicsSnapshotDto FromStruct(SPageFileGraphicEvo g)
    {
        return new GraphicsSnapshotDto
        {
            PacketId = g.PacketId,
            Status = g.Status.ToString(),
            Rpm = g.Rpm,
            IsRpmLimiterOn = g.IsRpmLimiterOn,
            IsChangeUpRpm = g.IsChangeUpRpm,
            IsChangeDownRpm = g.IsChangeDownRpm,
            TcActive = g.TcActive,
            AbsActive = g.AbsActive,
            EscActive = g.EscActive,
            LaunchActive = g.LaunchActive,
            IsIgnitionOn = g.IsIgnitionOn,
            IsEngineRunning = g.IsEngineRunning,
            KersIsCharging = g.KersIsCharging,
            IsWrongWay = g.IsWrongWay,
            IsDrsAvailable = g.IsDrsAvailable,
            BatteryIsCharging = g.BatteryIsCharging,
            DisplaySpeedKmh = g.DisplaySpeedKmh,
            DisplaySpeedMph = g.DisplaySpeedMph,
            DisplaySpeedMs = g.DisplaySpeedMs,
            GearInt = g.GearInt,
            RpmPercent = g.RpmPercent,
            GasPercent = g.GasPercent,
            BrakePercent = g.BrakePercent,
            HandbrakePercent = g.HandbrakePercent,
            ClutchPercent = g.ClutchPercent,
            SteeringPercent = g.SteeringPercent,
            FfbStrength = g.FfbStrength,
            CarFfbMultiplier = g.CarFfbMultiplier,
            WaterTemperaturePercent = g.WaterTemperaturePercent,
            WaterPressureBar = g.WaterPressureBar,
            FuelPressureBar = g.FuelPressureBar,
            WaterTemperatureC = g.WaterTemperatureC,
            AirTemperatureC = g.AirTemperatureC,
            OilTemperatureC = g.OilTemperatureC,
            OilPressureBar = g.OilPressureBar,
            ExhaustTemperatureC = g.ExhaustTemperatureC,
            GForcesX = g.GForcesX,
            GForcesY = g.GForcesY,
            GForcesZ = g.GForcesZ,
            TurboBoost = g.TurboBoost,
            TurboBoostLevel = g.TurboBoostLevel,
            TurboBoostPerc = g.TurboBoostPerc,
            SteerDegrees = g.SteerDegrees,
            CurrentKm = g.CurrentKm,
            TotalKm = g.TotalKm,
            TotalDrivingTimeS = g.TotalDrivingTimeS,
            TimeOfDayHours = g.TimeOfDayHours,
            TimeOfDayMinutes = g.TimeOfDayMinutes,
            TimeOfDaySeconds = g.TimeOfDaySeconds,
            DeltaTimeMs = g.DeltaTimeMs,
            CurrentLapTimeMs = g.CurrentLapTimeMs,
            PredictedLapTimeMs = g.PredictedLapTimeMs,
            FuelLiterCurrentQuantity = g.FuelLiterCurrentQuantity,
            FuelLiterCurrentQuantityPercent = g.FuelLiterCurrentQuantityPercent,
            FuelLiterPerKm = g.FuelLiterPerKm,
            KmPerFuelLiter = g.KmPerFuelLiter,
            CurrentTorque = g.CurrentTorque,
            CurrentBhp = g.CurrentBhp,
            TyreLf = TyreStateDto.FromStruct(g.TyreLf),
            TyreRf = TyreStateDto.FromStruct(g.TyreRf),
            TyreLr = TyreStateDto.FromStruct(g.TyreLr),
            TyreRr = TyreStateDto.FromStruct(g.TyreRr),
            Npos = g.Npos,
            KersChargePerc = g.KersChargePerc,
            KersCurrentPerc = g.KersCurrentPerc,
            ControlLockTime = g.ControlLockTime,
            CarDamage = DamageStateDto.FromStruct(g.CarDamage),
            CarLocation = g.CarLocation.ToString(),
            PitInfo = PitInfoDto.FromStruct(g.PitInfo),
            FuelLiterUsed = g.FuelLiterUsed,
            FuelLiterPerLap = g.FuelLiterPerLap,
            LapsPossibleWithFuel = g.LapsPossibleWithFuel,
            BatteryTemperature = g.BatteryTemperature,
            BatteryVoltage = g.BatteryVoltage,
            InstantaneousFuelLiterPerKm = g.InstantaneousFuelLiterPerKm,
            InstantaneousKmPerFuelLiter = g.InstantaneousKmPerFuelLiter,
            GearRpmWindow = g.GearRpmWindow,
            Instrumentation = InstrumentationDto.FromStruct(g.Instrumentation),
            Electronics = ElectronicsDto.FromStruct(g.Electronics),
            TotalLapCount = g.TotalLapCount,
            CurrentPos = g.CurrentPos,
            TotalDrivers = g.TotalDrivers,
            LastLaptimeMs = g.LastLaptimeMs,
            BestLaptimeMs = g.BestLaptimeMs,
            Flag = g.Flag.ToString(),
            GlobalFlag = g.GlobalFlag.ToString(),
            MaxGears = g.MaxGears,
            EngineType = g.EngineType.ToString(),
            HasKers = g.HasKers,
            IsLastLap = g.IsLastLap,
            PerformanceModeName = DecodeBytes(g.PerformanceModeName),
            DiffCoastRawValue = g.DiffCoastRawValue,
            DiffPowerRawValue = g.DiffPowerRawValue,
            RaceCutGainedTimeMs = g.RaceCutGainedTimeMs,
            DistanceToDeadline = g.DistanceToDeadline,
            RaceCutCurrentDelta = g.RaceCutCurrentDelta,
            SessionState = SessionStateDto.FromStruct(g.SessionState),
            TimingState = TimingStateDto.FromStruct(g.TimingState),
            PlayerPing = g.PlayerPing,
            PlayerLatency = g.PlayerLatency,
            PlayerCpuUsage = g.PlayerCpuUsage,
            PlayerCpuUsageAvg = g.PlayerCpuUsageAvg,
            PlayerQos = g.PlayerQos,
            PlayerQosAvg = g.PlayerQosAvg,
            PlayerFps = g.PlayerFps,
            PlayerFpsAvg = g.PlayerFpsAvg,
            DriverName = DecodeBytes(g.DriverName),
            DriverSurname = DecodeBytes(g.DriverSurname),
            CarModel = DecodeBytes(g.CarModel),
            IsInPitBox = g.IsInPitBox,
            IsInPitLane = g.IsInPitLane,
            IsValidLap = g.IsValidLap,
            GapAhead = g.GapAhead,
            GapBehind = g.GapBehind,
            ActiveCars = g.ActiveCars,
            FuelPerLap = g.FuelPerLap,
            FuelEstimatedLaps = g.FuelEstimatedLaps,
            AssistsState = AssistsStateDto.FromStruct(g.AssistsState),
            MaxFuel = g.MaxFuel,
            MaxTurboBoost = g.MaxTurboBoost,
            UseSingleCompound = g.UseSingleCompound
        };
    }

    private static string DecodeBytes(byte[]? data)
    {
        if (data == null) return "";
        int nullIdx = Array.IndexOf(data, (byte)0);
        int len = nullIdx >= 0 ? nullIdx : data.Length;
        return System.Text.Encoding.ASCII.GetString(data, 0, len).Trim();
    }
}

public sealed class StaticSnapshotDto
{
    public string SmVersion { get; set; } = "";
    public string AcEvoVersion { get; set; } = "";
    public string Session { get; set; } = "";
    public string SessionName { get; set; } = "";
    public string StartingGrip { get; set; } = "";
    public float StartingAmbientTemperatureC { get; set; }
    public float StartingGroundTemperatureC { get; set; }
    public bool IsStaticWeather { get; set; }
    public bool IsTimedRace { get; set; }
    public bool IsOnline { get; set; }
    public int NumberOfSessions { get; set; }
    public string Nation { get; set; } = "";
    public float Longitude { get; set; }
    public float Latitude { get; set; }
    public string Track { get; set; } = "";
    public string TrackConfiguration { get; set; } = "";
    public float TrackLengthM { get; set; }
    public int NumCars { get; set; }
    public int MaxRpm { get; set; }
    public float MaxFuel { get; set; }
    public float SteerRatio { get; set; }
    public float[] SuspensionMaxTravel { get; set; } = [];
    public string PlayerName { get; set; } = "";
    public string PlayerSurname { get; set; } = "";
    public string PlayerNick { get; set; } = "";
    public string CarModel { get; set; } = "";

    public static StaticSnapshotDto FromRawBuffer(byte[] buf)
    {
        if (buf == null || buf.Length == 0)
            return new StaticSnapshotDto();

        return new StaticSnapshotDto
        {
            SmVersion = StaticFieldReader.GetSmVersion(buf),
            AcEvoVersion = StaticFieldReader.GetAcEvoVersion(buf),
            Session = ((AcEvoSessionType)StaticFieldReader.GetSession(buf)).ToString(),
            SessionName = StaticFieldReader.GetSessionName(buf),
            StartingGrip = "", 
            StartingAmbientTemperatureC = 0,
            StartingGroundTemperatureC = 0,
            IsStaticWeather = false,
            IsTimedRace = false,
            IsOnline = false,
            NumberOfSessions = StaticFieldReader.GetNumberOfSessions(buf),
            Nation = StaticFieldReader.GetNation(buf),
            Longitude = StaticFieldReader.GetLongitude(buf),
            Latitude = StaticFieldReader.GetLatitude(buf),
            Track = StaticFieldReader.GetTrack(buf),
            TrackConfiguration = StaticFieldReader.GetTrackConfiguration(buf),
            TrackLengthM = StaticFieldReader.GetTrackLengthM(buf),
            NumCars = 0,
            MaxRpm = 0,
            MaxFuel = 0,
            SteerRatio = 0,
            SuspensionMaxTravel = [],
            PlayerName = "",
            PlayerSurname = "",
            PlayerNick = "",
            CarModel = ""
        };
    }

    public static StaticSnapshotDto FromStruct(SPageFileStaticEvo s)
    {
        return new StaticSnapshotDto
        {
            SmVersion = DecodeBytes(s.SmVersion),
            AcEvoVersion = DecodeBytes(s.AcEvoVersion),
            Session = s.Session.ToString(),
            SessionName = DecodeBytes(s.SessionName),
            StartingGrip = s.StartingGrip.ToString(),
            StartingAmbientTemperatureC = s.StartingAmbientTemperatureC,
            StartingGroundTemperatureC = s.StartingGroundTemperatureC,
            IsStaticWeather = s.IsStaticWeather,
            IsTimedRace = s.IsTimedRace,
            IsOnline = s.IsOnline,
            NumberOfSessions = s.NumberOfSessions,
            Nation = DecodeBytes(s.Nation),
            Longitude = s.Longitude,
            Latitude = s.Latitude,
            Track = DecodeBytes(s.Track),
            TrackConfiguration = DecodeBytes(s.TrackConfiguration),
            TrackLengthM = s.TrackLengthM,
            NumCars = s.NumCars,
            MaxRpm = s.MaxRpm,
            MaxFuel = s.MaxFuel,
            SteerRatio = s.SteerRatio,
            SuspensionMaxTravel = s.SuspensionMaxTravel != null ? (float[])s.SuspensionMaxTravel.Clone() : [],
            PlayerName = DecodeBytes(s.PlayerName),
            PlayerSurname = DecodeBytes(s.PlayerSurname),
            PlayerNick = DecodeBytes(s.PlayerNick),
            CarModel = DecodeBytes(s.CarModel)
        };
    }

    private static string DecodeBytes(byte[]? data)
    {
        if (data == null) return "";
        int nullIdx = Array.IndexOf(data, (byte)0);
        int len = nullIdx >= 0 ? nullIdx : data.Length;
        return System.Text.Encoding.ASCII.GetString(data, 0, len).Trim();
    }
}

public sealed class Vector3Dto
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public sealed class TyreStateDto
{
    public float Slip { get; set; }
    public bool Lock { get; set; }
    public float TyrePression { get; set; }
    public float TyreTemperatureC { get; set; }
    public float BrakeTemperatureC { get; set; }
    public float BrakePressure { get; set; }
    public float TyreTemperatureLeft { get; set; }
    public float TyreTemperatureCenter { get; set; }
    public float TyreTemperatureRight { get; set; }
    public string TyreCompoundFront { get; set; } = "";
    public string TyreCompoundRear { get; set; } = "";
    public float TyreNormalizedPressure { get; set; }
    public float TyreNormalizedTemperatureLeft { get; set; }
    public float TyreNormalizedTemperatureCenter { get; set; }
    public float TyreNormalizedTemperatureRight { get; set; }
    public float BrakeNormalizedTemperature { get; set; }
    public float TyreNormalizedTemperatureCore { get; set; }

    public static TyreStateDto FromStruct(SmevoTyreState t)
    {
        return new TyreStateDto
        {
            Slip = t.Slip,
            Lock = t.Lock,
            TyrePression = t.TyrePression,
            TyreTemperatureC = t.TyreTemperatureC,
            BrakeTemperatureC = t.BrakeTemperatureC,
            BrakePressure = t.BrakePressure,
            TyreTemperatureLeft = t.TyreTemperatureLeft,
            TyreTemperatureCenter = t.TyreTemperatureCenter,
            TyreTemperatureRight = t.TyreTemperatureRight,
            TyreCompoundFront = DecodeBytes(t.TyreCompoundFront),
            TyreCompoundRear = DecodeBytes(t.TyreCompoundRear),
            TyreNormalizedPressure = t.TyreNormalizedPressure,
            TyreNormalizedTemperatureLeft = t.TyreNormalizedTemperatureLeft,
            TyreNormalizedTemperatureCenter = t.TyreNormalizedTemperatureCenter,
            TyreNormalizedTemperatureRight = t.TyreNormalizedTemperatureRight,
            BrakeNormalizedTemperature = t.BrakeNormalizedTemperature,
            TyreNormalizedTemperatureCore = t.TyreNormalizedTemperatureCore
        };
    }

    private static string DecodeBytes(byte[]? data)
    {
        if (data == null) return "";
        int nullIdx = Array.IndexOf(data, (byte)0);
        int len = nullIdx >= 0 ? nullIdx : data.Length;
        return System.Text.Encoding.ASCII.GetString(data, 0, len).Trim();
    }
}

public sealed class DamageStateDto
{
    public float DamageFront { get; set; }
    public float DamageRear { get; set; }
    public float DamageLeft { get; set; }
    public float DamageRight { get; set; }
    public float DamageCenter { get; set; }
    public float DamageSuspensionLf { get; set; }
    public float DamageSuspensionRf { get; set; }
    public float DamageSuspensionLr { get; set; }
    public float DamageSuspensionRr { get; set; }

    public static DamageStateDto FromStruct(SmevoDamageState d)
    {
        return new DamageStateDto
        {
            DamageFront = d.DamageFront,
            DamageRear = d.DamageRear,
            DamageLeft = d.DamageLeft,
            DamageRight = d.DamageRight,
            DamageCenter = d.DamageCenter,
            DamageSuspensionLf = d.DamageSuspensionLf,
            DamageSuspensionRf = d.DamageSuspensionRf,
            DamageSuspensionLr = d.DamageSuspensionLr,
            DamageSuspensionRr = d.DamageSuspensionRr
        };
    }
}

public sealed class PitInfoDto
{
    public sbyte Damage { get; set; }
    public sbyte Fuel { get; set; }
    public sbyte TyresLf { get; set; }
    public sbyte TyresRf { get; set; }
    public sbyte TyresLr { get; set; }
    public sbyte TyresRr { get; set; }

    public static PitInfoDto FromStruct(SmevoPitInfo p)
    {
        return new PitInfoDto
        {
            Damage = p.Damage,
            Fuel = p.Fuel,
            TyresLf = p.TyresLf,
            TyresRf = p.TyresRf,
            TyresLr = p.TyresLr,
            TyresRr = p.TyresRr
        };
    }
}

public sealed class InstrumentationDto
{
    public sbyte MainLightStage { get; set; }
    public sbyte SpecialLightStage { get; set; }
    public sbyte CockpitLightStage { get; set; }
    public sbyte WiperLevel { get; set; }
    public bool RainLights { get; set; }
    public bool DirectionLightLeft { get; set; }
    public bool DirectionLightRight { get; set; }
    public bool FlashingLights { get; set; }
    public bool WarningLights { get; set; }
    public bool AreHeadlightsVisible { get; set; }
    public sbyte SelectedDisplayIndex { get; set; }

    public static InstrumentationDto FromStruct(SmevoInstrumentation i)
    {
        return new InstrumentationDto
        {
            MainLightStage = i.MainLightStage,
            SpecialLightStage = i.SpecialLightStage,
            CockpitLightStage = i.CockpitLightStage,
            WiperLevel = i.WiperLevel,
            RainLights = i.RainLights,
            DirectionLightLeft = i.DirectionLightLeft,
            DirectionLightRight = i.DirectionLightRight,
            FlashingLights = i.FlashingLights,
            WarningLights = i.WarningLights,
            AreHeadlightsVisible = i.AreHeadlightsVisible,
            SelectedDisplayIndex = i.SelectedDisplayIndex
        };
    }
}

public sealed class ElectronicsDto
{
    public sbyte TcLevel { get; set; }
    public sbyte TcCutLevel { get; set; }
    public sbyte AbsLevel { get; set; }
    public sbyte EscLevel { get; set; }
    public sbyte EbbLevel { get; set; }
    public float BrakeBias { get; set; }
    public sbyte EngineMapLevel { get; set; }
    public float TurboLevel { get; set; }
    public sbyte ErsDeploymentMap { get; set; }
    public float ErsRechargeMap { get; set; }
    public bool IsErsHeatChargingOn { get; set; }
    public bool IsErsOvertakeModeOn { get; set; }
    public bool IsDrsOpen { get; set; }
    public sbyte DiffPowerLevel { get; set; }
    public sbyte DiffCoastLevel { get; set; }
    public sbyte FrontBumpDamperLevel { get; set; }
    public sbyte FrontReboundDamperLevel { get; set; }
    public sbyte RearBumpDamperLevel { get; set; }
    public sbyte RearReboundDamperLevel { get; set; }
    public bool IsIgnitionOn { get; set; }
    public bool IsPitlimiterOn { get; set; }
    public sbyte ActivePerformanceMode { get; set; }

    public static ElectronicsDto FromStruct(SmevoElectronics e)
    {
        return new ElectronicsDto
        {
            TcLevel = e.TcLevel,
            TcCutLevel = e.TcCutLevel,
            AbsLevel = e.AbsLevel,
            EscLevel = e.EscLevel,
            EbbLevel = e.EbbLevel,
            BrakeBias = e.BrakeBias,
            EngineMapLevel = e.EngineMapLevel,
            TurboLevel = e.TurboLevel,
            ErsDeploymentMap = e.ErsDeploymentMap,
            ErsRechargeMap = e.ErsRechargeMap,
            IsErsHeatChargingOn = e.IsErsHeatChargingOn,
            IsErsOvertakeModeOn = e.IsErsOvertakeModeOn,
            IsDrsOpen = e.IsDrsOpen,
            DiffPowerLevel = e.DiffPowerLevel,
            DiffCoastLevel = e.DiffCoastLevel,
            FrontBumpDamperLevel = e.FrontBumpDamperLevel,
            FrontReboundDamperLevel = e.FrontReboundDamperLevel,
            RearBumpDamperLevel = e.RearBumpDamperLevel,
            RearReboundDamperLevel = e.RearReboundDamperLevel,
            IsIgnitionOn = e.IsIgnitionOn,
            IsPitlimiterOn = e.IsPitlimiterOn,
            ActivePerformanceMode = e.ActivePerformanceMode
        };
    }
}

public sealed class SessionStateDto
{
    public string PhaseName { get; set; } = "";
    public string TimeLeft { get; set; } = "";
    public int TimeLeftMs { get; set; }
    public string WaitTime { get; set; } = "";
    public int TotalLap { get; set; }
    public int CurrentLap { get; set; }
    public int LightsOn { get; set; }
    public int LightsMode { get; set; }
    public float LapLengthKm { get; set; }
    public int EndSessionFlag { get; set; }
    public string TimeToNextSession { get; set; } = "";
    public bool DisconnectedFromServer { get; set; }
    public bool RestartSeasonEnabled { get; set; }
    public bool UiEnableDrive { get; set; }
    public bool UiEnableSetup { get; set; }
    public bool IsReadyToNextBlinking { get; set; }
    public bool ShowWaitingForPlayers { get; set; }

    public static SessionStateDto FromStruct(SmevoSessionState s)
    {
        return new SessionStateDto
        {
            PhaseName = DecodeBytes(s.PhaseName),
            TimeLeft = DecodeBytes(s.TimeLeft),
            TimeLeftMs = s.TimeLeftMs,
            WaitTime = DecodeBytes(s.WaitTime),
            TotalLap = s.TotalLap,
            CurrentLap = s.CurrentLap,
            LightsOn = s.LightsOn,
            LightsMode = s.LightsMode,
            LapLengthKm = s.LapLengthKm,
            EndSessionFlag = s.EndSessionFlag,
            TimeToNextSession = DecodeBytes(s.TimeToNextSession),
            DisconnectedFromServer = s.DisconnectedFromServer,
            RestartSeasonEnabled = s.RestartSeasonEnabled,
            UiEnableDrive = s.UiEnableDrive,
            UiEnableSetup = s.UiEnableSetup,
            IsReadyToNextBlinking = s.IsReadyToNextBlinking,
            ShowWaitingForPlayers = s.ShowWaitingForPlayers
        };
    }

    private static string DecodeBytes(byte[]? data)
    {
        if (data == null) return "";
        int nullIdx = Array.IndexOf(data, (byte)0);
        int len = nullIdx >= 0 ? nullIdx : data.Length;
        return System.Text.Encoding.ASCII.GetString(data, 0, len).Trim();
    }
}

public sealed class TimingStateDto
{
    public string CurrentLaptime { get; set; } = "";
    public string DeltaCurrent { get; set; } = "";
    public int DeltaCurrentP { get; set; }
    public string LastLaptime { get; set; } = "";
    public string DeltaLast { get; set; } = "";
    public int DeltaLastP { get; set; }
    public string BestLaptime { get; set; } = "";
    public string IdealLaptime { get; set; } = "";
    public string TotalTime { get; set; } = "";
    public bool IsInvalid { get; set; }

    public static TimingStateDto FromStruct(SmevoTimingState t)
    {
        return new TimingStateDto
        {
            CurrentLaptime = DecodeBytes(t.CurrentLaptime),
            DeltaCurrent = DecodeBytes(t.DeltaCurrent),
            DeltaCurrentP = t.DeltaCurrentP,
            LastLaptime = DecodeBytes(t.LastLaptime),
            DeltaLast = DecodeBytes(t.DeltaLast),
            DeltaLastP = t.DeltaLastP,
            BestLaptime = DecodeBytes(t.BestLaptime),
            IdealLaptime = DecodeBytes(t.IdealLaptime),
            TotalTime = DecodeBytes(t.TotalTime),
            IsInvalid = t.IsInvalid
        };
    }

    private static string DecodeBytes(byte[]? data)
    {
        if (data == null) return "";
        int nullIdx = Array.IndexOf(data, (byte)0);
        int len = nullIdx >= 0 ? nullIdx : data.Length;
        return System.Text.Encoding.ASCII.GetString(data, 0, len).Trim();
    }
}

public sealed class AssistsStateDto
{
    public byte AutoGear { get; set; }
    public byte AutoBlip { get; set; }
    public byte AutoClutch { get; set; }
    public byte AutoClutchOnStart { get; set; }
    public byte ManualIgnitionEStart { get; set; }
    public byte AutoPitLimiter { get; set; }
    public byte StandingStartAssist { get; set; }
    public float AutoSteer { get; set; }
    public float ArcadeStabilityControl { get; set; }

    public static AssistsStateDto FromStruct(SmevoAssistsState a)
    {
        return new AssistsStateDto
        {
            AutoGear = a.AutoGear,
            AutoBlip = a.AutoBlip,
            AutoClutch = a.AutoClutch,
            AutoClutchOnStart = a.AutoClutchOnStart,
            ManualIgnitionEStart = a.ManualIgnitionEStart,
            AutoPitLimiter = a.AutoPitLimiter,
            StandingStartAssist = a.StandingStartAssist,
            AutoSteer = a.AutoSteer,
            ArcadeStabilityControl = a.ArcadeStabilityControl
        };
    }
}
