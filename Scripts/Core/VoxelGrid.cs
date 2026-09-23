using System;
using System.Collections.Generic;
using Godot;

namespace SandboxPolyGame.Core;

/// <summary>Блок данных 16x16x16 клеток: id блока (0 = пусто) и упакованный цвет каждой клетки.</summary>
public sealed class VoxelChunk
{
    public readonly ushort[] Ids = new ushort[BuildSpace.ChunkVolume];
    public readonly uint[] Colors = new uint[BuildSpace.ChunkVolume];
    public int SolidCount;

    public static int Index(int lx, int ly, int lz) => lx | (ly << BuildSpace.ChunkShift) | (lz << (BuildSpace.ChunkShift * 2));
}

/// <summary>Разреженное хранилище построек: словарь чанков. Не зависит от сцены и рендера.</summary>
public sealed class VoxelGrid
{
    private readonly Dictionary<Vector3I, VoxelChunk> _chunks = new();

    public int BlockCount { get; private set; }

    /// <summary>Любое изменение клетки (установка, удаление, перекраска).</summary>
    public event Action<Vector3I>? CellChanged;

    public IEnumerable<Vector3I> ChunkCoords => _chunks.Keys;

    public VoxelChunk? GetChunk(Vector3I coord) => _chunks.TryGetValue(coord, out var c) ? c : null;

    public ushort GetId(Vector3I cell)
    {
        if (!_chunks.TryGetValue(BuildSpace.ChunkOf(cell), out var chunk)) return 0;
        return chunk.Ids[LocalIndex(cell)];
    }

    public uint GetColor(Vector3I cell)
    {
        if (!_chunks.TryGetValue(BuildSpace.ChunkOf(cell), out var chunk)) return 0;
        return chunk.Colors[LocalIndex(cell)];
    }

    public bool IsSolid(Vector3I cell) => GetId(cell) != 0;

    /// <summary>Ставит блок (или заменяет существующий). false — если вне области или ничего не изменилось.</summary>
    public bool TrySet(Vector3I cell, ushort id, uint color)
    {
        if (id == 0 || !BuildSpace.InBounds(cell)) return false;

        var coord = BuildSpace.ChunkOf(cell);
        if (!_chunks.TryGetValue(coord, out var chunk))
        {
            chunk = new VoxelChunk();
            _chunks[coord] = chunk;
        }

        int i = LocalIndex(cell);
        if (chunk.Ids[i] == id && chunk.Colors[i] == color) return false;

        if (chunk.Ids[i] == 0)
        {
            chunk.SolidCount++;
            BlockCount++;
        }

        chunk.Ids[i] = id;
        chunk.Colors[i] = color;
        CellChanged?.Invoke(cell);
        return true;
    }

    public bool TryRemove(Vector3I cell)
    {
        var coord = BuildSpace.ChunkOf(cell);
        if (!_chunks.TryGetValue(coord, out var chunk)) return false;

        int i = LocalIndex(cell);
        if (chunk.Ids[i] == 0) return false;

        chunk.Ids[i] = 0;
        chunk.Colors[i] = 0;
        chunk.SolidCount--;
        BlockCount--;
        if (chunk.SolidCount == 0) _chunks.Remove(coord);

        CellChanged?.Invoke(cell);
        return true;
    }

    /// <summary>Перекрашивает существующий блок. Не создаёт блок в пустой клетке.</summary>
    public bool TryPaint(Vector3I cell, uint color)
    {
        if (!_chunks.TryGetValue(BuildSpace.ChunkOf(cell), out var chunk)) return false;

        int i = LocalIndex(cell);
        if (chunk.Ids[i] == 0 || chunk.Colors[i] == color) return false;

        chunk.Colors[i] = color;
        CellChanged?.Invoke(cell);
        return true;
    }

    public void Clear()
    {
        var cells = new List<Vector3I>();
        foreach (var (coord, chunk) in _chunks)
        {
            for (int i = 0; i < BuildSpace.ChunkVolume; i++)
            {
                if (chunk.Ids[i] == 0) continue;
                cells.Add(new Vector3I(
                    (coord.X << BuildSpace.ChunkShift) | (i & BuildSpace.ChunkMask),
                    (coord.Y << BuildSpace.ChunkShift) | ((i >> BuildSpace.ChunkShift) & BuildSpace.ChunkMask),
                    (coord.Z << BuildSpace.ChunkShift) | (i >> (BuildSpace.ChunkShift * 2))));
            }
        }

        foreach (var cell in cells) TryRemove(cell);
    }

    /// <summary>Чанк и 26 соседей: индекс = (dx+1) + 3*((dy+1) + 3*(dz+1)); отсутствующие чанки = null.</summary>
    public VoxelChunk?[] GetNeighborhood(Vector3I coord)
    {
        var result = new VoxelChunk?[27];
        for (int dz = -1; dz <= 1; dz++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            result[(dx + 1) + 3 * ((dy + 1) + 3 * (dz + 1))] = GetChunk(coord + new Vector3I(dx, dy, dz));
        }

        return result;
    }

    /// <summary>
    /// Чанки, меши которых зависят от клетки: собственный и соседи по тем сторонам, на границе которых клетка лежит
    /// (соседние грани для отсечения + рёбра каркаса, которыми владеет соседний чанк).
    /// </summary>
    public static void GetAffectedChunks(Vector3I cell, List<Vector3I> result)
    {
        result.Clear();
        var chunk = BuildSpace.ChunkOf(cell);
        var ox = BoundaryOffsets(cell.X & BuildSpace.ChunkMask);
        var oy = BoundaryOffsets(cell.Y & BuildSpace.ChunkMask);
        var oz = BoundaryOffsets(cell.Z & BuildSpace.ChunkMask);
        foreach (int dx in ox)
        foreach (int dy in oy)
        foreach (int dz in oz)
        {
            result.Add(chunk + new Vector3I(dx, dy, dz));
        }
    }

    private static int[] BoundaryOffsets(int local) =>
        local == 0 ? new[] { 0, -1 } : local == BuildSpace.ChunkMask ? new[] { 0, 1 } : new[] { 0 };

    private static int LocalIndex(Vector3I cell) => VoxelChunk.Index(
        cell.X & BuildSpace.ChunkMask, cell.Y & BuildSpace.ChunkMask, cell.Z & BuildSpace.ChunkMask);
}
