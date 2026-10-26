using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Editor.Ui;

namespace SandboxPolyGame.Dev;

/// <summary>
/// Боковая панель <see cref="BlockEditor"/> (<c>--blockeditor</c>) — все текстовые поля/кнопки, без 3D-логики (та — в самом
/// <see cref="BlockEditor"/>). Числовые поля — тот же паттерн, что и у Resize-панели <see cref="ToolbarUi"/>: применяются по
/// Enter/потере фокуса, некорректный ввод заменяется значением по умолчанию, а не падает и не зависает в невалидном состоянии.
/// Footprint здесь НЕ поле: он считается автоматически из bbox модели (см. <see cref="BlockModelLayout.ComputeFootprint"/>) и
/// только показывается (<see cref="SetModelInfo"/>).
/// </summary>
public sealed class BlockEditorUi
{
    /// <summary>Режим гизмо в 3D-виде редактора блоков: масштаб модели (стрелки), поворот модели (кольца), занимаемый объём — footprint (ручки на гранях).</summary>
    public event Action<BlockEditorTool>? ToolChanged;

    /// <summary>Переключили «Manual volume» (галочка): редактор делает точку Undo и пересчитывает превью.</summary>
    public event Action? FootprintModeToggled;

    /// <summary>«Fit to model»: взять в ручной footprint значения, которые дал бы автоматический расчёт по bbox.</summary>
    public event Action? FitFootprintRequested;

    public event Action<string>? LoadRequested;
    public event Action? NewRequested;
    public event Action? SaveRequested;
    public event Action? FieldsChanged; // любое поле (кроме рядов коллизии/портов/нод) изменилось - пересчитать превью
    public event Action? ReloadSceneRequested;
    public event Action? AddCollisionGroupRequested;
    public event Action? FillCollisionFromFootprintRequested;
    public event Action<int>? RemoveCollisionGroupRequested;
    public event Action? CollisionChanged;
    public event Action? AddPortRequested;
    public event Action<int>? RemovePortRequested;
    public event Action? PortsChanged;
    public event Action? AddNodeRequested;
    public event Action<int>? RemoveNodeRequested;
    public event Action? NodesChanged;
    public event Action? AddDecorRequested;
    public event Action<int>? RemoveDecorRequested;
    public event Action? DecorChanged;

    /// <summary>
    /// Фокус вошёл в любое редактируемое поле (текст/дропдаун) — ПЕРЕД тем, как пользователь успел что-то в нём поменять
    /// (см. <see cref="BindUndoCapture"/>). <see cref="BlockEditor"/> снимает снэпшот для Undo/Redo ДО изменения именно по
    /// этому событию, а не по событиям «значение изменилось» — те стреляют уже ПОСЛЕ того, как Godot применил новое значение.
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
    private readonly LineEdit[] _scaleFields = new LineEdit[3];
    private readonly OptionButton[] _anchorDropdowns = new OptionButton[3];
    private readonly LineEdit[] _rotationFields = new LineEdit[3];
    private readonly Button[] _toolButtons = new Button[3];
    private readonly CheckBox _footprintManualBox;
    private readonly LineEdit[] _footprintMinFields = new LineEdit[3];
    private readonly LineEdit[] _footprintSizeFields = new LineEdit[3];
    private readonly LineEdit[] _attachFields = new LineEdit[3];
    private readonly Label _modelInfo;
    private readonly LineEdit _behaviorField;
    private readonly LineEdit _capacityField;
    private readonly TextEdit _paramsField;
    private readonly TextEdit _schemaField;
    private readonly VBoxContainer _nodeList;
    private readonly VBoxContainer _decorList;
    private readonly VBoxContainer _collisionList;
    private readonly VBoxContainer _portList;
    private readonly Label _status;

    private readonly List<(LineEdit[] Min, LineEdit[] Size)> _collisionRows = new();
    private bool _suppressCollisionEvents;

    private readonly List<(LineEdit Id, OptionButton Resource, OptionButton Direction, OptionButton Face, LineEdit[] FaceCell)> _portRows = new();
    private bool _suppressPortEvents;

    private readonly List<(LineEdit Id, OptionButton Type, OptionButton Direction, LineEdit[] Cell)> _nodeRows = new();
    private bool _suppressNodeEvents;

    private readonly List<(LineEdit Scene, LineEdit[] Position, LineEdit[] Rotation, LineEdit[] Scale, OptionButton Show)> _decorRows = new();
    private bool _suppressDecorEvents;
    private static readonly DecorShowWhen[] DecorShowValues = (DecorShowWhen[])Enum.GetValues(typeof(DecorShowWhen));
    private bool _suppressFootprintEvents;

    private static readonly ResourceType[] ResourceValues = (ResourceType[])Enum.GetValues(typeof(ResourceType));
    private static readonly PortDirection[] DirectionValues = (PortDirection[])Enum.GetValues(typeof(PortDirection));
    private static readonly BlockFace[] FaceValues = (BlockFace[])Enum.GetValues(typeof(BlockFace));
    private static readonly NodeType[] NodeTypeValues = (NodeType[])Enum.GetValues(typeof(NodeType));
    private static readonly float[] AnchorFractions = { 0f, 0.5f, 1f };
    private static readonly string[] AnchorNames = { "Min", "Center", "Max" };

    /// <summary>Верхний предел числа клеток, которые можно записать в XML коллизии (защита от случайного 1000×1000×1000).</summary>
    public const int MaxCollisionCells = 20000;

