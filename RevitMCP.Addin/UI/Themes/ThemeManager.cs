using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace RevitMCP.Addin.UI.Themes;

/// <summary>
/// Applies the appearance to a window, the same way Sentinel does: Dark/Light palette, plus the Windows
/// accessibility preferences
///  - High contrast       → palette built from the system high-contrast colours,
///  - Transparency off    → solid materials (no gradients/shadows),
///  - Animations off      → <see cref="ReducedMotion"/> (Motion uses opacity only).
/// Only the palette dictionaries are swapped; the shared styles stay merged and use DynamicResource.
/// </summary>
public class ThemeManager
{
    /// <summary>Overrides for tests (null = follow Windows).</summary>
    public static bool? ForceHighContrast { get; set; }
    public static bool? ForceSolidMaterials { get; set; }
    public static bool? ForceReducedMotion { get; set; }

    private readonly Window _window;
    private ResourceDictionary[] _applied = [];

    public bool IsDarkMode { get; private set; }

    public ThemeManager(Window window, bool isDarkMode)
    {
        _window = window;
        IsDarkMode = isDarkMode;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        window.Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }

    public static bool HighContrast => ForceHighContrast ?? SystemParameters.HighContrast;

    /// <summary>Windows "Transparency effects" (Settings → Personalization → Colors) switched off.</summary>
    public static bool SolidMaterials
    {
        get
        {
            if (ForceSolidMaterials.HasValue) return ForceSolidMaterials.Value;
            if (SystemParameters.HighContrast) return true;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = key?.GetValue("EnableTransparency");
                    return v is int i && i == 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Windows "Animation effects" switched off.</summary>
    public static bool ReducedMotion => ForceReducedMotion ?? !SystemParameters.ClientAreaAnimation;

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast) || e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            _window.Dispatcher.BeginInvoke(new Action(Apply));
    }

    public void SetDarkMode(bool isDarkMode)
    {
        IsDarkMode = isDarkMode;
        Apply();
    }

    public void Apply()
    {
        try
        {
            var target = _window.Resources;
            foreach (var d in _applied) target.MergedDictionaries.Remove(d);
            // Palettes merged from XAML at parse time (design-time default) are replaced as well.
            foreach (var d in target.MergedDictionaries.Where(IsPaletteSource).ToList()) target.MergedDictionaries.Remove(d);

            _applied = BuildDictionaries();
            for (int i = 0; i < _applied.Length; i++) target.MergedDictionaries.Insert(i, _applied[i]);
        }
        catch
        {
            // A theme that fails to apply leaves the previous palette in place.
        }
        Applied?.Invoke();
    }

    /// <summary>
    /// After every palette change (user toggle, high contrast, animation setting). Values a converter looked up
    /// from the palette do not follow DynamicResource, so their bindings are refreshed on this.
    /// </summary>
    public event Action? Applied;

    private static bool IsPaletteSource(ResourceDictionary d)
    {
        var s = d.Source?.OriginalString;
        return s != null && s.Contains("/UI/Themes/") && (s.EndsWith("DarkTheme.xaml") || s.EndsWith("LightTheme.xaml"));
    }

    private ResourceDictionary[] BuildDictionaries()
    {
        if (HighContrast) return new[] { BuildHighContrast() };

        var palette = new ResourceDictionary
        {
            Source = new Uri(IsDarkMode
                ? "pack://application:,,,/RevitMCP.Addin;component/UI/Themes/DarkTheme.xaml"
                : "pack://application:,,,/RevitMCP.Addin;component/UI/Themes/LightTheme.xaml", UriKind.Absolute)
        };
        var materials = SolidMaterials
            ? BuildSolidMaterials(palette)
            : new ResourceDictionary { ["Shadow.Opacity"] = IsDarkMode ? 0.45 : 0.16 };
        return new[] { palette, materials };
    }

    /// <summary>Reduced transparency: materials become solid surfaces with a defined edge, no shadows.</summary>
    private static ResourceDictionary BuildSolidMaterials(ResourceDictionary palette)
    {
        var raised = palette["Surface.Raised"] as Brush;
        var stroke = palette["Surface.Stroke"] as Brush;
        var content = palette["Surface.Content"] as Brush;
        return new ResourceDictionary
        {
            ["Toolbar.Material"] = palette["Window.Background"],
            ["Floating.Material"] = raised ?? content,
            ["Floating.Edge"] = stroke,
            ["Shadow.Opacity"] = 0.0
        };
    }

    /// <summary>High contrast: every brush maps to a Windows system colour so the user's HC scheme is honoured.</summary>
    private static ResourceDictionary BuildHighContrast()
    {
        Func<Color, SolidColorBrush> b = c => { var br = new SolidColorBrush(c); br.Freeze(); return br; };
        var window = b(SystemColors.WindowColor);
        var text = b(SystemColors.WindowTextColor);
        var highlight = b(SystemColors.HighlightColor);
        var hot = b(SystemColors.HotTrackColor);
        var btn = b(SystemColors.ControlColor);
        var d = new ResourceDictionary();
        foreach (var k in new[] { "Window.Background", "Sidebar.Material", "Toolbar.Material", "Floating.Material", "Surface.Content",
                                  "Surface.Raised", "Surface.Inset", "Input.Fill", "Control.Fill", "Row.Alternate",
                                  "Status.Ok.Subtle", "Status.Info.Subtle", "Status.Warning.Subtle", "Status.Error.Subtle",
                                  "Status.Neutral.Subtle", "Accent.Subtle" })
            d[k] = window;
        foreach (var k in new[] { "Window.Stroke", "Sidebar.Edge", "Floating.Edge", "Surface.Stroke", "Separator", "Control.Stroke",
                                  "Input.Stroke", "Input.StrokeHover", "Text.Primary", "Text.Secondary", "Text.Tertiary",
                                  "Status.Ok", "Status.Info", "Status.Warning", "Status.Error", "Status.Neutral" })
            d[k] = text;
        d["Text.Disabled"] = b(SystemColors.GrayTextColor);
        foreach (var k in new[] { "Accent", "Accent.Hover", "Accent.Pressed", "Row.Selected", "Row.SelectedInactive", "Control.FillPressed" })
            d[k] = highlight;
        d["Text.OnAccent"] = b(SystemColors.HighlightTextColor);
        d["Accent.Text"] = hot;
        d["Focus.Ring"] = hot;
        d["Control.FillHover"] = btn;
        d["Control.Plain.Hover"] = btn;
        d["Control.Plain.Pressed"] = highlight;
        d["Row.Hover"] = btn;
        d["Scrim"] = b(Color.FromArgb(0x80, 0, 0, 0));
        d["Shadow.Color"] = Colors.Black;
        d["Shadow.Opacity"] = 0.0;
        return d;
    }
}
