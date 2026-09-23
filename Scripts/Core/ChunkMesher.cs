using System;
using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Core;

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
/// Это относится только к кубическим блокам (<see cref="BlockShape.Cube"/>) — клетки не-кубических форм
/// (Wedge/Pyramid/InvertedPyramid) по-прежнему числятся «занятыми» (участвуют в отсечении граней соседей и в
/// рейкасте), но своих собственных граней тут не рисуют: их визуал — отдельный меш на экземпляр, см.
/// <c>ShapeMeshBuilder</c>/<c>Editor.ShapeInstanceView</c>.
///
/// Каркас (wireframe): полное отображение полигонов — у каждого нарисованного (уже склеенного) прямоугольника
/// рисуется весь его контур плюс диагональ, разбивающая его на 2 треугольника (как встроенный wireframe-режим
/// движка: видно каждое ребро каждого треугольника, включая диагонали и швы между соседними прямоугольниками).
/// </summary>
public static class ChunkMesher
{
    private const int S = BuildSpace.ChunkSize;
    private const int P = S + 2; // с рамкой в 1 клетку с каждой стороны

    /// <summary>Смещение линий каркаса наружу от грани (метры), чтобы они не мерцали (z-fighting) на самой грани.</summary>
    public const float WireOffsetMeters = 0.004f;

    private static readonly int[] PaddedStride = { 1, P, P * P };
    private static readonly int[] ChunkStride = { 1, S, S * S };
    private const int PaddedBase = 1 + P + P * P;

    public static ChunkMeshData Build(VoxelGrid grid, Vector3I chunk)
    {
        var data = new ChunkMeshData();
        var solid = new bool[P * P * P];
        var ownFace = new bool[P * P * P];
        var colors = new uint[S * S * S];

        if (!Fill(grid, chunk, solid, ownFace, colors)) return data;

        BuildFaces(data, solid, ownFace, colors);
        return data;
    }

    private static bool Fill(VoxelGrid grid, Vector3I chunk, bool[] solid, bool[] ownFace, uint[] colors)
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
                    ushort id = source.Ids[li];
                    if (id == 0) continue;

                    int idx = PaddedBase + x + y * P + z * P * P;
                    solid[idx] = true;
                    ownFace[idx] = IsCubeShaped(id);
                    any = true;
                    if (ox == 1 && oy == 1 && oz == 1) colors[li] = source.Colors[li];
                }
            }
        }

        return any;
    }

    private static bool IsCubeShaped(ushort runtimeId)
    {
        if (!BlockCatalog.Instance.TryGetByRuntimeId(runtimeId, out var definition)) return true; // неизвестный id (напр. самотесты) — по умолчанию куб
        var building = definition.GetComponent<BuildingBlockComponent>();
        return building == null || building.Shape == BlockShape.Cube;
    }

    // ---------------------------------------------------------------- поверхность + каркас

    private static void BuildFaces(ChunkMeshData data, bool[] solid, bool[] ownFace, uint[] colors)
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
                        if (!solid[idx] || solid[idx + step * PaddedStride[axis]] || !ownFace[idx]) continue;

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
    /// поэтому порядок вершин зависит от знака нормали (проверяется в самотесте). Заодно рисует каркас этого
    /// прямоугольника: контур (4 ребра) + диагональ, которая делит его на те же 2 треугольника, что и индексы ниже
    /// (у обоих порядков обхода общая диагональ p00–p11) — сдвинутые наружу вдоль нормали на <see cref="WireOffsetMeters"/>.
    /// </summary>
    private static void EmitQuad(ChunkMeshData d, int axis, bool positive, int plane, int i, int j, int w, int h, uint packed)
    {
        int u = (axis + 1) % 3;
        int v = (axis + 2) % 3;

        var p00 = Corner(axis, u, v, plane, i, j);
        var p10 = Corner(axis, u, v, plane, i + w, j);
        var p11 = Corner(axis, u, v, plane, i + w, j + h);
        var p01 = Corner(axis, u, v, plane, i, j + h);

        int first = d.Vertices.Count;
        d.Vertices.Add(p00);
        d.Vertices.Add(p10);
        d.Vertices.Add(p11);
        d.Vertices.Add(p01);

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

        var offset = normal * WireOffsetMeters;
        p00 += offset; p10 += offset; p11 += offset; p01 += offset;
        AddLine(d, p00, p10);
        AddLine(d, p10, p11);
        AddLine(d, p11, p01);
        AddLine(d, p01, p00);
        AddLine(d, p00, p11); // диагональ
    }

    private static void AddLine(ChunkMeshData d, Vector3 a, Vector3 b)
    {
        d.LineVertices.Add(a);
        d.LineVertices.Add(b);
    }
}
