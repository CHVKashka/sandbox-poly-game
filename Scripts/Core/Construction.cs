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
    public BlockInstance? Place(Vector3I cell, BlockDefinition definition, Color color, Vector3I rotationSteps = default) =>
        PlaceBlock(cell, Vector3I.One, definition, color, rotationSteps);

    /// <summary>
    /// Ставит блок сразу заданного размера (используется загрузкой построек — <see cref="TrySetSize"/> мутирует
    /// уже существующий экземпляр, а не создаёт новый). null, если хотя бы одна из клеток области занята или
    /// вне области построек.
    /// </summary>
    public BlockInstance? PlaceBlock(Vector3I origin, Vector3I size, BlockDefinition definition, Color color, Vector3I rotationSteps = default)
    {
        var instance = new BlockInstance
        {
            InstanceId = _nextId,
            BlockSlug = definition.Slug,
            Origin = origin,
            Size = size,
            Color = CellColor.Pack(color),
            RotationSteps = rotationSteps,
        };

        var cells = new List<Vector3I>(CellsOf(instance));
        foreach (var cell in cells)
        {
            if (!BuildSpace.InBounds(cell) || Grid.IsSolid(cell)) return null;
        }

        _nextId++;
        _instances[instance.InstanceId] = instance;
        foreach (var cell in cells)
        {
            _owner[cell] = instance.InstanceId;
            Grid.TrySet(cell, definition.RuntimeId, instance.Color);
        }

        Changed?.Invoke();
        return instance;
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

    public bool Paint(BlockInstance instance, Color color)
    {
        uint packed = CellColor.Pack(color);
        if (instance.Color == packed) return false;

        instance.Color = packed;
        foreach (var cell in CellsOf(instance)) Grid.TryPaint(cell, packed);
        Changed?.Invoke();
        return true;
    }

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

        instance.Size = newSize;
        foreach (var cell in newCells)
        {
            if (oldCells.Contains(cell)) continue;
            _owner[cell] = instance.InstanceId;
            Grid.TrySet(cell, definition.RuntimeId, instance.Color);
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
}