    public BlockEditorUi(Control layerRoot)
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

        column.AddChild(UiStyle.MakeLabel("Block Editor (--blockeditor)", 18));
        var hint = UiStyle.MakeLabel(
            "WASD/Q/E + зажатая СКМ - камера, колесо - зум. ЛКМ по белому маркеру на фиолетовой рамке - выбрать якорь. Гизмо в 3D-виде (кнопки Scale/Rotate/Volume или клавиши 1/2/3): " +
            "Scale - перетаскивание цветной стрелки (Shift - по всем осям), Rotate - перетаскивание цветного кольца вокруг оси (шаг 15°, Shift - 1°), " +
            "Volume - перетаскивание ручки грани footprint'а (шаг - клетка). Ctrl+Z / Ctrl+Y - отмена/повтор. Числа применяются по Enter/потере фокуса.", 12, UiStyle.TextDim);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(hint);

        var toolRow = new HBoxContainer();
        toolRow.AddChild(UiStyle.MakeLabel("Gizmo", 13));
        var toolGroup = new ButtonGroup();
        string[] toolNames = { "1 Scale", "2 Rotate", "3 Volume" };
        for (int i = 0; i < 3; i++)
        {
            var tool = (BlockEditorTool)i;
            var button = UiStyle.MakeButton(toolNames[i], new Vector2(110, 28), toggle: true);
            button.ButtonGroup = toolGroup;
            button.ButtonPressed = i == 0;
            button.Pressed += () => ToolChanged?.Invoke(tool);
            toolRow.AddChild(button);
            _toolButtons[i] = button;
        }

        column.AddChild(toolRow);

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

