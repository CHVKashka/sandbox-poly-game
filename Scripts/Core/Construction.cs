using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Core;

/// <summary>
/// Постройка: набор размещённых блоков (<see cref="BlockInstance"/>) поверх <see cref="VoxelGrid"/>.
/// Держит соответствие клетка → владеющий её экземпляр и синхронизирует занятые клетки с решёткой
/// (которую использует меширование/рейкаст, ничего не зная про экземпляры). Сериализуется в/из JSON —
/// см. <c>ConstructionIO</c>.
///
/// Клетки, попавшие в решётку в обход этого класса (например, инструменты разработчика в <c>Dev/DemoBuilds</c>,
/// пишущие в <see cref="Grid"/> напрямую), не имеют владеющего экземпляра — это осознанно: такие клетки нельзя
/// растянуть инструментом Resize, но красить/удалять их по-прежнему можно поклеточно (см. <c>Editor.BuildEditor</c>).
/// </summary>
public sealed class Construction
{
    private readonly Dictionary<int, BlockInstance> _instances = new();
    private readonly Dictionary<Vector3I, int> _owner = new();
    private int _nextId = 1;

    public VoxelGrid Grid { get; }

    public Construction(VoxelGrid grid) => Grid = grid;

    public IReadOnlyCollection<BlockInstance> Instances => _instances.Values;

    /// <summary>
    /// Экземпляр создан/удалён/перекрашен/изменил размер. Слушает визуальный слой не-кубических форм
    /// (<c>Editor.ShapeInstanceView</c>), которому нужно пересобрать меш при любом из этих изменений —
    /// в т.ч. Resize, который двигает вершины формы (см. <see cref="TrySetSize"/>/<c>ShapeMeshBuilder</c>).
    /// </summary>
    public event Action? Changed;

    /// <summary>Экземпляр, которому принадлежит клетка, или null (клетка пуста или заполнена в обход Construction).</summary>
    public BlockInstance? GetOwner(Vector3I cell) => _owner.TryGetValue(cell, out int id) ? _instances[id] : null;

    /// <summary>Ставит новый блок 1x1x1 в клетку. null, если клетка занята или вне области.</summary>
    public BlockInstance? Place(Vector3I cell, BlockDefinition definition, Color color, Vector3I rotationSteps = default, Vector3I mirror = default) =>
        PlaceBlock(cell, Vector3I.One, definition, color, rotationSteps, mirror);

    /// <summary>
    /// Ставит блок сразу заданного размера (используется как установкой блока игроком — размер/поворот/отражение
    /// берутся из ожидающих настроек призрака, см. <c>Editor.EditorState</c>, — так и загрузкой построек). null,
    /// если хотя бы одна из клеток области занята или вне области построек.
    /// </summary>
    public BlockInstance? PlaceBlock(Vector3I origin, Vector3I size, BlockDefinition definition, Color color, Vector3I rotationSteps = default, Vector3I mirror = default)
    {
        var instance = new BlockInstance
        {
            InstanceId = _nextId,
            BlockSlug = definition.Slug,
            Origin = origin,
            Size = size,
            Color = CellColor.Pack(color),
            RotationSteps = rotationSteps,
            Mirror = mirror,
        };

        var cells = new List<Vector3I>(CellsOf(instance));
        foreach (var cell in cells)
        {
            if (!BuildSpace.InBounds(cell) || Grid.IsSolid(cell)) return null;
        }

        byte instanceMask = FullCoverageMask(definition, rotationSteps, mirror);
        _nextId++;
        _instances[instance.InstanceId] = instance;
        foreach (var cell in cells)
        {
            _owner[cell] = instance.InstanceId;
            Grid.TrySet(cell, definition.RuntimeId, instance.Color, BoundaryFaceMask(instanceMask, cell, instance.Origin, instance.MaxCell));
        }

        Changed?.Invoke();
        return instance;
    }

