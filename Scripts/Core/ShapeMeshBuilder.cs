using Godot;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Core;

/// <summary>
/// Строит меш не-кубического блока (Wedge/Pyramid/InvertedPyramid) из сырых данных <see cref="BlockGeometry"/>:
/// отражает и поворачивает вершины ВНУТРИ единичного куба (0..1, вокруг его центра 0.5 — см. <paramref name="mirror"/>
/// ниже), и только потом растягивает результат под текущий размер экземпляра (<see cref="Construction.TrySetSize"/>
/// двигает именно эти вершины, пересобирая меш заново) — порядок важен, см. комментарий в <c>BuildData</c>. Строит
/// полный wireframe (контур + диагонали граней, как у <see cref="ChunkMesher"/>). Результат — в локальных
/// координатах относительно МИНИМАЛЬНОГО угла bounding box (0 .. Size*CellSize), тем же соглашением, что и чанки
/// (позиционируются в мире через угол, не центр).
/// </summary>
public static class ShapeMeshBuilder
{
    public static (ArrayMesh? Solid, ArrayMesh? Wire, ArrayMesh? Border) Build(BlockShape shape, Vector3I size, Vector3I rotationSteps, Vector3I mirror, Color color)
    {
        var data = BuildData(shape, size, rotationSteps, mirror, color);
        return data == null ? (null, null, null) : (data.CreateSolidMesh(), data.CreateWireMesh(), data.CreateBorderMesh());
    }

    /// <summary>То же самое, но отдаёт сырые данные меша (вершины/нормали/индексы/линии каркаса) вместо готового
    /// <see cref="ArrayMesh"/> — удобно для самотестов (проверка обхода треугольников, масштаба, поворота).
    /// null для <see cref="BlockShape.Cube"/> (его строит <see cref="ChunkMesher"/>, не этот класс).
    /// <paramref name="mirror"/> — по компоненте на X/Y/Z, 0 = как есть, 1 = отражена (координата унитарного
    /// пространства заменяется на <c>1 - c</c> ДО масштабирования/поворота, то есть блок отражается в собственных
    /// границах, вокруг своей середины по этой оси). Нормали граней отражаются тем же способом — правильный обход
    /// треугольников (винд) после этого чинит <see cref="EmitFace"/>, как и для поворота.</summary>
    public static ChunkMeshData? BuildData(BlockShape shape, Vector3I size, Vector3I rotationSteps, Vector3I mirror, Color color)
    {
        if (!BlockGeometry.TryGet(shape, out var localVertices, out var faces)) return null;

        var extent = new Vector3(size.X, size.Y, size.Z) * BuildSpace.CellSize;
        var rotation = ComposeRotation(rotationSteps);
        var unitCenter = new Vector3(0.5f, 0.5f, 0.5f);

        // Поворот/отражение применяются НАД НОРМАЛИЗОВАННЫМ единичным кубом (вокруг его центра 0.5,0.5,0.5) —
        // и только ПОТОМ результат растягивается под реальный размер (extent, в осях СЕТКИ/МИРА, не блока).
        // Раньше растягивали (Size) СНАЧАЛА, а вращали уже неравномерно вытянутую фигуру — при повороте на
        // 90°/270° вокруг оси, меняющей местами две РАЗНЫЕ по размеру грани (несимметричный Size), итоговый
        // bounding box поворачивался вместе с формой и переставал совпадать с реально занятой областью клеток
        // (Construction.Size — она осями сетки не поворачивается), из-за чего блок визуально "плавал" не по
        // границе клеток. Поворот единичного куба (90°-степени) всегда переводит [0,1]³ само в себя, поэтому
        // после растяжения на extent форма гарантированно укладывается ровно в занятые клетки.
        Vector3 ToWorld(Vector3 unit)
        {
            var oriented = rotation * (Mirrored(unit, mirror) - unitCenter) + unitCenter; // всё ещё внутри [0,1]³
            return oriented * extent; // теперь — реальные метры относительно угла bounding box
        }

        var data = new ChunkMeshData();
        var positions = new Vector3[8]; // максимум вершин в грани в наших формах — 4
        foreach (var (ring, localNormal, fullCoverage) in faces)
        {
            // Грани, целиком закрывающие одну из 6 осевых сторон клетки, рисует ChunkMesher (в том же проходе, что
            // и кубы, со склейкой/отсечением по соседям) — см. FullCoverageMask и BlockGeometry class doc.
            if (fullCoverage) continue;

            var worldNormal = (rotation * MirroredNormal(localNormal, mirror)).Normalized();
            for (int i = 0; i < ring.Length; i++) positions[i] = ToWorld(localVertices[ring[i]]);
            EmitFace(data, positions, ring.Length, worldNormal, color);
        }

        return data;
    }

