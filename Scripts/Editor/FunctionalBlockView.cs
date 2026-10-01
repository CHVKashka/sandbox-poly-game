using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Editor;

/// <summary>
/// Визуальный слой функциональных блоков с настоящей 3D-моделью (<see cref="FunctionalBlockComponent.ScenePath"/>,
/// например мотор/вал) — как и <see cref="ShapeInstanceView"/> для Wedge/Pyramid, у каждого такого экземпляра свой
/// собственный узел (импортированная glTF-сцена), а не чанкованный куб. Блок БЕЗ модели (батарея/бак/труба/кабель —
/// своих моделей для них пока нет) этот слой не трогает — по-прежнему рисуется обычным кубом через
/// <see cref="ChunkMesher"/> (см. <see cref="Construction"/>, которая отключает маску покрытия куба ровно для тех
/// блоков, у кого <see cref="FunctionalBlockComponent.ScenePath"/> задан — иначе ChunkMesher рисовал бы куб ПОД
/// моделью одновременно с ней).
/// <para/>
/// Подгонка модели под размер клетки — <see cref="FunctionalBlockGeometry"/> (общая с иконкой в хотбаре, см.
/// <see cref="Ui.BlockIconView"/>). <see cref="Sync"/> вызывается на каждое изменение постройки
/// (<see cref="Construction.Changed"/>), как и у <see cref="ShapeInstanceView"/>.
/// </summary>
public partial class FunctionalBlockView : Node3D
{
    private readonly Dictionary<int, Node3D> _views = new();

    public void Sync(Construction construction)
    {
        var alive = new HashSet<int>();

        foreach (var instance in construction.Instances)
        {
            if (!BlockCatalog.Instance.TryGetBySlug(instance.BlockSlug, out var definition)) continue;
            var functional = definition.GetComponent<FunctionalBlockComponent>();
            if (functional == null || string.IsNullOrEmpty(functional.ScenePath)) continue;

            var (scene, aabb) = FunctionalBlockGeometry.GetOrLoadScene(functional.ScenePath);
            if (scene == null) continue;

            alive.Add(instance.InstanceId);
            if (!_views.TryGetValue(instance.InstanceId, out var node))
            {
                node = scene.Instantiate<Node3D>();
                _views[instance.InstanceId] = node;
                AddChild(node);
            }

            var targetExtent = new Vector3(instance.Size.X, instance.Size.Y, instance.Size.Z) * BuildSpace.CellSize;
            var targetCenter = BuildSpace.CellMin(instance.Origin) + targetExtent * 0.5f;
            node.Transform = FunctionalBlockGeometry.ComputeFitTransform(aabb, targetExtent, targetCenter, instance.RotationSteps, functional.ModelScale);
        }

        foreach (var id in new List<int>(_views.Keys))
        {
            if (alive.Contains(id)) continue;
            _views[id].QueueFree();
            _views.Remove(id);
        }
    }
}
