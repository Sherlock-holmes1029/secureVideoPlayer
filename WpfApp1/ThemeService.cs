using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace WpfApp1
{
    public static class ThemeService
    {
        private static Dictionary<string, string> _cssVars = new();

        // ── Color mappings ──────────────────────────────────────────────────────
        private static readonly Dictionary<string, string> ColorMap = new()
        {
            ["--background"]                  = "PlayerBackgroundBrush",
            ["--bg-panel"]                    = "PlayerSurfaceBrush",
            ["--bg-elevated"]                 = "PlayerElevatedBrush",
            ["--bg-hover"]                    = "PlayerHoverBrush",
            ["--border"]                      = "PlayerBorderBrush",
            ["--foreground"]                  = "PlayerForegroundBrush",
            ["--text-primary"]                = "PlayerTextPrimaryBrush",
            ["--text-secondary"]              = "PlayerTextSecondaryBrush",
            ["--text-muted"]                  = "PlayerMutedBrush",
            ["--color-primary"]               = "PlayerPrimaryBrush",
            ["--color-primary-hover"]         = "PlayerPrimaryHoverBrush",
            ["--color-secondary"]             = "PlayerSecondaryBrush",
            ["--color-accent"]                = "PlayerAccentBrush",
            ["--color-danger"]                = "PlayerDangerBrush",
            ["--ring"]                        = "PlayerFocusRingBrush",
            ["--component-button-bg"]         = "PlayerButtonBgBrush",
            ["--component-button-text"]       = "PlayerButtonFgBrush",
            ["--component-button-border"]     = "PlayerButtonBorderBrush",
            ["--component-button-hover-bg"]   = "PlayerButtonHoverBrush",
            ["--component-card-bg"]           = "PlayerCardBgBrush",
            ["--component-card-text"]         = "PlayerCardFgBrush",
            ["--component-card-border"]       = "PlayerCardBorderBrush",
        };

        // ── Step 1: called before MainWindow is created (colors only) ───────────
        public static void Apply(Dictionary<string, string> cssVars, string mode = "light")
        {
            _cssVars = cssVars;
            var res = Application.Current.Resources;

            // Colors
            foreach (var (cssKey, wpfKey) in ColorMap)
                if (cssVars.TryGetValue(cssKey, out var hex) && hex.StartsWith('#'))
                    res[wpfKey] = new SolidColorBrush(HexToColor(hex));

            // Control bar: semi-transparent card bg
            if (cssVars.TryGetValue("--component-card-bg", out var cardHex) && cardHex.StartsWith('#'))
            {
                var c = HexToColor(cardHex);
                res["PlayerControlBarBrush"] = new SolidColorBrush(Color.FromArgb(220, c.R, c.G, c.B));
            }
        }

        // ── Step 2: called after MainWindow is loaded (radius, font, weight) ────
        public static void ApplyToWindow(MainWindow window)
        {
            var cssVars = _cssVars;

            // Font family — strip ", sans-serif" fallback
            var fontName = "Inter";
            if (cssVars.TryGetValue("--font-family", out var fontVal))
                fontName = fontVal.Split(',')[0].Trim();
            var font = new FontFamily(fontName);

            // Font size
            var fontSize = 13.0;
            if (cssVars.TryGetValue("--font-size-sm", out var sizeVal))
                fontSize = ParsePx(sizeVal);

            // Font weight
            var fontWeight = FontWeights.Medium;
            if (cssVars.TryGetValue("--component-button-font-weight", out var weightVal)
                && int.TryParse(weightVal, out var w))
                fontWeight = FontWeight.FromOpenTypeWeight(w);

            // Corner radii
            var buttonRadius = new CornerRadius(9.6);
            if (cssVars.TryGetValue("--component-button-radius", out var btnRem))
                buttonRadius = new CornerRadius(ParseRem(btnRem));

            var cardRadius = new CornerRadius(12);
            if (cssVars.TryGetValue("--component-card-radius", out var cardRem))
                cardRadius = new CornerRadius(ParseRem(cardRem));

            // Apply font to window (cascades to all children)
            window.FontFamily = font;
            window.FontSize = fontSize;

            // Wait for visual tree to be ready, then patch borders inside templates
            window.Loaded += (_, _) =>
            {
                // Patch all buttons
                foreach (var btn in FindChildren<Button>(window))
                {
                    btn.FontFamily = font;
                    btn.FontSize = fontSize;
                    btn.FontWeight = fontWeight;
                    btn.ApplyTemplate();
                    if (btn.Template.FindName("border", btn) is Border b)
                        b.CornerRadius = buttonRadius;
                }

                // Patch control bar border
                if (window.FindName("ControlBar") is Border controlBar)
                    controlBar.CornerRadius = cardRadius;
            };
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var c in FindChildren<T>(child)) yield return c;
            }
        }

        private static Color HexToColor(string hex)
        {
            hex = hex.TrimStart('#');
            return Color.FromRgb(
                Convert.ToByte(hex[0..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        }

        private static double ParsePx(string val)
        {
            val = val.Trim().ToLower().Replace("px", "");
            return double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        private static double ParseRem(string val)
        {
            val = val.Trim().ToLower().Replace("rem", "");
            return double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d * 16.0 : 0;
        }
    }
}
