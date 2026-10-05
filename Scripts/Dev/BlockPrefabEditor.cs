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
/// Дебаг-редактор для СОЗДАНИЯ/КАЛИБРОВКИ функциональных блоков (мотор/вал и т.п., см.
/// <see cref="FunctionalBlockComponent"/>) — отдельный инструмент, не часть <see cref="BuildEditor"/> (другая цель:
/// не строить постройку из готовых блоков, а подготовить ОДИН блок для каталога). Запуск:
/// <c>godot --path . -- --blockeditor[=slug]</c> (см. <see cref="World.GameWorld"/> — своя сцена
/// <c>res://Scenes/BlockPrefabEditor.tscn</c>, не <c>BuildEditor.tscn</c>, т.к. этому инструменту не нужна ни
/// воксельная сетка, ни хотбар).
/// <para/>
/// Позволяет: загрузить существующий <c>blocks/&lt;slug&gt;.xml</c> ИЛИ начать новый, указать модель (<c>scene</c>,
/// живая перезагрузка), подобрать <see cref="FunctionalBlockComponent.Footprint"/> (превью — РОВНО та же подгонка,
/// что и в игре, <see cref="FunctionalBlockGeometry.ComputeFitTransform"/> — то есть видно именно то, что получится
/// после установки блока, а не "сырой", нефабопричной размер модели) и при необходимости вручную растянуть модель
/// по осям поверх этой подгонки (<see cref="FunctionalBlockComponent.ModelScale"/> — для случаев, когда
/// равномерная автоподгонка не годится), настроить коллизию как набор боксов (<see cref="CollisionBox"/>,
/// позиция+размер в метрах, см. <see cref="World.VehicleSpawner"/> — пусто означает, что у блока коллизии нет
/// ВООБЩЕ, никакого автоматического бокса "на всякий случай": некоторые модели нарочно выпирают деталями за
/// footprint, которым коллизия не нужна), и назначить ресурсные порты (<see cref="ResourcePort"/> — вход/выход
/// конкретного <see cref="ResourceType"/>, на конкретной стороне footprint'а и клетке этой стороны, см.
/// <see cref="FunctionalBlockGeometry.ComputePortAnchor"/>; несколько портов МОГУТ сидеть в одном и том же месте —
/// не ошибка, см. <see cref="ResourcePort"/> class doc). "Save" пишет/перезаписывает <c>res://blocks/&lt;slug&gt;.xml</c>
/// целиком.
/// </summary>
public partial class BlockPrefabEditor : Node3D
{
    private static readonly Color FootprintBoxColor = new(0.55f, 0.88f, 1.0f);
    private static readonly Color CollisionBoxColor = new(1.0f, 0.35f, 0.25f, 0.35f);
    private static readonly Color SkyColor = Color.FromHtml("#808080");

    /// <summary>Цвет маркера порта по типу ресурса (см. <see cref="ResourceType"/>) — для визуального отличия ВИДА
    /// ресурса в превью, не часть данных блока (та же роль, что и <see cref="CollisionBoxColor"/> для коллизии).</summary>
    private static readonly Dictionary<ResourceType, Color> PortColors = new()
    {
        [ResourceType.Fluid] = new Color(0.25f, 0.6f, 1.0f),
        [ResourceType.Torque] = new Color(1.0f, 0.5f, 0.15f),
    };
    /// <summary>Цвет маркера логической ноды по типу (<see cref="NodeType"/>) — только для превью, не часть данных блока.</summary>
    private static readonly Dictionary<NodeType, Color> NodeColors = new()
    {
        [NodeType.Electricity] = new Color(1.0f, 0.85f, 0.2f),
        [NodeType.Boolean] = new Color(0.45f, 1.0f, 0.5f),
        [NodeType.Number] = new Color(0.8f, 0.55f, 1.0f),
    };
    private static readonly Color GridColor = new(1f, 1f, 1f, 0.22f);
    private static readonly Color AxisColorX = new(1f, 0.3f, 0.3f);
    private static readonly Color AxisColorY = new(0.35f, 1f, 0.35f);
    private static readonly Color AxisColorZ = new(0.35f, 0.55f, 1f);
    private const float GridRadius = 1.5f; // метров от начала координат в каждую сторону

