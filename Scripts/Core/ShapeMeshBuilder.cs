using Godot;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Core;

/// <summary>
/// Строит меш не-кубического блока (Wedge/Pyramid/InvertedPyramid) из сырых данных <see cref="BlockGeometry"/>:
/// масштабирует вершины под текущий размер экземпляра (<see cref="Construction.TrySetSize"/> двигает именно эти
/// вершины, пересобирая меш заново), поворачивает вокруг ЦЕНТРА блока (ориджин вершин — в нулевых координатах угла,
/// но ориджин самого блока для вращения — его центр) и строит полный wireframe (контур + диагонали граней, как
/// у <see cref="ChunkMesher"/>). Результат — в локальных координатах относительно МИНИМАЛЬНОГО угла bounding box
/// (0 .. Size*CellSize), тем же соглашением, что и чанки (позиционируются в мире через угол, не центр).
/// </summary>
public static class ShapeMeshBuilder
{
    public static (ArrayMesh? Solid, ArrayMesh? Wire) Build(BlockShape shape, Vector3I size, Vector3I rotationSteps, Color color)
    {
        var data = BuildData(shape, size, rotationSteps, color);
        return data == null ? (null, null) : (data.CreateSolidMesh(), data.CreateWireMesh());
    }

    /// <summary>То же самое, но отдаёт сырые данные меша (вершины/нормали/индексы/линии каркаса) вместо готового
    /// <see cref="ArrayMesh"/> — удобно для самотестов (проверка обхода треугольников, масштаба, поворота).
    /// null для <see cref="BlockShape.Cube"/> (его строит <see cref="ChunkMesher"/>, не этот класс).</summary>
    public static ChunkMeshData? BuildData(BlockShape shape, Vector3I size, Vector3I rotationSteps, Color color)
    {
        if (!BlockGeometry.TryGet(shape, out var localVertices, out var faces)) return null;

        var extent = new Vector3(size.X, size.Y, size.Z) * BuildSpace.CellSize;
        var center = extent * 0.5f;
        var rotation = ComposeRotation(rotationSteps);

        Vector3 ToWorld(Vector3 unit)
        {
            var scaled = unit * extent; // 0 .. extent, относительно угла bounding box
            var rotated = rotation * (scaled - center); // вращение вокруг центра блока
            return rotated + center; // обратно в координаты относительно угла
        }

        var data = new ChunkMeshData();
        var positions = new Vector3[8]; // максимум вершин в грани в наших формах — 4
        foreach (var (ring, localNormal) in faces)
        {
            var worldNormal = (rotation * localNormal).Normalized();
            for (int i = 0; i < ring.Length; i++) positions[i] = ToWorld(localVertices[ring[i]]);
            EmitFace(data, positions, ring.Length, worldNormal, color);
        }

        return data;
    }

    /// <summary>Кольцо (2..4 точки, уже в мировых локальных координатах), веерная триангуляция + полный wireframe.</summary>
    private static void EmitFace(ChunkMeshData d, Vector3[] positions, int count, Vector3 normal, Color color)
    {
        if (count < 3) return;

        // Наша конвенция (см. ChunkMesher): у правильно обойдённого треугольника (a,b,c) вектор (b-a)x(c-a)
        // направлен ПРОТИВ внешней нормали. Данные в BlockGeometry заданы произвольным обходом — чиним тут.
        var test = (positions[1] - positions[0]).Cross(positions[2] - positions[0]);
        bool reversed = test.Dot(normal) > 0f;

        int first = d.Vertices.Count;
        for (int i = 0; i < count; i++)
        {
            int idx = reversed ? count - 1 - i : i;
            d.Vertices.Add(positions[idx]);
            d.Normals.Add(normal);
            d.Colors.Add(color);
        }

        for (int i = 1; i < count - 1; i++)
        {
            d.Indices.Add(first);
            d.Indices.Add(first + i);
            d.Indices.Add(first + i + 1);
        }

        // Полное отображение полигонов: контур грани + диагонали веерной триангуляции (для треугольника их нет).
        var offset = normal * ChunkMesher.WireOffsetMeters;
        for (int i = 0; i < count; i++)
        {
            var a = d.Vertices[first + i] + offset;
            var b = d.Vertices[first + (i + 1) % count] + offset;
            d.LineVertices.Add(a);
            d.LineVertices.Add(b);
        }

        for (int i = 2; i < count - 1; i++)
        {
            d.LineVertices.Add(d.Vertices[first] + offset);
            d.LineVertices.Add(d.Vertices[first + i] + offset);
        }
    }

    /// <summary>Три четверть-поворота (0..3 каждый) вокруг X, Y, Z, применённые в этом порядке.</summary>
    public static Basis ComposeRotation(Vector3I steps)
    {
        var basis = Basis.Identity;
        if (steps.X != 0) basis = basis.Rotated(Vector3.Right, steps.X * Mathf.Pi / 2f);
        if (steps.Y != 0) basis = basis.Rotated(Vector3.Up, steps.Y * Mathf.Pi / 2f);
        if (steps.Z != 0) basis = basis.Rotated(new Vector3(0, 0, 1), steps.Z * Mathf.Pi / 2f);
        return basis;
    }
}
