namespace Optimus.Shell.Controls;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Optimus.Shell.Models;

/// <summary>
/// A small, fully local ink renderer for the companion capsule. The bitmap is regenerated on the
/// compositor tick so the blob remains fluid without a scene graph full of animated elements.
/// </summary>
public partial class FluidInkOrb : UserControl
{
    private const int DefaultSize = 48;
    private WriteableBitmap? _bitmap;
    private byte[]? _pixels;
    private DateTime _lastFrame = DateTime.UtcNow;
    private double _phase;
    private bool _rendering;
    private double _dpiX = 1;
    private double _dpiY = 1;
    private readonly Random _noise = new(17);

    public FluidInkOrb()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => CreateBitmap();
    }

    public static readonly DependencyProperty WidgetStateProperty =
        DependencyProperty.Register(nameof(WidgetState), typeof(WidgetState), typeof(FluidInkOrb),
            new PropertyMetadata(WidgetState.Idle));
    public static readonly DependencyProperty MicEnergyProperty =
        DependencyProperty.Register(nameof(MicEnergy), typeof(double), typeof(FluidInkOrb), new PropertyMetadata(0d));
    public static readonly DependencyProperty PlaybackEnergyProperty =
        DependencyProperty.Register(nameof(PlaybackEnergy), typeof(double), typeof(FluidInkOrb), new PropertyMetadata(0d));
    public static readonly DependencyProperty IsSpeakingReviewProperty =
        DependencyProperty.Register(nameof(IsSpeakingReview), typeof(bool), typeof(FluidInkOrb), new PropertyMetadata(false));
    public static readonly DependencyProperty IsSessionOpenProperty =
        DependencyProperty.Register(nameof(IsSessionOpen), typeof(bool), typeof(FluidInkOrb), new PropertyMetadata(false));
    public static readonly DependencyProperty IsPausedProperty =
        DependencyProperty.Register(nameof(IsPaused), typeof(bool), typeof(FluidInkOrb), new PropertyMetadata(false));
    public static readonly DependencyProperty IsMutedProperty =
        DependencyProperty.Register(nameof(IsMuted), typeof(bool), typeof(FluidInkOrb), new PropertyMetadata(false));
    public static readonly DependencyProperty OrbLabelProperty =
        DependencyProperty.Register(nameof(OrbLabel), typeof(string), typeof(FluidInkOrb), new PropertyMetadata("Ready"));

    public WidgetState WidgetState { get => (WidgetState)GetValue(WidgetStateProperty); set => SetValue(WidgetStateProperty, value); }
    public double MicEnergy { get => (double)GetValue(MicEnergyProperty); set => SetValue(MicEnergyProperty, value); }
    public double PlaybackEnergy { get => (double)GetValue(PlaybackEnergyProperty); set => SetValue(PlaybackEnergyProperty, value); }
    public bool IsSpeakingReview { get => (bool)GetValue(IsSpeakingReviewProperty); set => SetValue(IsSpeakingReviewProperty, value); }
    public bool IsSessionOpen { get => (bool)GetValue(IsSessionOpenProperty); set => SetValue(IsSessionOpenProperty, value); }
    public bool IsPaused { get => (bool)GetValue(IsPausedProperty); set => SetValue(IsPausedProperty, value); }
    public bool IsMuted { get => (bool)GetValue(IsMutedProperty); set => SetValue(IsMutedProperty, value); }
    public string OrbLabel { get => (string)GetValue(OrbLabelProperty); set => SetValue(OrbLabelProperty, value); }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ReadDpi();
        CreateBitmap();
        if (!_rendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }

    private void ReadDpi()
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            _dpiX = target.TransformToDevice.M11;
            _dpiY = target.TransformToDevice.M22;
        }
    }

    private void CreateBitmap()
    {
        if (!IsLoaded) return;
        int width = Math.Max(1, (int)Math.Round(DefaultSize * _dpiX));
        int height = Math.Max(1, (int)Math.Round(DefaultSize * _dpiY));
        _bitmap = new WriteableBitmap(width, height, 96 * _dpiX, 96 * _dpiY, PixelFormats.Bgra32, null);
        _pixels = new byte[width * height * 4];
        InkSurface.Source = _bitmap;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_bitmap == null || _pixels == null) return;
        DateTime now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastFrame).TotalSeconds, 0.001, 0.1);
        _lastFrame = now;
        _phase += dt;
        RenderFrame();
    }

    private void RenderFrame()
    {
        if (_bitmap == null || _pixels == null) return;
        int width = _bitmap.PixelWidth;
        int height = _bitmap.PixelHeight;
        double mic = Math.Clamp(MicEnergy, 0, 1);
        double playback = Math.Clamp(PlaybackEnergy, 0, 1);
        bool listening = WidgetState is WidgetState.Listening or WidgetState.SessionListening or WidgetState.Redictating;
        bool processing = WidgetState is WidgetState.Processing or WidgetState.Sending;
        bool speaking = IsSpeakingReview || WidgetState == WidgetState.ReadingDraft;
        bool error = WidgetState == WidgetState.Error;
        double pulse = speaking ? 0.08 * (0.5 + 0.5 * Math.Sin(_phase * 8.0)) + playback * 0.06 : 0;
        double radius = 0.78 + (listening ? mic * 0.15 : 0) + pulse;
        if (processing) radius += 0.03;

        Color ink = error ? Color.FromRgb(0xE0, 0x53, 0x53) :
            processing ? Color.FromRgb(0xB6, 0xC0, 0xFF) : Color.FromRgb(0x8C, 0x9E, 0xFF);
        Color shadow = error ? Color.FromRgb(0x6E, 0x26, 0x30) : Color.FromRgb(0x3D, 0x4A, 0x6B);
        double rotation = processing ? _phase * 0.9 : speaking ? _phase * 0.12 : 0;
        Span<byte> pixels = _pixels;
        for (int y = 0; y < height; y++)
        {
            double ny = (y + 0.5) / height * 2 - 1;
            for (int x = 0; x < width; x++)
            {
                double nx = (x + 0.5) / width * 2 - 1;
                double ca = Math.Cos(rotation), sa = Math.Sin(rotation);
                double rx = nx * ca - ny * sa, ry = nx * sa + ny * ca;
                double angle = Math.Atan2(ry, rx);
                double noise = Math.Sin(angle * 3.0 + _phase * 0.7) * 0.035 + Math.Sin(angle * 7.0 - _phase * 0.45) * 0.018;
                double jitter = error ? Math.Sin(_phase * 35 + x * 0.7 + y) * 0.025 : 0;
                double edge = radius + noise + jitter + Math.Sin(_phase * 0.8 + angle * 2) * 0.025;
                double distance = Math.Sqrt(rx * rx + ry * ry);
                double alpha = SmoothStep(edge, edge - 0.12, distance);
                int index = (y * width + x) * 4;
                double highlight = Math.Clamp(1 - Math.Sqrt((rx + 0.28) * (rx + 0.28) + (ry + 0.35) * (ry + 0.35)), 0, 1);
                Color color = Color.FromRgb(
                    (byte)Math.Clamp(shadow.R + (ink.R - shadow.R) * (0.55 + highlight * 0.45), 0, 255),
                    (byte)Math.Clamp(shadow.G + (ink.G - shadow.G) * (0.55 + highlight * 0.45), 0, 255),
                    (byte)Math.Clamp(shadow.B + (ink.B - shadow.B) * (0.55 + highlight * 0.45), 0, 255));
                byte grain = (byte)(Math.Sin(x * 12.7 + y * 8.1 + _phase * 3) * 5 + 5);
                pixels[index] = (byte)Math.Clamp(color.B + grain, 0, 255);
                pixels[index + 1] = (byte)Math.Clamp(color.G + grain, 0, 255);
                pixels[index + 2] = (byte)Math.Clamp(color.R + grain, 0, 255);
                pixels[index + 3] = (byte)Math.Clamp(alpha * (IsPaused ? 90 : 245), 0, 255);
            }
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels.ToArray(), width * 4, 0);
        Halo.Opacity = listening ? 0.18 + mic * 0.35 : processing ? 0.3 : speaking ? 0.22 : error ? 0.45 : 0;
        Halo.Stroke = new SolidColorBrush(error ? Color.FromRgb(0xE0, 0x53, 0x53) : ink);
        MicOffIndicator.Visibility = IsPaused ? Visibility.Visible : Visibility.Collapsed;
        OrbLabel = WidgetState switch
        {
            WidgetState.Listening or WidgetState.SessionListening or WidgetState.Redictating => "Listening",
            WidgetState.Processing or WidgetState.Sending => "Processing",
            WidgetState.ReadingDraft => "Speaking",
            WidgetState.Error => "Error",
            WidgetState.Sent => "Sent",
            _ => IsSessionOpen ? "Session idle" : "Ready"
        };
    }

    private static double SmoothStep(double edge, double feather, double value)
    {
        double t = Math.Clamp((edge - value) / Math.Max(0.001, edge - feather), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
