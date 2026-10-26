using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;

namespace SandboxPolyGame.Dev;

/// <summary>
/// Редактор функциональных блоков (<c>godot --path . -- --blockeditor[=slug]</c>, см. <see cref="World.GameWorld"/> — своя сцена
/// <c>res://Scenes/BlockEditor.tscn</c>) — отдельный инструмент, не часть <see cref="BuildEditor"/>: готовит ОДИН блок для каталога.
/// <para/>
/// <b>Модель.</b> Масштаб по осям (по умолчанию <see cref="BlockModelLayout.DefaultScale"/> — 2 м в Blender = 1 клетка) задаётся числами
/// и гизмо из трёх стрелок; автоподгонки по bbox нет. При загрузке нижний задний левый угол bbox модели стоит в точке (0,0,0) клетки
/// (0,0,0); вокруг модели рисуется ФИОЛЕТОВАЯ рамка bbox (только в редакторе, в игре её нет) с 27 якорями (8 углов, 12 середин рёбер,
/// 6 центров граней, центр) — выбранный якорь встаёт в клетку (0,0,0) и сдвигает модель (правило — <see cref="BlockModelLayout"/>).
/// <para/>
/// <b>Поворот модели</b> — углы Эйлера в градусах (<see cref="BlockModelLayout.RotationBasis"/>), поля или гизмо Rotate (кольца вокруг осей, шаг 15°,
/// Shift — 1°); рамка bbox и якорь считаются по повёрнутой модели. <b>Гизмо</b> переключаются кнопками Scale/Rotate/Volume или клавишами 1/2/3.
/// <para/>
/// <b>Footprint</b> — клетки, в которые попадает bbox (допуск 1%, минимум 1×1×1, <see cref="BlockModelLayout.ComputeFootprint"/>),
/// закрашиваются полупрозрачным голубым и записываются в XML автоматически; либо ЗАДАН ВРУЧНУЮ (галочка «Manual volume», числа или гизмо Volume — ручки на гранях,
/// шаг в клетку; в XML <c>"footprintManual": true</c>): части модели могут выпирать за занятые клетки, там нет ни места, ни коллизии. Корневая клетка (0,0,0) всегда внутри. <b>Коллизия</b> задаётся клетками (группа = min + размер в
/// клетках), в XML хранятся сами клетки; оранжевым показываются боксы, в которые они сольются при загрузке игры
/// (<see cref="CollisionCells.Merge"/>). Порты и ноды — как раньше (<see cref="ResourcePort"/>, <see cref="LogicNode"/>), индексы клеток
/// — в рамке блока. Горячая загрузка glTF (<see cref="FunctionalBlockGeometry.GetOrLoadScene"/>), кнопка Browse и Undo/Redo
/// (Ctrl+Z/Ctrl+Y) сохранены; изменённый на диске файл модели подхватывается сам (раз в полсекунды) или по кнопке Reload.
/// </summary>
public enum BlockEditorTool
{
    /// <summary>Стрелки масштаба модели по осям.</summary>
    Scale,

    /// <summary>Кольца поворота модели вокруг осей.</summary>
    Rotate,

    /// <summary>Ручки на гранях занимаемого объёма (footprint'а).</summary>
    Volume,
}

public partial class BlockEditor : Node3D
{
    private static readonly Color BoundsColor = new(0.65f, 0.25f, 1.0f);
    private static readonly Color CellsColor = new(0.35f, 0.75f, 1.0f, 0.22f);
    private static readonly Color CellLinesColor = new(0.5f, 0.85f, 1.0f, 0.7f);
    private static readonly Color CollisionColor = new(1.0f, 0.5f, 0.15f, 0.35f);
    private static readonly Color RootCellColor = new(1f, 1f, 1f, 0.9f);
    private static readonly Color AttachCellColor = new(1f, 0.85f, 0.1f, 0.95f);
    private static readonly Color AnchorColor = new(1f, 1f, 1f, 0.85f);
    private static readonly Color AnchorSelectedColor = new(1f, 0.85f, 0.1f);
    private static readonly Color SkyColor = Color.FromHtml("#808080");

    /// <summary>Цвет маркера порта по типу ресурса — только для превью, не часть данных блока.</summary>
    private static readonly Dictionary<ResourceType, Color> PortColors = new()
    {
        [ResourceType.Fluid] = new Color(0.25f, 0.6f, 1.0f),
        [ResourceType.Torque] = new Color(1.0f, 0.5f, 0.15f),
    };

    /// <summary>Цвет маркера логической ноды по типу — только для превью, не часть данных блока.</summary>
    private static readonly Dictionary<NodeType, Color> NodeColors = new()
    {
        [NodeType.Electricity] = new Color(1.0f, 0.85f, 0.2f),
        [NodeType.Boolean] = new Color(1.0f, 0.3f, 0.28f),
        [NodeType.Number] = new Color(0.3f, 0.9f, 0.4f),
    };

    private static readonly Color GridColor = new(1f, 1f, 1f, 0.22f);
    private static readonly Color AxisColorX = new(1f, 0.3f, 0.3f);
    private static readonly Color AxisColorY = new(0.35f, 1f, 0.35f);
    private static readonly Color AxisColorZ = new(0.35f, 0.55f, 1f);
    private static readonly Color[] AxisColors = { AxisColorX, AxisColorY, AxisColorZ };
    private const float GridRadius = 1.5f; // метров от начала координат в каждую сторону

    /// <summary>Длина стрелок гизмо масштаба (метры) и радиус попадания по ним/по якорям на экране (пиксели).</summary>
    public const float GizmoLength = 0.3f;
    public const float GizmoPickPixels = 10f;
    public const float AnchorPickPixels = 14f;

    /// <summary>Радиус колец гизмо поворота (метры) и шаг привязки угла (градусы; с Shift — мелкий шаг).</summary>
    public const float RotateGizmoRadius = 0.28f;
    public const double RotateSnapDegrees = 15;
    public const double RotateFineSnapDegrees = 1;
    private const int RingSegments = 64;

    /// <summary>Больше стольких клеток footprint в превью рисуется одним боксом, без клеточной сетки (защита от огромных моделей).</summary>
    private const int MaxDrawnCells = 4096;

    private FlyCamera _camera = null!;
    private BlockEditorUi _ui = null!;
    private Node3D? _modelNode;
    private PackedScene? _modelScene;
    private Aabb _modelAabb;
    private bool _hasModel;
    private Aabb _bounds = new(Vector3.Zero, new Vector3(BuildSpace.CellSize, BuildSpace.CellSize, BuildSpace.CellSize));
    private Vector3I _footprintMin = Vector3I.Zero;
    private Vector3I _footprintSize = Vector3I.One;

    private MeshInstance3D _boundsWire = null!;
    private MeshInstance3D _cellsFaces = null!;
    private MeshInstance3D _cellsLines = null!;
    private MeshInstance3D _rootCellWire = null!;
    private MeshInstance3D _attachCellWire = null!;
    private Node3D _anchorMarkers = null!;
    private readonly List<(MeshInstance3D Marker, Vector3 Fraction)> _anchors = new();
    private Node3D _gizmo = null!;
    private Node3D _rotationGizmo = null!;
    private Node3D _volumeHandles = null!;
    private readonly MeshInstance3D[] _volumeHandleMeshes = new MeshInstance3D[6];
    private Node3D _collisionVisuals = null!;
    private StandardMaterial3D _collisionMaterial = null!;
    private Node3D _portVisuals = null!;
    private Node3D _decorVisuals = null!;

    private Vector2 _mousePosition;
    private bool _looking;
    private double _reloadPollTimer;

    // Перетаскивание стрелки масштаба.
    private int _dragAxis = -1;
    private Vector3 _dragStartScale;
    private double _dragStartParam;

    // Перетаскивание кольца поворота.
    private int _dragRotateAxis = -1;
    private Basis _dragStartRotation = Basis.Identity;
    private double _dragStartAngle;

    // Перетаскивание ручки грани footprint'а (ось 0..2 и сторона).
    private int _dragFaceAxis = -1;
    private bool _dragFacePositive;
    private Vector3I _dragStartMin, _dragStartSize;
    private double _dragFaceStartParam;

    /// <summary>
    /// Undo/Redo (Ctrl+Z/Ctrl+Y) — снэпшот ВСЕХ редактируемых полей разом (простой инструмент с одним «документом» на экране, не
    /// дерево независимых объектов). <see cref="PushUndoPoint"/> снимает снэпшот ДО изменения: для Add/Remove, выбора якоря и
    /// перетаскивания стрелки — явно, для правки любого поля — по <see cref="BlockEditorUi.EditSessionStarting"/> (фокус ВОШЁЛ в поле:
    /// к событию «значение изменилось» старое значение уже потеряно).
    /// </summary>
    private readonly record struct Snapshot(
        string Slug, string Name, Color Color, float Mass, float Durability, float DamageResistance,
        string ScenePath, Vector3 ModelScale, Vector3 Anchor, string Behavior, float Capacity,
        IReadOnlyList<CellBox> CollisionGroups, IReadOnlyList<ResourcePort> Ports, IReadOnlyList<LogicNode> Nodes, string ParamsJson, string SchemaJson,
        Vector3 Rotation, bool FootprintManual, Vector3I ManualMin, Vector3I ManualSize, Vector3I AttachCell, IReadOnlyList<DecorMesh> Decor);

    private const int MaxUndoDepth = 50;
    private readonly List<Snapshot> _undoStack = new();
    private readonly List<Snapshot> _redoStack = new();

    /// <summary>Для самотестов — сама панель полей/кнопок.</summary>
    public BlockEditorUi Ui => _ui;

    /// <summary>Папка с XML блоков, из/в которую грузит и пишет редактор. По умолчанию — каталог игры; самотесты подставляют временную.</summary>
    public string BlocksDirectory { get; set; } = BlockCatalog.BlocksDirectory;

    /// <summary>bbox модели в рамке блока (метры) — то, что рисует фиолетовая рамка и из чего считается footprint. Без модели — клетка (0,0,0).</summary>
    public Aabb Bounds => _bounds;

