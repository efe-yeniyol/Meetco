using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Meetco.Core.Assistant;

namespace MeetcoApp.Platform;

/// <summary>
/// Captures the primary display with GDI and encodes it as JPEG, as optional visual context for
/// the next answer. Meetco's own windows are included unless "Hide from screen sharing" is on.
/// </summary>
public sealed class ScreenshotService : IScreenContextCapture
{
    private const int MaxWidth = 1600;

    public Task<ScreenContext> CaptureMainDisplayAsync()
    {
        try
        {
            return Task.FromResult(Capture());
        }
        catch (Exception error)
        {
            return Task.FromException<ScreenContext>(new InvalidOperationException("The screenshot could not be taken: " + error.Message, error));
        }
    }

    private static ScreenContext Capture()
    {
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("No display found.");

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        var memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
        var bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
        var previous = NativeMethods.SelectObject(memoryDc, bitmap);
        BitmapSource source;
        try
        {
            if (!NativeMethods.BitBlt(memoryDc, 0, 0, width, height, screenDc, 0, 0, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            {
                throw new InvalidOperationException("Screen copy failed.");
            }
            NativeMethods.SelectObject(memoryDc, previous);
            source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            NativeMethods.DeleteObject(bitmap);
            NativeMethods.DeleteDC(memoryDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }

        BitmapSource scaled = source;
        if (source.PixelWidth > MaxWidth)
        {
            var scale = (double)MaxWidth / source.PixelWidth;
            scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }
        scaled.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = 70 };
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new ScreenContext(stream.ToArray(), DateTimeOffset.Now, scaled.PixelWidth, scaled.PixelHeight);
    }
}
