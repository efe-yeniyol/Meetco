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

    /// <summary>
    /// Captures and encodes on a short-lived STA thread (WPF imaging is happiest there), so a
    /// large screen never freezes the UI while the JPEG is encoded.
    /// </summary>
    public Task<ScreenContext> CaptureMainDisplayAsync()
    {
        var completion = new TaskCompletionSource<ScreenContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Capture());
            }
            catch (Exception error)
            {
                completion.SetException(new InvalidOperationException("The screenshot could not be taken: " + error.Message, error));
            }
        })
        {
            IsBackground = true,
            Name = "Meetco screenshot",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static ScreenContext Capture()
    {
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("No display found.");

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        BitmapSource source;
        try
        {
            if (screenDc == IntPtr.Zero) throw new InvalidOperationException("The screen could not be read.");
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException("Out of graphics memory.");
            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            if (!NativeMethods.BitBlt(memoryDc, 0, 0, width, height, screenDc, 0, 0, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
            {
                throw new InvalidOperationException("Screen copy failed.");
            }
            // The bitmap must be deselected before WPF (and DeleteObject) can use it.
            NativeMethods.SelectObject(memoryDc, previous);
            previous = IntPtr.Zero;
            source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            if (previous != IntPtr.Zero) NativeMethods.SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) NativeMethods.DeleteDC(memoryDc);
            if (screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
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
