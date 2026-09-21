using System;
using Godot;
using SwV2.Core;

namespace SwV2.Editor;

/// <summary>Инструмент, действующий на ЛКМ. Установка блока (ПКМ) работает всегда независимо от инструмента.</summary>
public enum ToolMode
{
    None,
    Paint,
    Delete,
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

    private readonly ushort[] _hotbar = new ushort[HotbarSize];
    private int _selectedSlot;
    private ToolMode _tool = ToolMode.None;
    private Color _paintColor = Color.FromHtml("#d94040");
    private WireMode _wire = WireMode.Off;

    public event Action? Changed;

    public EditorState()
    {
        // Хотбар по умолчанию заполнен первыми блоками списка.
        for (int i = 0; i < HotbarSize && i < BlockRegistry.All.Count; i++) _hotbar[i] = BlockRegistry.All[i].Id;
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

    public ushort SelectedBlockId => _hotbar[_selectedSlot];

    public ushort GetSlot(int slot) => _hotbar[slot];

    public void SetSlot(int slot, ushort blockId)
    {
        if (slot < 0 || slot >= HotbarSize || _hotbar[slot] == blockId) return;
        _hotbar[slot] = blockId;
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
}
