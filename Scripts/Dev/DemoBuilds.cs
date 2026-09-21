using Godot;
using SwV2.Core;

namespace SwV2.Dev;

/// <summary>Готовые тестовые постройки для скриншотов и замеров (запуск: <c>-- --demo=house|stress</c>).</summary>
public static class DemoBuilds
{
    private const ushort Steel = 1, Aluminium = 2, Copper = 4, Wood = 6, Concrete = 7, Brick = 8, Plastic = 9;

    public static void Build(string name, VoxelGrid grid)
    {
        switch (name)
        {
            case "house":
                House(grid);
                break;
            case "stress":
                Fill(grid, new Vector3I(-50, 0, -50), new Vector3I(49, 7, 49), Steel);
                break;
            default:
                GD.PrintErr($"Unknown demo '{name}'. Available: house, stress");
                break;
        }
    }

    public static void Fill(VoxelGrid grid, Vector3I min, Vector3I max, ushort id, Color? color = null)
    {
        uint packed = CellColor.Pack(color ?? BlockRegistry.Get(id).DefaultColor);
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
        Fill(g, new Vector3I(-12, 0, -8), new Vector3I(11, 0, 7), Concrete);            // фундамент
        Fill(g, new Vector3I(-12, 1, -8), new Vector3I(11, 8, -8), Brick);              // задняя стена
        Fill(g, new Vector3I(-12, 1, 7), new Vector3I(11, 8, 7), Brick);                // передняя стена
        Fill(g, new Vector3I(-12, 1, -8), new Vector3I(-12, 8, 7), Brick);              // левая стена
        Fill(g, new Vector3I(11, 1, -8), new Vector3I(11, 8, 7), Brick);                // правая стена

        foreach (int x0 in new[] { -8, -2, 4 })                                          // окна
        {
            Carve(g, new Vector3I(x0, 3, -8), new Vector3I(x0 + 2, 5, -8));
            Carve(g, new Vector3I(x0, 3, 7), new Vector3I(x0 + 2, 5, 7));
        }

        Carve(g, new Vector3I(0, 1, 7), new Vector3I(2, 6, 7));                          // дверной проём

        foreach (int x in new[] { -12, 11 })                                             // угловые стальные колонны
        foreach (int z in new[] { -8, 7 })
        {
            Fill(g, new Vector3I(x, 1, z), new Vector3I(x, 8, z), Steel);
        }

        Fill(g, new Vector3I(-13, 9, -9), new Vector3I(12, 9, 8), Wood);                // крыша с навесом
        Paint(g, new Vector3I(-13, 1, -9), new Vector3I(12, 2, 8), Color.FromHtml("#e8862c")); // цветной пояс
        Fill(g, new Vector3I(5, 10, -6), new Vector3I(6, 15, -5), Brick);               // труба

        for (int i = 0; i < 8; i++)                                                      // лестница справа
        {
            Fill(g, new Vector3I(13 + i, 0, -2), new Vector3I(13 + i, i, 2), i % 2 == 0 ? Aluminium : Plastic);
        }

        Fill(g, new Vector3I(-6, 1, -4), new Vector3I(-4, 3, -2), Copper);              // ящик внутри
    }
}
