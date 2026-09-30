using Godot;

namespace SandboxPolyGame.Core;

/// <summary>
/// Правило соседства при установке блока — вынесено из <c>Editor.BuildEditor</c> (где живёт как есть, без изменений
/// поведения), чтобы им же мог пользоваться сервер сетевой сессии редактирования (<see cref="NetHub"/>): и клиент
/// (призрак/гейт перед ЛКМ), и сервер (валидация входящего запроса на постройку блока) должны применять РОВНО одно
/// и то же правило — иначе они разошлись бы во мнении, что можно ставить, а что нет.
/// </summary>
public static class PlacementRules
{
    private static readonly Vector3I[] NeighborOffsets =
    {
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    };

    /// <summary>Все ли клетки прямоугольной области <paramref name="size"/> клеток от <paramref name="origin"/>
    /// свободны, внутри области построек, и касаются уже стоящего блока хотя бы одной гранью (кроме случая, когда
    /// решётка совсем пуста — тогда первый блок ставится куда угодно в пределах области).</summary>
    public static bool CanPlaceFootprint(VoxelGrid grid, Vector3I origin, Vector3I size)
    {
        var max = origin + size - Vector3I.One;
        for (int z = origin.Z; z <= max.Z; z++)
        for (int y = origin.Y; y <= max.Y; y++)
        for (int x = origin.X; x <= max.X; x++)
        {
            var cell = new Vector3I(x, y, z);
            if (!BuildSpace.InBounds(cell) || grid.IsSolid(cell)) return false;
        }

        return grid.BlockCount == 0 || TouchesExistingBlock(grid, origin, max);
    }

    private static bool TouchesExistingBlock(VoxelGrid grid, Vector3I origin, Vector3I max)
    {
        for (int z = origin.Z; z <= max.Z; z++)
        for (int y = origin.Y; y <= max.Y; y++)
        for (int x = origin.X; x <= max.X; x++)
        {
            var cell = new Vector3I(x, y, z);
            foreach (var offset in NeighborOffsets)
            {
                if (grid.IsSolid(cell + offset)) return true;
            }
        }

        return false;
    }
}
