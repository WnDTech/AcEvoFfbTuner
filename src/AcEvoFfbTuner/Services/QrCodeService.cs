using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using QRCoder;

namespace AcEvoFfbTuner.Services;

/// <summary>
/// Generates QR code images for overlay URLs so users can scan them with a phone.
/// App code — shared helper, no game-specific logic.
/// </summary>
public static class QrCodeService
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>
    /// Renders <paramref name="text"/> as a QR code BitmapSource, or null on failure.
    /// </summary>
    public static BitmapSource? GenerateQrCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            using var qr = new QRCode(data);
            // 20 px per module would be oversized; 5 gives ~165px for typical overlay URLs.
            // WPF scales to the Image control size with HighQuality scaling.
            using var bitmap = qr.GetGraphic(5, Color.Black, Color.White, drawQuietZones: true);
            IntPtr hBitmap = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }
}
