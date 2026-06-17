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
    private float _cgX, _cgY;

    public bool IsCalibrated => _isCalibrated;
    public float CurrentRotationDeg => (float)(Math.Atan2(_matrixB, _matrixA) * 180.0 / Math.PI);
    public double CurrentRotationRad => Math.Atan2(_matrixB, _matrixA);
    public int PointCount => _lapGamePoints.Count;
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
        _anchorLat = anchorLat;
        _anchorLon = anchorLon;

        if (trackLayout != null)
            foreach (var pt in trackLayout)
                _osmCachedMeters.Add(GpsToMeters(pt.Latitude, pt.Longitude));
        // Pit points are NOT added to _osmCachedMeters — they would contaminate
        // cumulative-distance matching since the car never drives the pit path.
        _ = pitLayout; // pit points are used separately if needed

        _matrixA = 1f;
        _matrixB = 0f;
    }

    public void SeedFirstWaypoint(float spawnX, float spawnZ)
    {
        _lapGamePoints.Clear();
        _lapGamePoints.Add(new Point2D(spawnX, spawnZ));
    }

    public void SetRotationFromRadians(double radians)
    {
        _matrixA = (float)Math.Cos(radians);
        _matrixB = (float)Math.Sin(radians);
        _isCalibrated = true;
    }

    public bool CheckLapCompletion(float carX, float carZ, int lapCount)
    {
        if (_isCalibrated) return true;

        if (lapCount > _prevLapCount)
        {
            _prevLapCount = lapCount;
            if (_lapGamePoints.Count >= 20)
                return ComputeFromLap();
            _lapGamePoints.Clear();
        }
        _prevLapCount = lapCount;

        if (_lapGamePoints.Count == 0 ||
            Math.Abs(carX - _lapGamePoints[^1].X) > 2f ||
            Math.Abs(carZ - _lapGamePoints[^1].Y) > 2f)
        {
            _lapGamePoints.Add(new Point2D(carX, carZ));
        }

        // Fallback: enough points collected
        if (_lapGamePoints.Count >= 500 && !_isCalibrated)
        {
            _prevLapCount = lapCount;
            return ComputeFromLap();
        }

        return false;
    }

    private bool ComputeFromLap()
    {
        if (_lapGamePoints.Count < 20 || _osmCachedMeters.Count < 10) return false;

        // Compute cumulative distances along game lap
        var gameCumDist = new float[_lapGamePoints.Count];
        float gameTotal = 0;
        for (int i = 1; i < _lapGamePoints.Count; i++)
        {
            float dx = _lapGamePoints[i].X - _lapGamePoints[i - 1].X;
            float dy = _lapGamePoints[i].Y - _lapGamePoints[i - 1].Y;
            gameTotal += (float)Math.Sqrt(dx * dx + dy * dy);
            gameCumDist[i] = gameTotal;
        }

        // Compute cumulative distances along OSM cached meters
        var osmCumDist = new float[_osmCachedMeters.Count];
        float osmTotal = 0;
        for (int i = 1; i < _osmCachedMeters.Count; i++)
        {
            float dx = _osmCachedMeters[i].X - _osmCachedMeters[i - 1].X;
            float dy = _osmCachedMeters[i].Y - _osmCachedMeters[i - 1].Y;
            osmTotal += (float)Math.Sqrt(dx * dx + dy * dy);
            osmCumDist[i] = osmTotal;
        }

        if (gameTotal < 10 || osmTotal < 10) return false;

        // Detect OSM direction: compare distance from first game point to
        // first OSM point vs first game point to last OSM point.
        float g0x = _lapGamePoints[0].X;
        float g0z = _lapGamePoints[0].Y;
        float dxFirst = _osmCachedMeters[0].X - g0x;
        float dzFirst = _osmCachedMeters[0].Y - g0z;
        float distFirst = dxFirst * dxFirst + dzFirst * dzFirst;
        float dxLast = _osmCachedMeters[^1].X - g0x;
        float dzLast = _osmCachedMeters[^1].Y - g0z;
        float distLast = dxLast * dxLast + dzLast * dzLast;
        bool osmReversed = distLast < distFirst;

        // Match by normalized position (not GPS proximity!)
        var gcp = new List<GroundControlPoint>();
        for (int gi = 0; gi < _lapGamePoints.Count; gi += 3)
        {
            float normPos = gameTotal > 0 ? gameCumDist[gi] / gameTotal : 0f;

            // Find OSM point with closest normalized position
            int bestOi = 0;
            float bestDiff = float.MaxValue;
            for (int oi = 0; oi < _osmCachedMeters.Count; oi++)
            {
                int idx = osmReversed ? _osmCachedMeters.Count - 1 - oi : oi;
                float onorm = osmTotal > 0 ? osmCumDist[idx] / osmTotal : 0f;
                float diff = Math.Abs(normPos - onorm);
                if (diff < bestDiff) { bestDiff = diff; bestOi = idx; }
            }

            // Convert OSM meters back to GPS
            var osmPt = MetersToGps(_osmCachedMeters[bestOi].X, _osmCachedMeters[bestOi].Y);
            gcp.Add(new GroundControlPoint
            {
                GameX = _lapGamePoints[gi].X,
                GameZ = _lapGamePoints[gi].Y,
                Latitude = osmPt.lat,
                Longitude = osmPt.lon
            });
        }

        if (gcp.Count < 4) return false;

        if (!TrackAlignmentService.TryComputeRigidTransform(gcp, out var gcX, out var gcZ, out var rotRad))
        {
            _lapGamePoints.Clear();
            return false;
        }

        // Quadrant disambiguation: test all 4 rotations and pick the one with
        // smallest GPS reprojection error against GCPs.
        double sumLat = 0, sumLon = 0;
        foreach (var p in gcp) { sumLat += p.Latitude; sumLon += p.Longitude; }
        double originLat = sumLat / gcp.Count;
        double originLon = sumLon / gcp.Count;

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

        // Outlier rejection: re-check error per GCP and keep only inliers.
        var inliers = new List<GroundControlPoint>();
        for (int k = 0; k < gcp.Count; k++)
        {
            double dx = gcp[k].GameX - gcX;
            double dz = gcp[k].GameZ - gcZ;
            double re = Math.Cos(rotRad) * dx - Math.Sin(rotRad) * dz;
            double rn = Math.Sin(rotRad) * dx + Math.Cos(rotRad) * dz;
            double dlat = (gcp[k].Latitude - originLat) * 111320 - rn;
            double dlon = (gcp[k].Longitude - originLon) * 111320 * Math.Cos(originLat * Math.PI / 180.0) - re;
            double resid = Math.Sqrt(dlat * dlat + dlon * dlon);
            if (resid < 50.0) // reject points >50m error
                inliers.Add(gcp[k]);
        }

        if (inliers.Count < 4)
        {
            _lapGamePoints.Clear();
            return false;
        }

        // Re-compute transform with inliers only
        if (!TrackAlignmentService.TryComputeRigidTransform(inliers, out gcX, out gcZ, out rotRad))
        {
            _lapGamePoints.Clear();
            return false;
        }

        // Apply quadrant disambiguation again on refined transform
        for (int q = 0; q < 4; q++)
        {
            double testRot = rotRad + q * Math.PI / 2;
            double err = 0;
            for (int k = 0; k < inliers.Count; k++)
            {
                double dx = inliers[k].GameX - gcX;
                double dz = inliers[k].GameZ - gcZ;
                double re = Math.Cos(testRot) * dx - Math.Sin(testRot) * dz;
                double rn = Math.Sin(testRot) * dx + Math.Cos(testRot) * dz;
                double dlat = (inliers[k].Latitude - originLat) * 111320 - rn;
                double dlon = (inliers[k].Longitude - originLon) * 111320 * Math.Cos(originLat * Math.PI / 180.0) - re;
                err += dlat * dlat + dlon * dlon;
            }
            if (err < bestErr) { bestErr = err; bestRot = testRot; }
        }
        rotRad = (float)bestRot;

        _matrixA = (float)Math.Cos(rotRad);
        _matrixB = (float)Math.Sin(rotRad);
        _cgX = gcX;
        _cgY = gcZ;
        _isCalibrated = true;
        CalibrationLocked?.Invoke();
        return true;
    }

    public (double lat, double lon) GetCarGps(float carX, float carZ)
    {
        float dx, dz;
        if (_isCalibrated)
        {
            dx = carX - _cgX;
            dz = carZ - _cgY;
            float ux = _matrixA * dx - _matrixB * dz;
            float uy = _matrixB * dx + _matrixA * dz;
            double lat = _anchorLat + uy / 111320.0;
            double lon = _anchorLon + ux / (111320.0 * Math.Cos(_anchorLat * Math.PI / 180.0));
            return (lat, lon);
        }

        // Identity pre-calibration
        dx = carX - (_lapGamePoints.Count > 0 ? _lapGamePoints[0].X : carX);
        dz = carZ - (_lapGamePoints.Count > 0 ? _lapGamePoints[0].Y : carZ);
        double alat = _anchorLat + dz / 111320.0;
        double alon = _anchorLon + dx / (111320.0 * Math.Cos(_anchorLat * Math.PI / 180.0));
        return (alat, alon);
    }

    public void Reset()
    {
        _lapGamePoints.Clear();
        _isCalibrated = false;
        _prevLapCount = 0;
        _matrixA = 1f;
        _matrixB = 0f;
        _cgX = _cgY = 0;
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