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
/// пока не активен ни один инструмент (см. <see cref="ButtonFor"/>); Delete и Paint — тоже ЛКМ, каждый в своём
/// режиме (`X` переключает Delete, как и кнопка на тулбаре; Paint красит ровно ту грань клетки, в которую попал луч,
/// см. <see cref="UseToolAtHover"/>); 1–9 — слот хотбара, колесо мыши — зум камеры (<see cref="FlyCamera.Zoom"/>);
/// Tab — список блоков;
/// J/K/L — повернуть блок, который встанет следующим, вокруг X/Y/Z; U/I/O — отразить его по X/Y/Z; панель Resize
/// на тулбаре — его размер. Все три (поворот/отражение/размер) настраивают ПРИЗРАК, а не уже поставленные блоки —
/// см. <see cref="EditorState"/>. Ctrl+Z/Ctrl+Y — отмена/повтор (см. <see cref="UndoHistory"/> — в сетевой сессии
/// верстака та же комбинация уходит на сервер, см. <see cref="_networkWorkbenchName"/>).
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

    // Призрак функционального блока со своей моделью (мотор/вал, см. FunctionalBlockComponent.ScenePath) - вместо
    // _ghost (BoxMesh/ShapeMeshBuilder, см. UpdateGhostMesh), т.к. настоящая glTF-сцена не укладывается в один
    // MeshInstance3D.Mesh. Создаётся лениво, пересоздаётся только при смене самой сцены (см. UpdateGhostModel).
    private Node3D? _ghostModel;
    private PackedScene? _ghostModelScene;

    private Vector2 _mousePosition;
    private bool _looking;
    private bool _toolButtonDown;
    private MouseButton? _activeToolButton;
    private RayHit _hover = RayHit.None;
    private Vector3I? _lastToolCell;
    private Vector2 _lastToolMouse;
    private double _infoTimer;

    // Мультиплеер (см. Docs/05-world-and-vehicle-systems.md, «Мультиплеер»): не null - это сетевая сессия
    // совместного редактирования верстака _networkWorkbenchName, а не одиночная игра. Правки идут через
    // NetHub.RequestEdit/EditApplied (см. ApplyEdit/OnNetworkEditApplied) вместо прямой мутации _world.Construction;
    // Ctrl+Z/Y тоже уходят на сервер (NetHub.RequestUndo/RequestRedo) - общая на сессию история с авторством записей,
    // откатить можно только своё последнее действие, и только пока никто другой не построил поверх (см. HandleKey).
    // null - обычное одиночное редактирование, ни один из новых веток кода не затронут.
    private string? _networkWorkbenchName;
    private bool _isSessionAdmin;

    // Снэпшот постройки на момент нажатия кнопки инструмента (см. UndoHistory) - весь "мазок" перетаскивания
    // Paint/Delete фиксируется в истории одним шагом, а не по клетке.
    private UndoHistory.Snapshot? _undoStrokeBefore;

    // Кэш последней собранной формы призрака — чтобы не пересобирать меш каждый кадр без нужды.
    private string? _ghostSlug;
    private Basis _ghostRotation = Basis.Identity;
    private Vector3I _ghostMirror;
    private Vector3I _ghostSize;

    // Плавная анимация поворота призрака на J/K/L (чисто визуальная — EditorState.PendingRotationSteps/
    // PendingRotationBasis меняются мгновенно, как и раньше, и уходят в реальную установку блока по ЛКМ напрямую,
    // см. PlaceAtHover). _ghostVisualBasis — текущая ОТОБРАЖАЕМАЯ ориентация призрака, плавно доводится (Slerp) от
    // той, что была ДО последнего изменения EditorState.PendingRotationBasis, к НОВОЙ целевой — за GhostRotationAnimDuration
    // секунд (см. _Process/OnStateChanged). Нужна отдельная от EditorState анимация (не меняем саму
    // PendingRotationBasis постепенно) — иначе реальная установка блока ЛКМ в середине анимации ставила бы его
    // под "недовёрнутым" углом.
    private const double GhostRotationAnimDuration = 1.0 / 6.0; // секунд на один довод (90°)
    private Basis _ghostVisualBasis = Basis.Identity;
    private Basis _ghostRotationFrom = Basis.Identity;
    private Basis _ghostRotationTo = Basis.Identity;
    private double _ghostRotationElapsed;
    private Basis _lastPendingRotationBasis = Basis.Identity;

    public EditorState State => _state;
    public UndoHistory Undo => _undo;
    public VoxelWorld World => _world;
    public FlyCamera EditorCamera => _camera;
    public EditorUi Ui => _ui;
    public RayHit Hover => _hover;
    public MeshInstance3D Ghost => _ghost;

    /// <summary>Виден ли сейчас призрак функционального блока со своей моделью (см. <see cref="_ghostModel"/>) —
    /// для самотестов; в обычном одиночном/кубическом/формо-призраке (<see cref="Ghost"/>) всегда false.</summary>
    public bool GhostModelVisible => _ghostModel is { Visible: true };

    /// <summary>Текущая (возможно, ещё анимирующаяся) визуальная ориентация призрака — для самотестов, см.
    /// <see cref="_ghostVisualBasis"/>.</summary>
    public Basis GhostVisualBasis => _ghostVisualBasis;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildWorkAreaBoundary();

        _world = new VoxelWorld { Name = "World" };
        AddChild(_world);

        // Мультиплеер: вход в сетевую сессию верстака (см. World.GameWorld.OnSessionReadyForMe/OnJoinAcceptedForMe)
        // вместо обычного Create vehicle/Load - постройка приходит уже сериализованной от сервера, не с диска.
        _networkWorkbenchName = EditorHandoff.NetworkedWorkbenchName;
        EditorHandoff.NetworkedWorkbenchName = null;
        _isSessionAdmin = EditorHandoff.IsSessionAdmin;

        if (_networkWorkbenchName != null)
        {
            if (EditorHandoff.PendingNetworkedConstructionJson is { } networkedJson)
            {
                EditorHandoff.PendingNetworkedConstructionJson = null;
                ConstructionIO.Deserialize(_world.Construction, networkedJson, BlockCatalog.Instance);
                _world.RebuildDirty();
            }

            NetHub.Instance.EditApplied += OnNetworkEditApplied;
            NetHub.Instance.JoinRequestIncoming += OnJoinRequestIncoming;
            NetHub.Instance.JoinCancelled += OnJoinCancelled;
            NetHub.Instance.SessionClosedForMe += OnSessionClosedForMe;
            NetHub.Instance.SessionSynced += OnSessionSynced;
            NetHub.Instance.UndoRedoRejected += OnUndoRedoRejected;
        }
        // Вход через верстак с уже выбранной постройкой (EditorHandoff.PendingConstructionPath, см. EditorUi) сам
        // заменит содержимое (ConstructionIO.Deserialize вызывает Construction.Clear()) - ставить и сразу стирать
        // корневой блок незачем; "Create vehicle" (путь не задан) получает его, как и раньше.
        else if (EditorHandoff.PendingConstructionPath == null)
        {
            PlaceRootBlock();
        }

        _camera = new FlyCamera { Name = "Camera", Fov = 70f, Near = 0.05f, Far = 600f };
        AddChild(_camera);
        _camera.Current = true;
        _camera.LookAtPoint(new Vector3(3.0, 2.5, 5.0), new Vector3(0.0, 0.3, 0.0));

        BuildCursorVisuals();

        _ui = new EditorUi(this, _state);
        if (_networkWorkbenchName is { } workbenchName)
        {
            _ui.JoinResponseRequested += (requesterId, accepted) => NetHub.Instance.RespondToJoin(workbenchName, requesterId, accepted);
        }

        _state.Changed += OnStateChanged;
        OnStateChanged();

        _mousePosition = GetViewport().GetMousePosition();
        DevHarness.Start(this);
    }

    public override void _ExitTree()
    {
        if (_networkWorkbenchName == null) return;
        NetHub.Instance.EditApplied -= OnNetworkEditApplied;
        NetHub.Instance.JoinRequestIncoming -= OnJoinRequestIncoming;
        NetHub.Instance.JoinCancelled -= OnJoinCancelled;
        NetHub.Instance.SessionClosedForMe -= OnSessionClosedForMe;
        NetHub.Instance.SessionSynced -= OnSessionSynced;
        NetHub.Instance.UndoRedoRejected -= OnUndoRedoRejected;
    }

    /// <summary>Я админ этой сетевой сессии и ухожу (Exit/Spawn, см. <see cref="Ui.EditorUi"/>) - сессия закрывается
    /// для ВСЕХ участников разом (см. Docs/05, «Мультиплеер» - раздельный уход одного не-админ участника не
    /// закрывает её, см. class doc <see cref="Core.WorkbenchSession"/>). Ничего не делает для одиночной игры и для
    /// участника, присоединившегося через Join (не админ).</summary>
    public void LeaveNetworkSessionIfAdmin()
    {
        if (_networkWorkbenchName != null && _isSessionAdmin) NetHub.Instance.RequestCloseSession(_networkWorkbenchName);
    }

    /// <summary>Правка принята сервером и разослана всем участникам сессии (см. <see cref="NetHub.EditApplied"/>) -
    /// применяем её локально РОВНО ТЕМ ЖЕ кодом, что применил сервер (<see cref="NetEditOps.Apply"/>), включая
    /// случай, когда правку запросил я сам (сервер не считает клиентский запрос состоявшимся, пока не подтвердит и
    /// не разошлёт - см. class doc про "не сырые клики").</summary>
    private void OnNetworkEditApplied(string workbenchName, NetEditKind kind, Vector3I cell, Vector3I size,
        string blockSlug, Color color, Vector3I rotation, Vector3I mirror, int extraInt, bool extraBool)
    {
        if (workbenchName != _networkWorkbenchName) return;
        NetEditOps.Apply(_world.Construction, kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);
        UpdateHover();
    }

    /// <summary>Мой Undo/Redo принят сервером — полная ресинхронизация постройки (см. <see cref="NetHub.SessionSynced"/>),
    /// тем же <see cref="UndoHistory.Restore"/>, что применяет Undo/Redo локально в одиночной игре.</summary>
    private void OnSessionSynced(string workbenchName, UndoHistory.Snapshot snapshot)
    {
        if (workbenchName != _networkWorkbenchName) return;
        UndoHistory.Restore(_world.Construction, BlockCatalog.Instance, snapshot);
        UpdateHover();
    }

    /// <summary>Мой Undo/Redo отклонён (ничего отменять, или отменяемое — не моё последнее действие, см. Docs/05,
    /// «Мультиплеер») — показываем причину в статусной строке, как и результат Save/Load.</summary>
    private void OnUndoRedoRejected(string workbenchName, string reason)
    {
        if (workbenchName != _networkWorkbenchName) return;
        _ui.SetStatus(reason);
    }

    /// <summary>Я админ и кто-то просится присоединиться, пока я уже в редакторе (см. class doc
    /// <see cref="Editor.Ui.JoinRequestPopupUi"/> про то, почему это может случиться и здесь, и в
    /// <c>World.GameWorld</c>). Чаще всего именно ЗДЕСЬ — админ обычно уже в редакторе к моменту чужого Join (сам
    /// только что создал сессию).</summary>
    private void OnJoinRequestIncoming(string workbenchName, long requesterId)
    {
        if (workbenchName != _networkWorkbenchName || !_isSessionAdmin) return;
        _ui.ShowJoinRequestPopup(requesterId);
        // Явно, не полагаясь на то, что мышь тут "и так обычно видна" (FlyCamera прячет её только на время зажатой
        // СКМ) - баг, найденный пользователем: курсор всё равно иногда пропадал, по кнопкам Accept/Decline
        // нельзя было кликнуть. Тот же фикс уже был у World.GameWorld.OnJoinRequestIncoming, тут его не хватало.
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    /// <summary>Заявитель передумал (кнопка Cancel на его плашке ожидания) - убрать попап, если он ещё показывает
    /// именно эту заявку (см. <see cref="Ui.EditorUi.HideJoinRequestPopupIfFrom"/>).</summary>
    private void OnJoinCancelled(string workbenchName, long requesterId)
    {
        if (workbenchName != _networkWorkbenchName || !_isSessionAdmin) return;
        _ui.HideJoinRequestPopupIfFrom(requesterId);
    }

    /// <summary>Админ завершил сессию (вышел/заспавнил) - остальных участников выкидывает обратно в мир без
    /// предупреждения (см. Docs/05, «Мультиплеер»). <see cref="EditorHandoff.PendingPlayerPosition"/> всё ещё
    /// хранит позицию, с которой участник вошёл (задана при Join, см. World.GameWorld) - GameWorld восстановит её
    /// как обычно при возврате, отдельно ничего готовить не нужно.</summary>
    private void OnSessionClosedForMe(string workbenchName)
    {
        if (workbenchName != _networkWorkbenchName) return;
        Callable.From(() => GetTree().ChangeSceneToFile("res://Scenes/World.tscn")).CallDeferred();
    }

    // ------------------------------------------------------------------ сцена

    // Один плоский цвет неба и земли (запрос пользователя) — общий с World.GameWorld, см. EnvironmentBuilder.
    private void BuildEnvironment() => EnvironmentBuilder.BuildFlatSkyAndSun(this, Color.FromHtml("#6682FF"));

    /// <summary>
    /// Ставит корневой блок 1x1x1 (обычный куб, слаг "block") в центральную клетку (0,0,0) при входе в редактор —
    /// см. <see cref="BuildSpace.MinCell"/> doc-комментарий про то, почему именно эта клетка считается центром.
    /// Пустой редактор без единого блока неудобен как точка отсчёта при построении — этот блок такую точку даёт
    /// сразу, и его, как и любой другой, можно потом удалить инструментом Delete.
    /// </summary>
    private void PlaceRootBlock()
    {
        if (BlockCatalog.Instance.TryGetBySlug("block", out var definition))
        {
            _world.Construction.Place(Vector3I.Zero, definition, definition.DefaultColor);
        }
    }

    /// <summary>
    /// Пунктирная чёрная рамка (12 рёбер) по границе всей области построек (<see cref="BuildSpace.WorldMin"/>/
    /// <see cref="BuildSpace.WorldMax"/>) — постоянная толщина 3 экранных пикселя независимо от расстояния до
    /// камеры (см. <c>Shaders/work_area_boundary.gdshader</c>: каждое ребро — не линия, а вытянутый в CLIP-пространстве
    /// прямоугольник, ширина которого пересчитывается из пикселей в NDC через <c>VIEWPORT_SIZE</c> прямо в вершинном
    /// шейдере — обычная толщина линий через <see cref="Mesh.PrimitiveType.Lines"/>, как у каркаса/границ блоков
    /// (см. <see cref="CreateWireCube"/>), не поддерживает произвольную ширину в пикселях). Пунктир — по фрагментному
    /// шейдеру, по расстоянию вдоль ребра (UV.x), не по геометрии — одна и та же геометрия на любую длину сегмента.
    /// </summary>
    private void BuildWorkAreaBoundary()
    {
        var min = BuildSpace.WorldMin;
        var max = BuildSpace.WorldMax;

        Vector3[] corners =
        {
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z),
            new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z),
            new(min.X, max.Y, max.Z), new(max.X, max.Y, max.Z),
        };
        // 12 рёбер коробки — пары индексов в corners; сгруппированы по оси, вдоль которой идёт ребро.
        int[][] edges =
        {
            new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 7 }, // вдоль X
            new[] { 0, 2 }, new[] { 1, 3 }, new[] { 4, 6 }, new[] { 5, 7 }, // вдоль Y
            new[] { 0, 4 }, new[] { 1, 5 }, new[] { 2, 6 }, new[] { 3, 7 }, // вдоль Z
        };

        var vertices = new List<Vector3>();
        var directions = new List<Vector3>(); // -> NORMAL: единичное направление ребра (a -> b)
        var sides = new List<Color>();         // -> COLOR.r: 0/1, какая из двух сторон "толщины" эта вершина
        var uvs = new List<Vector2>();         // UV.x: расстояние вдоль ребра (для пунктира), UV.y: длина ребра
        var indices = new List<int>();

        foreach (var edge in edges)
        {
            var a = corners[edge[0]];
            var b = corners[edge[1]];
            var length = a.DistanceTo(b); // real_t (double в этой сборке движка, см. Docs/01-engine-build.md)
            var direction = (b - a) / length;
            int baseIndex = vertices.Count;

            vertices.Add(a); directions.Add(direction); sides.Add(new Color(0, 0, 0)); uvs.Add(new Vector2(0, length));
            vertices.Add(a); directions.Add(direction); sides.Add(new Color(1, 0, 0)); uvs.Add(new Vector2(0, length));
            vertices.Add(b); directions.Add(direction); sides.Add(new Color(0, 0, 0)); uvs.Add(new Vector2(length, length));
            vertices.Add(b); directions.Add(direction); sides.Add(new Color(1, 0, 0)); uvs.Add(new Vector2(length, length));

            indices.Add(baseIndex); indices.Add(baseIndex + 1); indices.Add(baseIndex + 2);
            indices.Add(baseIndex + 2); indices.Add(baseIndex + 1); indices.Add(baseIndex + 3);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = directions.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = sides.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        AddChild(new MeshInstance3D
        {
            Name = "WorkAreaBoundary",
            Mesh = mesh,
            MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/work_area_boundary.gdshader") },
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
        if (_ui.SaveDialogOpen)
        {
            // Пока открыт диалог сохранения, буквы/цифры должны попадать в поля имени/описания как текст, а не
            // становиться горячими клавишами редактора (иначе, например, "x" в названии заодно переключало бы
            // инструмент Delete) - Escape остаётся единственным исключением, закрывает диалог без сохранения,
            // как и кнопка Cancel.
            if (key.PhysicalKeycode == Key.Escape)
            {
                _ui.CloseSaveDialog();
                GetViewport().SetInputAsHandled();
            }

            return;
        }

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
            // В сетевой сессии (_networkWorkbenchName != null) уходят запросом на сервер (NetHub.RequestUndo/Redo) —
            // откатить можно только СВОЁ последнее действие, и только пока никто другой не построил поверх (сервер
            // отклонит иначе, см. Docs/05-world-and-vehicle-systems.md, «Мультиплеер»); применение — по ответу
            // (OnSessionSynced/OnUndoRedoRejected), не сразу здесь.
            case Key.Z when key.CtrlPressed && GetViewport().GuiGetFocusOwner() is not LineEdit:
                if (_networkWorkbenchName != null) NetHub.Instance.RequestUndo(_networkWorkbenchName);
                else if (_undo.Undo(_world.Construction, BlockCatalog.Instance)) UpdateHover();
                break;

            case Key.Y when key.CtrlPressed && GetViewport().GuiGetFocusOwner() is not LineEdit:
                if (_networkWorkbenchName != null) NetHub.Instance.RequestRedo(_networkWorkbenchName);
                else if (_undo.Redo(_world.Construction, BlockCatalog.Instance)) UpdateHover();
                break;
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventMouseButton button || _ui.IsModalOpen) return;

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

            // Колесо мыши — зум камеры (FlyCamera.Zoom), не листание хотбара (слот теперь меняется только клавишами
            // 1-9 или кликом по слоту/карточке в списке блоков).
            case MouseButton.WheelUp when button.Pressed:
                _camera.Zoom(1);
                break;

            case MouseButton.WheelDown when button.Pressed:
                _camera.Zoom(-1);
                break;
        }
    }

    /// <summary>Какая кнопка мыши применяет данный инструмент. И Delete, и Paint — ЛКМ (не путают друг друга: это
    /// взаимоисключающие режимы, см. <see cref="EditorState.Tool"/>/<see cref="ToolMode"/> — активен максимум один,
    /// поэтому ЛКМ каждый раз однозначна); пока любой из них активен, ЛКМ ничего не ставит, см.
    /// <see cref="UpdateCursorVisuals"/>. ПКМ инструментам не назначена вообще. null для None (ЛКМ в этом случае
    /// как раз и ставит блок).</summary>
    private static MouseButton? ButtonFor(ToolMode tool) => tool switch
    {
        ToolMode.Delete => MouseButton.Left,
        ToolMode.Paint => MouseButton.Left,
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
        // Тот же гейт, что и у призрака (CanPlaceFootprint) - блок можно поставить только рядом с уже стоящим
        // (кроме самой первой клетки постройки) - иначе ЛКМ по пустой земле поставила бы блок в стороне от всего.
        if (!CanPlaceFootprint(_hover.PlaceCell, _state.PendingSize)) return;

        ApplyEdit(NetEditKind.Place, _hover.PlaceCell, _state.PendingSize, definition.Slug, definition.DefaultColor,
            _state.PendingRotationSteps, _state.PendingMirror);
    }

    /// <summary>
    /// Paint красит РОВНО ту грань клетки под курсором, в которую попал луч (<see cref="RayHit.Normal"/> — уже
    /// известно, какая это сторона, см. <see cref="VoxelRaycaster"/>), не всю клетку и не весь экземпляр — иначе
    /// склейка/культинг в <see cref="ChunkMesher"/> не дали бы покрасить одну поверхность из нескольких сросшихся
    /// впритык блоков без расклейки остальных (см. ROADMAP.md). Если попавшая грань — FullCoverage (её и правда
    /// рисует ChunkMesher по <see cref="VoxelGrid.FaceColors"/>, см. <see cref="VoxelGrid.GetFaceMask"/>: у куба это
    /// все 6 сторон, у форм вроде Wedge — только 2 из 6, низ/задняя стенка), красится именно она через
    /// <see cref="VoxelGrid.TryPaintFace"/>. Иначе (рампа, срез, треугольный борт — рисует не ChunkMesher, а
    /// <see cref="ShapeMeshBuilder"/> отдельным мешем экземпляра, см. <c>Editor.ShapeInstanceView</c>) красится РОВНО
    /// та грань ФОРМЫ, которой принадлежит попадание, через <see cref="Construction.PaintRegion"/> — какую именно
    /// определяет НАСТОЯЩЕЕ пересечение того же луча камеры с реальной геометрией формы
    /// (<see cref="ShapeMeshBuilder.TryRaycastFace"/> — рейкастер клетки, в отличие от него, бьёт по ограничивающему
    /// кубу, не по форме, см. класс-док <see cref="VoxelRaycaster"/>, поэтому нужен отдельный точный тест именно
    /// здесь); если он ничего не пересёк (луч бьёт по кубу клетки мимо настоящей геометрии формы), откат на
    /// приближение по направлению — <see cref="ShapeMeshBuilder.TryFindPaintRegion"/>. Delete по-прежнему действует
    /// на весь экземпляр блока, которому принадлежит клетка (если клетка не принадлежит ни одному экземпляру —
    /// например, залита инструментом разработчика в обход Construction — откатывается на поклеточное удаление/
    /// покраску, как раньше). Resize сюда не входит — он не действует на уже поставленные блоки, см.
    /// <see cref="EditorState.PendingSize"/>.
    /// <para/>
    /// Этот метод (резолв — какая именно грань/регион/блок/клетка) ОДИНАКОВ для одиночной игры и сетевой сессии —
    /// разница только в <see cref="ApplyEdit"/> (применить сразу локально или запросом на сервер, см. её doc).
    /// Полная покраска в сети (2026-09-29 (6)) — раньше сетевая сессия огрубляла Paint до целиком блока/клетки; клиент
    /// и так уже резолвит грань/регион локально (нужно и для одиночной игры), поэтому отправить резолвленный результат
    /// по сети — не сложнее, чем отправить огрублённый.
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
                ApplyPaintAtCell(cell);
                break;

            case ToolMode.Delete:
                ApplyEdit(NetEditKind.Remove, cell, Vector3I.One, "", Colors.White, Vector3I.Zero, Vector3I.Zero);
                break;
        }
    }

    private void ApplyPaintAtCell(Vector3I cell)
    {
        var normal = _hover.Normal;
        int axis = normal.X != 0 ? 0 : normal.Y != 0 ? 1 : 2;
        bool positive = normal[axis] > 0;
        byte hitBit = (byte)(1 << (axis * 2 + (positive ? 1 : 0)));
        bool isFullCoverageSide = (_world.Grid.GetFaceMask(cell) & hitBit) != 0;

        if (isFullCoverageSide)
        {
            ApplyEdit(NetEditKind.PaintFace, cell, Vector3I.One, "", _state.PaintColor, Vector3I.Zero, Vector3I.Zero, axis, positive);
            return;
        }

        var owner = _world.Construction.GetOwner(cell);
        var building = owner != null && BlockCatalog.Instance.TryGetBySlug(owner.BlockSlug, out var def)
            ? def.GetComponent<BuildingBlockComponent>()
            : null;
        int region = -1;
        if (owner != null && building != null)
        {
            var rayOrigin = _camera.ProjectRayOrigin(_mousePosition);
            var rayDir = _camera.ProjectRayNormal(_mousePosition);
            var originWorld = BuildSpace.CellMin(owner.Origin);
            if (!ShapeMeshBuilder.TryRaycastFace(building.Shape, owner.Size, owner.RotationSteps, owner.Mirror, originWorld, rayOrigin, rayDir, out region))
            {
                ShapeMeshBuilder.TryFindPaintRegion(building.Shape, owner.RotationSteps, owner.Mirror, hitBit, out region);
            }
        }

        if (owner != null && region >= 0)
        {
            ApplyEdit(NetEditKind.PaintRegion, cell, Vector3I.One, "", _state.PaintColor, Vector3I.Zero, Vector3I.Zero, region);
        }
        else if (owner != null)
        {
            ApplyEdit(NetEditKind.PaintInstance, cell, Vector3I.One, "", _state.PaintColor, Vector3I.Zero, Vector3I.Zero);
        }
        else
        {
            ApplyEdit(NetEditKind.PaintCell, cell, Vector3I.One, "", _state.PaintColor, Vector3I.Zero, Vector3I.Zero);
        }
    }

    /// <summary>
    /// Единственная точка мутации <see cref="_world"/>.<see cref="VoxelWorld.Construction"/> из ввода игрока —
    /// одиночная игра применяет правку сразу (<see cref="NetEditOps.Apply"/>, тот же метод, что использует сервер
    /// сетевой сессии) и пишет её в свою локальную <see cref="_undo"/>; сетевая сессия НИЧЕГО не мутирует тут же —
    /// только шлёт запрос (<see cref="NetHub.RequestEdit"/>) и ждёт подтверждения (<see cref="OnNetworkEditApplied"/>),
    /// чтобы не разойтись с сервером/остальными участниками, если два игрока попали в одну клетку одновременно (см.
    /// Docs/05-world-and-vehicle-systems.md, «Мультиплеер»).
    /// </summary>
    private void ApplyEdit(NetEditKind kind, Vector3I cell, Vector3I size, string blockSlug, Color color,
        Vector3I rotation, Vector3I mirror, int extraInt = 0, bool extraBool = false)
    {
        if (_networkWorkbenchName != null)
        {
            NetHub.Instance.RequestEdit(_networkWorkbenchName, kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);
            return;
        }

        var before = _undo.Capture(_world.Construction);
        if (NetEditOps.Apply(_world.Construction, kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool))
        {
            _undo.RecordIfChanged(before, _world.Construction);
        }

        UpdateHover();
    }

    // ------------------------------------------------------------------ кадр

    public override void _Process(double delta)
    {
        _camera.MovementEnabled = !_ui.IsModalOpen;
        _ghostRotationElapsed += delta;
        var rotationT = Mathf.Clamp(_ghostRotationElapsed / GhostRotationAnimDuration, 0.0, 1.0);
        _ghostVisualBasis = _ghostRotationFrom.Slerp(_ghostRotationTo, rotationT);
        UpdateHover();

        // Удержание кнопки инструмента: он применяется к каждому новому блоку под курсором, но только если мышь сдвинулась —
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

        // Функциональный блок со своей моделью (мотор/вал) не укладывается в один MeshInstance3D.Mesh (см.
        // _ghostModel doc) - призрак ставится отдельным узлом вместо куба/формы _ghost, та же подгонка
        // (FunctionalBlockGeometry), что и у уже размещённых блоков (FunctionalBlockView)/иконки (BlockIconView).
        var functional = definition?.GetComponent<FunctionalBlockComponent>();
        (PackedScene? scene, Aabb aabb) ghostScene = default;
        if (canPlace && functional != null && !string.IsNullOrEmpty(functional.ScenePath))
        {
            ghostScene = FunctionalBlockGeometry.GetOrLoadScene(functional.ScenePath);
        }

        bool showModel = ghostScene.scene != null;
        _ghost.Visible = canPlace && !showModel;
        if (_ghostModel != null) _ghostModel.Visible = showModel;

        if (showModel)
        {
            UpdateGhostModel(ghostScene.scene!, ghostScene.aabb, functional!.ModelScale);
        }
        else if (canPlace)
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

    /// <summary>
    /// Все ли клетки прямоугольной области <paramref name="size"/> клеток от <paramref name="origin"/> (растёт
    /// только в положительную сторону) свободны, внутри области построек, и КАСАЮТСЯ уже стоящего блока хотя бы
    /// одной гранью — используется и для видимости призрака, и как гейт перед вызовом
    /// <see cref="Construction.PlaceBlock"/> в <see cref="PlaceAtHover"/> (сам он о соседстве ничего не знает —
    /// проверка только здесь, на уровне UI, единая для того, что видно призраком, и того, что реально ставится).
    /// Требование соседства не действует, пока постройка совсем пуста (<see cref="VoxelGrid.BlockCount"/> == 0) —
    /// иначе самый первый блок в принципе некуда было бы поставить; на практике это почти никогда не наступает —
    /// редактор сам ставит корневой блок при входе (<see cref="PlaceRootBlock"/>), это защита от мёртвой ситуации,
    /// если его всё же удалили.
    /// </summary>
    // Правило соседства теперь общее с сервером сетевой сессии редактирования (см. PlacementRules class doc) — и
    // клиент (призрак/гейт ЛКМ здесь), и NetHub на сервере применяют ровно одну и ту же проверку.
    private bool CanPlaceFootprint(Vector3I origin, Vector3I size) => PlacementRules.CanPlaceFootprint(_world.Grid, origin, size);

    /// <summary>
    /// Призрак отражает настоящую форму выбранного блока (не только куб), текущий поворот/отражение
    /// (<see cref="EditorState.PendingRotationSteps"/>/<see cref="EditorState.PendingMirror"/>) и размер
    /// (<see cref="EditorState.PendingSize"/>) — то есть выглядит ровно так же, как блок, который встанет по ЛКМ
    /// (панель Resize на тулбаре меняет именно это, не уже поставленные блоки). Меш кубов — <see cref="BoxMesh"/>
    /// размером во весь Size (без склейки соседних граней, как у настоящих кубов в постройке, но снаружи выглядит
    /// так же — одна сплошная область без швов); меш остальных форм строится той же <c>ShapeMeshBuilder</c>, что и
    /// уже поставленные блоки (координаты — от угла клетки, см. <see cref="ShapeInstanceView"/>).
    /// <para/>
    /// Поворот берётся из <see cref="_ghostVisualBasis"/> (плавно доводится, Slerp, до <see cref="EditorState.PendingRotationBasis"/>
    /// на J/K/L, см. его doc-комментарий), не напрямую из <see cref="EditorState"/> — во время анимации это
    /// промежуточная, не кратная 90° ориентация, поэтому меш пересобирается каждый кадр, пока анимация не осядет на
    /// целевой (сравнение ниже естественно перестаёт совпадать, пока поворот ещё "в пути").
    /// </summary>
    private void UpdateGhostMesh(BlockDefinition definition, string slug)
    {
        var building = definition.GetComponent<BuildingBlockComponent>();
        var rotation = _ghostVisualBasis;
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
            // includeFullCoverageFaces: true — призрак не стоит в VoxelGrid, поэтому ChunkMesher никогда не дорисует
            // ему низ/заднюю стенку и т.п. (см. doc-комментарий ShapeMeshBuilder.BuildData); без этого флага у
            // призрака Wedge/Pyramid/InvertedPyramid были видны только рампа/треугольные борта, силуэт был "дырявым".
            var (solid, _, _) = ShapeMeshBuilder.Build(building!.Shape, size, rotation, mirror, Colors.White, includeFullCoverageFaces: true);
            _ghost.Mesh = (Mesh?)solid ?? new BoxMesh { Size = extent };
        }

        _ghostSlug = slug;
        _ghostRotation = rotation;
        _ghostMirror = mirror;
        _ghostSize = size;
    }

    /// <summary>
    /// Призрак функционального блока со своей моделью (мотор/вал) — узел пересоздаётся, только если сменилась сама
    /// сцена (разные типы блока или блок без модели вообще, см. <see cref="UpdateCursorVisuals"/>), трансформ
    /// (позиция/масштаб/поворот) пересчитывается каждый вызов, как и у куба/формы в <see cref="UpdateGhostMesh"/> —
    /// дёшево, это просто присваивание <see cref="Node3D.Transform"/>, без пересборки меша. Подгонка под размер
    /// клетки — та же <see cref="FunctionalBlockGeometry"/>, что и у уже поставленных блоков
    /// (<see cref="FunctionalBlockView"/>), поворот — <see cref="_ghostVisualBasis"/> (как у куба/формы в
    /// <see cref="UpdateGhostMesh"/> — плавный довод, Slerp, не мгновенный <see cref="EditorState.PendingRotationBasis"/>);
    /// <see cref="EditorState.PendingMirror"/> для настоящих моделей не поддерживается (см. <see cref="FunctionalBlockGeometry"/>
    /// class doc) — призрак его тоже игнорирует, ровно как и уже поставленный блок.
    /// </summary>
    private void UpdateGhostModel(PackedScene scene, Aabb aabb, Vector3 modelScale)
    {
        if (_ghostModelScene != scene)
        {
            _ghostModel?.QueueFree();
            _ghostModel = scene.Instantiate<Node3D>();
            AddChild(_ghostModel);
            _ghostModelScene = scene;
        }

        var size = _state.PendingSize;
        var targetExtent = new Vector3(size.X, size.Y, size.Z) * BuildSpace.CellSize;
        var targetCenter = BuildSpace.CellMin(_hover.PlaceCell) + targetExtent * 0.5f;
        _ghostModel!.Transform = FunctionalBlockGeometry.ComputeFitTransform(aabb, targetExtent, targetCenter, _ghostVisualBasis, modelScale);
    }

    private void OnStateChanged()
    {
        _world.WireframeOn = _state.Wireframe;
        _world.BordersOn = _state.Borders;

        // Призрак доворачивается к НОВОЙ целевой ориентации (см. _ghostVisualBasis doc) каждый раз, когда
        // PendingRotationBasis реально меняется (J/K/L, в т.ч. вызванные напрямую в самотестах, не только настоящей
        // клавишей) - отправная точка довода - ТЕКУЩЕЕ отображаемое положение (которое само может быть ещё
        // "в пути" от предыдущего нажатия), не обязательно уже осевшее на прошлой цели - частые нажатия подряд не
        // дёргают призрак рывками.
        if (_state.PendingRotationBasis != _lastPendingRotationBasis)
        {
            _ghostRotationFrom = _ghostVisualBasis;
            _ghostRotationTo = _state.PendingRotationBasis;
            _ghostRotationElapsed = 0.0;
            _lastPendingRotationBasis = _state.PendingRotationBasis;
        }

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

        // Только debug-статистика (запрос пользователя) — подсказки по управлению убраны из HUD; сами подсказки
        // по-прежнему актуальны, см. class doc BuildEditor и таблицу управления в Docs/03-build-editor.md.
        _ui.SetInfo(
            $"Blocks: {_world.Grid.BlockCount}   Quads: {_world.Quads} (faces before merge: {_world.FacesBeforeMerge})   " +
            $"Wire segments: {_world.LineSegments}   Chunks: {_world.ChunkCount}   Cursor: {cursor}   FPS: {Engine.GetFramesPerSecond()}");
    }
}
