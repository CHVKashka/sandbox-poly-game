using System;
using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

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
        // Хотбар по умолчанию заполнен первыми блоками каталога — обычные (резиновые) блоки идут первыми, затем
        // функциональные (каждая группа в своём алфавитном порядке) - иначе добавление новых функциональных блоков
        // в каталог молча вытесняло бы из хотбара уже привычные базовые формы (block/wedge/pyramid/inverse_pyramid).
        var all = BlockCatalog.Instance.All;
        var ordered = new List<BlockDefinition>(all.Count);
        foreach (var definition in all) if (!definition.HasComponent<FunctionalBlockComponent>()) ordered.Add(definition);
        foreach (var definition in all) if (definition.HasComponent<FunctionalBlockComponent>()) ordered.Add(definition);
        for (int i = 0; i < HotbarSize && i < ordered.Count; i++) _hotbar[i] = ordered[i].Slug;
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
        // Если это переопределило СЕЙЧАС выбранный слот - размер призрака может быть не годен для нового блока
        // (например, слот держал резиновый блок с PendingSize 5x5x5, переназначен на функциональный блок с
        // фиксированным footprint 1x1x1) - пересчитать сразу, не дожидаясь следующей смены слота.
        if (slot == _selectedSlot) _pendingSize = ClampToSelectedBlock(_pendingSize);
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

    /// <summary>Фиксированные мировые оси вращения — ИНДЕКС совпадает с осью (0=X/J, 1=Y/K, 2=Z/L), сам вектор
    /// НЕ зависит от текущей ориентации блока (см. <see cref="RotatePending"/> class doc).</summary>
    private static readonly Vector3[] GlobalRotationAxes = { Vector3.Right, Vector3.Up, new Vector3(0, 0, 1) };

    /// <summary>
    /// Накопленная ориентация блока, который встанет следующим на ЛКМ — авторитетное представление (см.
    /// <see cref="RotatePending"/>); <see cref="PendingRotationSteps"/> ниже — лишь его проекция на формат хранения
    /// <see cref="Core.BlockInstance.RotationSteps"/>. Используется плавной анимацией довода призрака на J/K/L (см.
    /// <c>Editor.BuildEditor</c>, <c>_ghostVisualBasis</c>) — ей нужна ТОЧНАЯ целевая ориентация для интерполяции
    /// (Slerp), а не производные три числа 0..3.
    /// </summary>
    public Basis PendingRotationBasis { get; private set; } = Basis.Identity;

    /// <summary>
    /// Ориентация блока, который встанет следующим на ЛКМ (см. <c>BuildEditor.PlaceAtHover</c>) — три четверть-поворота
    /// вокруг X/Y/Z (см. <see cref="Core.BlockInstance.RotationSteps"/>/<see cref="Core.ShapeMeshBuilder.ComposeRotation"/>),
    /// подобранные так, чтобы ВОСПРОИЗВОДИТЬ <see cref="PendingRotationBasis"/> (см. <see cref="FindStepsFor"/>) —
    /// именно ЭТО значение в итоге сохраняется в <see cref="Core.BlockInstance.RotationSteps"/> при установке, формат
    /// хранения/сериализации не меняется. Меняется клавишами J (X) / K (Y) / L (Z) и сохраняется между установками,
    /// пока не изменена снова. На уже поставленные блоки не влияет.
    /// </summary>
    public Vector3I PendingRotationSteps { get; private set; } = Vector3I.Zero;

    public void RotatePendingX() => RotatePending(0);
    public void RotatePendingY() => RotatePending(1);
    public void RotatePendingZ() => RotatePending(2);

    /// <summary>
    /// Поворот ВСЕГДА вокруг глобальной (мировой, фиксированной) оси — новый поворот ПРЕДУМНОЖАЕТСЯ на уже
    /// накопленный (<c>new Basis(axis, angle) * PendingRotationBasis</c>), а не вокруг текущей, уже повёрнутой
    /// ЛОКАЛЬНОЙ оси блока (что дало бы послеумножение, <c>PendingRotationBasis * new Basis(axis, angle)</c>).
    /// <para/>
    /// Раньше (баг, найденный пользователем, 2026-10-01 (8)) ориентация не накапливалась инкрементально вообще —
    /// каждый вызов пересобирал её ЗАНОВО из трёх НЕЗАВИСИМЫХ счётчиков в ФИКСИРОВАННОМ порядке X→Y→Z
    /// (<c>Rz(z)·Ry(y)·Rx(x)</c>), независимо от РЕАЛЬНОГО порядка нажатий. Пока нажимались клавиши одной оси подряд
    /// или строго в порядке X,Y,Z — результат случайно совпадал с ожидаемым; как только оси чередовались (например,
    /// K, затем J) — пересборка в фиксированном порядке давала СОВСЕМ другую ориентацию, чем "доверни ещё на 90°
    /// вокруг мировой X от того, что уже есть" — внешне это выглядело так, будто кнопки вращения "меняются местами".
    /// Теперь ориентация — ЕДИНСТВЕННЫЙ накапливаемый <see cref="Basis"/>, обновляемый строго в порядке реальных
    /// нажатий; <see cref="PendingRotationSteps"/> лишь подбирается ПОД него для хранения/сериализации, никогда не
    /// участвует в вычислении самой ориентации.
    /// </summary>
    private void RotatePending(int axis)
    {
        PendingRotationBasis = new Basis(GlobalRotationAxes[axis], Mathf.Pi / 2f) * PendingRotationBasis;
        PendingRotationSteps = FindStepsFor(PendingRotationBasis);
        Changed?.Invoke();
    }

    /// <summary>
    /// Подбирает (x,y,z) в 0..3, при котором <see cref="Core.ShapeMeshBuilder.ComposeRotation(Vector3I)"/> ближе
    /// всего к <paramref name="target"/> (скалярное произведение кватернионов по модулю, 1 — идеальное совпадение).
    /// Совпадение гарантированно существует: любая композиция 90°-поворотов вокруг мировых X/Y/Z — всегда элемент
    /// группы вращений куба (24 элемента), а полный перебор (x,y,z) как раз её и перечисляет (64 варианта с
    /// повторами на 24 уникальных) — "ближайший", а не точное равенство, для устойчивости к накоплению погрешности
    /// float после многих нажатий подряд.
    /// </summary>
    private static Vector3I FindStepsFor(Basis target)
    {
        var targetQuat = new Quaternion(target);
        var best = Vector3I.Zero;
        var bestDot = -1.0;

        for (int x = 0; x < 4; x++)
        for (int y = 0; y < 4; y++)
        for (int z = 0; z < 4; z++)
        {
            var candidate = new Quaternion(ShapeMeshBuilder.ComposeRotation(new Vector3I(x, y, z)));
            var dot = Math.Abs(targetQuat.Dot(candidate));
            if (dot > bestDot)
            {
                bestDot = dot;
                best = new Vector3I(x, y, z);
            }
        }

        return best;
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

    /// <summary>
    /// Блок с <see cref="FunctionalBlockComponent"/> игнорирует запрошенный размер целиком и всегда возвращает свой
    /// фиксированный <see cref="FunctionalBlockComponent.Footprint"/> - такие блоки не резинятся (нет
    /// <see cref="BuildingBlockComponent"/>, см. <see cref="Core.Construction.TrySetSize"/>), Resize-панель на
    /// тулбаре для них не действует ни на одну из осей.
    /// </summary>
    private Vector3I ClampToSelectedBlock(Vector3I size)
    {
        var min = Vector3I.One;
        var max = new Vector3I(8, 8, 8);
        if (BlockCatalog.Instance.TryGetBySlug(SelectedBlockSlug, out var definition))
        {
            var functional = definition.GetComponent<FunctionalBlockComponent>();
            if (functional != null) return functional.Footprint;

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
