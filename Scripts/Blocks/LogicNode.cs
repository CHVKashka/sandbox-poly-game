using Godot;

namespace SandboxPolyGame.Blocks;

/// <summary>
/// Логическая нода функционального блока (см. <see cref="FunctionalBlockComponent.Nodes"/>) — вход или выход
/// электричества/булева/числа. В отличие от физического порта (<see cref="ResourcePort"/>: вал/труба, сидит на ГРАНИ
/// блока), нода находится в ЦЕНТРЕ клетки блока: <see cref="Cell"/> — целая клетка внутри footprint'а (0-based, от
/// минимального угла), мировая точка — центр этой клетки (<see cref="Editor.FunctionalBlockGeometry.ComputeNodeAnchor"/>).
/// <para/>
/// Нод в блоке может быть сколько угодно. Несколько нод МОГУТ сидеть в одной клетке, но только РАЗНЫХ типов
/// (<see cref="Type"/>): две ноды одного типа в одной клетке — ошибка данных (даже если одна вход, другая выход), см.
/// <see cref="FunctionalBlockComponent.FindNodeConflict"/>.
/// </summary>
public sealed class LogicNode
{
    public required string Id { get; init; }
    public required NodeType Type { get; init; }
    public required PortDirection Direction { get; init; }

    /// <summary>Клетка блока, в центре которой сидит нода (по умолчанию (0,0,0) — клетка у минимального угла footprint'а).</summary>
    public Vector3I Cell { get; init; } = Vector3I.Zero;
}
