using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Editor;

/// <summary>
/// Визуальный слой не-кубических блоков (Wedge/Pyramid/InvertedPyramid): в отличие от кубов, которые чанками
/// меширует <see cref="ChunkMesher"/>, у каждого такого экземпляра — свой собственный <see cref="MeshInstance3D"/>
/// (+ каркас), построенный <c>ShapeMeshBuilder</c> прямо по его размеру/повороту/цвету. <see cref="Sync"/>
/// вызывается на каждое изменение постройки (<see cref="Construction.Changed"/>) и полностью пересобирает узлы:
/// удобно и достаточно быстро для ожидаемого числа таких блоков в постройке (не тысячи, в отличие от кубов).
/// </summary>
public partial class ShapeInstanceView : Node3D
{
    private sealed class View
    {
        public MeshInstance3D Solid = null!;
        public MeshInstance3D Wire = null!;
    }

    private readonly Dictionary<int, View> _views = new();
    private StandardMaterial3D _solidMaterial = null!;
    private StandardMaterial3D _wireMaterial = null!;
    private WireMode _wireMode = WireMode.Off;

    public WireMode WireMode
    {
        get => _wireMode;
        set
        {
            _wireMode = value;
            ApplyWireMode();
        }
    }

    public override void _Ready()
    {
        _solidMaterial = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            Roughness = 0.8f,
            Metallic = 0.0f,
        };
        _wireMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.55f, 0.88f, 1.0f),
        };
        ApplyWireMode();
    }

    public void Sync(Construction construction)
    {
        var alive = new HashSet<int>();

        foreach (var instance in construction.Instances)
        {
            if (!BlockCatalog.Instance.TryGetBySlug(instance.BlockSlug, out var definition)) continue;
            var building = definition.GetComponent<BuildingBlockComponent>();
            if (building == null || building.Shape == BlockShape.Cube) continue; // кубы рисует ChunkMesher

            alive.Add(instance.InstanceId);
            if (!_views.TryGetValue(instance.InstanceId, out var view))
            {
                view = CreateView(instance.InstanceId);
                _views[instance.InstanceId] = view;
            }

            var (solid, wire) = ShapeMeshBuilder.Build(building.Shape, instance.Size, instance.RotationSteps, CellColor.Unpack(instance.Color));
            var origin = BuildSpace.CellMin(instance.Origin);
            view.Solid.Mesh = solid;
            view.Solid.Position = origin;
            view.Wire.Mesh = wire;
            view.Wire.Position = origin;
        }

        foreach (var id in new List<int>(_views.Keys))
        {
            if (alive.Contains(id)) continue;
            _views[id].Solid.QueueFree();
            _views[id].Wire.QueueFree();
            _views.Remove(id);
        }
    }

    private View CreateView(int instanceId)
    {
        var view = new View
        {
            Solid = new MeshInstance3D { Name = $"Shape_{instanceId}", MaterialOverride = _solidMaterial },
            Wire = new MeshInstance3D
            {
                Name = $"ShapeWire_{instanceId}",
                MaterialOverride = _wireMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            },
        };
        view.Solid.Visible = _wireMode != WireMode.WireOnly;
        view.Wire.Visible = _wireMode != WireMode.Off;
        AddChild(view.Solid);
        AddChild(view.Wire);
        return view;
    }

    private void ApplyWireMode()
    {
        if (_wireMaterial == null) return;

        foreach (var view in _views.Values)
        {
            view.Solid.Visible = _wireMode != WireMode.WireOnly;
            view.Wire.Visible = _wireMode != WireMode.Off;
        }
    }
}