    private FlyCamera _camera = null!;
    private BlockPrefabEditorUi _ui = null!;
    private Node3D? _modelInstance;
    private MeshInstance3D _footprintWire = null!;
    private Node3D _collisionVisuals = null!;
    private StandardMaterial3D _collisionMaterial = null!;
    private Node3D _portVisuals = null!;

    private Vector2 _mousePosition;
    private bool _looking;

    /// <summary>
    /// Undo/Redo (Ctrl+Z/Ctrl+Y, по запросу пользователя) — снэпшот ВСЕХ редактируемых полей разом (не только
    /// списков), т.к. это простой инструмент с одним "документом" на экране, а не дерево независимых объектов
    /// (как блоки в <c>Editor.BuildEditor</c>/<see cref="Core.UndoHistory"/>, с которым это НЕ связано и не
    /// переиспользует его). <see cref="PushUndoPoint"/> снимает снэпшот ДО изменения — для Add/Remove порта/бокса
    /// коллизии вызывается явно (см. <see cref="AddCollisionBox"/> и соседние), для правки любого текстового поля —
    /// по <see cref="BlockPrefabEditorUi.EditSessionStarting"/> (фокус ВОШЁЛ в поле, см. его doc про то, почему не
    /// по событию "значение изменилось" — к тому моменту старое значение уже потеряно).
    /// </summary>
    private readonly record struct Snapshot(
        string Slug, string Name, Color Color, float Mass, float Durability, float DamageResistance,
        string ScenePath, Vector3I Footprint, Vector3 ModelScale, Vector3 ModelOffset, string Behavior, float Capacity,
        IReadOnlyList<CollisionBox> CollisionBoxes, IReadOnlyList<ResourcePort> Ports, IReadOnlyList<LogicNode> Nodes, string ParamsJson);

    private const int MaxUndoDepth = 50; // разумный потолок, не бесконечно растущий список
    private readonly List<Snapshot> _undoStack = new();
    private readonly List<Snapshot> _redoStack = new();

    /// <summary>Для самотестов (см. <c>SelfTest.RunBlockPrefabEditorTests</c>) — сама панель полей/кнопок.</summary>
    public BlockPrefabEditorUi Ui => _ui;

    public override void _Ready()
    {
        BuildEnvironment();

        _camera = new FlyCamera { Current = true };
        AddChild(_camera);
        _camera.LookAtPoint(new Vector3(1.6f, 1.2f, 2.0f), Vector3.Zero);

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

        _footprintWire = new MeshInstance3D
        {
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = FootprintBoxColor },
        };
        AddChild(_footprintWire);

        _collisionMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = CollisionBoxColor,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _collisionVisuals = new Node3D();
        AddChild(_collisionVisuals);

        _portVisuals = new Node3D();
        AddChild(_portVisuals);

        var uiLayer = new CanvasLayer();
        AddChild(uiLayer);
        var root = new Control();
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        root.MouseFilter = Control.MouseFilterEnum.Ignore;
        uiLayer.AddChild(root);

        _ui = new BlockPrefabEditorUi(root);
        _ui.LoadRequested += LoadSlug;
        _ui.NewRequested += () => ResetToDefaults(_ui.Slug);
        _ui.SaveRequested += Save;
        _ui.FieldsChanged += RefreshPreview;
        _ui.ReloadSceneRequested += RefreshPreview;
        _ui.AddCollisionBoxRequested += AddCollisionBox;
        _ui.RemoveCollisionBoxRequested += RemoveCollisionBox;
        _ui.CollisionBoxesChanged += RefreshCollisionVisuals;
        _ui.AddPortRequested += AddPort;
        _ui.RemovePortRequested += RemovePort;
        _ui.PortsChanged += RefreshPortVisuals;
        _ui.AddNodeRequested += AddNode;
        _ui.RemoveNodeRequested += RemoveNode;
        _ui.NodesChanged += RefreshPortVisuals;
        _ui.EditSessionStarting += PushUndoPoint;

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

