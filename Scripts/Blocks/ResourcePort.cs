using Godot;

namespace SandboxPolyGame.Blocks;

/// <summary>Направление порта функционального блока — потребляет ресурс или отдаёт его.</summary>
public enum PortDirection
{
    In,
    Out,
}

/// <summary>
/// Одна из 6 осеориентированных сторон НЕповёрнутого footprint'а блока, на которой может сидеть
/// <see cref="ResourcePort"/> (см. <see cref="ResourcePort.Face"/>). Та же конвенция (индекс = <c>axis*2+(positive?1:0)</c>),
/// что и у битовой маски сторон в <see cref="Core.ShapeMeshBuilder"/> (<c>TryAxisAlignedBit</c>)/<c>VoxelGrid.GetFaceMask</c> —
/// используется тут не ради совместимости формата (порты её не используют), а чтобы не плодить вторую, другую по
/// порядку нумерацию сторон куба в том же проекте.
/// </summary>
public enum BlockFace
{
    NegX = 0,
    PosX = 1,
    NegY = 2,
    PosY = 3,
    NegZ = 4,
    PosZ = 5,
}

/// <summary>
/// Один вход/выход функционального блока (см. <see cref="FunctionalBlockComponent.Ports"/>): какой ресурс, в какую
/// сторону, и ГДЕ он физически находится на хитбоксе блока. <see cref="Id"/> — стабильный идентификатор порта внутри
/// блока (на будущее — для адресации соединений, см. Docs/05-world-and-vehicle-systems.md, «Соединения»), сейчас
/// используется только для отображения.
/// <para/>
/// <see cref="Face"/> + <see cref="FaceCell"/> задают положение БЕЗ ограничения footprint'а 1×1×1 — footprint
/// функционального блока уже поддерживает произвольный размер (<see cref="FunctionalBlockComponent.Footprint"/>,
/// не клампится до 1×1×1 нигде, см. <see cref="Editor.EditorState"/>), и порт может сидеть в ЛЮБОЙ клетке ЛЮБОЙ из
/// 6 сторон такого footprint'а — см. <see cref="Editor.FunctionalBlockGeometry.ComputePortAnchor"/> для перевода
/// (Face, FaceCell) в мировую/локальную точку. Намеренно НЕ валидируется и не дедуплицируется здесь: несколько
/// портов могут сидеть в ОДНОЙ и той же (Face, FaceCell) — например, кабель и провод питания в одном месте панели —
/// это осознанно разрешено (по запросу пользователя), не ошибка данных.
/// </summary>
public sealed class ResourcePort
{
    public required string Id { get; init; }
    public required ResourceType Resource { get; init; }
    public required PortDirection Direction { get; init; }

    /// <summary>На какой из 6 сторон footprint'а сидит порт. По умолчанию <see cref="BlockFace.PosZ"/> — применяется
    /// к портам, у которых в XML нет поля <c>"face"</c> (старые, написанные руками до появления этого поля,
    /// см. <see cref="FunctionalBlockComponent"/> doc) — не ошибка, просто "лицевая" сторона по умолчанию.</summary>
    public BlockFace Face { get; init; } = BlockFace.PosZ;

    /// <summary>Координата клетки порта ВНУТРИ 2D-сетки этой стороны (0-based; см. <see cref="Editor.FunctionalBlockGeometry.FaceDimensions"/>
    /// для того, что именно означают X/Y для каждой стороны — это НЕ мировые/блочные оси, а оси конкретной грани).
    /// По умолчанию (0,0) — угол грани, ближайший к минимальному углу footprint'а.</summary>
    public Vector2I FaceCell { get; init; } = Vector2I.Zero;
}
