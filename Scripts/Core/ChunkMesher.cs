using System;
using System.Collections.Generic;
using Godot;

namespace SwV2.Core;

/// <summary>Результат меширования одного чанка. Координаты вершин — в метрах, относительно угла чанка.</summary>
public sealed class ChunkMeshData
{
    public readonly List<Vector3> Vertices = new();
    public readonly List<Vector3> Normals = new();
    public readonly List<Color> Colors = new();
    public readonly List<int> Indices = new();

    /// <summary>Пары вершин: каждый отрезок каркаса = 2 вершины.</summary>
    public readonly List<Vector3> LineVertices = new();

    /// <summary>Сколько одиночных видимых граней было до склейки (для статистики).</summary>
    public int VisibleFaces;

    public int Quads => Indices.Count / 6;
    public int LineSegments => LineVertices.Count / 2;
    public bool IsEmpty => Indices.Count == 0 && LineVertices.Count == 0;

    public ArrayMesh? CreateSolidMesh()
    {
        if (Indices.Count == 0) return null;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = Vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = Normals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = Colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = Indices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    public ArrayMesh? CreateWireMesh()
    {
        if (LineVertices.Count == 0) return null;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = LineVertices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return mesh;
    }
}

/// <summary>
/// Строит меш чанка.
///
/// Поверхность: рисуются только грани, у которых по нормали нет соседнего блока (внутренние грани между блоками
/// не создаются), затем смежные грани одного цвета в одной плоскости склеиваются в прямоугольники (greedy meshing).
///
/// Каркас (wireframe): отрезок ребра сетки показывается, только если он является «настоящим» ребром формы.
/// Ребро окружено четырьмя клетками; оно НЕ показывается, когда заполнено 0, 4 или две соседние клетки
/// (тогда это шов внутри плоской поверхности или пустота). Каркас не зависит ни от цвета, ни от типа блоков,
/// ни от границ чанков — поэтому он «единый» для всей постройки.
/// </summary>
public static class ChunkMesher
{
    private const int S = BuildSpace.ChunkSize;
    private const int P = S + 2; // с рамкой в 1 клетку с каждой стороны

    /// <summary>Смещение линий каркаса наружу (метры), чтобы они не мерцали (z-fighting) на самих гранях.</summary>
    public const float WireOffsetMeters = 0.004f;

    private static readonly int[] PaddedStride = { 1, P, P * P };
    private static readonly int[] ChunkStride = { 1, S, S * S };
    private const int PaddedBase = 1 + P + P * P;

    public static ChunkMeshData Build(VoxelGrid grid, Vector3I chunk)
    {
        var data = new ChunkMeshData();
        var solid = new bool[P * P * P];
        var colors = new uint[S * S * S];

        if (!Fill(grid, chunk, solid, colors)) return data;

        BuildFaces(data, solid, colors);
        BuildEdges(data, solid);
        return data;
    }

    private static bool Fill(VoxelGrid grid, Vector3I chunk, bool[] solid, uint[] colors)
    {
        var neighborhood = grid.GetNeighborhood(chunk);
        bool any = false;

        for (int z = -1; z <= S; z++)
        {
            int oz = z < 0 ? 0 : z >= S ? 2 : 1;
            for (int y = -1; y <= S; y++)
            {
                int oy = y < 0 ? 0 : y >= S ? 2 : 1;
                for (int x = -1; x <= S; x++)
                {
                    int ox = x < 0 ? 0 : x >= S ? 2 : 1;
                    var source = neighborhood[ox + 3 * (oy + 3 * oz)];
                    if (source == null) continue;

                    int li = (x & BuildSpace.ChunkMask)
                             | ((y & BuildSpace.ChunkMask) << BuildSpace.ChunkShift)
                             | ((z & BuildSpace.ChunkMask) << (BuildSpace.ChunkShift * 2));
                    if (source.Ids[li] == 0) continue;

                    solid[PaddedBase + x + y * P + z * P * P] = true;
                    any = true;
                    if (ox == 1 && oy == 1 && oz == 1) colors[li] = source.Colors[li];
                }
            }
        }

        return any;
    }