    /// <summary>Действующий footprint: минимальная клетка и размер - автоматический (<see cref="BlockModelLayout.ComputeFootprint"/>) или заданный вручную (<see cref="BlockEditorUi.FootprintManual"/>).</summary>
    public Vector3I FootprintMin => _footprintMin;
    public Vector3I FootprintSize => _footprintSize;

    /// <summary>Загружена ли модель (если нет — якоря/стрелки скрыты, footprint = 1×1×1).</summary>
    public bool HasModel => _hasModel;

    public FlyCamera Camera => _camera;

    /// <summary>Клетка размещения (поле «Attach cell»); для самотестов.</summary>
    public Vector3I AttachCell => _ui.AttachCell;

    /// <summary>Положение жёлтого каркаса клетки размещения (минимальный угол каркаса, чуть выступающего за клетку); для самотестов.</summary>
    public Vector3 AttachMarkerPosition => _attachCellWire.Position;

    /// <summary>Лежит ли клетка размещения внутри действующего footprint-а (иначе блок не загрузится в игре и Save отказывает).</summary>
    public bool AttachCellInsideFootprint
    {
        get
        {
            var cell = _ui.AttachCell;
            for (int axis = 0; axis < 3; axis++)
            {
                if (cell[axis] < _footprintMin[axis] || cell[axis] >= _footprintMin[axis] + _footprintSize[axis]) return false;
            }

            return true;
        }
    }

    public override void _Ready()
    {
        BuildEnvironment();

        _camera = new FlyCamera { Current = true };
        AddChild(_camera);
        _camera.LookAtPoint(new Vector3(1.0f, 0.8f, 1.2f), new Vector3(0.25f, 0.2f, 0.25f));

        AddChild(new MeshInstance3D
        {
            Name = "Grid",
            Mesh = BuildGridMesh(GridRadius, BuildSpace.CellSize),
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = GridColor,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        });

        AddChild(new MeshInstance3D
        {
            Name = "Axes",
            Mesh = BuildAxesMesh(GridRadius),
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true },
        });

        _rootCellWire = new MeshInstance3D
        {
            Name = "RootCell",
            Mesh = BuildWireBox(new Vector3(BuildSpace.CellSize, BuildSpace.CellSize, BuildSpace.CellSize)),
            MaterialOverride = UnshadedMaterial(RootCellColor),
        };
        AddChild(_rootCellWire);

        // Клетка размещения (attachCell): жёлтый каркас чуть крупнее корневой, чтобы был виден и поверх белого, когда клетки совпадают.
        _attachCellWire = new MeshInstance3D
        {
            Name = "AttachCell",
            Mesh = BuildWireBox(new Vector3(BuildSpace.CellSize, BuildSpace.CellSize, BuildSpace.CellSize) * 1.04f),
            MaterialOverride = UnshadedMaterial(AttachCellColor),
        };
        AddChild(_attachCellWire);

        _cellsFaces = new MeshInstance3D { Name = "FootprintCells", MaterialOverride = TranslucentMaterial(CellsColor) };
        AddChild(_cellsFaces);
        _cellsLines = new MeshInstance3D { Name = "FootprintCellLines", MaterialOverride = UnshadedMaterial(CellLinesColor, transparent: true) };
        AddChild(_cellsLines);

        _boundsWire = new MeshInstance3D { Name = "ModelBounds", MaterialOverride = UnshadedMaterial(BoundsColor) };
        AddChild(_boundsWire);

        _collisionMaterial = TranslucentMaterial(CollisionColor);
        _collisionVisuals = new Node3D { Name = "Collision" };
        AddChild(_collisionVisuals);

        _portVisuals = new Node3D { Name = "Ports" };
        AddChild(_portVisuals);

        _decorVisuals = new Node3D { Name = "Decor" };
        AddChild(_decorVisuals);

        BuildAnchorMarkers();
        BuildGizmo();
        BuildRotationGizmo();
        BuildVolumeHandles();

        var uiLayer = new CanvasLayer();
        AddChild(uiLayer);
        var root = new Control();
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.MouseFilter = Control.MouseFilterEnum.Ignore;
        uiLayer.AddChild(root);

        _ui = new BlockEditorUi(root);
        _ui.LoadRequested += LoadSlug;
        _ui.NewRequested += () => ResetToDefaults(_ui.Slug);
        _ui.SaveRequested += Save;
        _ui.FieldsChanged += () => RefreshPreview();
        _ui.ReloadSceneRequested += () => RefreshPreview(forceReload: true);
        _ui.AddCollisionGroupRequested += AddCollisionGroup;
        _ui.FillCollisionFromFootprintRequested += FillCollisionFromFootprint;
        _ui.RemoveCollisionGroupRequested += RemoveCollisionGroup;
        _ui.CollisionChanged += RefreshCollisionVisuals;
        _ui.AddPortRequested += AddPort;
        _ui.RemovePortRequested += RemovePort;
        _ui.PortsChanged += RefreshPortVisuals;
        _ui.AddNodeRequested += AddNode;
        _ui.RemoveNodeRequested += RemoveNode;
        _ui.NodesChanged += RefreshPortVisuals;
        _ui.EditSessionStarting += PushUndoPoint;
        _ui.AddDecorRequested += AddDecor;
        _ui.RemoveDecorRequested += RemoveDecor;
        _ui.DecorChanged += RefreshDecorVisuals;
        _ui.ToolChanged += SetTool;
        _ui.FootprintModeToggled += OnFootprintModeToggled;
        _ui.FitFootprintRequested += FitFootprintToModel;

        string? startupSlug = ReadStartupSlug();
        if (startupSlug != null) LoadSlug(startupSlug);
        else ResetToDefaults("");

