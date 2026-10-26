using System.Collections.Generic;
using Godot;

namespace SandboxPolyGame.Core;

/// <summary>
/// Один размещённый в постройке блок: прямоугольная область клеток одного типа и цвета. Изначально 1x1x1;
/// блоки с компонентом <see cref="Blocks.BuildingBlockComponent"/> можно растягивать инструментом Resize
/// (см. <see cref="Construction.Grow"/>/<see cref="Construction.Shrink"/>).
/// </summary>
public sealed class BlockInstance
{
    public int InstanceId { get; init; }

    /// <summary>Слаг блока в <see cref="Blocks.BlockCatalog"/>.</summary>
    public required string BlockSlug { get; init; }

    /// <summary>Минимальный угол занимаемой области (клетка).</summary>
    public Vector3I Origin { get; set; }

    /// <summary>Размер в клетках по каждой оси (всегда >= 1).</summary>
    public Vector3I Size { get; set; } = Vector3I.One;

    /// <summary>Упакованный цвет (см. <see cref="CellColor"/>).</summary>
    public uint Color { get; set; }

    /// <summary>
    /// Три четверть-поворота (каждый 0..3) вокруг X, Y, Z соответственно, применённые в этом порядке —
    /// см. <see cref="ShapeMeshBuilder.ComposeRotation"/>. Влияет только на визуальную форму не-кубических блоков
    /// (Wedge/Pyramid/InvertedPyramid); задаётся один раз при установке блока (см. <c>Editor.EditorState</c>),
    /// после установки не меняется. Вращение — вокруг ЦЕНТРА блока, а не угла его вершин.
    /// </summary>
    public Vector3I RotationSteps { get; set; } = Vector3I.Zero;

    /// <summary>
    /// Отражение по X/Y/Z (каждая компонента 0 — как есть, 1 — отражена), применяется до вращения (см.
    /// <see cref="ShapeMeshBuilder.BuildData"/>). Влияет только на визуальную форму не-кубических блоков; задаётся
    /// один раз при установке блока (см. <c>Editor.EditorState.PendingMirror</c>), после установки не меняется.
    /// </summary>
    public Vector3I Mirror { get; set; } = Vector3I.Zero;

    /// <summary>Включительный максимальный угол занимаемой области.</summary>
    public Vector3I MaxCell => Origin + Size - Vector3I.One;

    /// <summary>
    /// Точечная покраска отдельных наклонных/треугольных граней не-кубической формы (см.
    /// <see cref="Construction.PaintRegion"/>/<see cref="ShapeMeshBuilder.TryFindPaintRegion"/>) — ключ: индекс
    /// грани в массиве <c>faces</c> у <see cref="BlockGeometry"/> (стабилен независимо от поворота/отражения/
    /// размера), значение: упакованный цвет (см. <see cref="CellColor"/>). null или отсутствие ключа — грань красится
    /// в общий <see cref="Color"/> экземпляра. Не персистентно (как и поклеточная покраска граней куба, см.
    /// <see cref="ConstructionIO"/>) — Save/Load в JSON хранит только <see cref="Color"/>; Ctrl+Z/Ctrl+Y это видят
    /// (см. <c>UndoHistory</c>).
    /// </summary>
    public Dictionary<int, uint>? RegionColors { get; set; }

    /// <summary>
    /// Значения настраиваемых параметров этого экземпляра, ОТЛИЧАЮЩИЕСЯ от умолчания схемы (<see cref="Blocks.ParametersComponent"/>) —
    /// id параметра → каноническое текстовое значение. null/пусто — всё по умолчанию. Меняется инструментом «Parameters»
    /// (<see cref="Construction.TrySetParameter"/>), сохраняется в файле постройки, откатывается Undo; читают поведения через
    /// <see cref="Runtime.ParameterSet"/>.
    /// </summary>
    public Dictionary<string, string>? Parameters { get; set; }
}
