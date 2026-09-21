using Godot;
using SwV2.Core;

namespace SwV2.Editor.Ui;

/// <summary>Окно со списком всех доступных блоков (Tab). Клик по блоку кладёт его в выбранный слот хотбара.</summary>
internal sealed class BlockPickerUi
{
    private readonly EditorState _state;
    private readonly ColorRect _overlay;
    private readonly Label _hint;

    public BlockPickerUi(Control layerRoot, EditorState state)
    {
        _state = state;

        _overlay = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.55f),
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _overlay.GuiInput += e =>
        {
            // Клик по затемнению (вне окна) закрывает список.
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Close();
        };

        var center = UiStyle.Transparent(new CenterContainer());
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _overlay.AddChild(center);

        var window = new PanelContainer();
        window.AddThemeStyleboxOverride("panel", UiStyle.Panel(16f));
        center.AddChild(window);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        window.AddChild(column);

        column.AddChild(UiStyle.MakeLabel("Blocks", 22));
        _hint = UiStyle.MakeLabel("", 13, UiStyle.TextDim);
        column.AddChild(_hint);

        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 8);
        column.AddChild(grid);

        foreach (var def in BlockRegistry.All) grid.AddChild(CreateCard(def));

        column.AddChild(UiStyle.MakeLabel("Tab / Esc - close", 12, UiStyle.TextDim));

        layerRoot.AddChild(_overlay);
        Refresh();
    }

    public bool IsOpen => _overlay.Visible;

    public void Open()
    {
        _overlay.Visible = true;
        Refresh();
    }

    public void Close() => _overlay.Visible = false;

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Refresh() =>
        _hint.Text = $"Click a block to put it into hotbar slot {_state.SelectedSlot + 1} (keys 1-9 or mouse wheel change the slot)";

    private Control CreateCard(BlockDefinition def)
    {
        var button = UiStyle.MakeButton("", new Vector2(190, 58));
        button.TooltipText = def.Name;
        button.Pressed += () => _state.SetSlot(_state.SelectedSlot, def.Id);

        var margin = UiStyle.Transparent(new MarginContainer());
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, 8);
        }

        var row = UiStyle.Transparent(new HBoxContainer());
        row.AddThemeConstantOverride("separation", 10);

        row.AddChild(new ColorRect
        {
            Color = def.DefaultColor,
            CustomMinimumSize = new Vector2(38, 38),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });

        var name = UiStyle.MakeLabel(def.Name, 15);
        name.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(name);

        margin.AddChild(row);
        button.AddChild(margin);
        return button;
    }
}