        RefreshPreview();
    }

    /// <summary><c>--blockeditor=slug</c> - сразу открыть существующий блок, не только пустую форму.</summary>
    private static string? ReadStartupSlug()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--blockeditor=")) return arg["--blockeditor=".Length..];
        }

        return null;
    }

    private static StandardMaterial3D UnshadedMaterial(Color color, bool transparent = false) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = color,
        Transparency = transparent ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
    };

    private static StandardMaterial3D TranslucentMaterial(Color color) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = color,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>
    /// Нейтральный серый фон + белый свет — не переиспользует <see cref="EnvironmentBuilder.BuildFlatSkyAndSun"/> (тот красит небо в
    /// синий для игры/редактора построек): этому инструменту важна точная цветопередача модели при калибровке, а цветное небо давало
    /// бы паразитный оттенок через ambient-свет от неба.
    /// </summary>
    private void BuildEnvironment()
    {
        var sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = SkyColor,
                SkyHorizonColor = SkyColor,
                GroundHorizonColor = SkyColor,
                GroundBottomColor = SkyColor,
            },
        };

        AddChild(new WorldEnvironment
        {
            Name = "Environment",
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = sky,
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            },
        });

        AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-55, -35, 0),
            LightColor = Colors.White,
            LightEnergy = 1.1f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 40f,
        });
    }

    /// <summary>Решётка на плоскости Y=0 с шагом <see cref="BuildSpace.CellSize"/> — ориентир, относительно которого видно реальный размер
    /// модели в клетках.</summary>
    private static ArrayMesh BuildGridMesh(float radius, float step)
    {
        var vertices = new List<Vector3>();
        for (float x = -radius; x <= radius + 0.001f; x += step)
        {
            vertices.Add(new Vector3(x, 0, -radius));
            vertices.Add(new Vector3(x, 0, radius));
        }

        for (float z = -radius; z <= radius + 0.001f; z += step)
        {
            vertices.Add(new Vector3(-radius, 0, z));
            vertices.Add(new Vector3(radius, 0, z));
        }

        return LinesMesh(vertices.ToArray());
    }

    /// <summary>Три цветные линии через начало координат (X — красная, Y — зелёная, Z — синяя) — один <see cref="ArrayMesh"/> с цветом НА ВЕРШИНУ.</summary>
    private static ArrayMesh BuildAxesMesh(float length)
    {
        var vertices = new[]
        {
            new Vector3(-length, 0, 0), new Vector3(length, 0, 0),
            new Vector3(0, -length, 0), new Vector3(0, length, 0),
            new Vector3(0, 0, -length), new Vector3(0, 0, length),
        };
        var colors = new[] { AxisColorX, AxisColorX, AxisColorY, AxisColorY, AxisColorZ, AxisColorZ };

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return mesh;
    }

    private static ArrayMesh LinesMesh(Vector3[] vertices)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;

        var mesh = new ArrayMesh();
        if (vertices.Length > 0) mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return mesh;
    }

    private static ArrayMesh TrianglesMesh(Vector3[] vertices)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;

        var mesh = new ArrayMesh();
        if (vertices.Length > 0) mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>Каркас-коробка (12 рёбер) от (0,0,0) до <paramref name="extent"/>.</summary>
    private static ArrayMesh BuildWireBox(Vector3 extent) => LinesMesh(WireBoxVertices(Vector3.Zero, extent));

    private static Vector3[] WireBoxVertices(Vector3 min, Vector3 extent)
    {
        Vector3 P(int bx, int by, int bz) => min + new Vector3(bx * extent.X, by * extent.Y, bz * extent.Z);
        var corners = new[]
        {
            P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1), // низ
            P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1), // верх
        };
        int[,] edges =
        {
            { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 }, // низ
            { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 }, // верх
            { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }, // вертикали
        };

        var vertices = new Vector3[edges.GetLength(0) * 2];
        for (int i = 0; i < edges.GetLength(0); i++)
        {
            vertices[i * 2] = corners[edges[i, 0]];
            vertices[i * 2 + 1] = corners[edges[i, 1]];
        }

        return vertices;
    }

    /// <summary>Добавляет в список треугольников грани бокса <paramref name="min"/>..<paramref name="max"/>; <paramref name="faceMask"/> —
    /// какие из 6 сторон (биты как у <see cref="BlockFace"/>) рисовать.</summary>
    private static void AppendBoxFaces(List<Vector3> vertices, Vector3 min, Vector3 max, int faceMask = 0b111111)
    {
        Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            vertices.Add(a); vertices.Add(b); vertices.Add(c);
            vertices.Add(a); vertices.Add(c); vertices.Add(d);
        }

        if ((faceMask & (1 << (int)BlockFace.NegX)) != 0) Quad(P(0, 0, 1), P(0, 0, 0), P(0, 1, 0), P(0, 1, 1));
        if ((faceMask & (1 << (int)BlockFace.PosX)) != 0) Quad(P(1, 0, 0), P(1, 0, 1), P(1, 1, 1), P(1, 1, 0));
        if ((faceMask & (1 << (int)BlockFace.NegY)) != 0) Quad(P(0, 0, 1), P(1, 0, 1), P(1, 0, 0), P(0, 0, 0));
        if ((faceMask & (1 << (int)BlockFace.PosY)) != 0) Quad(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1));
        if ((faceMask & (1 << (int)BlockFace.NegZ)) != 0) Quad(P(0, 0, 0), P(1, 0, 0), P(1, 1, 0), P(0, 1, 0));
        if ((faceMask & (1 << (int)BlockFace.PosZ)) != 0) Quad(P(1, 0, 1), P(0, 0, 1), P(0, 1, 1), P(1, 1, 1));
    }

    // ------------------------------------------------------------------ якоря и гизмо масштаба

    /// <summary>27 маркеров якорей (доли {0, 0.5, 1} по каждой оси) — маленькие кубики БЕЗ теста глубины, чтобы были видны сквозь модель.</summary>
    private void BuildAnchorMarkers()
    {
        _anchorMarkers = new Node3D { Name = "Anchors" };
        AddChild(_anchorMarkers);

        float[] fractions = { 0f, 0.5f, 1f };
        foreach (float fx in fractions)
        foreach (float fy in fractions)
        foreach (float fz in fractions)
        {
            var marker = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.014f, 0.014f, 0.014f) },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = AnchorColor,
                    NoDepthTest = true,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                },
            };
            _anchorMarkers.AddChild(marker);
            _anchors.Add((marker, new Vector3(fx, fy, fz)));
        }
    }

    /// <summary>Три стрелки (X красная, Y зелёная, Z синяя) от точки якоря в +оси — гизмо масштаба. Положение задаёт <see cref="UpdateGizmo"/>.</summary>
    private void BuildGizmo()
    {
        _gizmo = new Node3D { Name = "ScaleGizmo" };
        AddChild(_gizmo);

        for (int axis = 0; axis < 3; axis++)
        {
            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = AxisColors[axis],
                NoDepthTest = true,
            };

            // Базис, при котором локальная +Y (ось цилиндра Godot) смотрит вдоль нужной оси.
            var basis = axis switch
            {
                0 => new Basis(Vector3.Back, Mathf.Pi / 2 * -1),  // +Y -> +X
                2 => new Basis(Vector3.Right, Mathf.Pi / 2),      // +Y -> +Z
                _ => Basis.Identity,
            };
            var dir = AxisDirection(axis);

            _gizmo.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.003f, BottomRadius = 0.003f, Height = GizmoLength * 0.85f, RadialSegments = 8 },
                MaterialOverride = material,
                Transform = new Transform3D(basis, dir * (GizmoLength * 0.425f)),
            });
            _gizmo.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.011f, Height = GizmoLength * 0.15f, RadialSegments = 12 },
                MaterialOverride = material,
                Transform = new Transform3D(basis, dir * (GizmoLength * 0.925f)),
            });
        }
    }

    /// <summary>Три кольца (X красное, Y зелёное, Z синее) вокруг точки якоря - гизмо поворота. Положение задаёт <see cref="UpdateAnchorsAndGizmo"/>.</summary>
    private void BuildRotationGizmo()
    {
        _rotationGizmo = new Node3D { Name = "RotateGizmo", Visible = false };
        AddChild(_rotationGizmo);

        for (int axis = 0; axis < 3; axis++)
        {
            var (u, v) = RingBasis(axis);
            var points = new Vector3[RingSegments * 2];
            for (int i = 0; i < RingSegments; i++)
            {
                points[i * 2] = RingPoint(u, v, i);
                points[i * 2 + 1] = RingPoint(u, v, i + 1);
            }

            _rotationGizmo.AddChild(new MeshInstance3D
            {
                Mesh = LinesMesh(points),
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = AxisColors[axis],
                    NoDepthTest = true,
                },
            });
        }
    }

    /// <summary>Орты плоскости кольца вокруг оси <paramref name="axis"/>: (u, v) циклически следующие оси, так что u × v = ось - угол от u к v растёт против часовой (как <c>Basis(ось, угол)</c>).</summary>
    private static (Vector3 U, Vector3 V) RingBasis(int axis) => (AxisDirection((axis + 1) % 3), AxisDirection((axis + 2) % 3));

    private static Vector3 RingPoint(Vector3 u, Vector3 v, int segment)
    {
        double angle = segment * 2 * Math.PI / RingSegments;
        return (u * Math.Cos(angle) + v * Math.Sin(angle)) * RotateGizmoRadius;
    }

    /// <summary>Шесть ручек на гранях footprint'а (по две на ось: −/+), цвет по оси. Положение задаёт <see cref="UpdateVolumeHandles"/>.</summary>
    private void BuildVolumeHandles()
    {
        _volumeHandles = new Node3D { Name = "VolumeHandles", Visible = false };
        AddChild(_volumeHandles);

        for (int handle = 0; handle < 6; handle++)
        {
            var mesh = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.03f, 0.03f, 0.03f) },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = AxisColors[handle / 2],
                    NoDepthTest = true,
                },
            };
            _volumeHandles.AddChild(mesh);
            _volumeHandleMeshes[handle] = mesh;
        }
    }

    /// <summary>Центр грани footprint'а (рамка блока, метры): ось <paramref name="axis"/>, сторона <paramref name="positive"/> (+ или −).</summary>
    public Vector3 VolumeHandlePosition(int axis, bool positive)
    {
        var min = BuildSpace.CellMin(_footprintMin);
        var max = BuildSpace.CellMin(_footprintMin + _footprintSize);
        var position = (min + max) * 0.5f;
        position[axis] = positive ? max[axis] : min[axis];
        return position;
    }

    private void UpdateVolumeHandles()
    {
        for (int handle = 0; handle < 6; handle++) _volumeHandleMeshes[handle].Position = VolumeHandlePosition(handle / 2, (handle & 1) == 1);
    }

    public static Vector3 AxisDirection(int axis) => axis switch
    {
        0 => Vector3.Right,
        1 => Vector3.Up,
        _ => Vector3.Back,
    };

    /// <summary>Обновляет положение маркеров якорей по текущей рамке bbox и подсветку выбранного, стрелок — в точку выбранного якоря.</summary>
    private void UpdateAnchorsAndGizmo()
    {
        var selected = BlockModelLayout.SnapAnchor(_ui.Anchor);
        foreach (var (marker, fraction) in _anchors)
        {
            marker.Position = BlockModelLayout.PointOnBounds(_bounds, fraction);
            bool isSelected = fraction.IsEqualApprox(selected);
            marker.Scale = isSelected ? new Vector3(1.9f, 1.9f, 1.9f) : Vector3.One;
            ((StandardMaterial3D)marker.MaterialOverride).AlbedoColor = isSelected ? AnchorSelectedColor : AnchorColor;
            marker.Visible = _hasModel;
        }

        _gizmo.Visible = _hasModel && Tool == BlockEditorTool.Scale;
        _gizmo.Position = BlockModelLayout.AnchorTarget(selected);
        _rotationGizmo.Visible = _hasModel && Tool == BlockEditorTool.Rotate;
        _rotationGizmo.Position = BlockModelLayout.AnchorTarget(selected);
        _volumeHandles.Visible = Tool == BlockEditorTool.Volume;
        UpdateVolumeHandles();
    }

    /// <summary>Выбранный инструмент гизмо.</summary>
    public BlockEditorTool Tool => _ui.Tool;

    /// <summary>Какие гизмо сейчас видны (для самотестов).</summary>
    public bool ScaleGizmoVisible => _gizmo.Visible;
    public bool RotateGizmoVisible => _rotationGizmo.Visible;
    public bool VolumeHandlesVisible => _volumeHandles.Visible;

    /// <summary>Переключает инструмент гизмо (кнопки панели, клавиши 1/2/3). Идущее перетаскивание не прерывается - переключение возможно только между ними.</summary>
    public void SetTool(BlockEditorTool tool)
    {
        _ui.SetTool(tool);
        UpdateAnchorsAndGizmo();
        _ui.SetStatus($"gizmo: {tool}");
    }

    /// <summary>Точка рамки блока, в которой сидят стрелки (она же — точка выбранного якоря, неподвижная при смене масштаба).</summary>
    public Vector3 GizmoPivot => BlockModelLayout.AnchorTarget(BlockModelLayout.SnapAnchor(_ui.Anchor));

    /// <summary>
    /// Параметр <c>t</c> точки на прямой <c>pivot + axisDir·t</c>, ближайшей к лучу камеры (<paramref name="rayOrigin"/>, <paramref name="rayDir"/>) —
    /// по нему перетаскивание стрелки двигает масштаб. Для луча, параллельного оси, возвращает 0 (масштаб не меняется).
    /// </summary>
    public static double ClosestParamOnAxis(Vector3 pivot, Vector3 axisDir, Vector3 rayOrigin, Vector3 rayDir)
    {
        var w0 = pivot - rayOrigin;
        double b = axisDir.Dot(rayDir);
        double d = axisDir.Dot(w0);
        double e = rayDir.Dot(w0);
        double denominator = 1.0 - b * b;
        if (Math.Abs(denominator) < 1e-9) return 0;
        return (b * e - d) / denominator;
    }

    /// <summary>
    /// Новый масштаб после перетаскивания стрелки на <paramref name="delta"/> метров вдоль оси <paramref name="axis"/>: перетащить на
    /// <see cref="GizmoLength"/> (длину стрелки) = масштаб×2. С <paramref name="uniform"/> (Shift) множитель применяется ко всем осям.
    /// Масштаб никогда не падает ниже 0.001 и округляется до 6 знаков.
    /// </summary>
    public static Vector3 DraggedScale(Vector3 startScale, int axis, double delta, bool uniform)
    {
        double factor = Math.Max(1.0 + delta / GizmoLength, 0.02);
        var result = startScale;
        for (int i = 0; i < 3; i++)
        {
            if (!uniform && i != axis) continue;
            result[i] = Math.Round(Math.Max(startScale[i] * factor, 0.001), 6);
        }

        return result;
    }

    private static double DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        double lengthSquared = ab.LengthSquared();
        if (lengthSquared < 1e-9) return p.DistanceTo(a);
        double t = Math.Clamp((p - a).Dot(ab) / lengthSquared, 0.0, 1.0);
        return p.DistanceTo(a + ab * (float)t);
    }

    /// <summary>Ось стрелки гизмо (0/1/2) под курсором <paramref name="mouse"/> (экранные пиксели) или -1. Выбирается ближайшая в пределах
    /// <see cref="GizmoPickPixels"/>.</summary>
    public int PickGizmoAxis(Vector2 mouse)
    {
        if (!_hasModel) return -1;

        var pivot = GizmoPivot;
        if (_camera.IsPositionBehind(pivot)) return -1;
        var a = _camera.UnprojectPosition(pivot);

        int best = -1;
        double bestDistance = GizmoPickPixels;
        for (int axis = 0; axis < 3; axis++)
        {
            var tip = pivot + AxisDirection(axis) * GizmoLength;
            if (_camera.IsPositionBehind(tip)) continue;
            double distance = DistanceToSegment(mouse, a, _camera.UnprojectPosition(tip));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = axis;
            }
        }

        return best;
    }

    /// <summary>Доли якоря (из 27) под курсором <paramref name="mouse"/> или null — ближайший маркер в пределах <see cref="AnchorPickPixels"/>.</summary>
    public Vector3? PickAnchor(Vector2 mouse)
    {
        if (!_hasModel) return null;

        Vector3? best = null;
        double bestDistance = AnchorPickPixels;
        foreach (var (marker, fraction) in _anchors)
        {
            if (_camera.IsPositionBehind(marker.Position)) continue;
            double distance = _camera.UnprojectPosition(marker.Position).DistanceTo(mouse);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = fraction;
            }
        }

        return best;
    }

    /// <summary>Выбирает якорь (доли bbox из {0, 0.5, 1}): точка bbox встаёт в клетку (0,0,0), модель сдвигается. Одна точка Undo.</summary>
    public void SelectAnchor(Vector3 fraction)
    {
        var snapped = BlockModelLayout.SnapAnchor(fraction);
        if (snapped.IsEqualApprox(_ui.Anchor)) return;

        PushUndoPoint();
        _ui.SetAnchor(snapped);
        RefreshPreview();
        _ui.SetStatus($"anchor set to ({snapped.X}, {snapped.Y}, {snapped.Z})");
    }

    /// <summary>Начало перетаскивания стрелки масштаба <paramref name="axis"/> (ЛКМ по стрелке): одна точка Undo на всё перетаскивание. Публичный - самотест
    /// вызывает без настоящих событий мыши.</summary>
    public void BeginScaleDrag(int axis, Vector2 mouse)
    {
        PushUndoPoint();
        _dragAxis = axis;
        _dragStartScale = _ui.ModelScale;
        _dragStartParam = ClosestParamOnAxis(GizmoPivot, AxisDirection(axis), _camera.ProjectRayOrigin(mouse), _camera.ProjectRayNormal(mouse));
    }

    /// <summary>Движение мыши при перетаскивании: масштаб = стартовый × (1 + сдвиг вдоль оси / длина стрелки), <paramref name="uniform"/> (Shift) - по всем осям.</summary>
    public void UpdateScaleDrag(Vector2 mouse, bool uniform)
    {
        double t = ClosestParamOnAxis(GizmoPivot, AxisDirection(_dragAxis), _camera.ProjectRayOrigin(mouse), _camera.ProjectRayNormal(mouse));
        _ui.SetModelScale(DraggedScale(_dragStartScale, _dragAxis, t - _dragStartParam, uniform));
        RefreshPreview();
    }

    /// <summary>Конец перетаскивания стрелки (отпустили ЛКМ).</summary>
    public void EndScaleDrag() => _dragAxis = -1;

    /// <summary>Идёт ли сейчас перетаскивание стрелки масштаба.</summary>
    public bool IsDraggingScale => _dragAxis >= 0;

    // ------------------------------------------------------------------ гизмо поворота

    /// <summary>Угол (рад) точки, в которую луч камеры попадает в плоскость кольца оси <paramref name="axis"/> через <paramref name="pivot"/>, отсчитанный от первой оси плоскости
    /// (<see cref="RingBasis"/>); null - луч почти параллелен плоскости (угол не определён).</summary>
    public static double? RingAngle(Vector3 pivot, int axis, Vector3 rayOrigin, Vector3 rayDir)
    {
        var normal = AxisDirection(axis);
        double denominator = rayDir.Dot(normal);
        if (Math.Abs(denominator) < 1e-4) return null;

        double t = (pivot - rayOrigin).Dot(normal) / denominator;
        var offset = rayOrigin + rayDir * t - pivot;
        var (u, v) = RingBasis(axis);
        return Math.Atan2(offset.Dot(v), offset.Dot(u));
    }

    /// <summary>Привязка угла поворота к шагу: <see cref="RotateSnapDegrees"/> или (<paramref name="fine"/>) <see cref="RotateFineSnapDegrees"/>; результат в градусах.</summary>
    public static double SnapRotation(double degrees, bool fine)
    {
        double step = fine ? RotateFineSnapDegrees : RotateSnapDegrees;
        return Math.Round(degrees / step) * step;
    }

    /// <summary>Ось кольца гизмо поворота (0/1/2) под курсором <paramref name="mouse"/> (экранные пиксели) или -1: кольцо рассматривается как ломаная, ближайшая в пределах
    /// <see cref="GizmoPickPixels"/>. Работает независимо от выбранного инструмента (проверяет и невидимые кольца) - решает вызывающий.</summary>
    public int PickRotationAxis(Vector2 mouse)
    {
        if (!_hasModel) return -1;

        var pivot = GizmoPivot;
        int best = -1;
        double bestDistance = GizmoPickPixels;
        for (int axis = 0; axis < 3; axis++)
        {
            var (u, v) = RingBasis(axis);
            Vector2? previous = null;
            for (int i = 0; i <= RingSegments; i++)
            {
                var point = pivot + RingPoint(u, v, i);
                if (_camera.IsPositionBehind(point))
                {
                    previous = null;
                    continue;
                }

                var screen = _camera.UnprojectPosition(point);
                if (previous.HasValue)
                {
                    double distance = DistanceToSegment(mouse, previous.Value, screen);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = axis;
                    }
                }

                previous = screen;
            }
        }

        return best;
    }

    /// <summary>Начало перетаскивания кольца оси <paramref name="axis"/>: одна точка Undo на всё перетаскивание. Публичный - самотест вызывает без настоящих событий мыши.</summary>
    public void BeginRotateDrag(int axis, Vector2 mouse)
    {
        PushUndoPoint();
        _dragRotateAxis = axis;
        _dragStartRotation = BlockModelLayout.RotationBasis(_ui.Rotation);
        _dragStartAngle = RingAngle(GizmoPivot, axis, _camera.ProjectRayOrigin(mouse), _camera.ProjectRayNormal(mouse)) ?? 0;
    }

    /// <summary>Движение мыши при перетаскивании кольца: поворот вокруг МИРОВОЙ оси (рамки блока) на угол от точки нажатия, привязанный к шагу (<paramref name="fine"/> - мелкий),
    /// поверх поворота на момент нажатия - пересчёт идёт от него, поэтому ошибки не копятся.</summary>
    public void UpdateRotateDrag(Vector2 mouse, bool fine)
    {
        var angle = RingAngle(GizmoPivot, _dragRotateAxis, _camera.ProjectRayOrigin(mouse), _camera.ProjectRayNormal(mouse));
        if (angle == null) return;

        double delta = Mathf.RadToDeg(WrapAngle(angle.Value - _dragStartAngle));
        double snapped = SnapRotation(delta, fine);
        var rotated = new Basis(AxisDirection(_dragRotateAxis), Mathf.DegToRad(snapped)) * _dragStartRotation;
        _ui.SetRotation(BlockModelLayout.RotationDegrees(rotated));
        RefreshPreview();
    }

    /// <summary>Конец перетаскивания кольца.</summary>
    public void EndRotateDrag() => _dragRotateAxis = -1;

    public bool IsDraggingRotation => _dragRotateAxis >= 0;

    private static double WrapAngle(double radians) => radians - 2 * Math.PI * Math.Floor((radians + Math.PI) / (2 * Math.PI));

    // ------------------------------------------------------------------ гизмо объёма (footprint)

    /// <summary>Ручка грани footprint'а под курсором (ось 0..2 и сторона) или null - ближайшая в пределах <see cref="AnchorPickPixels"/>.</summary>
    public (int Axis, bool Positive)? PickVolumeHandle(Vector2 mouse)
    {
        (int, bool)? best = null;
        double bestDistance = AnchorPickPixels;
        for (int handle = 0; handle < 6; handle++)
        {
            var position = VolumeHandlePosition(handle / 2, (handle & 1) == 1);
            if (_camera.IsPositionBehind(position)) continue;
            double distance = _camera.UnprojectPosition(position).DistanceTo(mouse);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = (handle / 2, (handle & 1) == 1);
            }
        }

        return best;
    }

    /// <summary>Начало перетаскивания грани: footprint становится ручным (с теми же значениями, что были), одна точка Undo на всё перетаскивание.</summary>
    public void BeginVolumeDrag(int axis, bool positive, Vector2 mouse)
    {
        PushUndoPoint();
        _dragFaceAxis = axis;
        _dragFacePositive = positive;
        _dragStartMin = _footprintMin;
        _dragStartSize = _footprintSize;
        _ui.SetFootprint(true, _footprintMin, _footprintSize);
        _dragFaceStartParam = ClosestParamOnAxis(VolumeHandlePosition(axis, positive), AxisDirection(axis), _camera.ProjectRayOrigin(mouse), _camera.ProjectRayNormal(mouse));
    }

    /// <summary>Движение мыши при перетаскивании грани: сдвиг вдоль оси, округлённый до целых клеток, двигает грань (<see cref="BlockModelLayout.MoveFootprintFace"/>).</summary>
    public void UpdateVolumeDrag(Vector2 mouse)
    {
        var start = VolumeFaceAtStart();
        double t = ClosestParamOnAxis(start, AxisDirection(_dragFaceAxis), _camera.ProjectRayOrigin(mouse), _camera.ProjectRayNormal(mouse));
        int cells = (int)Math.Round((t - _dragFaceStartParam) / BuildSpace.CellSize);
        var (min, size) = BlockModelLayout.MoveFootprintFace(_dragStartMin, _dragStartSize, _dragFaceAxis, _dragFacePositive, cells);
        _ui.SetFootprint(true, min, size);
        RefreshPreview();
    }

    /// <summary>Положение перетаскиваемой грани на момент нажатия (прямая, вдоль которой считается сдвиг, неподвижна всё перетаскивание).</summary>
    private Vector3 VolumeFaceAtStart()
    {
        var min = BuildSpace.CellMin(_dragStartMin);
        var max = BuildSpace.CellMin(_dragStartMin + _dragStartSize);
        var position = (min + max) * 0.5f;
        position[_dragFaceAxis] = _dragFacePositive ? max[_dragFaceAxis] : min[_dragFaceAxis];
        return position;
    }

    public void EndVolumeDrag() => _dragFaceAxis = -1;

    public bool IsDraggingVolume => _dragFaceAxis >= 0;

    /// <summary>Галочка «Manual volume» переключена: Undo-точка с ПРЕЖНИМ режимом (снэпшот брать поздно - галочка уже щёлкнула) и новый расчёт.</summary>
    private void OnFootprintModeToggled()
    {
        var before = CaptureSnapshot() with { FootprintManual = !_ui.FootprintManual };
        _undoStack.Add(before);
        if (_undoStack.Count > MaxUndoDepth) _undoStack.RemoveAt(0);
        _redoStack.Clear();
        RefreshPreview();
        _ui.SetStatus(_ui.FootprintManual ? "volume: manual (drag the handles or type the numbers)" : "volume: automatic (from the model bbox)");
    }

    /// <summary>«Fit to model»: ручной footprint := то, что дал бы автоматический расчёт по текущей рамке bbox.</summary>
    public void FitFootprintToModel()
    {
        PushUndoPoint();
        var (min, size) = BlockModelLayout.ComputeFootprint(_bounds);
        _ui.SetFootprint(true, min, size);
        RefreshPreview();
    }

    // ------------------------------------------------------------------ ввод (камера, тот же приём, что и BuildEditor)

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouse mouse && !_looking) _mousePosition = mouse.Position;

        switch (e)
        {
            // Поле ввода оставалось сфокусированным после печати, и последующие WASD для камеры заодно печатались в него: снимаем
            // фокус на ЛЮБОЙ клик мышью ДО того, как GUI успеет обработать событие (_Input вызывается раньше GUI-слоя) - если клик
            // был РЕАЛЬНО по полю/кнопке панели, Godot тут же вернёт фокус туда сам; если в 3D-вид - фокус остаётся снятым.
            case InputEventMouseButton { Pressed: true } anyButton
                when anyButton.ButtonIndex is MouseButton.Left or MouseButton.Middle or MouseButton.Right:
                GetViewport().GuiReleaseFocus();
                break;

            // Undo/Redo не перехватываем, пока фокус на текстовом поле (у LineEdit свой Ctrl+Z на уровне текста).
            case InputEventKey { Pressed: true, Keycode: Key.Z, CtrlPressed: true } when GetViewport().GuiGetFocusOwner() is not LineEdit and not TextEdit:
                Undo();
                break;

            case InputEventKey { Pressed: true, Keycode: Key.Y, CtrlPressed: true } when GetViewport().GuiGetFocusOwner() is not LineEdit and not TextEdit:
                Redo();
                break;
        }

        switch (e)
        {
            case InputEventMouseMotion motion when _looking:
                _camera.Look(motion.Relative);
                GetViewport().SetInputAsHandled();
                break;

            case InputEventMouseMotion motion when _dragAxis >= 0:
                UpdateScaleDrag(motion.Position, Input.IsKeyPressed(Key.Shift));
                GetViewport().SetInputAsHandled();
                break;

            case InputEventMouseMotion motion when _dragRotateAxis >= 0:
                UpdateRotateDrag(motion.Position, Input.IsKeyPressed(Key.Shift));
                GetViewport().SetInputAsHandled();
                break;

            case InputEventMouseMotion motion when _dragFaceAxis >= 0:
                UpdateVolumeDrag(motion.Position);
                GetViewport().SetInputAsHandled();
                break;

            // Клавиши 1/2/3 - инструмент гизмо (не при печати в поле и не с модификаторами).
            case InputEventKey { Pressed: true, Echo: false, CtrlPressed: false, AltPressed: false, Keycode: Key.Key1 or Key.Key2 or Key.Key3 } toolKey
                when GetViewport().GuiGetFocusOwner() is not LineEdit and not TextEdit && !_looking:
                SetTool(toolKey.Keycode == Key.Key1 ? BlockEditorTool.Scale : toolKey.Keycode == Key.Key2 ? BlockEditorTool.Rotate : BlockEditorTool.Volume);
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Middle, Pressed: false } when _looking:
                _looking = false;
                Input.MouseMode = Input.MouseModeEnum.Visible;
                break;

            // СКМ над панелью не должна запускать вращение камеры (панель на неё никак не реагирует).
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle, Pressed: true } when GetViewport().GuiGetHoveredControl() == null:
                _looking = true;
                Input.MouseMode = Input.MouseModeEnum.Captured;
                break;

            // ЛКМ в 3D-виде (не над панелью): сначала гизмо выбранного инструмента (стрелки масштаба / кольца поворота / ручки объёма), затем маркеры якорей.
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press
                when !_looking && GetViewport().GuiGetHoveredControl() == null:
            {
                int axis = Tool == BlockEditorTool.Scale ? PickGizmoAxis(press.Position) : -1;
                if (axis >= 0)
                {
                    BeginScaleDrag(axis, press.Position);
                    GetViewport().SetInputAsHandled();
                    break;
                }

                int ring = Tool == BlockEditorTool.Rotate ? PickRotationAxis(press.Position) : -1;
                if (ring >= 0)
                {
                    BeginRotateDrag(ring, press.Position);
                    GetViewport().SetInputAsHandled();
                    break;
                }

                var handle = Tool == BlockEditorTool.Volume ? PickVolumeHandle(press.Position) : null;
                if (handle.HasValue)
                {
                    BeginVolumeDrag(handle.Value.Axis, handle.Value.Positive, press.Position);
                    GetViewport().SetInputAsHandled();
                    break;
                }

                var anchor = PickAnchor(press.Position);
                if (anchor.HasValue)
                {
                    SelectAnchor(anchor.Value);
                    GetViewport().SetInputAsHandled();
                }

                break;
            }

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _dragAxis >= 0:
                EndScaleDrag();
                GetViewport().SetInputAsHandled();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _dragRotateAxis >= 0:
                EndRotateDrag();
                GetViewport().SetInputAsHandled();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _dragFaceAxis >= 0:
                EndVolumeDrag();
                GetViewport().SetInputAsHandled();
                break;

            // Колесо зумит камеру, только если курсор НЕ над панелью (над ней оно крутит ScrollContainer).
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } when GetViewport().GuiGetHoveredControl() == null:
                _camera.Zoom(1);
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } when GetViewport().GuiGetHoveredControl() == null:
                _camera.Zoom(-1);
                break;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut && _looking)
        {
            _looking = false;
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
    }

    /// <summary>Раз в полсекунды проверяет файл модели (<see cref="FunctionalBlockGeometry.GetOrLoadScene"/> сам сравнивает время
    /// изменения/размер): переэкспортировали из Blender - превью обновляется без кнопки Reload.</summary>
    public override void _Process(double delta)
    {
        _reloadPollTimer += delta;
        if (_reloadPollTimer < 0.5) return;
        _reloadPollTimer = 0;
        PollModelFile();
    }

    /// <summary>Публичный — самотест вызывает без ожидания таймера (и с <paramref name="forceRecheck"/>, без паузы между проверками файла).
    /// true — файл изменился и превью обновлено.</summary>
    public bool PollModelFile(bool forceRecheck = false)
    {
        string path = _ui.ScenePath;
        if (string.IsNullOrEmpty(path)) return false;

        var (scene, _) = FunctionalBlockGeometry.GetOrLoadScene(path, forceRecheck);
        if (ReferenceEquals(scene, _modelScene)) return false;

        RefreshPreview();
        return true;
    }

    // ------------------------------------------------------------------ Undo/Redo (Ctrl+Z/Ctrl+Y, см. Snapshot)

    private Snapshot CaptureSnapshot() => new(
        _ui.Slug, _ui.Name, _ui.Color, _ui.Mass, _ui.Durability, _ui.DamageResistance, _ui.ScenePath,
        _ui.ModelScale, _ui.Anchor, _ui.Behavior, _ui.Capacity, _ui.CollisionGroups, _ui.Ports, _ui.Nodes, _ui.ParamsJson, _ui.ParametersSchemaJson,
        _ui.Rotation, _ui.FootprintManual, _ui.ManualFootprint.Min, _ui.ManualFootprint.Size, _ui.AttachCell, _ui.Decor);

    private void RestoreSnapshot(Snapshot s)
    {
        _ui.LoadFields(s.Slug, s.Name, s.Color, s.Mass, s.Durability, s.DamageResistance, s.ScenePath,
            s.ModelScale, s.Anchor, s.Behavior, s.Capacity, s.CollisionGroups, s.Ports, s.Nodes, s.ParamsJson, s.SchemaJson,
            s.Rotation, s.FootprintManual, s.ManualMin, s.ManualSize, s.AttachCell, s.Decor);
        RefreshPreview();
    }

    /// <summary>Снимает снэпшот ДО изменения. Любое новое действие после Undo стирает «будущее» (Redo) — обычная семантика.</summary>
    private void PushUndoPoint()
    {
        _undoStack.Add(CaptureSnapshot());
        if (_undoStack.Count > MaxUndoDepth) _undoStack.RemoveAt(0);
        _redoStack.Clear();
    }

    /// <summary>Загрузка блока (Load/New) начинает Undo/Redo с чистого листа — переключение документа не трогает чужую историю.</summary>
    private void ClearUndoHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    public void Undo()
    {
        if (_undoStack.Count == 0)
        {
            _ui.SetStatus("nothing to undo");
            return;
        }

        _redoStack.Add(CaptureSnapshot());
        var snapshot = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreSnapshot(snapshot);
        _ui.SetStatus("undone");
    }

    public void Redo()
    {
        if (_redoStack.Count == 0)
        {
            _ui.SetStatus("nothing to redo");
            return;
        }

        _undoStack.Add(CaptureSnapshot());
        var snapshot = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RestoreSnapshot(snapshot);
        _ui.SetStatus("redone");
    }

    // ------------------------------------------------------------------ загрузка/сброс

    public void ResetToDefaults(string slug)
    {
        ClearUndoHistory();
        _ui.LoadFields(slug, "", Colors.White, 10f, 100f, 0.1f, "", BlockModelLayout.DefaultScaleVector, Vector3.Zero, "", 0f,
            Array.Empty<CellBox>(), Array.Empty<ResourcePort>(), Array.Empty<LogicNode>(), "");
        _ui.SetStatus(string.IsNullOrEmpty(slug) ? "new block (not yet saved)" : $"new block '{slug}' (not yet saved)");
        RefreshPreview();
    }

    public void LoadSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            _ui.SetStatus("enter a slug first");
            return;
        }

        string path = $"{BlocksDirectory}/{slug}.xml";
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file == null)
        {
            ResetToDefaults(slug);
            return;
        }

        try
        {
            var root = XDocument.Parse(file.GetAsText()).Root ?? throw new InvalidOperationException("empty XML");
            string name = (string?)root.Attribute("name") ?? slug;
            var color = Color.FromHtml(root.Element("Color")?.Value.Trim() ?? "#ffffff");

            float mass = 10f, durability = 100f, damage = 0.1f;
            FunctionalBlockComponent? functional = null;
            string paramsJson = "";
            string schemaJson = "";
            bool hasBuildingBlock = false;

            foreach (var componentNode in root.Elements("Component"))
            {
                string type = (string?)componentNode.Attribute("type") ?? "";
                string json = componentNode.Value.Trim();
                if (type == BuildingBlockComponent.ComponentType) hasBuildingBlock = true;
                if (type == ParametersComponent.ComponentType && json.Length > 0)
                {
                    new ParametersComponent().LoadFromJson(JsonDocument.Parse(json).RootElement); // схема должна разбираться - иначе блок не загрузится в игре
                    schemaJson = json;
                    continue;
                }

                if (json.Length == 0) continue;
                using var doc = JsonDocument.Parse(json);
                var element = doc.RootElement;

                if (type == BaseComponent.ComponentType)
                {
                    if (element.TryGetProperty("mass", out var m)) mass = m.GetSingle();
                    if (element.TryGetProperty("durability", out var d)) durability = d.GetSingle();
                    if (element.TryGetProperty("damageResistance", out var dr)) damage = dr.GetSingle();
                }
                else if (type == FunctionalBlockComponent.ComponentType)
                {
                    // Тот же разбор, что и у каталога игры (в т.ч. понятные ошибки на ключи старого формата).
                    functional = new FunctionalBlockComponent();
                    functional.LoadFromJson(element);
                    if (element.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object)
                    {
                        paramsJson = JsonSerializer.Serialize(paramsElement);
                    }
                }
            }

            ClearUndoHistory();
            _ui.LoadFields(slug, name, color, mass, durability, damage,
                functional?.ScenePath ?? "", functional?.ModelScale ?? BlockModelLayout.DefaultScaleVector, functional?.Anchor ?? Vector3.Zero,
                functional?.Behavior ?? "", functional?.Capacity ?? 0f,
                functional?.CollisionBoxes ?? Array.Empty<CellBox>(), functional?.Ports ?? Array.Empty<ResourcePort>(),
                functional?.Nodes ?? Array.Empty<LogicNode>(), paramsJson, schemaJson,
                functional?.ModelRotation ?? Vector3.Zero, functional?.FootprintManual ?? false,
                functional?.FootprintMin ?? Vector3I.Zero, functional?.Footprint ?? Vector3I.One, functional?.AttachCell ?? Vector3I.Zero,
                functional?.Decor ?? Array.Empty<DecorMesh>());
            _ui.SetStatus(functional != null
                ? $"loaded '{slug}'"
                : hasBuildingBlock
                    ? $"loaded '{slug}' - this is a rubber block (BuildingBlock), saving would REPLACE it with a functional block"
                    : $"loaded '{slug}' - no FunctionalBlock component yet, saving will add one");
        }
        catch (Exception ex)
        {
            _ui.SetStatus($"failed to parse '{path}': {ex.Message}");
        }

        RefreshPreview();
    }

    // ------------------------------------------------------------------ превью (модель + bbox + footprint + коллизия + порты)

    /// <summary>Перечитывает все поля и перерисовывает превью. <paramref name="forceReload"/> — перепроверить файл модели прямо сейчас (Reload).</summary>
    public void RefreshPreview(bool forceReload = false)
    {
        string scenePath = _ui.ScenePath;
        PackedScene? scene = null;
        Aabb aabb = default;
        if (!string.IsNullOrEmpty(scenePath))
        {
            (scene, aabb) = FunctionalBlockGeometry.GetOrLoadScene(scenePath, forceReload);
            if (scene == null) _ui.SetStatus($"scene not found/failed to load: {scenePath}");
        }

        if (!ReferenceEquals(scene, _modelScene))
        {
            _modelNode?.QueueFree();
            _modelNode = null;
            _modelScene = scene;
            if (scene != null)
            {
                _modelNode = scene.Instantiate<Node3D>();
                AddChild(_modelNode);
            }
        }

        _hasModel = scene != null;
        _modelAabb = aabb;

        var scale = _ui.ModelScale;
        var anchor = BlockModelLayout.SnapAnchor(_ui.Anchor);
        var rotation = BlockModelLayout.RotationBasis(_ui.Rotation);
        if (_hasModel)
        {
            _bounds = BlockModelLayout.BoundsInBlockFrame(aabb, scale, anchor, rotation);
            if (_modelNode != null) _modelNode.Transform = BlockModelLayout.ModelTransform(aabb, scale, anchor, rotation);
        }
        else
        {
            _bounds = new Aabb(Vector3.Zero, new Vector3(BuildSpace.CellSize, BuildSpace.CellSize, BuildSpace.CellSize));
        }

        var automatic = BlockModelLayout.ComputeFootprint(_bounds);
        if (_ui.FootprintManual)
        {
            // Ручной footprint: числа из панели, исправленные до допустимых (размер >= 1, корневая клетка внутри); исправление возвращается в панель.
            var (typedMin, typedSize) = _ui.ManualFootprint;
            (_footprintMin, _footprintSize) = BlockModelLayout.SanitizeFootprint(typedMin, typedSize);
            if (_footprintMin != typedMin || _footprintSize != typedSize) _ui.SetFootprint(true, _footprintMin, _footprintSize);
        }
        else
        {
            (_footprintMin, _footprintSize) = automatic;
            _ui.SetFootprint(false, _footprintMin, _footprintSize);
        }

        _attachCellWire.Position = BuildSpace.CellMin(_ui.AttachCell) - new Vector3(1, 1, 1) * (BuildSpace.CellSize * 0.02f);
        _attachCellWire.Visible = true;

        _boundsWire.Mesh = LinesMesh(WireBoxVertices(_bounds.Position, _bounds.Size));
        _boundsWire.Visible = _hasModel;
        RebuildFootprintCells();
        UpdateAnchorsAndGizmo();
        UpdateModelInfo(scale);

        RefreshCollisionVisuals();
        RefreshPortVisuals();
        RefreshDecorVisuals();
    }

    private void UpdateModelInfo(Vector3 scale)
    {
        var fmin = _footprintMin;
        var fsize = _footprintSize;
        int cells = fsize.X * fsize.Y * fsize.Z;
        string footprint = $"footprint ({(_ui.FootprintManual ? "вручную" : "авто")}): min ({fmin.X}, {fmin.Y}, {fmin.Z}), размер {fsize.X}×{fsize.Y}×{fsize.Z} = {cells} кл.";
        if (!_hasModel)
        {
            _ui.SetModelInfo(footprint + " (модели нет - блок рисуется кубом)");
            return;
        }

        var size = _modelAabb.Size;
        _ui.SetModelInfo(
            $"bbox модели: {size.X:0.###} × {size.Y:0.###} × {size.Z:0.###} ед. → {_bounds.Size.X:0.###} × {_bounds.Size.Y:0.###} × {_bounds.Size.Z:0.###} м " +
            $"(scale {scale.X:0.######}, {scale.Y:0.######}, {scale.Z:0.######})\n{footprint}");
    }

    /// <summary>Клетки footprint'а — полупрозрачные голубые грани (только наружные, чтобы не копились слоями) + линии клеток; клетка (0,0,0) — белый каркас.</summary>
    private void RebuildFootprintCells()
    {
        var min = _footprintMin;
        var size = _footprintSize;
        int count = size.X * size.Y * size.Z;
        var faces = new List<Vector3>();
        var lines = new List<Vector3>();

        if (count > MaxDrawnCells)
        {
            AppendBoxFaces(faces, BuildSpace.CellMin(min), BuildSpace.CellMin(min + size));
            lines.AddRange(WireBoxVertices(BuildSpace.CellMin(min), new Vector3(size.X, size.Y, size.Z) * BuildSpace.CellSize));
        }
        else
        {
            var cellExtent = new Vector3(BuildSpace.CellSize, BuildSpace.CellSize, BuildSpace.CellSize);
            for (int x = 0; x < size.X; x++)
            for (int y = 0; y < size.Y; y++)
            for (int z = 0; z < size.Z; z++)
            {
                var cell = new Vector3I(min.X + x, min.Y + y, min.Z + z);
                var cellMin = BuildSpace.CellMin(cell);
                int mask = 0;
                if (x == 0) mask |= 1 << (int)BlockFace.NegX;
                if (x == size.X - 1) mask |= 1 << (int)BlockFace.PosX;
                if (y == 0) mask |= 1 << (int)BlockFace.NegY;
                if (y == size.Y - 1) mask |= 1 << (int)BlockFace.PosY;
                if (z == 0) mask |= 1 << (int)BlockFace.NegZ;
                if (z == size.Z - 1) mask |= 1 << (int)BlockFace.PosZ;
                if (mask != 0) AppendBoxFaces(faces, cellMin, cellMin + cellExtent, mask);
                lines.AddRange(WireBoxVertices(cellMin, cellExtent));
            }
        }

        _cellsFaces.Mesh = TrianglesMesh(faces.ToArray());
        _cellsLines.Mesh = LinesMesh(lines.ToArray());
    }

    /// <summary>Оранжевые боксы коллизии — ровно те, в которые клетки сольются при загрузке игры (<see cref="CollisionCells.Merge"/>).</summary>
    private void RefreshCollisionVisuals()
    {
        foreach (var child in _collisionVisuals.GetChildren()) child.QueueFree();

        var groups = _ui.CollisionGroups;
        if (TooManyCollisionCells(groups)) return; // слишком много - не разворачиваем (случайное 1000×1000×1000 повесило бы редактор), предупреждение при Save

        var cells = CollisionCells.Expand(groups);
        const float inset = 0.002f;
        var vertices = new List<Vector3>();
        foreach (var box in CollisionCells.Merge(cells))
        {
            var min = box.MinMeters + new Vector3(inset, inset, inset);
            AppendBoxFaces(vertices, min, box.MinMeters + box.SizeMeters - new Vector3(inset, inset, inset));
        }

        _collisionVisuals.AddChild(new MeshInstance3D { Mesh = TrianglesMesh(vertices.ToArray()), MaterialOverride = _collisionMaterial });
    }

    /// <summary>Больше ли клеток в группах, чем <see cref="BlockEditorUi.MaxCollisionCells"/>. Считается по размерам групп БЕЗ разворачивания в клетки
    /// (перекрытия не вычитаются — это верхняя оценка): случайный ввод 1000 в поле размера не должен вешать редактор.</summary>
    private static bool TooManyCollisionCells(IReadOnlyList<CellBox> groups) => groups.Sum(g => g.CellCount) > BlockEditorUi.MaxCollisionCells;

    /// <summary>Сколько указателей сейчас нарисовано в превью (для самотестов).</summary>
    public int DecorVisualCount => _decorVisuals.GetChildCount(includeInternal: false);

    /// <summary>Положение указателя в превью (для самотестов).</summary>
    public Transform3D DecorVisualTransform(int index) => ((Node3D)_decorVisuals.GetChild(index)).Transform;

    /// <summary>Перерисовывает указатели блока: каждый - своя glTF-модель в рамке блока (<see cref="DecorMesh.LocalTransform"/>). Здесь они видны всегда (и Ghost, и Always) - редактор блоков
    /// показывает всё, что блок покажет в редакторе построек. Строка без модели или с ненайденным файлом просто не рисуется (при Save пустой путь - отказ).</summary>
    private void RefreshDecorVisuals()
    {
        foreach (var child in _decorVisuals.GetChildren())
        {
            _decorVisuals.RemoveChild(child); // сразу, а не только QueueFree: иначе до конца кадра старые узлы считались бы вместе с новыми
            child.QueueFree();
        }

        foreach (var decor in _ui.Decor)
        {
            if (decor.Scene.Length == 0) continue;
            var (scene, _) = FunctionalBlockGeometry.GetOrLoadScene(decor.Scene);
            if (scene == null) continue;

            var node = scene.Instantiate<Node3D>();
            node.Transform = decor.LocalTransform;
            _decorVisuals.AddChild(node);
        }
    }

    /// <summary>Новый указатель с умолчаниями (центр корневой клетки, без поворота, масштаб 0.125, Ghost) - дальше правится полями.</summary>
    public void AddDecor()
    {
        PushUndoPoint();
        var decor = new List<DecorMesh>(_ui.Decor) { DecorMesh.Create() };
        _ui.SetDecorRows(decor);
        RefreshDecorVisuals();
    }

    public void RemoveDecor(int index)
    {
        var decor = new List<DecorMesh>(_ui.Decor);
        if (index < 0 || index >= decor.Count) return;
        PushUndoPoint();
        decor.RemoveAt(index);
        _ui.SetDecorRows(decor);
        RefreshDecorVisuals();
    }

    /// <summary>Группа клеток 1×1×1 в клетке (0,0,0) — отправная точка, дальше правится числами.</summary>
    public void AddCollisionGroup()
    {
        PushUndoPoint();
        var groups = new List<CellBox>(_ui.CollisionGroups) { new(Vector3I.Zero, Vector3I.One) };
        _ui.SetCollisionRows(groups);
        RefreshCollisionVisuals();
    }

    /// <summary>Группа клеток ровно по текущему footprint'у — коллизия «весь блок» одним действием (по умолчанию её нет вообще).</summary>
    public void FillCollisionFromFootprint()
    {
        PushUndoPoint();
        var groups = new List<CellBox>(_ui.CollisionGroups) { new(_footprintMin, _footprintSize) };
        _ui.SetCollisionRows(groups);
        RefreshCollisionVisuals();
    }

    public void RemoveCollisionGroup(int index)
    {
        var groups = new List<CellBox>(_ui.CollisionGroups);
        if (index < 0 || index >= groups.Count) return;
        PushUndoPoint();
        groups.RemoveAt(index);
        _ui.SetCollisionRows(groups);
        RefreshCollisionVisuals();
    }

    /// <summary>
    /// Маленький цветной шар (цвет — по <see cref="ResourceType"/>) чуть НАД поверхностью footprint'а (сдвинут по нормали грани на
    /// небольшое расстояние — иначе маркер наполовину тонет в модели) плюс подпись <c>Id (Direction)</c>. Позиция — через
    /// <see cref="FunctionalBlockGeometry.ComputePortAnchor"/> в рамке блока (метры от угла корневой клетки), как и вся остальная сцена.
    /// </summary>
    private void RefreshPortVisuals()
    {
        foreach (var child in _portVisuals.GetChildren()) child.QueueFree();

        const float markerOffset = 0.03f;
        AddNodeMarkers();

        foreach (var port in _ui.Ports)
        {
            var (position, normal) = FunctionalBlockGeometry.ComputePortAnchor(port.Face, port.FaceCell, _footprintMin, _footprintSize, BuildSpace.CellSize);
            var color = PortColors.GetValueOrDefault(port.Resource, Colors.White);

            _portVisuals.AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.035f, Height = 0.07f },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = color,
                    EmissionEnabled = true,
                    Emission = color,
                    EmissionEnergyMultiplier = 0.6f,
                },
                Position = position + normal * markerOffset,
            });

            _portVisuals.AddChild(new Label3D
            {
                Text = $"{port.Id} ({port.Direction})",
                FontSize = 28,
                PixelSize = 0.0022f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                Modulate = color,
                Position = position + normal * (markerOffset + 0.05f),
            });
        }
    }

    /// <summary>
    /// Логические ноды (<see cref="LogicNode"/>) — маленький КУБИК в ЦЕНТРЕ своей клетки плюс подпись <c>Id (Direction, Type)</c>. Цвет — по
    /// <see cref="NodeType"/>. Ноды РАЗНЫХ типов в одной клетке чуть разнесены по X. Конфликт «два одинаковых типа в одной клетке»
    /// показывается в статусе (сохранение при нём отказывает).
    /// </summary>
    private void AddNodeMarkers()
    {
        var nodes = _ui.Nodes;
        string? conflict = FunctionalBlockComponent.FindNodeConflict(nodes);
        if (conflict != null) _ui.SetStatus("nodes: " + conflict);

        foreach (var node in nodes)
        {
            var anchor = FunctionalBlockGeometry.ComputeNodeAnchor(node.Cell, _footprintMin, _footprintSize, BuildSpace.CellSize);
            var color = NodeColors.GetValueOrDefault(node.Type, Colors.White);
            var position = anchor + new Vector3(((int)node.Type - 1) * FunctionalBlockGeometry.NodeSpread, 0, 0); // тот же разнос по типу, что и в инструменте «Nodes»

            _portVisuals.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.04f, 0.04f, 0.04f) },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = color,
                    EmissionEnabled = true,
                    Emission = color,
                    EmissionEnergyMultiplier = 0.6f,
                    NoDepthTest = true, // нода внутри блока - иначе её закрывает сама модель/куб
                },
                Position = position,
            });

            _portVisuals.AddChild(new Label3D
            {
                Text = $"{node.Id} ({node.Direction}, {node.Type})",
                FontSize = 28,
                PixelSize = 0.0022f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                Modulate = color,
                Position = position + new Vector3(0, 0.045f + 0.03f * (int)node.Type, 0),
            });
        }
    }

    /// <summary>Разбирает поле "Behavior params" (JSON-объект; пусто = нет параметров) — компактный JSON без пробелов; false и текст
    /// ошибки, если это не объект.</summary>
    private bool TryParseParams(out string compactJson, out string error)
    {
        compactJson = "";
        error = "";
        string text = _ui.ParamsJson;
        if (text.Length == 0) return true;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "must be a JSON object";
                return false;
            }

            compactJson = JsonSerializer.Serialize(doc.RootElement);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Добавляет ноду с заглушками по умолчанию: тип Boolean в клетке (0,0,0) — а если там уже есть нода этого типа, берёт
    /// первый ещё не занятый тип (чтобы новая нода не создавала конфликт сразу).</summary>
    public void AddNode()
    {
        PushUndoPoint();
        var existing = _ui.Nodes;
        var type = NodeType.Boolean;
        foreach (var candidate in new[] { NodeType.Boolean, NodeType.Number, NodeType.Electricity })
        {
            if (!existing.Any(n => n.Type == candidate && n.Cell == Vector3I.Zero))
            {
                type = candidate;
                break;
            }
        }

        var nodes = new List<LogicNode>(existing)
        {
            new() { Id = $"node_{existing.Count + 1}", Type = type, Direction = PortDirection.In, Cell = Vector3I.Zero },
        };
        _ui.SetNodeRows(nodes);
        RefreshPortVisuals();
    }

    public void RemoveNode(int index)
    {
        var nodes = new List<LogicNode>(_ui.Nodes);
        if (index < 0 || index >= nodes.Count) return;
        PushUndoPoint();
        nodes.RemoveAt(index);
        _ui.SetNodeRows(nodes);
        RefreshPortVisuals();
    }

    public void AddPort()
    {
        PushUndoPoint();
        var ports = new List<ResourcePort>(_ui.Ports)
        {
            new() { Id = $"port_{_ui.Ports.Count + 1}", Resource = ResourceType.Fluid, Direction = PortDirection.In, Face = BlockFace.PosZ, FaceCell = Vector2I.Zero },
        };
        _ui.SetPortRows(ports);
        RefreshPortVisuals();
    }

    public void RemovePort(int index)
    {
        var ports = new List<ResourcePort>(_ui.Ports);
        if (index < 0 || index >= ports.Count) return;
        PushUndoPoint();
        ports.RemoveAt(index);
        _ui.SetPortRows(ports);
        RefreshPortVisuals();
    }

    // ------------------------------------------------------------------ сохранение

    public void Save()
    {
        string slug = SanitizeSlug(_ui.Slug);
        if (string.IsNullOrEmpty(slug))
        {
            _ui.SetStatus("slug can't be empty");
            return;
        }

        // Не пишем файл, который перестал бы грузиться (каталог бросает на ошибке разбора) или в котором footprint нельзя посчитать.
        string? nodeConflict = FunctionalBlockComponent.FindNodeConflict(_ui.Nodes);
        if (nodeConflict != null)
        {
            _ui.SetStatus("not saved - " + nodeConflict);
            return;
        }

        if (_ui.ParametersSchemaJson.Length > 0)
        {
            try { new ParametersComponent().LoadFromJson(JsonDocument.Parse(_ui.ParametersSchemaJson).RootElement); }
            catch (Exception ex)
            {
                _ui.SetStatus("not saved - parameters schema: " + ex.Message);
                return;
            }
        }

        if (!TryParseParams(out _, out string paramsError))
        {
            _ui.SetStatus("not saved - behavior params: " + paramsError);
            return;
        }

        if (!string.IsNullOrEmpty(_ui.ScenePath) && !_hasModel && !_ui.FootprintManual)
        {
            _ui.SetStatus($"not saved - the model '{_ui.ScenePath}' failed to load, so the footprint can't be computed (fix the path or clear the field)");
            return;
        }

        if (!AttachCellInsideFootprint)
        {
            var attach = _ui.AttachCell;
            _ui.SetStatus($"not saved - attach cell ({attach.X}, {attach.Y}, {attach.Z}) lies outside the footprint (min {_footprintMin}, size {_footprintSize}): the game would refuse to load the block");
            return;
        }

        var decorList = _ui.Decor;
        for (int i = 0; i < decorList.Count; i++)
        {
            if (decorList[i].Scene.Length == 0)
            {
                _ui.SetStatus($"not saved - decor #{i} has no scene (set the model path or remove the row)");
                return;
            }
        }

        if (TooManyCollisionCells(_ui.CollisionGroups))
        {
            _ui.SetStatus($"not saved - collision has more than {BlockEditorUi.MaxCollisionCells} cells");
            return;
        }

        string path = $"{BlocksDirectory}/{slug}.xml";
        string xml = BuildXml(slug);

        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file == null)
        {
            _ui.SetStatus($"failed to write {path}: {FileAccess.GetOpenError()}");
            return;
        }

        file.StoreString(xml);
        _ui.SetStatus($"saved {path} ({_footprintSize.X}×{_footprintSize.Y}×{_footprintSize.Z} cells) - restart the game to see it in the catalog");
    }

    /// <summary>Только символы, безопасные для имени файла и для <c>Block id="..."</c> — латиница/цифры/подчёркивание.</summary>
    private static string SanitizeSlug(string slug)
    {
        var builder = new StringBuilder();
        foreach (char c in slug.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_') builder.Append(c);
            else if (c == ' ' || c == '-') builder.Append('_');
        }

        return builder.ToString();
    }

    /// <summary>Текст XML блока по текущему состоянию редактора (публичный — самотесты проверяют формат, не трогая диск).</summary>
    public string BuildXml(string slug)
    {
        string colorHex = "#" + _ui.Color.ToHtml(false);
        var scale = _ui.ModelScale;
        var anchor = BlockModelLayout.SnapAnchor(_ui.Anchor);

        var fields = new List<string>();
        if (!string.IsNullOrEmpty(_ui.ScenePath)) fields.Add($"\"scene\": {Json(_ui.ScenePath)}");
        fields.Add($"\"scale\": [{F(scale.X)}, {F(scale.Y)}, {F(scale.Z)}]");
        fields.Add($"\"anchor\": [{F(anchor.X)}, {F(anchor.Y)}, {F(anchor.Z)}]");
        var rotationDegrees = _ui.Rotation;
        if (rotationDegrees != Vector3.Zero) fields.Add($"\"rotation\": [{F(rotationDegrees.X)}, {F(rotationDegrees.Y)}, {F(rotationDegrees.Z)}]");
        fields.Add($"\"footprint\": [{_footprintSize.X}, {_footprintSize.Y}, {_footprintSize.Z}]");
        fields.Add($"\"footprintMin\": [{_footprintMin.X}, {_footprintMin.Y}, {_footprintMin.Z}]");
        if (_ui.FootprintManual) fields.Add("\"footprintManual\": true");
        var attachCell = _ui.AttachCell;
        if (attachCell != Vector3I.Zero) fields.Add($"\"attachCell\": [{attachCell.X}, {attachCell.Y}, {attachCell.Z}]");
        if (!string.IsNullOrEmpty(_ui.Behavior)) fields.Add($"\"behavior\": {Json(_ui.Behavior)}");
        if (_ui.Capacity > 0) fields.Add($"\"capacity\": {F(_ui.Capacity)}");

        if (TryParseParams(out string paramsCompact, out _) && paramsCompact.Length > 0) fields.Add($"\"params\": {paramsCompact}");

        var collisionCells = CollisionCells.Expand(_ui.CollisionGroups);
        if (collisionCells.Count > 0)
        {
            var rows = new List<string>();
            for (int i = 0; i < collisionCells.Count; i += 8)
            {
                rows.Add(string.Join(", ", collisionCells.Skip(i).Take(8).Select(c => $"[{c.X}, {c.Y}, {c.Z}]")));
            }

            fields.Add("\"collision\": [\n      " + string.Join(",\n      ", rows) + "\n    ]");
        }

        var ports = _ui.Ports;
        string portsJson = ports.Count == 0
            ? "[]"
            : "[\n      " + string.Join(",\n      ", ports.Select(p =>
                $"{{ \"id\": {Json(p.Id)}, \"resource\": \"{p.Resource}\", \"direction\": \"{p.Direction}\", \"face\": \"{p.Face}\", \"position\": [{p.FaceCell.X}, {p.FaceCell.Y}] }}")) + "\n    ]";
        fields.Add($"\"ports\": {portsJson}");

        var decorMeshes = _ui.Decor;
        if (decorMeshes.Count > 0)
        {
            string decorJson = "[\n      " + string.Join(",\n      ", decorMeshes.Select(d =>
                $"{{ \"scene\": {Json(d.Scene)}, \"position\": [{F(d.Position.X)}, {F(d.Position.Y)}, {F(d.Position.Z)}], \"rotation\": [{F(d.Rotation.X)}, {F(d.Rotation.Y)}, {F(d.Rotation.Z)}], " +
                $"\"scale\": [{F(d.Scale.X)}, {F(d.Scale.Y)}, {F(d.Scale.Z)}], \"show\": \"{d.ShowWhen}\" }}")) + "\n    ]";
            fields.Add($"\"decor\": {decorJson}");
        }

        var nodes = _ui.Nodes;
        if (nodes.Count > 0)
        {
            string nodesJson = "[\n      " + string.Join(",\n      ", nodes.Select(n =>
                $"{{ \"id\": {Json(n.Id)}, \"type\": \"{n.Type}\", \"direction\": \"{n.Direction}\", \"position\": [{n.Cell.X}, {n.Cell.Y}, {n.Cell.Z}] }}")) + "\n    ]";
            fields.Add($"\"nodes\": {nodesJson}");
        }

        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        xml.AppendLine($"<Block id=\"{slug}\" name=\"{System.Security.SecurityElement.Escape(_ui.Name)}\">");
        xml.AppendLine($"  <Color>{colorHex}</Color>");
        xml.AppendLine($"  <Component type=\"BaseComponent\">{{ \"mass\": {F(_ui.Mass)}, \"durability\": {F(_ui.Durability)}, \"damageResistance\": {F(_ui.DamageResistance)} }}</Component>");
        if (_ui.ParametersSchemaJson.Length > 0) xml.AppendLine($"  <Component type=\"Parameters\">{XmlText(_ui.ParametersSchemaJson)}</Component>");
        xml.AppendLine("  <Component type=\"FunctionalBlock\">{");
        xml.AppendLine("    " + XmlText(string.Join(",\n    ", fields)));
        xml.AppendLine("  }</Component>");
        xml.AppendLine("</Block>");
        return xml.ToString();
    }

    /// <summary>Текст JSON внутри XML-элемента: <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c> экранируются (подсказка в схеме параметров может содержать «&lt;»); читатель XML вернёт исходный текст.</summary>
    private static string XmlText(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Json(string value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    // double, не float - сборка движка double-precision (real_t = double); float (например, _ui.Mass) неявно расширяется до double.
    private static string F(double value) => Math.Round(value, 6).ToString(CultureInfo.InvariantCulture);
}
