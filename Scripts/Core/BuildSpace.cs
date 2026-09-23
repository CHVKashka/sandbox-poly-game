using Godot;

namespace SandboxPolyGame.Core;

/// <summary>Параметры сетки построек. Одна клетка = один блок = <see cref="CellSize"/> метров.</summary>
public static class BuildSpace
{
    public const float CellSize = 0.25f;

    public const int ChunkShift = 4;
    public const int ChunkSize = 1 << ChunkShift; // 16 клеток по ребру
    public const int ChunkMask = ChunkSize - 1;
    public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

    /// <summary>Допустимая область построек (включительно), в клетках: 64 м x 64 м x 64 м.</summary>
    public static readonly Vector3I MinCell = new(-128, -64, -128);
    public static readonly Vector3I MaxCell = new(127, 191, 127);

    public static bool InBounds(Vector3I c) =>
        c.X >= MinCell.X && c.X <= MaxCell.X &&
        c.Y >= MinCell.Y && c.Y <= MaxCell.Y &&
        c.Z >= MinCell.Z && c.Z <= MaxCell.Z;

    /// <summary>Мировая позиция минимального угла клетки.</summary>
    public static Vector3 CellMin(Vector3I c) => new Vector3(c.X, c.Y, c.Z) * CellSize;

    public static Vector3 CellCenter(Vector3I c) =>
        (new Vector3(c.X, c.Y, c.Z) + new Vector3(0.5f, 0.5f, 0.5f)) * CellSize;

    /// <summary>Координаты чанка, в котором лежит клетка (арифметический сдвиг корректен для отрицательных).</summary>
    public static Vector3I ChunkOf(Vector3I c) => new(c.X >> ChunkShift, c.Y >> ChunkShift, c.Z >> ChunkShift);

    /// <summary>Мировая позиция минимального угла чанка.</summary>
    public static Vector3 ChunkOrigin(Vector3I chunk) =>
        new Vector3(chunk.X, chunk.Y, chunk.Z) * (ChunkSize * CellSize);
}