    /// <summary>
    /// Нейтральный серый фон + белый свет (по запросу пользователя) — не переиспользует
    /// <see cref="Core.EnvironmentBuilder.BuildFlatSkyAndSun"/> (тот красит небо в синий для игры/редактора построек,
    /// трогать его ради ОДНОГО инструмента не нужно): этому инструменту важна точная цветопередача модели/материалов
    /// при калибровке, а не атмосфера игрового мира — цветное небо давало бы паразитный оттенок через ambient-свет
    /// от неба (<see cref="Godot.Environment.AmbientSource.Sky"/>).
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

    /// <summary>Решётка на плоскости Y=0 с шагом <see cref="BuildSpace.CellSize"/> (видно масштаб в клетках) — по
    /// запросу пользователя, та же идея, что и у координатных осей ниже: ориентир, относительно которого видно
    /// реальный размер модели/footprint'а, не только "на глаз".</summary>
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

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return mesh;
    }

    /// <summary>Три цветные линии через начало координат (X — красная, Y — зелёная, Z — синяя, стандартная
    /// конвенция) — один <see cref="ArrayMesh"/> с цветом НА ВЕРШИНУ (<see cref="StandardMaterial3D.VertexColorUseAsAlbedo"/>),
    /// не три отдельных меша/материала.</summary>
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

    // ------------------------------------------------------------------ ввод (камера, тот же приём, что и BuildEditor)

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouse mouse && !_looking) _mousePosition = mouse.Position;

        switch (e)
        {
            // Баг, найденный пользователем: поле ввода оставалось сфокусированным после печати (слаг/footprint/...),
            // и последующие WASD для камеры заодно печатались в него как текст - Input.IsPhysicalKeyPressed (которым
            // FlyCamera читает WASD) не заботится о фокусе UI, но Godot ОТДЕЛЬНО доставляет те же нажатия
            // сфокусированному Control как текст. Снимаем фокус на ЛЮБОЙ клик мышью (ЛКМ/СКМ/ПКМ) ДО того, как GUI
            // успеет обработать событие (_Input вызывается раньше GUI-слоя) - если клик был РЕАЛЬНО по полю/кнопке
            // панели, Godot тут же вернёт фокус туда сам через свою обычную обработку клика, это не мешает обычному
            // клику по полю; если клик был в 3D-вид (где и начинается движение камеры) - фокус остаётся снятым.
            case InputEventMouseButton { Pressed: true } anyButton
                when anyButton.ButtonIndex is MouseButton.Left or MouseButton.Middle or MouseButton.Right:
                GetViewport().GuiReleaseFocus();
                break;

            // Undo/Redo - тот же принцип, что и у Ctrl+Z/Y в Editor.BuildEditor: не перехватывать, пока фокус на
            // текстовом поле (иначе отменяли бы правку полей вместо родного текстового undo самого LineEdit внутри
            // него - у Godot LineEdit есть свой Ctrl+Z на уровне редактирования текста, трогать его не нужно).
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

            case InputEventMouseButton { ButtonIndex: MouseButton.Middle, Pressed: false } when _looking:
                _looking = false;
                Input.MouseMode = Input.MouseModeEnum.Visible;
                break;

            // Та же причина, что и у зума колесом ниже (курсор над панелью -> не трогаем камеру) - раньше СКМ
            // над панелью ВСЁ РАВНО запускала вращение камеры (эта ветка ничего не проверяла про курсор), хотя
            // сама панель на СКМ никак не реагирует - баг, найденный пользователем вместе с WASD-в-поле выше.
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle, Pressed: true } when GetViewport().GuiGetHoveredControl() == null:
                _looking = true;
                Input.MouseMode = Input.MouseModeEnum.Captured;
                break;

            // Баг, найденный пользователем: колесо зумило камеру И ОДНОВРЕМЕННО прокручивало панель (у её
            // ScrollContainer колесо работает через обычный GUI-путь, независимо от этого _Input, который
            // срабатывает раньше) - зумим камеру, только если курсор НЕ над панелью (GuiGetHoveredControl() вернёт
            // null над пустым 3D-видом, т.к. корневой Control панели - MouseFilter.Ignore, а дочерние виджеты самой
            // панели - Stop). Над панелью просто не обрабатываем сами - событие уходит в GUI-слой как обычно,
            // крутит ScrollContainer, камера не трогается.
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

    // ------------------------------------------------------------------ Undo/Redo (Ctrl+Z/Ctrl+Y, см. class doc)

    private Snapshot CaptureSnapshot() => new(
        _ui.Slug, _ui.Name, _ui.Color, _ui.Mass, _ui.Durability, _ui.DamageResistance, _ui.ScenePath,
        _ui.Footprint, _ui.ModelScale, _ui.ModelOffset, _ui.Behavior, _ui.Capacity, _ui.CollisionBoxes, _ui.Ports, _ui.Nodes, _ui.ParamsJson);

    private void RestoreSnapshot(Snapshot s)
    {
        _ui.LoadFields(s.Slug, s.Name, s.Color, s.Mass, s.Durability, s.DamageResistance, s.ScenePath,
            s.Footprint, s.ModelScale, s.ModelOffset, s.Behavior, s.Capacity, s.CollisionBoxes, s.Ports, s.Nodes, s.ParamsJson);
        RefreshPreview();
    }

    /// <summary>Снимает снэпшот ДО изменения — см. вызовы ниже (Add/Remove) и подписку на
    /// <see cref="BlockPrefabEditorUi.EditSessionStarting"/> в <c>_Ready</c>. Любое новое действие после Undo
    /// стирает "будущее" (Redo) — обычная семантика undo/redo, как и у <see cref="Core.UndoHistory"/>.</summary>
    private void PushUndoPoint()
    {
        _undoStack.Add(CaptureSnapshot());
        if (_undoStack.Count > MaxUndoDepth) _undoStack.RemoveAt(0);
        _redoStack.Clear();
    }

    /// <summary>Начинать редактирование нового блока (Load/New) — это НЕ действие, которое логично "отменять"
    /// отменой правок ДРУГОГО, уже закрытого блока (та же логика, что у большинства редакторов текста — переключение
    /// документа не трогает чужую историю undo) — каждая загрузка начинает Undo/Redo с чистого листа.</summary>
    private void ClearUndoHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    /// <summary>Публичный (не <c>private</c>) — как и <see cref="AddCollisionBox"/>/<see cref="AddPort"/> рядом,
    /// чтобы напрямую вызывался из <c>SelfTest</c>, без симуляции реального нажатия Ctrl+Z.</summary>
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
        _ui.LoadFields(slug, "", Colors.White, 10f, 100f, 0.1f, "", Vector3I.One, Vector3.One, Vector3.Zero, "", 0f,
            Array.Empty<CollisionBox>(), Array.Empty<ResourcePort>(), Array.Empty<LogicNode>(), "");
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

        string path = $"res://blocks/{slug}.xml";
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
            string scenePath = "", behavior = "";
            float capacity = 0f;
            var footprint = Vector3I.One;
            var modelScale = Vector3.One;
            var modelOffset = Vector3.Zero;
            var collisionBoxes = new List<CollisionBox>();
            var ports = new List<ResourcePort>();
            var nodes = new List<LogicNode>();
            string paramsJson = "";
            bool hasFunctionalBlock = false;

            foreach (var componentNode in root.Elements("Component"))
            {
                string type = (string?)componentNode.Attribute("type") ?? "";
                string json = componentNode.Value.Trim();
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
                    hasFunctionalBlock = true;
                    if (element.TryGetProperty("footprint", out var fp) && fp.ValueKind == JsonValueKind.Array)
                    {
                        var items = fp.EnumerateArray().ToArray();
                        footprint = new Vector3I(items[0].GetInt32(), items[1].GetInt32(), items[2].GetInt32());
                    }

                    if (element.TryGetProperty("behavior", out var b)) behavior = b.GetString() ?? "";
                    if (element.TryGetProperty("capacity", out var cap)) capacity = cap.GetSingle();
                    if (element.TryGetProperty("scene", out var sc)) scenePath = sc.GetString() ?? "";
                    if (element.TryGetProperty("modelScale", out var ms) && ms.ValueKind == JsonValueKind.Array)
                    {
                        var items = ms.EnumerateArray().ToArray();
                        modelScale = new Vector3(items[0].GetSingle(), items[1].GetSingle(), items[2].GetSingle());
                    }

                    if (element.TryGetProperty("modelOffset", out var mo) && mo.ValueKind == JsonValueKind.Array && mo.GetArrayLength() == 3)
                    {
                        var items = mo.EnumerateArray().ToArray();
                        modelOffset = new Vector3(items[0].GetSingle(), items[1].GetSingle(), items[2].GetSingle());
                    }

                    if (element.TryGetProperty("nodes", out var nodesElement) && nodesElement.ValueKind == JsonValueKind.Array)
                    {
                        nodes.AddRange(FunctionalBlockComponent.ParseNodes(nodesElement));
                    }

                    if (element.TryGetProperty("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object)
                    {
                        paramsJson = JsonSerializer.Serialize(paramsElement);
                    }

                    if (element.TryGetProperty("ports", out var portsJson) && portsJson.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var portJson in portsJson.EnumerateArray())
                        {
                            string id = portJson.GetProperty("id").GetString() ?? "";
                            var resource = FunctionalBlockComponent.ParseResourceType(portJson.GetProperty("resource").GetString()!);
                            var direction = Enum.Parse<PortDirection>(portJson.GetProperty("direction").GetString()!, ignoreCase: true);
                            var face = portJson.TryGetProperty("face", out var faceJson) && faceJson.ValueKind == JsonValueKind.String
                                ? Enum.Parse<BlockFace>(faceJson.GetString()!, ignoreCase: true)
                                : BlockFace.PosZ;
                            var faceCell = Vector2I.Zero;
                            if (portJson.TryGetProperty("position", out var posJson) && posJson.ValueKind == JsonValueKind.Array && posJson.GetArrayLength() == 2)
                            {
                                var items = posJson.EnumerateArray().ToArray();
                                faceCell = new Vector2I(items[0].GetInt32(), items[1].GetInt32());
                            }

                            ports.Add(new ResourcePort { Id = id, Resource = resource, Direction = direction, Face = face, FaceCell = faceCell });
                        }
                    }

                    if (element.TryGetProperty("collision", out var collision) && collision.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var box in collision.EnumerateArray())
                        {
                            var pos = box.GetProperty("position").EnumerateArray().ToArray();
                            var size = box.GetProperty("size").EnumerateArray().ToArray();
                            collisionBoxes.Add(new CollisionBox
                            {
                                Position = new Vector3(pos[0].GetSingle(), pos[1].GetSingle(), pos[2].GetSingle()),
                                Size = new Vector3(size[0].GetSingle(), size[1].GetSingle(), size[2].GetSingle()),
                            });
                        }
                    }
                }
            }

            ClearUndoHistory();
            _ui.LoadFields(slug, name, color, mass, durability, damage, scenePath, footprint, modelScale, modelOffset, behavior, capacity, collisionBoxes, ports, nodes, paramsJson);
            _ui.SetStatus(hasFunctionalBlock
                ? $"loaded '{slug}'"
                : $"loaded '{slug}' - no FunctionalBlock component yet, saving will add one");
        }
        catch (Exception ex)
        {
            _ui.SetStatus($"failed to parse '{path}': {ex.Message}");
        }

        RefreshPreview();
    }

    // ------------------------------------------------------------------ превью (модель + footprint + коллизия)

    /// <summary>
    /// Блок всегда рисуется ЦЕНТРИРОВАННЫМ на мировом начале координат (0,0,0), по запросу пользователя (вместе с
    /// координатными осями выше это даёт наглядный ориентир масштаба) — footprint-бокс спускается от
    /// <c>-extent/2</c> до <c>+extent/2</c>, а не от (0,0,0), как фактическая область клеток блока в игре (та
    /// конвенция, "от минимального угла", не меняется — это только смещение самого ПРЕВЬЮ в 3D-виде этого
    /// инструмента). <see cref="CollisionBox.Position"/> в полях/XML по-прежнему в исходной, угловой системе
    /// координат блока — здесь лишь визуально смещается на тот же оффсет при отрисовке.
    /// </summary>
    private void RefreshPreview()
    {
        var footprint = _ui.Footprint;
        var extent = new Vector3(footprint.X, footprint.Y, footprint.Z) * BuildSpace.CellSize;
        var previewOffset = -extent * 0.5f;

        _footprintWire.Mesh = BuildWireBox(extent);
        _footprintWire.Position = previewOffset;

        _modelInstance?.QueueFree();
        _modelInstance = null;

        string scenePath = _ui.ScenePath;
        if (!string.IsNullOrEmpty(scenePath))
        {
            var (scene, aabb) = FunctionalBlockGeometry.GetOrLoadScene(scenePath);
            if (scene != null)
            {
                _modelInstance = scene.Instantiate<Node3D>();
                AddChild(_modelInstance);
                _modelInstance.Transform = FunctionalBlockGeometry.ComputeFitTransform(aabb, extent, Vector3.Zero, Basis.Identity, _ui.ModelScale, _ui.ModelOffset);
            }
            else
            {
                _ui.SetStatus($"scene not found/failed to load: {scenePath}");
            }
        }

        RefreshCollisionVisuals();
        RefreshPortVisuals();
    }

    private void RefreshCollisionVisuals()
    {
        foreach (var child in _collisionVisuals.GetChildren()) child.QueueFree();

        var footprint = _ui.Footprint;
        var previewOffset = -new Vector3(footprint.X, footprint.Y, footprint.Z) * BuildSpace.CellSize * 0.5f;

        foreach (var box in _ui.CollisionBoxes)
        {
            _collisionVisuals.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = box.Size },
                MaterialOverride = _collisionMaterial,
                Position = box.Position + box.Size * 0.5f + previewOffset,
            });
        }
    }

    public void AddCollisionBox()
    {
        PushUndoPoint();
        var footprint = _ui.Footprint;
        var defaultSize = new Vector3(footprint.X, footprint.Y, footprint.Z) * BuildSpace.CellSize;
        var boxes = new List<CollisionBox>(_ui.CollisionBoxes) { new() { Position = Vector3.Zero, Size = defaultSize } };
        _ui.SetCollisionBoxRows(boxes);
        RefreshCollisionVisuals();
    }

    public void RemoveCollisionBox(int index)
    {
        var boxes = new List<CollisionBox>(_ui.CollisionBoxes);
        if (index < 0 || index >= boxes.Count) return;
        PushUndoPoint();
        boxes.RemoveAt(index);
        _ui.SetCollisionBoxRows(boxes);
        RefreshCollisionVisuals();
    }

    /// <summary>
    /// Маленький цветной шар (цвет — по <see cref="ResourceType"/>, см. <see cref="PortColors"/>) чуть НАД
    /// поверхностью footprint'а (сдвинут по нормали грани на небольшое фиксированное расстояние — иначе маркер
    /// наполовину тонет в кубе/модели и его плохо видно) плюс подпись <c>Id (Direction)</c> рядом — тот же приём
    /// центрирования превью на начале координат (<see cref="RefreshPreview"/> doc), что и у коллизии/footprint'а.
    /// Позиция считается через <see cref="FunctionalBlockGeometry.ComputePortAnchor"/> — ту же геометрию, которой
    /// позже будет пользоваться любой код соединений портов (ещё не реализован, см. <see cref="ResourcePort"/> class doc).
    /// </summary>
    private void RefreshPortVisuals()
    {
        foreach (var child in _portVisuals.GetChildren()) child.QueueFree();

        var footprint = _ui.Footprint;
        var previewOffset = -new Vector3(footprint.X, footprint.Y, footprint.Z) * BuildSpace.CellSize * 0.5f;
        const float markerOffset = 0.03f;

        AddNodeMarkers(footprint, previewOffset);

        foreach (var port in _ui.Ports)
        {
            var (localPosition, normal) = FunctionalBlockGeometry.ComputePortAnchor(port.Face, port.FaceCell, footprint, BuildSpace.CellSize);
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
                Position = localPosition + previewOffset + normal * markerOffset,
            });

            _portVisuals.AddChild(new Label3D
            {
                Text = $"{port.Id} ({port.Direction})",
                FontSize = 28,
                PixelSize = 0.0022f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                Modulate = color,
                Position = localPosition + previewOffset + normal * (markerOffset + 0.05f),
            });
        }
    }

    /// <summary>
    /// Логические ноды (<see cref="LogicNode"/>) — маленький КУБИК в ЦЕНТРЕ своей клетки (ноды сидят внутри блока, а не на
    /// грани, как цветные шарики физических портов) плюс подпись <c>Id (Direction, Type)</c>. Цвет — по <see cref="NodeType"/>
    /// (<see cref="NodeColors"/>). Ноды РАЗНЫХ типов в одной клетке чуть разнесены по X, чтобы маркеры не слипались в один.
    /// Конфликт «два одинаковых типа в одной клетке» показывается в статусе (сохранение при нём отказывает).
    /// </summary>
    private void AddNodeMarkers(Vector3I footprint, Vector3 previewOffset)
    {
        var nodes = _ui.Nodes;
        string? conflict = FunctionalBlockComponent.FindNodeConflict(nodes);
        if (conflict != null) _ui.SetStatus("nodes: " + conflict);

        foreach (var node in nodes)
        {
            var anchor = FunctionalBlockGeometry.ComputeNodeAnchor(node.Cell, footprint, BuildSpace.CellSize);
            var color = NodeColors.GetValueOrDefault(node.Type, Colors.White);
            float spread = ((int)node.Type - 1) * 0.045f;
            var position = anchor + previewOffset + new Vector3(spread, 0, 0);

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

    /// <summary>Разбирает поле "Behavior params" (JSON-объект; пусто = нет параметров) — компактный JSON без пробелов
    /// между токенами (как его пишет <see cref="BuildXml"/>); false и текст ошибки, если это не объект.</summary>
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

    /// <summary>Добавляет ноду с заглушками по умолчанию: тип Boolean в клетке (0,0,0) — а если там уже есть нода этого типа,
    /// берёт первый ещё не занятый тип (чтобы новая нода не создавала конфликт сразу).</summary>
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

    /// <summary>Каркас-коробка (12 рёбер) от (0,0,0) до <paramref name="extent"/> — тот же визуальный язык, что и
    /// линии каркаса/границ у <see cref="Core.ChunkMesher"/>, минимальная версия только под этот инструмент
    /// (отдельный <see cref="ArrayMesh"/> с примитивом Lines).</summary>
    private static ArrayMesh BuildWireBox(Vector3 extent)
    {
        Vector3 P(int bx, int by, int bz) => new(bx * extent.X, by * extent.Y, bz * extent.Z);
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

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return mesh;
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

        // Не пишем файл с битым JSON в params / конфликтом нод - иначе блок перестал бы грузиться - иначе блок перестал бы грузиться (каталог бросает на ошибке разбора).
        string? nodeConflict = FunctionalBlockComponent.FindNodeConflict(_ui.Nodes);
        if (nodeConflict != null)
        {
            _ui.SetStatus("not saved - " + nodeConflict);
            return;
        }

        if (!TryParseParams(out _, out string paramsError))
        {
            _ui.SetStatus("not saved - behavior params: " + paramsError);
            return;
        }

        string path = $"res://blocks/{slug}.xml";
        string xml = BuildXml(slug);

        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file == null)
        {
            _ui.SetStatus($"failed to write {path}: {FileAccess.GetOpenError()}");
            return;
        }

        file.StoreString(xml);
        _ui.SetStatus($"saved {path} - restart the game (or re-run --import if the scene changed) to see it in the catalog");
    }

    /// <summary>Только символы, безопасные для имени файла и для <c>Block id="..."</c> — то же ограничение, что и
    /// у остальных слагов каталога (см. <c>blocks/*.xml</c>: только латиница/цифры/подчёркивание).</summary>
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

    private string BuildXml(string slug)
    {
        var footprint = _ui.Footprint;
        string colorHex = "#" + _ui.Color.ToHtml(false);

        var functionalFields = new List<string>
        {
            $"\"footprint\": [{footprint.X}, {footprint.Y}, {footprint.Z}]",
        };
        if (!string.IsNullOrEmpty(_ui.Behavior)) functionalFields.Add($"\"behavior\": \"{_ui.Behavior}\"");
        if (_ui.Capacity > 0) functionalFields.Add($"\"capacity\": {_ui.Capacity.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrEmpty(_ui.ScenePath)) functionalFields.Add($"\"scene\": \"{_ui.ScenePath}\"");
        var modelScale = _ui.ModelScale;
        if (!modelScale.IsEqualApprox(Vector3.One))
        {
            functionalFields.Add($"\"modelScale\": [{F(modelScale.X)}, {F(modelScale.Y)}, {F(modelScale.Z)}]");
        }

        var modelOffset = _ui.ModelOffset;
        if (!modelOffset.IsEqualApprox(Vector3.Zero))
        {
            functionalFields.Add($"\"modelOffset\": [{F(modelOffset.X)}, {F(modelOffset.Y)}, {F(modelOffset.Z)}]");
        }

        var collisionBoxes = _ui.CollisionBoxes;
        if (collisionBoxes.Count > 0)
        {
            string boxesJson = string.Join(",\n      ", collisionBoxes.Select(b =>
                $"{{ \"position\": [{F(b.Position.X)}, {F(b.Position.Y)}, {F(b.Position.Z)}], \"size\": [{F(b.Size.X)}, {F(b.Size.Y)}, {F(b.Size.Z)}] }}"));
            functionalFields.Add($"\"collision\": [\n      {boxesJson}\n    ]");
        }

        if (TryParseParams(out string paramsCompact, out _) && paramsCompact.Length > 0)
        {
            functionalFields.Add($"\"params\": {paramsCompact}");
        }

        var ports = _ui.Ports;
        string portsJson = ports.Count == 0
            ? "[]"
            : "[\n      " + string.Join(",\n      ", ports.Select(p =>
                $"{{ \"id\": \"{p.Id}\", \"resource\": \"{p.Resource}\", \"direction\": \"{p.Direction}\", \"face\": \"{p.Face}\", \"position\": [{p.FaceCell.X}, {p.FaceCell.Y}] }}")) + "\n    ]";
        functionalFields.Add($"\"ports\": {portsJson}");

        var nodes = _ui.Nodes;
        if (nodes.Count > 0)
        {
            string nodesJson = "[\n      " + string.Join(",\n      ", nodes.Select(n =>
                $"{{ \"id\": \"{n.Id}\", \"type\": \"{n.Type}\", \"direction\": \"{n.Direction}\", \"position\": [{n.Cell.X}, {n.Cell.Y}, {n.Cell.Z}] }}")) + "\n    ]";
            functionalFields.Add($"\"nodes\": {nodesJson}");
        }

        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        xml.AppendLine($"<Block id=\"{slug}\" name=\"{_ui.Name}\">");
        xml.AppendLine($"  <Color>{colorHex}</Color>");
        xml.AppendLine($"  <Component type=\"BaseComponent\">{{ \"mass\": {F(_ui.Mass)}, \"durability\": {F(_ui.Durability)}, \"damageResistance\": {F(_ui.DamageResistance)} }}</Component>");
        xml.AppendLine("  <Component type=\"FunctionalBlock\">{");
        xml.AppendLine("    " + string.Join(",\n    ", functionalFields));
        xml.AppendLine("  }</Component>");
        xml.AppendLine("</Block>");
        return xml.ToString();
    }

    // double, не float - сборка движка double-precision (real_t = double), Vector3.X/Y/Z имеют тип double здесь,
    // а float (например, _ui.Mass) неявно расширяется до double - одна перегрузка на оба случая.
    private static string F(double value) => value.ToString(CultureInfo.InvariantCulture);
}
