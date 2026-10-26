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

    /// <summary>Соединить две ноды проводом (<see cref="Construction.TryConnect"/>). Кодирование без новых полей протокола: <c>cell</c> — любая клетка
    /// ПЕРВОГО блока, <c>size</c> — любая клетка ВТОРОГО блока (поле переиспользовано как вторая клетка), <c>blockSlug</c> — <c>"узел1|узел2"</c>
    /// (<see cref="NetEditOps.EncodeNodes"/>). Какая нода выход, а какая вход, определяется на принимающей стороне.</summary>
    Connect = 6,

    /// <summary>Убрать провод между двумя нодами (то же кодирование, что у <see cref="Connect"/>) либо — если второй узел пуст
    /// (<c>"узел1|"</c>) — ВСЕ провода первой ноды.</summary>
    Disconnect = 7,

    /// <summary>Задать параметр блока (<see cref="Construction.TrySetParameter"/>): <c>cell</c> — любая клетка блока, <c>blockSlug</c> — <c>"id=значение"</c>
    /// (<see cref="NetEditOps.EncodeParameter"/>).</summary>
    SetParameter = 8,
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

    /// <summary>Кодирует пару нод для <see cref="NetEditKind.Connect"/>/<see cref="NetEditKind.Disconnect"/>: <c>"узел1|узел2"</c> (второй может быть пуст).</summary>
    public static string EncodeNodes(string nodeA, string nodeB) => nodeA + "|" + nodeB;

    private static (string A, string B) DecodeNodes(string text)
    {
        int bar = text.IndexOf('|');
        return bar < 0 ? (text, "") : (text[..bar], text[(bar + 1)..]);
    }

    /// <summary>Кодирует параметр для <see cref="NetEditKind.SetParameter"/>: <c>"id=значение"</c> (id параметра не содержит '=').</summary>
    public static string EncodeParameter(string parameterId, string value) => parameterId + "=" + value;

    private static (string Id, string Value) DecodeParameter(string text)
    {
        int eq = text.IndexOf('=');
        return eq < 0 ? (text, "") : (text[..eq], text[(eq + 1)..]);
    }
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

            case NetEditKind.Connect:
            {
                var (nodeA, nodeB) = DecodeNodes(blockSlug);
                var first = construction.GetOwner(cell);
                var second = construction.GetOwner(size);
                return first != null && second != null && construction.TryConnect(first.InstanceId, nodeA, second.InstanceId, nodeB, out _);
            }

            case NetEditKind.Disconnect:
            {
                var (nodeA, nodeB) = DecodeNodes(blockSlug);
                var first = construction.GetOwner(cell);
                if (first == null) return false;
                if (nodeB.Length == 0) return construction.DisconnectNode(first.InstanceId, nodeA) > 0;

                var second = construction.GetOwner(size);
                if (second == null) return false;
                foreach (var wire in construction.Wires)
                {
                    bool forward = wire.FromInstance == first.InstanceId && wire.FromNode == nodeA && wire.ToInstance == second.InstanceId && wire.ToNode == nodeB;
                    bool backward = wire.FromInstance == second.InstanceId && wire.FromNode == nodeB && wire.ToInstance == first.InstanceId && wire.ToNode == nodeA;
                    if (forward || backward) return construction.Disconnect(wire);
                }

                return false;
            }

            case NetEditKind.SetParameter:
            {
                var (parameterId, value) = DecodeParameter(blockSlug);
                var target = construction.GetOwner(cell);
                return target != null && construction.TrySetParameter(target, parameterId, value);
            }

            default:
                return false;
        }
    }
}
