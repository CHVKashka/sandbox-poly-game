using System;
using System.Collections.Generic;
using Godot;

namespace SandboxPolyGame.Core;

/// <summary>Результат меширования одного чанка. Координаты вершин — в метрах, относительно угла чанка.</summary>
public sealed class ChunkMeshData
{
    public readonly List<Vector3> Vertices = new();
    public readonly List<Vector3> Normals = new();
    public readonly List<Color> Colors = new();
    public readonly List<int> Indices = new();

    /// <summary>Пары вершин: каждый отрезок каркаса (полигоны + диагонали, инструмент Wireframe) = 2 вершины.</summary>
    public readonly List<Vector3> LineVertices = new();

    /// <summary>Пары вершин: каждый отрезок границ отдельных клеток/блоков (без диагоналей, инструмент Borders) = 2 вершины.</summary>
    public readonly List<Vector3> BorderVertices = new();

    /// <summary>Сколько одиночных видимых граней было до склейки (для статистики).</summary>
    public int VisibleFaces;

    public int Quads => Indices.Count / 6;
    public int LineSegments => LineVertices.Count / 2;
    public int BorderSegments => BorderVertices.Count / 2;
    public bool IsEmpty => Indices.Count == 0 && LineVertices.Count == 0 && BorderVertices.Count == 0;

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

    public ArrayMesh? CreateWireMesh() => CreateLineMesh(LineVertices);

    public ArrayMesh? CreateBorderMesh() => CreateLineMesh(BorderVertices);

    private static ArrayMesh? CreateLineMesh(List<Vector3> vertices)
    {
        if (vertices.Count == 0) return null;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return mesh;
    }
}

/// <summary>
/// Строит меш чанка.
///
/// Поверхность: у каждой занятой клетки есть 6-битная маска <see cref="VoxelChunk.FaceMask"/> — какие из 6 осевых
/// сторон она закрывает ЦЕЛИКОМ (у куба — все шесть; у Wedge/InvertedPyramid — только те прямоугольные грани, что
/// реально совпадают с гранью клетки, например низ и задняя стенка Wedge; у Pyramid — никакие, там таких граней нет
/// вообще, см. <c>ShapeMeshBuilder.FullCoverageMask</c>/<c>BlockGeometry</c>). Грань клетки по направлению рисуется,
/// только если сама клетка её закрывает (маска) И (по этому направлению нет соседа, ИЛИ соседняя клетка не закрывает
/// СВОЮ сторону, обращённую сюда, целиком — тогда в стыке была бы настоящая дыра, если её всё равно скрыть).
/// Соседние видимые грани одного цвета в одной плоскости затем склеиваются в прямоугольники (greedy meshing) — как и
/// раньше для кубов, но теперь ЛЮБАЯ клетка может внести грань в этот проход по своим "полным" сторонам. Остальную,
/// НЕ покрытую целиком геометрию не-кубических форм (наклонные/треугольные грани) по-прежнему рисует отдельный меш
/// на экземпляр, всегда полностью, см. <c>ShapeMeshBuilder</c>/<c>Editor.ShapeInstanceView</c>.
///
/// Каркас (инструмент Wireframe): полное отображение полигонов — у каждого нарисованного (уже склеенного)
/// прямоугольника рисуется весь его контур плюс диагональ, разбивающая его на 2 треугольника (как встроенный
/// wireframe-режим движка: видно каждое ребро каждого треугольника, включая диагонали и швы между соседними
/// прямоугольниками).
///
/// Границы (инструмент Borders): независимо от склейки в прямоугольники, для каждой занятой клетки рисуется
/// её собственная граница (без диагоналей) чёрным цветом — в отличие от Wireframe, тут видно исходные клетки/блоки,
/// даже если несколько соседних склеились в один большой прямоугольник.
///
/// Обе группы линий рисуются РОВНО на поверхности грани (без смещения по нормали) — отдельными
/// <c>MeshInstance3D</c> с более высоким <c>RenderPriority</c>, чем у сплошных граней (см. <c>VoxelWorld</c>/
/// <c>ShapeInstanceView</c>), поэтому они консистентно рисуются поверх без мерцания (z-fighting), но БЕЗ дрейфа в
/// сторону от поверхности. Смещение по нормали использовалось раньше и оказалось хуже: у соседних граней с разными
/// нормалями (например, куб и примыкающий к нему скос) офсет уводит линии в разные стороны, и на стыке они
/// расходятся/перекрещиваются, выглядя как отдельная, будто «перпендикулярная» геометрия.
/// </summary>
public static class ChunkMesher
{
    private const int S = BuildSpace.ChunkSize;
    private const int P = S + 2; // с рамкой в 1 клетку с каждой стороны

