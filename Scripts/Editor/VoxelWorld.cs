using System.Collections.Generic;
using Godot;
using SwV2.Core;

namespace SwV2.Editor;

/// <summary>
/// Сцена-представление постройки: держит воксельную сетку и по одному «сплошному» и «каркасному» MeshInstance3D
/// на каждый непустой чанк. Изменённые чанки пересобираются раз в кадр (несколько правок за кадр склеиваются).
/// </summary>
public partial class VoxelWorld : Node3D
{
    // Светлый голубой хорошо виден и на тёмных, и на большинстве светлых блоков.
    private static readonly Color OverlayWireColor = new(0.55f, 0.88f, 1.0f);
    private static readonly Color WireOnlyColor = new(0.55f, 0.88f, 1.0f);

    private sealed class ChunkView
    {
        public MeshInstance3D Solid = null!;
        public MeshInstance3D Wire = null!;
        public int Quads;
        public int Faces;
        public int Segments;
    }

    private readonly Dictionary<Vector3I, ChunkView> _views = new();
    private readonly HashSet<Vector3I> _dirty = new();
    private readonly List<Vector3I> _scratch = new();

    private StandardMaterial3D _solidMaterial = null!;
    private StandardMaterial3D _wireMaterial = null!;
    private WireMode _wireMode = WireMode.Off;

    public VoxelGrid Grid { get; } = new();

    public int ChunkCount => _views.Count;
    public int Quads { get; private set; }
    public int FacesBeforeMerge { get; private set; }
    public int LineSegments { get; private set; }

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
            AlbedoColor = OverlayWireColor,
        };

        Grid.CellChanged += OnCellChanged;
        ApplyWireMode();
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

        _wireMaterial.AlbedoColor = _wireMode == WireMode.WireOnly ? WireOnlyColor : OverlayWireColor;
        foreach (var view in _views.Values)
        {
            view.Solid.Visible = _wireMode != WireMode.WireOnly;
            view.Wire.Visible = _wireMode != WireMode.Off;
        }
    }
}
