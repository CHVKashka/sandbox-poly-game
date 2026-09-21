using System.Collections.Generic;
using Godot;
using SwV2.Core;
using SwV2.Dev;
using SwV2.Editor.Ui;

namespace SwV2.Editor;

/// <summary>
/// Корневой узел редактора построек. Всё создаётся кодом: окружение, сетка, мир блоков, камера, курсор, интерфейс.
///
/// Управление: WASD/Q/E/Shift — камера; зажатая СКМ — поворот; ПКМ — поставить блок из выбранного слота хотбара;
/// ЛКМ — применить инструмент тулбара (покраска/удаление); 1–9 и колесо — слот хотбара; Tab — список блоков.
/// </summary>
public partial class BuildEditor : Node3D
{
    private const double MaxRayMeters = 300.0;

    private readonly EditorState _state = new();
    private VoxelWorld _world = null!;
    private FlyCamera _camera = null!;
    private EditorUi _ui = null!;

    private MeshInstance3D _ghost = null!;
    private MeshInstance3D _outline = null!;
    private StandardMaterial3D _ghostMaterial = null!;
    private StandardMaterial3D _outlineMaterial = null!;

    private Vector2 _mousePosition;
    private bool _looking;
    private bool _leftDown;
    private RayHit _hover = RayHit.None;
    private Vector3I? _lastToolCell;
    private Vector2 _lastToolMouse;
    private double _infoTimer;

    public EditorState State => _state;
    public VoxelWorld World => _world;
    public FlyCamera EditorCamera => _camera;
    public EditorUi Ui => _ui;
    public RayHit Hover => _hover;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildGroundGrid();

        _world = new VoxelWorld { Name = "World" };
        AddChild(_world);

        _camera = new FlyCamera { Name = "Camera", Fov = 70f, Near = 0.05f, Far = 600f };
        AddChild(_camera);
        _camera.Current = true;
        _camera.LookAtPoint(new Vector3(3.0, 2.5, 5.0), new Vector3(0.0, 0.3, 0.0));

        BuildCursorVisuals();

        _ui = new EditorUi(this, _state);
        _state.Changed += OnStateChanged;
        OnStateChanged();

