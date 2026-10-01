using System;
using System.Collections.Generic;
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

        newSize = new Vector3I(
            Math.Clamp(newSize.X, Math.Max(1, building.MinSize.X), Math.Max(1, building.MaxSize.X)),
            Math.Clamp(newSize.Y, Math.Max(1, building.MinSize.Y), Math.Max(1, building.MaxSize.Y)),
            Math.Clamp(newSize.Z, Math.Max(1, building.MinSize.Z), Math.Max(1, building.MaxSize.Z)));
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
