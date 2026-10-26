using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Blocks;

/// <summary>
/// Прямоугольная группа клеток в рамке блока (<see cref="BlockModelLayout"/>): <see cref="Min"/> — минимальная клетка (индексы
/// от корневой клетки (0,0,0), могут быть отрицательными), <see cref="Size"/> — размер в клетках (≥ 1 по каждой оси).
/// Используется для коллизии (<see cref="FunctionalBlockComponent.CollisionBoxes"/>) и как строка «группа клеток» в редакторе блоков.
/// </summary>
public readonly record struct CellBox(Vector3I Min, Vector3I Size)
{
    /// <summary>Минимальный угол в метрах (рамка блока).</summary>
    public Vector3 MinMeters => BuildSpace.CellMin(Min);

    /// <summary>Размер в метрах.</summary>
    public Vector3 SizeMeters => new Vector3(Size.X, Size.Y, Size.Z) * BuildSpace.CellSize;

    /// <summary>Центр бокса в метрах (рамка блока) — позиция <see cref="BoxShape3D"/> коллизии.</summary>
    public Vector3 CenterMeters => MinMeters + SizeMeters * 0.5f;

    public long CellCount => (long)Size.X * Size.Y * Size.Z;

    /// <summary>Все клетки группы.</summary>
    public IEnumerable<Vector3I> Cells()
    {
        for (int x = 0; x < Size.X; x++)
        for (int y = 0; y < Size.Y; y++)
        for (int z = 0; z < Size.Z; z++)
        {
            yield return new Vector3I(Min.X + x, Min.Y + y, Min.Z + z);
        }
    }
}

/// <summary>
/// Клетки коллизии ↔ боксы. В XML источник правды — сами клетки (целые индексы, <c>"collision": [[x,y,z], ...]</c>); при загрузке
/// они сливаются в набор боксов (<see cref="Merge"/>), которые и идут в физику (<c>World.VehicleSpawner</c>): параллелепипед
/// даёт ОДИН бокс, несвязные группы — отдельные боксы.
/// </summary>
public static class CollisionCells
{
    private static readonly int[][] AxisOrders =
    {
        new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 },
    };

    /// <summary>Клетки (без повторов), отсортированные по X, затем Y, затем Z — канонический порядок для записи в XML.</summary>
    public static List<Vector3I> Normalize(IEnumerable<Vector3I> cells) =>
        cells.Distinct().OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z).ToList();

    /// <summary>Клетки всех групп (без повторов, канонический порядок).</summary>
    public static List<Vector3I> Expand(IEnumerable<CellBox> boxes) => Normalize(boxes.SelectMany(b => b.Cells()));

    /// <summary>
    /// Сливает клетки в МИНИМАЛЬНЫЙ набор непересекающихся боксов, покрывающих ровно эти клетки (ни одной лишней и пропущенной).
    /// Бокс не может перекрыть пустую клетку, поэтому несвязные группы никогда не склеиваются. Жадный рост бокса от первой
    /// свободной клетки по трём осям; пробуются все 6 порядков осей, берётся тот, что даёт меньше боксов (при равенстве —
    /// первый, детерминированно). Для параллелепипеда, L-образной и ступенчатой фигур результат оптимален; для сложных
    /// фигур набор боксов гарантированно корректен, но минимальность в общем случае (NP-трудная задача) — только эвристическая.
    /// Результат отсортирован по Min (X, Y, Z).
    /// </summary>
    public static List<CellBox> Merge(IEnumerable<Vector3I> cells)
    {
        var all = new HashSet<Vector3I>(cells);
        List<CellBox>? best = null;
        foreach (var order in AxisOrders)
        {
            var candidate = Greedy(all, order[0], order[1], order[2]);
            if (best == null || candidate.Count < best.Count) best = candidate;
        }

        return (best ?? new List<CellBox>()).OrderBy(b => b.Min.X).ThenBy(b => b.Min.Y).ThenBy(b => b.Min.Z).ToList();
    }

    private static List<CellBox> Greedy(HashSet<Vector3I> all, int a0, int a1, int a2)
    {
        var remaining = new HashSet<Vector3I>(all);
        var ordered = all.OrderBy(c => c[a2]).ThenBy(c => c[a1]).ThenBy(c => c[a0]).ToList();
        var result = new List<CellBox>();

        foreach (var start in ordered)
        {
            if (!remaining.Contains(start)) continue;

            bool Has(int i, int j, int k)
            {
                var p = start;
                p[a0] += i;
                p[a1] += j;
                p[a2] += k;
                return remaining.Contains(p);
            }

            int n0 = 1;
            while (Has(n0, 0, 0)) n0++;

            int n1 = 1;
            while (RowFilled(n0, n1, 0)) n1++;

            int n2 = 1;
            while (SlabFilled(n0, n1, n2)) n2++;

            bool RowFilled(int len0, int j, int k)
            {
                for (int i = 0; i < len0; i++) if (!Has(i, j, k)) return false;
                return true;
            }

            bool SlabFilled(int len0, int len1, int k)
            {
                for (int j = 0; j < len1; j++) if (!RowFilled(len0, j, k)) return false;
                return true;
            }

            var size = Vector3I.One;
            size[a0] = n0;
            size[a1] = n1;
            size[a2] = n2;
            var box = new CellBox(start, size);
            foreach (var cell in box.Cells()) remaining.Remove(cell);
            result.Add(box);
        }

        return result;
    }
}