        _mousePosition = GetViewport().GetMousePosition();
        DevHarness.Start(this);
    }

    // ------------------------------------------------------------------ сцена

    private void BuildEnvironment()
    {
        var sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = Color.FromHtml("#3b6fa8"),
                SkyHorizonColor = Color.FromHtml("#a9c2d8"),
                GroundHorizonColor = Color.FromHtml("#8a949c"),
                GroundBottomColor = Color.FromHtml("#3c4148"),
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
            LightEnergy = 1.1f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 40f,
        });
    }

    private void BuildGroundGrid()
    {
        var extent = -BuildSpace.MinCell.X * BuildSpace.CellSize; // 32 м
        AddChild(new MeshInstance3D
        {
            Name = "GroundGrid",
            Mesh = new PlaneMesh { Size = new Vector2(extent * 2f + 0.5f, extent * 2f + 0.5f) },
            MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/build_grid.gdshader") },
            Position = new Vector3(0, -0.002f, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    private void BuildCursorVisuals()
    {
        _ghostMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = new Color(1f, 1f, 1f, 0.5f),
        };
        _ghost = new MeshInstance3D
        {
            Name = "PlaceGhost",
            Mesh = new BoxMesh { Size = Vector3.One * (BuildSpace.CellSize * 0.98f) },
            MaterialOverride = _ghostMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_ghost);

        _outlineMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = Colors.White,
        };
        _outline = new MeshInstance3D
        {
            Name = "TargetOutline",
            Mesh = CreateWireCube(),
            MaterialOverride = _outlineMaterial,
            Scale = Vector3.One * (BuildSpace.CellSize * 1.04f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_outline);
    }

    private static ArrayMesh CreateWireCube()
    {
        var lines = new List<Vector3>();
        for (int axis = 0; axis < 3; axis++)
        {
            int u = (axis + 1) % 3;
            int v = (axis + 2) % 3;
            foreach (float su in new[] { -0.5f, 0.5f })
            foreach (float sv in new[] { -0.5f, 0.5f })
            {
                var a = Vector3.Zero;
                var b = Vector3.Zero;
                a[axis] = -0.5f;
                b[axis] = 0.5f;
                a[u] = b[u] = su;
                a[v] = b[v] = sv;
                lines.Add(a);
                lines.Add(b);
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = lines.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return mesh;
    }

    // ------------------------------------------------------------------ ввод

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouse mouse && !_looking) _mousePosition = mouse.Position;

        switch (e)
        {
            case InputEventMouseMotion motion when _looking:
                _camera.Look(motion.Relative);
                GetViewport().SetInputAsHandled();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Middle, Pressed: false } when _looking:
                StopLook();
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                _leftDown = false;
                break;

            case InputEventKey { Pressed: true, Echo: false } key:
                HandleKey(key);
                break;
        }
    }

    private void HandleKey(InputEventKey key)
    {
        switch (key.PhysicalKeycode)
        {
            case Key.Tab:
                _ui.TogglePicker();
                GetViewport().SetInputAsHandled();
                break;

            case Key.Escape when _ui.PickerOpen:
                _ui.ClosePicker();
                GetViewport().SetInputAsHandled();
                break;

            case >= Key.Key1 and <= Key.Key9:
                _state.SelectedSlot = (int)(key.PhysicalKeycode - Key.Key1);
                break;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventMouseButton button || _ui.PickerOpen) return;

        switch (button.ButtonIndex)
        {
            case MouseButton.Middle when button.Pressed:
                StartLook();
                break;

            case MouseButton.Right when button.Pressed:
                PlaceAtHover();
                break;

            case MouseButton.Left when button.Pressed:
                _leftDown = true;
                _lastToolCell = null;
                UseToolAtHover();
                break;

            case MouseButton.WheelUp when button.Pressed:
                _state.SelectedSlot = (_state.SelectedSlot + EditorState.HotbarSize - 1) % EditorState.HotbarSize;
                break;

            case MouseButton.WheelDown when button.Pressed:
                _state.SelectedSlot = (_state.SelectedSlot + 1) % EditorState.HotbarSize;
                break;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut && _looking) StopLook();
    }

    private void StartLook()
    {
        _looking = true;
        _leftDown = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    private void StopLook()
    {
        _looking = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    // ------------------------------------------------------------------ действия

    private void PlaceAtHover()
    {
        if (!_hover.Found) return;

        ushort id = _state.SelectedBlockId;
        var cell = _hover.PlaceCell;
        if (id == BlockRegistry.None || !BuildSpace.InBounds(cell) || _world.Grid.IsSolid(cell)) return;

        _world.Grid.TrySet(cell, id, CellColor.Pack(BlockRegistry.Get(id).DefaultColor));
        UpdateHover();
    }

    private void UseToolAtHover()
    {
        if (_state.Tool == ToolMode.None || !_hover.IsBlock) return;

        var cell = _hover.BlockCell;
        _lastToolCell = cell;
        _lastToolMouse = _mousePosition;
        switch (_state.Tool)
        {
            case ToolMode.Paint:
                _world.Grid.TryPaint(cell, CellColor.Pack(_state.PaintColor));
                break;
            case ToolMode.Delete:
                _world.Grid.TryRemove(cell);
                break;
        }

        UpdateHover();
    }

    // ------------------------------------------------------------------ кадр

    public override void _Process(double delta)
    {
        _camera.MovementEnabled = !_ui.PickerOpen;
        UpdateHover();

        // Удержание ЛКМ: инструмент применяется к каждому новому блоку под курсором, но только если мышь сдвинулась —
        // иначе после удаления цель сразу «перескакивает» на следующий блок и удаление проедает постройку насквозь.
        if (_leftDown && _hover.IsBlock && _state.Tool != ToolMode.None
            && _lastToolCell != _hover.BlockCell && _mousePosition != _lastToolMouse)
        {
            UseToolAtHover();
        }

        _infoTimer -= delta;
        if (_infoTimer <= 0)
        {
            _infoTimer = 0.25;
            UpdateInfo();
        }
    }

    public void UpdateHover()
    {
        _hover = RayHit.None;
        if (!_looking && !_ui.IsPointOverUi(_mousePosition))
        {
            var origin = _camera.ProjectRayOrigin(_mousePosition);
            var direction = _camera.ProjectRayNormal(_mousePosition);
            _hover = VoxelRaycaster.Cast(_world.Grid, origin, direction, MaxRayMeters);
        }

        UpdateCursorVisuals();
    }

    private void UpdateCursorVisuals()
    {
        ushort id = _state.SelectedBlockId;
        bool canPlace = _hover.Found
                        && id != BlockRegistry.None
                        && BuildSpace.InBounds(_hover.PlaceCell)
                        && !_world.Grid.IsSolid(_hover.PlaceCell);
        _ghost.Visible = canPlace;
        if (canPlace)
        {
            _ghost.Position = BuildSpace.CellCenter(_hover.PlaceCell);
            var color = BlockRegistry.Get(id).DefaultColor;
            _ghostMaterial.AlbedoColor = new Color(color.R, color.G, color.B, 0.55f);
        }

        bool showOutline = _hover.IsBlock && _state.Tool != ToolMode.None;
        _outline.Visible = showOutline;
        if (showOutline)
        {
            _outline.Position = BuildSpace.CellCenter(_hover.BlockCell);
            _outlineMaterial.AlbedoColor = _state.Tool == ToolMode.Delete ? new Color(1f, 0.25f, 0.2f) : _state.PaintColor;
        }
    }

    private void OnStateChanged()
    {
        _world.WireMode = _state.Wire;
        UpdateCursorVisuals();
    }

    private void UpdateInfo()
    {
        string cursor = "-";
        if (_hover.IsBlock)
        {
            var b = _hover.BlockCell;
            cursor = $"block ({b.X}, {b.Y}, {b.Z})";
        }
        else if (_hover.Found)
        {
            var p = _hover.PlaceCell;
            cursor = $"ground ({p.X}, {p.Y}, {p.Z})";
        }

        _ui.SetInfo(
            "WASD move | Q/E down/up | Shift fast | hold MMB - look | RMB place | LMB tool | 1-9 / wheel hotbar | Tab blocks\n" +
            $"Blocks: {_world.Grid.BlockCount}   Quads: {_world.Quads} (faces before merge: {_world.FacesBeforeMerge})   " +
            $"Wire segments: {_world.LineSegments}   Chunks: {_world.ChunkCount}   Cursor: {cursor}   FPS: {Engine.GetFramesPerSecond()}");
    }
}
