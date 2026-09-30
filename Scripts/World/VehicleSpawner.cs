using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;

namespace SandboxPolyGame.World;

/// <summary>
/// Строит физическое тело постройки (<see cref="VehicleBody"/>, тонкая обёртка над <see cref="RigidBody3D"/>) в
/// мире по сериализованной (<see cref="ConstructionIO.Serialize"/>) постройке — используется кнопкой Spawn в
/// редакторе (см. <c>Core.EditorHandoff.PendingSpawnJson</c>, читается и сбрасывается <see cref="GameWorld"/> при
/// входе в мир). Центр масс и масса — по блокам (<see cref="BaseComponent.Mass"/>, "кг на клетку 1x1x1"), коллизия
/// — один <see cref="BoxShape3D"/> НА ЭКЗЕМПЛЯР (не на клетку — заметно меньше форм на растянутую постройку), визуал
/// переиспользует <see cref="VoxelWorld"/> (тот же рендер, что и в редакторе, просто под <see cref="RigidBody3D"/>
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

            body.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = instanceMax - instanceMin },
                Position = instanceCenter,
            });
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