        var scaleRow = new HBoxContainer();
        scaleRow.AddChild(UiStyle.MakeLabel("Scale X/Y/Z", 13));
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new LineEdit
            {
                CustomMinimumSize = new Vector2(80, 28),
                Text = BlockModelLayout.DefaultScale.ToString(CultureInfo.InvariantCulture),
            };
            field.TextSubmitted += _ => FieldsChanged?.Invoke();
            field.FocusExited += () => FieldsChanged?.Invoke();
            scaleRow.AddChild(field);
            _scaleFields[axis] = field;
        }

        var resetScaleButton = UiStyle.MakeButton("Default", new Vector2(70, 28));
        resetScaleButton.Pressed += ResetScale;
        scaleRow.AddChild(resetScaleButton);
        column.AddChild(scaleRow);
        var scaleHint = UiStyle.MakeLabel(
            "Масштаб модели по осям (по умолчанию 0.125: 2 м в Blender = 1 клетка = 0.25 м). Автоподгонки по bbox нет. " +
            "Якорь при изменении масштаба остаётся на месте.", 11, UiStyle.TextDim);
        scaleHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(scaleHint);

        var anchorRow = new HBoxContainer();
        anchorRow.AddChild(UiStyle.MakeLabel("Anchor X/Y/Z", 13));
        for (int axis = 0; axis < 3; axis++)
        {
            var dropdown = MakeEnumDropdown(AnchorNames, 0);
            dropdown.ItemSelected += _ => FieldsChanged?.Invoke();
            BindUndoCapture(dropdown);
            anchorRow.AddChild(dropdown);
            _anchorDropdowns[axis] = dropdown;
        }

        column.AddChild(anchorRow);
        var anchorHint = UiStyle.MakeLabel(
            "Какая точка bbox модели встаёт в клетку (0,0,0) (Min/Center/Max по каждой оси; можно и кликом по маркеру в 3D). " +
            "Min/Min/Min - нижний задний левый угол bbox в точке (0,0,0). Точка попадает в ту же долю клетки (0,0,0), что и в bbox.", 11, UiStyle.TextDim);
        anchorHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(anchorHint);

        var rotationRow = new HBoxContainer();
        rotationRow.AddChild(UiStyle.MakeLabel("Rotation X/Y/Z °", 13));
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new LineEdit { CustomMinimumSize = new Vector2(80, 28), Text = "0" };
            field.TextSubmitted += _ => FieldsChanged?.Invoke();
            field.FocusExited += () => FieldsChanged?.Invoke();
            rotationRow.AddChild(field);
            _rotationFields[axis] = field;
        }

        var resetRotationButton = UiStyle.MakeButton("Reset", new Vector2(70, 28));
        resetRotationButton.Pressed += ResetRotation;
        rotationRow.AddChild(resetRotationButton);
        column.AddChild(rotationRow);
        var rotationHint = UiStyle.MakeLabel(
            "Поворот МОДЕЛИ в градусах (углы Эйлера, порядок YXZ; гизмо Rotate пишет сюда же). Клетки блока (footprint, коллизия, порты, ноды) не поворачиваются. " +
            "Рамка bbox и якорь считаются по повёрнутой модели (для углов не кратных 90° рамка - AABB повёрнутого bbox, чуть больше геометрии).", 11, UiStyle.TextDim);
        rotationHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(rotationHint);

        _footprintManualBox = new CheckBox { Text = "Manual volume (footprint)", FocusMode = Control.FocusModeEnum.None };
        _footprintManualBox.Toggled += _ =>
        {
            if (!_suppressFootprintEvents) FootprintModeToggled?.Invoke();
        };
        column.AddChild(_footprintManualBox);
        BuildFootprintRow(column, "Volume min x/y/z", _footprintMinFields);
        BuildFootprintRow(column, "Volume size x/y/z", _footprintSizeFields);
        var fitButton = UiStyle.MakeButton("Fit to model", new Vector2(110, 26));
        fitButton.Pressed += () => FitFootprintRequested?.Invoke();
        column.AddChild(fitButton);
        var attachRow = new HBoxContainer();
        attachRow.AddChild(UiStyle.MakeLabel("Attach cell x/y/z", 12));
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new LineEdit { CustomMinimumSize = new Vector2(60, 26), Text = "0" };
            field.TextSubmitted += _ => FieldsChanged?.Invoke();
            field.FocusExited += () => FieldsChanged?.Invoke();
            attachRow.AddChild(field);
            _attachFields[axis] = field;
        }

        column.AddChild(attachRow);
        var attachHint = UiStyle.MakeLabel(
            "Клетка, которая при установке встаёт ПОД КУРСОР и вокруг которой блок поворачивается (по умолчанию 0,0,0 = корень). Корень при установке вычисляется. Нужна, когда корень " +
            "(например, ступица колеса) - не то место, которым блок крепится (ось на клетку дальше). Должна лежать внутри footprint; клетка показана жёлтым каркасом. В XML пишется, только если не 0,0,0.", 11, UiStyle.TextDim);
        attachHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(attachHint);

        var volumeHint = UiStyle.MakeLabel(
            "Занимаемый объём блока в клетках. По умолчанию считается из bbox модели (поля только показывают). С галочкой его можно задать вручную (числами или гизмо Volume): " +
            "части модели могут выпирать за занятые клетки - там нет ни места, ни коллизии. Корневая клетка (0,0,0) всегда остаётся внутри.", 11, UiStyle.TextDim);
        volumeHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(volumeHint);

        _modelInfo = UiStyle.MakeLabel("", 12, UiStyle.Accent);
        _modelInfo.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _modelInfo.CustomMinimumSize = new Vector2(480, 0);
        column.AddChild(_modelInfo);

        column.AddChild(Separator());
        _behaviorField = AddTextRow(column, "Behavior", "");
        _capacityField = AddNumericRow(column, "Capacity", "0");
        _paramsField = AddJsonField(column,
            "Behavior params (JSON-объект, пусто = нет; у Button: \"mode\" momentary|toggle, \"glowNode\" имя узла-крышки, " +
            "\"glowColor\" #rrggbb)", 66);
        _schemaField = AddJsonField(column,
            "Parameters schema (компонент Parameters, JSON: что игрок настраивает инструментом Parameters; пусто = блок без настроек; сохраняется как есть)", 100);

        column.AddChild(Separator());
        var portsHeader = new HBoxContainer();
        var portsHeaderLabel = UiStyle.MakeLabel(
            "Ports (ФИЗИЧЕСКИЕ: вал Torque / труба Fluid; несколько портов могут сидеть в одном месте; cell - индексы клетки грани в рамке блока)", 13, UiStyle.TextDim);
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
        var collisionLabel = UiStyle.MakeLabel(
            "Collision (клетки: min x/y/z + размер в клетках; источник правды в XML - сами клетки; пусто = нет коллизии)", 13, UiStyle.TextDim);
        collisionLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        collisionLabel.CustomMinimumSize = new Vector2(260, 0);
        collisionHeader.AddChild(collisionLabel);
        var addCellsButton = UiStyle.MakeButton("+ Cells", new Vector2(70, 26));
        addCellsButton.Pressed += () => AddCollisionGroupRequested?.Invoke();
        collisionHeader.AddChild(addCellsButton);
        var fillButton = UiStyle.MakeButton("Fill footprint", new Vector2(110, 26));
        fillButton.Pressed += () => FillCollisionFromFootprintRequested?.Invoke();
        collisionHeader.AddChild(fillButton);
        column.AddChild(collisionHeader);

        _collisionList = new VBoxContainer();
        _collisionList.AddThemeConstantOverride("separation", 10);
        column.AddChild(_collisionList);

        column.AddChild(Separator());
        var decorHeader = new HBoxContainer();
        var decorLabel = UiStyle.MakeLabel(
            "Decor (декоративные меши-указатели: куда смотрит блок, в какую сторону вращение; видны ТОЛЬКО в редакторе построек, в игре их нет, клеток и коллизии не занимают; " +
            "position - точка рамки блока в метрах, куда встаёт начало координат модели; Ghost - только пока блок ставится, Always - и у поставленных)", 13, UiStyle.TextDim);
        decorLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        decorLabel.CustomMinimumSize = new Vector2(360, 0);
        decorHeader.AddChild(decorLabel);
        var addDecorButton = UiStyle.MakeButton("+ Add decor", new Vector2(100, 26));
        addDecorButton.Pressed += () => AddDecorRequested?.Invoke();
        decorHeader.AddChild(addDecorButton);
        column.AddChild(decorHeader);

        _decorList = new VBoxContainer();
        _decorList.AddThemeConstantOverride("separation", 10);
        column.AddChild(_decorList);

        column.AddChild(Separator());
        var saveButton = UiStyle.MakeButton("Save to blocks/<slug>.xml", new Vector2(480, 36));
        saveButton.Pressed += () => SaveRequested?.Invoke();
        column.AddChild(saveButton);

        _status = UiStyle.MakeLabel("", 12, UiStyle.TextDim);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_status);

        layerRoot.AddChild(panel);

        // Каждое базовое поле помечает превью как "грязное" И снимает снэпшот для Undo/Redo (см. BindUndoCapture) ПРИ ВХОДЕ
        // фокуса в поле - до того, как значение изменится.
        foreach (var field in new[] { _nameField, _colorField, _massField, _durabilityField, _damageField, _behaviorField, _capacityField })
        {
            field.TextSubmitted += _ => FieldsChanged?.Invoke();
            field.FocusExited += () => FieldsChanged?.Invoke();
            BindUndoCapture(field);
        }

        BindUndoCapture(_sceneField);
        _paramsField.FocusExited += () => FieldsChanged?.Invoke(); // многострочное поле: Enter - новая строка, применяем по потере фокуса
        BindUndoCapture(_paramsField);
        _schemaField.FocusExited += () => FieldsChanged?.Invoke();
        BindUndoCapture(_schemaField);
        foreach (var field in _scaleFields) BindUndoCapture(field);
        foreach (var field in _rotationFields) BindUndoCapture(field);
        foreach (var field in _footprintMinFields) BindUndoCapture(field);
        foreach (var field in _footprintSizeFields) BindUndoCapture(field);
        foreach (var field in _attachFields) BindUndoCapture(field);
    }

    /// <summary>Ряд из трёх целых полей footprint'а (min или размер): применяются по Enter/потере фокуса, как и остальные числа.</summary>
    private void BuildFootprintRow(VBoxContainer column, string label, LineEdit[] fields)
    {
        var row = new HBoxContainer();
        row.AddChild(UiStyle.MakeLabel(label, 12));
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new LineEdit { CustomMinimumSize = new Vector2(60, 26), Text = "0", Editable = false };
            field.TextSubmitted += _ => FieldsChanged?.Invoke();
            field.FocusExited += () => FieldsChanged?.Invoke();
            row.AddChild(field);
            fields[axis] = field;
        }

        column.AddChild(row);
    }

    private void ResetRotation()
    {
        EditSessionStarting?.Invoke();
        SetRotation(Vector3.Zero);
        FieldsChanged?.Invoke();
    }

    /// <summary>Клетка размещения блока (рамка блока, целые; нечисловой ввод - 0).</summary>
    public Vector3I AttachCell => new(ParseIntOrZero(_attachFields[0].Text), ParseIntOrZero(_attachFields[1].Text), ParseIntOrZero(_attachFields[2].Text));

    /// <summary>Записывает клетку размещения в поля (без событий).</summary>
    public void SetAttachCell(Vector3I cell)
    {
        for (int axis = 0; axis < 3; axis++) _attachFields[axis].Text = cell[axis].ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Выбранный инструмент гизмо (кнопки Scale/Rotate/Volume).</summary>
    public BlockEditorTool Tool
    {
        get
        {
            for (int i = 0; i < _toolButtons.Length; i++)
            {
                if (_toolButtons[i].ButtonPressed) return (BlockEditorTool)i;
            }

            return BlockEditorTool.Scale;
        }
    }

    /// <summary>Для самотестов - щёлкает галочку «Manual volume» как пользователь (с событием <see cref="FootprintModeToggled"/>).</summary>
    public void SetFootprintManualForTesting(bool manual) => _footprintManualBox.ButtonPressed = manual;

    /// <summary>Выбирает инструмент без события (клавиши 1/2/3 и Undo выставляют его сами).</summary>
    public void SetTool(BlockEditorTool tool)
    {
        for (int i = 0; i < _toolButtons.Length; i++) _toolButtons[i].SetPressedNoSignal(i == (int)tool);
    }

    /// <summary>Поворот модели, градусы (углы Эйлера YXZ, см. <see cref="BlockModelLayout.RotationBasis"/>); нечисловой ввод - 0.</summary>
    public Vector3 Rotation => new(
        ParseFloatOr(_rotationFields[0].Text, 0f), ParseFloatOr(_rotationFields[1].Text, 0f), ParseFloatOr(_rotationFields[2].Text, 0f));

    /// <summary>Записывает поворот в поля (без событий) - вызывается и гизмо в 3D, и Undo.</summary>
    public void SetRotation(Vector3 degrees)
    {
        for (int axis = 0; axis < 3; axis++) _rotationFields[axis].Text = FormatNumber(degrees[axis]);
    }

    /// <summary>Footprint задан вручную (галочка «Manual volume»); иначе его считает редактор из bbox модели.</summary>
    public bool FootprintManual => _footprintManualBox.ButtonPressed;

    /// <summary>Введённый вручную footprint: min (любые целые) и размер (не меньше 1) - как в полях, до исправления <see cref="BlockModelLayout.SanitizeFootprint"/>.</summary>
    public (Vector3I Min, Vector3I Size) ManualFootprint => (
        new Vector3I(ParseIntOrZero(_footprintMinFields[0].Text), ParseIntOrZero(_footprintMinFields[1].Text), ParseIntOrZero(_footprintMinFields[2].Text)),
        new Vector3I(ParseSizeOr1(_footprintSizeFields[0].Text), ParseSizeOr1(_footprintSizeFields[1].Text), ParseSizeOr1(_footprintSizeFields[2].Text)));

    /// <summary>Записывает режим и значения footprint'а в панель без событий. Поля редактируемы только в ручном режиме; в автоматическом они показывают рассчитанные значения.</summary>
    public void SetFootprint(bool manual, Vector3I min, Vector3I size)
    {
        _suppressFootprintEvents = true;
        _footprintManualBox.ButtonPressed = manual;
        for (int axis = 0; axis < 3; axis++)
        {
            _footprintMinFields[axis].Text = min[axis].ToString(CultureInfo.InvariantCulture);
            _footprintSizeFields[axis].Text = size[axis].ToString(CultureInfo.InvariantCulture);
            _footprintMinFields[axis].Editable = manual;
            _footprintSizeFields[axis].Editable = manual;
        }

        _suppressFootprintEvents = false;
    }

    /// <summary>Снимает Undo-снэпшот, когда фокус ВХОДИТ в это поле/дропдаун — ДО того, как пользователь успеет что-то
    /// в нём поменять (см. <see cref="EditSessionStarting"/> doc).</summary>
    private void BindUndoCapture(Control control) => control.FocusEntered += () => EditSessionStarting?.Invoke();

    private void ResetScale()
    {
        EditSessionStarting?.Invoke();
        SetModelScale(BlockModelLayout.DefaultScaleVector);
        FieldsChanged?.Invoke();
    }

    /// <summary>
    /// Открывает НАТИВНЫЙ диалог выбора файла ОС (<see cref="DisplayServer.FileDialogShow"/> — на Windows это обычный проводник) для
    /// выбора `.glb`/`.gltf`. Выбранный файл ВНЕ папки проекта копируется в <c>res://meshes/</c> (<see cref="DirAccess.CopyAbsolute"/>,
    /// тем же именем, перезаписывая одноимённый) — движок ссылается только на файлы внутри проекта; файл, уже лежащий внутри
    /// проекта, используется по месту. Модель видна в превью СРАЗУ: <see cref="FunctionalBlockGeometry.GetOrLoadScene"/> читает
    /// `.glb`/`.gltf` напрямую через <see cref="GltfDocument"/>, без `--import`/перезапуска.
    /// </summary>
    private void BrowseForModel() => ShowModelDialog(ApplyChosenModelPath);

    /// <summary>Нативный диалог выбора glTF; выбранный путь отдаётся <paramref name="apply"/> (модель блока или указатель).</summary>
    private void ShowModelDialog(Action<string> apply)
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
                apply(selectedPaths[0]);
            }));
    }

    /// <summary>Обрабатывает выбранный в диалоге файл (публичный — вызывается и самотестом без настоящего диалога).</summary>
    public void ApplyChosenModelPath(string nativePath)
    {
        string? resPath = ImportModelFile(nativePath, out bool copied);
        if (resPath == null) return;

        // Порядок важен: ставим СВОЙ статус ДО перезагрузки - если сцена реально не прочитается, превью само перезапишет его
        // настоящей ошибкой, наш "скопировано"/"установлено" не должен её скрывать. Смена модели - правка, значит точка Undo.
        EditSessionStarting?.Invoke();
        _sceneField.Text = resPath;
        SetStatus(copied ? $"copied into {resPath}" : $"model set to {resPath}");
        ReloadSceneRequested?.Invoke();
    }

    /// <summary>То же для модели-указателя ряда <paramref name="index"/> списка Decor.</summary>
    public void ApplyChosenDecorPath(int index, string nativePath)
    {
        string? resPath = ImportModelFile(nativePath, out bool copied);
        if (resPath == null || index < 0 || index >= _decorRows.Count) return;

        EditSessionStarting?.Invoke();
        _decorRows[index].Scene.Text = resPath;
        SetStatus(copied ? $"decor model copied into {resPath}" : $"decor model set to {resPath}");
        NotifyDecorChanged();
    }

    /// <summary>Путь <c>res://</c> для выбранного файла: внутри проекта - как есть, снаружи - копия в <c>res://meshes/</c>; null - копирование не удалось (причина в статусе).</summary>
    private string? ImportModelFile(string nativePath, out bool copied)
    {
        copied = false;
        string localized = ProjectSettings.LocalizePath(nativePath);
        string resPath;

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
                return null;
            }

            copied = true;
        }

        return resPath;
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

    /// <summary>Подпись + многострочное поле для сырого JSON (параметры поведения) — применяется по потере фокуса
    /// (Enter в многострочном поле — перенос строки, не "применить").</summary>
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

    /// <summary>Для самотестов — меняет только слаг (в обычном UI он печатается руками).</summary>
    public void SetSlugForTesting(string slug) => _slugField.Text = slug;

    public string Name => string.IsNullOrWhiteSpace(_nameField.Text) ? Slug : _nameField.Text.Trim();
    public string ScenePath => _sceneField.Text.Trim();
    public string Behavior => _behaviorField.Text.Trim();

    public Color Color => TryParseColor(_colorField.Text, out var color) ? color : Colors.White;

    /// <summary>Масштаб модели по осям; нечисловой/неположительный ввод заменяется значением по умолчанию
    /// (<see cref="BlockModelLayout.DefaultScale"/>), округляется до 6 знаков (чтобы в XML не попадал float-шум).</summary>
    public Vector3 ModelScale => new(
        ParsePositiveOr(_scaleFields[0].Text, BlockModelLayout.DefaultScale),
        ParsePositiveOr(_scaleFields[1].Text, BlockModelLayout.DefaultScale),
        ParsePositiveOr(_scaleFields[2].Text, BlockModelLayout.DefaultScale));

    /// <summary>Якорь — доли bbox {0, 0.5, 1} по осям (см. <see cref="BlockModelLayout"/>).</summary>
    public Vector3 Anchor => new(
        AnchorFractions[Math.Max(_anchorDropdowns[0].Selected, 0)],
        AnchorFractions[Math.Max(_anchorDropdowns[1].Selected, 0)],
        AnchorFractions[Math.Max(_anchorDropdowns[2].Selected, 0)]);

    public float Mass => ParseFloatOr(_massField.Text, 10f);
    public float Durability => ParseFloatOr(_durabilityField.Text, 100f);
    public float DamageResistance => ParseFloatOr(_damageField.Text, 0.1f);
    public float Capacity => ParseFloatOr(_capacityField.Text, 0f);

    /// <summary>Сырой текст поля "Behavior params" (JSON-объект или пусто) — разбор и проверка в <see cref="BlockEditor"/>.</summary>
    public string ParamsJson => _paramsField.Text.Trim();

    /// <summary>Текст компонента <c>Parameters</c> (схема настраиваемых параметров блока, JSON; пусто — у блока нет настроек) — редактор хранит и записывает его как есть, чтобы пересохранение блока его не стирало.</summary>
    public string ParametersSchemaJson => _schemaField.Text.Trim();

    /// <summary>Для самотестов — задаёт текст схемы параметров.</summary>
    public void SetParametersSchemaForTesting(string json) => _schemaField.Text = json;

    /// <summary>Для самотестов — задаёт только параметры поведения (JSON-текст), не трогая остальные поля.</summary>
    public void SetParamsForTesting(string paramsJson) => _paramsField.Text = paramsJson;

    /// <summary>Записывает масштаб в поля (без событий) — вызывается и перетаскиванием стрелки в 3D, и Undo.</summary>
    public void SetModelScale(Vector3 scale)
    {
        for (int axis = 0; axis < 3; axis++) _scaleFields[axis].Text = FormatNumber(scale[axis]);
    }

    /// <summary>Записывает якорь в выпадающие списки (без событий). Доли привязываются к {0, 0.5, 1}.</summary>
    public void SetAnchor(Vector3 anchor)
    {
        var snapped = BlockModelLayout.SnapAnchor(anchor);
        // (float): в сборке движка с double-precision Vector3[axis] - double, а Array.IndexOf сравнивает как object и вернул бы -1 для float-массива.
        for (int axis = 0; axis < 3; axis++) _anchorDropdowns[axis].Selected = Array.IndexOf(AnchorFractions, (float)snapped[axis]);
    }

    /// <summary>Строка с размерами модели/footprint'ом под полями якоря — только показ, не данные.</summary>
    public void SetModelInfo(string text) => _modelInfo.Text = text;

    /// <summary>Для самотестов — заполняет поля УЖЕ СУЩЕСТВУЮЩЕГО ряда порта напрямую, без события <see cref="PortsChanged"/>.</summary>
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

    /// <summary>Для самотестов — заполняет поля УЖЕ СУЩЕСТВУЮЩЕГО ряда ноды напрямую, без события <see cref="NodesChanged"/>.</summary>
    public void SetNodeForTesting(int index, string id, NodeType type, PortDirection direction, Vector3I cell)
    {
        var row = _nodeRows[index];
        row.Id.Text = id;
        row.Type.Selected = Array.IndexOf(NodeTypeValues, type);
        row.Direction.Selected = Array.IndexOf(DirectionValues, direction);
        for (int axis = 0; axis < 3; axis++) row.Cell[axis].Text = cell[axis].ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Для самотестов — заполняет поля УЖЕ СУЩЕСТВУЮЩЕГО ряда группы клеток коллизии, без события <see cref="CollisionChanged"/>.</summary>
    public void SetCollisionGroupForTesting(int index, Vector3I min, Vector3I size)
    {
        var row = _collisionRows[index];
        for (int axis = 0; axis < 3; axis++)
        {
            row.Min[axis].Text = min[axis].ToString(CultureInfo.InvariantCulture);
            row.Size[axis].Text = size[axis].ToString(CultureInfo.InvariantCulture);
        }
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

    /// <summary>Группы клеток коллизии (min + размер, размер не меньше 1) — из живых полей рядов при каждом обращении.</summary>
    public IReadOnlyList<CellBox> CollisionGroups
    {
        get
        {
            var result = new List<CellBox>(_collisionRows.Count);
            foreach (var row in _collisionRows)
            {
                result.Add(new CellBox(
                    new Vector3I(ParseIntOrZero(row.Min[0].Text), ParseIntOrZero(row.Min[1].Text), ParseIntOrZero(row.Min[2].Text)),
                    new Vector3I(ParseSizeOr1(row.Size[0].Text), ParseSizeOr1(row.Size[1].Text), ParseSizeOr1(row.Size[2].Text))));
            }

            return result;
        }
    }

    /// <summary>Физические порты — тот же приём "читать из живых полей", что и у групп коллизии.</summary>
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
        string scenePath, Vector3 modelScale, Vector3 anchor, string behavior, float capacity,
        IReadOnlyList<CellBox> collisionGroups, IReadOnlyList<ResourcePort> ports, IReadOnlyList<LogicNode> nodes, string paramsJson, string parametersSchemaJson = "",
        Vector3 rotation = default, bool footprintManual = false, Vector3I footprintMin = default, Vector3I footprintSize = default, Vector3I attachCell = default, IReadOnlyList<DecorMesh>? decor = null)
    {
        _slugField.Text = slug;
        _nameField.Text = name;
        _colorField.Text = "#" + color.ToHtml(false);
        _colorSwatch.Color = color;
        _massField.Text = mass.ToString(CultureInfo.InvariantCulture);
        _durabilityField.Text = durability.ToString(CultureInfo.InvariantCulture);
        _damageField.Text = damage.ToString(CultureInfo.InvariantCulture);
        _sceneField.Text = scenePath;
        SetModelScale(modelScale);
        SetAnchor(anchor);
        SetRotation(rotation);
        SetAttachCell(attachCell);
        SetFootprint(footprintManual, footprintMin, footprintSize == Vector3I.Zero ? Vector3I.One : footprintSize);
        _behaviorField.Text = behavior;
        _capacityField.Text = capacity.ToString(CultureInfo.InvariantCulture);
        _paramsField.Text = paramsJson;
        _schemaField.Text = parametersSchemaJson;

        SetCollisionRows(collisionGroups);
        SetPortRows(ports);
        SetNodeRows(nodes);
        SetDecorRows(decor ?? Array.Empty<DecorMesh>());
    }

    /// <summary>Перестраивает список рядов коллизии с нуля — вызывается при загрузке блока и при Add/Remove (сами значения
    /// полей читаются геттером <see cref="CollisionGroups"/>, этот метод только меняет их КОЛИЧЕСТВО/начальные значения).</summary>
    public void SetCollisionRows(IReadOnlyList<CellBox> groups)
    {
        _suppressCollisionEvents = true;
        foreach (var child in _collisionList.GetChildren()) child.QueueFree();
        _collisionRows.Clear();

        for (int i = 0; i < groups.Count; i++) AddCollisionRow(groups[i], i);

        _suppressCollisionEvents = false;
    }

    private void AddCollisionRow(CellBox group, int index)
    {
        var entry = new VBoxContainer();
        entry.AddThemeConstantOverride("separation", 2);

        var header = new HBoxContainer();
        header.AddChild(UiStyle.MakeLabel($"Cells #{index}", 12, UiStyle.TextDim));
        int capturedIndex = index;
        var removeButton = UiStyle.MakeButton("Remove", new Vector2(70, 24));
        removeButton.Pressed += () => RemoveCollisionGroupRequested?.Invoke(capturedIndex);
        header.AddChild(removeButton);
        entry.AddChild(header);

        var minFields = new LineEdit[3];
        var sizeFields = new LineEdit[3];

        var minRow = new HBoxContainer();
        minRow.AddChild(UiStyle.MakeLabel("min x/y/z", 11, UiStyle.TextDim));
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new LineEdit { CustomMinimumSize = new Vector2(60, 26), Text = group.Min[axis].ToString(CultureInfo.InvariantCulture) };
            field.TextSubmitted += _ => NotifyCollisionChanged();
            field.FocusExited += () => NotifyCollisionChanged();
            BindUndoCapture(field);
            minRow.AddChild(field);
            minFields[axis] = field;
        }

        entry.AddChild(minRow);

        var sizeRow = new HBoxContainer();
        sizeRow.AddChild(UiStyle.MakeLabel("size x/y/z", 11, UiStyle.TextDim));
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new LineEdit { CustomMinimumSize = new Vector2(60, 26), Text = group.Size[axis].ToString(CultureInfo.InvariantCulture) };
            field.TextSubmitted += _ => NotifyCollisionChanged();
            field.FocusExited += () => NotifyCollisionChanged();
            BindUndoCapture(field);
            sizeRow.AddChild(field);
            sizeFields[axis] = field;
        }

        entry.AddChild(sizeRow);

        _collisionList.AddChild(entry);
        _collisionList.AddChild(new HSeparator());
        _collisionRows.Add((minFields, sizeFields));
    }

    private void NotifyCollisionChanged()
    {
        if (!_suppressCollisionEvents) CollisionChanged?.Invoke();
    }

    // ------------------------------------------------------------------ ресурсные порты

    /// <summary>Перестраивает список рядов портов с нуля — тот же приём, что и <see cref="SetCollisionRows"/>.</summary>
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

    // ------------------------------------------------------------------ логические ноды

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

    // ------------------------------------------------------------------ декоративные указатели

    /// <summary>Для самотестов - заполняет поля УЖЕ СУЩЕСТВУЮЩЕГО ряда указателя напрямую, без события <see cref="DecorChanged"/>.</summary>
    public void SetDecorForTesting(int index, DecorMesh decor)
    {
        var row = _decorRows[index];
        row.Scene.Text = decor.Scene;
        for (int axis = 0; axis < 3; axis++)
        {
            row.Position[axis].Text = FormatNumber(decor.Position[axis]);
            row.Rotation[axis].Text = FormatNumber(decor.Rotation[axis]);
            row.Scale[axis].Text = FormatNumber(decor.Scale[axis]);
        }

        row.Show.Selected = Array.IndexOf(DecorShowValues, decor.ShowWhen);
    }

    /// <summary>Указатели — из живых полей рядов (scale не меньше/равен нулю заменяется умолчанием, как у масштаба модели).</summary>
    public IReadOnlyList<DecorMesh> Decor
    {
        get
        {
            var result = new List<DecorMesh>(_decorRows.Count);
            foreach (var row in _decorRows)
            {
                result.Add(new DecorMesh(
                    row.Scene.Text.Trim(),
                    new Vector3(ParseFloatOr(row.Position[0].Text, 0f), ParseFloatOr(row.Position[1].Text, 0f), ParseFloatOr(row.Position[2].Text, 0f)),
                    new Vector3(ParseFloatOr(row.Rotation[0].Text, 0f), ParseFloatOr(row.Rotation[1].Text, 0f), ParseFloatOr(row.Rotation[2].Text, 0f)),
                    new Vector3(ParsePositiveOr(row.Scale[0].Text, BlockModelLayout.DefaultScale), ParsePositiveOr(row.Scale[1].Text, BlockModelLayout.DefaultScale), ParsePositiveOr(row.Scale[2].Text, BlockModelLayout.DefaultScale)),
                    DecorShowValues[Math.Max(row.Show.Selected, 0)]));
            }

            return result;
        }
    }

    /// <summary>Перестраивает список рядов указателей с нуля (загрузка блока, Add/Remove, Undo) - тот же приём, что и у портов.</summary>
    public void SetDecorRows(IReadOnlyList<DecorMesh> decor)
    {
        _suppressDecorEvents = true;
        foreach (var child in _decorList.GetChildren()) child.QueueFree();
        _decorRows.Clear();
        for (int i = 0; i < decor.Count; i++) AddDecorRow(decor[i], i);
        _suppressDecorEvents = false;
    }

    private LineEdit[] AddVectorRow(VBoxContainer entry, string label, Vector3 value)
    {
        var row = new HBoxContainer();
        row.AddChild(UiStyle.MakeLabel(label, 11, UiStyle.TextDim));
        var fields = new LineEdit[3];
        for (int axis = 0; axis < 3; axis++)
        {
            var field = new LineEdit { CustomMinimumSize = new Vector2(70, 26), Text = FormatNumber(value[axis]) };
            field.TextSubmitted += _ => NotifyDecorChanged();
            field.FocusExited += () => NotifyDecorChanged();
            BindUndoCapture(field);
            row.AddChild(field);
            fields[axis] = field;
        }

        entry.AddChild(row);
        return fields;
    }

    private void AddDecorRow(DecorMesh decor, int index)
    {
        var entry = new VBoxContainer();
        entry.AddThemeConstantOverride("separation", 2);

        var header = new HBoxContainer();
        header.AddChild(UiStyle.MakeLabel($"Decor #{index}", 12, UiStyle.TextDim));
        int capturedIndex = index;
        var removeButton = UiStyle.MakeButton("Remove", new Vector2(70, 24));
        removeButton.Pressed += () => RemoveDecorRequested?.Invoke(capturedIndex);
        header.AddChild(removeButton);
        entry.AddChild(header);

        var sceneRow = new HBoxContainer();
        sceneRow.AddChild(UiStyle.MakeLabel("scene", 11, UiStyle.TextDim));
        var sceneField = new LineEdit { CustomMinimumSize = new Vector2(250, 26), Text = decor.Scene, PlaceholderText = "res://meshes/arrow.glb" };
        sceneField.TextSubmitted += _ => NotifyDecorChanged();
        sceneField.FocusExited += () => NotifyDecorChanged();
        BindUndoCapture(sceneField);
        sceneRow.AddChild(sceneField);
        var browse = UiStyle.MakeButton("Browse...", new Vector2(80, 26));
        browse.Pressed += () => ShowModelDialog(path => ApplyChosenDecorPath(capturedIndex, path));
        sceneRow.AddChild(browse);
        entry.AddChild(sceneRow);

        var position = AddVectorRow(entry, "position m", decor.Position);
        var rotation = AddVectorRow(entry, "rotation °", decor.Rotation);
        var scale = AddVectorRow(entry, "scale", decor.Scale);

        var showRow = new HBoxContainer();
        showRow.AddChild(UiStyle.MakeLabel("show", 11, UiStyle.TextDim));
        var show = MakeEnumDropdown(DecorShowValues.Select(v => v.ToString()), Array.IndexOf(DecorShowValues, decor.ShowWhen));
        show.ItemSelected += _ => NotifyDecorChanged();
        BindUndoCapture(show);
        showRow.AddChild(show);
        entry.AddChild(showRow);

        _decorList.AddChild(entry);
        _decorList.AddChild(new HSeparator());
        _decorRows.Add((sceneField, position, rotation, scale, show));
    }

    private void NotifyDecorChanged()
    {
        if (!_suppressDecorEvents) DecorChanged?.Invoke();
    }

    public void SetStatus(string message) => _status.Text = message;

    /// <summary>Текущий текст строки статуса (для самотестов).</summary>
    public string Status => _status.Text;

    private static bool TryParseColor(string text, out Color color)
    {
        try { color = Color.FromHtml(text); return true; }
        catch { color = Colors.White; return false; }
    }

    /// <summary>Число для поля: инвариантная культура, до 6 знаков после запятой (без float-шума вроде 0.12345670163631439).</summary>
    public static string FormatNumber(double value) => Math.Round(value, 6).ToString(CultureInfo.InvariantCulture);

    private static float ParsePositiveOr(string text, float fallback) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && value > 0f && float.IsFinite(value)
            ? (float)Math.Round(value, 6)
            : fallback;

    /// <summary>Целое без ограничений (координата клетки законно 0 и отрицательная); мусор — 0.</summary>
    private static int ParseIntOrZero(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    /// <summary>Размер группы клеток — целое не меньше 1 (иначе 1) и не больше 4096 по оси (чтобы произведение не переполняло long при подсчёте клеток).</summary>
    private static int ParseSizeOr1(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 1 ? Math.Min(value, 4096) : 1;

    private static float ParseFloatOr(string text, float fallback) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
}
