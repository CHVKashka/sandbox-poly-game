using Godot;

namespace SandboxPolyGame.Blocks;

/// <summary>
/// Один бокс коллизии функционального блока (см. <see cref="FunctionalBlockComponent.CollisionBoxes"/>) — в
/// локальной системе координат НЕПОВЁРНУТОГО блока (<see cref="Core.BlockInstance.RotationSteps"/> == 0), метры,
/// <see cref="Position"/> — минимальный угол бокса относительно минимального угла footprint'а (тем же соглашением,
/// что и <see cref="Core.BuildSpace.CellMin"/> у самой клетки). Пусто (<see cref="FunctionalBlockComponent.CollisionBoxes"/>
/// — пустой список) — у функционального блока коллизии НЕТ ВООБЩЕ (намеренно, никакого автоматического бокса "на
/// всякий случай" — некоторые модели нарочно выпирают за пределы footprint деталями, которым коллизия не нужна, см.
/// <see cref="World.VehicleSpawner"/>). Это отличается от обычного блока (куб/форма, без <see cref="FunctionalBlockComponent"/>
/// вовсе) — тот всегда получает автоматический бокс на весь экземпляр, этого поля у него просто нет.
/// </summary>
public sealed class CollisionBox
{
    public required Vector3 Position { get; init; }
    public required Vector3 Size { get; init; }
}
