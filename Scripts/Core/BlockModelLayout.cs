using System;
using Godot;

namespace SandboxPolyGame.Core;

/// <summary>
/// Чистая математика размещения модели функционального блока в «рамке блока» — новый формат блоков (экспериментальный
/// редактор блоков, ветка <c>expiremental-blockeditor</c>). Никакой автоподгонки по bbox нет: модель получает ЯВНЫЙ
/// масштаб по осям (<see cref="DefaultScale"/> по умолчанию: 2 м в Blender = 1 клетка = <see cref="BuildSpace.CellSize"/>
/// м) и ЯКОРЬ — одну из 27 точек её bbox (8 углов, 12 середин рёбер, 6 центров граней, центр), которая «встаёт в клетку
/// (0,0,0)».
/// <para/>
/// <b>Рамка блока</b> — метры от минимального угла КОРНЕВОЙ клетки (0,0,0), оси те же, что у сетки построек (X вправо,
/// Y вверх, Z — как в Godot). Клетка (0,0,0) — это та, что стоит под курсором при установке и вокруг которой блок
/// поворачивается (см. <see cref="BlockFootprint"/>); индексы других клеток блока (footprint, коллизия, ноды, порты)
/// могут быть отрицательными.
/// <para/>
/// <b>Правило якоря</b> (одно на все случаи): якорь хранится как доли <c>(fx,fy,fz)</c> вдоль bbox модели, каждая из
/// {0, 0.5, 1} (мин/центр/макс по оси). Точка bbox с этими долями ставится в точку клетки (0,0,0) с ТЕМИ ЖЕ долями:
/// <c>(fx,fy,fz) · CellSize</c>. Якорь по умолчанию (0,0,0) — это ровно правило «нижний задний левый угол bbox в точке
/// (0,0,0)»; якорь «центр» у модели в одну клетку даёт модель ровно по центру клетки (0,0,0); якорь «макс» у модели в
/// две клетки по X ставит её в клетки −1 и 0 (корень — правая клетка). Изменение масштаба двигает модель так, что
/// якорь остаётся на месте.
/// </summary>
public static class BlockModelLayout
{
    /// <summary>2 м в Blender = 1 клетка (0.25 м) → 0.125.</summary>
    public const float DefaultScale = 0.125f;

    /// <summary>Допуск при расчёте клеток bbox — доля клетки (1%): шум float не закрашивает соседнюю клетку.</summary>
    public const double CellTolerance = 0.01;

    public static readonly Vector3 DefaultScaleVector = new(DefaultScale, DefaultScale, DefaultScale);

    /// <summary>Привязывает долю якоря к одному из допустимых значений {0, 0.5, 1}.</summary>
    public static float SnapAnchorFraction(double value) => value < 0.25 ? 0f : value > 0.75 ? 1f : 0.5f;

    public static Vector3 SnapAnchor(Vector3 anchor) => new(
        SnapAnchorFraction(anchor.X), SnapAnchorFraction(anchor.Y), SnapAnchorFraction(anchor.Z));

    /// <summary>Точка клетки (0,0,0), в которую встаёт якорь с долями <paramref name="anchor"/> (рамка блока, метры).</summary>
    public static Vector3 AnchorTarget(Vector3 anchor) => anchor * BuildSpace.CellSize;

    /// <summary>Трансформ модели в рамку блока: масштаб по осям + сдвиг, при котором якорь встаёт на свою точку клетки (0,0,0).</summary>
    public static Transform3D ModelTransform(Aabb modelAabb, Vector3 scale, Vector3 anchor) =>
        ModelTransform(modelAabb, scale, anchor, Basis.Identity);

    /// <summary>
    /// Поворот модели в блоке задаётся углами Эйлера в ГРАДУСАХ (<c>"rotation": [x, y, z]</c>), порядок Godot по умолчанию — YXZ
    /// (сначала Z, потом X, потом Y). Градусы, а не кватернион/матрица — чтобы значения можно было читать и править руками.
    /// </summary>
    public static Basis RotationBasis(Vector3 eulerDegrees) =>
        Basis.FromEuler(new Vector3(Mathf.DegToRad(eulerDegrees.X), Mathf.DegToRad(eulerDegrees.Y), Mathf.DegToRad(eulerDegrees.Z)), EulerOrder.Yxz);

    /// <summary>Обратное к <see cref="RotationBasis"/>: углы Эйлера (градусы, до 2 знаков — без float-шума в файле) для поворота <paramref name="rotation"/>.</summary>
    public static Vector3 RotationDegrees(Basis rotation)
    {
        var euler = rotation.Orthonormalized().GetEuler(EulerOrder.Yxz);
        return new Vector3(Round(Mathf.RadToDeg(euler.X)), Round(Mathf.RadToDeg(euler.Y)), Round(Mathf.RadToDeg(euler.Z)));

        static float Round(double degrees)
        {
            double rounded = Math.Round(degrees, 2);
            return (float)(rounded == 0 ? 0 : rounded); // без «-0»
        }
    }

