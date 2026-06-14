using System;
using System.Collections.Generic;

namespace AcEvoFfbTuner.Core.TrackMapping;

public struct Point2D
{
    public float X;
    public float Y;
    public Point2D(float x, float y) { X = x; Y = y; }
    public Point2D(double x, double y) { X = (float)x; Y = (float)y; }
}

public class ContinuousTrackAligner
{
    private readonly List<Point2D> _osmCachedMeters = new();
    private double _anchorLat, _anchorLon;
    private readonly List<Point2D> _lapGamePoints = new();
    private float _prevLapCount;
    private bool _isCalibrated;
    private float _matrixA = 1f, _matrixB;
    private float _cgX, _cgY, _coX, _coY;
    private int _refinementCount;
    private bool _handednessFlipped;

    public bool IsCalibrated => _isCalibrated;
    public float CurrentRotationDeg => (float)(Math.Atan2(_matrixB, _matrixA) * 180.0 / Math.PI);
    public double CurrentRotationRad => Math.Atan2(_matrixB, _matrixA);
    public bool HasFirstWaypoint => _lapGamePoints.Count > 0;

    public event Action? CalibrationLocked;

    public void InitializeTrack(
        List<TrackPoint>? trackLayout,
        List<TrackPoint>? pitLayout,
        double anchorLat, double anchorLon)
    {
        _osmCachedMeters.Clear();
        _lapGamePoints.Clear();
        _isCalibrated = false;
        _refinementCount = 0;
        _anchorLat = anchorLat;
        _anchorLon = anchorLon;

        if (trackLayout != null)
            foreach (var pt in trackLayout)
                _osmCachedMeters.Add(GpsToMeters(pt.Latitude, pt.Longitude));
        if (pitLayout != null)
            foreach (var pt in pitLayout)
                _osmCachedMeters.Add(GpsToMeters(pt.Latitude, pt.Longitude));
    }

    public void ProcessTelemetry(float carX, float carZ, float heading) { }

    /// <summary>
    /// Accumulate lap waypoints. Call every frame with the current lap count.
    /// When a lap completes (lapCount changes), ComputeFromLap() is triggered.
    /// </summary>
    public bool CheckLapCompletion(float carX, float carZ, int lapCount)
    {
        if (_isCalibrated) return true;

        if (lapCount > _prevLapCount)
        {
            _prevLapCount = lapCount;
            if (_lapGamePoints.Count >= 20)
            {
                return ComputeFromLap();
            }
            _lapGamePoints.Clear();
        }
        else
        {
            _prevLapCount = lapCount;
        }

        // Accumulate waypoints (downsample to ~every 2m)
        if (_lapGamePoints.Count == 0 ||
            Math.Abs(carX - _lapGamePoints[^1].X) > 2f ||
            Math.Abs(carZ - _lapGamePoints[^1].Y) > 2f)
        {
            _lapGamePoints.Add(new Point2D(carX, carZ));
        }

        // Fallback: if we have enough points (>500), trigger ComputeFromLap anyway.
        // This handles cases where lap count doesn't change (e.g., free roam, no laps set).
        if (_lapGamePoints.Count >= 500 && _refinementCount == 0)
        {
            _prevLapCount = lapCount;
            return ComputeFromLap();
        }

        return false;
    }