    private static Vector3 Mirrored(Vector3 unit, Vector3I mirror) => new(
        mirror.X != 0 ? 1f - unit.X : unit.X,
        mirror.Y != 0 ? 1f - unit.Y : unit.Y,
        mirror.Z != 0 ? 1f - unit.Z : unit.Z);

    private static Vector3 MirroredNormal(Vector3 n, Vector3I mirror) => new(
        mirror.X != 0 ? -n.X : n.X,
        mirror.Y != 0 ? -n.Y : n.Y,
        mirror.Z != 0 ? -n.Z : n.Z);

    /// <summary>
    /// Какие из 6 осевых сторон клетки ЦЕЛИКОМ закрыты (не наклонными/треугольными гранями формы), после текущего
    /// поворота/отражения — бит = <c>axis*2 + (positive ? 1 : 0)</c> (axis: 0=X,1=Y,2=Z), т.е. ровно та же
    /// конвенция, что <see cref="ChunkMesher"/> использует для <c>FaceMask</c> у обычных кубов (там маска всегда
    /// <c>0b111111</c>). Для <see cref="BlockShape.Cube"/> и любой формы без данных в <see cref="BlockGeometry"/>
    /// вызывающая сторона должна сама подставить <c>0b111111</c> — этот метод для них вернёт 0 (нет граней вообще).
    /// </summary>
    public static byte FullCoverageMask(BlockShape shape, Vector3I rotationSteps, Vector3I mirror)
    {
        if (!BlockGeometry.TryGet(shape, out _, out var faces)) return 0;

        var rotation = ComposeRotation(rotationSteps);
        byte mask = 0;
        foreach (var (_, localNormal, fullCoverage) in faces)
        {
            if (!fullCoverage) continue;
            var worldNormal = (rotation * MirroredNormal(localNormal, mirror)).Normalized();
            mask |= AxisBit(worldNormal);
        }

        return mask;
    }

    /// <summary>Нормаль после 90°-поворотов и отражения всегда осеориентирована (компоненты в {-1,0,1}) —
    /// переводит её в один бит <c>axis*2 + (positive ? 1 : 0)</c>.</summary>
    private static byte AxisBit(Vector3 n)
    {
        if (n.X < -0.5f) return 1 << 0;
        if (n.X > 0.5f) return 1 << 1;
        if (n.Y < -0.5f) return 1 << 2;
        if (n.Y > 0.5f) return 1 << 3;
        if (n.Z < -0.5f) return 1 << 4;
        if (n.Z > 0.5f) return 1 << 5;
        return 0;
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

        // Wireframe: контур грани + диагонали веерной триангуляции (для треугольника их нет). Координаты РОВНО те
        // же, что у грани — без смещения по нормали (см. класс-док ChunkMesher: разница в RenderPriority между
        // сплошным мешем и линиями, а не геометрический офсет, чтобы линии не "плыли" на стыках разных нормалей).
        for (int i = 0; i < count; i++)
        {
            d.LineVertices.Add(d.Vertices[first + i]);
            d.LineVertices.Add(d.Vertices[first + (i + 1) % count]);
        }

        for (int i = 2; i < count - 1; i++)
        {
            d.LineVertices.Add(d.Vertices[first]);
            d.LineVertices.Add(d.Vertices[first + i]);
        }

        // Borders: только контур грани, без диагоналей (у не-кубических форм граница блока = граница его граней).
        for (int i = 0; i < count; i++)
        {
            d.BorderVertices.Add(d.Vertices[first + i]);
            d.BorderVertices.Add(d.Vertices[first + (i + 1) % count]);
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
