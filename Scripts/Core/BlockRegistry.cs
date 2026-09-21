using System.Collections.Generic;
using Godot;

namespace SwV2.Core;

/// <summary>Описание типа блока. Id 0 зарезервирован под «пустую клетку».</summary>
public sealed record BlockDefinition(ushort Id, string Name, Color DefaultColor);

/// <summary>Реестр всех доступных блоков (список для окна по Tab).</summary>
public static class BlockRegistry
{
    public const ushort None = 0;

    private static readonly BlockDefinition[] Definitions =
    {
        new(1, "Steel", Color.FromHtml("#808890")),
        new(2, "Aluminium", Color.FromHtml("#c9d0d8")),
        new(3, "Titanium", Color.FromHtml("#5f6b7a")),
        new(4, "Copper", Color.FromHtml("#b87333")),
        new(5, "Brass", Color.FromHtml("#c9a227")),
        new(6, "Wood", Color.FromHtml("#a9773f")),
        new(7, "Concrete", Color.FromHtml("#9a9a94")),
        new(8, "Brick", Color.FromHtml("#a5483a")),
        new(9, "Plastic", Color.FromHtml("#ececec")),
        new(10, "Rubber", Color.FromHtml("#2a2a2c")),
        new(11, "Carbon Fiber", Color.FromHtml("#1c2230")),
        new(12, "Camo", Color.FromHtml("#4b5d3a")),
    };

    public static IReadOnlyList<BlockDefinition> All => Definitions;

    public static BlockDefinition Get(ushort id) => Definitions[id - 1];

    public static bool TryGet(ushort id, out BlockDefinition definition)
    {
        if (id >= 1 && id <= Definitions.Length)
        {
            definition = Definitions[id - 1];
            return true;
        }

        definition = null!;
        return false;
    }
}
