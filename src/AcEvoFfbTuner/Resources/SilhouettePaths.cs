using System.Windows;
using System.Windows.Media;
using AcEvoFfbTuner.Models;

namespace AcEvoFfbTuner.Resources;

public static class SilhouettePaths
{
    private static readonly Size ViewBox = new(48, 48);

    public static Geometry GetGeometry(DeviceIconType type) => type switch
    {
        DeviceIconType.Wheelbase => WheelbaseGeometry(),
        DeviceIconType.Wheel => WheelGeometry(),
        DeviceIconType.Pedals => PedalsGeometry(),
        DeviceIconType.Haptics => HapticsGeometry(),
        DeviceIconType.Game => GameGeometry(),
        _ => WheelbaseGeometry()
    };

    private static Geometry WheelbaseGeometry()
    {
        var group = new GeometryGroup();

        // Main DD motor casing (rounded rectangle)
        group.Children.Add(new RectangleGeometry
        {
            Rect = new Rect(6, 4, 36, 40),
            RadiusX = 6,
            RadiusY = 6
        });

        // Top vent slots
        for (int i = 0; i < 4; i++)
        {
            double y = 12 + i * 7;
            group.Children.Add(new RectangleGeometry
            {
                Rect = new Rect(12, y, 24, 2.5),
                RadiusX = 1,
                RadiusY = 1
            });
        }

        // Front connector port detail
        group.Children.Add(new RectangleGeometry
        {
            Rect = new Rect(18, 34, 12, 4),
            RadiusX = 1.5,
            RadiusY = 1.5
        });

        return group;
    }

    private static Geometry WheelGeometry()
    {
        var group = new GeometryGroup();

        // Outer rim (thick ring)
        group.Children.Add(new EllipseGeometry
        {
            Center = new Point(24, 24),
            RadiusX = 20,
            RadiusY = 20
        });
        group.Children.Add(new EllipseGeometry
        {
            Center = new Point(24, 24),
            RadiusX = 14,
            RadiusY = 14
        });

        // Three spokes
        group.Children.Add(new RectangleGeometry
        {
            Rect = new Rect(22.5, 10, 3, 16),
            RadiusX = 1.5,
            RadiusY = 1.5
        });
        group.Children.Add(new RectangleGeometry
        {
            Rect = new Rect(10, 22.5, 16, 3),
            RadiusX = 1.5,
            RadiusY = 1.5
        });
        group.Children.Add(new RectangleGeometry
        {
            Rect = new Rect(22.5, 22.5, 16, 3),
            RadiusX = 1.5,
            RadiusY = 1.5
        });

        // Center hub
        group.Children.Add(new EllipseGeometry
        {
            Center = new Point(24, 24),
            RadiusX = 5,
            RadiusY = 5
        });

        return group;
    }

    private static Geometry PedalsGeometry()
    {
        var group = new GeometryGroup();

        // Clutch (left)
        group.Children.Add(new RectangleGeometry
        {
            Rect = new Rect(4, 8, 10, 34),
            RadiusX = 3,
            RadiusY = 3
        });

        // Brake (center)
        group.Children.Add(new RectangleGeometry
        {
            Rect = new Rect(19, 4, 10, 38),
            RadiusX = 3,
            RadiusY = 3
        });

        // Gas (right)
        group.Children.Add(new RectangleGeometry
        {
            Rect = new Rect(34, 12, 10, 30),
            RadiusX = 3,
            RadiusY = 3
        });

        // Pedal surface lines
        foreach (var rect in new[] {
            new Rect(6, 10, 6, 2),
            new Rect(21, 6, 6, 2),
            new Rect(36, 14, 6, 2)
        })
        {
            group.Children.Add(new RectangleGeometry
            {
                Rect = rect,
                RadiusX = 0.5,
                RadiusY = 0.5
            });
        }

        return group;
    }

    private static Geometry HapticsGeometry()
    {
        var group = new GeometryGroup();

        // Seat cushion / pad base
        group.Children.Add(new RectangleGeometry
        {
            Rect = new Rect(4, 10, 40, 28),
            RadiusX = 8,
            RadiusY = 8
        });

        // Motor bumps (HF8 zone markers)
        double[,] motors =
        {
            { 10, 16 }, { 22, 16 }, { 34, 16 },
            { 10, 26 }, { 22, 26 }, { 34, 26 }
        };

        for (int i = 0; i < motors.GetLength(0); i++)
        {
            group.Children.Add(new EllipseGeometry
            {
                Center = new Point(motors[i, 0], motors[i, 1]),
                RadiusX = 2.5,
                RadiusY = 2.5
            });
        }

        return group;
    }

    private static Geometry GameGeometry()
    {
        var group = new GeometryGroup();

        // Monitor screen
        group.Children.Add(new RectangleGeometry
        {
            Rect = new Rect(6, 4, 36, 28),
            RadiusX = 3,
            RadiusY = 3
        });

        // Screen inner bezel
        group.Children.Add(new RectangleGeometry
        {
            Rect = new Rect(10, 8, 28, 20),
            RadiusX = 1,
            RadiusY = 1
        });

        // Stand neck
        group.Children.Add(new RectangleGeometry
        {
            Rect = new Rect(22, 32, 4, 6),
            RadiusX = 1,
            RadiusY = 1
        });

        // Stand base
        group.Children.Add(new RectangleGeometry
        {
            Rect = new Rect(14, 38, 20, 4),
            RadiusX = 2,
            RadiusY = 2
        });

        return group;
    }
}
