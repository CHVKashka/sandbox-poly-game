using System;
using Godot;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Editor;

/// <summary>Инструмент, действующий на ПКМ. Установка блока (ЛКМ) работает всегда независимо от инструмента.</summary>
public enum ToolMode
{
    None,
    Paint,
    Delete,

    /// <summary>ПКМ на блоке с компонентом BuildingBlock открывает диалог Resize (X/Y/Z, кнопки +/-) — см.
    /// <see cref="Ui.ResizeDialogUi"/>.</summary>
    Resize,
}

/// <summary>Режим отображения каркаса постройки.</summary>
public enum WireMode
{
    /// <summary>Только сплошные грани.</summary>
    Off,

    /// <summary>Сплошные грани + линии каркаса поверх.</summary>
    Overlay,

    /// <summary>Только каркас (грани скрыты).</summary>
    WireOnly,
}

/// <summary>Общее состояние редактора; UI подписывается на <see cref="Changed"/> и перерисовывается.</summary>
public sealed class EditorState
{
    public const int HotbarSize = 9;

    /// <summary>Слаг блока (<see cref="BlockDefinition.Slug"/>) или "" — слот пуст.</summary>
    private readonly string[] _hotbar = new string[HotbarSize];

    private int _selectedSlot;
    private ToolMode _tool = ToolMode.None;
    private Color _paintColor = Color.FromHtml("#d94040");
    private WireMode _wire = WireMode.Off;

    public event Action? Changed;

    public EditorState()
    {
        Array.Fill(_hotbar, "");
        // Хотбар по умолчанию заполнен первыми блоками каталога.
        var all = BlockCatalog.Instance.All;
        for (int i = 0; i < HotbarSize && i < all.Count; i++) _hotbar[i] = all[i].Slug;
    }

    public int SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            value = Mathf.Clamp(value, 0, HotbarSize - 1);
            if (value == _selectedSlot) return;
            _selectedSlot = value;
            Changed?.Invoke();
        }
    }

    public string SelectedBlockSlug => _hotbar[_selectedSlot];

    public string GetSlot(int slot) => _hotbar[slot];

    public void SetSlot(int slot, string blockSlug)
    {
        if (slot < 0 || slot >= HotbarSize || _hotbar[slot] == blockSlug) return;
        _hotbar[slot] = blockSlug;
        Changed?.Invoke();
    }

    public ToolMode Tool
    {
        get => _tool;
        set
        {
            if (value == _tool) return;
            _tool = value;
            Changed?.Invoke();
        }
    }

    public Color PaintColor
    {
        get => _paintColor;
        set
        {
            if (value == _paintColor) return;
            _paintColor = value;
            Changed?.Invoke();
        }
    }

    public WireMode Wire
    {
        get => _wire;
        set
        {
            if (value == _wire) return;
            _wire = value;
            Changed?.Invoke();
        }
    }

    public void CycleWire() => Wire = (WireMode)(((int)_wire + 1) % 3);

    /// <summary>
    /// Ориентация блока, который встанет следующим на ЛКМ (см. <c>BuildEditor.PlaceAtHover</c>) — три четверть-поворота
    /// вокруг X/Y/Z (см. <see cref="Core.BlockInstance.RotationSteps"/>). Меняется клавишами J (X) / K (Y) / I (Z)
    /// и сохраняется между установками, пока не изменена снова. На уже поставленные блоки не влияет.
    /// </summary>
    public Vector3I PendingRotationSteps { get; private set; } = Vector3I.Zero;

    public void RotatePendingX() => RotatePending(0);
    public void RotatePendingY() => RotatePending(1);
    public void RotatePendingZ() => RotatePending(2);

    private void RotatePending(int axis)
    {
        var r = PendingRotationSteps;
        r[axis] = (r[axis] + 1) % 4;
        PendingRotationSteps = r;
        Changed?.Invoke();
    }
}
