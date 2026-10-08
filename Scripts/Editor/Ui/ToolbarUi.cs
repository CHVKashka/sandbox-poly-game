using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SandboxPolyGame.Editor.Ui;

/// <summary>
/// Тулбар справа: перекраска (с палитрой, см. ниже), удаление, размер следующего ставящегося блока (Resize, панель
/// с полями X/Y/Z), каркас (Wireframe) и границы блоков (Borders), сохранение/загрузка постройки. Paint/Delete —
/// взаимоисключающие инструменты, оба на ЛКМ (см. <c>BuildEditor.ButtonFor</c> — не конфликтуют, т.к. активен
/// максимум один; пока активен любой из них, ЛКМ не ставит блок и призрак скрыт). Resize/Wireframe/Borders —
/// независимые переключатели, не занимают ни одну кнопку мыши (Resize настраивает ПРИЗРАК —
/// <see cref="EditorState.PendingSize"/> — а не уже поставленные блоки). У каждой кнопки-переключателя есть
/// кружок-индикатор в углу, показывающий, что она включена.
/// </summary>
internal sealed class ToolbarUi
{
    private const string CustomPalettePath = "user://custom_palette.cfg";
    private static readonly string[] AxisNames = { "X", "Y", "Z" };

    private static readonly string[] BasePaletteHex =
    {
        "#d94040", "#e8862c", "#e8cf3a", "#58b04a",
        "#2fb5a6", "#3d78d8", "#8f5bd0", "#d95fa8",
        "#f2f2f2", "#8a8f96", "#3a3d44", "#16171a",
    };

    private readonly EditorState _state;
    private readonly Button _paint;
    private readonly Button _delete;
    private readonly Button _resize;
    private readonly Button _wireframe;
    private readonly Button _borders;
    private readonly Panel _paintDot;
    private readonly Panel _deleteDot;
    private readonly Button _wire;
    private readonly Button _parameters;
    private readonly Control _wireLayerPanel;
    private readonly Button _layerElectricity;
    private readonly Button _layerLogic;
    private readonly Panel _wireDot;
    private readonly Panel _parametersDot;
    private readonly Panel _resizeDot;
    private readonly Panel _wireframeDot;
    private readonly Panel _bordersDot;
    private readonly Control _paintPanel;
    private readonly ColorPickerButton _colorPicker;
    private readonly HSlider _sliderR;
    private readonly HSlider _sliderG;
    private readonly HSlider _sliderB;
    private readonly List<Color> _customColors;
    private bool _syncingSliders;

    // Панель Resize: раскрывается под кнопкой "Resize", как и палитра под "Paint". Редактирует EditorState.PendingSize
    // напрямую (размер СЛЕДУЮЩЕГО ставящегося блока/призрака) — в отличие от старой версии, тут не нужна цель
    // (ПКМ по блоку), панель всегда активна и отражает текущий PendingSize.
    private readonly Control _resizePanel;
    private readonly LineEdit[] _resizeFields = new LineEdit[3];

    /// <summary>Нажата кнопка Save — открыть диалог выбора файла (см. <see cref="EditorUi"/>).</summary>
    public event Action? SaveRequested;

    /// <summary>Нажата кнопка Load — открыть диалог выбора файла (см. <see cref="EditorUi"/>).</summary>
    public event Action? LoadRequested;

    /// <summary>Нажата кнопка Exit — выйти из редактора обратно в мир БЕЗ спавна постройки (см. <see cref="EditorUi"/>).</summary>
    public event Action? ExitRequested;

    /// <summary>Нажата кнопка Spawn — посчитать параметры постройки и выйти в мир, поставив её у верстака физическим
    /// телом (см. <see cref="EditorUi"/>).</summary>
    public event Action? SpawnRequested;

    public Control Panel { get; }

    public ToolbarUi(Control layerRoot, EditorState state)
    {
        _state = state;
        _customColors = CustomPalette.Load(CustomPalettePath);

        var margin = UiStyle.Transparent(new MarginContainer());
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_right", 14);

        var row = UiStyle.Transparent(new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End });
        margin.AddChild(row);

