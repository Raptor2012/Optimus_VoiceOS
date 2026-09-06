namespace Optimus.Shell.Theme;

using System;
using System.Windows;
using System.Windows.Media;

/// <summary>
/// Shared design tokens for the WPF companion window.
/// Defines the refined dark palette, 4px-base spacing scale, corner radii,
/// motion durations, and reduced-motion accessibility support.
/// </summary>
public static class DesignTokens
{
    // ==========================================
    // COLOR PALETTE (Refined Dark Scheme)
    // ==========================================
    public const string BackgroundHex = "#101114";
    public const string SurfaceHex = "#181A1F";
    public const string SurfaceRaisedHex = "#20232A";
    public const string TextPrimaryHex = "#F1F2F4";
    public const string TextSecondaryHex = "#A3A9B5";
    public const string AccentHex = "#8C9EFF";        // Periwinkle accent
    public const string AccentMutedHex = "#7080D9";   // Muted periwinkle
    public const string OnAccentHex = "#101114";      // Dark text on accent
    public const string SuccessHex = "#34C759";       // Muted slightly from #30D158
    public const string ErrorHex = "#E05353";         // Muted slightly from #FF5555
    public const string BorderHex = "#282C34";        // Thin border
    public const string BorderSubtleHex = "#26FFFFFF"; // Subtle opacity border (~15%)
    public const string SurfaceSuccessHex = "#162419"; // Subdued success surface
    public const string SurfaceErrorHex = "#2D1818";   // Subdued error surface
    public const string SurfaceWarningHex = "#2A2415"; // Subdued warning surface
    public const string WarningHex = "#F59E0B";        // Amber warning

    // Frozen WPF Colors
    public static readonly Color ColorBackground = ParseColor(BackgroundHex);
    public static readonly Color ColorSurface = ParseColor(SurfaceHex);
    public static readonly Color ColorSurfaceRaised = ParseColor(SurfaceRaisedHex);
    public static readonly Color ColorTextPrimary = ParseColor(TextPrimaryHex);
    public static readonly Color ColorTextSecondary = ParseColor(TextSecondaryHex);
    public static readonly Color ColorAccent = ParseColor(AccentHex);
    public static readonly Color ColorAccentMuted = ParseColor(AccentMutedHex);
    public static readonly Color ColorOnAccent = ParseColor(OnAccentHex);
    public static readonly Color ColorSuccess = ParseColor(SuccessHex);
    public static readonly Color ColorError = ParseColor(ErrorHex);
    public static readonly Color ColorBorder = ParseColor(BorderHex);
    public static readonly Color ColorBorderSubtle = ParseColor(BorderSubtleHex);
    public static readonly Color ColorSurfaceSuccess = ParseColor(SurfaceSuccessHex);
    public static readonly Color ColorSurfaceError = ParseColor(SurfaceErrorHex);
    public static readonly Color ColorSurfaceWarning = ParseColor(SurfaceWarningHex);
    public static readonly Color ColorWarning = ParseColor(WarningHex);
    public static readonly Color ColorDropShadow = Color.FromArgb(0x40, 0x00, 0x00, 0x00);

    // ==========================================
    // SPACING SCALE (4px base: 8, 12, 16, 24, 32px)
    // ==========================================
    public const double SpacingBase = 4.0;
    public const double SpacingXs = 4.0;
    public const double SpacingSm = 8.0;
    public const double SpacingMd = 12.0;
    public const double SpacingLg = 16.0;
    public const double SpacingXl = 24.0;
    public const double SpacingXxl = 32.0;

    public static readonly Thickness ThicknessXs = new(SpacingXs);
    public static readonly Thickness ThicknessSm = new(SpacingSm);
    public static readonly Thickness ThicknessMd = new(SpacingMd);
    public static readonly Thickness ThicknessLg = new(SpacingLg);
    public static readonly Thickness ThicknessXl = new(SpacingXl);
    public static readonly Thickness ThicknessXxl = new(SpacingXxl);

    // ==========================================
    // CORNERS (12px controls, 16px cards, fully rounded companion)
    // ==========================================
    public const double CornerRadiusControlValue = 12.0;
    public const double CornerRadiusCardValue = 16.0;
    public const double CornerRadiusCompanionValue = 24.0;

    public static readonly CornerRadius CornerRadiusControl = new(CornerRadiusControlValue);
    public static readonly CornerRadius CornerRadiusCard = new(CornerRadiusCardValue);
    public static readonly CornerRadius CornerRadiusCompanion = new(CornerRadiusCompanionValue);

    // ==========================================
    // MOTION (160-240ms controls, 240-320ms panel transitions)
    // ==========================================
    public const int MotionControlDurationMs = 200;
    public const int MotionPanelDurationMs = 280;

    /// <summary>
    /// Checks Windows accessibility animation settings via SystemParameters.MenuAnimation.
    /// When animations are turned off in Windows, this returns true.
    /// </summary>
    public static bool IsReducedMotion => !SystemParameters.MenuAnimation;

    /// <summary>
    /// Animation duration for interactive controls (160-240ms range), or 0 if reduced motion is enabled.
    /// </summary>
    public static TimeSpan ControlMotionDuration =>
        IsReducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(MotionControlDurationMs);

    /// <summary>
    /// Animation duration for panel transitions (240-320ms range), or 0 if reduced motion is enabled.
    /// </summary>
    public static TimeSpan PanelMotionDuration =>
        IsReducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(MotionPanelDurationMs);