    // ---------------------------------------------------------------- поверхность

    private static void BuildFaces(ChunkMeshData data, bool[] solid, uint[] colors)
    {
        var mask = new uint[S * S];

        for (int axis = 0; axis < 3; axis++)
        {
            int u = (axis + 1) % 3;
            int v = (axis + 2) % 3;

            for (int side = 0; side < 2; side++)
            {
                bool positive = side == 1;
                int step = positive ? 1 : -1;

                for (int slice = 0; slice < S; slice++)
                {
                    Array.Clear(mask);
                    bool anyFace = false;

                    for (int j = 0; j < S; j++)
                    for (int i = 0; i < S; i++)
                    {
                        int idx = PaddedBase + slice * PaddedStride[axis] + i * PaddedStride[u] + j * PaddedStride[v];
                        if (!solid[idx] || solid[idx + step * PaddedStride[axis]]) continue;

                        mask[i + j * S] = colors[slice * ChunkStride[axis] + i * ChunkStride[u] + j * ChunkStride[v]];
                        anyFace = true;
                        data.VisibleFaces++;
                    }

                    if (!anyFace) continue;

                    for (int j = 0; j < S; j++)
                    for (int i = 0; i < S; i++)
                    {
                        uint color = mask[i + j * S];
                        if (color == 0) continue;

                        int w = 1;
                        while (i + w < S && mask[i + w + j * S] == color) w++;

                        int h = 1;
                        bool grow = true;
                        while (j + h < S && grow)
                        {
                            for (int k = 0; k < w; k++)
                            {
                                if (mask[i + k + (j + h) * S] != color)
                                {
                                    grow = false;
                                    break;
                                }
                            }

                            if (grow) h++;
                        }

                        for (int jj = 0; jj < h; jj++)
                        for (int ii = 0; ii < w; ii++)
                        {
                            mask[i + ii + (j + jj) * S] = 0;
                        }

                        EmitQuad(data, axis, positive, slice + (positive ? 1 : 0), i, j, w, h, color);
                    }
                }
            }
        }
    }

    private static Vector3 Corner(int axis, int u, int v, int a, int b, int c)
    {
        var p = Vector3.Zero;
        p[axis] = a * BuildSpace.CellSize;
        p[u] = b * BuildSpace.CellSize;
        p[v] = c * BuildSpace.CellSize;
        return p;
    }

    /// <summary>
    /// Godot считает лицевой стороной треугольника ту, с которой вершины идут ПО часовой стрелке,
    /// поэтому порядок вершин зависит от знака нормали (проверяется в самотесте).
    /// </summary>
    private static void EmitQuad(ChunkMeshData d, int axis, bool positive, int plane, int i, int j, int w, int h, uint packed)
    {
        int u = (axis + 1) % 3;
        int v = (axis + 2) % 3;

        int first = d.Vertices.Count;
        d.Vertices.Add(Corner(axis, u, v, plane, i, j));         // p00
        d.Vertices.Add(Corner(axis, u, v, plane, i + w, j));     // p10
        d.Vertices.Add(Corner(axis, u, v, plane, i + w, j + h)); // p11
        d.Vertices.Add(Corner(axis, u, v, plane, i, j + h));     // p01

        var normal = Vector3.Zero;
        normal[axis] = positive ? 1 : -1;
        var color = CellColor.Unpack(packed);
        for (int k = 0; k < 4; k++)
        {
            d.Normals.Add(normal);
            d.Colors.Add(color);
        }

        if (positive)
        {
            d.Indices.Add(first); d.Indices.Add(first + 3); d.Indices.Add(first + 2);
            d.Indices.Add(first); d.Indices.Add(first + 2); d.Indices.Add(first + 1);
        }
        else
        {
            d.Indices.Add(first); d.Indices.Add(first + 1); d.Indices.Add(first + 2);
            d.Indices.Add(first); d.Indices.Add(first + 2); d.Indices.Add(first + 3);
        }
    }

