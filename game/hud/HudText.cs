using System.Collections.Generic;
using System.Text;
using Godot;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// Xonotic colored-text parsing for the HUD — the modernized successor to QuakeC's <c>drawcolorcodedstring</c>
/// / <c>COLOR_TAG_*</c> handling (Base/.../qcsrc/lib/string.qh + the engine's color-code lexer). Player
/// names, centerprints and kill-feed lines carry inline <c>^N</c> color codes (a single digit 0-9) and the
/// long form <c>^xRGB</c> (three hex nibbles), which select the color of the text that follows. This helper
/// turns such a string into a list of (text, color) runs the panels draw left-to-right, and also strips the
/// codes for width measurement.
///
/// Supported (matching the engine):
///   <c>^0</c>..<c>^9</c>  — the ten palette colors (0 black … 7 white, etc.).
///   <c>^xRGB</c>          — 12-bit hex color, e.g. <c>^xf80</c> = orange.
///   <c>^^</c>             — a literal caret.
/// A trailing/!invalid <c>^</c> is treated as a literal.
/// </summary>
public static class HudText
{
    /// <summary>One colored run of a parsed string (QC: a segment between color tags).</summary>
    public readonly struct Run
    {
        public readonly string Text;
        public readonly Color Color;
        public Run(string text, Color color) { Text = text; Color = color; }
    }

    // The Quake/Xonotic ^0..^9 palette (engine "qfont" color table). Alpha applied by the caller.
    private static readonly Color[] Palette =
    {
        new(0.20f, 0.20f, 0.20f), // ^0 black-ish
        new(1.00f, 0.20f, 0.20f), // ^1 red
        new(0.20f, 1.00f, 0.20f), // ^2 green
        new(1.00f, 1.00f, 0.20f), // ^3 yellow
        new(0.20f, 0.40f, 1.00f), // ^4 blue
        new(0.20f, 1.00f, 1.00f), // ^5 cyan
        new(1.00f, 0.20f, 1.00f), // ^6 magenta
        new(1.00f, 1.00f, 1.00f), // ^7 white
        new(0.60f, 0.60f, 0.60f), // ^8 grey
        new(0.50f, 0.50f, 0.50f), // ^9 dark grey
    };

    /// <summary>
    /// Split <paramref name="text"/> into colored runs. <paramref name="baseColor"/> tints the leading
    /// (uncolored) run and supplies the alpha for every run (the codes only set RGB, like the engine).
    /// </summary>
    public static List<Run> Parse(string? text, Color baseColor)
    {
        var runs = new List<Run>();
        if (string.IsNullOrEmpty(text)) return runs;

        Color cur = baseColor;
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length > 0) { runs.Add(new Run(sb.ToString(), cur)); sb.Clear(); }
        }

        for (int i = 0; i < text!.Length; i++)
        {
            char ch = text[i];
            if (ch == '^' && i + 1 < text.Length)
            {
                char n = text[i + 1];
                if (n == '^') { sb.Append('^'); i++; continue; }          // ^^ -> literal caret
                if (n >= '0' && n <= '9')                                  // ^N palette
                {
                    Flush();
                    cur = Palette[n - '0'];
                    cur.A = baseColor.A;
                    i++;
                    continue;
                }
                if ((n == 'x' || n == 'X') && i + 4 < text.Length          // ^xRGB hex
                    && IsHex(text[i + 2]) && IsHex(text[i + 3]) && IsHex(text[i + 4]))
                {
                    Flush();
                    float r = HexNibble(text[i + 2]) / 15f;
                    float g = HexNibble(text[i + 3]) / 15f;
                    float b = HexNibble(text[i + 4]) / 15f;
                    cur = new Color(r, g, b, baseColor.A);
                    i += 4;
                    continue;
                }
                // not a valid code: fall through and emit the caret literally
            }
            sb.Append(ch);
        }
        Flush();
        return runs;
    }

    /// <summary>Remove all color codes, returning the plain text (QC <c>strdecolorize</c>).</summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text!.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '^' && i + 1 < text.Length)
            {
                char n = text[i + 1];
                if (n == '^') { sb.Append('^'); i++; continue; }
                if (n >= '0' && n <= '9') { i++; continue; }
                if ((n == 'x' || n == 'X') && i + 4 < text.Length
                    && IsHex(text[i + 2]) && IsHex(text[i + 3]) && IsHex(text[i + 4]))
                { i += 4; continue; }
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int HexNibble(char c) =>
        c <= '9' ? c - '0' : (char.ToLowerInvariant(c) - 'a' + 10);
}
