using Godot;

namespace SandboxPolyGame.Core;

/// <summary>Цвет клетки хранится упакованным в uint (RGBA8, альфа всегда 255, поэтому значение никогда не равно 0).</summary>
public static class CellColor
{
    public static uint Pack(Color c)
    {
        uint r = (uint)Mathf.Clamp(Mathf.RoundToInt(c.R * 255f), 0, 255);
        uint g = (uint)Mathf.Clamp(Mathf.RoundToInt(c.G * 255f), 0, 255);
        uint b = (uint)Mathf.Clamp(Mathf.RoundToInt(c.B * 255f), 0, 255);
        return r | (g << 8) | (b << 16) | (255u << 24);
    }

    public static Color Unpack(uint packed) => new(
        (packed & 255u) / 255f,
        ((packed >> 8) & 255u) / 255f,
        ((packed >> 16) & 255u) / 255f);
}
