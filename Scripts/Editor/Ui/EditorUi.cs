using Godot;

namespace SwV2.Editor.Ui;

/// <summary>Весь интерфейс редактора: хотбар, список блоков (Tab), тулбар справа, инфо-панель слева сверху.</summary>
public sealed class EditorUi
{
    private readonly HotbarUi _hotbar;
    private readonly ToolbarUi _toolbar;
    private readonly BlockPickerUi _picker;
    private readonly Label _info;

    public EditorUi(Node parent, EditorState state)
    {
        var layer = new CanvasLayer { Layer = 10, Name = "Ui" };
        parent.AddChild(layer);

        var root = UiStyle.Transparent(new Control { Name = "UiRoot" });
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(root);

        var infoMargin = UiStyle.Transparent(new MarginContainer());
        infoMargin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_top" }) infoMargin.AddThemeConstantOverride(side, 12);
        var infoColumn = UiStyle.Transparent(new VBoxContainer());
        infoMargin.AddChild(infoColumn);
        _info = UiStyle.MakeLabel("", 13);
        _info.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
        _info.AddThemeConstantOverride("shadow_offset_x", 1);
        _info.AddThemeConstantOverride("shadow_offset_y", 1);
        infoColumn.AddChild(_info);
        root.AddChild(infoMargin);

        // Порядок важен: список блоков поверх сцены, но под хотбаром/тулбаром — слот можно выбрать, не закрывая список.
        _picker = new BlockPickerUi(root, state);
        _hotbar = new HotbarUi(root, state);
        _toolbar = new ToolbarUi(root, state);

        state.Changed += () =>
        {
            _hotbar.Refresh();
            _toolbar.Refresh();
            _picker.Refresh();
        };
    }

    public bool PickerOpen => _picker.IsOpen;

    public void TogglePicker() => _picker.Toggle();

    public void ClosePicker() => _picker.Close();

    public Vector2 GetHotbarSlotCenter(int index) => _hotbar.GetSlotCenter(index);

    public void SetInfo(string text) => _info.Text = text;

    /// <summary>true, если точка экрана лежит над элементом интерфейса (клик по ней не должен попадать в мир).</summary>
    public bool IsPointOverUi(Vector2 point) =>
        _picker.IsOpen || _hotbar.Panel.GetGlobalRect().HasPoint(point) || _toolbar.Panel.GetGlobalRect().HasPoint(point);
}