    /// <summary>
    /// Трансформ модели в рамку блока с поворотом: вершина <c>v</c> → <c>R · (scale · v) + t</c> (сначала масштаб по осям МОДЕЛИ, потом поворот). Якорь —
    /// доли bbox ПОВЁРНУТОЙ модели (его рамка — AABB восьми углов масштабированного bbox после поворота; для поворотов, кратных 90°, она точная, для остальных —
    /// чуть больше самой геометрии): точка с этими долями встаёт на свою точку клетки (0,0,0), так что при повороте якорь остаётся на месте.
    /// </summary>
    public static Transform3D ModelTransform(Aabb modelAabb, Vector3 scale, Vector3 anchor, Basis rotation)
    {
        var rotated = RotatedScaledBounds(modelAabb, scale, rotation);
        var anchorPoint = rotated.Position + rotated.Size * anchor;
        return new Transform3D(rotation * Basis.FromScale(scale), AnchorTarget(anchor) - anchorPoint);
    }

    /// <summary>bbox модели в рамке блока (метры) — то, что рисуется фиолетовой рамкой и из чего считается footprint.</summary>
    public static Aabb BoundsInBlockFrame(Aabb modelAabb, Vector3 scale, Vector3 anchor) =>
        BoundsInBlockFrame(modelAabb, scale, anchor, Basis.Identity);

    /// <summary><see cref="BoundsInBlockFrame(Aabb, Vector3, Vector3)"/> для повёрнутой модели (см. <see cref="ModelTransform(Aabb, Vector3, Vector3, Basis)"/>).</summary>
    public static Aabb BoundsInBlockFrame(Aabb modelAabb, Vector3 scale, Vector3 anchor, Basis rotation)
    {
        var transform = ModelTransform(modelAabb, scale, anchor, rotation);
        var rotated = RotatedScaledBounds(modelAabb, scale, rotation);
        return new Aabb(rotated.Position + transform.Origin, rotated.Size);
    }

    /// <summary>AABB восьми углов bbox модели после масштаба и поворота (до сдвига якоря в клетку).</summary>
    private static Aabb RotatedScaledBounds(Aabb modelAabb, Vector3 scale, Basis rotation)
    {
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = modelAabb.Position + new Vector3(
                (i & 1) != 0 ? modelAabb.Size.X : 0, (i & 2) != 0 ? modelAabb.Size.Y : 0, (i & 4) != 0 ? modelAabb.Size.Z : 0);
            var point = rotation * (corner * scale);
            min = new Vector3(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
            max = new Vector3(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
        }

        return new Aabb(min, max - min);
    }

    /// <summary>
    /// Минимальный и максимальный угол клетки <paramref name="min"/> .. <paramref name="min"/>+<paramref name="size"/> в клетках после правки вручную: размер не меньше 1,
    /// а корневая клетка (0,0,0) остаётся внутри (иначе повороты и «центр колеса» блока потеряли бы смысл). Возвращает исправленные значения.
    /// </summary>
    public static (Vector3I Min, Vector3I Size) SanitizeFootprint(Vector3I min, Vector3I size)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            size[axis] = Math.Clamp(size[axis], 1, 4096);
            min[axis] = Math.Clamp(min[axis], -4096, 4096);
            if (min[axis] > 0) { size[axis] += min[axis]; min[axis] = 0; }
            if (min[axis] + size[axis] - 1 < 0) size[axis] = -min[axis] + 1;
        }

        return (min, size);
    }

    /// <summary>
    /// Сдвиг одной грани бокса footprint'а на <paramref name="deltaCells"/> клеток (гизмо «Volume»): <paramref name="positiveFace"/> — грань на +оси (меняется размер),
    /// иначе на −оси (меняются min и размер, противоположная грань стоит на месте). Результат проходит <see cref="SanitizeFootprint"/>.
    /// </summary>
    public static (Vector3I Min, Vector3I Size) MoveFootprintFace(Vector3I min, Vector3I size, int axis, bool positiveFace, int deltaCells)
    {
        if (positiveFace) size[axis] += deltaCells;
        else
        {
            int newMin = Math.Min(min[axis] + deltaCells, min[axis] + size[axis] - 1); // меньше одной клетки не бывает
            size[axis] += min[axis] - newMin;
            min[axis] = newMin;
        }

        return SanitizeFootprint(min, size);
    }

    /// <summary>Точка bbox (рамка блока, метры) с долями <paramref name="fraction"/> — позиция якоря-ручки в редакторе.</summary>
    public static Vector3 PointOnBounds(Aabb bounds, Vector3 fraction) => bounds.Position + bounds.Size * fraction;

    /// <summary>Клетки, в которые попадает <paramref name="bounds"/> (рамка блока, метры): минимальная клетка и размер в клетках.
    /// Допуск <see cref="CellTolerance"/> — грань bbox, лежащая в пределах 1% клетки от границы клеток, не считается входящей
    /// в соседнюю клетку. Минимум 1×1×1 (плоская/вырожденная модель всё равно занимает клетку).</summary>
    public static (Vector3I Min, Vector3I Size) ComputeFootprint(Aabb bounds)
    {
        var min = Vector3I.Zero;
        var size = Vector3I.One;
        for (int axis = 0; axis < 3; axis++)
        {
            double lo = (double)bounds.Position[axis] / BuildSpace.CellSize;
            double hi = ((double)bounds.Position[axis] + (double)bounds.Size[axis]) / BuildSpace.CellSize;
            int first = (int)Math.Floor(lo + CellTolerance);
            int endExclusive = (int)Math.Ceiling(hi - CellTolerance);
            if (endExclusive <= first) endExclusive = first + 1;
            min[axis] = first;
            size[axis] = endExclusive - first;
        }

        return (min, size);
    }
}
