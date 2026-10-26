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
/// <see cref="ButtonVisual"/> (нажатие + свечение), колёса — <see cref="WheelVisual"/> (вращение, руль, радиусы, пробитая шина). Валы НЕ вращаются визуально: вал — аналог трубы, он только передаёт крутящий момент (сеть вращения — <see cref="TorqueNetwork"/>).
/// <para/>
/// <b>Отображение в инструментах редактора:</b> <see cref="DefaultOpacity"/> делает все модели полупрозрачными, <see cref="SetInstanceTint"/> красит модель
/// одного экземпляра сплошным цветом (подсветка в «Parameters»).
/// </summary>
public partial class FunctionalBlockView : Node3D
{
    private readonly Dictionary<int, Node3D> _views = new();
    private readonly Dictionary<int, FunctionalBlockComponent> _definitions = new();
    private readonly Dictionary<int, ButtonVisual> _buttonVisuals = new();
    private readonly Dictionary<int, WheelVisual> _wheelVisuals = new();
    private readonly Dictionary<int, List<Node3D>> _decor = new();
    private readonly Dictionary<int, Color> _tints = new();
    private readonly Dictionary<Color, StandardMaterial3D> _tintMaterials = new();
    private float _defaultOpacity = 1f;

    /// <summary>
    /// Показывать декоративные меши-указатели блоков (<see cref="DecorMesh"/>, режим <see cref="DecorShowWhen.Always"/>) — только в редакторе построек. В игре (заспавненная постройка)
    /// выключено, и указателей там нет вовсе.
    /// </summary>
    public bool ShowDecor { get; set; }

    /// <summary>Сколько узлов-указателей сейчас у экземпляра (для самотестов).</summary>
    /// <summary>Положение указателя экземпляра (для самотестов).</summary>
    public Transform3D DecorTransform(int instanceId, int index) => _decor[instanceId][index].Transform;

    public int DecorCount(int instanceId) => _decor.TryGetValue(instanceId, out var nodes) ? nodes.Count : 0;

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
            if (functional == null) continue;
            SyncDecor(instance, functional, alive);
            if (string.IsNullOrEmpty(functional.ScenePath)) continue;

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
            var model = BlockModelLayout.ModelTransform(aabb, functional.ModelScale, functional.Anchor, functional.ModelRotationBasis);
            node.Transform = frame * model;
            if (created) ApplyDisplay(instance.InstanceId);
        }

        foreach (var id in new List<int>(_decor.Keys))
        {
            if (construction.GetInstance(id) != null) continue;
            foreach (var node in _decor[id]) node.QueueFree();
            _decor.Remove(id);
        }

        foreach (var id in new List<int>(_views.Keys))
        {
            if (alive.Contains(id)) continue;
            _views[id].QueueFree();
            _views.Remove(id);
            _definitions.Remove(id);
            _buttonVisuals.Remove(id);
            _wheelVisuals.Remove(id);
            _tints.Remove(id);
        }
    }

    /// <summary>Указатели экземпляра пересоздаются при каждой синхронизации (их единицы на блок): рамка экземпляра · положение указателя в рамке блока.</summary>
    private void SyncDecor(BlockInstance instance, FunctionalBlockComponent functional, HashSet<int> alive)
    {
        if (_decor.TryGetValue(instance.InstanceId, out var old))
        {
            foreach (var node in old) node.QueueFree();
            _decor.Remove(instance.InstanceId);
        }

        if (!ShowDecor) return;
        var decorAlive = false;
        var frame = FunctionalBlockGeometry.InstanceFrame(instance.Origin, functional.FootprintMin, functional.Footprint, instance.RotationSteps);
        var mirror = FunctionalBlockGeometry.DecorMirror(functional, instance.Mirror);
        var nodes = new List<Node3D>();
        foreach (var decor in functional.Decor)
        {
            if (decor.ShowWhen != DecorShowWhen.Always) continue;
            var (scene, _) = FunctionalBlockGeometry.GetOrLoadScene(decor.Scene);
            if (scene == null) continue;

            var node = scene.Instantiate<Node3D>();
            AddChild(node);
            node.Transform = frame * mirror * decor.LocalTransform;
            nodes.Add(node);
            decorAlive = true;
        }

        if (decorAlive)
        {
            _decor[instance.InstanceId] = nodes;
            alive.Add(instance.InstanceId);
        }
    }

    public override void _Process(double delta)
    {
        if (Runtime == null) return;

        foreach (var (id, node) in _views)
        {
            var definition = _definitions[id];
            if (definition.Behavior == WheelBehavior.Key)
            {
                var wheel = Runtime.GetState<WheelState>(id);
                if (wheel == null) continue;

                // Состояние пересоздаётся при смене параметров экземпляра (радиусы) - визуал держит только узлы модели, радиусы читает из состояния каждый кадр.
                if (!_wheelVisuals.TryGetValue(id, out var wheelVisual))
                {
                    wheelVisual = new WheelVisual(node, wheel.Settings);
                    _wheelVisuals[id] = wheelVisual;
                }

                wheelVisual.Update(delta, wheel);
                continue;
            }

            if (definition.Behavior != ButtonBehavior.Key) continue;

            var state = Runtime.GetState<ButtonState>(id);
            if (state == null) continue;

            if (!_buttonVisuals.TryGetValue(id, out var visual))
            {
                visual = new ButtonVisual(node, ButtonSettings.From(definition) with { Mode = state.Mode, GlowColor = state.GlowColor });
                _buttonVisuals[id] = visual;
            }

            visual.GlowColor = state.GlowColor; // состояние пересоздаётся при смене параметров экземпляра - цвет мог поменяться

            visual.Update(delta, state.Active, state.Powered);
        }
    }
}
