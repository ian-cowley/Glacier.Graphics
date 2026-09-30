namespace Glacier.Graphics.Vector;

/// <summary>
/// Drawing verb for 2D vector path commands.
/// </summary>
public enum PathVerb : byte
{
    MoveTo,
    LineTo,
    QuadTo,
    CubicTo,
    Close
}
