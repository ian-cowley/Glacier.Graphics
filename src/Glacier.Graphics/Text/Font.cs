namespace Glacier.Graphics.Text;

using System;

/// <summary>
/// Font specification including typeface, point size, and typographical styling.
/// </summary>
public sealed class Font
{
    public TrueTypeFont? Typeface { get; }
    public float Size { get; }
    public string FamilyName { get; }
    public bool Bold { get; }
    public bool Italic { get; }

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
        if (Typeface != null)
        {
            float totalWidth = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                int codePoint = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                    ? char.ConvertToUtf32(text[i++], text[i])
                    : text[i];
                int glyphIdx = Typeface.GetGlyphIndex(codePoint);
                ushort adv = Typeface.GetAdvanceWidth(glyphIdx);
                totalWidth += (adv / (float)Typeface.UnitsPerEm) * Size;
            }
            return totalWidth;
        }

        // Fallback default proportional metric (approx 0.6 em per character)
        return text.Length * Size * 0.6f;
    }
}
