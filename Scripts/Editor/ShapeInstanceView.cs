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
        public MeshInstance3D Border = null!;
    }

    private readonly Dictionary<int, View> _views = new();
    private StandardMaterial3D _solidMaterial = null!;
    private StandardMaterial3D _wireMaterial = null!;
    private StandardMaterial3D _borderMaterial = null!;
    private bool _wireframe;
    private bool _borders = true;

    private float _opacity = 1f;

    /// <summary>Непрозрачность сплошных граней форм (1 — как обычно): инструменты «Nodes»/«Parameters» делают постройку полупрозрачной, чтобы были видны ноды внутри.
    /// Каркас и границы блоков не затрагиваются.</summary>
    public float Opacity
    {
        get => _opacity;
        set
        {
            _opacity = value;
            ApplyOpacity();
        }
    }

    private void ApplyOpacity()
    {
        if (_solidMaterial == null) return;
        _solidMaterial.Transparency = _opacity < 1f ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled;
        _solidMaterial.AlbedoColor = new Color(1f, 1f, 1f, _opacity);
    }

    /// <summary>Инструмент Wireframe: только полигоны и их диагонали, без сплошных граней.</summary>
    public bool Wireframe
    {
        get => _wireframe;
        set
        {
            _wireframe = value;
            ApplyVisibility();
        }
    }

    /// <summary>Инструмент Borders: чёрные границы отдельных блоков, независимо от Wireframe.</summary>
    public bool Borders
    {
        get => _borders;
        set
        {
            _borders = value;
            ApplyVisibility();
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
        // RenderPriority выше, чем у _solidMaterial — см. VoxelWorld._Ready.
        _wireMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.55f, 0.88f, 1.0f),
            RenderPriority = 1,
        };
        _borderMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = Colors.Black,
            RenderPriority = 1,
        };
        ApplyOpacity();
        ApplyVisibility();
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

            var occludedMask = ShapeMeshBuilder.ComputeOcclusionMask(construction.Grid, instance.Origin, instance.MaxCell);
            var (solid, wire, border) = ShapeMeshBuilder.Build(building.Shape, BlockFootprint.UnrotatedSize(instance.Size, instance.RotationSteps), instance.RotationSteps, instance.Mirror, CellColor.Unpack(instance.Color), occludedMask, regionColors: instance.RegionColors);
            var origin = BuildSpace.CellMin(instance.Origin);
            view.Solid.Mesh = solid;
            view.Solid.Position = origin;
            view.Wire.Mesh = wire;
            view.Wire.Position = origin;
            view.Border.Mesh = border;
            view.Border.Position = origin;
        }

        foreach (var id in new List<int>(_views.Keys))
        {
            if (alive.Contains(id)) continue;
            _views[id].Solid.QueueFree();
            _views[id].Wire.QueueFree();
            _views[id].Border.QueueFree();
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
            Border = new MeshInstance3D
            {
                Name = $"ShapeBorder_{instanceId}",
                MaterialOverride = _borderMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            },
        };
        view.Solid.Visible = !_wireframe;
        view.Wire.Visible = _wireframe;
        view.Border.Visible = _borders;
        AddChild(view.Solid);
        AddChild(view.Wire);
        AddChild(view.Border);
        return view;
    }

    private void ApplyVisibility()
    {
        if (_wireMaterial == null) return;

        foreach (var view in _views.Values)
        {
            view.Solid.Visible = !_wireframe;
            view.Wire.Visible = _wireframe;
            view.Border.Visible = _borders;
        }
    }
}
