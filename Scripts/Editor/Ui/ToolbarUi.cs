using System;
using System.Collections.Generic;
using Godot;

namespace SandboxPolyGame.Editor.Ui;

/// <summary>
/// Тулбар справа: перекраска (с палитрой, см. ниже), удаление, растягивание (Resize), режим wireframe,
/// сохранение/загрузка постройки. Paint/Delete/Resize действуют на ПКМ (взаимоисключающие инструменты).
/// </summary>
internal sealed class ToolbarUi
{
    private const string CustomPalettePath = "user://custom_palette.cfg";

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
    private readonly Button _wire;
    private readonly Control _paintPanel;
    private readonly ColorPickerButton _colorPicker;
    private readonly HSlider _sliderR;
    private readonly HSlider _sliderG;
    private readonly HSlider _sliderB;
    private readonly List<Color> _customColors;
    private bool _syncingSliders;

    /// <summary>Нажата кнопка Save — открыть диалог выбора файла (см. <see cref="EditorUi"/>).</summary>
    public event Action? SaveRequested;

    /// <summary>Нажата кнопка Load — открыть диалог выбора файла (см. <see cref="EditorUi"/>).</summary>
    public event Action? LoadRequested;

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

        column.AddChild(UiStyle.MakeLabel("Tools", 18));

        _paintPanel = BuildPaintPanel(out _colorPicker, out _sliderR, out _sliderG, out _sliderB);
        _paintPanel.Visible = false;

        _paint = UiStyle.MakeButton("Paint", new Vector2(168, 38), toggle: true);
        _paint.TooltipText = "RMB: paint the block under the cursor with the selected color";
        _paint.Toggled += on =>
        {
            _state.Tool = on ? ToolMode.Paint : ToolMode.None;
            _paintPanel.Visible = on;
        };
        column.AddChild(_paint);
        column.AddChild(_paintPanel);

        column.AddChild(new HSeparator());

        _delete = UiStyle.MakeButton("Delete", new Vector2(168, 38), toggle: true);
        _delete.TooltipText = "RMB: remove the block under the cursor";
        _delete.Toggled += on => _state.Tool = on ? ToolMode.Delete : ToolMode.None;
        column.AddChild(_delete);

        _resize = UiStyle.MakeButton("Resize", new Vector2(168, 38), toggle: true);
        _resize.TooltipText = "RMB on a block opens a size dialog (X/Y/Z, +/-)";
        _resize.Toggled += on => _state.Tool = on ? ToolMode.Resize : ToolMode.None;
        column.AddChild(_resize);

        column.AddChild(new HSeparator());

        _wire = UiStyle.MakeButton("", new Vector2(168, 38));
        _wire.TooltipText = "Cycle: solid / solid + wireframe / wireframe only";
        _wire.Pressed += _state.CycleWire;
        column.AddChild(_wire);

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
        column.AddChild(UiStyle.MakeLabel("LMB - place block\nRMB - use tool", 12, UiStyle.TextDim));

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

    public void Refresh()
    {
        _paint.SetPressedNoSignal(_state.Tool == ToolMode.Paint);
        _delete.SetPressedNoSignal(_state.Tool == ToolMode.Delete);
        _resize.SetPressedNoSignal(_state.Tool == ToolMode.Resize);
        _paintPanel.Visible = _state.Tool == ToolMode.Paint;

        if (_colorPicker.Color != _state.PaintColor) _colorPicker.Color = _state.PaintColor;

        _syncingSliders = true;
        _sliderR.SetValueNoSignal(Mathf.Round(_state.PaintColor.R * 255f));
        _sliderG.SetValueNoSignal(Mathf.Round(_state.PaintColor.G * 255f));
        _sliderB.SetValueNoSignal(Mathf.Round(_state.PaintColor.B * 255f));
        _syncingSliders = false;

        _wire.Text = _state.Wire switch
        {
            WireMode.Off => "Wireframe: off",
            WireMode.Overlay => "Wireframe: overlay",
            _ => "Wireframe: only",
        };
    }
}
