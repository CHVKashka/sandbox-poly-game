using Godot;

namespace SandboxPolyGame.Core;

/// <summary>
/// Занимаемая область блока при повороте — для ВСЕХ блоков (функциональные с фиксированным
/// <see cref="Blocks.FunctionalBlockComponent.Footprint"/> и резиновые куб/формы с выбранным <c>PendingSize</c>). Блок крутится вокруг своей
/// <b>корневой клетки</b> — клетки (0,0,0) неповёрнутого («локального») бокса, той, что стоит под курсором при установке: она при
/// повороте остаётся на месте, остальные клетки поворачиваются вокруг её центра (<c>клетка = корень + R · локальная_клетка</c>, где R —
/// <see cref="ShapeMeshBuilder.ComposeRotation"/>), и размер бокса поворачивается вместе с блоком. Занятая область — всё равно
/// прямоугольный бокс по осям сетки (поворот на 90° переводит бокс в бокс), поэтому <see cref="BlockInstance.Origin"/>/
/// <see cref="BlockInstance.Size"/> по-прежнему описывают её без изменений формата: это уже ПОВЁРНУТЫЙ бокс (размер — перестановка осей
/// локального размера, origin сдвинут так, чтобы корневая клетка осталась там, где стояла). Локальный размер блока при необходимости
/// восстанавливается из повёрнутого и ступеней (<see cref="UnrotatedSize"/>) — он нужен меш-билдеру форм и подгонке модели.
/// </summary>
public static class BlockFootprint
{
    /// <summary>Поворачивает целый вектор клеток на <paramref name="rotationSteps"/> (результат — снова целые, повороты кратны 90°).</summary>
    public static Vector3I Rotate(Vector3I vector, Vector3I rotationSteps) =>
        Round(ShapeMeshBuilder.ComposeRotation(rotationSteps) * new Vector3(vector.X, vector.Y, vector.Z));

    private static Vector3I Round(Vector3 v) => new(Mathf.RoundToInt(v.X), Mathf.RoundToInt(v.Y), Mathf.RoundToInt(v.Z));

    /// <summary>Размер занятого бокса блока с локальным размером <paramref name="localSize"/> после поворота (перестановка осей, без знаков).</summary>
    public static Vector3I RotatedSize(Vector3I localSize, Vector3I rotationSteps)
    {
        var r = Rotate(localSize, rotationSteps);
        return new Vector3I(System.Math.Abs(r.X), System.Math.Abs(r.Y), System.Math.Abs(r.Z));
    }

    /// <summary>Обратное к <see cref="RotatedSize"/>: локальный (неповёрнутый) размер блока по размеру его занятого бокса
    /// <paramref name="aabbSize"/> и ступеням поворота.</summary>
    public static Vector3I UnrotatedSize(Vector3I aabbSize, Vector3I rotationSteps)
    {
        var inverse = ShapeMeshBuilder.ComposeRotation(rotationSteps).Inverse();
        var r = Round(inverse * new Vector3(aabbSize.X, aabbSize.Y, aabbSize.Z));
        return new Vector3I(System.Math.Abs(r.X), System.Math.Abs(r.Y), System.Math.Abs(r.Z));
    }

    /// <summary>
    /// Занятый бокс (мин. угол и размер в клетках) блока с локальным размером <paramref name="localSize"/>, чья корневая клетка стоит
    /// в <paramref name="rootCell"/>, после поворота на <paramref name="rotationSteps"/>. Без поворота — просто
    /// (<paramref name="rootCell"/>, <paramref name="localSize"/>), как и раньше.
    /// </summary>
    public static (Vector3I Origin, Vector3I Size) PlaceBox(Vector3I rootCell, Vector3I localSize, Vector3I rotationSteps) =>
        PlaceBox(rootCell, Vector3I.Zero, localSize, rotationSteps);

    /// <summary>
    /// То же для блока, чей локальный бокс начинается НЕ с корневой клетки: <paramref name="localMin"/> — минимальная клетка бокса
    /// в рамке блока (индексы от корня, могут быть отрицательными — так бывает у функционального блока, якорь модели которого
    /// не в её мин. углу, см. <see cref="BlockModelLayout"/>), корень (0,0,0) может лежать в любой клетке бокса и при повороте
    /// остаётся на месте. С <c>localMin == (0,0,0)</c> это ровно прежняя перегрузка.
    /// </summary>
    public static (Vector3I Origin, Vector3I Size) PlaceBox(Vector3I rootCell, Vector3I localMin, Vector3I localSize, Vector3I rotationSteps)
    {
        // Две противоположные клетки бокса после поворота; сам корень — (0,0,0) — остаётся на месте.
        var a = Rotate(localMin, rotationSteps);
        var b = Rotate(localMin + localSize - Vector3I.One, rotationSteps);
        var min = new Vector3I(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y), System.Math.Min(a.Z, b.Z));
        var max = new Vector3I(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y), System.Math.Max(a.Z, b.Z));
        return (rootCell + min, max - min + Vector3I.One);
    }

    /// <summary>Обратное к <see cref="PlaceBox(Vector3I, Vector3I, Vector3I, Vector3I)"/>: корневая клетка уже поставленного блока по
    /// минимальному углу его занятого бокса (<see cref="BlockInstance.Origin"/>), локальному боксу и ступеням поворота.</summary>
    public static Vector3I RootCell(Vector3I origin, Vector3I localMin, Vector3I localSize, Vector3I rotationSteps) =>
        origin - PlaceBox(Vector3I.Zero, localMin, localSize, rotationSteps).Origin;
}