        var panel = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(10f));
        row.AddChild(panel);
        Panel = panel;

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        panel.AddChild(column);

        // Выход из редактора - наверху, отдельно от инструментов построения (см. EditorUi): Exit ничего не спавнит,
        // Spawn считает параметры постройки (центр масс, коллизия) и материализует её у верстака физическим телом.
        var sessionRow = UiStyle.Transparent(new HBoxContainer());
        sessionRow.AddThemeConstantOverride("separation", 8);
        var exit = UiStyle.MakeButton("Exit", new Vector2(78, 34));
        exit.TooltipText = "Leave the editor without spawning the construction into the world";
        exit.Pressed += () => ExitRequested?.Invoke();
        sessionRow.AddChild(exit);
        var spawn = UiStyle.MakeButton("Spawn", new Vector2(78, 34));
        spawn.TooltipText = "Spawn this construction into the world as a physical vehicle and leave the editor";
        spawn.Pressed += () => SpawnRequested?.Invoke();
        sessionRow.AddChild(spawn);
        column.AddChild(sessionRow);
        column.AddChild(new HSeparator());

        column.AddChild(UiStyle.MakeLabel("Tools", 18));

        _paintPanel = BuildPaintPanel(out _colorPicker, out _sliderR, out _sliderG, out _sliderB);
        _paintPanel.Visible = false;

        _paint = UiStyle.MakeButton("Paint", new Vector2(168, 38), toggle: true);
        _paint.TooltipText = "LMB: paint the face under the cursor with the selected color";
        _paintDot = UiStyle.AddActiveIndicator(_paint);
        _paint.Toggled += on =>
        {
            _state.Tool = on ? ToolMode.Paint : ToolMode.None;
            _paintPanel.Visible = on;
        };
        column.AddChild(_paint);
        column.AddChild(_paintPanel);

        column.AddChild(new HSeparator());

        _delete = UiStyle.MakeButton("Delete", new Vector2(168, 38), toggle: true);
        _delete.TooltipText = "LMB (or X to toggle this tool): remove the block under the cursor";
        _deleteDot = UiStyle.AddActiveIndicator(_delete);
        _delete.Toggled += on => _state.Tool = on ? ToolMode.Delete : ToolMode.None;
        column.AddChild(_delete);

        _wire = UiStyle.MakeButton("Nodes", new Vector2(168, 38), toggle: true);
        _wire.TooltipText = "N: show the nodes of functional blocks and wire them. Drag LMB from a node to another (either direction) to connect them; drag between two already connected nodes to disconnect. Hold Ctrl to keep a node as the anchor and connect it to several others by clicking them. RMB removes all wires of a node. Esc: cancel / deselect";
        _wireDot = UiStyle.AddActiveIndicator(_wire);
        _wire.Toggled += on => _state.Tool = on ? ToolMode.Wire : ToolMode.None;
        column.AddChild(_wire);

        // Вкладки слоёв логики: показывается только пока включён «Nodes».
        var layers = new VBoxContainer();
        layers.AddThemeConstantOverride("separation", 4);
        var layerRow = UiStyle.Transparent(new HBoxContainer());
        layerRow.AddThemeConstantOverride("separation", 4);
        _layerElectricity = UiStyle.MakeButton("Electricity", new Vector2(82, 32), toggle: true);
        _layerElectricity.TooltipText = "Electricity layer: power nodes (yellow)";
        _layerElectricity.Toggled += on => { if (on) _state.WireLayer = WireLayer.Electricity; else Refresh(); };
        layerRow.AddChild(_layerElectricity);
        _layerLogic = UiStyle.MakeButton("Logic", new Vector2(82, 32), toggle: true);
        _layerLogic.TooltipText = "Logic layer: Number (green) and Boolean (red) nodes";
        _layerLogic.Toggled += on => { if (on) _state.WireLayer = WireLayer.Logic; else Refresh(); };
        layerRow.AddChild(_layerLogic);
        layers.AddChild(layerRow);
        layers.AddChild(UiStyle.MakeLabel("Number - green, Boolean - red", 12, UiStyle.TextDim));
        layers.Visible = false;
        _wireLayerPanel = layers;
        column.AddChild(layers);

        _parameters = UiStyle.MakeButton("Parameters", new Vector2(168, 38), toggle: true);
        _parameters.TooltipText = "P: configure blocks that have settings (seat axes, button mode, motor power...). Dark purple = configurable, cyan = under the cursor; Esc closes the panel";
        _parametersDot = UiStyle.AddActiveIndicator(_parameters);
        _parameters.Toggled += on => _state.Tool = on ? ToolMode.Parameters : ToolMode.None;
        column.AddChild(_parameters);

        _resizePanel = BuildResizePanel(out _resizeFields[0], out _resizeFields[1], out _resizeFields[2]);
        _resizePanel.Visible = false;

        _resize = UiStyle.MakeButton("Resize", new Vector2(168, 38), toggle: true);
        _resize.TooltipText = "Set the size (X/Y/Z) of the next block placed with LMB";
        _resizeDot = UiStyle.AddActiveIndicator(_resize);
        _resize.Toggled += on =>
        {
            _state.ResizePanelOpen = on;
            _resizePanel.Visible = on;
        };
        column.AddChild(_resize);
        column.AddChild(_resizePanel);

        column.AddChild(new HSeparator());

        _wireframe = UiStyle.MakeButton("Wireframe", new Vector2(168, 34), toggle: true);
        _wireframe.TooltipText = "Show only polygons and their diagonals, hide solid blocks";
        _wireframeDot = UiStyle.AddActiveIndicator(_wireframe);
        _wireframe.Toggled += on => _state.Wireframe = on;
        column.AddChild(_wireframe);

        _borders = UiStyle.MakeButton("Borders", new Vector2(168, 34), toggle: true);
        _borders.TooltipText = "Show individual block borders (black), independent of Wireframe";
        _bordersDot = UiStyle.AddActiveIndicator(_borders);
        _borders.Toggled += on => _state.Borders = on;
        column.AddChild(_borders);

        column.AddChild(new HSeparator());

        var save = UiStyle.MakeButton("Save...", new Vector2(168, 34));
        save.TooltipText = "Save the construction to a JSON file";
        save.Pressed += () => SaveRequested?.Invoke();
        column.AddChild(save);

        var load = UiStyle.MakeButton("Load...", new Vector2(168, 34));
        load.TooltipText = "Load a construction from a JSON file (replaces the current one)";
        load.Pressed += () => LoadRequested?.Invoke();
        column.AddChild(load);

        column.AddChild(new HSeparator());
        column.AddChild(UiStyle.MakeLabel("LMB - place block\n(or Delete/Paint when active)", 12, UiStyle.TextDim));

        layerRoot.AddChild(margin);
        Refresh();
    }

    /// <summary>Всё, что связано с выбором цвета — свёрнуто в один блок, видимый только пока активен Paint.</summary>
    private Control BuildPaintPanel(out ColorPickerButton colorPicker, out HSlider sliderR, out HSlider sliderG, out HSlider sliderB)
    {
        var panel = new VBoxContainer();
        panel.AddThemeConstantOverride("separation", 6);

        colorPicker = new ColorPickerButton
        {
            Color = _state.PaintColor,
            EditAlpha = false,
            CustomMinimumSize = new Vector2(168, 30),
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Paint color",
        };
        colorPicker.ColorChanged += color => _state.PaintColor = color;
        panel.AddChild(colorPicker);

        sliderR = CreateChannelSlider(0, "R");
        panel.AddChild(WrapSliderRow("R", sliderR));
        sliderG = CreateChannelSlider(1, "G");
        panel.AddChild(WrapSliderRow("G", sliderG));
        sliderB = CreateChannelSlider(2, "B");
        panel.AddChild(WrapSliderRow("B", sliderB));

        panel.AddChild(UiStyle.MakeLabel("Base colors", 12, UiStyle.TextDim));
        var basePalette = new GridContainer { Columns = 4 };
        basePalette.AddThemeConstantOverride("h_separation", 4);
        basePalette.AddThemeConstantOverride("v_separation", 4);
        foreach (var hex in BasePaletteHex) basePalette.AddChild(CreatePaletteButton(Color.FromHtml(hex)));
        panel.AddChild(basePalette);

        panel.AddChild(UiStyle.MakeLabel("Saved colors", 12, UiStyle.TextDim));
        var customGrid = new GridContainer { Columns = 4 };
        customGrid.AddThemeConstantOverride("h_separation", 4);
        customGrid.AddThemeConstantOverride("v_separation", 4);
        foreach (var color in _customColors) customGrid.AddChild(CreatePaletteButton(color));

        panel.AddChild(customGrid);

        var saveColor = UiStyle.MakeButton("+ Save color", new Vector2(168, 30));
        saveColor.TooltipText = "Add the current paint color to Saved colors (persisted on disk)";
        saveColor.Pressed += () => SaveCurrentColor(customGrid);
        panel.AddChild(saveColor);

        return panel;
    }

    /// <summary>Панель Resize: подсказка сверху, три ряда X/Y/Z (кнопка "-", текстовое поле, кнопка "+") — всегда
    /// активны, редактируют <see cref="EditorState.PendingSize"/> напрямую.</summary>
    private Control BuildResizePanel(out LineEdit fieldX, out LineEdit fieldY, out LineEdit fieldZ)
    {
        var panel = new VBoxContainer();
        panel.AddThemeConstantOverride("separation", 6);

        panel.AddChild(UiStyle.MakeLabel("Size of the next placed block", 12, UiStyle.TextDim));

        var fields = new LineEdit[3];
        for (int axis = 0; axis < 3; axis++)
        {
            panel.AddChild(CreateResizeRow(axis, out fields[axis]));
        }

        fieldX = fields[0];
        fieldY = fields[1];
        fieldZ = fields[2];
        return panel;
    }

    private Control CreateResizeRow(int axis, out LineEdit field)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);

        var label = UiStyle.MakeLabel(AxisNames[axis], 14);
        label.CustomMinimumSize = new Vector2(14, 0);
        row.AddChild(label);

        var minus = UiStyle.MakeButton("-", new Vector2(30, 30));
        minus.Pressed += () => AdjustResize(axis, -1);
        row.AddChild(minus);

        var capturedField = new LineEdit
        {
            Text = "1",
            Editable = true,
            Alignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(52, 30),
            MaxLength = 2,
            FocusMode = Control.FocusModeEnum.Click,
        };
        capturedField.TextChanged += text => FilterDigits(capturedField, text);
        capturedField.TextSubmitted += _ => CommitResizeField(axis, capturedField);
        capturedField.FocusExited += () => CommitResizeField(axis, capturedField);
        row.AddChild(capturedField);
        field = capturedField;

        var plus = UiStyle.MakeButton("+", new Vector2(30, 30));
        plus.Pressed += () => AdjustResize(axis, 1);
        row.AddChild(plus);

        return row;
    }

    private static void FilterDigits(LineEdit field, string text)
    {
        // Значения размера всегда целые и положительные (>= 1) - непечатаемо-цифровые символы отсекаются на лету.
        string digitsOnly = new(text.Where(char.IsDigit).ToArray());
        if (digitsOnly == text) return;

        int caret = field.CaretColumn;
        field.Text = digitsOnly;
        field.CaretColumn = Math.Min(caret, digitsOnly.Length);
    }

    private void CommitResizeField(int axis, LineEdit field)
    {
        if (int.TryParse(field.Text, out int value) && value > 0) _state.SetPendingSizeAxis(axis, value);
        RefreshResizeFields();
    }

    private void AdjustResize(int axis, int delta)
    {
        _state.AdjustPendingSize(axis, delta);
        RefreshResizeFields();
    }

    private void RefreshResizeFields()
    {
        for (int axis = 0; axis < 3; axis++) _resizeFields[axis].Text = _state.PendingSize[axis].ToString();
    }

    private static Control WrapSliderRow(string label, HSlider slider)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var text = UiStyle.MakeLabel(label, 13, UiStyle.TextDim);
        text.CustomMinimumSize = new Vector2(14, 0);
        row.AddChild(text);
        slider.CustomMinimumSize = new Vector2(140, 20);
        slider.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(slider);
        return row;
    }

    private HSlider CreateChannelSlider(int channelIndex, string label)
    {
        var slider = new HSlider { MinValue = 0, MaxValue = 255, Step = 1, FocusMode = Control.FocusModeEnum.None };
        slider.TooltipText = $"{label} (0-255)";
        slider.ValueChanged += value =>
        {
            if (_syncingSliders) return;

            var c = _state.PaintColor;
            float f = (float)(value / 255.0);
            _state.PaintColor = channelIndex switch
            {
                0 => new Color(f, c.G, c.B),
                1 => new Color(c.R, f, c.B),
                _ => new Color(c.R, c.G, f),
            };
        };
        return slider;
    }

    private void SaveCurrentColor(GridContainer targetGrid)
    {
        var color = _state.PaintColor;
        foreach (var existing in _customColors)
        {
            if (existing.ToHtml(false) == color.ToHtml(false)) return; // уже сохранён
        }

        _customColors.Add(color);
        targetGrid.AddChild(CreatePaletteButton(color));
        CustomPalette.Save(CustomPalettePath, _customColors);
    }

    private Button CreatePaletteButton(Color color)
    {
        var button = UiStyle.MakeButton("", new Vector2(38, 26));
        foreach (var name in new[] { "normal", "hover", "pressed", "focus" })
        {
            button.AddThemeStyleboxOverride(name, UiStyle.Box(color, new Color(1f, 1f, 1f, 0.25f), 1, 4, 0f));
        }

        button.Pressed += () => _state.PaintColor = color;
        return button;
    }

    /// <summary>Кнопка-вкладка слоя логики (для самотестов).</summary>
    public Button WireLayerTab(WireLayer layer) => layer == WireLayer.Electricity ? _layerElectricity : _layerLogic;

    public void Refresh()
    {
        _paint.SetPressedNoSignal(_state.Tool == ToolMode.Paint);
        _delete.SetPressedNoSignal(_state.Tool == ToolMode.Delete);
        _resize.SetPressedNoSignal(_state.ResizePanelOpen);
        _paintPanel.Visible = _state.Tool == ToolMode.Paint;
        _resizePanel.Visible = _state.ResizePanelOpen;
        RefreshResizeFields();

        _paintDot.Visible = _state.Tool == ToolMode.Paint;
        _deleteDot.Visible = _state.Tool == ToolMode.Delete;
        _wire.SetPressedNoSignal(_state.Tool == ToolMode.Wire);
        _parameters.SetPressedNoSignal(_state.Tool == ToolMode.Parameters);
        _wireDot.Visible = _state.Tool == ToolMode.Wire;
        _wireLayerPanel.Visible = _state.Tool == ToolMode.Wire;
        _layerElectricity.SetPressedNoSignal(_state.WireLayer == WireLayer.Electricity);
        _layerLogic.SetPressedNoSignal(_state.WireLayer == WireLayer.Logic);
        _parametersDot.Visible = _state.Tool == ToolMode.Parameters;
        _resizeDot.Visible = _state.ResizePanelOpen;

        if (_colorPicker.Color != _state.PaintColor) _colorPicker.Color = _state.PaintColor;

        _syncingSliders = true;
        _sliderR.SetValueNoSignal(Mathf.Round(_state.PaintColor.R * 255f));
        _sliderG.SetValueNoSignal(Mathf.Round(_state.PaintColor.G * 255f));
        _sliderB.SetValueNoSignal(Mathf.Round(_state.PaintColor.B * 255f));
        _syncingSliders = false;

        _wireframe.SetPressedNoSignal(_state.Wireframe);
        _wireframeDot.Visible = _state.Wireframe;
        _borders.SetPressedNoSignal(_state.Borders);
        _bordersDot.Visible = _state.Borders;
    }
}