    private bool ComputeFromLap()
    {
        if (_lapGamePoints.Count < 20 || _osmCachedMeters.Count < 10) return false;

        // Build correspondence pairs by NORMALIZED shape matching:
        // Instead of matching absolute positions, we compute the centroid of ALL game points
        // and the centroid of ALL OSM points, then match normalized offsets from each centroid.
        float gcx = 0, gcz = 0;
        foreach (var p in _lapGamePoints) { gcx += p.X; gcz += p.Y; }
        gcx /= _lapGamePoints.Count; gcz /= _lapGamePoints.Count;

        float ocx = 0, ocy = 0;
        foreach (var p in _osmCachedMeters) { ocx += p.X; ocy += p.Y; }
        ocx /= _osmCachedMeters.Count; ocy /= _osmCachedMeters.Count;

        var gcp = new List<GroundControlPoint>();

        for (int i = 0; i < _lapGamePoints.Count; i += 3)
        {
            float relGx = _lapGamePoints[i].X - gcx;
            float relGz = _lapGamePoints[i].Y - gcz;

            // Find nearest OSM point by displacement from OSM centroid
            int bestIdx = 0;
            float bestDist = float.MaxValue;
            for (int j = 0; j < _osmCachedMeters.Count; j++)
            {
                float relOx = _osmCachedMeters[j].X - ocx;
                float relOz = _osmCachedMeters[j].Y - ocy;
                float dx = relOx - relGx;
                float dz = relOz - relGz;
                float d = dx * dx + dz * dz;
                if (d < bestDist) { bestDist = d; bestIdx = j; }
            }

            var osmPt = MetersToGps(_osmCachedMeters[bestIdx].X, _osmCachedMeters[bestIdx].Y);
            gcp.Add(new GroundControlPoint
            {
                GameX = _lapGamePoints[i].X,
                GameZ = _lapGamePoints[i].Y,
                Latitude = osmPt.lat,
                Longitude = osmPt.lon
            });
        }

        if (gcp.Count < 4) return false;

        if (TrackAlignmentService.TryComputeRigidTransform(gcp, out var gcX, out var gcZ, out var rotRad))
        {
            // Compute OSM GPS centroid for error evaluation
            double sumLat = 0, sumLon = 0;
            foreach (var p in gcp) { sumLat += p.Latitude; sumLon += p.Longitude; }
            double originLat = sumLat / gcp.Count;
            double originLon = sumLon / gcp.Count;

            // Try each 90° quadrant offset and pick the one with lowest error
            double bestRot = rotRad;
            double bestErr = double.MaxValue;

            for (int q = 0; q < 4; q++)
            {
                double testRot = rotRad + q * Math.PI / 2;
                double err = 0;
                for (int k = 0; k < gcp.Count; k++)
                {
                    double dx = gcp[k].GameX - gcX;
                    double dz = gcp[k].GameZ - gcZ;
                    double re = Math.Cos(testRot) * dx - Math.Sin(testRot) * dz;
                    double rn = Math.Sin(testRot) * dx + Math.Cos(testRot) * dz;
                    double dlat = (gcp[k].Latitude - originLat) * 111320 - rn;
                    double dlon = (gcp[k].Longitude - originLon) * 111320 * Math.Cos(originLat * Math.PI / 180.0) - re;
                    err += dlat * dlat + dlon * dlon;
                }
                if (err < bestErr) { bestErr = err; bestRot = testRot; }
            }

            rotRad = (float)bestRot;
            _matrixA = (float)Math.Cos(rotRad);
            _matrixB = (float)Math.Sin(rotRad);

            // Store centroids and GPS reference
            _cgX = gcX;
            _cgY = gcZ;
            _coX = (float)originLon;
            _coY = (float)originLat;

            _isCalibrated = true;
            _refinementCount++;
            CalibrationLocked?.Invoke();
            return true;
        }

        _lapGamePoints.Clear();
        return false;
    }

    public (double lat, double lon) GetCarGps(float carX, float carZ)
    {
        float dx, dz;
        if (_isCalibrated)
        {
            dx = carX - _cgX;
            dz = carZ - _cgY;
        }
        else
        {
            dx = carX - (_lapGamePoints.Count > 0 ? _lapGamePoints[0].X : carX);
            dz = carZ - (_lapGamePoints.Count > 0 ? _lapGamePoints[0].Y : carZ);
        }

        // Rotated displacement in OSM meters
        float ux = _matrixA * dx - _matrixB * dz;
        float uy = _matrixB * dx + _matrixA * dz;

        if (_isCalibrated)
        {
            // Convert OSM meters to GPS offset from the GCP origin
            double lat = _coY + uy / 111320.0;
            double lon = _coX + ux / (111320.0 * Math.Cos(_coY * Math.PI / 180.0));
            return (lat, lon);
        }
        else
        {
            return MetersToGps(ux, uy);
        }
    }

    public void SetRotationFromRadians(double radians)
    {
        _matrixA = (float)Math.Cos(radians);
        _matrixB = (float)Math.Sin(radians);
        _isCalibrated = true;
    }

    public void SeedFirstWaypoint(float spawnX, float spawnZ)
    {
        _lapGamePoints.Clear();
        _lapGamePoints.Add(new Point2D(spawnX, spawnZ));
    }

    public void Reset()
    {
        _lapGamePoints.Clear();
        _isCalibrated = false;
        _refinementCount = 0;
        _prevLapCount = 0;
        _matrixA = 1f;
        _matrixB = 0f;
        _cgX = _cgY = _coX = _coY = 0;
    }

    private (double lat, double lon) MetersToGps(double eastMeters, double northMeters)
    {
        double lat = _anchorLat + northMeters / 111320.0;
        double lon = _anchorLon + eastMeters / (111320.0 * Math.Cos(_anchorLat * Math.PI / 180.0));
        return (lat, lon);
    }

    private Point2D GpsToMeters(double lat, double lon)
    {
        double e = (lon - _anchorLon) * 111320.0 * Math.Cos(_anchorLat * Math.PI / 180.0);
        double n = (lat - _anchorLat) * 111320.0;
        return new Point2D(e, n);
    }
}