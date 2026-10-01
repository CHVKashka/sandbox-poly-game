using System;
using System.Collections.Generic;
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
    public static (ArrayMesh? Solid, ArrayMesh? Wire, ArrayMesh? Border) Build(BlockShape shape, Vector3I size, Vector3I rotationSteps, Vector3I mirror, Color color, byte occludedMask = 0, bool includeFullCoverageFaces = false, IReadOnlyDictionary<int, uint>? regionColors = null) =>
        Build(shape, size, ComposeRotation(rotationSteps), mirror, color, occludedMask, includeFullCoverageFaces, regionColors);

    /// <summary>Та же сборка, но поворот — уже готовый произвольный <see cref="Basis"/>, а не 0..3 ступени вокруг
    /// X/Y/Z. Единственный потребитель произвольного базиса — анимация поворота призрака (см. <c>Editor.BuildEditor</c>,
    /// плавный довод между двумя ориентациями через <see cref="Basis.Slerp"/>) — у настоящих поставленных блоков
    /// поворот всегда одна из 0..3 ступеней (<see cref="Core.BlockInstance.RotationSteps"/>), для них по-прежнему
    /// используется перегрузка выше.</summary>
    public static (ArrayMesh? Solid, ArrayMesh? Wire, ArrayMesh? Border) Build(BlockShape shape, Vector3I size, Basis rotation, Vector3I mirror, Color color, byte occludedMask = 0, bool includeFullCoverageFaces = false, IReadOnlyDictionary<int, uint>? regionColors = null)
    {
        var data = BuildData(shape, size, rotation, mirror, color, occludedMask, includeFullCoverageFaces, regionColors);
        return data == null ? (null, null, null) : (data.CreateSolidMesh(), data.CreateWireMesh(), data.CreateBorderMesh());
    }

    /// <summary>То же самое, но отдаёт сырые данные меша (вершины/нормали/индексы/линии каркаса) вместо готового
    /// <see cref="ArrayMesh"/> — удобно для самотестов (проверка обхода треугольников, масштаба, поворота).
    /// null для <see cref="BlockShape.Cube"/> (его строит <see cref="ChunkMesher"/>, не этот класс).
    /// <paramref name="mirror"/> — по компоненте на X/Y/Z, 0 = как есть, 1 = отражена (координата унитарного
    /// пространства заменяется на <c>1 - c</c> ДО масштабирования/поворота, то есть блок отражается в собственных
    /// границах, вокруг своей середины по этой оси). Нормали граней отражаются тем же способом — правильный обход
    /// треугольников (винд) после этого чинит <see cref="EmitFace"/>, как и для поворота.
    /// <paramref name="includeFullCoverageFaces"/> — для превью размещения ("призрак", см. <c>Editor.BuildEditor</c>):
    /// у настоящего поставленного блока полные грани рисует <see cref="ChunkMesher"/> (склейка/отсечение по соседям
    /// из <see cref="VoxelGrid"/>), но призрак никогда не попадает в сетку, поэтому без этого флага у него не было
    /// бы вообще никаких полных граней (низа/задней стенки у Wedge и т.п.) — только рампа и треугольные борта, то
    /// есть визуально "дырявый" силуэт. true заставляет нарисовать их тут же, без склейки с соседями (превью
    /// разово, соседей у него нет).
    /// <paramref name="regionColors"/> — точечная покраска (см. <see cref="Construction.PaintRegion"/>/
    /// <see cref="TryFindPaintRegion"/>): цвет отдельной грани формы (индекс — позиция в массиве <c>faces</c> у
    /// <see cref="BlockGeometry"/>, СТАБИЛЬНА независимо от поворота/отражения/размера — это позиция В ДАННЫХ, а не
    /// в мировых осях), переопределяющий <paramref name="color"/> только для этой грани. null или отсутствие ключа —
    /// грань красится в общий <paramref name="color"/> экземпляра, как раньше.</summary>
    public static ChunkMeshData? BuildData(BlockShape shape, Vector3I size, Vector3I rotationSteps, Vector3I mirror, Color color, byte occludedMask = 0, bool includeFullCoverageFaces = false, IReadOnlyDictionary<int, uint>? regionColors = null) =>
        BuildData(shape, size, ComposeRotation(rotationSteps), mirror, color, occludedMask, includeFullCoverageFaces, regionColors);

    /// <summary>Та же сборка, но поворот — уже готовый произвольный <see cref="Basis"/> — см. doc-комментарий
    /// соответствующей перегрузки <see cref="Build(BlockShape,Vector3I,Basis,Vector3I,Color,byte,bool,IReadOnlyDictionary{int,uint})"/>.</summary>
    public static ChunkMeshData? BuildData(BlockShape shape, Vector3I size, Basis rotation, Vector3I mirror, Color color, byte occludedMask = 0, bool includeFullCoverageFaces = false, IReadOnlyDictionary<int, uint>? regionColors = null)
    {
        if (!BlockGeometry.TryGet(shape, out var localVertices, out var faces)) return null;

        var extent = new Vector3(size.X, size.Y, size.Z) * BuildSpace.CellSize;
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
        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            var (ring, localNormal, fullCoverage) = faces[faceIndex];

            // Грани, целиком закрывающие одну из 6 осевых сторон клетки, обычно рисует ChunkMesher (в том же
            // проходе, что и кубы, со склейкой/отсечением по соседям) — см. FullCoverageMask и BlockGeometry class
            // doc. Исключение — includeFullCoverageFaces (призрак, см. его doc-комментарий выше).
            if (fullCoverage && !includeFullCoverageFaces) continue;

            var worldNormal = (rotation * MirroredNormal(localNormal, mirror)).Normalized();

            // ЧАСТИЧНАЯ (не FullCoverage), но всё же осеориентированная грань (треугольные борта Wedge, боковые
            // грани Pyramid/InvertedPyramid — в отличие от рампы/среза, которые всегда диагональны и сюда не
            // попадают, см. TryAxisAlignedBit) не рисуется, если ПО ВСЕЙ границе экземпляра в эту сторону стоит
            // сплошной сосед, целиком закрывающий СВОЮ обращённую сюда сторону (occludedMask — см.
            // ComputeOcclusionMask) — тогда эта грань гарантированно спрятана внутри постройки, камера её увидеть
            // не может ни при каком ракурсе (см. Docs/ROADMAP.md, п. 2). Само по себе устройство этой грани — плоский
            // треугольник ровно в этой осевой плоскости, просто не покрывающий её целиком (BlockGeometry class doc) —
            // отсюда безопасность: сосед, целиком закрывающий ту же плоскость, накрывает и её тоже. (fullCoverage
            // грани сюда не попадают при обычном вызове — continue выше — поэтому occludedMask на них не влияет.)
            if (!fullCoverage && TryAxisAlignedBit(worldNormal, out byte bit) && (occludedMask & bit) != 0) continue;

            var faceColor = regionColors != null && regionColors.TryGetValue(faceIndex, out var packed)
                ? CellColor.Unpack(packed)
                : color;

            for (int i = 0; i < ring.Length; i++) positions[i] = ToWorld(localVertices[ring[i]]);
            EmitFace(data, positions, ring.Length, worldNormal, faceColor);
        }

        return data;
    }

    /// <summary>
    /// Точечная покраска наклонных/треугольных граней: НАСТОЯЩЕЕ пересечение луча камеры с реальной геометрией формы
    /// (веерная триангуляция каждой не-<c>FullCoverage</c> грани, та же, что строит <see cref="BuildData"/>/
    /// <see cref="EmitFace"/> — Мёллер–Трумбор per-треугольник, ближайшее по лучу пересечение выигрывает), а не
    /// приближение по осевому направлению попадания в ограничивающий куб клетки (то было <see cref="TryFindPaintRegion"/>,
    /// оставлен как запасной вариант ниже). Только так возможно попасть именно в диагональную грань (рампа Wedge,
    /// срез Pyramid/InvertedPyramid) НАПРЯМУЮ, а не только откатом — например, срез InvertedPyramid раньше был
    /// принципиально недостижим (все 6 осевых направлений её ограничивающего куба заняты другими гранями формы), а
    /// с точным рейкастом виден и красится, если луч действительно попадает в его треугольник. <paramref name="originWorld"/> —
    /// мировая позиция МИНИМАЛЬНОГО угла экземпляра (<see cref="BuildSpace.CellMin"/> от его <c>Origin</c>, то же
    /// соглашение, что и у <see cref="BuildData"/>); <paramref name="rayOrigin"/>/<paramref name="rayDir"/> — луч
    /// камеры в мировых координатах (метры). false — ни один треугольник не пересечён (например, ограничивающий куб
    /// клетки "цельный" для рейкастера, но в этой конкретной точке настоящей геометрии формы физически нет — см.
    /// <see cref="VoxelRaycaster"/> класс-док: он бьёт по кубу клетки, не по форме) — тогда вызывающая сторона
    /// откатывается на <see cref="TryFindPaintRegion"/>.
    /// </summary>
    public static bool TryRaycastFace(BlockShape shape, Vector3I size, Vector3I rotationSteps, Vector3I mirror, Vector3 originWorld, Vector3 rayOrigin, Vector3 rayDir, out int regionIndex)
    {
        regionIndex = -1;
        if (!BlockGeometry.TryGet(shape, out var localVertices, out var faces)) return false;

        var extent = new Vector3(size.X, size.Y, size.Z) * BuildSpace.CellSize;
        var rotation = ComposeRotation(rotationSteps);
        var unitCenter = new Vector3(0.5f, 0.5f, 0.5f);
        Vector3 ToWorld(Vector3 unit) => originWorld + (rotation * (Mirrored(unit, mirror) - unitCenter) + unitCenter) * extent;

        double bestT = double.PositiveInfinity;
        var positions = new Vector3[8];
        for (int faceIndex = 0; faceIndex < faces.Length; faceIndex++)
        {
            var (ring, _, fullCoverage) = faces[faceIndex];
            if (fullCoverage) continue; // эти красит VoxelGrid по клетке, сюда не входят вовсе

            for (int i = 0; i < ring.Length; i++) positions[i] = ToWorld(localVertices[ring[i]]);

            // Веерная триангуляция кольца — та же, что EmitFace использует для самого меша.
            for (int i = 1; i < ring.Length - 1; i++)
            {
                if (TryIntersectTriangle(rayOrigin, rayDir, positions[0], positions[i], positions[i + 1], out double t) && t < bestT)
                {
                    bestT = t;
                    regionIndex = faceIndex;
                }
            }
        }

        return regionIndex >= 0;
    }

    /// <summary>Пересечение луча с треугольником (алгоритм Мёллера–Трумбора); <paramref name="t"/> — расстояние
    /// вдоль луча (только положительные, т.е. вперёд по направлению взгляда) в случае пересечения.</summary>
    private static bool TryIntersectTriangle(Vector3 rayOrigin, Vector3 rayDir, Vector3 v0, Vector3 v1, Vector3 v2, out double t)
    {
        const double epsilon = 1e-9;
        t = 0;

        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var pvec = rayDir.Cross(edge2);
        double det = edge1.Dot(pvec);
        if (Math.Abs(det) < epsilon) return false; // луч параллелен плоскости треугольника

        double invDet = 1.0 / det;
        var tvec = rayOrigin - v0;
        double u = tvec.Dot(pvec) * invDet;
        if (u < -1e-6 || u > 1.0 + 1e-6) return false;

        var qvec = tvec.Cross(edge1);
        double v = rayDir.Dot(qvec) * invDet;
        if (v < -1e-6 || u + v > 1.0 + 1e-6) return false;

        t = edge2.Dot(qvec) * invDet;
        return t > epsilon;
    }

    /// <summary>
    /// Точечная покраска наклонных/треугольных граней — ЗАПАСНОЙ вариант на случай, когда <see cref="TryRaycastFace"/>
    /// не нашёл настоящего пересечения (луч бьёт по ограничивающему кубу клетки мимо реальной геометрии формы, см.
    /// его doc-комментарий): для стороны клетки, куда попал луч (<paramref name="hitBit"/> —
    /// <c>axis*2+(positive?1:0)</c>, конвенция <see cref="FullCoverageMask"/>/<see cref="VoxelGrid.GetFaceMask"/>),
    /// но которая НЕ FullCoverage (иначе красить нужно грань КЛЕТКИ в <see cref="VoxelGrid"/>, а не сюда — см.
    /// <c>Editor.BuildEditor.UseToolAtHover</c>), определяет, какую именно грань формы (индекс в массиве <c>faces</c>
    /// у <see cref="BlockGeometry"/> — см. doc <paramref name="regionColors"/> у <see cref="BuildData"/>) красить
    /// <see cref="Construction.PaintRegion"/>. Если по этому направлению у формы есть СВОЯ осеориентированная
    /// частичная грань (треугольные борта Wedge, грани-основания Pyramid и т.п.) — возвращает именно её; иначе
    /// (сторона, где у формы вообще нет геометрии в этом точном осевом направлении — например, верх/перед Wedge, где
    /// на самом деле видна диагональная рампа) возвращает единственную диагональную грань формы (рампа/срез), если
    /// она есть — приближение похуже точного рейкаста выше, но всё ещё лучше, чем красить весь экземпляр. false — у
    /// формы вообще нет данных (Cube) или совсем нет граней вообще.
    /// </summary>
    public static bool TryFindPaintRegion(BlockShape shape, Vector3I rotationSteps, Vector3I mirror, byte hitBit, out int regionIndex)
    {
        regionIndex = -1;
        if (!BlockGeometry.TryGet(shape, out _, out var faces)) return false;

        var rotation = ComposeRotation(rotationSteps);
        int diagonal = -1;
        for (int i = 0; i < faces.Length; i++)
        {
            var (_, localNormal, fullCoverage) = faces[i];
            if (fullCoverage) continue;

            var worldNormal = (rotation * MirroredNormal(localNormal, mirror)).Normalized();
            if (TryAxisAlignedBit(worldNormal, out byte bit))
            {
                if (bit != hitBit) continue;
                regionIndex = i;
                return true;
            }

            diagonal = i; // диагональная грань формы (рампа/срез) — их не больше одной на форму
        }

        if (diagonal < 0) return false;
        regionIndex = diagonal;
        return true;
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

    /// <summary>
    /// Как <see cref="AxisBit"/>, но строго: true, только если нормаль ТОЧНО осеориентирована (после 90°-поворота
    /// одна компонента ~±1, другие ~0) — в отличие от AxisBit (который трактует любую нормаль с доминирующей осью
    /// как осевую и годится только там, где это заведомо гарантировано, т.е. для FullCoverage-граней), это нужно
    /// для ЧАСТИЧНЫХ граней, среди которых есть настоящие диагональные (рампа Wedge, срез Pyramid/InvertedPyramid,
    /// нормаль вида (1,1,1).Normalized()) — их отсечение по соседу невозможно (нет простого осевого соседа,
    /// который мог бы их спрятать целиком) и не должно даже пытаться сработать.
    /// </summary>
    private static bool TryAxisAlignedBit(Vector3 n, out byte bit)
    {
        const float threshold = 1f - 0.001f;
        if (n.X < -threshold) { bit = 1 << 0; return true; }
        if (n.X > threshold) { bit = 1 << 1; return true; }
        if (n.Y < -threshold) { bit = 1 << 2; return true; }
        if (n.Y > threshold) { bit = 1 << 3; return true; }
        if (n.Z < -threshold) { bit = 1 << 4; return true; }
        if (n.Z > threshold) { bit = 1 << 5; return true; }
        bit = 0;
        return false;
    }

    /// <summary>
    /// Для каждого из 6 осевых направлений: стоит ли ПО ВСЕЙ границе экземпляра (все клетки его bounding box вдоль
    /// двух других осей, от <paramref name="origin"/> до <paramref name="maxCell"/> включительно) сплошной сосед,
    /// целиком закрывающий СВОЮ обращённую сюда сторону (<see cref="VoxelGrid.GetFaceMask"/>) — если да, соответствующий
    /// бит взводится в результате, и <see cref="BuildData"/> не рисует частичную (не FullCoverage, но осеориентированную)
    /// грань экземпляра в эту сторону, см. её комментарий. Консервативно: не хватает соседа хотя бы у одной клетки
    /// границы — направление считается ОТКРЫТЫМ (грань рисуется как раньше); лучше лишний невидимый полигон, чем
    /// настоящая дыра. Не пытается распознать совпадение частичной геометрии двух соседних форм (например, два
    /// зеркальных скоса, формирующих общий конёк крыши, где обе стороны сами по себе НЕ FullCoverage) — см.
    /// Docs/ROADMAP.md, п. 2, оставшийся scoped-гэп.
    /// </summary>
    public static byte ComputeOcclusionMask(VoxelGrid grid, Vector3I origin, Vector3I maxCell)
    {
        byte mask = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            if (IsBoundaryBacked(grid, origin, maxCell, axis, false)) mask |= (byte)(1 << (axis * 2 + 0));
            if (IsBoundaryBacked(grid, origin, maxCell, axis, true)) mask |= (byte)(1 << (axis * 2 + 1));
        }

        return mask;
    }

    private static bool IsBoundaryBacked(VoxelGrid grid, Vector3I origin, Vector3I maxCell, int axis, bool positive)
    {
        int u = (axis + 1) % 3;
        int v = (axis + 2) % 3;
        int neighborLayer = (positive ? maxCell[axis] : origin[axis]) + (positive ? 1 : -1);
        // Сосед по эту сторону закрывает нас, если он закрывает СВОЮ обращённую к нам сторону — она напротив нашей.
        int neighborBit = 1 << (axis * 2 + (positive ? 0 : 1));

        for (int b = origin[v]; b <= maxCell[v]; b++)
        for (int a = origin[u]; a <= maxCell[u]; a++)
        {
            var neighbor = Vector3I.Zero;
            neighbor[axis] = neighborLayer;
            neighbor[u] = a;
            neighbor[v] = b;

            if (!BuildSpace.InBounds(neighbor) || !grid.IsSolid(neighbor)) return false;
            if ((grid.GetFaceMask(neighbor) & neighborBit) == 0) return false;
        }

        return true;
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

    /// <summary>Три четверть-поворота (0..3 каждый) вокруг X, Y, Z, применённые в этом порядке — ФИКСИРОВАННЫЙ
    /// порядок осей внутри ОДНОГО вызова (не зависит от того, в каком порядке их меняли снаружи, см.
    /// <see cref="Editor.EditorState.PendingRotationBasis"/> class doc про то, почему для РЕДАКТИРУЕМОЙ, пошагово
    /// накапливаемой ориентации этого недостаточно — здесь же, для УЖЕ готовой тройки конкретного поставленного
    /// блока, однозначность не нужна, нужна только воспроизводимость).</summary>
    public static Basis ComposeRotation(Vector3I steps)
    {
        var basis = Basis.Identity;
        if (steps.X != 0) basis = basis.Rotated(Vector3.Right, steps.X * Mathf.Pi / 2f);
        if (steps.Y != 0) basis = basis.Rotated(Vector3.Up, steps.Y * Mathf.Pi / 2f);
        if (steps.Z != 0) basis = basis.Rotated(new Vector3(0, 0, 1), steps.Z * Mathf.Pi / 2f);
        return basis;
    }
}