    private static readonly int[] PaddedStride = { 1, P, P * P };
    private static readonly int[] ChunkStride = { 1, S, S * S };
    private const int PaddedBase = 1 + P + P * P;

    public static ChunkMeshData Build(VoxelGrid grid, Vector3I chunk)
    {
        var data = new ChunkMeshData();
        var solid = new bool[P * P * P];
        var ownFaceMask = new byte[P * P * P];
        var faceColors = new uint[S * S * S * 6];

        if (!Fill(grid, chunk, solid, ownFaceMask, faceColors)) return data;

        BuildFaces(data, solid, ownFaceMask, faceColors);
        return data;
    }

    private static bool Fill(VoxelGrid grid, Vector3I chunk, bool[] solid, byte[] ownFaceMask, uint[] faceColors)
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
                    ownFaceMask[idx] = source.FaceMask[li];
                    any = true;
                    if (ox == 1 && oy == 1 && oz == 1)
                    {
                        for (int f = 0; f < 6; f++) faceColors[li * 6 + f] = source.FaceColors[li * 6 + f];
                    }
                }
            }
        }

        return any;
    }

    private static bool HasFace(byte faceMask, int axis, bool positive) => (faceMask & (1 << (axis * 2 + (positive ? 1 : 0)))) != 0;

    // ---------------------------------------------------------------- поверхность + каркас

    private static void BuildFaces(ChunkMeshData data, bool[] solid, byte[] ownFaceMask, uint[] faceColors)
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
                        int neighborIdx = idx + step * PaddedStride[axis];
                        // Клетка вносит грань по этому направлению, только если сама закрывает его целиком (маска —
                        // у куба все 6 битов всегда включены). Сосед скрывает эту грань, только если ОН тоже
                        // закрывает СВОЮ обращённую сюда сторону целиком (та же маска, противоположный бит) —
                        // иначе в стыке образовалась бы настоящая дыра (сосед не рисует то, чего у него нет, см.
                        // BlockGeometry/ShapeMeshBuilder.FullCoverageMask).
                        if (!solid[idx] || !HasFace(ownFaceMask[idx], axis, positive)
                            || (solid[neighborIdx] && HasFace(ownFaceMask[neighborIdx], axis, !positive))) continue;

                        int cellIndex = slice * ChunkStride[axis] + i * ChunkStride[u] + j * ChunkStride[v];
                        mask[i + j * S] = faceColors[cellIndex * 6 + axis * 2 + (positive ? 1 : 0)];
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
    /// (у обоих порядков обхода общая диагональ p00–p11) — координаты РОВНО те же, что у самой грани (см. класс-док
    /// про смещение по нормали: раньше офсет уводил линии соседних граней с разными нормалями в разные стороны и
    /// они расходились/скрещивались прямо на стыке разно ориентированных поверхностей).
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

        AddLine(d.LineVertices, p00, p10);
        AddLine(d.LineVertices, p10, p11);
        AddLine(d.LineVertices, p11, p01);
        AddLine(d.LineVertices, p01, p00);
        AddLine(d.LineVertices, p00, p11); // диагональ

        EmitBorderGrid(d, axis, u, v, plane, i, j, w, h);
    }

    /// <summary>
    /// Сетка границ отдельных клеток внутри уже склеенного прямоугольника (w × h клеток): по (w+1) линий вдоль v
    /// и (h+1) линий вдоль u — то есть полная решётка 1×1, без диагоналей (в отличие от <see cref="EmitQuad"/>).
    /// </summary>
    private static void EmitBorderGrid(ChunkMeshData d, int axis, int u, int v, int plane, int i, int j, int w, int h)
    {
        for (int a = 0; a <= w; a++)
        {
            AddLine(d.BorderVertices, Corner(axis, u, v, plane, i + a, j), Corner(axis, u, v, plane, i + a, j + h));
        }

        for (int b = 0; b <= h; b++)
        {
            AddLine(d.BorderVertices, Corner(axis, u, v, plane, i, j + b), Corner(axis, u, v, plane, i + w, j + b));
        }
    }

    private static void AddLine(List<Vector3> lines, Vector3 a, Vector3 b)
    {
        lines.Add(a);
        lines.Add(b);
    }
}
