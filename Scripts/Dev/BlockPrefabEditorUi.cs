using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Editor.Ui;

namespace SandboxPolyGame.Dev;

/// <summary>
/// Боковая панель <see cref="BlockPrefabEditor"/> (<c>--blockeditor</c>) — все текстовые поля/кнопки, без 3D-логики
/// (та — в самом <see cref="BlockPrefabEditor"/>). Числовые поля — тот же паттерн, что и у Resize-панели
/// <see cref="ToolbarUi"/>: применяются по Enter/потере фокуса, некорректный ввод откатывается на предыдущее
/// значение, а не падает и не зависает в невалидном состоянии.
/// </summary>
public sealed class BlockPrefabEditorUi
{
    public event Action<string>? LoadRequested;
    public event Action? NewRequested;
    public event Action? SaveRequested;
    public event Action? FieldsChanged; // любое поле (кроме боксов коллизии) изменилось - призыв пересчитать превью
    public event Action? ReloadSceneRequested;
    public event Action? AddCollisionBoxRequested;
    public event Action<int>? RemoveCollisionBoxRequested;
    public event Action? CollisionBoxesChanged;
    public event Action? AddPortRequested;
    public event Action<int>? RemovePortRequested;
    public event Action? PortsChanged;
    public event Action? AddNodeRequested;
    public event Action<int>? RemoveNodeRequested;
    public event Action? NodesChanged;

    /// <summary>
    /// Фокус вошёл в любое редактируемое поле (текст/дропдаун) — ПЕРЕД тем, как пользователь успел что-то в нём
    /// поменять (см. <see cref="BindUndoCapture"/>). <see cref="BlockPrefabEditor"/> снимает снэпшот ДО изменения
    /// именно по этому событию, а не по <see cref="FieldsChanged"/>/<see cref="CollisionBoxesChanged"/>/
    /// <see cref="PortsChanged"/> — те стреляют уже ПОСЛЕ того, как Godot применил новое значение к полю (по Enter/
    /// потере фокуса), то есть "состояние до правки" к этому моменту уже потеряно.
    /// </summary>
    public event Action? EditSessionStarting;

    private readonly LineEdit _slugField;
    private readonly LineEdit _nameField;
    private readonly LineEdit _colorField;
    private readonly ColorRect _colorSwatch;
    private readonly LineEdit _massField;
    private readonly LineEdit _durabilityField;
    private readonly LineEdit _damageField;
    private readonly LineEdit _sceneField;
    private readonly LineEdit[] _footprintFields = new LineEdit[3];
    private readonly LineEdit[] _modelScaleFields = new LineEdit[3];
    private readonly LineEdit[] _modelOffsetFields = new LineEdit[3];
    private readonly LineEdit _behaviorField;
    private readonly LineEdit _capacityField;
    private readonly TextEdit _paramsField;
    private readonly VBoxContainer _nodeList;
    private readonly VBoxContainer _collisionList;
    private readonly VBoxContainer _portList;
    private readonly Label _status;

    private readonly List<(LineEdit[] Position, LineEdit[] Size)> _collisionRows = new();
    private bool _suppressCollisionEvents;

    private readonly List<(LineEdit Id, OptionButton Resource, OptionButton Direction, OptionButton Face, LineEdit[] FaceCell)> _portRows = new();
    private bool _suppressPortEvents;

    private readonly List<(LineEdit Id, OptionButton Type, OptionButton Direction, LineEdit[] Cell)> _nodeRows = new();
    private bool _suppressNodeEvents;

    private static readonly ResourceType[] ResourceValues = (ResourceType[])Enum.GetValues(typeof(ResourceType));
    private static readonly PortDirection[] DirectionValues = (PortDirection[])Enum.GetValues(typeof(PortDirection));
    private static readonly BlockFace[] FaceValues = (BlockFace[])Enum.GetValues(typeof(BlockFace));
    private static readonly NodeType[] NodeTypeValues = (NodeType[])Enum.GetValues(typeof(NodeType));

