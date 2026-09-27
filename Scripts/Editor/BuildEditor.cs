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
/// Управление: WASD/Q/E/Shift — камера; зажатая СКМ — поворот; ЛКМ — поставить блок из выбранного слота хотбара,
/// пока не активен ни один инструмент (см. <see cref="ButtonFor"/>); Delete — ЛКМ (`X` переключает инструмент,
/// как и кнопка на тулбаре), Paint — ПКМ, красит ровно ту грань клетки, в которую попал луч (см.
/// <see cref="UseToolAtHover"/>); 1–9 и колесо — слот хотбара; Tab — список блоков;
/// J/K/L — повернуть блок, который встанет следующим, вокруг X/Y/Z; U/I/O — отразить его по X/Y/Z; панель Resize
/// на тулбаре — его размер. Все три (поворот/отражение/размер) настраивают ПРИЗРАК, а не уже поставленные блоки —
/// см. <see cref="EditorState"/>. Ctrl+Z/Ctrl+Y — отмена/повтор (см. <see cref="UndoHistory"/>).
/// </summary>
public partial class BuildEditor : Node3D
{
    private const double MaxRayMeters = 300.0;

    private readonly EditorState _state = new();
    private readonly UndoHistory _undo = new();
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
    private MouseButton? _activeToolButton;
    private RayHit _hover = RayHit.None;
    private Vector3I? _lastToolCell;
    private Vector2 _lastToolMouse;
    private double _infoTimer;

    // Снэпшот постройки на момент нажатия ПКМ (см. UndoHistory) - весь "мазок" перетаскивания Paint/Delete
    // фиксируется в истории одним шагом, а не по клетке.
    private UndoHistory.Snapshot? _undoStrokeBefore;

    // Кэш последней собранной формы призрака — чтобы не пересобирать меш каждый кадр без нужды.
    private string? _ghostSlug;
    private Vector3I _ghostRotation;
    private Vector3I _ghostMirror;
    private Vector3I _ghostSize;

    public EditorState State => _state;
    public UndoHistory Undo => _undo;
    public VoxelWorld World => _world;
    public FlyCamera EditorCamera => _camera;
    public EditorUi Ui => _ui;
    public RayHit Hover => _hover;
    public MeshInstance3D Ghost => _ghost;

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
        // Непрозрачный, как настоящий блок (см. UpdateCursorVisuals: цвет берётся из DefaultColor блока, лит так же,
        // как _solidMaterial у VoxelWorld/ShapeInstanceView) - призрак должен выглядеть так, будто блок уже стоит.
        _ghostMaterial = new StandardMaterial3D { Roughness = 0.8f, Metallic = 0f };
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

            case InputEventMouseButton { Pressed: false } up when up.ButtonIndex == _activeToolButton:
                _toolButtonDown = false;
                _activeToolButton = null;
                if (_undoStrokeBefore != null)
                {
                    _undo.RecordIfChanged(_undoStrokeBefore.Value, _world.Construction);
                    _undoStrokeBefore = null;
                }

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

            // Горячая клавиша инструмента Delete — эквивалент клика по кнопке Delete на тулбаре (повторное
            // нажатие выключает, как и клик).
            case Key.X:
                _state.Tool = _state.Tool == ToolMode.Delete ? ToolMode.None : ToolMode.Delete;
                break;

            // Вращение блока, который встанет следующим (см. EditorState.PendingRotationSteps): J — вокруг X,
            // K — вокруг Y, L — вокруг Z, на 90° за нажатие.
            case Key.J:
                _state.RotatePendingX();
                break;

            case Key.K:
                _state.RotatePendingY();
                break;

            case Key.L:
                _state.RotatePendingZ();
                break;

            // Отражение блока, который встанет следующим (см. EditorState.PendingMirror): U — по X, I — по Y,
            // O — по Z. Как и вращение выше, действует на ПРИЗРАК, не на уже поставленные блоки.
            case Key.U:
                _state.ToggleMirrorX();
                break;

            case Key.I:
                _state.ToggleMirrorY();
                break;

            case Key.O:
                _state.ToggleMirrorZ();
                break;

            // Отмена/повтор — как и остальные "глобальные" горячие клавиши тут, не должны перехватывать Ctrl+Z/Y,
            // когда фокус на текстовом поле (иначе отменяли бы постройку вместо правки текста в панели Resize).
            case Key.Z when key.CtrlPressed && GetViewport().GuiGetFocusOwner() is not LineEdit:
                if (_undo.Undo(_world.Construction, BlockCatalog.Instance)) UpdateHover();
                break;

