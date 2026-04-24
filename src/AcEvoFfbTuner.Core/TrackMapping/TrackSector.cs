namespace AcEvoFfbTuner.Core.TrackMapping;

public sealed class TrackSector
{
    public int SectorNumber { get; set; }
    public int StartWaypointIndex { get; set; }
    public int EndWaypointIndex { get; set; }
    public float StartDistanceM { get; set; }
    public float EndDistanceM { get; set; }
    public float LengthM { get; set; }
    public string StartCornerName { get; set; } = "";

    public float AvgOutputForce { get; set; }
    public float PeakOutputForce { get; set; }
    public float ClippingPct { get; set; }
    public float AvgMzFront { get; set; }
    public float PeakMzFront { get; set; }
    public float AvgSpeedKmh { get; set; }
    public int SampleCount { get; set; }

    internal float _sumForce;
    internal float _sumMz;
    internal float _sumSpeed;
    internal int _clipCount;
    internal float _peakForce;
    internal float _peakMz;

    public string DisplayName => $"S{SectorNumber}";
}