    /// <summary>
    /// Какие из 6 осевых сторон клетки этот блок закрывает ЦЕЛИКОМ, с учётом его поворота/отражения — передаётся
    /// в <see cref="VoxelGrid.TrySet"/>, чтобы <see cref="ChunkMesher"/> мог отсекать/склеивать эти стороны с
    /// соседями наравне с кубами (см. <see cref="ShapeMeshBuilder.FullCoverageMask"/>). Куб (или функциональный блок
    /// без собственной модели, см. <see cref="FunctionalBlockComponent"/>) закрыт целиком со всех 6 сторон — рисуется
    /// как куб-плейсхолдер. Функциональный блок С моделью (<see cref="FunctionalBlockComponent.ScenePath"/> задан) —
    /// 0 (ничего не закрывает): его рисует не <see cref="ChunkMesher"/>, а отдельный узел на экземпляр
    /// (<see cref="Editor.FunctionalBlockView"/>, как и <see cref="Editor.ShapeInstanceView"/> для процедурных форм) —
    /// без этого под настоящей моделью продолжал бы просвечивать цветной куб.
    /// </summary>
    private static byte FullCoverageMask(BlockDefinition definition, Vector3I rotationSteps, Vector3I mirror)
    {
        var functional = definition.GetComponent<FunctionalBlockComponent>();
        if (functional != null) return string.IsNullOrEmpty(functional.ScenePath) ? (byte)0b111111 : (byte)0;

        var building = definition.GetComponent<BuildingBlockComponent>();
        if (building == null || building.Shape == BlockShape.Cube) return 0b111111;
        return ShapeMeshBuilder.FullCoverageMask(building.Shape, rotationSteps, mirror);
    }

    /// <summary>
    /// <paramref name="instanceMask"/> описывает, какие стороны ЕДИНИЧНОЙ формы блока закрыты целиком (см.
    /// <see cref="FullCoverageMask"/>) — это относится к форме САМОЙ ПО СЕБЕ, а не к конкретной клетке внутри
    /// растянутого Resize'ом многоклеточного экземпляра. Клетка ВНУТРИ такого экземпляра (не на границе его
    /// bounding box вдоль соответствующей оси) на самом деле ни с какой стороны не "заканчивается" — с этой стороны
    /// у неё точно такой же сосед из ТОГО ЖЕ экземпляра, поэтому включать туда бит нельзя: у формы направленный
    /// только в одну сторону (например, только Y- у Wedge, но не Y+), два таких соседа НЕ гасят друг друга взаимно
    /// (в отличие от куба, у которого закрыты сразу обе стороны каждой оси) — <see cref="ChunkMesher"/> решил бы,
    /// что внутренняя клетка "закрывает" сторону, которой сосед не противопоставляет встречный бит, и нарисовал бы
    /// ложную, торчащую наружу внутреннюю стену прямо посреди фигуры (на каждой внутренней границе клеток вдоль этой
    /// оси - тем заметнее, чем крупнее Resize). Поэтому бит направления оставляем только на КРАЙНЕМ слое клеток
    /// экземпляра вдоль этой оси/стороны (сравнение с <paramref name="origin"/>/<paramref name="maxCell"/>) — ровно
    /// там, где эта сторона формы действительно является внешней гранью всей постройки, а не внутренним стыком.
    /// Для куба (маска 0b111111, обе стороны каждой оси) это ничего не меняет: соседняя клетка ТОГО ЖЕ экземпляра
    /// по-прежнему взаимно гасит грань как раньше — просто через отсутствие встречного бита с обеих сторон
    /// одновременно, а не через явное закрытие с обеих; итоговый видимый меш идентичен.
    /// </summary>
    private static byte BoundaryFaceMask(byte instanceMask, Vector3I cell, Vector3I origin, Vector3I maxCell)
    {
        byte mask = 0;
        if ((instanceMask & (1 << 0)) != 0 && cell.X == origin.X) mask |= 1 << 0; // X-
        if ((instanceMask & (1 << 1)) != 0 && cell.X == maxCell.X) mask |= 1 << 1; // X+
        if ((instanceMask & (1 << 2)) != 0 && cell.Y == origin.Y) mask |= 1 << 2; // Y-
        if ((instanceMask & (1 << 3)) != 0 && cell.Y == maxCell.Y) mask |= 1 << 3; // Y+
        if ((instanceMask & (1 << 4)) != 0 && cell.Z == origin.Z) mask |= 1 << 4; // Z-
        if ((instanceMask & (1 << 5)) != 0 && cell.Z == maxCell.Z) mask |= 1 << 5; // Z+
        return mask;
    }

