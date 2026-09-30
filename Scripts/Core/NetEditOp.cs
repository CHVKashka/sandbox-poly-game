using Godot;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Core;

/// <summary>Виды правки, которые совместное редактирование на верстаке умеет реплицировать — см.
/// Docs/05-world-and-vehicle-systems.md, «Мультиплеер». Полный паритет с одиночным редактированием
/// (2026-09-29 (6)) — те же 4 исхода, что различает <c>Editor.BuildEditor.UseToolAtHover</c> для Paint (какая именно
/// грань/регион/блок/клетка), не грубее.</summary>
public enum NetEditKind : byte
{
    Place = 0,
    Remove = 1,

    /// <summary>Клетка закрывает попавшую грань целиком (<c>VoxelGrid.GetFaceMask</c>) — красится РЕШЁТКА
    /// (<see cref="VoxelGrid.TryPaintFace"/>), не экземпляр. <c>extraInt</c> — ось (0=X/1=Y/2=Z),
    /// <c>extraBool</c> — положительная сторона оси.</summary>
    PaintFace = 2,

    /// <summary>Попадание — в наклонную/треугольную грань НЕ-кубической формы, распознанную клиентским рейкастом
    /// (<c>ShapeMeshBuilder.TryRaycastFace</c>/<c>TryFindPaintRegion</c> — то же самое, что уже вычисляет одиночное
    /// редактирование). <c>extraInt</c> — индекс региона (<see cref="BlockInstance.RegionColors"/>); владелец клетки
    /// определяется заново на принимающей стороне (<see cref="Construction.GetOwner"/>), не передаётся отдельно —
    /// клетка его однозначно определяет.</summary>
    PaintRegion = 3,

    /// <summary>Ни то, ни другое, но клетка принадлежит экземпляру — красится ВЕСЬ экземпляр целиком
    /// (<see cref="Construction.Paint"/>).</summary>
    PaintInstance = 4,

    /// <summary>Клетка никому не принадлежит (залита в обход <see cref="Construction"/>, см. <c>Dev.DemoBuilds</c>) —
    /// красится голая клетка решётки (<see cref="VoxelGrid.TryPaint"/>).</summary>
    PaintCell = 5,
}

/// <summary>
/// Единая точка правды "что означает эта правка" — используется и сервером сетевой сессии (<see cref="NetHub"/>,
/// применяет к авторитативной <see cref="Construction"/> сессии и проверяет её же правилами перед рассылкой), и
/// каждым клиентом (применяет РОВНО ТО ЖЕ САМОЕ к своей локальной копии при получении рассылки), и одиночным
/// редактором (<c>Editor.BuildEditor</c> с 2026-09-29 (6) тоже применяет правки через этот же метод — раньше мутировал
/// <see cref="Construction"/> напрямую своим отдельным кодом, теперь один код на все три случая, что исключает
/// целый класс ошибок "разошлись в трактовке одной и той же правки" между одиночным/сетевым путём.
/// </summary>
public static class NetEditOps
{
    /// <summary><paramref name="cell"/> — origin для <see cref="NetEditKind.Place"/>, целевая клетка для остальных.
    /// <paramref name="extraInt"/>/<paramref name="extraBool"/> — смысл зависит от <paramref name="kind"/>, см.
    /// <see cref="NetEditKind"/> doc на каждом значении. false — правка отклонена (нарушено правило соседства,
    /// занято, блок неизвестен, регион не найден и т.п.) — сервер в этом случае НЕ рассылает её дальше и НЕ пишет
    /// в историю (см. <see cref="NetHub"/>).</summary>
    public static bool Apply(Construction construction, NetEditKind kind, Vector3I cell, Vector3I size,
        string blockSlug, Color color, Vector3I rotation, Vector3I mirror, int extraInt = 0, bool extraBool = false)
    {
        switch (kind)
        {
            case NetEditKind.Place:
                if (!BlockCatalog.Instance.TryGetBySlug(blockSlug, out var definition)) return false;
                if (!PlacementRules.CanPlaceFootprint(construction.Grid, cell, size)) return false;
                return construction.PlaceBlock(cell, size, definition, color, rotation, mirror) != null;

            case NetEditKind.Remove:
                var owner = construction.GetOwner(cell);
                return owner != null ? construction.Remove(owner) : construction.Grid.TryRemove(cell);

            case NetEditKind.PaintFace:
                return construction.Grid.TryPaintFace(cell, extraInt, extraBool, CellColor.Pack(color));

            case NetEditKind.PaintRegion:
                var regionOwner = construction.GetOwner(cell);
                return regionOwner != null && construction.PaintRegion(regionOwner, extraInt, color);

            case NetEditKind.PaintInstance:
                var instanceOwner = construction.GetOwner(cell);
                return instanceOwner != null && construction.Paint(instanceOwner, color);

            case NetEditKind.PaintCell:
                return construction.Grid.TryPaint(cell, CellColor.Pack(color));

            default:
                return false;
        }
    }
}
