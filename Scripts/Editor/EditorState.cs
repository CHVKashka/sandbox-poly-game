using System;
using Godot;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Editor;

/// <summary>Инструмент, действующий на ЛКМ (см. <c>Editor.BuildEditor.ButtonFor</c>) — Delete и Paint делят эту
/// кнопку, не конфликтуя, потому что активен максимум один из них разом. Пока активен любой из них, обычная
/// установка блока по ЛКМ выключена (см. <c>Editor.BuildEditor</c> <c>_UnhandledInput</c>/<c>UpdateCursorVisuals</c>).
/// Resize сюда не входит: он не действует на уже поставленные блоки, а настраивает размер ПРИЗРАКА (см.
/// <see cref="EditorState.PendingSize"/>/<see cref="EditorState.ResizePanelOpen"/>), поэтому кнопку мыши не занимает
/// вообще.</summary>
public enum ToolMode
{
    None,
    Paint,
    Delete,
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
    private bool _wireframe;
    private bool _borders = true;
    private bool _resizePanelOpen;
    private Vector3I _pendingSize = Vector3I.One;
    private Vector3I _pendingMirror = Vector3I.Zero;

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
            _pendingSize = ClampToSelectedBlock(_pendingSize); // новый блок может иметь другой MaxSize
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

    /// <summary>Инструмент Wireframe: только полигоны и их диагонали текущим цветом, без сплошных граней.</summary>
    public bool Wireframe
    {
        get => _wireframe;
        set
        {
            if (value == _wireframe) return;
            _wireframe = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Инструмент Borders: чёрные границы отдельных блоков, независимо от Wireframe. Включён по умолчанию.</summary>
    public bool Borders
    {
        get => _borders;
        set
        {
            if (value == _borders) return;
            _borders = value;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Ориентация блока, который встанет следующим на ЛКМ (см. <c>BuildEditor.PlaceAtHover</c>) — три четверть-поворота
    /// вокруг X/Y/Z (см. <see cref="Core.BlockInstance.RotationSteps"/>). Меняется клавишами J (X) / K (Y) / L (Z)
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

    /// <summary>
    /// Отражение блока, который встанет следующим на ЛКМ — по компоненте на X/Y/Z, 0/1 (см.
    /// <see cref="Core.BlockInstance.Mirror"/>/<see cref="Core.ShapeMeshBuilder.BuildData"/>). Меняется клавишами
    /// U (X) / I (Y) / O (Z), сохраняется между установками, пока не изменено снова. На уже поставленные блоки
    /// не влияет — как и вращение выше, а не двигает их (см. WORKLOG: прежний инструмент "отскакивания" двигал
    /// блок на +1 клетку — это была ошибка, вместо отражения вершин).
    /// </summary>
    public Vector3I PendingMirror { get; private set; } = Vector3I.Zero;

    public void ToggleMirrorX() => ToggleMirror(0);
    public void ToggleMirrorY() => ToggleMirror(1);
    public void ToggleMirrorZ() => ToggleMirror(2);

    private void ToggleMirror(int axis)
    {
        var m = PendingMirror;
        m[axis] = m[axis] == 0 ? 1 : 0;
        PendingMirror = m;
        Changed?.Invoke();
    }

    /// <summary>Открыта ли панель Resize на тулбаре (см. <see cref="Ui.ToolbarUi"/>) — чисто визуальный переключатель,
    /// не влияет на то, действует ли <see cref="PendingSize"/> (он действует всегда, панель лишь показывает поля).</summary>
    public bool ResizePanelOpen
    {
        get => _resizePanelOpen;
        set
        {
            if (value == _resizePanelOpen) return;
            _resizePanelOpen = value;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Размер блока, который встанет следующим на ЛКМ (см. <c>BuildEditor.PlaceAtHover</c>) — растёт от клетки под
    /// курсором только в положительную сторону каждой оси, как раньше рос уже поставленный блок инструментом
    /// Resize. Панель Resize на тулбаре (X/Y/Z, поля + кнопки +/-) редактирует именно это значение, а не размер
    /// уже поставленных блоков — те, установленные ранее, Resize не трогает. Зажат в
    /// <c>[max(1, BuildingBlock.MinSize) .. BuildingBlock.MaxSize]</c> выбранного в хотбаре блока (см.
    /// <see cref="ClampToSelectedBlock"/>); значение сохраняется между установками, пока не изменено снова.
    /// </summary>
    public Vector3I PendingSize
    {
        get => _pendingSize;
        private set
        {
            if (value == _pendingSize) return;
            _pendingSize = value;
            Changed?.Invoke();
        }
    }

    public void AdjustPendingSize(int axis, int delta) => SetPendingSizeAxis(axis, _pendingSize[axis] + delta);

    public void SetPendingSizeAxis(int axis, int value)
    {
        var size = _pendingSize;
        size[axis] = value;
        PendingSize = ClampToSelectedBlock(size);
    }

    private Vector3I ClampToSelectedBlock(Vector3I size)
    {
        var min = Vector3I.One;
        var max = new Vector3I(8, 8, 8);
        if (BlockCatalog.Instance.TryGetBySlug(SelectedBlockSlug, out var definition))
        {
            var building = definition.GetComponent<BuildingBlockComponent>();
            if (building != null)
            {
                min = building.MinSize;
                max = building.MaxSize;
            }
        }

        return new Vector3I(
            Math.Clamp(size.X, Math.Max(1, min.X), Math.Max(1, max.X)),
            Math.Clamp(size.Y, Math.Max(1, min.Y), Math.Max(1, max.Y)),
            Math.Clamp(size.Z, Math.Max(1, min.Z), Math.Max(1, max.Z)));
    }
}
