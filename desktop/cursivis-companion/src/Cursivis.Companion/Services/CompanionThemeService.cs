using Cursivis.Companion.Models;
using System.Windows;
using System.Windows.Media;

namespace Cursivis.Companion.Services;

public static class CompanionThemeService
{
    public static CompanionThemeMode CurrentMode { get; private set; } = CompanionThemeMode.Dark;

    public static event EventHandler<CompanionThemeMode>? ThemeChanged;

    public static void Apply(CompanionThemeMode mode)
    {
        if (Application.Current is null)
        {
            CurrentMode = mode;
            return;
        }

        CurrentMode = mode;
        var palette = mode == CompanionThemeMode.Light ? ThemePalette.Light : ThemePalette.Dark;

        UpdateSolidBrush("Brush.SurfaceDeep", palette.SurfaceDeep);
        UpdateSolidBrush("Brush.SurfaceMid", palette.SurfaceMid);
        UpdateSolidBrush("Brush.SurfaceHigh", palette.SurfaceHigh);
        UpdateSolidBrush("Brush.SurfaceGlass", palette.SurfaceGlass);
        UpdateSolidBrush("Brush.StrokeSoft", palette.StrokeSoft);
        UpdateSolidBrush("Brush.StrokeStrong", palette.StrokeStrong);
        UpdateSolidBrush("Brush.TextMain", palette.TextMain);
        UpdateSolidBrush("Brush.TextMuted", palette.TextMuted);
        UpdateSolidBrush("Brush.TextSoft", palette.TextSoft);
        UpdateSolidBrush("Brush.Cyan", palette.AccentCyan);
        UpdateSolidBrush("Brush.Magenta", palette.AccentMagenta);
        UpdateSolidBrush("Brush.Gold", palette.AccentGold);
        UpdateSolidBrush("Brush.WindowBorder", palette.WindowBorder);
        UpdateSolidBrush("Brush.HeroBadgeBackground", palette.HeroBadgeBackground);
        UpdateSolidBrush("Brush.HeroBadgeBorder", palette.HeroBadgeBorder);
        UpdateSolidBrush("Brush.HeroBadgeText", palette.HeroBadgeText);
        UpdateSolidBrush("Brush.SectionBackground", palette.SectionBackground);
        UpdateSolidBrush("Brush.SectionBorder", palette.SectionBorder);
        UpdateSolidBrush("Brush.SectionAccentBorder", palette.SectionAccentBorder);
        UpdateSolidBrush("Brush.ResultPanelShell", palette.ResultPanelShell);
        UpdateSolidBrush("Brush.ResultPanelStroke", palette.ResultPanelStroke);
        UpdateSolidBrush("Brush.ResultPanelHeader", palette.ResultPanelHeader);
        UpdateSolidBrush("Brush.ResultPanelSurface", palette.ResultPanelSurface);
        UpdateSolidBrush("Brush.ResultPanelSurfaceBorder", palette.ResultPanelSurfaceBorder);
        UpdateSolidBrush("Brush.ResultActionBadgeBackground", palette.ResultActionBadgeBackground);
        UpdateSolidBrush("Brush.ResultActionBadgeBorder", palette.ResultActionBadgeBorder);
        UpdateSolidBrush("Brush.ResultActionBadgeText", palette.ResultActionBadgeText);
        UpdateSolidBrush("Brush.ButtonPrimaryBackground", palette.ButtonPrimaryBackground);
        UpdateSolidBrush("Brush.ButtonPrimaryBorder", palette.ButtonPrimaryBorder);
        UpdateSolidBrush("Brush.ButtonPrimaryText", palette.ButtonPrimaryText);
        UpdateSolidBrush("Brush.ButtonSecondaryBackground", palette.ButtonSecondaryBackground);
        UpdateSolidBrush("Brush.ButtonSecondaryBorder", palette.ButtonSecondaryBorder);
        UpdateSolidBrush("Brush.ButtonSecondaryText", palette.ButtonSecondaryText);
        UpdateSolidBrush("Brush.ButtonAccentBackground", palette.ButtonAccentBackground);
        UpdateSolidBrush("Brush.ButtonAccentBorder", palette.ButtonAccentBorder);
        UpdateSolidBrush("Brush.ButtonAccentText", palette.ButtonAccentText);
        UpdateSolidBrush("Brush.InputBackground", palette.InputBackground);
        UpdateSolidBrush("Brush.InputBorder", palette.InputBorder);
        UpdateSolidBrush("Brush.InputForeground", palette.InputForeground);
        UpdateSolidBrush("Brush.ResultHeadingText", palette.ResultHeadingText);
        UpdateSolidBrush("Brush.ResultMetaText", palette.ResultMetaText);
        UpdateSolidBrush("Brush.ResultBulletText", palette.ResultBulletText);
        UpdateSolidBrush("Brush.ResultCodeForeground", palette.ResultCodeForeground);
        UpdateSolidBrush("Brush.ResultCodeBackground", palette.ResultCodeBackground);
        UpdateSolidBrush("Brush.ResultMathForeground", palette.ResultMathForeground);
        UpdateSolidBrush("Brush.ResultMathBackground", palette.ResultMathBackground);

        UpdateGradientBrush(
            "Brush.WindowGradient",
            palette.WindowGradientStart,
            palette.WindowGradientMid,
            palette.WindowGradientEnd);
        UpdateGradientBrush(
            "Brush.CardGradient",
            palette.CardGradientStart,
            palette.CardGradientEnd);

        ThemeChanged?.Invoke(null, mode);
    }