    // ---------------------------------------------------------------- каркас

    private static void BuildEdges(ChunkMeshData data, bool[] solid)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            int u = (axis + 1) % 3;
            int v = (axis + 2) % 3;

            for (int pv = 0; pv < S; pv++)
            for (int pu = 0; pu < S; pu++)
            {
                // Идём вдоль оси и склеиваем подряд идущие одинаковые единичные рёбра в один отрезок.
                int runStart = 0;
                int runConfig = 0;
                for (int pa = 0; pa <= S; pa++)
                {
                    int config = pa < S ? EdgeConfig(solid, axis, u, v, pa, pu, pv) : 0;
                    if (config == runConfig) continue;

                    if (runConfig != 0) AddEdge(data, axis, u, v, runStart, pa, pu, pv, runConfig);
                    runStart = pa;
                    runConfig = config;
                }
            }
        }
    }

    /// <summary>
    /// Ребро проходит вдоль <paramref name="axis"/> и начинается в узле (pa, pu, pv). Вокруг него 4 клетки:
    /// бит0 = (u-1, v-1), бит1 = (u, v-1), бит2 = (u-1, v), бит3 = (u, v).
    /// Возвращает маску заполненных клеток, если ребро «настоящее», иначе 0.
    /// </summary>
    private static int EdgeConfig(bool[] solid, int axis, int u, int v, int pa, int pu, int pv)
    {
        bool s0 = solid[Padded(axis, u, v, pa, pu - 1, pv - 1)];
        bool s1 = solid[Padded(axis, u, v, pa, pu, pv - 1)];
        bool s2 = solid[Padded(axis, u, v, pa, pu - 1, pv)];
        bool s3 = solid[Padded(axis, u, v, pa, pu, pv)];

        int count = (s0 ? 1 : 0) + (s1 ? 1 : 0) + (s2 ? 1 : 0) + (s3 ? 1 : 0);
        bool feature = count == 1 || count == 3 || (count == 2 && s0 == s3);
        if (!feature) return 0;

        return (s0 ? 1 : 0) | (s1 ? 2 : 0) | (s2 ? 4 : 0) | (s3 ? 8 : 0);
    }

    private static int Padded(int axis, int u, int v, int a, int b, int c) =>
        PaddedBase + a * PaddedStride[axis] + b * PaddedStride[u] + c * PaddedStride[v];

    private static void AddEdge(ChunkMeshData d, int axis, int u, int v, int start, int end, int pu, int pv, int config)
    {
        // Направление «наружу» = сумма центров пустых клеток вокруг ребра; для плоского шва (0) сдвиг не нужен.
        float du = 0f, dv = 0f;
        if ((config & 1) == 0) { du -= 0.5f; dv -= 0.5f; }
        if ((config & 2) == 0) { du += 0.5f; dv -= 0.5f; }
        if ((config & 4) == 0) { du -= 0.5f; dv += 0.5f; }
        if ((config & 8) == 0) { du += 0.5f; dv += 0.5f; }

        float offU = 0f, offV = 0f;
        float length = MathF.Sqrt(du * du + dv * dv);
        if (length > 1e-4f)
        {
            float k = WireOffsetMeters / length;
            offU = du * k;
            offV = dv * k;
        }

        var a = Vector3.Zero;
        var b = Vector3.Zero;
        a[axis] = start * BuildSpace.CellSize;
        b[axis] = end * BuildSpace.CellSize;
        a[u] = b[u] = pu * BuildSpace.CellSize + offU;
        a[v] = b[v] = pv * BuildSpace.CellSize + offV;
        d.LineVertices.Add(a);
        d.LineVertices.Add(b);
    }
}
