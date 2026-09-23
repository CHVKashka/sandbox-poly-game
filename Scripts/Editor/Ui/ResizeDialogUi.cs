using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Editor.Ui;

/// <summary>
/// Диалог инструмента Resize: три поля X/Y/Z с кнопками "-"/"+". Блок всегда растягивается от своего Origin
/// только в положительную сторону каждой оси (см. <see cref="Construction.TrySetSize"/>) — Origin не двигается,
/// отрицательный размер ввести нельзя (кнопка "-" на границе MinSize/1 просто ничего не делает).
/// </summary>
internal sealed class ResizeDialogUi
{
    private static readonly string[] AxisNames = { "X", "Y", "Z" };

    private readonly ColorRect _overlay;
    private readonly Label _title;
    private readonly Label[] _values = new Label[3];

    private Construction? _construction;
    private BlockInstance? _instance;
    private BlockDefinition? _definition;

    public ResizeDialogUi(Control layerRoot)
    {
        _overlay = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.55f),
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _overlay.GuiInput += e =>
        {
            // Клик по затемнению (вне окна) закрывает диалог.
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

        _title = UiStyle.MakeLabel("Resize", 22);
        column.AddChild(_title);
        column.AddChild(UiStyle.MakeLabel("Size in cells - grows from the block's origin", 13, UiStyle.TextDim));

        for (int axis = 0; axis < 3; axis++) column.AddChild(CreateRow(axis));

        column.AddChild(UiStyle.MakeLabel("Esc / click outside - close", 12, UiStyle.TextDim));

        layerRoot.AddChild(_overlay);
    }

    public bool IsOpen => _overlay.Visible;

    public void Open(Construction construction, BlockInstance instance, BlockDefinition definition)
    {
        _construction = construction;
        _instance = instance;
        _definition = definition;
        _title.Text = $"Resize: {definition.Name}";
        Refresh();
        _overlay.Visible = true;
    }

    public void Close() => _overlay.Visible = false;

    private Control CreateRow(int axis)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(260, 0) };
        row.AddThemeConstantOverride("separation", 8);

        var label = UiStyle.MakeLabel(AxisNames[axis], 16);
        label.CustomMinimumSize = new Vector2(20, 0);
        row.AddChild(label);

        var minus = UiStyle.MakeButton("-", new Vector2(34, 34));
        minus.Pressed += () => Adjust(axis, -1);
        row.AddChild(minus);

        _values[axis] = UiStyle.MakeLabel("1", 16);
        _values[axis].CustomMinimumSize = new Vector2(40, 0);
        _values[axis].HorizontalAlignment = HorizontalAlignment.Center;
        row.AddChild(_values[axis]);

        var plus = UiStyle.MakeButton("+", new Vector2(34, 34));
        plus.Pressed += () => Adjust(axis, 1);
        row.AddChild(plus);

        return row;
    }

    private void Adjust(int axis, int delta)
    {
        if (_construction == null || _instance == null || _definition == null) return;

        var size = _instance.Size;
        size[axis] += delta;
        _construction.TrySetSize(_instance, _definition, size);
        Refresh();
    }

    private void Refresh()
    {
        if (_instance == null) return;
        for (int axis = 0; axis < 3; axis++) _values[axis].Text = _instance.Size[axis].ToString();
    }
}