    private static void UpdateSolidBrush(string resourceKey, Color color)
    {
        if (Application.Current.Resources[resourceKey] is SolidColorBrush brush)
        {
            if (brush.IsFrozen)
            {
                Application.Current.Resources[resourceKey] = new SolidColorBrush(color);
                return;
            }

            brush.Color = color;
            return;
        }

        Application.Current.Resources[resourceKey] = new SolidColorBrush(color);
    }

    private static void UpdateGradientBrush(string resourceKey, params Color[] colors)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };

        for (var index = 0; index < colors.Length; index += 1)
        {
            brush.GradientStops.Add(new GradientStop(
                colors[index],
                colors.Length == 1 ? 0 : (double)index / (colors.Length - 1)));
        }

        Application.Current.Resources[resourceKey] = brush;
    }

    private sealed record ThemePalette(
        Color SurfaceDeep,
        Color SurfaceMid,
        Color SurfaceHigh,
        Color SurfaceGlass,
        Color StrokeSoft,
        Color StrokeStrong,
        Color TextMain,
        Color TextMuted,
        Color TextSoft,
        Color AccentCyan,
        Color AccentMagenta,
        Color AccentGold,
        Color WindowBorder,
        Color WindowGradientStart,
        Color WindowGradientMid,
        Color WindowGradientEnd,
        Color CardGradientStart,
        Color CardGradientEnd,
        Color HeroBadgeBackground,
        Color HeroBadgeBorder,
        Color HeroBadgeText,
        Color SectionBackground,
        Color SectionBorder,
        Color SectionAccentBorder,
        Color ResultPanelShell,
        Color ResultPanelStroke,
        Color ResultPanelHeader,
        Color ResultPanelSurface,
        Color ResultPanelSurfaceBorder,
        Color ResultActionBadgeBackground,
        Color ResultActionBadgeBorder,
        Color ResultActionBadgeText,
        Color ButtonPrimaryBackground,
        Color ButtonPrimaryBorder,
        Color ButtonPrimaryText,
        Color ButtonSecondaryBackground,
        Color ButtonSecondaryBorder,
        Color ButtonSecondaryText,
        Color ButtonAccentBackground,
        Color ButtonAccentBorder,
        Color ButtonAccentText,
        Color InputBackground,
        Color InputBorder,
        Color InputForeground,
        Color ResultHeadingText,
        Color ResultMetaText,
        Color ResultBulletText,
        Color ResultCodeForeground,
        Color ResultCodeBackground,
        Color ResultMathForeground,
        Color ResultMathBackground)
    {
        public static ThemePalette Dark { get; } = new(
            SurfaceDeep: FromHex("#FF0C0E11"),
            SurfaceMid: FromHex("#FF111419"),
            SurfaceHigh: FromHex("#FF171B20"),
            SurfaceGlass: FromHex("#C2171C22"),
            StrokeSoft: FromHex("#22FFFFFF"),
            StrokeStrong: FromHex("#52FFFFFF"),
            TextMain: FromHex("#FFF5F7FA"),
            TextMuted: FromHex("#FFA9B0B8"),
            TextSoft: FromHex("#FF7A828C"),
            AccentCyan: FromHex("#FFF5F7FA"),
            AccentMagenta: FromHex("#FFD9DEE4"),
            AccentGold: FromHex("#FFB9C0C8"),
            WindowBorder: FromHex("#1AFFFFFF"),
            WindowGradientStart: FromHex("#FF07090B"),
            WindowGradientMid: FromHex("#FF0B0E11"),
            WindowGradientEnd: FromHex("#FF101418"),
            CardGradientStart: FromHex("#EE101418"),
            CardGradientEnd: FromHex("#F114181D"),
            HeroBadgeBackground: FromHex("#20181D23"),
            HeroBadgeBorder: FromHex("#26FFFFFF"),
            HeroBadgeText: FromHex("#FFE8ECF1"),
            SectionBackground: FromHex("#D61A1F25"),
            SectionBorder: FromHex("#1EFFFFFF"),
            SectionAccentBorder: FromHex("#28FFFFFF"),
            ResultPanelShell: FromHex("#D021262D"),
            ResultPanelStroke: FromHex("#00FFFFFF"),
            ResultPanelHeader: FromHex("#C8101418"),
            ResultPanelSurface: FromHex("#C8101418"),
            ResultPanelSurfaceBorder: FromHex("#00FFFFFF"),
            ResultActionBadgeBackground: FromHex("#00FFFFFF"),
            ResultActionBadgeBorder: FromHex("#00FFFFFF"),
            ResultActionBadgeText: FromHex("#FFA9B0B8"),
            ButtonPrimaryBackground: FromHex("#1AFFFFFF"),
            ButtonPrimaryBorder: FromHex("#26FFFFFF"),
            ButtonPrimaryText: FromHex("#FFF7FAFF"),
            ButtonSecondaryBackground: FromHex("#0E000000"),
            ButtonSecondaryBorder: FromHex("#18FFFFFF"),
            ButtonSecondaryText: FromHex("#FFA9B0B8"),
            ButtonAccentBackground: FromHex("#FF2C323A"),
            ButtonAccentBorder: FromHex("#3AFFFFFF"),
            ButtonAccentText: FromHex("#FFF7FAFF"),
            InputBackground: FromHex("#B814181D"),
            InputBorder: FromHex("#28FFFFFF"),
            InputForeground: FromHex("#FFF7FAFD"),
            ResultHeadingText: FromHex("#FFF7FAFD"),
            ResultMetaText: FromHex("#FFA9B0B8"),
            ResultBulletText: FromHex("#FFD4DAE1"),
            ResultCodeForeground: FromHex("#FFF2F5FA"),
            ResultCodeBackground: FromHex("#241A1D21"),
            ResultMathForeground: FromHex("#FFE9EDF2"),
            ResultMathBackground: FromHex("#22171B20"));

        public static ThemePalette Light { get; } = new(
            SurfaceDeep: FromHex("#FFF3F4F5"),
            SurfaceMid: FromHex("#FFF7F8F9"),
            SurfaceHigh: FromHex("#FFFFFFFF"),
            SurfaceGlass: FromHex("#CCFFFFFF"),
            StrokeSoft: FromHex("#12000000"),
            StrokeStrong: FromHex("#2C000000"),
            TextMain: FromHex("#FF111418"),
            TextMuted: FromHex("#FF67707A"),
            TextSoft: FromHex("#FF8C949C"),
            AccentCyan: FromHex("#FF111418"),
            AccentMagenta: FromHex("#FF2D3238"),
            AccentGold: FromHex("#FF626973"),
            WindowBorder: FromHex("#12000000"),
            WindowGradientStart: FromHex("#FFF8F9FA"),
            WindowGradientMid: FromHex("#FFF4F5F6"),
            WindowGradientEnd: FromHex("#FFF0F2F3"),
            CardGradientStart: FromHex("#F4FFFFFF"),
            CardGradientEnd: FromHex("#EEF3F4F5"),
            HeroBadgeBackground: FromHex("#EAFFFFFF"),
            HeroBadgeBorder: FromHex("#12000000"),
            HeroBadgeText: FromHex("#FF444B54"),
            SectionBackground: FromHex("#D3FFFFFF"),
            SectionBorder: FromHex("#12000000"),
            SectionAccentBorder: FromHex("#18000000"),
            ResultPanelShell: FromHex("#D0FFFFFF"),
            ResultPanelStroke: FromHex("#10000000"),
            ResultPanelHeader: FromHex("#B8FFFFFF"),
            ResultPanelSurface: FromHex("#B2FFFFFF"),
            ResultPanelSurfaceBorder: FromHex("#12000000"),
            ResultActionBadgeBackground: FromHex("#00FFFFFF"),
            ResultActionBadgeBorder: FromHex("#00FFFFFF"),
            ResultActionBadgeText: FromHex("#FF67707A"),
            ButtonPrimaryBackground: FromHex("#EDFFFFFF"),
            ButtonPrimaryBorder: FromHex("#18000000"),
            ButtonPrimaryText: FromHex("#FF12161A"),
            ButtonSecondaryBackground: FromHex("#E7FFFFFF"),
            ButtonSecondaryBorder: FromHex("#14000000"),
            ButtonSecondaryText: FromHex("#FF6A727A"),
            ButtonAccentBackground: FromHex("#FF14141A"),
            ButtonAccentBorder: FromHex("#FF14141A"),
            ButtonAccentText: FromHex("#FFFFFFFF"),
            InputBackground: FromHex("#F6FFFFFF"),
            InputBorder: FromHex("#1E000000"),
            InputForeground: FromHex("#FF111418"),
            ResultHeadingText: FromHex("#FF0F1419"),
            ResultMetaText: FromHex("#FF67707A"),
            ResultBulletText: FromHex("#FF5D6670"),
            ResultCodeForeground: FromHex("#FF1B232D"),
            ResultCodeBackground: FromHex("#FFF4F5F6"),
            ResultMathForeground: FromHex("#FF2B3138"),
            ResultMathBackground: FromHex("#FFF3F4F5"));
    }

    private static Color FromHex(string value)
    {
        return (Color)ColorConverter.ConvertFromString(value);
    }
}
