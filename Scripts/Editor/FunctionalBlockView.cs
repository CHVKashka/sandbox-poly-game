using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.Editor;

/// <summary>
/// Визуальный слой функциональных блоков с настоящей 3D-моделью (<see cref="FunctionalBlockComponent.ScenePath"/>,
/// например мотор/вал) — как и <see cref="ShapeInstanceView"/> для Wedge/Pyramid, у каждого такого экземпляра свой
/// собственный узел (импортированная glTF-сцена), а не чанкованный куб. Блок БЕЗ модели (батарея/бак/кнопка —
/// своих моделей для них пока нет) этот слой не трогает — по-прежнему рисуется обычным кубом через
/// <see cref="ChunkMesher"/> (см. <see cref="Construction"/>, которая отключает маску покрытия куба ровно для тех
/// блоков, у кого <see cref="FunctionalBlockComponent.ScenePath"/> задан — иначе ChunkMesher рисовал бы куб ПОД
/// моделью одновременно с ней).
/// <para/>
/// Подгонка модели под размер клетки — <see cref="FunctionalBlockGeometry"/> (общая с иконкой в хотбаре, см.
/// <see cref="Ui.BlockIconView"/>). <see cref="Sync"/> вызывается на каждое изменение постройки
/// (<see cref="Construction.Changed"/>), как и у <see cref="ShapeInstanceView"/>.
/// <para/>
/// Анимация: если задан <see cref="Runtime"/> (у заспавненной в мире постройки — см. <see cref="World.VehicleSpawner"/>),
/// каждый кадр кнопки (<see cref="ButtonBehavior"/>) получают свой <see cref="ButtonVisual"/> (создаётся лениво) и
/// ведутся по состоянию из рантайма. В редакторе построек рантайма нет — модели статичны.
/// </summary>
public partial class FunctionalBlockView : Node3D
{
    private readonly Dictionary<int, Node3D> _views = new();
    private readonly Dictionary<int, FunctionalBlockComponent> _definitions = new();
    private readonly Dictionary<int, ButtonVisual> _buttonVisuals = new();

    /// <summary>Рантайм функциональных блоков этой постройки; null — анимации нет (редактор, иконки).</summary>
    public FunctionalBlockRuntime? Runtime { get; set; }

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
                _definitions[instance.InstanceId] = functional;
                AddChild(node);
            }

            // Модель подгоняется под НЕповёрнутый footprint (локальные метры), а поворот и положение даёт рамка занятого
            // бокса - блок крутится вокруг корневой клетки (см. BlockFootprint), а не вокруг центра своего хитбокса.
            var localExtent = new Vector3(functional.Footprint.X, functional.Footprint.Y, functional.Footprint.Z) * BuildSpace.CellSize;
            var frame = FunctionalBlockGeometry.InstanceFrame(instance.Origin, instance.Size, functional.Footprint, instance.RotationSteps);
            node.Transform = frame * FunctionalBlockGeometry.ComputeFitTransform(aabb, localExtent, localExtent * 0.5f, Basis.Identity, functional.ModelScale, functional.ModelOffset);
        }

        foreach (var id in new List<int>(_views.Keys))
        {
            if (alive.Contains(id)) continue;
            _views[id].QueueFree();
            _views.Remove(id);
            _definitions.Remove(id);
            _buttonVisuals.Remove(id);
        }
    }

    public override void _Process(double delta)
    {
        if (Runtime == null) return;

        foreach (var (id, node) in _views)
        {
            if (_definitions[id].Behavior != ButtonBehavior.Key) continue;
            var state = Runtime.GetState<ButtonState>(id);
            if (state == null) continue;

            if (!_buttonVisuals.TryGetValue(id, out var visual))
            {
                visual = new ButtonVisual(node, ButtonSettings.From(_definitions[id]));
                _buttonVisuals[id] = visual;
            }

            visual.Update(delta, state.Active, state.Powered);
        }
    }
}
