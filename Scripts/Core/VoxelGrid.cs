using System;
using System.Collections.Generic;
using Godot;

namespace SandboxPolyGame.Core;

/// <summary>
/// Блок данных 16x16x16 клеток: id блока (0 = пусто), цвет КАЖДОЙ из 6 граней клетки по отдельности (не один цвет
/// на клетку — см. <see cref="FaceColors"/>) и маска <see cref="FaceMask"/>, показывающая, какие из 6 осевых сторон
/// клетки целиком закрыты геометрией (и поэтому могут отсечься/склеиться с соседями в <see cref="ChunkMesher"/>).
/// </summary>
public sealed class VoxelChunk
{
    public readonly ushort[] Ids = new ushort[BuildSpace.ChunkVolume];

    /// <summary>6 цветов на клетку: индекс = <c>cellIndex*6 + axis*2 + (positive?1:0)</c> (axis: 0=X,1=Y,2=Z) —
    /// та же конвенция бит, что у <see cref="FaceMask"/> и <see cref="ChunkMesher"/>. При установке блока
    /// (<see cref="VoxelGrid.TrySet"/>) все 6 инициализируются одним цветом; точечно меняются
    /// <see cref="VoxelGrid.TryPaintFace"/> (одна грань) и <see cref="VoxelGrid.TryPaint"/> (все 6 разом, старое
    /// поведение "покрасить всю клетку").</summary>
    public readonly uint[] FaceColors = new uint[BuildSpace.ChunkVolume * 6];

    /// <summary>Один байт на клетку: бит <c>axis*2 + (positive?1:0)</c> = эта осевая сторона клетки ПОЛНОСТЬЮ
    /// закрыта геометрией (у куба — всегда <c>0b111111</c>; у Wedge/InvertedPyramid — только те стороны, что их
    /// прямоугольные грани реально покрывают целиком, см. <see cref="ShapeMeshBuilder.FullCoverageMask"/>; у
    /// Pyramid — 0, там таких граней нет вообще). <see cref="ChunkMesher"/> отсекает/склеивает грань клетки по этой
    /// маске вместо старой проверки "это куб?" — не-кубическая клетка добавляет в общий проход ровно те стороны,
    /// которые её форма покрывает целиком; остальное продолжает рисовать <see cref="ShapeMeshBuilder"/> отдельным
    /// мешем на экземпляр (треугольные борта, рампа, весь Pyramid).</summary>
    public readonly byte[] FaceMask = new byte[BuildSpace.ChunkVolume];

    public int SolidCount;

    public static int Index(int lx, int ly, int lz) => lx | (ly << BuildSpace.ChunkShift) | (lz << (BuildSpace.ChunkShift * 2));

    public static int FaceSlot(int axis, bool positive) => axis * 2 + (positive ? 1 : 0);
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

    /// <summary>"Представительный" цвет клетки (грань X-) — для UI/тестов, которым нужен один цвет на клетку, а не
    /// per-face (см. <see cref="GetFaceColor"/> для точного цвета конкретной грани). Совпадает с реальным цветом,
    /// пока клетку не перекрашивали по граням точечно (<see cref="TryPaintFace"/>) — обычный случай сразу после
    /// установки или после поклеточного <see cref="TryPaint"/>, у которых все 6 граней равны.</summary>
    public uint GetColor(Vector3I cell)
    {
        if (!_chunks.TryGetValue(BuildSpace.ChunkOf(cell), out var chunk)) return 0;
        return chunk.FaceColors[LocalIndex(cell) * 6];
    }

    public uint GetFaceColor(Vector3I cell, int axis, bool positive)
    {
        if (!_chunks.TryGetValue(BuildSpace.ChunkOf(cell), out var chunk)) return 0;
        return chunk.FaceColors[LocalIndex(cell) * 6 + VoxelChunk.FaceSlot(axis, positive)];
    }

    public byte GetFaceMask(Vector3I cell)
    {
        if (!_chunks.TryGetValue(BuildSpace.ChunkOf(cell), out var chunk)) return 0;
        return chunk.FaceMask[LocalIndex(cell)];
    }

    public bool IsSolid(Vector3I cell) => GetId(cell) != 0;

