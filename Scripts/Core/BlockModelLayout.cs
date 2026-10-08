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

    /// <summary>Положение точки bbox с долями <paramref name="anchor"/> в координатах самой модели, ПОСЛЕ масштаба, но ДО
    /// сдвига якоря в клетку.</summary>
    private static Vector3 ScaledAnchorPoint(Aabb modelAabb, Vector3 scale, Vector3 anchor) =>
        modelAabb.Position * scale + modelAabb.Size * scale * anchor;

    /// <summary>Трансформ модели в рамку блока: масштаб по осям + сдвиг, при котором якорь встаёт на свою точку клетки (0,0,0).</summary>
    public static Transform3D ModelTransform(Aabb modelAabb, Vector3 scale, Vector3 anchor) =>
        new(Basis.Identity.Scaled(scale), AnchorTarget(anchor) - ScaledAnchorPoint(modelAabb, scale, anchor));

    /// <summary>bbox модели в рамке блока (метры) — то, что рисуется фиолетовой рамкой и из чего считается footprint.</summary>
    public static Aabb BoundsInBlockFrame(Aabb modelAabb, Vector3 scale, Vector3 anchor)
    {
        var transform = ModelTransform(modelAabb, scale, anchor);
        return new Aabb(modelAabb.Position * scale + transform.Origin, modelAabb.Size * scale);
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
