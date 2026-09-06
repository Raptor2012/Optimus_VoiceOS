namespace Optimus.Providers.Tests.Desktop;

using System;
using Optimus.Providers.Desktop;
using Xunit;

public sealed class ScreenCaptureTests
{
    private sealed class FakeVramMonitor : IVramMonitor
    {
        public long AvailableBytes { get; set; } = 6144L * 1024 * 1024; // 6 GB default
        public long TotalBytes { get; set; } = 8192L * 1024 * 1024; // 8 GB default

        public long GetAvailableVramBytes() => AvailableBytes;
        public long GetTotalVramBytes() => TotalBytes;
        public bool HasSufficientHeadroom(long requiredHeadroomBytes) => AvailableBytes >= requiredHeadroomBytes;
    }

    private sealed class FakeCaptureBackend : IScreenCaptureBackend
    {
        public IntPtr LastCapturedHwnd { get; private set; }
        public ScreenRegion? LastRegion { get; private set; }
        public double LastScale { get; private set; }

        public CapturedScreen Capture(IntPtr hwnd, ScreenRegion? region = null, double scaleFactor = 1.0)
        {
            LastCapturedHwnd = hwnd;
            LastRegion = region;
            LastScale = scaleFactor;

            return new CapturedScreen(
                ImageBytes: new byte[] { 0x89, 0x50, 0x4E, 0x47 }, // PNG magic
                Width: region?.Width ?? 800,
                Height: region?.Height ?? 600,
                CropOriginX: 100 + (region?.X ?? 0),
                CropOriginY: 200 + (region?.Y ?? 0),
                ScaleFactor: scaleFactor,
                CapturedAtUtc: DateTimeOffset.UtcNow);
        }
    }

    [Fact]
    public void CoordinateMapping_CalculatesAccurateBidirectionalCoordinates()
    {
        // 1.25 DPI scale, crop origin at (100, 200)
        var capture = new CapturedScreen(
            ImageBytes: Array.Empty<byte>(),
            Width: 1000,
            Height: 800,
            CropOriginX: 100,
            CropOriginY: 200,
            ScaleFactor: 1.25,
            CapturedAtUtc: DateTimeOffset.UtcNow);

        // Point inside captured image: (250, 500)
        // Screen X = 100 + (250 / 1.25) = 100 + 200 = 300
        // Screen Y = 200 + (500 / 1.25) = 200 + 400 = 600
        ScreenPoint screenPt = capture.MapImageToScreen(250, 500);
        Assert.Equal(300, screenPt.X);
        Assert.Equal(600, screenPt.Y);

        // Reverse map from screen coordinates (300, 600)
        // Image X = (300 - 100) * 1.25 = 250
        // Image Y = (600 - 200) * 1.25 = 500
        ScreenPoint imagePt = capture.MapScreenToImage(300, 600);
        Assert.Equal(250, imagePt.X);
        Assert.Equal(500, imagePt.Y);
    }

    [Fact]
    public void EnsureVramHeadroom_ThrowsWhenHeadroomIsExhausted()
    {
        var vram = new FakeVramMonitor
        {
            // Only 800 MB available, but 1.5 GB is required
            AvailableBytes = 800L * 1024 * 1024
        };

        var backend = new FakeCaptureBackend();
        var capture = new ScreenCapture(
            vramMonitor: vram,
            backend: backend,
            reservedVramHeadroomBytes: 1536L * 1024 * 1024);

        VramHeadroomException ex = Assert.Throws<VramHeadroomException>(() =>
            capture.CaptureWindow(new IntPtr(1234)));

        Assert.Contains("Insufficient VRAM headroom", ex.Message);
        Assert.Contains("800 MiB", ex.Message);
    }

    [Fact]
    public void EnsureVramHeadroom_SucceedsWhenSufficientHeadroomAvailable()
    {
        var vram = new FakeVramMonitor
        {
            // 4 GB available, 1.5 GB reserved
            AvailableBytes = 4096L * 1024 * 1024
        };

        var backend = new FakeCaptureBackend();
        var capture = new ScreenCapture(
            vramMonitor: vram,
            backend: backend,
            reservedVramHeadroomBytes: 1536L * 1024 * 1024);

        CapturedScreen result = capture.CaptureWindow(new IntPtr(1234), scaleFactor: 1.5);

        Assert.NotNull(result);
        Assert.Equal(new IntPtr(1234), backend.LastCapturedHwnd);
        Assert.Equal(1.5, backend.LastScale);
        Assert.Equal(1.5, result.ScaleFactor);
    }
}
