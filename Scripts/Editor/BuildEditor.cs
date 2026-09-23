using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Dev;
using SandboxPolyGame.Editor.Ui;

namespace SandboxPolyGame.Editor;

/// <summary>
/// Корневой узел редактора построек. Всё создаётся кодом: окружение, сетка, мир блоков, камера, курсор, интерфейс.
///
/// Управление: WASD/Q/E/Shift — камера; зажатая СКМ — поворот; ЛКМ — поставить блок из выбранного слота хотбара;
/// ПКМ — применить инструмент тулбара (покраска/удаление/растягивание); 1–9 и колесо — слот хотбара; Tab — список блоков;
/// J/K/I — повернуть блок, который встанет следующим, вокруг X/Y/Z.
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
    private bool _toolButtonDown;
    private RayHit _hover = RayHit.None;
    private Vector3I? _lastToolCell;
    private Vector2 _lastToolMouse;
    private double _infoTimer;

    // Кэш последней собранной формы призрака — чтобы не пересобирать меш каждый кадр без нужды.
    private string? _ghostSlug;
    private Vector3I _ghostRotation;

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

            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false }:
                _toolButtonDown = false;
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

            case Key.Escape when _ui.ResizeDialogOpen:
                _ui.CloseResizeDialog();
                GetViewport().SetInputAsHandled();
                break;

            case >= Key.Key1 and <= Key.Key9:
                _state.SelectedSlot = (int)(key.PhysicalKeycode - Key.Key1);
                break;

            // Вращение блока, который встанет следующим (см. EditorState.PendingRotationSteps): J — вокруг X,
            // K — вокруг Y, I — вокруг Z, на 90° за нажатие.
            case Key.J:
                _state.RotatePendingX();
                break;

            case Key.K:
                _state.RotatePendingY();
                break;

            case Key.I:
                _state.RotatePendingZ();
                break;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventMouseButton button || _ui.PickerOpen || _ui.ResizeDialogOpen) return;

        switch (button.ButtonIndex)
        {
            case MouseButton.Middle when button.Pressed:
                StartLook();
                break;

            case MouseButton.Left when button.Pressed:
                PlaceAtHover();
                break;

            case MouseButton.Right when button.Pressed:
                _toolButtonDown = true;
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
        _toolButtonDown = false;
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

        string slug = _state.SelectedBlockSlug;
        var cell = _hover.PlaceCell;
        if (string.IsNullOrEmpty(slug) || !BlockCatalog.Instance.TryGetBySlug(slug, out var definition)) return;
        if (!BuildSpace.InBounds(cell) || _world.Grid.IsSolid(cell)) return;

        _world.Construction.Place(cell, definition, definition.DefaultColor, _state.PendingRotationSteps);
        UpdateHover();
    }

    /// <summary>
    /// Paint/Delete действуют на весь экземпляр блока, которому принадлежит клетка под курсором (если клетка не
    /// принадлежит ни одному экземпляру — например, залита инструментом разработчика в обход Construction —
    /// откатываются на поклеточную операцию, как раньше). Resize открывает диалог с размерами блока
    /// (см. <see cref="Ui.ResizeDialogUi"/>) вместо немедленного действия.
    /// </summary>
    private void UseToolAtHover()
    {
        if (_state.Tool == ToolMode.None || !_hover.IsBlock) return;

        var cell = _hover.BlockCell;
        _lastToolCell = cell;
        _lastToolMouse = _mousePosition;
        var instance = _world.Construction.GetOwner(cell);

        switch (_state.Tool)
        {
            case ToolMode.Paint:
                if (instance != null) _world.Construction.Paint(instance, _state.PaintColor);
                else _world.Grid.TryPaint(cell, CellColor.Pack(_state.PaintColor));
                break;

            case ToolMode.Delete:
                if (instance != null) _world.Construction.Remove(instance);
                else _world.Grid.TryRemove(cell);
                break;

            case ToolMode.Resize:
                if (instance != null && BlockCatalog.Instance.TryGetBySlug(instance.BlockSlug, out var definition))
                {
                    _ui.OpenResizeDialog(_world.Construction, instance, definition);
                }

                break;
        }

        UpdateHover();
    }

    // ------------------------------------------------------------------ кадр

    public override void _Process(double delta)
    {
        _camera.MovementEnabled = !_ui.PickerOpen && !_ui.ResizeDialogOpen;
        UpdateHover();

        // Удержание ПКМ: инструмент применяется к каждому новому блоку под курсором, но только если мышь сдвинулась —
        // иначе после удаления цель сразу «перескакивает» на следующий блок и удаление проедает постройку насквозь.
        // Resize сам по себе не повторяется (открытие диалога — разовое действие на нажатие).
        if (_toolButtonDown && _hover.IsBlock && _state.Tool != ToolMode.None && _state.Tool != ToolMode.Resize
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
        string slug = _state.SelectedBlockSlug;
        BlockCatalog.Instance.TryGetBySlug(slug, out var definition);
        bool canPlace = _hover.Found && definition != null
                        && BuildSpace.InBounds(_hover.PlaceCell)
                        && !_world.Grid.IsSolid(_hover.PlaceCell);
        _ghost.Visible = canPlace;
        if (canPlace)
        {
            UpdateGhostMesh(definition!, slug);
            var color = definition!.DefaultColor;
            _ghostMaterial.AlbedoColor = new Color(color.R, color.G, color.B, 0.55f);
        }

        bool showOutline = _hover.IsBlock && _state.Tool != ToolMode.None;
        _outline.Visible = showOutline;
        if (showOutline)
        {
            _outline.Position = BuildSpace.CellCenter(_hover.BlockCell);
            _outlineMaterial.AlbedoColor = _state.Tool switch
            {
                ToolMode.Delete => new Color(1f, 0.25f, 0.2f),
                ToolMode.Resize => new Color(0.3f, 0.9f, 0.45f),
                _ => _state.PaintColor,
            };
        }
    }

    /// <summary>
    /// Призрак отражает настоящую форму выбранного блока (не только куб) и текущий <see cref="EditorState.PendingRotationSteps"/>.
    /// Меш кубов — центрированный стандартный <see cref="BoxMesh"/> (позиция — центр клетки); меш остальных форм
    /// строится <c>ShapeMeshBuilder</c> тем же способом, что и уже поставленные блоки (координаты — от угла клетки,
    /// см. <see cref="Ui.ResizeDialogUi"/> и <see cref="ShapeInstanceView"/>), только всегда размером 1×1×1 —
    /// установка всегда создаёт блок такого размера, растягивается он уже потом инструментом Resize.
    /// </summary>
    private void UpdateGhostMesh(BlockDefinition definition, string slug)
    {
        var building = definition.GetComponent<BuildingBlockComponent>();
        var rotation = _state.PendingRotationSteps;

        if (building == null || building.Shape == BlockShape.Cube)
        {
            _ghost.Position = BuildSpace.CellCenter(_hover.PlaceCell);
            if (_ghostSlug == slug && _ghostRotation == rotation) return;
            _ghost.Mesh = new BoxMesh { Size = Vector3.One * (BuildSpace.CellSize * 0.98f) };
        }
        else
        {
            _ghost.Position = BuildSpace.CellMin(_hover.PlaceCell);
            if (_ghostSlug == slug && _ghostRotation == rotation) return;
            var (solid, _) = ShapeMeshBuilder.Build(building.Shape, Vector3I.One, rotation, Colors.White);
            _ghost.Mesh = (Mesh?)solid ?? new BoxMesh { Size = Vector3.One * (BuildSpace.CellSize * 0.98f) };
        }

        _ghostSlug = slug;
        _ghostRotation = rotation;
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
            "WASD move | Q/E down/up | Shift fast | hold MMB - look | LMB place | RMB tool | 1-9 / wheel hotbar | Tab blocks\n" +
            "J/K/I rotate the next placed block around X/Y/Z | Resize tool: RMB on a block opens a size dialog (X/Y/Z, +/-)\n" +
            $"Blocks: {_world.Grid.BlockCount}   Quads: {_world.Quads} (faces before merge: {_world.FacesBeforeMerge})   " +
            $"Wire segments: {_world.LineSegments}   Chunks: {_world.ChunkCount}   Cursor: {cursor}   FPS: {Engine.GetFramesPerSecond()}");
    }
}
