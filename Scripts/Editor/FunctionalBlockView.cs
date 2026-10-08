using System;
using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.Editor;

/// <summary>
/// Визуальный слой функциональных блоков с настоящей 3D-моделью (<see cref="FunctionalBlockComponent.ScenePath"/>) — как и
/// <see cref="ShapeInstanceView"/> для Wedge/Pyramid, у каждого такого экземпляра свой собственный узел (glTF-сцена), а не чанкованный куб. Блок БЕЗ модели
/// этот слой не трогает — по-прежнему рисуется обычным кубом через <see cref="ChunkMesher"/> (см. <see cref="Construction"/>, которая отключает маску покрытия куба
/// ровно для блоков с <see cref="FunctionalBlockComponent.ScenePath"/>).
/// <para/>
/// Положение модели — рамка блока (<see cref="FunctionalBlockGeometry.InstanceFrame"/>) · масштаб и якорь (<see cref="BlockModelLayout.ModelTransform"/>).
/// <see cref="Sync"/> вызывается на каждое изменение постройки (<see cref="Construction.Changed"/>).
/// <para/>
/// <b>Анимация</b> (если задан <see cref="Runtime"/> — у заспавненной в мире постройки, см. <see cref="World.VehicleSpawner"/>): кнопки получают свой
/// <see cref="ButtonVisual"/> (нажатие + свечение). Валы НЕ вращаются визуально: вал — аналог трубы, он только передаёт крутящий момент (сеть вращения — <see cref="TorqueNetwork"/>).
/// <para/>
/// <b>Отображение в инструментах редактора:</b> <see cref="DefaultOpacity"/> делает все модели полупрозрачными, <see cref="SetInstanceTint"/> красит модель
/// одного экземпляра сплошным цветом (подсветка в «Parameters»).
/// </summary>
public partial class FunctionalBlockView : Node3D
{
    private readonly Dictionary<int, Node3D> _views = new();
    private readonly Dictionary<int, FunctionalBlockComponent> _definitions = new();
    private readonly Dictionary<int, ButtonVisual> _buttonVisuals = new();
    private readonly Dictionary<int, Color> _tints = new();
    private readonly Dictionary<Color, StandardMaterial3D> _tintMaterials = new();
    private float _defaultOpacity = 1f;

    /// <summary>Рантайм функциональных блоков этой постройки; null — анимации нет (редактор, иконки).</summary>
    public FunctionalBlockRuntime? Runtime { get; set; }

    /// <summary>Непрозрачность моделей (1 — как обычно). Экземпляры с подсветкой (<see cref="SetInstanceTint"/>) всегда непрозрачны.</summary>
    public float DefaultOpacity
    {
        get => _defaultOpacity;
        set
        {
            _defaultOpacity = value;
            foreach (int id in _views.Keys) ApplyDisplay(id);
        }
    }

    /// <summary>Корневой узел модели экземпляра (для самотестов и подсветки); null — у экземпляра нет модели в этом слое.</summary>
    public Node3D? GetView(int instanceId) => _views.GetValueOrDefault(instanceId);

    /// <summary>Красит модель экземпляра сплошным цветом (null — вернуть обычный вид). Подсвеченная модель непрозрачна, даже если остальные полупрозрачны.</summary>
    public void SetInstanceTint(int instanceId, Color? tint)
    {
        if (tint.HasValue) _tints[instanceId] = tint.Value;
        else _tints.Remove(instanceId);
        if (_views.ContainsKey(instanceId)) ApplyDisplay(instanceId);
    }

    /// <summary>Снимает подсветку со всех экземпляров.</summary>
    public void ClearTints()
    {
        var ids = new List<int>(_tints.Keys);
        _tints.Clear();
        foreach (int id in ids)
        {
            if (_views.ContainsKey(id)) ApplyDisplay(id);
        }
    }

    private StandardMaterial3D TintMaterial(Color color)
    {
        if (_tintMaterials.TryGetValue(color, out var material)) return material;

        // Освещаемый (чтобы форма читалась), с небольшим собственным свечением — цвет виден и в тени, и сквозь полупрозрачные блоки.
        material = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.9f, EmissionEnabled = true, Emission = color, EmissionEnergyMultiplier = 0.35f };
        _tintMaterials[color] = material;
        return material;
    }

    private void ApplyDisplay(int instanceId)
    {
        if (!_views.TryGetValue(instanceId, out var node)) return;

        bool tinted = _tints.TryGetValue(instanceId, out var tint);
        float transparency = tinted ? 0f : 1f - _defaultOpacity;
        foreach (var mesh in MeshesOf(node))
        {
            mesh.Transparency = transparency;
            mesh.MaterialOverride = tinted ? TintMaterial(tint) : null;
        }
    }

    private static IEnumerable<MeshInstance3D> MeshesOf(Node root)
    {
        if (root is MeshInstance3D self) yield return self;
        foreach (var child in root.GetChildren())
        {
            foreach (var mesh in MeshesOf(child)) yield return mesh;
        }
    }

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
            bool created = false;
            if (!_views.TryGetValue(instance.InstanceId, out var node))
            {
                node = scene.Instantiate<Node3D>();
                _views[instance.InstanceId] = node;
                _definitions[instance.InstanceId] = functional;
                AddChild(node);
                created = true;
            }

            // Модель стоит в рамке блока по явному масштабу и якорю (BlockModelLayout), а поворот и положение даёт рамка
            // корневой клетки - блок крутится вокруг неё (см. BlockFootprint), а не вокруг центра своего хитбокса.
            var frame = FunctionalBlockGeometry.InstanceFrame(instance.Origin, functional.FootprintMin, functional.Footprint, instance.RotationSteps);
            var model = BlockModelLayout.ModelTransform(aabb, functional.ModelScale, functional.Anchor);
            node.Transform = frame * model;
            if (created) ApplyDisplay(instance.InstanceId);
        }

        foreach (var id in new List<int>(_views.Keys))
        {
            if (alive.Contains(id)) continue;
            _views[id].QueueFree();
            _views.Remove(id);
            _definitions.Remove(id);
            _buttonVisuals.Remove(id);
            _tints.Remove(id);
        }
    }

    public override void _Process(double delta)
    {
        if (Runtime == null) return;

        foreach (var (id, node) in _views)
        {
            var definition = _definitions[id];
            if (definition.Behavior != ButtonBehavior.Key) continue;

            var state = Runtime.GetState<ButtonState>(id);
            if (state == null) continue;

            if (!_buttonVisuals.TryGetValue(id, out var visual))
            {
                visual = new ButtonVisual(node, ButtonSettings.From(definition));
                _buttonVisuals[id] = visual;
            }

            visual.Update(delta, state.Active, state.Powered);
        }
    }
}
