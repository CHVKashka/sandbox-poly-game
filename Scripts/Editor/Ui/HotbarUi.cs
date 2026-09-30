using Godot;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Editor.Ui;

/// <summary>Хотбар на 9 слотов внизу по центру. Клик по слоту (или клавиши 1–9) выбирает слот — колесо мыши с этого
/// релиза зумит камеру (см. <c>BuildEditor</c>), а не листает слоты.</summary>
internal sealed class HotbarUi
{
    private sealed class Slot
    {
        public PanelContainer Panel = null!;
        public Control IconHost = null!;
        public Label Name = null!;
        public string CurrentSlug = "";
    }

    private readonly EditorState _state;
    private readonly Slot[] _slots = new Slot[EditorState.HotbarSize];

    public Control Panel { get; }

    public HotbarUi(Control layerRoot, EditorState state)
    {
        _state = state;

        var margin = UiStyle.Transparent(new MarginContainer());
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_bottom", 14);

        var column = UiStyle.Transparent(new VBoxContainer { Alignment = BoxContainer.AlignmentMode.End });
        margin.AddChild(column);

        var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(8f));
        column.AddChild(panel);
        Panel = panel;

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        panel.AddChild(row);

        for (int i = 0; i < _slots.Length; i++) _slots[i] = CreateSlot(row, i);

        layerRoot.AddChild(margin);
        Refresh();
    }

    private Slot CreateSlot(Control parent, int index)
    {
        var slot = new Slot
        {
            Panel = new PanelContainer
            {
                CustomMinimumSize = new Vector2(78, 78),
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            },
        };

        var column = UiStyle.Transparent(new VBoxContainer());
        column.AddThemeConstantOverride("separation", 2);

        column.AddChild(UiStyle.MakeLabel((index + 1).ToString(), 12, UiStyle.TextDim));

        slot.IconHost = UiStyle.Transparent(new CenterContainer
        {
            CustomMinimumSize = new Vector2(0, 34),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        });
        column.AddChild(slot.IconHost);

        slot.Name = UiStyle.MakeLabel("", 11);
        slot.Name.HorizontalAlignment = HorizontalAlignment.Center;
        slot.Name.ClipText = true;
        column.AddChild(slot.Name);

        slot.Panel.AddChild(column);
        slot.Panel.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) _state.SelectedSlot = index;
        };

        parent.AddChild(slot.Panel);
        return slot;
    }

    public Vector2 GetSlotCenter(int index) => _slots[index].Panel.GetGlobalRect().GetCenter();

    public void Refresh()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = _slots[i];
            bool selected = i == _state.SelectedSlot;
            string slug = _state.GetSlot(i);

            // Иконка — мини 3D-сцена (см. BlockIconView), а не просто цвет: пересобираем только когда слаг слота
            // реально изменился (Refresh вызывается на каждое EditorState.Changed, в т.ч. не связанное с хотбаром).
            if (slot.CurrentSlug != slug)
            {
                slot.CurrentSlug = slug;
                foreach (var child in slot.IconHost.GetChildren()) child.QueueFree();

                if (BlockCatalog.Instance.TryGetBySlug(slug, out var iconDef))
                {
                    slot.IconHost.AddChild(BlockIconView.Create(iconDef, 40));
                }
            }

            if (BlockCatalog.Instance.TryGetBySlug(slug, out var def))
            {
                slot.Name.Text = def.Name;
            }
            else
            {
                slot.Name.Text = "-";
            }

            slot.Panel.AddThemeStyleboxOverride("panel", selected
                ? UiStyle.Box(new Color(0.16f, 0.17f, 0.20f, 0.95f), UiStyle.Accent, 3, 6, 5f)
                : UiStyle.Box(new Color(0.11f, 0.12f, 0.14f, 0.9f), UiStyle.PanelBorder, 1, 6, 5f));
        }
    }
}