    /// <summary>Ставит блок (или заменяет существующий), закрашивая все 6 граней клетки одним цветом. false — если
    /// вне области или ничего не изменилось. <paramref name="faceMask"/> — какие из 6 осевых сторон клетки целиком
    /// закрыты геометрией (см. <see cref="VoxelChunk.FaceMask"/>); по умолчанию — куб, закрыт целиком со всех
    /// сторон. Не-кубические формы передают маску, посчитанную <see cref="ShapeMeshBuilder.FullCoverageMask"/>
    /// (см. <see cref="Construction"/>).</summary>
    public bool TrySet(Vector3I cell, ushort id, uint color, byte faceMask = 0b111111)
    {
        if (id == 0 || !BuildSpace.InBounds(cell)) return false;

        var coord = BuildSpace.ChunkOf(cell);
        if (!_chunks.TryGetValue(coord, out var chunk))
        {
            chunk = new VoxelChunk();
            _chunks[coord] = chunk;
        }

        int i = LocalIndex(cell);
        bool wasEmpty = chunk.Ids[i] == 0;
        if (!wasEmpty && chunk.Ids[i] == id && chunk.FaceMask[i] == faceMask && chunk.FaceColors[i * 6] == color) return false;

        if (wasEmpty)
        {
            chunk.SolidCount++;
            BlockCount++;
        }

        chunk.Ids[i] = id;
        chunk.FaceMask[i] = faceMask;
        for (int f = 0; f < 6; f++) chunk.FaceColors[i * 6 + f] = color;
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
        chunk.FaceMask[i] = 0;
        for (int f = 0; f < 6; f++) chunk.FaceColors[i * 6 + f] = 0;
        chunk.SolidCount--;
        BlockCount--;
        if (chunk.SolidCount == 0) _chunks.Remove(coord);

        CellChanged?.Invoke(cell);
        return true;
    }

    /// <summary>Перекрашивает ВСЕ 6 граней существующего блока разом (старое, поклеточное поведение — используется
    /// инструментом Paint для клеток без владеющего экземпляра, и <see cref="Construction.Paint"/> для целого
    /// многоклеточного блока). Не создаёт блок в пустой клетке. Точечная покраска одной грани — <see cref="TryPaintFace"/>.</summary>
    public bool TryPaint(Vector3I cell, uint color)
    {
        if (!_chunks.TryGetValue(BuildSpace.ChunkOf(cell), out var chunk)) return false;

        int i = LocalIndex(cell);
        if (chunk.Ids[i] == 0) return false;

        bool changed = false;
        for (int f = 0; f < 6; f++)
        {
            int idx = i * 6 + f;
            if (chunk.FaceColors[idx] == color) continue;
            chunk.FaceColors[idx] = color;
            changed = true;
        }

        if (!changed) return false;
        CellChanged?.Invoke(cell);
        return true;
    }

    /// <summary>Перекрашивает ровно ОДНУ грань существующего блока (инструмент Paint, "по грани" — см.
    /// <c>Editor.BuildEditor</c>). <paramref name="axis"/>/<paramref name="positive"/> — та же конвенция, что у
    /// <see cref="VoxelChunk.FaceMask"/> (обычно берётся прямо из <see cref="RayHit.Normal"/>). Не создаёт блок в
    /// пустой клетке; работает для любой клетки независимо от того, покрыта ли эта сторона целиком
    /// (<see cref="VoxelChunk.FaceMask"/>) — красит per-face хранилище всегда, даже если сейчас эту грань рисует не
    /// <see cref="ChunkMesher"/>, а собственный меш формы (на будущее/для UV-независимости данных).</summary>
    public bool TryPaintFace(Vector3I cell, int axis, bool positive, uint color)
    {
        if (!_chunks.TryGetValue(BuildSpace.ChunkOf(cell), out var chunk)) return false;

        int i = LocalIndex(cell);
        if (chunk.Ids[i] == 0) return false;

        int idx = i * 6 + VoxelChunk.FaceSlot(axis, positive);
        if (chunk.FaceColors[idx] == color) return false;

        chunk.FaceColors[idx] = color;
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
