namespace Optimus.Shell.Controls;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Optimus.Shell.Models;

/// <summary>
/// Animated voice orb that renders at 60 fps during active transitions and drops to low
/// frequency (~4 fps) during idle breathing. Driven entirely by <see cref="OrbStateReducer"/>.
/// </summary>
public partial class VoiceOrb : UserControl
{
    private double _breathPhase;
    private DateTime _lastRender = DateTime.UtcNow;
    private OrbStateReducer.OrbFrame _currentFrame;
    private OrbStateReducer.OrbFrame _targetFrame;
    private bool _isRendering;

    // Smoothing factor for interpolation (higher = snappier)
    private const double SmoothFactor = 8.0;

    // Low frequency rendering interval for idle states (250ms ≈ 4fps)
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMilliseconds(250);
    private DateTime _lastIdleRender = DateTime.MinValue;

    public VoiceOrb()
    {
        InitializeComponent();
        _currentFrame = OrbStateReducer.Reduce(
            WidgetState.Idle, 0, 0, false, false, false, false, 0);
        _targetFrame = _currentFrame;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    #region Dependency properties

    public static readonly DependencyProperty WidgetStateProperty =
        DependencyProperty.Register(nameof(WidgetState), typeof(WidgetState), typeof(VoiceOrb),
            new PropertyMetadata(WidgetState.Idle, OnStateInputChanged));

    public static readonly DependencyProperty MicEnergyProperty =
        DependencyProperty.Register(nameof(MicEnergy), typeof(double), typeof(VoiceOrb),
            new PropertyMetadata(0.0));

    public static readonly DependencyProperty PlaybackEnergyProperty =
        DependencyProperty.Register(nameof(PlaybackEnergy), typeof(double), typeof(VoiceOrb),
            new PropertyMetadata(0.0));

    public static readonly DependencyProperty IsSpeakingReviewProperty =
        DependencyProperty.Register(nameof(IsSpeakingReview), typeof(bool), typeof(VoiceOrb),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsSessionOpenProperty =
        DependencyProperty.Register(nameof(IsSessionOpen), typeof(bool), typeof(VoiceOrb),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsPausedProperty =
        DependencyProperty.Register(nameof(IsPaused), typeof(bool), typeof(VoiceOrb),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsMutedProperty =
        DependencyProperty.Register(nameof(IsMuted), typeof(bool), typeof(VoiceOrb),
            new PropertyMetadata(false));

    public WidgetState WidgetState
    {
        get => (WidgetState)GetValue(WidgetStateProperty);
        set => SetValue(WidgetStateProperty, value);
    }

    public double MicEnergy
    {
        get => (double)GetValue(MicEnergyProperty);
        set => SetValue(MicEnergyProperty, value);
    }

    public double PlaybackEnergy
    {
        get => (double)GetValue(PlaybackEnergyProperty);
        set => SetValue(PlaybackEnergyProperty, value);
    }

    public bool IsSpeakingReview
    {
        get => (bool)GetValue(IsSpeakingReviewProperty);
        set => SetValue(IsSpeakingReviewProperty, value);
    }

    public bool IsSessionOpen
    {
        get => (bool)GetValue(IsSessionOpenProperty);
        set => SetValue(IsSessionOpenProperty, value);
    }

    public bool IsPaused
    {
        get => (bool)GetValue(IsPausedProperty);
        set => SetValue(IsPausedProperty, value);
    }

    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    /// <summary>The orb label text, read by the parent for display.</summary>
    public static readonly DependencyProperty OrbLabelProperty =
        DependencyProperty.Register(nameof(OrbLabel), typeof(string), typeof(VoiceOrb),
            new PropertyMetadata("Ready"));

    public string OrbLabel
    {
        get => (string)GetValue(OrbLabelProperty);
        set => SetValue(OrbLabelProperty, value);
    }

    #endregion

    private static void OnStateInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VoiceOrb orb) orb.RecalculateTarget();
    }

    private void RecalculateTarget()
    {
        _targetFrame = OrbStateReducer.Reduce(
            WidgetState, MicEnergy, PlaybackEnergy,
            IsSpeakingReview, IsSessionOpen, IsPaused, IsMuted, _breathPhase);
        OrbLabel = _targetFrame.Label;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isRendering)
        {
            CompositionTarget.Rendering += OnRender;
            _isRendering = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isRendering)
        {
            CompositionTarget.Rendering -= OnRender;
            _isRendering = false;
        }
    }

    private bool IsActiveState => WidgetState is WidgetState.Listening or WidgetState.SessionListening
        or WidgetState.Processing or WidgetState.ReadingDraft or WidgetState.Redictating
        or WidgetState.Sending;

    private void OnRender(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = (now - _lastRender).TotalSeconds;
        _lastRender = now;

        // Throttle idle rendering
        if (!IsActiveState && now - _lastIdleRender < IdleInterval)
            return;
        _lastIdleRender = now;

        // Advance breathing phase (~0.8 Hz breathing rate)
        _breathPhase += dt * 0.8 * 2.0 * Math.PI;
        if (_breathPhase > 2.0 * Math.PI)
            _breathPhase -= 2.0 * Math.PI;

        // Recalculate target with current energy and breath
        _targetFrame = OrbStateReducer.Reduce(
            WidgetState, MicEnergy, PlaybackEnergy,
            IsSpeakingReview, IsSessionOpen, IsPaused, IsMuted, _breathPhase);

        // Smooth interpolation
        double t = Math.Min(1.0, SmoothFactor * dt);
        double scale = Lerp(_currentFrame.RadiusScale, _targetFrame.RadiusScale, t);
        double coreOpacity = Lerp(_currentFrame.CoreOpacity, _targetFrame.CoreOpacity, t);
        double haloOpacity = Lerp(_currentFrame.HaloOpacity, _targetFrame.HaloOpacity, t);
        double rotation = _currentFrame.RotationSpeed;
        if (Math.Abs(_targetFrame.RotationSpeed - rotation) > 0.1)
            rotation = Lerp(rotation, _targetFrame.RotationSpeed, t);

        _currentFrame = _targetFrame with
        {
            RadiusScale = scale,
            CoreOpacity = coreOpacity,
            HaloOpacity = haloOpacity,
            RotationSpeed = rotation
        };

        // Apply to visuals
        OrbScale.ScaleX = scale;
        OrbScale.ScaleY = scale;
        OrbCore.Opacity = coreOpacity;
        HaloRing.Opacity = haloOpacity;

        // Rotation
        if (Math.Abs(rotation) > 0.1)
            OrbRotation.Angle += rotation * dt;

        // Update colors (snap, no interpolation needed)
        GradCenter.Color = ParseColor(_targetFrame.CenterColor);
        GradEdge.Color = ParseColor(_targetFrame.EdgeColor);

        // Update label
        OrbLabel = _targetFrame.Label;

        // Mic-off indicator
        MicOffIndicator.Visibility = IsPaused ? Visibility.Visible : Visibility.Collapsed;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static Color ParseColor(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            return c;
        }
        catch
        {
            return Colors.Gray;
        }
    }
}
