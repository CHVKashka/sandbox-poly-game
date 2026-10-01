using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly LineEdit _behaviorField;
    private readonly LineEdit _capacityField;
    private readonly VBoxContainer _collisionList;
    private readonly Label _status;

    private readonly List<(LineEdit[] Position, LineEdit[] Size)> _collisionRows = new();
    private bool _suppressCollisionEvents;

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
        _sceneField = new LineEdit { CustomMinimumSize = new Vector2(360, 28), PlaceholderText = "res://meshes/....glb" };
        _sceneField.TextSubmitted += _ => ReloadSceneRequested?.Invoke();
        _sceneField.FocusExited += () => ReloadSceneRequested?.Invoke();
        sceneRow.AddChild(_sceneField);
        var reloadButton = UiStyle.MakeButton("Reload", new Vector2(80, 28));
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

        column.AddChild(Separator());
        var portsHint = UiStyle.MakeLabel("Ресурсы (необязательно - ports не редактируются здесь, см. XML руками)", 12, UiStyle.TextDim);
        portsHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(portsHint);
        _behaviorField = AddTextRow(column, "Behavior", "");
        _capacityField = AddNumericRow(column, "Capacity", "0");

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

        // Каждое базовое поле (кроме slug/collision, у которых своя логика) помечает превью как "грязное".
        foreach (var field in new[] { _nameField, _colorField, _massField, _durabilityField, _damageField, _behaviorField, _capacityField })
        {
            field.TextSubmitted += _ => FieldsChanged?.Invoke();
            field.FocusExited += () => FieldsChanged?.Invoke();
        }
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

    /// <summary>Для самотестов — меняет только "Model stretch" (см. <see cref="ModelScale"/>), не трогая остальные поля.</summary>
    public void SetModelScaleForTesting(Vector3 scale)
    {
        for (int axis = 0; axis < 3; axis++) _modelScaleFields[axis].Text = scale[axis].ToString(CultureInfo.InvariantCulture);
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

    public float Mass => ParseFloatOr(_massField.Text, 10f);
    public float Durability => ParseFloatOr(_durabilityField.Text, 100f);
    public float DamageResistance => ParseFloatOr(_damageField.Text, 0.1f);
    public float Capacity => ParseFloatOr(_capacityField.Text, 0f);

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

    // ------------------------------------------------------------------ запись полей (загрузка существующего/нового блока)

    public void LoadFields(string slug, string name, Color color, float mass, float durability, float damage,
        string scenePath, Vector3I footprint, Vector3 modelScale, string behavior, float capacity, IReadOnlyList<CollisionBox> collisionBoxes)
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
        _behaviorField.Text = behavior;
        _capacityField.Text = capacity.ToString(CultureInfo.InvariantCulture);

        SetCollisionBoxRows(collisionBoxes);
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

    public void SetStatus(string message) => _status.Text = message;

    private static bool TryParseColor(string text, out Color color)
    {
        try { color = Color.FromHtml(text); return true; }
        catch { color = Colors.White; return false; }
    }

    private static int ParseIntOr(string text, int fallback) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0 ? value : fallback;

    private static float ParseFloatOr(string text, float fallback) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
}
