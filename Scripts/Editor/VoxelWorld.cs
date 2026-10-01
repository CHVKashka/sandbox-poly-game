using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Editor;

/// <summary>
/// Сцена-представление постройки: держит воксельную сетку и по одному «сплошному» (Solid), «каркасному» (Wire,
/// инструмент Wireframe) и «граничному» (Border, инструмент Borders — чёрные границы отдельных блоков) MeshInstance3D
/// на каждый непустой чанк. Изменённые чанки пересобираются раз в кадр (несколько правок за кадр склеиваются).
/// </summary>
public partial class VoxelWorld : Node3D
{
    // Светлый голубой хорошо виден и на тёмных, и на большинстве светлых блоков.
    private static readonly Color WireframeColor = new(0.55f, 0.88f, 1.0f);

    private sealed class ChunkView
    {
        public MeshInstance3D Solid = null!;
        public MeshInstance3D Wire = null!;
        public MeshInstance3D Border = null!;
        public int Quads;
        public int Faces;
        public int Segments;
    }

    private readonly Dictionary<Vector3I, ChunkView> _views = new();
    private readonly HashSet<Vector3I> _dirty = new();
    private readonly List<Vector3I> _scratch = new();

    private StandardMaterial3D _solidMaterial = null!;
    private StandardMaterial3D _wireMaterial = null!;
    private StandardMaterial3D _borderMaterial = null!;
    private bool _wireframe;
    private bool _borders = true;
    private ShapeInstanceView _shapes = null!;
    private FunctionalBlockView _functionalBlocks = null!;

    public VoxelGrid Grid { get; } = new();

    /// <summary>Слой размещённых блоков (позиция/размер/тип) поверх <see cref="Grid"/> — см. <see cref="Construction"/>.</summary>
    public Construction Construction { get; }

    public VoxelWorld() => Construction = new Construction(Grid);

    public int ChunkCount => _views.Count;
    public int Quads { get; private set; }
    public int FacesBeforeMerge { get; private set; }
    public int LineSegments { get; private set; }

    /// <summary>Инструмент Wireframe: только полигоны и их диагонали (текущим цветом), сплошные грани скрыты.</summary>
    public bool WireframeOn
    {
        get => _wireframe;
        set
        {
            _wireframe = value;
            ApplyVisibility();
            _shapes.Wireframe = value;
        }
    }

    /// <summary>Инструмент Borders: чёрные границы отдельных блоков/клеток, независимо от Wireframe и сплошных граней.</summary>
    public bool BordersOn
    {
        get => _borders;
        set
        {
            _borders = value;
            ApplyVisibility();
            _shapes.Borders = value;
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
        // RenderPriority выше, чем у _solidMaterial (0 по умолчанию) — при координатах, буквально совпадающих
        // с гранью (см. класс-док ChunkMesher), это надёжно решает, кто выигрывает z-fighting, без смещения
        // линий по нормали (которое на стыке разных нормалей визуально "расходится").
        _wireMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = WireframeColor,
            RenderPriority = 1,
        };
        _borderMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = Colors.Black,
            RenderPriority = 1,
        };

        _shapes = new ShapeInstanceView { Name = "Shapes" };
        AddChild(_shapes);
        Construction.Changed += () => _shapes.Sync(Construction);

        _functionalBlocks = new FunctionalBlockView { Name = "FunctionalBlocks" };
        AddChild(_functionalBlocks);
        Construction.Changed += () => _functionalBlocks.Sync(Construction);

        Grid.CellChanged += OnCellChanged;
        ApplyVisibility();
    }

    public override void _Process(double delta) => RebuildDirty();

    private void OnCellChanged(Vector3I cell)
    {
        VoxelGrid.GetAffectedChunks(cell, _scratch);
        foreach (var chunk in _scratch) _dirty.Add(chunk);
    }

    /// <summary>Немедленно пересобирает все изменённые чанки (для тестов и загрузки больших построек).</summary>
    public void RebuildDirty()
    {
        if (_dirty.Count == 0) return;

        foreach (var coord in _dirty) Rebuild(coord);
        _dirty.Clear();
    }

    private void Rebuild(Vector3I coord)
    {
        var data = ChunkMesher.Build(Grid, coord);
        _views.TryGetValue(coord, out var view);

        if (view != null)
        {
            Quads -= view.Quads;
            FacesBeforeMerge -= view.Faces;
            LineSegments -= view.Segments;
        }

        if (data.IsEmpty)
        {
            if (view == null) return;
            view.Solid.QueueFree();
            view.Wire.QueueFree();
            view.Border.QueueFree();
            _views.Remove(coord);
            return;
        }

        if (view == null)
        {
            view = CreateView(coord);
            _views[coord] = view;
        }

        view.Solid.Mesh = data.CreateSolidMesh();
        view.Wire.Mesh = data.CreateWireMesh();
        view.Border.Mesh = data.CreateBorderMesh();
        view.Quads = data.Quads;
        view.Faces = data.VisibleFaces;
        view.Segments = data.LineSegments;
        Quads += view.Quads;
        FacesBeforeMerge += view.Faces;
        LineSegments += view.Segments;
    }

    private ChunkView CreateView(Vector3I coord)
    {
        var origin = BuildSpace.ChunkOrigin(coord);
        var view = new ChunkView
        {
            Solid = new MeshInstance3D
            {
                Name = $"Solid_{coord.X}_{coord.Y}_{coord.Z}",
                MaterialOverride = _solidMaterial,
                Position = origin,
            },
            Wire = new MeshInstance3D
            {
                Name = $"Wire_{coord.X}_{coord.Y}_{coord.Z}",
                MaterialOverride = _wireMaterial,
                Position = origin,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            },
            Border = new MeshInstance3D
            {
                Name = $"Border_{coord.X}_{coord.Y}_{coord.Z}",
                MaterialOverride = _borderMaterial,
                Position = origin,
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
