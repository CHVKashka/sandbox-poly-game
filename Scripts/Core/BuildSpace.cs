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

    /// <summary>
    /// Допустимая область построек (включительно), в клетках — симметрична относительно центра мира (0,0,0) по
    /// каждой оси: влево/вправо (X) и вверх/вниз (Y) по 25 м от центра, вперёд/назад (Z) по 50 м от центра
    /// (см. <see cref="Editor.BuildEditor"/> — там же граница области отрисовывается пунктирной линией). Центральная
    /// клетка (0,0,0) занимает угол [0,0,0]..[0.25,0.25,0.25] м — ровно с ней граничит начало координат, поэтому
    /// "центром" считается именно клетка (0,0,0) (см. <see cref="Editor.BuildEditor"/>, корневой блок редактора).
    /// Не const (в отличие от <see cref="CellSize"/>) — предполагается, что позже область сможет меняться в
    /// процессе игры (растущий мир и т.п.), тогда это станет обычным изменяемым состоянием; весь остальной код
    /// читает границы отсюда, а не хранит собственные копии чисел, так что готов к этому уже сейчас.
    /// </summary>
    private const float SideHalfExtentMeters = 25f;
    private const float ForwardHalfExtentMeters = 50f;

    public static readonly Vector3I MinCell = new(
        -(int)(SideHalfExtentMeters / CellSize), -(int)(SideHalfExtentMeters / CellSize), -(int)(ForwardHalfExtentMeters / CellSize));
    public static readonly Vector3I MaxCell = new(
        (int)(SideHalfExtentMeters / CellSize) - 1, (int)(SideHalfExtentMeters / CellSize) - 1, (int)(ForwardHalfExtentMeters / CellSize) - 1);

    public static bool InBounds(Vector3I c) =>
        c.X >= MinCell.X && c.X <= MaxCell.X &&
        c.Y >= MinCell.Y && c.Y <= MaxCell.Y &&
        c.Z >= MinCell.Z && c.Z <= MaxCell.Z;

    /// <summary>Мировая позиция минимального угла клетки.</summary>
    public static Vector3 CellMin(Vector3I c) => new Vector3(c.X, c.Y, c.Z) * CellSize;

    /// <summary>Мировые координаты минимального/максимального угла ВСЕЙ области построек (внешние грани клеток
    /// <see cref="MinCell"/>/<see cref="MaxCell"/>) — используется для отрисовки её границы, см.
    /// <see cref="Editor.BuildEditor"/>.</summary>
    public static Vector3 WorldMin => CellMin(MinCell);
    public static Vector3 WorldMax => CellMin(MaxCell + Vector3I.One);

    public static Vector3 CellCenter(Vector3I c) =>
        (new Vector3(c.X, c.Y, c.Z) + new Vector3(0.5f, 0.5f, 0.5f)) * CellSize;

    /// <summary>Координаты чанка, в котором лежит клетка (арифметический сдвиг корректен для отрицательных).</summary>
    public static Vector3I ChunkOf(Vector3I c) => new(c.X >> ChunkShift, c.Y >> ChunkShift, c.Z >> ChunkShift);

    /// <summary>Мировая позиция минимального угла чанка.</summary>
    public static Vector3 ChunkOrigin(Vector3I chunk) =>
        new Vector3(chunk.X, chunk.Y, chunk.Z) * (ChunkSize * CellSize);
}
