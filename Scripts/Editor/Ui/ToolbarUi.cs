using Godot;

namespace SwV2.Editor.Ui;

/// <summary>Тулбар справа: перекраска, удаление, режим wireframe. Инструмент действует на ЛКМ.</summary>
internal sealed class ToolbarUi
{
    private static readonly string[] PaletteHex =
    {
        "#d94040", "#e8862c", "#e8cf3a", "#58b04a",
        "#2fb5a6", "#3d78d8", "#8f5bd0", "#d95fa8",
        "#f2f2f2", "#8a8f96", "#3a3d44", "#16171a",
    };

    private readonly EditorState _state;
    private readonly Button _paint;
    private readonly Button _delete;
    private readonly Button _wire;
    private readonly ColorPickerButton _colorPicker;

    public Control Panel { get; }

    public ToolbarUi(Control layerRoot, EditorState state)
    {
        _state = state;

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

        _paint = UiStyle.MakeButton("Paint", new Vector2(168, 38), toggle: true);
        _paint.TooltipText = "LMB: paint the block under the cursor with the selected color";
        _paint.Toggled += on => _state.Tool = on ? ToolMode.Paint : ToolMode.None;
        column.AddChild(_paint);

        _colorPicker = new ColorPickerButton
        {
            Color = _state.PaintColor,
            EditAlpha = false,
            CustomMinimumSize = new Vector2(168, 30),
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Paint color",
        };
        _colorPicker.ColorChanged += color =>
        {
            _state.PaintColor = color;
            _state.Tool = ToolMode.Paint;
        };
        column.AddChild(_colorPicker);

        var palette = new GridContainer { Columns = 4 };
        palette.AddThemeConstantOverride("h_separation", 4);
        palette.AddThemeConstantOverride("v_separation", 4);
        foreach (var hex in PaletteHex) palette.AddChild(CreatePaletteButton(Color.FromHtml(hex)));
        column.AddChild(palette);

        column.AddChild(new HSeparator());

        _delete = UiStyle.MakeButton("Delete", new Vector2(168, 38), toggle: true);
        _delete.TooltipText = "LMB: remove the block under the cursor";
        _delete.Toggled += on => _state.Tool = on ? ToolMode.Delete : ToolMode.None;
        column.AddChild(_delete);

        column.AddChild(new HSeparator());

        _wire = UiStyle.MakeButton("", new Vector2(168, 38));
        _wire.TooltipText = "Cycle: solid / solid + wireframe / wireframe only";
        _wire.Pressed += _state.CycleWire;
        column.AddChild(_wire);

        column.AddChild(new HSeparator());
        column.AddChild(UiStyle.MakeLabel("RMB - place block\nLMB - use tool", 12, UiStyle.TextDim));

        layerRoot.AddChild(margin);
        Refresh();
    }

    private Button CreatePaletteButton(Color color)
    {
        var button = UiStyle.MakeButton("", new Vector2(38, 26));
        foreach (var name in new[] { "normal", "hover", "pressed", "focus" })
        {
            button.AddThemeStyleboxOverride(name, UiStyle.Box(color, new Color(1f, 1f, 1f, 0.25f), 1, 4, 0f));
        }

        button.Pressed += () =>
        {
            _state.PaintColor = color;
            _state.Tool = ToolMode.Paint;
        };
        return button;
    }

    public void Refresh()
    {
        _paint.SetPressedNoSignal(_state.Tool == ToolMode.Paint);
        _delete.SetPressedNoSignal(_state.Tool == ToolMode.Delete);
        if (_colorPicker.Color != _state.PaintColor) _colorPicker.Color = _state.PaintColor;

        _wire.Text = _state.Wire switch
        {
            WireMode.Off => "Wireframe: off",
            WireMode.Overlay => "Wireframe: overlay",
            _ => "Wireframe: only",
        };
    }
}