    public BlockPrefabEditorUi(Control layerRoot)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(14f));
        panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        panel.Position = new Vector2(16, 16);
        panel.CustomMinimumSize = new Vector2(520, 0);

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(520, 900) };
        panel.AddChild(scroll);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(column);

        column.AddChild(UiStyle.MakeLabel("Block Prefab Editor (--blockeditor)", 18));
        var hint = UiStyle.MakeLabel(
            "WASD/Q/E + зажатая СКМ - камера, колесо - зум. Фокус с полей снимается автоматически при клике в " +
            "3D-вид, чтобы WASD не уходило в текст. Числа применяются по Enter/потере фокуса.", 12, UiStyle.TextDim);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(hint);

        column.AddChild(Separator());
        var slugRow = new HBoxContainer();
        slugRow.AddChild(UiStyle.MakeLabel("Slug", 13));
        _slugField = new LineEdit { CustomMinimumSize = new Vector2(180, 28), PlaceholderText = "electric_motor" };
        slugRow.AddChild(_slugField);
        var loadButton = UiStyle.MakeButton("Load", new Vector2(70, 28));
        loadButton.Pressed += () => LoadRequested?.Invoke(_slugField.Text.Trim());
        slugRow.AddChild(loadButton);
        var newButton = UiStyle.MakeButton("New", new Vector2(70, 28));
        newButton.Pressed += () => NewRequested?.Invoke();
        slugRow.AddChild(newButton);
        column.AddChild(slugRow);

        _nameField = AddTextRow(column, "Name", "Electric Motor");
        (_colorField, _colorSwatch) = AddColorRow(column, "#ffffff");

        column.AddChild(Separator());
        column.AddChild(UiStyle.MakeLabel("BaseComponent", 13, UiStyle.TextDim));
        _massField = AddNumericRow(column, "Mass", "10");
        _durabilityField = AddNumericRow(column, "Durability", "100");
        _damageField = AddNumericRow(column, "DamageResistance", "0.1");

        column.AddChild(Separator());
        column.AddChild(UiStyle.MakeLabel("Model", 13, UiStyle.TextDim));
        var sceneRow = new HBoxContainer();
        _sceneField = new LineEdit { CustomMinimumSize = new Vector2(260, 28), PlaceholderText = "res://meshes/....glb" };
        _sceneField.TextSubmitted += _ => ReloadSceneRequested?.Invoke();
        _sceneField.FocusExited += () => ReloadSceneRequested?.Invoke();
        sceneRow.AddChild(_sceneField);
        var browseButton = UiStyle.MakeButton("Browse...", new Vector2(90, 28));
        browseButton.Pressed += BrowseForModel;
        sceneRow.AddChild(browseButton);
        var reloadButton = UiStyle.MakeButton("Reload", new Vector2(70, 28));
        reloadButton.Pressed += () => ReloadSceneRequested?.Invoke();
        sceneRow.AddChild(reloadButton);
        column.AddChild(sceneRow);

        var footprintRow = new HBoxContainer();
        footprintRow.AddChild(UiStyle.MakeLabel("Footprint (клетки)", 13));
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new LineEdit { CustomMinimumSize = new Vector2(50, 28), Text = "1" };
            field.TextSubmitted += _ => FieldsChanged?.Invoke();
            field.FocusExited += () => FieldsChanged?.Invoke();
            footprintRow.AddChild(field);
            _footprintFields[axis] = field;
        }
        column.AddChild(footprintRow);

        var scaleRow = new HBoxContainer();
        scaleRow.AddChild(UiStyle.MakeLabel("Model stretch X/Y/Z", 13));
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new LineEdit { CustomMinimumSize = new Vector2(50, 28), Text = "1" };
            field.TextSubmitted += _ => FieldsChanged?.Invoke();
            field.FocusExited += () => FieldsChanged?.Invoke();
            scaleRow.AddChild(field);
            _modelScaleFields[axis] = field;
        }
        column.AddChild(scaleRow);
        var scaleHint = UiStyle.MakeLabel(
            "Множитель поверх автоматической подгонки (1 = как в игре по умолчанию) - растянуть модель вручную " +
            "под границы хитбокса, если автоматическая подгонка не годится.", 11, UiStyle.TextDim);
        scaleHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(scaleHint);

        var offsetRow = new HBoxContainer();
        offsetRow.AddChild(UiStyle.MakeLabel("Model offset X/Y/Z (м)", 13));
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new LineEdit { CustomMinimumSize = new Vector2(60, 28), Text = "0" };
            field.TextSubmitted += _ => FieldsChanged?.Invoke();
            field.FocusExited += () => FieldsChanged?.Invoke();
            offsetRow.AddChild(field);
            _modelOffsetFields[axis] = field;
        }
        column.AddChild(offsetRow);
        var offsetHint = UiStyle.MakeLabel(
            "Сдвиг модели внутри клетки в метрах (оси неповёрнутого блока, 0 = по центру габарита модели) - чтобы " +
            "асимметричная модель (угловая труба) совпала с центрами клетки. Коллизию и порты не двигает.", 11, UiStyle.TextDim);
        offsetHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(offsetHint);

        column.AddChild(Separator());
        _behaviorField = AddTextRow(column, "Behavior", "");
        _capacityField = AddNumericRow(column, "Capacity", "0");
        _paramsField = AddJsonField(column,
            "Behavior params (JSON-объект, пусто = нет; у Button: \"mode\" momentary|toggle, \"glowNode\" имя узла-крышки, " +
            "\"glowColor\" #rrggbb)", 66);

        column.AddChild(Separator());
        var portsHeader = new HBoxContainer();
        var portsHeaderLabel = UiStyle.MakeLabel("Ports (ФИЗИЧЕСКИЕ: вал Torque / труба Fluid; несколько портов могут сидеть в одном месте)", 13, UiStyle.TextDim);
        portsHeaderLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        portsHeaderLabel.CustomMinimumSize = new Vector2(360, 0);
        portsHeader.AddChild(portsHeaderLabel);
        var addPortButton = UiStyle.MakeButton("+ Add port", new Vector2(100, 26));
        addPortButton.Pressed += () => AddPortRequested?.Invoke();
        portsHeader.AddChild(addPortButton);
        column.AddChild(portsHeader);

        _portList = new VBoxContainer();
        _portList.AddThemeConstantOverride("separation", 10);
        column.AddChild(_portList);

        column.AddChild(Separator());
        var nodesHeader = new HBoxContainer();
        var nodesHeaderLabel = UiStyle.MakeLabel(
            "Nodes (логика: Electricity/Boolean/Number; сидят в ЦЕНТРЕ клетки блока; в одной клетке - только разные типы)", 13, UiStyle.TextDim);
        nodesHeaderLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        nodesHeaderLabel.CustomMinimumSize = new Vector2(360, 0);
        nodesHeader.AddChild(nodesHeaderLabel);
        var addNodeButton = UiStyle.MakeButton("+ Add node", new Vector2(100, 26));
        addNodeButton.Pressed += () => AddNodeRequested?.Invoke();
        nodesHeader.AddChild(addNodeButton);
        column.AddChild(nodesHeader);

        _nodeList = new VBoxContainer();
        _nodeList.AddThemeConstantOverride("separation", 10);
        column.AddChild(_nodeList);

        column.AddChild(Separator());
        var collisionHeader = new HBoxContainer();
        collisionHeader.AddChild(UiStyle.MakeLabel("Collision boxes (метры, локально для блока; пусто = нет коллизии)", 13, UiStyle.TextDim));
        var addBoxButton = UiStyle.MakeButton("+ Add box", new Vector2(100, 26));
        addBoxButton.Pressed += () => AddCollisionBoxRequested?.Invoke();
        collisionHeader.AddChild(addBoxButton);
        column.AddChild(collisionHeader);

        _collisionList = new VBoxContainer();
        _collisionList.AddThemeConstantOverride("separation", 10);
        column.AddChild(_collisionList);

        column.AddChild(Separator());
        var saveButton = UiStyle.MakeButton("Save to blocks/<slug>.xml", new Vector2(480, 36));
        saveButton.Pressed += () => SaveRequested?.Invoke();
        column.AddChild(saveButton);

        _status = UiStyle.MakeLabel("", 12, UiStyle.TextDim);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_status);

        layerRoot.AddChild(panel);

        // Каждое базовое поле (кроме slug/collision, у которых своя логика) помечает превью как "грязное" И снимает
        // снэпшот для Undo/Redo (см. BindUndoCapture) ПРИ ВХОДЕ фокуса в поле - до того, как значение изменится.
        foreach (var field in new[] { _nameField, _colorField, _massField, _durabilityField, _damageField, _behaviorField, _capacityField })
        {
            field.TextSubmitted += _ => FieldsChanged?.Invoke();
            field.FocusExited += () => FieldsChanged?.Invoke();
            BindUndoCapture(field);
        }
        BindUndoCapture(_sceneField);
        _paramsField.FocusExited += () => FieldsChanged?.Invoke(); // многострочное поле: Enter - новая строка, применяем по потере фокуса
        BindUndoCapture(_paramsField);
        foreach (var field in _footprintFields) BindUndoCapture(field);
        foreach (var field in _modelScaleFields) BindUndoCapture(field);
        foreach (var field in _modelOffsetFields) BindUndoCapture(field);
    }

    /// <summary>Снимает Undo-снэпшот, когда фокус ВХОДИТ в это поле/дропдаун — ДО того, как пользователь успеет что-то
    /// в нём поменять (см. <see cref="EditSessionStarting"/> doc про то, почему не на событии "значение изменилось").</summary>
    private void BindUndoCapture(Control control) => control.FocusEntered += () => EditSessionStarting?.Invoke();

    /// <summary>
    /// Открывает НАТИВНЫЙ диалог выбора файла ОС (<see cref="DisplayServer.FileDialogShow"/> — на Windows это и есть
    /// обычный проводник, а не свой `Control`-диалог Godot) для выбора `.glb`/`.gltf`, по запросу пользователя
    /// ("подгрузка моделей через проводник Windows"), вместо печати пути в <see cref="_sceneField"/> руками.
    /// <para/>
    /// Выбранный файл ВНЕ папки проекта скопировать НЕЛЬЗЯ сослаться на него через <c>res://</c> напрямую (движок
    /// пакует только то, что лежит внутри проекта) — поэтому такой файл копируется в <c>res://meshes/</c>
    /// (<see cref="DirAccess.CopyAbsolute"/>, тем же именем файла, перезаписывая одноимённый, если уже есть). Файл,
    /// уже лежащий где-то ВНУТРИ проекта, просто используется по месту (<see cref="ProjectSettings.LocalizePath"/>
    /// возвращает для него настоящий <c>res://...</c>, без копирования).
    /// <para/>
    /// Модель видна в превью СРАЗУ, без `--import`/перезапуска — <see cref="FunctionalBlockGeometry.GetOrLoadScene"/>
    /// при отсутствии кэша импорта (свежескопированный/никогда не импортировавшийся файл) читает `.glb`/`.gltf`
    /// напрямую через <see cref="GltfDocument"/>, в обход конвейера импорта редактора (см. его doc-комментарий).
    /// </summary>
    private void BrowseForModel()
    {
        if (!DisplayServer.HasFeature(DisplayServer.Feature.NativeDialogFile))
        {
            SetStatus("native file dialog not supported on this platform/display server - type the res:// path manually");
            return;
        }

        string startDir = ProjectSettings.GlobalizePath("res://meshes");
        var filters = new[] { "*.glb,*.gltf;3D Model (glTF)" };

        DisplayServer.FileDialogShow(
            "Select a 3D model (.glb/.gltf)", startDir, "", false, DisplayServer.FileDialogMode.OpenFile, filters,
            Callable.From((bool status, string[] selectedPaths, long _) =>
            {
                if (!status || selectedPaths.Length == 0) return;
                ApplyChosenModelPath(selectedPaths[0]);
            }));
    }

    private void ApplyChosenModelPath(string nativePath)
    {
        string localized = ProjectSettings.LocalizePath(nativePath);
        string resPath;
        bool copied = false;

        if (localized.StartsWith("res://"))
        {
            resPath = localized;
        }
        else
        {
            // Файл вне проекта - res:// на него сослаться не может, копируем в meshes/ под тем же именем.
            string fileName = nativePath.Replace('\\', '/').Split('/')[^1];
            resPath = $"res://meshes/{fileName}";
            var err = DirAccess.CopyAbsolute(nativePath, resPath);
            if (err != Error.Ok)
            {
                SetStatus($"failed to copy '{nativePath}' into res://meshes/: {err}");
                return;
            }

            copied = true;
        }

        // Порядок важен: ставим СВОЙ статус ДО перезагрузки, а не после - если сцена реально не прочитается (битый
        // файл и т.п.), RefreshPreview сам перезапишет его настоящей ошибкой ("scene not found/failed to load: ...",
        // см. Dev.BlockPrefabEditor.RefreshPreview) - наш "скопировано"/"установлено" не должен эту ошибку скрывать.
        _sceneField.Text = resPath;
        SetStatus(copied ? $"copied into {resPath}" : $"model set to {resPath}");
        ReloadSceneRequested?.Invoke();
    }

    private static Control Separator() => new HSeparator { CustomMinimumSize = new Vector2(0, 4) };

    private static LineEdit AddTextRow(VBoxContainer column, string label, string defaultValue)
    {
        var row = new HBoxContainer();
        row.AddChild(UiStyle.MakeLabel(label, 13));
        var field = new LineEdit { CustomMinimumSize = new Vector2(300, 28), Text = defaultValue };
        row.AddChild(field);
        column.AddChild(row);
        return field;
    }

    /// <summary>Подпись + многострочное поле для сырого JSON (параметры поведения, сигнальные порты) — применяется по потере
    /// фокуса (Enter в многострочном поле — перенос строки, не "применить").</summary>
    private static TextEdit AddJsonField(VBoxContainer column, string label, float height)
    {
        var caption = UiStyle.MakeLabel(label, 12, UiStyle.TextDim);
        caption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(caption);

        var field = new TextEdit
        {
            CustomMinimumSize = new Vector2(480, height),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
        };
        column.AddChild(field);
        return field;
    }

    private static LineEdit AddNumericRow(VBoxContainer column, string label, string defaultValue)
    {
        var row = new HBoxContainer();
        row.AddChild(UiStyle.MakeLabel(label, 13));
        var field = new LineEdit { CustomMinimumSize = new Vector2(100, 28), Text = defaultValue };
        row.AddChild(field);
        column.AddChild(row);
        return field;
    }

    private static (LineEdit, ColorRect) AddColorRow(VBoxContainer column, string defaultHex)
    {
        var row = new HBoxContainer();
        row.AddChild(UiStyle.MakeLabel("Color", 13));
        var field = new LineEdit { CustomMinimumSize = new Vector2(100, 28), Text = defaultHex };
        row.AddChild(field);
        var swatch = new ColorRect { CustomMinimumSize = new Vector2(28, 28), Color = Colors.White };
        row.AddChild(swatch);
        field.TextChanged += text =>
        {
            try { swatch.Color = Color.FromHtml(text); }
            catch { /* промежуточный невалидный ввод во время печати - просто не обновляем образец */ }
        };
        column.AddChild(row);
        return (field, swatch);
    }

    // ------------------------------------------------------------------ чтение полей

    public string Slug => _slugField.Text.Trim();

    /// <summary>Для самотестов — меняет только слаг (остальные поля через <see cref="LoadFields"/>), т.к. в обычном
    /// UI слаг печатается руками, а не задаётся программно.</summary>
    public void SetSlugForTesting(string slug) => _slugField.Text = slug;

    /// <summary>Для самотестов — меняет только "Model offset" (см. <see cref="ModelOffset"/>), не трогая остальные поля.</summary>
    public void SetModelOffsetForTesting(Vector3 offset)
    {
        for (int axis = 0; axis < 3; axis++) _modelOffsetFields[axis].Text = offset[axis].ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Для самотестов — меняет только "Model stretch" (см. <see cref="ModelScale"/>), не трогая остальные поля.</summary>
    public void SetModelScaleForTesting(Vector3 scale)
    {
        for (int axis = 0; axis < 3; axis++) _modelScaleFields[axis].Text = scale[axis].ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Для самотестов — заполняет поля УЖЕ СУЩЕСТВУЮЩЕГО ряда порта (добавленного через
    /// <see cref="AddPortRequested"/>/<see cref="SetPortRows"/>) напрямую, без события <see cref="PortsChanged"/> —
    /// обычный UI правит их руками по одному полю за раз, тестам нужно выставить все сразу.</summary>
    public void SetPortForTesting(int index, string id, ResourceType resource, PortDirection direction, BlockFace face, Vector2I faceCell)
    {
        var row = _portRows[index];
        row.Id.Text = id;
        row.Resource.Selected = Array.IndexOf(ResourceValues, resource);
        row.Direction.Selected = Array.IndexOf(DirectionValues, direction);
        row.Face.Selected = Array.IndexOf(FaceValues, face);
        row.FaceCell[0].Text = faceCell.X.ToString(CultureInfo.InvariantCulture);
        row.FaceCell[1].Text = faceCell.Y.ToString(CultureInfo.InvariantCulture);
    }

    public string Name => string.IsNullOrWhiteSpace(_nameField.Text) ? Slug : _nameField.Text.Trim();
    public string ScenePath => _sceneField.Text.Trim();
    public string Behavior => _behaviorField.Text.Trim();

    public Color Color => TryParseColor(_colorField.Text, out var color) ? color : Colors.White;

    public Vector3I Footprint => new(
        ParseIntOr(_footprintFields[0].Text, 1),
        ParseIntOr(_footprintFields[1].Text, 1),
        ParseIntOr(_footprintFields[2].Text, 1));

    /// <summary>Ручной множитель поверх автоматической подгонки модели (см. <see cref="FunctionalBlockComponent.ModelScale"/>) —
    /// (1,1,1) по умолчанию, т.е. поведение не отличается от прежнего, пока пользователь не растянул модель сам.</summary>
    public Vector3 ModelScale => new(
        ParseFloatOr(_modelScaleFields[0].Text, 1f),
        ParseFloatOr(_modelScaleFields[1].Text, 1f),
        ParseFloatOr(_modelScaleFields[2].Text, 1f));

    /// <summary>Ручной сдвиг модели в клетке, метры (см. <see cref="FunctionalBlockComponent.ModelOffset"/>) - (0,0,0) по умолчанию.</summary>
    public Vector3 ModelOffset => new(
        ParseFloatOr(_modelOffsetFields[0].Text, 0f),
        ParseFloatOr(_modelOffsetFields[1].Text, 0f),
        ParseFloatOr(_modelOffsetFields[2].Text, 0f));

    public float Mass => ParseFloatOr(_massField.Text, 10f);
    public float Durability => ParseFloatOr(_durabilityField.Text, 100f);
    public float DamageResistance => ParseFloatOr(_damageField.Text, 0.1f);
    public float Capacity => ParseFloatOr(_capacityField.Text, 0f);

    /// <summary>Сырой текст поля "Behavior params" (JSON-объект или пусто) — разбор и проверка в <see cref="BlockPrefabEditor"/>.</summary>
    public string ParamsJson => _paramsField.Text.Trim();

    /// <summary>Для самотестов — задаёт только параметры поведения (JSON-текст), не трогая остальные поля.</summary>
    public void SetParamsForTesting(string paramsJson) => _paramsField.Text = paramsJson;

    /// <summary>Для самотестов — заполняет поля УЖЕ СУЩЕСТВУЮЩЕГО ряда ноды (добавленного через
    /// <see cref="AddNodeRequested"/>/<see cref="SetNodeRows"/>) напрямую, без события <see cref="NodesChanged"/>.</summary>
    public void SetNodeForTesting(int index, string id, NodeType type, PortDirection direction, Vector3I cell)
    {
        var row = _nodeRows[index];
        row.Id.Text = id;
        row.Type.Selected = Array.IndexOf(NodeTypeValues, type);
        row.Direction.Selected = Array.IndexOf(DirectionValues, direction);
        for (int axis = 0; axis < 3; axis++) row.Cell[axis].Text = cell[axis].ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Логические ноды — собираются из живых полей рядов при каждом обращении (как <see cref="Ports"/>).</summary>
    public IReadOnlyList<LogicNode> Nodes
    {
        get
        {
            var result = new List<LogicNode>(_nodeRows.Count);
            foreach (var row in _nodeRows)
            {
                result.Add(new LogicNode
                {
                    Id = row.Id.Text.Trim(),
                    Type = NodeTypeValues[row.Type.Selected],
                    Direction = DirectionValues[row.Direction.Selected],
                    Cell = new Vector3I(ParseIntOrZero(row.Cell[0].Text), ParseIntOrZero(row.Cell[1].Text), ParseIntOrZero(row.Cell[2].Text)),
                });
            }

            return result;
        }
    }

    public IReadOnlyList<CollisionBox> CollisionBoxes
    {
        get
        {
            var result = new List<CollisionBox>(_collisionRows.Count);
            foreach (var row in _collisionRows)
            {
                result.Add(new CollisionBox
                {
                    Position = new Vector3(ParseFloatOr(row.Position[0].Text, 0), ParseFloatOr(row.Position[1].Text, 0), ParseFloatOr(row.Position[2].Text, 0)),
                    Size = new Vector3(ParseFloatOr(row.Size[0].Text, 0.25f), ParseFloatOr(row.Size[1].Text, 0.25f), ParseFloatOr(row.Size[2].Text, 0.25f)),
                });
            }

            return result;
        }
    }

    /// <summary>Та же "жить читается из живых полей" идея, что и у <see cref="CollisionBoxes"/> — порт собирается
    /// заново при каждом обращении, УЖЕ ОТРАЖАЯ поля ряда (Id/Resource/Direction/Face/FaceCell).</summary>
    public IReadOnlyList<ResourcePort> Ports
    {
        get
        {
            var result = new List<ResourcePort>(_portRows.Count);
            foreach (var row in _portRows)
            {
                result.Add(new ResourcePort
                {
                    Id = row.Id.Text.Trim(),
                    Resource = ResourceValues[row.Resource.Selected],
                    Direction = DirectionValues[row.Direction.Selected],
                    Face = FaceValues[row.Face.Selected],
                    FaceCell = new Vector2I(ParseIntOrZero(row.FaceCell[0].Text), ParseIntOrZero(row.FaceCell[1].Text)),
                });
            }

            return result;
        }
    }

    // ------------------------------------------------------------------ запись полей (загрузка существующего/нового блока)

    public void LoadFields(string slug, string name, Color color, float mass, float durability, float damage,
        string scenePath, Vector3I footprint, Vector3 modelScale, Vector3 modelOffset, string behavior, float capacity,
        IReadOnlyList<CollisionBox> collisionBoxes, IReadOnlyList<ResourcePort> ports, IReadOnlyList<LogicNode> nodes, string paramsJson)
    {
        _slugField.Text = slug;
        _nameField.Text = name;
        _colorField.Text = "#" + color.ToHtml(false);
        _colorSwatch.Color = color;
        _massField.Text = mass.ToString(CultureInfo.InvariantCulture);
        _durabilityField.Text = durability.ToString(CultureInfo.InvariantCulture);
        _damageField.Text = damage.ToString(CultureInfo.InvariantCulture);
        _sceneField.Text = scenePath;
        for (int axis = 0; axis < 3; axis++) _footprintFields[axis].Text = footprint[axis].ToString(CultureInfo.InvariantCulture);
        for (int axis = 0; axis < 3; axis++) _modelScaleFields[axis].Text = modelScale[axis].ToString(CultureInfo.InvariantCulture);
        for (int axis = 0; axis < 3; axis++) _modelOffsetFields[axis].Text = modelOffset[axis].ToString(CultureInfo.InvariantCulture);
        _behaviorField.Text = behavior;
        _capacityField.Text = capacity.ToString(CultureInfo.InvariantCulture);
        _paramsField.Text = paramsJson;

        SetCollisionBoxRows(collisionBoxes);
        SetPortRows(ports);
        SetNodeRows(nodes);
    }

    /// <summary>Перестраивает список рядов коллизии с нуля — вызывается при загрузке блока и при Add/Remove
    /// (сами значения полей читаются геттером <see cref="CollisionBoxes"/>, этот метод только меняет их КОЛИЧЕСТВО/
    /// начальные значения).</summary>
    public void SetCollisionBoxRows(IReadOnlyList<CollisionBox> boxes)
    {
        _suppressCollisionEvents = true;
        foreach (var child in _collisionList.GetChildren()) child.QueueFree();
        _collisionRows.Clear();

        for (int i = 0; i < boxes.Count; i++) AddCollisionRow(boxes[i], i);

        _suppressCollisionEvents = false;
    }

    /// <summary>Каждая запись — СВОИ две строки (pos/size), не одна длинная строка на 6 полей + ярлыки — раньше не
    /// помещалось по ширине панели (баг, найденный пользователем).</summary>
    private void AddCollisionRow(CollisionBox box, int index)
    {
        var entry = new VBoxContainer();
        entry.AddThemeConstantOverride("separation", 2);

        var header = new HBoxContainer();
        header.AddChild(UiStyle.MakeLabel($"Box #{index}", 12, UiStyle.TextDim));
        int capturedIndex = index;
        var removeButton = UiStyle.MakeButton("Remove", new Vector2(70, 24));
        removeButton.Pressed += () => RemoveCollisionBoxRequested?.Invoke(capturedIndex);
        header.AddChild(removeButton);
        entry.AddChild(header);

        var positionFields = new LineEdit[3];
        var sizeFields = new LineEdit[3];

        var posRow = new HBoxContainer();
        posRow.AddChild(UiStyle.MakeLabel("pos", 11, UiStyle.TextDim));
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new LineEdit { CustomMinimumSize = new Vector2(60, 26), Text = box.Position[axis].ToString(CultureInfo.InvariantCulture) };
            field.TextSubmitted += _ => NotifyCollisionChanged();
            field.FocusExited += () => NotifyCollisionChanged();
            BindUndoCapture(field);
            posRow.AddChild(field);
            positionFields[axis] = field;
        }
        entry.AddChild(posRow);

        var sizeRow = new HBoxContainer();
        sizeRow.AddChild(UiStyle.MakeLabel("size", 11, UiStyle.TextDim));
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new LineEdit { CustomMinimumSize = new Vector2(60, 26), Text = box.Size[axis].ToString(CultureInfo.InvariantCulture) };
            field.TextSubmitted += _ => NotifyCollisionChanged();
            field.FocusExited += () => NotifyCollisionChanged();
            BindUndoCapture(field);
            sizeRow.AddChild(field);
            sizeFields[axis] = field;
        }
        entry.AddChild(sizeRow);

        _collisionList.AddChild(entry);
        _collisionList.AddChild(new HSeparator());
        _collisionRows.Add((positionFields, sizeFields));
    }

    private void NotifyCollisionChanged()
    {
        if (!_suppressCollisionEvents) CollisionBoxesChanged?.Invoke();
    }

    // ------------------------------------------------------------------ ресурсные порты (тот же приём "список рядов", что и коллизия выше)

    /// <summary>Перестраивает список рядов портов с нуля — тот же приём, что и <see cref="SetCollisionBoxRows"/>.</summary>
    public void SetPortRows(IReadOnlyList<ResourcePort> ports)
    {
        _suppressPortEvents = true;
        foreach (var child in _portList.GetChildren()) child.QueueFree();
        _portRows.Clear();

        for (int i = 0; i < ports.Count; i++) AddPortRow(ports[i], i);

        _suppressPortEvents = false;
    }

    private static OptionButton MakeEnumDropdown(IEnumerable<string> names, int selectedIndex)
    {
        var dropdown = new OptionButton { CustomMinimumSize = new Vector2(110, 26) };
        foreach (string name in names) dropdown.AddItem(name);
        dropdown.Selected = selectedIndex;
        return dropdown;
    }

    private void AddPortRow(ResourcePort port, int index)
    {
        var entry = new VBoxContainer();
        entry.AddThemeConstantOverride("separation", 2);

        var header = new HBoxContainer();
        header.AddChild(UiStyle.MakeLabel($"Port #{index}", 12, UiStyle.TextDim));
        int capturedIndex = index;
        var removeButton = UiStyle.MakeButton("Remove", new Vector2(70, 24));
        removeButton.Pressed += () => RemovePortRequested?.Invoke(capturedIndex);
        header.AddChild(removeButton);
        entry.AddChild(header);

        var idRow = new HBoxContainer();
        idRow.AddChild(UiStyle.MakeLabel("id", 11, UiStyle.TextDim));
        var idField = new LineEdit { CustomMinimumSize = new Vector2(140, 26), Text = port.Id };
        idField.TextSubmitted += _ => NotifyPortChanged();
        idField.FocusExited += () => NotifyPortChanged();
        BindUndoCapture(idField);
        idRow.AddChild(idField);

        var resourceDropdown = MakeEnumDropdown(ResourceValues.Select(r => r.ToString()), Array.IndexOf(ResourceValues, port.Resource));
        resourceDropdown.ItemSelected += _ => NotifyPortChanged();
        BindUndoCapture(resourceDropdown);
        idRow.AddChild(resourceDropdown);

        var directionDropdown = MakeEnumDropdown(DirectionValues.Select(d => d.ToString()), Array.IndexOf(DirectionValues, port.Direction));
        directionDropdown.ItemSelected += _ => NotifyPortChanged();
        BindUndoCapture(directionDropdown);
        idRow.AddChild(directionDropdown);
        entry.AddChild(idRow);

        var placementRow = new HBoxContainer();
        placementRow.AddChild(UiStyle.MakeLabel("face", 11, UiStyle.TextDim));
        var faceDropdown = MakeEnumDropdown(FaceValues.Select(f => f.ToString()), Array.IndexOf(FaceValues, port.Face));
        faceDropdown.ItemSelected += _ => NotifyPortChanged();
        BindUndoCapture(faceDropdown);
        placementRow.AddChild(faceDropdown);

        placementRow.AddChild(UiStyle.MakeLabel("cell", 11, UiStyle.TextDim));
        var faceCellFields = new LineEdit[2];
        for (int axis = 0; axis < 2; axis++)
        {
            var field = new LineEdit { CustomMinimumSize = new Vector2(44, 26), Text = port.FaceCell[axis].ToString(CultureInfo.InvariantCulture) };
            field.TextSubmitted += _ => NotifyPortChanged();
            field.FocusExited += () => NotifyPortChanged();
            BindUndoCapture(field);
            placementRow.AddChild(field);
            faceCellFields[axis] = field;
        }
        entry.AddChild(placementRow);

        _portList.AddChild(entry);
        _portList.AddChild(new HSeparator());
        _portRows.Add((idField, resourceDropdown, directionDropdown, faceDropdown, faceCellFields));
    }

    private void NotifyPortChanged()
    {
        if (!_suppressPortEvents) PortsChanged?.Invoke();
    }

    // ------------------------------------------------------------------ логические ноды (тот же приём "список рядов", что и порты)

    /// <summary>Перестраивает список рядов нод с нуля — тот же приём, что и <see cref="SetPortRows"/>.</summary>
    public void SetNodeRows(IReadOnlyList<LogicNode> nodes)
    {
        _suppressNodeEvents = true;
        foreach (var child in _nodeList.GetChildren()) child.QueueFree();
        _nodeRows.Clear();

        for (int i = 0; i < nodes.Count; i++) AddNodeRow(nodes[i], i);

        _suppressNodeEvents = false;
    }

    private void AddNodeRow(LogicNode node, int index)
    {
        var entry = new VBoxContainer();
        entry.AddThemeConstantOverride("separation", 2);

        var header = new HBoxContainer();
        header.AddChild(UiStyle.MakeLabel($"Node #{index}", 12, UiStyle.TextDim));
        int capturedIndex = index;
        var removeButton = UiStyle.MakeButton("Remove", new Vector2(70, 24));
        removeButton.Pressed += () => RemoveNodeRequested?.Invoke(capturedIndex);
        header.AddChild(removeButton);
        entry.AddChild(header);

        var idRow = new HBoxContainer();
        idRow.AddChild(UiStyle.MakeLabel("id", 11, UiStyle.TextDim));
        var idField = new LineEdit { CustomMinimumSize = new Vector2(140, 26), Text = node.Id };
        idField.TextSubmitted += _ => NotifyNodeChanged();
        idField.FocusExited += () => NotifyNodeChanged();
        BindUndoCapture(idField);
        idRow.AddChild(idField);

        var typeDropdown = MakeEnumDropdown(NodeTypeValues.Select(t => t.ToString()), Array.IndexOf(NodeTypeValues, node.Type));
        typeDropdown.ItemSelected += _ => NotifyNodeChanged();
        BindUndoCapture(typeDropdown);
        idRow.AddChild(typeDropdown);

        var directionDropdown = MakeEnumDropdown(DirectionValues.Select(d => d.ToString()), Array.IndexOf(DirectionValues, node.Direction));
        directionDropdown.ItemSelected += _ => NotifyNodeChanged();
        BindUndoCapture(directionDropdown);
        idRow.AddChild(directionDropdown);
        entry.AddChild(idRow);

        var cellRow = new HBoxContainer();
        cellRow.AddChild(UiStyle.MakeLabel("cell x/y/z", 11, UiStyle.TextDim));
        var cellFields = new LineEdit[3];
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new LineEdit { CustomMinimumSize = new Vector2(44, 26), Text = node.Cell[axis].ToString(CultureInfo.InvariantCulture) };
            field.TextSubmitted += _ => NotifyNodeChanged();
            field.FocusExited += () => NotifyNodeChanged();
            BindUndoCapture(field);
            cellRow.AddChild(field);
            cellFields[axis] = field;
        }
        entry.AddChild(cellRow);

        _nodeList.AddChild(entry);
        _nodeList.AddChild(new HSeparator());
        _nodeRows.Add((idField, typeDropdown, directionDropdown, cellFields));
    }

    private void NotifyNodeChanged()
    {
        if (!_suppressNodeEvents) NodesChanged?.Invoke();
    }

    public void SetStatus(string message) => _status.Text = message;

    private static bool TryParseColor(string text, out Color color)
    {
        try { color = Color.FromHtml(text); return true; }
        catch { color = Colors.White; return false; }
    }

    private static int ParseIntOr(string text, int fallback) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0 ? value : fallback;

    /// <summary>Как <see cref="ParseIntOr"/>, но без "floor at 1" — координата клетки на грани (<see cref="ResourcePort.FaceCell"/>)
    /// законно равна 0 (угол грани), в отличие от footprint'а/размера, у которых 0 бессмысленно.</summary>
    private static int ParseIntOrZero(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static float ParseFloatOr(string text, float fallback) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
}
