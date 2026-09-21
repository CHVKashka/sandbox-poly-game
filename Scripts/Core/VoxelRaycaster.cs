using System;
using Godot;

namespace SwV2.Core;

/// <summary>
/// Результат луча из камеры. Блок: <see cref="BlockCell"/> — попавший блок, <see cref="PlaceCell"/> — клетка рядом
/// с гранью, куда ставится новый блок. Земля (сетка на y=0): блока нет, PlaceCell — клетка над плоскостью.
/// </summary>
public readonly record struct RayHit(bool Found, bool IsBlock, Vector3I BlockCell, Vector3I PlaceCell, Vector3I Normal)
{
    public static readonly RayHit None = default;
}

/// <summary>Точный обход клеток лучом (алгоритм Амануидеса–Ву), без физики и коллайдеров.</summary>
public static class VoxelRaycaster
{
    public static RayHit Cast(VoxelGrid grid, Vector3 originWorld, Vector3 direction, double maxDistanceMeters = 300.0)
    {
        double cell = BuildSpace.CellSize;
        double ox = originWorld.X / cell, oy = originWorld.Y / cell, oz = originWorld.Z / cell;
        double dx = direction.X, dy = direction.Y, dz = direction.Z;
        double maxT = maxDistanceMeters / cell;

        int[] pos = { (int)Math.Floor(ox), (int)Math.Floor(oy), (int)Math.Floor(oz) };
        double[] o = { ox, oy, oz };
        double[] d = { dx, dy, dz };
        int[] step = new int[3];
        double[] tMax = new double[3];

        for (int a = 0; a < 3; a++)
        {
            if (d[a] > 0)
            {
                step[a] = 1;
                tMax[a] = (pos[a] + 1 - o[a]) / d[a];
            }
            else if (d[a] < 0)
            {
                step[a] = -1;
                tMax[a] = (pos[a] - o[a]) / d[a];
            }
            else
            {
                step[a] = 0;
                tMax[a] = double.PositiveInfinity;
            }
        }

        // Плоскость земли y = 0 (граница клеток): пересекается, только если луч идёт к ней.
        double tGround = double.PositiveInfinity;
        if ((dy < 0 && oy > 0) || (dy > 0 && oy < 0)) tGround = -oy / dy;

        int lastAxis = -1;
        for (int iteration = 0; iteration < 8192; iteration++)
        {
            var current = new Vector3I(pos[0], pos[1], pos[2]);
            if (lastAxis >= 0 && grid.IsSolid(current))
            {
                var normal = Vector3I.Zero;
                normal[lastAxis] = -step[lastAxis];
                return new RayHit(true, true, current, current + normal, normal);
            }

            int axis = tMax[0] < tMax[1] ? (tMax[0] < tMax[2] ? 0 : 2) : (tMax[1] < tMax[2] ? 1 : 2);
            double tNext = tMax[axis];

            if (tGround <= tNext)
            {
                // Луч достигает плоскости земли раньше, чем выйдет из текущей клетки. Клетку берём из самой точки
                // пересечения (а не из счётчика шагов): над плоскостью (y = 0) для камеры выше земли, под ней (y = -1) — ниже.
                var placeCell = new Vector3I(
                    (int)Math.Floor(ox + dx * tGround),
                    dy < 0 ? 0 : -1,
                    (int)Math.Floor(oz + dz * tGround));
                if (!BuildSpace.InBounds(placeCell)) return RayHit.None;
                return new RayHit(true, false, default, placeCell, new Vector3I(0, dy < 0 ? 1 : -1, 0));
            }

            if (double.IsInfinity(tNext) || tNext > maxT) break;

            pos[axis] += step[axis];
            // Не накапливаем tDelta (ошибка округления растёт с числом шагов), а считаем границу заново.
            tMax[axis] = ((step[axis] > 0 ? pos[axis] + 1 : pos[axis]) - o[axis]) / d[axis];
            lastAxis = axis;
        }

        return RayHit.None;
    }
}
