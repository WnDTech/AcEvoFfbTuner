using System.Net.Sockets;
using System.Text;

namespace TelemetryBrowser.Services;

// Official ACC Broadcasting SDK implementation
// Based on ksBroadcastingNetwork from Dedicated Server SDK
public sealed class AccBroadcastService : IDisposable
{
    private readonly UdpClient? _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<int, CarEntry> _cars = new();
    private readonly object _lock = new();
    private int _msgCount;
    private string _lastError = "";
    private string _registerResult = "Not attempted";
    private int _connectionId;
    private bool _registered;
    private readonly int[] _typeCounts = new int[20];

    private const int AccPort = 9000;

    public AccBroadcastService()
    {
        try
        {
            _udp = new UdpClient(0);
            SendRegistration();
            _registered = true;
            _ = ListenAsync(_cts.Token);
            // Periodic entry list refresh every 30 seconds
            _ = RefreshTimerAsync(_cts.Token);
        }
        catch (Exception ex) { _lastError = $"Init:{ex.Message}"; }
    }

    // === Registration (Outbound Type 1) ===
    private void SendRegistration()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write((byte)1);                    // REGISTER_COMMAND_APPLICATION
        w.Write((byte)4);                    // BROADCASTING_PROTOCOL_VERSION
        WriteStr(w, "TelemetryBrowser");      // display name
        WriteStr(w, "asd");                   // connection password
        w.Write(250);                         // update interval (int32)
        WriteStr(w, "");                      // command password
        _udp!.Send(ms.ToArray(), (int)ms.Length, "127.0.0.1", AccPort);
        _registerResult = "Sent";
    }

    private static void WriteStr(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write(Convert.ToUInt16(b.Length));
        w.Write(b);
    }

    // === Outbound Requests ===
    private void RequestEntryList()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)10);        // REQUEST_ENTRY_LIST
        w.Write(_connectionId);   // int32
        _udp!.Send(ms.ToArray(), (int)ms.Length, "127.0.0.1", AccPort);
    }

    private void RequestTrackData()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)11);        // REQUEST_TRACK_DATA
        w.Write(_connectionId);   // int32
        _udp!.Send(ms.ToArray(), (int)ms.Length, "127.0.0.1", AccPort);
    }

    // === Properties ===
    public bool IsConnected => _registered && _msgCount >= 2;
    public int MessageCount => _msgCount;
    public string LastError => _lastError;
    public string RegisterResult => _registerResult;
    public int BoundPort => ((System.Net.IPEndPoint?)_udp?.Client?.LocalEndPoint)?.Port ?? -1;

    public CarEntry[] GetAllCars()
    {
        lock (_lock) { return _cars.Values.OrderBy(c => c.RaceNumber).ToArray(); }
    }

    public Dictionary<string, object> GetOpponentsData()
    {
        var entries = new List<Dictionary<string, object?>>();
        lock (_lock)
        {
            foreach (var car in _cars.Values.OrderBy(c => c.RaceNumber))
            {
                entries.Add(new Dictionary<string, object?>
                {
                    ["carIndex"] = car.CarIndex,
                    ["raceNumber"] = car.RaceNumber,
                    ["driverName"] = car.DriverName,
                    ["driverSurname"] = car.DriverSurname,
                    ["shortName"] = car.ShortName,
                    ["carModel"] = car.CarModel,
                    ["teamName"] = car.TeamName,
                    ["gear"] = car.Gear,
                    ["speedKmh"] = car.SpeedKmh,
                    ["position"] = car.Position > 0 ? car.Position : null,
                    ["trackPosition"] = car.TrackPosition > 0 ? car.TrackPosition : null,
                    ["laps"] = car.CompletedLaps,
                    ["posX"] = car.CarPosX,
                    ["posY"] = car.CarPosY,
                    ["splinePos"] = car.SplinePosition,
                    ["pitStatus"] = car.PitStatus,
                    ["bestLapTime"] = car.BestLapTime > 0 ? car.BestLapTime : null,
                    ["lastLapTime"] = car.LastLapTime > 0 ? car.LastLapTime : null,
                    ["currentLapTime"] = car.CurrentLapTime > 0 ? car.CurrentLapTime : null,
                    ["isPlayer"] = car.IsPlayer,
                });
            }
        }
        return new Dictionary<string, object>
        {
            ["entries"] = entries,
            ["carCount"] = entries.Count,
            ["_debug_types"] = string.Join(", ", _typeCounts.Select((c, i) => c > 0 ? $"T{i}={c}" : "").Where(s => s != ""))
        };
    }

    // === Receive Loop ===
    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct);
                _msgCount++;
                using var br = new BinaryReader(new MemoryStream(result.Buffer));
                byte type = br.ReadByte(); // read type, advancing stream to offset 1
                if (type < _typeCounts.Length) _typeCounts[type]++;
                try
                {
                    switch (type)
                    {
                        case 1: ParseRegResult(br); break;       // REGISTRATION_RESULT
                        case 2: ParseRealtimeUpdate(br); break;   // REALTIME_UPDATE
                        case 3: ParseCarUpdate(br); break;        // REALTIME_CAR_UPDATE
                        case 4: ParseEntryList(br); break;        // ENTRY_LIST
                        case 5: ParseTrackData(br); break;        // TRACK_DATA
                        case 6: ParseEntryListCar(br); break;     // ENTRY_LIST_CAR
                        case 7: br.ReadInt32(); break;            // BROADCASTING_EVENT
                    }
                }
                catch (Exception ex)
                {
                    _lastError = $"Pkt{type}:{ex.Message}";
                    if (_msgCount <= 3)
                    {
                        var hex = string.Join(" ", result.Buffer.Take(48).Select(b => $"{b:X2}"));
                        System.Console.Error.WriteLine($"[ACC] Error type {type}: {ex.Message} hex={hex}");
                    }
                }
            }
            catch (Exception ex) { _lastError = $"UDP:{ex.Message}"; }
        }
    }

    // Type 1: REGISTRATION_RESULT
    void ParseRegResult(BinaryReader r)
    {
        _connectionId = r.ReadInt32();
        bool ok = r.ReadByte() > 0;
        r.ReadByte(); // isReadonly
        string err = ReadStr(r);
        _registerResult = ok ? $"OK id={_connectionId}" : $"FAIL: {err}";
        System.Console.Error.WriteLine($"[ACC] Registration: {_registerResult}");
        if (ok)
        {
            RequestEntryList();
            RequestTrackData();

            // Retry: keep requesting the entry list until we have car data
            _ = RetryEntryListAsync(_cts.Token);
        }
    }

    private async Task RetryEntryListAsync(CancellationToken ct)
    {
        await Task.Delay(2000, ct); // wait 2s for initial packets to arrive
        for (int i = 0; i < 5 && !ct.IsCancellationRequested; i++)
        {
            bool hasAny = false, hasNames = false;
            lock (_lock)
            {
                hasAny = _cars.Count > 0;
                hasNames = _cars.Any(c => !string.IsNullOrEmpty(c.Value.DriverName));
            }
            if (hasNames) return;
            System.Console.Error.WriteLine($"[ACC] EntryList retry {i + 1}/5: {_cars.Count} cars, names={hasNames}");
            RequestEntryList();
            await Task.Delay(2000, ct);
        }
    }

    private async Task RefreshTimerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(30000, ct);
            RequestEntryList();
        }
    }

    // Type 2: REALTIME_UPDATE (session data, not per-car)
    void ParseRealtimeUpdate(BinaryReader r)
    {
        r.ReadUInt16();  // eventIndex
        r.ReadUInt16();  // sessionIndex
        r.ReadByte();    // sessionType
        r.ReadByte();    // phase
        r.ReadSingle();  // sessionTime
        r.ReadSingle();  // sessionEndTime
        r.ReadInt32();   // focusedCarIndex
        ReadStr(r);      // activeCameraSet
        ReadStr(r);      // activeCamera
        ReadStr(r);      // currentHudPage
        r.ReadByte();    // isReplayPlaying
        // skip remaining fields
    }

    // Type 3: REALTIME_CAR_UPDATE (per-car telemetry)
    void ParseCarUpdate(BinaryReader r)
    {
        int carIdx = r.ReadUInt16();
        int driverIdx = r.ReadUInt16();
        r.ReadByte(); // driverCount
        int gear = r.ReadByte() - 2; // SDK: -2 makes R=-1, N=0, 1st=1
        float posX = r.ReadSingle();
        float posY = r.ReadSingle();
        float yaw = r.ReadSingle();
        int carLoc = r.ReadByte(); // 0=Track, 1=Pitlane, 2=PitEntry, 3=PitExit
        int kmh = r.ReadUInt16();
        int position = r.ReadUInt16();
        r.ReadUInt16(); // cupPosition
        int trackPos = r.ReadUInt16();
        float spline = r.ReadSingle();
        int laps = r.ReadUInt16();
        int delta = r.ReadInt32();

        // Lap info blocks
        int bestMs = ReadLapMs(r);
        int lastMs = ReadLapMs(r);
        int curMs = ReadLapMs(r);

        lock (_lock)
        {
            if (!_cars.TryGetValue(carIdx, out var car))
            { car = new CarEntry { CarIndex = carIdx }; _cars[carIdx] = car; }
            car.Gear = gear;
            car.SpeedKmh = kmh;
            car.Position = position;
            car.TrackPosition = trackPos;
            car.CompletedLaps = laps;
            car.SplinePosition = spline;
            car.CarPosX = San(posX);
            car.CarPosY = San(posY);
            car.DriverIndex = driverIdx;
            car.PitStatus = carLoc switch { 1 => "pitlane", 2 => "pitEntry", 3 => "pitExit", _ => "track" };
            if (_msgCount <= 15) 
                System.Console.Error.WriteLine($"[ACC] Car{carIdx}: carLoc={carLoc} kmh={kmh} pit={car.PitStatus}");
            if (bestMs > 0 && bestMs < int.MaxValue) car.BestLapTime = bestMs / 1000f;
            if (lastMs > 0 && lastMs < int.MaxValue) car.LastLapTime = lastMs / 1000f;
            if (curMs > 0 && curMs < int.MaxValue) car.CurrentLapTime = curMs / 1000f;
        }
    }

    // Type 4: ENTRY_LIST (car indices only)
    void ParseEntryList(BinaryReader r)
    {
        r.ReadInt32(); // connectionId
        ushort count = r.ReadUInt16();
        var newIndices = new HashSet<int>();
        for (int i = 0; i < count; i++)
            newIndices.Add(r.ReadUInt16());

        lock (_lock)
        {
            // Remove cars that are no longer in the entry list
            var toRemove = _cars.Keys.Where(k => !newIndices.Contains(k)).ToList();
            foreach (var k in toRemove) _cars.Remove(k);
            // Add new cars
            foreach (var idx in newIndices)
                if (!_cars.ContainsKey(idx))
                    _cars[idx] = new CarEntry { CarIndex = idx };
        }
        // Request details for any cars that might need them
        RequestEntryList();
    }

    // Type 5: TRACK_DATA
    void ParseTrackData(BinaryReader r)
    {
        r.ReadInt32(); // connectionId
        ReadStr(r);    // trackName
        r.ReadInt32(); // trackId
        r.ReadInt32(); // trackMeters
        // Skip camera sets and HUD pages
    }

    // Type 6: ENTRY_LIST_CAR (detailed car info)
    void ParseEntryListCar(BinaryReader r)
    {
        int carIdx = r.ReadUInt16();
        byte modelType = r.ReadByte();
        string team = ReadStr(r);
        int raceNum = r.ReadInt32();
        r.ReadByte(); // cupCategory
        byte curDriver = r.ReadByte();
        r.ReadUInt16(); // nationality

        string fn = "", ln = "", sn = "";
        byte dCount = r.ReadByte();
        for (int d = 0; d < dCount; d++)
        {
            fn = ReadStr(r);
            ln = ReadStr(r);
            sn = ReadStr(r);
            r.ReadByte(); // driverCategory
            r.ReadUInt16(); // driverNationality
        }

        if (_msgCount <= 10)
            System.Console.Error.WriteLine($"[ACC] EntryCar #{carIdx}: model={modelType} team='{team}' #{raceNum} driver='{fn} {ln}'");

        lock (_lock)
        {
            if (!_cars.TryGetValue(carIdx, out var car))
            { car = new CarEntry { CarIndex = carIdx }; _cars[carIdx] = car; }
            car.RaceNumber = raceNum;
            car.DriverName = fn;
            car.DriverSurname = ln;
            car.ShortName = sn;
            car.TeamName = team;
            car.CarModel = ModelTypeToName(modelType);
        }
    }

    static string ReadStr(BinaryReader r)
    {
        ushort len = r.ReadUInt16();
        if (len == 0) return "";
        if (r.BaseStream.Position + len > r.BaseStream.Length)
        { r.BaseStream.Position = r.BaseStream.Length; return ""; }
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    // LapInfo: int32 ms + carIndex(uint16) + driverIndex(uint16) + splits + flags
    static int ReadLapMs(BinaryReader r)
    {
        int ms = r.ReadInt32();
        r.ReadUInt16(); // carIndex
        r.ReadUInt16(); // driverIndex
        byte splitCount = r.ReadByte();
        for (int i = 0; i < splitCount; i++) r.ReadInt32();
        r.ReadByte(); // isInvalid
        r.ReadByte(); // isValidForBest
        r.ReadByte(); // isOutlap
        r.ReadByte(); // isInlap
        return ms;
    }

    static float San(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0 : v;

    static string ModelTypeToName(int type) => type switch
    {
        0 => "Porsche 991 GT3 R",
        1 => "Mercedes-AMG GT3",
        2 => "Ferrari 488 GT3",
        3 => "Audi R8 LMS",
        4 => "Lamborghini Huracan GT3",
        5 => "McLaren 650S GT3",
        6 => "Nissan GT-R Nismo GT3 2018",
        7 => "BMW M6 GT3",
        8 => "Bentley Continental GT3 2018",
        9 => "Porsche 991II GT3 Cup",
        10 => "Nissan GT-R Nismo GT3 2017",
        11 => "Bentley Continental GT3 2016",
        12 => "Aston Martin V12 Vantage GT3",
        13 => "Lamborghini Gallardo R-EX",
        14 => "Jaguar G3",
        15 => "Lexus RC F GT3",
        16 => "Lamborghini Huracan Evo (2019)",
        17 => "Honda NSX GT3",
        18 => "Lamborghini Huracan SuperTrofeo",
        19 => "Audi R8 LMS Evo (2019)",
        20 => "AMR V8 Vantage (2019)",
        21 => "Honda NSX Evo (2019)",
        22 => "McLaren 720S GT3 (2019)",
        23 => "Porsche 911II GT3 R (2019)",
        24 => "Ferrari 488 GT3 Evo 2020",
        25 => "Mercedes-AMG GT3 2020",
        26 => "Ferrari 488 Challenge Evo",
        27 => "BMW M2 CS Racing",
        28 => "Porsche 911 GT3 Cup (992)",
        29 => "Lamborghini Huracan ST Evo2",
        30 => "BMW M4 GT3",
        31 => "Audi R8 LMS Evo II",
        32 => "Ferrari 296 GT3",
        33 => "Lamborghini Huracan Evo2",
        34 => "Porsche 992 GT3 R",
        35 => "McLaren 720S GT3 Evo 2023",
        36 => "Ford Mustang GT3",
        50 => "Alpine A110 GT4",
        51 => "AMR V8 Vantage GT4",
        52 => "Audi R8 LMS GT4",
        53 => "BMW M4 GT4",
        55 => "Chevrolet Camaro GT4",
        56 => "Ginetta G55 GT4",
        57 => "KTM X-Bow GT4",
        58 => "Maserati MC GT4",
        59 => "McLaren 570S GT4",
        60 => "Mercedes-AMG GT4",
        61 => "Porsche 718 Cayman GT4",
        80 => "Audi R8 LMS GT2",
        82 => "KTM XBOW GT2",
        83 => "Maserati MC20 GT2",
        84 => "Mercedes AMG GT2",
        85 => "Porsche 911 GT2 RS CS Evo",
        86 => "Porsche 935",
        _ => $"Car #{type}"
    };

    public void Dispose() { _cts.Cancel(); _udp?.Dispose(); _cts.Dispose(); }
}

public class CarEntry
{
    public int CarIndex, RaceNumber, DriverIndex, Gear = -1, Position = -1, TrackPosition = -1, CompletedLaps;
    public string DriverName = "", DriverSurname = "", ShortName = "", CarModel = "", TeamName = "", PitStatus = "";
    public bool IsPlayer;
    public float SpeedKmh, CarPosX, CarPosY, SplinePosition;
    public float BestLapTime = -1, LastLapTime = -1, CurrentLapTime = -1;
}
