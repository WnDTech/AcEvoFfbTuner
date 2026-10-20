using AcEvoFfbTuner.Core.TrackMapping;
using FluentAssertions;
using Xunit;

namespace AcEvoFfbTuner.Tests;

public class TrackGeoFitterTests
{
    private const double Lat0 = 45.62, Lon0 = 9.28;
    private const double R = 700;
    private const int OsmPoints = 300;
    private const int SfVertex = 75;

    // Asymmetric closed curve (~4.6 km): its mirror image is not a rotation of itself,
    // so handedness is identifiable — like a real circuit.
    private static (double e, double n) Curve(double th)
    {
        double r = R * (1 + 0.25 * Math.Sin(2 * th) + 0.15 * Math.Cos(3 * th) + 0.10 * Math.Sin(5 * th));
        return (r * Math.Cos(th), r * Math.Sin(th));
    }

    private static (double e, double n) CurveWithLateral(double th, double lateral)
    {
        var p = Curve(th);
        var q = Curve(th + 1e-4);
        double tx = q.e - p.e, ty = q.n - p.n, len = Math.Sqrt(tx * tx + ty * ty);
        return (p.e - ty / len * lateral, p.n + tx / len * lateral);
    }

    private static List<TrackPoint> OsmLayout()
    {
        var proj = new LocalProjection(Lat0, Lon0);
        var layout = new List<TrackPoint>(OsmPoints);
        for (int k = 0; k < OsmPoints; k++)
        {
            var (e, n) = Curve(2 * Math.PI * k / OsmPoints);
            var (lat, lon) = proj.ToGps(e, n);
            layout.Add(new TrackPoint(lat, lon));
        }
        return layout;
    }

    private sealed record Drive(List<GeoPathSample> Path, List<(double e, double n)> TruthEn, double ExpectedSf);

    /// <summary>
    /// Drive part of a lap and express it in a synthetic game frame: rotated 37°, offset,
    /// optionally mirrored (game Z = -north), optionally against the OSM vertex order.
    /// </summary>
    private static Drive MakeDrive(bool mirrored, bool reversed, double lapFraction, bool withNpos = true)
    {
        const int fine = 9000;
        var arc = new double[fine + 1];
        for (int i = 1; i <= fine; i++)
        {
            var a = Curve(2 * Math.PI * (i - 1) / fine);
            var b = Curve(2 * Math.PI * i / fine);
            arc[i] = arc[i - 1] + Math.Sqrt((b.e - a.e) * (b.e - a.e) + (b.n - a.n) * (b.n - a.n));
        }
        double L = arc[fine];
        int sfFine = SfVertex * fine / OsmPoints;
        double sfArc = arc[sfFine];

        double alpha = 37 * Math.PI / 180, tE = -1234, tN = 567;
        double c = Math.Cos(-alpha), s = Math.Sin(-alpha);
        var path = new List<GeoPathSample>();
        var truth = new List<(double e, double n)>();
        int count = (int)(fine * lapFraction);
        int start = fine / 3;
        for (int k = 0; k < count; k++)
        {
            int i = reversed ? ((start - k) % fine + fine) % fine : (start + k) % fine;
            double th = 2 * Math.PI * i / fine;
            var en = CurveWithLateral(th, 3 * Math.Sin(th * 7)); // racing line wanders ±3 m
            double dx = en.e - tE, dy = en.n - tN;
            double hx = c * dx - s * dy, hy = s * dx + c * dy;
            double d = reversed ? sfArc - arc[i] : arc[i] - sfArc;
            float npos = withNpos ? (float)(((d % L) + L) % L / L) : 0f;
            path.Add(new GeoPathSample((float)hx, (float)(mirrored ? -hy : hy), npos));
            truth.Add(en);
        }
        return new Drive(path, truth, sfArc / L);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Fit_RecoversTransformHandednessDirectionAndSf(bool mirrored, bool reversed)
    {
        var drive = MakeDrive(mirrored, reversed, 0.6);

        var fit = TrackGeoFitter.Fit(drive.Path, OsmLayout(), out var diag);

        fit.Should().NotBeNull(diag);
        fit!.Mirrored.Should().Be(mirrored, diag);
        fit.Reversed.Should().Be(reversed, diag);
        fit.MedianResidualM.Should().BeLessThan(5, diag);
        fit.SfFraction.Should().BeApproximately(drive.ExpectedSf, 0.005, diag);

        // Placement error of the driven samples, in meters.
        var proj = new LocalProjection(Lat0, Lon0);
        var errs = new List<double>();
        for (int i = 0; i < drive.Path.Count; i += 50)
        {
            var (lat, lon) = fit.WorldToGps(drive.Path[i].X, drive.Path[i].Z);
            var (e, n) = proj.ToLocal(lat, lon);
            errs.Add(Math.Sqrt((e - drive.TruthEn[i].e) * (e - drive.TruthEn[i].e) + (n - drive.TruthEn[i].n) * (n - drive.TruthEn[i].n)));
        }
        errs.Sort();
        errs[errs.Count / 2].Should().BeLessThan(5, diag);
    }

    [Fact]
    public void Fit_WithoutNpos_SolvesTransformButNoSfAnchor()
    {
        var drive = MakeDrive(false, false, 0.6, withNpos: false);
        var fit = TrackGeoFitter.Fit(drive.Path, OsmLayout(), out var diag);
        fit.Should().NotBeNull(diag);
        fit!.SfFraction.Should().Be(-1);
    }

    [Fact]
    public void Fit_ShortPath_ReturnsNull()
    {
        var drive = MakeDrive(false, false, 0.15);
        TrackGeoFitter.Fit(drive.Path, OsmLayout(), out _).Should().BeNull();
    }

    [Fact]
    public void Fit_FeaturelessCircle_ReturnsNull()
    {
        var proj = new LocalProjection(Lat0, Lon0);
        var layout = new List<TrackPoint>();
        for (int k = 0; k < OsmPoints; k++)
        {
            double th = 2 * Math.PI * k / OsmPoints;
            var (lat, lon) = proj.ToGps(R * Math.Cos(th), R * Math.Sin(th));
            layout.Add(new TrackPoint(lat, lon));
        }
        var path = new List<GeoPathSample>();
        for (int i = 0; i < 3000; i++)
        {
            double th = 2 * Math.PI * i / 4400;
            path.Add(new GeoPathSample((float)(R * Math.Cos(th)), (float)(R * Math.Sin(th)), 0f));
        }
        // A circle is rotation-symmetric and mirror-symmetric: no unique solution.
        TrackGeoFitter.Fit(path, layout, out _).Should().BeNull();
    }

    [Fact]
    public void LocalProjection_RoundTrips()
    {
        var proj = new LocalProjection(50.437, 5.97);
        var (lat, lon) = proj.ToGps(1234.5, -678.9);
        var (e, n) = proj.ToLocal(lat, lon);
        e.Should().BeApproximately(1234.5, 1e-6);
        n.Should().BeApproximately(-678.9, 1e-6);
    }
}
