namespace Glacier.Graphics.Text;

using System;
using System.IO;

/// <summary>
/// Font specification including typeface, point size, and typographical styling.
/// </summary>
public sealed class Font
{
    // Lazy system-font fallback shared across all Font instances that lack an explicit typeface.
    private static TrueTypeFont? s_systemFallback;
    private static readonly object s_lock = new();

    /// <summary>Returns a shared system-font fallback, loading it on first access.</summary>
    private static TrueTypeFont? GetSystemFallback()
    {
        if (s_systemFallback != null) return s_systemFallback;
        lock (s_lock)
        {
            if (s_systemFallback != null) return s_systemFallback;

            // Candidate system font paths (Windows, Linux, macOS)
            string[] candidates =
            [
                // Windows
                @"C:\Windows\Fonts\arial.ttf",
                @"C:\Windows\Fonts\segoeui.ttf",
                @"C:\Windows\Fonts\tahoma.ttf",
                @"C:\Windows\Fonts\verdana.ttf",
                @"C:\Windows\Fonts\calibri.ttf",
                // Linux
                "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
                "/usr/share/fonts/truetype/freefont/FreeSans.ttf",
                "/usr/share/fonts/opentype/cantarell/Cantarell-Regular.otf",
                // macOS
                "/System/Library/Fonts/Helvetica.ttc",
                "/System/Library/Fonts/Arial.ttf",
                "/Library/Fonts/Arial.ttf",
            ];

            foreach (string path in candidates)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    s_systemFallback = new TrueTypeFont(File.ReadAllBytes(path));
                    break;
                }
                catch
                {
                    // Try next candidate
                }
            }

            return s_systemFallback;
        }
    }

    public TrueTypeFont? Typeface { get; }
    public float Size { get; }
    public string FamilyName { get; }
    public bool Bold { get; }
    public bool Italic { get; }

    /// <summary>Returns the effective typeface — the explicit one, or the system fallback.</summary>
    public TrueTypeFont? EffectiveTypeface => Typeface ?? GetSystemFallback();

    public Font(float size, string familyName = "Sans-Serif", bool bold = false, bool italic = false)
    {
        Size = Math.Max(0.1f, size);
        FamilyName = familyName;
        Bold = bold;
        Italic = italic;
        Typeface = null;
    }

    public Font(TrueTypeFont typeface, float size, bool bold = false, bool italic = false)
    {
        Typeface = typeface ?? throw new ArgumentNullException(nameof(typeface));
        Size = Math.Max(0.1f, size);
        FamilyName = "TrueType";
        Bold = bold;
        Italic = italic;
    }

    public float MeasureText(ReadOnlySpan<char> text)
    {
        var face = EffectiveTypeface;
        if (face != null)
        {
            float totalWidth = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                int codePoint = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                    ? char.ConvertToUtf32(text[i++], text[i])
                    : text[i];
                int glyphIdx = face.GetGlyphIndex(codePoint);
                ushort adv = face.GetAdvanceWidth(glyphIdx);
                totalWidth += (adv / (float)face.UnitsPerEm) * Size;
            }
            return totalWidth;
        }

        // Fallback default proportional metric (approx 0.6 em per character)
        return text.Length * Size * 0.6f;
    }
}