    public static Duration ControlDuration => new(ControlMotionDuration);
    public static Duration PanelDuration => new(PanelMotionDuration);

    // ==========================================
    // RESOURCE KEYS FOR WPF XAML
    // ==========================================
    public static class ResourceKeys
    {
        public const string BackgroundBrush = "BackgroundBrush";
        public const string SurfaceBrush = "SurfaceBrush";
        public const string SurfaceRaisedBrush = "SurfaceRaisedBrush";
        public const string TextPrimaryBrush = "TextPrimaryBrush";
        public const string TextSecondaryBrush = "TextSecondaryBrush";
        public const string AccentBrush = "AccentBrush";
        public const string AccentMutedBrush = "AccentMutedBrush";
        public const string OnAccentBrush = "OnAccentBrush";
        public const string SuccessBrush = "SuccessBrush";
        public const string ErrorBrush = "ErrorBrush";
        public const string BorderBrush = "BorderBrush";
        public const string BorderSubtleBrush = "BorderSubtleBrush";
        public const string SurfaceSuccessBrush = "SurfaceSuccessBrush";
        public const string SurfaceErrorBrush = "SurfaceErrorBrush";
        public const string SurfaceWarningBrush = "SurfaceWarningBrush";
        public const string WarningBrush = "WarningBrush";

        public const string DropShadowColor = "DropShadowColor";

        public const string ControlCornerRadius = "ControlCornerRadius";
        public const string CardCornerRadius = "CardCornerRadius";
        public const string CompanionCornerRadius = "CompanionCornerRadius";

        public const string SpacingXs = "SpacingXs";
        public const string SpacingSm = "SpacingSm";
        public const string SpacingMd = "SpacingMd";
        public const string SpacingLg = "SpacingLg";
        public const string SpacingXl = "SpacingXl";
        public const string SpacingXxl = "SpacingXxl";

        public const string ControlAnimationDuration = "ControlAnimationDuration";
        public const string PanelAnimationDuration = "PanelAnimationDuration";
    }

    /// <summary>
    /// Populates a ResourceDictionary with frozen brushes, metrics, and motion settings.
    /// </summary>
    public static void PopulateResourceDictionary(ResourceDictionary dict)
    {
        ArgumentNullException.ThrowIfNull(dict);

        dict[ResourceKeys.BackgroundBrush] = CreateFrozenBrush(BackgroundHex);
        dict[ResourceKeys.SurfaceBrush] = CreateFrozenBrush(SurfaceHex);
        dict[ResourceKeys.SurfaceRaisedBrush] = CreateFrozenBrush(SurfaceRaisedHex);
        dict[ResourceKeys.TextPrimaryBrush] = CreateFrozenBrush(TextPrimaryHex);
        dict[ResourceKeys.TextSecondaryBrush] = CreateFrozenBrush(TextSecondaryHex);
        dict[ResourceKeys.AccentBrush] = CreateFrozenBrush(AccentHex);
        dict[ResourceKeys.AccentMutedBrush] = CreateFrozenBrush(AccentMutedHex);
        dict[ResourceKeys.OnAccentBrush] = CreateFrozenBrush(OnAccentHex);
        dict[ResourceKeys.SuccessBrush] = CreateFrozenBrush(SuccessHex);
        dict[ResourceKeys.ErrorBrush] = CreateFrozenBrush(ErrorHex);
        dict[ResourceKeys.BorderBrush] = CreateFrozenBrush(BorderHex);
        dict[ResourceKeys.BorderSubtleBrush] = CreateFrozenBrush(BorderSubtleHex);
        dict[ResourceKeys.SurfaceSuccessBrush] = CreateFrozenBrush(SurfaceSuccessHex);
        dict[ResourceKeys.SurfaceErrorBrush] = CreateFrozenBrush(SurfaceErrorHex);
        dict[ResourceKeys.SurfaceWarningBrush] = CreateFrozenBrush(SurfaceWarningHex);
        dict[ResourceKeys.WarningBrush] = CreateFrozenBrush(WarningHex);

        dict[ResourceKeys.DropShadowColor] = ColorDropShadow;

        dict[ResourceKeys.ControlCornerRadius] = CornerRadiusControl;
        dict[ResourceKeys.CardCornerRadius] = CornerRadiusCard;
        dict[ResourceKeys.CompanionCornerRadius] = CornerRadiusCompanion;

        dict[ResourceKeys.SpacingXs] = SpacingXs;
        dict[ResourceKeys.SpacingSm] = SpacingSm;
        dict[ResourceKeys.SpacingMd] = SpacingMd;
        dict[ResourceKeys.SpacingLg] = SpacingLg;
        dict[ResourceKeys.SpacingXl] = SpacingXl;
        dict[ResourceKeys.SpacingXxl] = SpacingXxl;

        dict[ResourceKeys.ControlAnimationDuration] = ControlDuration;
        dict[ResourceKeys.PanelAnimationDuration] = PanelDuration;
    }

    public static ResourceDictionary CreateResourceDictionary()
    {
        var dict = new ResourceDictionary();
        PopulateResourceDictionary(dict);
        return dict;
    }

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var brush = new SolidColorBrush(ParseColor(hex));
        brush.Freeze();
        return brush;
    }
}
