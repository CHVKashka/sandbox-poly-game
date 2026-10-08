using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.World;

/// <summary>
/// Строит физическое тело постройки (<see cref="VehicleBody"/>, тонкая обёртка над <see cref="RigidBody3D"/>) в
/// мире по сериализованной (<see cref="ConstructionIO.Serialize"/>) постройке — используется кнопкой Spawn в
/// редакторе (см. <c>Core.EditorHandoff.PendingSpawnJson</c>, читается и сбрасывается <see cref="GameWorld"/> при
/// входе в мир). Центр масс и масса — по блокам (<see cref="BaseComponent.Mass"/>, "кг на клетку 1x1x1"), коллизия
/// — для обычного блока (куб/форма) один <see cref="BoxShape3D"/> НА ЭКЗЕМПЛЯР целиком (не на клетку — заметно
/// меньше форм на растянутую постройку), как и раньше; функциональный блок (<see cref="FunctionalBlockComponent"/>)
/// получает РОВНО то, что задано в его <see cref="FunctionalBlockComponent.CollisionBoxes"/>
/// (клетки коллизии из редактора блоков, слитые в боксы) — БЕЗ автоматического общего бокса, пусто означает "блок физически проходим
/// насквозь целиком" (нарочно — у некоторых моделей есть выпирающие за пределы footprint детали, которым коллизия
/// не нужна). Визуал переиспользует <see cref="VoxelWorld"/> (тот же рендер, что и в редакторе, просто под <see cref="RigidBody3D"/>
/// вместо статичного узла), без чёрных границ блоков (тот инструмент — только для редактора). Плавучесть/
/// аэродинамика сюда сознательно не входят — см. Docs/05-world-and-vehicle-systems.md, «Спавн постройки»: зависят
/// от ещё не собранного «сборщика сил», добавляются отдельным шагом позже.
/// </summary>
public static class VehicleSpawner
{
    /// <summary><paramref name="spawnPosition"/> — мировая точка, где должна оказаться НИЖНЯЯ точка постройки по Y
    /// (не даёт ей появиться наполовину под землёй) и центр по X/Z (не обязательно центр масс — геометрический
    /// центр её занятой области, что нагляднее для игрока, наблюдающего появление). <paramref name="sourceWorkbench"/> —
    /// верстак, с которого спавнили (см. <see cref="VehicleBody.SourceWorkbench"/>, нужен для возврата по `R`).</summary>
    public static VehicleBody Spawn(Node3D parent, string constructionJson, Vector3 spawnPosition, Workbench? sourceWorkbench = null)
    {
        var body = new VehicleBody { Name = "Vehicle", SourceWorkbench = sourceWorkbench };
        parent.AddChild(body);

        var voxelWorld = new VoxelWorld();
        body.AddChild(voxelWorld);
        body.RegisterVisual(voxelWorld);
        voxelWorld.BordersOn = false; // чёрная сетка границ блоков — инструмент редактора, не для готовых построек в мире
        ConstructionIO.Deserialize(voxelWorld.Construction, constructionJson, BlockCatalog.Instance);
        voxelWorld.RebuildDirty();

        var construction = voxelWorld.Construction;
        body.Runtime = new FunctionalBlockRuntime(construction, BlockCatalog.Instance);
        voxelWorld.FunctionalBlocks.Runtime = body.Runtime; // модели кнопок анимируются по состоянию из рантайма
        double totalMass = 0;
        Vector3 weightedCenterSum = Vector3.Zero;

        foreach (var instance in construction.Instances)
        {
            if (!BlockCatalog.Instance.TryGetBySlug(instance.BlockSlug, out var definition)) continue;

            double massPerCell = definition.GetComponent<BaseComponent>()?.Mass ?? 1.0;
            int cellCount = instance.Size.X * instance.Size.Y * instance.Size.Z;
            double instanceMass = massPerCell * cellCount;
            totalMass += instanceMass;

            var instanceMin = BuildSpace.CellMin(instance.Origin);
            var instanceMax = BuildSpace.CellMin(instance.Origin + instance.Size);
            var instanceCenter = (instanceMin + instanceMax) * 0.5f;
            weightedCenterSum += instanceCenter * (float)instanceMass;

            var functional = definition.GetComponent<FunctionalBlockComponent>();
            if (functional != null)
            {
                // Функциональный блок - коллизия ТОЛЬКО то, что явно задано клетками в его XML (слитыми при загрузке в минимальный
                // набор боксов, см. CollisionCells.Merge), никакого автоматического бокса на весь экземпляр "на всякий случай":
                // некоторые детали модели нарочно выпирают за пределы footprint и не должны иметь коллизию вообще. Пусто - блок
                // физически проходим насквозь целиком. Координаты боксов - в рамке блока (метры от угла корневой клетки), повёрнуты
                // и сдвинуты той же рамкой, что и модель (FunctionalBlockView): блок крутится вокруг корневой клетки.
                // CollisionShape3D.Transform, не только Position, т.к. поворот тоже нужен (бокс не обязан быть кубом).
                var frame = FunctionalBlockGeometry.InstanceFrame(instance.Origin, functional.FootprintMin, functional.Footprint, instance.RotationSteps);
                foreach (var box in functional.CollisionBoxes)
                {
                    body.AddChild(new CollisionShape3D
                    {
                        Shape = new BoxShape3D { Size = box.SizeMeters },
                        Transform = frame * new Transform3D(Basis.Identity, box.CenterMeters),
                    });
                }
            }
            else
            {
                // Обычный блок (куб/форма, BuildingBlockComponent) - как и раньше, один бокс на весь экземпляр
                // целиком - не затронуто этим изменением, у таких блоков своих боксов коллизии не бывает.
                body.AddChild(new CollisionShape3D
                {
                    Shape = new BoxShape3D { Size = instanceMax - instanceMin },
                    Position = instanceCenter,
                });
            }
        }

        if (totalMass <= 0) totalMass = 1; // защита от постройки без единого распознанного блока (не должно происходить)
        var centerOfMass = construction.Instances.Count > 0 ? weightedCenterSum / (float)totalMass : Vector3.Zero;

        body.Mass = (float)totalMass;
        body.CenterOfMassMode = RigidBody3D.CenterOfMassModeEnum.Custom;
        body.CenterOfMass = centerOfMass;

        var (minCell, maxCell) = construction.ComputeBounds();
        var boundsMin = BuildSpace.CellMin(minCell);
        var boundsMax = BuildSpace.CellMin(maxCell + Vector3I.One);
        var centerX = (boundsMin.X + boundsMax.X) / 2f;
        var centerZ = (boundsMin.Z + boundsMax.Z) / 2f;
        body.Position = new Vector3(spawnPosition.X - centerX, spawnPosition.Y - boundsMin.Y, spawnPosition.Z - centerZ);

        return body;
    }
}