    public bool Remove(BlockInstance instance)
    {
        if (!_instances.Remove(instance.InstanceId)) return false;
        _wires.RemoveAll(w => w.FromInstance == instance.InstanceId || w.ToInstance == instance.InstanceId);

        foreach (var cell in CellsOf(instance))
        {
            _owner.Remove(cell);
            Grid.TryRemove(cell);
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Красит ВЕСЬ экземпляр целиком в один цвет — сбрасывает и представительный <see cref="BlockInstance.Color"/>
    /// (влияет на все FullCoverage-грани его клеток в <see cref="VoxelGrid"/>), и любую точечную покраску отдельных
    /// наклонных/треугольных граней (<see cref="BlockInstance.RegionColors"/>, см. <see cref="PaintRegion"/>) —
    /// иначе "перекрасить целиком" не выглядело бы таковым, если старые точечные правки продолжали бы проступать.</summary>
    public bool Paint(BlockInstance instance, Color color)
    {
        uint packed = CellColor.Pack(color);
        bool hadRegionColors = instance.RegionColors is { Count: > 0 };
        if (instance.Color == packed && !hadRegionColors) return false;

        instance.Color = packed;
        instance.RegionColors = null;
        foreach (var cell in CellsOf(instance)) Grid.TryPaint(cell, packed);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Красит РОВНО одну наклонную/треугольную грань не-кубической формы (см. <see cref="ShapeMeshBuilder.TryFindPaintRegion"/>
    /// про то, как определяется <paramref name="regionIndex"/> по попаданию луча), не весь экземпляр — см.
    /// <see cref="BlockInstance.RegionColors"/>. Не трогает <see cref="VoxelGrid"/> (эти грани рисует не
    /// <see cref="ChunkMesher"/>, а <see cref="ShapeMeshBuilder"/> напрямую для каждого экземпляра, см.
    /// <c>Editor.ShapeInstanceView</c>) — только событие <see cref="Changed"/>, на которое та и подписана.
    /// </summary>
    public bool PaintRegion(BlockInstance instance, int regionIndex, Color color)
    {
        uint packed = CellColor.Pack(color);
        instance.RegionColors ??= new Dictionary<int, uint>();
        if (instance.RegionColors.TryGetValue(regionIndex, out uint existing) && existing == packed) return false;

        instance.RegionColors[regionIndex] = packed;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Принудительно поднимает <see cref="Changed"/> без изменения состояния — нужно
    /// <c>UndoHistory</c> после того, как оно восстановило <see cref="BlockInstance.RegionColors"/> напрямую
    /// (в отличие от поклеточной покраски граней куба, это состояние самого <see cref="Construction"/>, а не
    /// <see cref="VoxelGrid"/>, поэтому не поднимает <see cref="VoxelGrid.CellChanged"/> само по себе).</summary>
    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>
    /// Задаёт новый размер блока. Origin никогда не двигается — блок всегда растёт/сжимается от своего origin
    /// в положительную сторону каждой оси (растянуть «назад», в отрицательную сторону, нельзя — см. Resize в
    /// редакторе). <paramref name="newSize"/> зажимается в [max(1, BuildingBlock.MinSize) .. BuildingBlock.MaxSize]
    /// покомпонентно. Требует у блока компонент BuildingBlock. false: блок не резинится, новый размер совпадает
    /// со старым, либо клетки, которые нужно занять, заняты чем-то другим или вне области построек (в этом случае
    /// состояние не меняется вообще — операция атомарна).
    /// </summary>
    public bool TrySetSize(BlockInstance instance, BlockDefinition definition, Vector3I newSize)
    {
        var building = definition.GetComponent<BuildingBlockComponent>();
        if (building == null) return false;

        // newSize - размер ЗАНЯТОГО бокса (оси мира); MinSize/MaxSize заданы в ЛОКАЛЬНЫХ осях блока, поэтому у повёрнутого блока
        // зажимаем локальный размер и переводим обратно (без поворота - то же, что и раньше).
        var local = BlockFootprint.UnrotatedSize(newSize, instance.RotationSteps);
        local = new Vector3I(
            Math.Clamp(local.X, Math.Max(1, building.MinSize.X), Math.Max(1, building.MaxSize.X)),
            Math.Clamp(local.Y, Math.Max(1, building.MinSize.Y), Math.Max(1, building.MaxSize.Y)),
            Math.Clamp(local.Z, Math.Max(1, building.MinSize.Z), Math.Max(1, building.MaxSize.Z)));
        newSize = BlockFootprint.RotatedSize(local, instance.RotationSteps);
        if (newSize == instance.Size) return false;

        var oldCells = new HashSet<Vector3I>(CellsOf(instance));
        var resized = new BlockInstance { InstanceId = instance.InstanceId, BlockSlug = instance.BlockSlug, Origin = instance.Origin, Size = newSize };
        var newCells = new HashSet<Vector3I>(CellsOf(resized));

        foreach (var cell in newCells)
        {
            if (oldCells.Contains(cell)) continue;
            if (!BuildSpace.InBounds(cell) || Grid.IsSolid(cell)) return false;
        }

        foreach (var cell in oldCells)
        {
            if (newCells.Contains(cell)) continue;
            _owner.Remove(cell);
            Grid.TryRemove(cell);
        }

        byte instanceMask = FullCoverageMask(definition, instance.RotationSteps, instance.Mirror);
        instance.Size = newSize;
        // Растущему экземпляру нужно пересчитать маску не только у НОВЫХ клеток, но и у уже стоявших вдоль границы -
        // клетка, которая раньше была крайней (несла бит FullCoverage), могла стать внутренней после роста в ту же
        // сторону (см. BoundaryFaceMask), и наоборот при сжатии. Проще всего пройтись по всем клеткам финального
        // размера разом.
        foreach (var cell in newCells)
        {
            _owner[cell] = instance.InstanceId;
            Grid.TrySet(cell, definition.RuntimeId, instance.Color, BoundaryFaceMask(instanceMask, cell, instance.Origin, instance.MaxCell));
        }

        Changed?.Invoke();
        return true;
    }


    // ------------------------------------------------------------------ провода между нодами и параметры блоков

    private readonly List<NodeWire> _wires = new();

    /// <summary>Провода между нодами блоков постройки (см. <see cref="NodeWire"/>) — в порядке создания.</summary>
    public IReadOnlyList<NodeWire> Wires => _wires;

    /// <summary>Ищет ноду <paramref name="nodeId"/> у экземпляра; null — нет экземпляра, его блок не функциональный или такой ноды нет.</summary>
    public LogicNode? FindNode(int instanceId, string nodeId, BlockCatalog? catalog = null)
    {
        if (!_instances.TryGetValue(instanceId, out var instance)) return null;
        if (!(catalog ?? BlockCatalog.Instance).TryGetBySlug(instance.BlockSlug, out var definition)) return null;
        return definition.GetComponent<FunctionalBlockComponent>()?.Nodes.FirstOrDefault(n => n.Id == nodeId);
    }

    /// <summary>
    /// Можно ли соединить две ноды и каким проводом: null в <paramref name="error"/> — можно, <paramref name="wire"/> уже приведён к виду
    /// «от выхода ко входу» (порядок аргументов не важен — игрок тянет провод с любого конца). Правила: нужны ровно один выход и один вход;
    /// ноды РАЗНЫХ блоков (провод блока сам на себя не нужен и создавал бы петли); типы совместимы — Electricity только с Electricity,
    /// Boolean и Number между собой свободно (булево — это число 0/1, см. <see cref="Runtime.NodeValue"/>). Такой провод уже есть — тоже ошибка.
    /// </summary>
    public bool CheckWire(int instanceA, string nodeA, int instanceB, string nodeB, out NodeWire wire, out string error, BlockCatalog? catalog = null)
    {
        wire = default;
        var a = FindNode(instanceA, nodeA, catalog);
        var b = FindNode(instanceB, nodeB, catalog);
        if (a == null || b == null) { error = "no such node"; return false; }
        if (instanceA == instanceB) { error = "a block cannot be wired to itself"; return false; }
        if (a.Direction == b.Direction) { error = a.Direction == PortDirection.Out ? "connect an output to an input, not two outputs" : "connect an input to an output, not two inputs"; return false; }
        if ((a.Type == NodeType.Electricity) != (b.Type == NodeType.Electricity)) { error = "electricity connects only to electricity"; return false; }

        wire = a.Direction == PortDirection.Out ? new NodeWire(instanceA, nodeA, instanceB, nodeB) : new NodeWire(instanceB, nodeB, instanceA, nodeA);
        if (_wires.Contains(wire)) { error = "already connected"; return false; }

        error = "";
        return true;
    }

    /// <summary>
    /// Соединяет две ноды (см. <see cref="CheckWire"/>). Вход Boolean/Number принимает ОДИН источник: новый провод ЗАМЕНЯЕТ прежний (иначе пришлось бы
    /// сначала отдельно его убирать); вход Electricity принимает сколько угодно источников (несколько батарей на один потребитель), выход
    /// раздаётся на сколько угодно входов. false — соединять нельзя (причина — в <paramref name="error"/>), постройка не меняется.
    /// </summary>
    public bool TryConnect(int instanceA, string nodeA, int instanceB, string nodeB, out string error, BlockCatalog? catalog = null)
    {
        if (!CheckWire(instanceA, nodeA, instanceB, nodeB, out var wire, out error, catalog)) return false;

        var target = FindNode(wire.ToInstance, wire.ToNode, catalog)!;
        if (target.Type != NodeType.Electricity) _wires.RemoveAll(w => w.ToInstance == wire.ToInstance && w.ToNode == wire.ToNode);
        _wires.Add(wire);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Провод между двумя нодами в ЛЮБОМ направлении (порядок аргументов не важен); null — они не соединены напрямую.</summary>
    public NodeWire? FindWireBetween(int instanceA, string nodeA, int instanceB, string nodeB)
    {
        foreach (var wire in _wires)
        {
            bool forward = wire.FromInstance == instanceA && wire.FromNode == nodeA && wire.ToInstance == instanceB && wire.ToNode == nodeB;
            bool backward = wire.FromInstance == instanceB && wire.FromNode == nodeB && wire.ToInstance == instanceA && wire.ToNode == nodeA;
            if (forward || backward) return wire;
        }

        return null;
    }

    /// <summary>Убирает конкретный провод; false — такого нет.</summary>
    public bool Disconnect(NodeWire wire)
    {
        if (!_wires.Remove(wire)) return false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Провода, подключённые к ноде (как выходу или как входу).</summary>
    public IEnumerable<NodeWire> WiresOf(int instanceId, string nodeId) =>
        _wires.Where(w => (w.FromInstance == instanceId && w.FromNode == nodeId) || (w.ToInstance == instanceId && w.ToNode == nodeId));

    /// <summary>Убирает ВСЕ провода ноды; возвращает, сколько убрано.</summary>
    public int DisconnectNode(int instanceId, string nodeId)
    {
        int removed = _wires.RemoveAll(w => (w.FromInstance == instanceId && w.FromNode == nodeId) || (w.ToInstance == instanceId && w.ToNode == nodeId));
        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    /// <summary>Добавляет провод напрямую, без проверок типа/дубля — только для загрузки из файла (<see cref="ConstructionIO"/>), где уже проверен узел
    /// существования; провод к несуществующему блоку отбрасывается.</summary>
    internal bool AddWireUnchecked(NodeWire wire)
    {
        if (!_instances.ContainsKey(wire.FromInstance) || !_instances.ContainsKey(wire.ToInstance) || _wires.Contains(wire)) return false;
        _wires.Add(wire);
        return true;
    }

    /// <summary>Схема параметров блока экземпляра (<see cref="ParametersComponent"/>); null — блок ничего не настраивается.</summary>
    public static ParametersComponent? ParametersOf(BlockDefinition definition)
    {
        var component = definition.GetComponent<ParametersComponent>();
        return component is { Parameters.Count: > 0 } ? component : null;
    }

    /// <summary>
    /// Задаёт параметр экземпляра: текст приводится к допустимому значению (<see cref="ParameterDefinition.TryNormalize"/> — границы, формат),
    /// значение, равное умолчанию, не хранится (экземпляр остаётся «по умолчанию»). false — у блока нет такого параметра, текст не разбирается, или
    /// значение не изменилось.
    /// </summary>
    public bool TrySetParameter(BlockInstance instance, string parameterId, string text, BlockCatalog? catalog = null)
    {
        if (!(catalog ?? BlockCatalog.Instance).TryGetBySlug(instance.BlockSlug, out var definition)) return false;
        var schema = ParametersOf(definition);
        if (schema == null || !schema.TryGet(parameterId, out var parameter) || !parameter.TryNormalize(text, out string normalized)) return false;

        string current = instance.Parameters != null && instance.Parameters.TryGetValue(parameterId, out var stored) ? stored : parameter.Default;
        if (current == normalized) return false;

        if (normalized == parameter.Default)
        {
            instance.Parameters?.Remove(parameterId);
            if (instance.Parameters is { Count: 0 }) instance.Parameters = null;
        }
        else
        {
            instance.Parameters ??= new Dictionary<string, string>();
            instance.Parameters[parameterId] = normalized;
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Экземпляр по идентификатору; null — нет такого.</summary>
    public BlockInstance? GetInstance(int instanceId) => _instances.TryGetValue(instanceId, out var instance) ? instance : null;
    public void Clear()
    {
        foreach (var instance in new List<BlockInstance>(_instances.Values)) Remove(instance);
    }

    public static IEnumerable<Vector3I> CellsOf(BlockInstance instance)
    {
        var min = instance.Origin;
        var max = instance.MaxCell;
        for (int z = min.Z; z <= max.Z; z++)
        for (int y = min.Y; y <= max.Y; y++)
        for (int x = min.X; x <= max.X; x++)
        {
            yield return new Vector3I(x, y, z);
        }
    }

    /// <summary>
    /// Минимальный/максимальный угол (включительно) всех клеток постройки разом — используется и превью-рендером
    /// (<c>Editor.ConstructionPreviewRenderer</c>), и спавном физического тела (<c>World.VehicleSpawner</c>), чтобы
    /// центрировать камеру/тело по фактическим границам постройки, а не по условному началу координат.
    /// <see cref="Vector3I.Zero"/>/<see cref="Vector3I.Zero"/>, если постройка пуста.
    /// </summary>
    public (Vector3I Min, Vector3I Max) ComputeBounds()
    {
        if (_instances.Count == 0) return (Vector3I.Zero, Vector3I.Zero);

        var min = new Vector3I(int.MaxValue, int.MaxValue, int.MaxValue);
        var max = new Vector3I(int.MinValue, int.MinValue, int.MinValue);
        foreach (var instance in _instances.Values)
        {
            min = new Vector3I(Math.Min(min.X, instance.Origin.X), Math.Min(min.Y, instance.Origin.Y), Math.Min(min.Z, instance.Origin.Z));
            max = new Vector3I(Math.Max(max.X, instance.MaxCell.X), Math.Max(max.Y, instance.MaxCell.Y), Math.Max(max.Z, instance.MaxCell.Z));
        }

        return (min, max);
    }
}
