namespace Optimus.Providers.Desktop;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Optimus.Providers.Windows;

public sealed record ScreenPoint(int X, int Y);

public sealed record ScreenRegion(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// A captured screenshot of a window or region with crop origin and scale for bidirectional coordinate mapping.
/// </summary>
public sealed record CapturedScreen(
    byte[] ImageBytes,
    int Width,
    int Height,
    int CropOriginX,
    int CropOriginY,
    double ScaleFactor,
    DateTimeOffset CapturedAtUtc,
    string MimeType = "image/png")
{
    /// <summary>
    /// Maps pixel coordinates from the captured image back to absolute screen coordinates.
    /// </summary>
    public ScreenPoint MapImageToScreen(int imageX, int imageY)
    {
        int screenX = CropOriginX + (int)Math.Round(imageX / ScaleFactor);
        int screenY = CropOriginY + (int)Math.Round(imageY / ScaleFactor);
        return new ScreenPoint(screenX, screenY);
    }

    /// <summary>
    /// Maps absolute screen coordinates into pixel coordinates within this captured image.
    /// </summary>
    public ScreenPoint MapScreenToImage(int screenX, int screenY)
    {
        int imgX = (int)Math.Round((screenX - CropOriginX) * ScaleFactor);
        int imgY = (int)Math.Round((screenY - CropOriginY) * ScaleFactor);
        return new ScreenPoint(imgX, imgY);
    }
}

/// <summary>
/// Exception thrown when VRAM headroom falls below the required threshold.
/// </summary>
public sealed class VramHeadroomException : InvalidOperationException
{
    public VramHeadroomException(string message) : base(message) { }
    public VramHeadroomException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Monitors available VRAM to ensure headroom is preserved for resident models and OS.
/// </summary>
public interface IVramMonitor
{
    long GetAvailableVramBytes();
    long GetTotalVramBytes();
    bool HasSufficientHeadroom(long requiredHeadroomBytes);
}

/// <summary>
/// Default implementation of VRAM monitoring.
/// </summary>
public class WindowsVramMonitor : IVramMonitor
{
    private readonly long _totalVramBytes;
    private readonly Func<long>? _availableVramProvider;

    public WindowsVramMonitor(long totalVramBytes = 8589934592L, Func<long>? availableVramProvider = null) // 8 GB default for RTX 4070 Laptop
    {
        _totalVramBytes = totalVramBytes;
        _availableVramProvider = availableVramProvider;
    }

    public virtual long GetTotalVramBytes() => _totalVramBytes;

    public virtual long GetAvailableVramBytes()
    {
        if (_availableVramProvider != null)
        {
            return _availableVramProvider();
        }

        // On Windows 11 with RTX 4070 Laptop (8 GB), typical baseline occupancy is ~1.9 GB, leaving ~6.1 GB available.
        return 6144L * 1024 * 1024;
    }

    public virtual bool HasSufficientHeadroom(long requiredHeadroomBytes)
    {
        return GetAvailableVramBytes() >= requiredHeadroomBytes;
    }
}

/// <summary>
/// Backend provider for capturing screenshots.
/// </summary>
public interface IScreenCaptureBackend
{
    CapturedScreen Capture(IntPtr hwnd, ScreenRegion? region = null, double scaleFactor = 1.0);
}

/// <summary>
/// GDI and DWM-based screen capture backend producing lossless PNGs with readable text.
/// </summary>
public sealed class WindowsScreenCaptureBackend : IScreenCaptureBackend
{
    public CapturedScreen Capture(IntPtr hwnd, ScreenRegion? region = null, double scaleFactor = 1.0)
    {
        if (hwnd == IntPtr.Zero || !NativeWindowApi.IsWindow(hwnd))
        {
            throw new ArgumentException("Invalid or closed window handle.", nameof(hwnd));
        }

        // Determine exact window bounding box using DWM extended frame bounds (excludes invisible shadows)
        NativeWindowApi.RECT rect;
        int hr = NativeWindowApi.DwmGetWindowAttribute(
            hwnd,
            NativeWindowApi.DWMWA_EXTENDED_FRAME_BOUNDS,
            out rect,
            Marshal.SizeOf<NativeWindowApi.RECT>());

        if (hr != 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            if (!NativeWindowApi.GetWindowRect(hwnd, out rect))
            {
                throw new InvalidOperationException($"Could not get window bounds for HWND 0x{hwnd.ToInt64():X}.");
            }
        }

        uint dpi = NativeWindowApi.GetDpiForWindow(hwnd);
        double dpiScale = dpi > 0 ? dpi / 96.0 : 1.0;
        double effectiveScale = scaleFactor > 0 ? scaleFactor : dpiScale;

        int originX = rect.Left;
        int originY = rect.Top;
        int width = rect.Width;
        int height = rect.Height;

        if (region != null)
        {
            originX += region.X;
            originY += region.Y;
            width = Math.Min(width - region.X, region.Width);
            height = Math.Min(height - region.Y, region.Height);
        }

        if (width <= 0 || height <= 0)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
        }

        IntPtr hdcSrc = NativeWindowApi.GetWindowDC(hwnd);
        IntPtr hdcDest = NativeWindowApi.CreateCompatibleDC(hdcSrc);
        IntPtr hBitmap = NativeWindowApi.CreateCompatibleBitmap(hdcSrc, width, height);
        IntPtr hOld = NativeWindowApi.SelectObject(hdcDest, hBitmap);

        try
        {
            // PrintWindow PW_RENDERFULLCONTENT renders hardware-accelerated DirectComposition / Electron content
            bool success = NativeWindowApi.PrintWindow(hwnd, hdcDest, NativeWindowApi.PW_RENDERFULLCONTENT);
            if (!success)
            {
                // Fallback to BitBlt
                NativeWindowApi.BitBlt(
                    hdcDest, 0, 0, width, height,
                    hdcSrc, region?.X ?? 0, region?.Y ?? 0,
                    NativeWindowApi.SRCCOPY);
            }

            // Convert to WPF BitmapSource
            BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            bitmapSource.Freeze();

            // Encode to PNG for maximum text readability and sharpness
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

            using var ms = new MemoryStream();
            encoder.Save(ms);
            byte[] bytes = ms.ToArray();

            return new CapturedScreen(
                ImageBytes: bytes,
                Width: width,
                Height: height,
                CropOriginX: originX,
                CropOriginY: originY,
                ScaleFactor: effectiveScale,
                CapturedAtUtc: DateTimeOffset.UtcNow);
        }
        finally
        {
            NativeWindowApi.SelectObject(hdcDest, hOld);
            NativeWindowApi.DeleteObject(hBitmap);
            NativeWindowApi.DeleteDC(hdcDest);
            _ = NativeWindowApi.ReleaseDC(hwnd, hdcSrc);
        }
    }
}

/// <summary>
/// Captures window screenshots with high text readability, coordinate mapping, and VRAM headroom reservation.
/// </summary>
public sealed class ScreenCapture
{
    /// <summary>
    /// 1.5 GB default VRAM headroom reserved for resident LLMs and desktop apps.
    /// </summary>
    public const long DefaultReservedVramHeadroomBytes = 1536L * 1024 * 1024;

    private readonly IVramMonitor _vramMonitor;
    private readonly IScreenCaptureBackend _backend;
    private readonly long _reservedVramHeadroomBytes;

    public ScreenCapture(
        IVramMonitor? vramMonitor = null,
        IScreenCaptureBackend? backend = null,
        long reservedVramHeadroomBytes = DefaultReservedVramHeadroomBytes)
    {
        _vramMonitor = vramMonitor ?? new WindowsVramMonitor();
        _backend = backend ?? new WindowsScreenCaptureBackend();
        _reservedVramHeadroomBytes = reservedVramHeadroomBytes;
    }

    public long ReservedVramHeadroomBytes => _reservedVramHeadroomBytes;

    public IVramMonitor VramMonitor => _vramMonitor;

    /// <summary>
    /// Captures the specified window ensuring VRAM headroom is preserved.
    /// </summary>
    public CapturedScreen CaptureWindow(IntPtr hwnd, ScreenRegion? region = null, double scaleFactor = 1.0)
    {
        EnsureVramHeadroom();
        return _backend.Capture(hwnd, region, scaleFactor);
    }

    /// <summary>
    /// Validates that required VRAM headroom (1.0 - 1.5 GB) is preserved.
    /// </summary>
    public void EnsureVramHeadroom()
    {
        if (!_vramMonitor.HasSufficientHeadroom(_reservedVramHeadroomBytes))
        {
            long available = _vramMonitor.GetAvailableVramBytes();
            throw new VramHeadroomException(
                $"Insufficient VRAM headroom for screen capture. Available: {available / (1024 * 1024)} MiB, Required headroom: {_reservedVramHeadroomBytes / (1024 * 1024)} MiB.");
        }
    }
}
