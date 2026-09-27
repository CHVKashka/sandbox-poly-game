using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Dev;

/// <summary>
/// Готовые тестовые постройки для скриншотов и замеров (запуск: <c>-- --demo=house|stress</c>).
/// Пишут напрямую в <see cref="VoxelGrid"/> в обход <see cref="Construction"/> — это массовая заливка клеток
/// (стены, окна), а не размещение отдельных блоков игроком, поэтому экземпляры (см. Construction) им не нужны.
/// Блоков-материалов больше нет (см. каталог <c>blocks/</c>: block/wedge/pyramid/inverse_pyramid) — разнообразие
/// в демо-доме теперь только цветом, тип блока везде один и тот же (`Block`, кубическая форма).
/// </summary>
public static class DemoBuilds
{
    private static readonly ushort Block = BlockCatalog.Instance.Get("block").RuntimeId;

    private static readonly Color Foundation = Color.FromHtml("#8a8f96");
    private static readonly Color Wall = Color.FromHtml("#a5483a");
    private static readonly Color Column = Color.FromHtml("#808890");
    private static readonly Color Roof = Color.FromHtml("#a9773f");
    private static readonly Color Chimney = Color.FromHtml("#7a382e");
    private static readonly Color StairA = Color.FromHtml("#c9d0d8");
    private static readonly Color StairB = Color.FromHtml("#ececec");
    private static readonly Color Crate = Color.FromHtml("#b87333");

    public static void Build(string name, VoxelGrid grid)
    {
        switch (name)
        {
            case "house":
                House(grid);
                break;
            case "stress":
                Fill(grid, new Vector3I(-50, 0, -50), new Vector3I(49, 7, 49), Block, Column);
                break;
            case "shapes":
                break; // см. Shapes(Construction) — этому демо нужен Construction, не просто VoxelGrid
            default:
                GD.PrintErr($"Unknown demo '{name}'. Available: house, stress, shapes");
                break;
        }
    }

    /// <summary>
    /// По одному экземпляру каждой формы в ряд (для визуальной проверки геометрии Wedge/Pyramid/InvertedPyramid —
    /// см. <c>ShapeMeshBuilder</c>), с разными поворотами и размерами, плюс контрольный куб для сравнения.
    /// </summary>
    public static void Shapes(Construction construction)
    {
        var catalog = BlockCatalog.Instance;
        var block = catalog.Get("block");
        var wedge = catalog.Get("wedge");
        var pyramid = catalog.Get("pyramid");
        var inversePyramid = catalog.Get("inverse_pyramid");

        construction.Place(new Vector3I(0, 0, 0), block, Color.FromHtml("#9a9a9a"));
        construction.Place(new Vector3I(4, 0, 0), wedge, Color.FromHtml("#b5924f"));
        construction.Place(new Vector3I(8, 0, 0), wedge, Color.FromHtml("#b5924f"), new Vector3I(0, 1, 0));
        construction.Place(new Vector3I(12, 0, 0), pyramid, Color.FromHtml("#5f86b8"));
        construction.Place(new Vector3I(16, 0, 0), inversePyramid, Color.FromHtml("#8a5fb8"));

        var stretched = construction.Place(new Vector3I(20, 0, 0), wedge, Color.FromHtml("#b5924f"));
        if (stretched != null) construction.TrySetSize(stretched, wedge, new Vector3I(3, 2, 4));

        // Отражённый скос рядом с обычным (см. Scripts/Core/ShapeMeshBuilder.cs) — Wedge симметричен по X (это
        // призма, вытянутая вдоль X), поэтому наглядно отражение видно по Z: рампа поднимается в противоположную
        // сторону, а не просто сдвигается.
        construction.Place(new Vector3I(26, 0, 0), wedge, Color.FromHtml("#b5924f"), mirror: new Vector3I(0, 0, 1));

        // Куб + скос впритык (как крыша на доме) — регрессия на дыру в стыке: Wedge не покрывает свою x=0 (левый
        // треугольный борт) и x=1 (правый) грани целиком, а z=1 у него вообще нулевой высоты (ramp сходит на нет).
        // Раньше ChunkMesher слепо culл'ил грань куба у ЛЮБОГО занятого соседа, включая такие частично закрытые —
        // в стыке образовывалась настоящая дыра (см. ChunkMesher.BuildFaces). Три куба ниже проверяют все три
        // "тонких" стороны скоса разом.
        construction.Place(new Vector3I(32, 0, 0), block, Color.FromHtml("#9a9a9a"));
        construction.Place(new Vector3I(33, 0, 0), wedge, Color.FromHtml("#b5924f")); // куб слева от x=0 борта скоса
        construction.Place(new Vector3I(34, 0, 0), block, Color.FromHtml("#9a9a9a")); // куб справа от x=1 борта скоса
        construction.Place(new Vector3I(33, 0, 1), block, Color.FromHtml("#9a9a9a")); // куб у нулевой-высоты грани z=1
    }

