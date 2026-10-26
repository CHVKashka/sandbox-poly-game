using Godot;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Blocks;

/// <summary>Когда виден декоративный меш в редакторе построек (<see cref="DecorMesh.ShowWhen"/>).</summary>
public enum DecorShowWhen
{
    /// <summary>Только пока блок ставится (призрак под курсором) — указатель «куда смотрит блок» исчезает, когда блок поставлен.</summary>
    Ghost,

    /// <summary>И у призрака, и у уже поставленных блоков в редакторе построек.</summary>
    Always,
}

/// <summary>
/// Декоративный меш-указатель блока (стрелка направления, обозначение стороны вращения и т.п., как в редакторе Stormworks): отдельная glTF-модель, которая стоит в рамке блока
/// и поворачивается вместе с блоком, но видна ТОЛЬКО в редакторе построек — в игре (мир, заспавненная постройка) её нет, коллизии и клеток она не занимает. XML:
/// <c>"decor": [ { "scene": "res://meshes/arrow.glb", "position": [x,y,z], "rotation": [x,y,z], "scale": [x,y,z], "show": "Ghost" } ]</c>; <c>scene</c> обязателен, остальное
/// опционально. <see cref="Position"/> — точка рамки блока (метры от минимального угла корневой клетки; умолчание — центр корневой клетки), в которую встаёт НАЧАЛО КООРДИНАТ модели
/// (никакой подгонки по bbox: модель указателя рисуется с началом в нужной точке); <see cref="Rotation"/> — углы Эйлера в градусах (как у поворота модели блока);
/// <see cref="Scale"/> по осям модели (умолчание 0.125: 2 м в Blender = клетка).
/// </summary>
public sealed record DecorMesh(string Scene, Vector3 Position, Vector3 Rotation, Vector3 Scale, DecorShowWhen ShowWhen)
{
    /// <summary>Центр корневой клетки в рамке блока — положение нового указателя по умолчанию.</summary>
    public static readonly Vector3 DefaultPosition = new(BuildSpace.CellSize / 2, BuildSpace.CellSize / 2, BuildSpace.CellSize / 2);

    /// <summary>Новый указатель с умолчаниями для редактора блоков.</summary>
    public static DecorMesh Create(string scene = "") => new(scene, DefaultPosition, Vector3.Zero, BlockModelLayout.DefaultScaleVector, DecorShowWhen.Ghost);

    /// <summary>Положение модели в рамке блока: масштаб по осям, потом поворот, потом сдвиг в <see cref="Position"/>.</summary>
    public Transform3D LocalTransform => new(BlockModelLayout.RotationBasis(Rotation) * Basis.FromScale(Scale), Position);
}