            case Key.Y when key.CtrlPressed && GetViewport().GuiGetFocusOwner() is not LineEdit:
                if (_undo.Redo(_world.Construction, BlockCatalog.Instance)) UpdateHover();
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

            case MouseButton.Left when button.Pressed:
                if (ButtonFor(_state.Tool) == MouseButton.Left) StartToolStroke(MouseButton.Left);
                else if (_state.Tool == ToolMode.None) PlaceAtHover();
                break;

            case MouseButton.Right when button.Pressed:
                if (ButtonFor(_state.Tool) == MouseButton.Right) StartToolStroke(MouseButton.Right);
                break;

            case MouseButton.WheelUp when button.Pressed:
                _state.SelectedSlot = (_state.SelectedSlot + EditorState.HotbarSize - 1) % EditorState.HotbarSize;
                break;

            case MouseButton.WheelDown when button.Pressed:
                _state.SelectedSlot = (_state.SelectedSlot + 1) % EditorState.HotbarSize;
                break;
        }
    }

    /// <summary>Какая кнопка мыши применяет данный инструмент. Delete — ЛКМ (чтобы не путать с установкой,
    /// пока инструмент активен — ЛКМ тогда ничего не ставит, см. <see cref="UpdateCursorVisuals"/>); Paint — ПКМ.
    /// null для None (ЛКМ в этом случае как раз и ставит блок).</summary>
    private static MouseButton? ButtonFor(ToolMode tool) => tool switch
    {
        ToolMode.Delete => MouseButton.Left,
        ToolMode.Paint => MouseButton.Right,
        _ => null,
    };

    private void StartToolStroke(MouseButton button)
    {
        _activeToolButton = button;
        _toolButtonDown = true;
        _lastToolCell = null;
        _undoStrokeBefore = _undo.Capture(_world.Construction);
        UseToolAtHover();
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
        if (string.IsNullOrEmpty(slug) || !BlockCatalog.Instance.TryGetBySlug(slug, out var definition)) return;

        var before = _undo.Capture(_world.Construction);
        _world.Construction.PlaceBlock(_hover.PlaceCell, _state.PendingSize, definition, definition.DefaultColor,
            _state.PendingRotationSteps, _state.PendingMirror);
        _undo.RecordIfChanged(before, _world.Construction);
        UpdateHover();
    }

    /// <summary>
    /// Paint красит РОВНО ту грань клетки под курсором, в которую попал луч (<see cref="RayHit.Normal"/> — уже
    /// известно, какая это сторона, см. <see cref="VoxelRaycaster"/>), не всю клетку и не весь экземпляр — иначе
    /// склейка/культинг в <see cref="ChunkMesher"/> не дали бы покрасить одну поверхность из нескольких сросшихся
    /// впритык блоков без расклейки остальных (см. ROADMAP.md). Delete по-прежнему действует на весь экземпляр
    /// блока, которому принадлежит клетка (если клетка не принадлежит ни одному экземпляру — например, залита
    /// инструментом разработчика в обход Construction — откатывается на поклеточное удаление, как раньше). Resize
    /// сюда не входит — он не действует на уже поставленные блоки, см. <see cref="EditorState.PendingSize"/>.
    /// </summary>
    private void UseToolAtHover()
    {
        if (_state.Tool == ToolMode.None || !_hover.IsBlock) return;

        var cell = _hover.BlockCell;
        _lastToolCell = cell;
        _lastToolMouse = _mousePosition;

        switch (_state.Tool)
        {
            case ToolMode.Paint:
                var normal = _hover.Normal;
                int axis = normal.X != 0 ? 0 : normal.Y != 0 ? 1 : 2;
                bool positive = normal[axis] > 0;
                _world.Grid.TryPaintFace(cell, axis, positive, CellColor.Pack(_state.PaintColor));
                break;

            case ToolMode.Delete:
                var instance = _world.Construction.GetOwner(cell);
                if (instance != null) _world.Construction.Remove(instance);
                else _world.Grid.TryRemove(cell);
                break;
        }

        UpdateHover();
    }

    // ------------------------------------------------------------------ кадр

    public override void _Process(double delta)
    {
        _camera.MovementEnabled = !_ui.PickerOpen;
        UpdateHover();

        // Удержание ПКМ: инструмент применяется к каждому новому блоку под курсором, но только если мышь сдвинулась —
        // иначе после удаления цель сразу «перескакивает» на следующий блок и удаление проедает постройку насквозь.
        if (_toolButtonDown && _hover.IsBlock && _state.Tool != ToolMode.None
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
        // Пока активен инструмент (Paint/Delete), ЛКМ ничего не ставит (см. ButtonFor/_UnhandledInput) - призрак
        // размещения только сбивал бы с толку, поэтому скрыт целиком, а не просто "показывает недоступную клетку".
        bool canPlace = _state.Tool == ToolMode.None && _hover.Found && definition != null
                        && CanPlaceFootprint(_hover.PlaceCell, _state.PendingSize);
        _ghost.Visible = canPlace;
        if (canPlace)
        {
            UpdateGhostMesh(definition!, slug);
            _ghostMaterial.AlbedoColor = definition!.DefaultColor;
        }

        bool showOutline = _hover.IsBlock && _state.Tool != ToolMode.None;
        _outline.Visible = showOutline;
        if (showOutline)
        {
            _outline.Position = BuildSpace.CellCenter(_hover.BlockCell);
            _outlineMaterial.AlbedoColor = _state.Tool == ToolMode.Delete ? new Color(1f, 0.25f, 0.2f) : _state.PaintColor;
        }
    }

    /// <summary>Все ли клетки прямоугольной области <paramref name="size"/> клеток от <paramref name="origin"/>
    /// (растёт только в положительную сторону) свободны и внутри области построек — используется и для видимости
    /// призрака, и как основа проверки в <see cref="Construction.PlaceBlock"/> (её же по факту делает он сам).</summary>
    private bool CanPlaceFootprint(Vector3I origin, Vector3I size)
    {
        var max = origin + size - Vector3I.One;
        for (int z = origin.Z; z <= max.Z; z++)
        for (int y = origin.Y; y <= max.Y; y++)
        for (int x = origin.X; x <= max.X; x++)
        {
            var cell = new Vector3I(x, y, z);
            if (!BuildSpace.InBounds(cell) || _world.Grid.IsSolid(cell)) return false;
        }

        return true;
    }

    /// <summary>
    /// Призрак отражает настоящую форму выбранного блока (не только куб), текущий поворот/отражение
    /// (<see cref="EditorState.PendingRotationSteps"/>/<see cref="EditorState.PendingMirror"/>) и размер
    /// (<see cref="EditorState.PendingSize"/>) — то есть выглядит ровно так же, как блок, который встанет по ЛКМ
    /// (панель Resize на тулбаре меняет именно это, не уже поставленные блоки). Меш кубов — <see cref="BoxMesh"/>
    /// размером во весь Size (без склейки соседних граней, как у настоящих кубов в постройке, но снаружи выглядит
    /// так же — одна сплошная область без швов); меш остальных форм строится той же <c>ShapeMeshBuilder</c>, что и
    /// уже поставленные блоки (координаты — от угла клетки, см. <see cref="ShapeInstanceView"/>).
    /// </summary>
    private void UpdateGhostMesh(BlockDefinition definition, string slug)
    {
        var building = definition.GetComponent<BuildingBlockComponent>();
        var rotation = _state.PendingRotationSteps;
        var mirror = _state.PendingMirror;
        var size = _state.PendingSize;
        bool isCube = building == null || building.Shape == BlockShape.Cube;
        var extent = new Vector3(size.X, size.Y, size.Z) * BuildSpace.CellSize;

        _ghost.Position = BuildSpace.CellMin(_hover.PlaceCell) + (isCube ? extent * 0.5f : Vector3.Zero);

        if (_ghostSlug == slug && _ghostRotation == rotation && _ghostMirror == mirror && _ghostSize == size) return;

        if (isCube)
        {
            _ghost.Mesh = new BoxMesh { Size = extent };
        }
        else
        {
            var (solid, _, _) = ShapeMeshBuilder.Build(building!.Shape, size, rotation, mirror, Colors.White);
            _ghost.Mesh = (Mesh?)solid ?? new BoxMesh { Size = extent };
        }

        _ghostSlug = slug;
        _ghostRotation = rotation;
        _ghostMirror = mirror;
        _ghostSize = size;
    }

    private void OnStateChanged()
    {
        _world.WireframeOn = _state.Wireframe;
        _world.BordersOn = _state.Borders;
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
            "WASD move | Q/E down/up | Shift fast | hold MMB - look | LMB place (or delete - X toggles Delete) | RMB paint (the face under the cursor) | 1-9 / wheel hotbar | Tab blocks\n" +
            "Next placement (ghost): J/K/L rotate around X/Y/Z | U/I/O mirror across X/Y/Z | Resize panel sets its size\n" +
            "Ctrl+Z undo | Ctrl+Y redo\n" +
            $"Blocks: {_world.Grid.BlockCount}   Quads: {_world.Quads} (faces before merge: {_world.FacesBeforeMerge})   " +
            $"Wire segments: {_world.LineSegments}   Chunks: {_world.ChunkCount}   Cursor: {cursor}   FPS: {Engine.GetFramesPerSecond()}");
    }
}
