namespace AcEvoFfbTuner.Core.SharedMemory;

/// <summary>
/// One car in the session: world position (game X/Z plane, meters), lap fraction
/// (0 = unknown — e.g. AC EVO publishes no per-car lap position), flags, the game's class
/// name ("" = unknown; LMU mVehicleClass, R3E class name), the race number ("" = unknown)
/// and the flag currently shown to that car.
/// </summary>
public readonly record struct CarPosition(
    int Id, float X, float Z, float Npos, bool IsPlayer, bool InPits, int Place, string Name,
    string CarClass = "", string Number = "", CarFlag Flag = CarFlag.None);

/// <summary>
/// Optional reader capability: positions of every car in the session, player
/// included and flagged. TelemetryLoop calls it on the telemetry thread right after
/// the physics/graphics reads (~10 Hz), so implementations may reuse the raw buffers
/// filled by those reads without locking.
/// </summary>
public interface ICarPositionSource
{
    /// <summary>Clear <paramref name="cars"/> and fill it with the current session's cars.</summary>
    void ReadCarPositions(List<CarPosition> cars);
}

/// <summary>
/// car_positions.log — layout-detection results of the per-game car sources
/// (written on state change only, so it stays tiny). Lets a live session confirm
/// offsets that could not be verified offline.
/// </summary>
internal static class CarPositionLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "car_positions.log");

    public static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
