using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AcEvoFfbTuner.Core.TrackMapping
{
    public class GroundControlPoint
    {
        public float GameX { get; set; }
        public float GameZ { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string PointId { get; set; } = "";
        public float Weight { get; set; } = 1.0f;
    }

    public class TrackAlignment
    {
        public string TrackName { get; set; } = "";
        public List<GroundControlPoint> Points { get; set; } = new();
        public string Method { get; set; } = "Unknown"; // "CornerMatch", "Manual", "AutoSave"
        public DateTime CreatedAt { get; set; }
        public float ErrorResidual { get; set; } // RMS error in meters
    }

    public static class TrackAlignmentService
    {
        private static readonly string AlignmentDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "TrackAlignments");

        public static (float lat, float lon)? LookupTrackLocation(string trackName)
        {
            return SatelliteMapService.LookupTrackLocation(trackName);
        }

        public static float? LookupTrackRotation(string trackName)
        {
            return SatelliteMapService.LookupTrackRotation(trackName);
        }

        public static (float lat, float lon, float rotationDeg)? LoadCalibration(string trackName)
        {
            return SatelliteMapService.LoadCalibration(trackName);
        }

    public static void SaveCalibration(string trackName, float lat, float lon, float rotationDeg)
    {
        SatelliteMapService.SaveCalibration(trackName, lat, lon, rotationDeg);
    }

    public static void DeleteCalibration(string trackName)
    {
        SatelliteMapService.DeleteCalibration(trackName);
    }

    public static void SaveAlignment(TrackAlignment alignment)
    {
        if (string.IsNullOrWhiteSpace(alignment?.TrackName)) return;
        try
        {
            Directory.CreateDirectory(AlignmentDir);
            var path = Path.Combine(AlignmentDir, $"{SanitizeFileName(alignment.TrackName)}.json");
            var json = JsonSerializer.Serialize(alignment);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    public static TrackAlignment? LoadAlignment(string trackName)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return null;
        var path = Path.Combine(AlignmentDir, $"{SanitizeFileName(trackName)}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TrackAlignment>(json);
        }
        catch { }
        return null;
    }

    public static void DeleteAlignment(string trackName)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return;
        try
        {
            var path = Path.Combine(AlignmentDir, $"{SanitizeFileName(trackName)}.json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    public static List<string> GetAllAlignmentNames()
    {
        try
        {
            if (!Directory.Exists(AlignmentDir)) return new();
            return Directory.GetFiles(AlignmentDir, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .ToList();
        }
        catch { return new(); }
    }

    private static string SanitizeFileName(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

        public static bool TryComputeRigidTransform(List<GroundControlPoint> points, out float gameCenterX, out float gameCenterZ, out float rotationRad)
        {
            gameCenterX = 0;
            gameCenterZ = 0;
            rotationRad = 0;

            if (points == null || points.Count < 2)
                return false;

            // Step 2: Convert the first point's lat/lon to the origin for the local east/north system.
            var lat0 = points[0].Latitude;
            var lon0 = points[0].Longitude;

            // Step 3: Compute local east/north coordinates for each point.
            var localPoints = new List<(double east, double north)>(points.Count);
            const double degToRad = Math.PI / 180.0;
            const double metersPerDegreeLat = 111320.0;

            foreach (var p in points)
            {
                double dLon = p.Longitude - lon0;
                double dLat = p.Latitude - lat0;
                double east = dLon * degToRad * metersPerDegreeLat * Math.Cos(lat0 * degToRad);
                double north = dLat * degToRad * metersPerDegreeLat;
                localPoints.Add((east, north));
            }

            // Step 4: Form the point lists.
            var gamePoints = new List<(float x, float z)>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                gamePoints.Add((points[i].GameX, points[i].GameZ));
            }

            // Step 5: Compute centroids.
            float muPx = 0, muPz = 0;
            double muQx = 0, muQy = 0;
            foreach (var p in gamePoints)
            {
                muPx += p.x;
                muPz += p.z;
            }
            muPx /= gamePoints.Count;
            muPz /= gamePoints.Count;

            foreach (var q in localPoints)
            {
                muQx += q.east;
                muQy += q.north;
            }
            muQx /= localPoints.Count;
            muQy /= localPoints.Count;

            // Step 6: Compute centered points and the matrix H.
            double h00 = 0, h01 = 0, h10 = 0, h11 = 0;
            for (int i = 0; i < gamePoints.Count; i++)
            {
                double px = gamePoints[i].x - muPx;
                double pz = gamePoints[i].z - muPz;
                double qx = localPoints[i].east - muQx;
                double qy = localPoints[i].north - muQy;

                h00 += px * qx;
                h01 += px * qy;
                h10 += pz * qx;
                h11 += pz * qy;
            }
            h00 /= gamePoints.Count;
            h01 /= gamePoints.Count;
            h10 /= gamePoints.Count;
            h11 /= gamePoints.Count;

            // Step 7: Compute SVD of H = U * D * V^T
            // We'll compute the SVD of a 2x2 matrix manually.
            // The SVD of a 2x2 matrix [a b; c d] can be computed as follows:
            //   Let:
            //       tau = (a + d) / 2
            //       delta = (a - d) / 2
            //       deltaSq = delta * delta;
            //       phi = b * c;
            //       s = Math.Sqrt(deltaSq + phi);
            //   Then the singular values are:
            //       s1 = tau + s;
            //       s2 = tau - s;
            //   And the eigenvectors for V^T are given by:
            //       For s1: (b, s1 - a)
            //       For s2: (b, s2 - a)
            //   But note: we need to handle the case when b==0 and c==0.
            //   Alternatively, we can use the formula from:
            //   https://en.wikipedia.org/wiki/Singular_value_decomposition#Singular_values_and_singular_vectors
            //   We'll do a more robust method by computing the eigenvalues of H^T * H.

            // Instead, we'll compute the eigenvalues of H^T * H, which is symmetric and positive semi-definite.
            // Let B = H^T * H = [h00*h00 + h10*h10, h00*h01 + h10*h11; h00*h01 + h10*h11, h01*h01 + h11*h11]
            double b00 = h00 * h00 + h10 * h10;
            double b01 = h00 * h01 + h10 * h11;
            double b10 = b01; // symmetric
            double b11 = h01 * h01 + h11 * h11;

            // The eigenvalues of B are the squares of the singular values of H.
            // Solve the characteristic equation: lambda^2 - (b00+b11)*lambda + (b00*b11 - b01*b10) = 0
            double traceB = b00 + b11;
            double detB = b00 * b11 - b01 * b10;
            double discriminant = traceB * traceB - 4 * detB;
            if (discriminant < 0)
                discriminant = 0; // due to floating point

            double lambda1 = (traceB + Math.Sqrt(discriminant)) / 2;
            double lambda2 = (traceB - Math.Sqrt(discriminant)) / 2;
            // Singular values are the square roots of the eigenvalues of B.
            double s1 = Math.Sqrt(Math.Max(0, lambda1));
            double s2 = Math.Sqrt(Math.Max(0, lambda2));

            // Now, we want the eigenvectors of B for the singular values.
            // For lambda1:
            //   (b00 - lambda1) * x + b01 * y = 0
            //   b01 * x + (b11 - lambda1) * y = 0
            // We can take x = b01, y = lambda1 - b00 (if b01 != 0) or x = lambda2 - b11, y = b10 (if b10 != 0)
            double v1x = 0, v1y = 0;
            if (Math.Abs(b01) > 1e-10)
            {
                v1x = b01;
                v1y = lambda1 - b00;
            }
            else if (Math.Abs(b10) > 1e-10)
            {
                v1x = lambda2 - b11;
                v1y = b10;
            }
            else
            {
                // B is diagonal
                v1x = 1;
                v1y = 0;
            }
            double len1 = Math.Sqrt(v1x * v1x + v1y * v1y);
            if (len1 > 1e-10)
            {
                v1x /= len1;
                v1y /= len1;
            }
            else
            {
                v1x = 1;
                v1y = 0;
            }

            // For lambda2, we can take the orthogonal vector: (-v1y, v1x)
            double v2x = -v1y;
            double v2y = v1x;
            double len2 = Math.Sqrt(v2x * v2x + v2y * v2y);
            if (len2 > 1e-10)
            {
                v2x /= len2;
                v2y /= len2;
            }
            else
            {
                v2x = 0;
                v2y = 1;
            }

            // Now, the matrix V = [v1, v2] (each column is an eigenvector)
            // But note: the eigenvectors of B are the columns of V, and we have V = [v1 v2]
            // Then, the SVD is H = U * D * V^T, where U = H * V * D^{-1}
            // We don't need U and D explicitly for the rotation.
            // The rotation matrix R is given by U * V^T.
            // We can compute R = H * V * (V^T * V)^{-1} * V^T = H * V * V^T   [if V is orthogonal, which it is]
            // But note: V is orthogonal by construction.
            // So R = H * V * V^T.

            // Compute R = H * V * V^T
            // First, compute V * V^T = [[v1x*v1x + v1y*v1y, v1x*v2x + v1y*v2y], [v2x*v1x + v2y*v1y, v2x*v2x + v2y*v2y]] = [[1,0],[0,1]] because v1 and v2 are orthonormal.
            // So V * V^T = I.
            // Therefore, R = H * V * V^T = H * I = H? That can't be right.

            // Actually, we have: B = V * D^2 * V^T, and H = U * D * V^T.
            // We want R = U * V^T.
            // Note that U = H * V * D^{-1} (if D is invertible).
            // So R = H * V * D^{-1} * V^T.

            // We'll compute R = H * V * D^{-1} * V^T.
            // Since V is orthogonal, V^T = V^{-1}.
            // So R = H * V * D^{-1} * V^T = H * (V * D^{-1} * V^T) = H * (V * V^T * D^{-1})? Not exactly.

            // Let's do it step by step.

            // We have V = [v1x, v2x; v1y, v2y] (column-major: first column is v1, second is v2)
            // V^T = [v1x, v1y; v2x, v2y]

            // Compute D^{-1} = [[1/s1, 0], [0, 1/s2]] (if s1 and s2 are not zero)
            double invS1 = s1 > 1e-10 ? 1.0 / s1 : 0;
            double invS2 = s2 > 1e-10 ? 1.0 / s2 : 0;

            // Compute W = V * D^{-1} = [v1x*invS1, v2x*invS2; v1y*invS1, v2y*invS2]
            double w00 = v1x * invS1;
            double w01 = v2x * invS2;
            double w10 = v1y * invS1;
            double w11 = v2y * invS2;

            // Compute R = H * W = [h00*w00 + h01*w10, h00*w01 + h01*w11; h10*w00 + h11*w10, h10*w01 + h11*w11]
            double r00 = h00 * w00 + h01 * w10;
            double r01 = h00 * w01 + h01 * w11;
            double r10 = h10 * w00 + h11 * w10;
            double r11 = h10 * w01 + h11 * w11;

            // Now, R should be close to a rotation matrix (orthonormal with determinant 1).
            // We'll extract the rotation angle from R.
            // For a rotation matrix [cosθ, -sinθ; sinθ, cosθ], we have:
            //   cosθ = r00
            //   sinθ = r10
            // But note: our R might not be exactly a rotation matrix due to scaling and noise.
            // We'll project it to the nearest rotation matrix by setting:
            //   cosθ = (r00 + r11) / 2   [average of the diagonal]? Not exactly.
            // Instead, we can compute the angle from the first column: [r00, r10] should be [cosθ, sinθ].
            double cosTheta = r00;
            double sinTheta = r10;
            double len = Math.Sqrt(cosTheta * cosTheta + sinTheta * sinTheta);
            if (len > 1e-10)
            {
                cosTheta /= len;
                sinTheta /= len;
            }
            else
            {
                cosTheta = 1;
                sinTheta = 0;
            }

            // Now, we have the rotation matrix R_rot = [cosTheta, -sinTheta; sinTheta, cosTheta]
            // But note: our R was computed as H * V * D^{-1} * V^T, and we expect it to be s * R_rot for some scale s.
            // We have ignored the scale, so we will use this rotation.

            // Step 12: Compute the translation.
            // We have: q = s * R_rot * p + t
            // We want to find t such that the mean of q - s * R_rot * p is t.
            // We'll compute the mean of (q - s * R_rot * p) for s=1 (since we are ignoring scale).
            double sumTx = 0, sumTy = 0;
            for (int i = 0; i < gamePoints.Count; i++)
            {
                double px = gamePoints[i].x;
                double pz = gamePoints[i].z;
                double qx = localPoints[i].east;
                double qy = localPoints[i].north;

                // Apply rotation by R_rot (with scale=1) to the game point:
                double rx = cosTheta * px - sinTheta * pz;
                double ry = sinTheta * px + cosTheta * pz;

                sumTx += qx - rx;
                sumTy += qy - ry;
            }
            double tx = sumTx / gamePoints.Count;
            double ty = sumTy / gamePoints.Count;

            // Step 13: Compute the game coordinates that map to the local origin (0,0).
            // Solve: [0]   [cosTheta, -sinTheta] [gameX]   [tx]
            //        [0] = [sinTheta,  cosTheta] [gameZ] + [ty]
            // => [gameX]   [ cosTheta,  sinTheta] [-tx]
            //    [gameZ] = [-sinTheta,  cosTheta] [-ty]
            double gcX = cosTheta * (-tx) + sinTheta * (-ty);
            double gcZ = -sinTheta * (-tx) + cosTheta * (-ty);

            // Step 14: Set the output parameters.
            gameCenterX = (float)gcX;
            gameCenterZ = (float)gcZ;
            rotationRad = (float)(-Math.Atan2(sinTheta, cosTheta)); // because we want _rotationRad = -theta, and theta = atan2(sinTheta, cosTheta)

            return true;
        }
    }
}