    public static void Fill(VoxelGrid grid, Vector3I min, Vector3I max, ushort id, Color? color = null)
    {
        uint packed = CellColor.Pack(color ?? BlockCatalog.Instance.Get(id).DefaultColor);
        for (int z = min.Z; z <= max.Z; z++)
        for (int y = min.Y; y <= max.Y; y++)
        for (int x = min.X; x <= max.X; x++)
        {
            grid.TrySet(new Vector3I(x, y, z), id, packed);
        }
    }

    public static void Carve(VoxelGrid grid, Vector3I min, Vector3I max)
    {
        for (int z = min.Z; z <= max.Z; z++)
        for (int y = min.Y; y <= max.Y; y++)
        for (int x = min.X; x <= max.X; x++)
        {
            grid.TryRemove(new Vector3I(x, y, z));
        }
    }

    public static void Paint(VoxelGrid grid, Vector3I min, Vector3I max, Color color)
    {
        uint packed = CellColor.Pack(color);
        for (int z = min.Z; z <= max.Z; z++)
        for (int y = min.Y; y <= max.Y; y++)
        for (int x = min.X; x <= max.X; x++)
        {
            grid.TryPaint(new Vector3I(x, y, z), packed);
        }
    }

    private static void House(VoxelGrid g)
    {
        Fill(g, new Vector3I(-12, 0, -8), new Vector3I(11, 0, 7), Block, Foundation);   // фундамент
        Fill(g, new Vector3I(-12, 1, -8), new Vector3I(11, 8, -8), Block, Wall);        // задняя стена
        Fill(g, new Vector3I(-12, 1, 7), new Vector3I(11, 8, 7), Block, Wall);          // передняя стена
        Fill(g, new Vector3I(-12, 1, -8), new Vector3I(-12, 8, 7), Block, Wall);        // левая стена
        Fill(g, new Vector3I(11, 1, -8), new Vector3I(11, 8, 7), Block, Wall);          // правая стена

        foreach (int x0 in new[] { -8, -2, 4 })                                          // окна
        {
            Carve(g, new Vector3I(x0, 3, -8), new Vector3I(x0 + 2, 5, -8));
            Carve(g, new Vector3I(x0, 3, 7), new Vector3I(x0 + 2, 5, 7));
        }

        Carve(g, new Vector3I(0, 1, 7), new Vector3I(2, 6, 7));                          // дверной проём

        foreach (int x in new[] { -12, 11 })                                             // угловые колонны
        foreach (int z in new[] { -8, 7 })
        {
            Fill(g, new Vector3I(x, 1, z), new Vector3I(x, 8, z), Block, Column);
        }

        Fill(g, new Vector3I(-13, 9, -9), new Vector3I(12, 9, 8), Block, Roof);          // крыша с навесом
        Paint(g, new Vector3I(-13, 1, -9), new Vector3I(12, 2, 8), Color.FromHtml("#e8862c")); // цветной пояс
        Fill(g, new Vector3I(5, 10, -6), new Vector3I(6, 15, -5), Block, Chimney);       // труба

        for (int i = 0; i < 8; i++)                                                      // лестница справа
        {
            Fill(g, new Vector3I(13 + i, 0, -2), new Vector3I(13 + i, i, 2), Block, i % 2 == 0 ? StairA : StairB);
        }

        Fill(g, new Vector3I(-6, 1, -4), new Vector3I(-4, 3, -2), Block, Crate);         // ящик внутри
    }
}
