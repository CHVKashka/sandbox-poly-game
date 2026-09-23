using System;
using System.Linq;
using System.Text.Json;
using Godot;

namespace SandboxPolyGame.Blocks;

/// <summary>Форма блока. Сейчас на меш не влияет (все блоки рисуются кубами, см. ROADMAP про формы блоков) — задел на будущее.</summary>
public enum BlockShape
{
    Cube,
    Slope,
    Pyramid,
    InvertedPyramid,
}

/// <summary>
/// Компонент, позволяющий блоку менять размер в редакторе построек. Такими блоками могут быть, например,
/// обычные блоки, скосы, пирамиды и обратные пирамиды — сама форма задаётся <see cref="Shape"/>.
/// Блок с этим компонентом изначально занимает 1x1x1 клетки; инструмент Resize растягивает (или сжимает)
/// его — и вместе с ним область занятых клеток («коллизию» для рейкаста и меширования) — вплоть до <see cref="MaxSize"/>.
/// </summary>
/// <remarks>JSON-параметры: <c>{ "shape": "Cube", "minSize": [1,1,1], "maxSize": [8,8,8] }</c>.</remarks>
public sealed class BuildingBlockComponent : BlockComponent
{
    public const string ComponentType = "BuildingBlock";

    public BlockShape Shape { get; private set; } = BlockShape.Cube;
    public Vector3I MinSize { get; private set; } = new(1, 1, 1);
    public Vector3I MaxSize { get; private set; } = new(8, 8, 8);

    public override void LoadFromJson(JsonElement json)
    {
        if (json.TryGetProperty("shape", out var shape)
            && Enum.TryParse<BlockShape>(shape.GetString(), ignoreCase: true, out var parsedShape))
        {
            Shape = parsedShape;
        }

        if (json.TryGetProperty("minSize", out var min)) MinSize = ReadVector(min, MinSize);
        if (json.TryGetProperty("maxSize", out var max)) MaxSize = ReadVector(max, MaxSize);
    }

    private static Vector3I ReadVector(JsonElement array, Vector3I fallback)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 3) return fallback;
        var items = array.EnumerateArray().ToArray();
        return new Vector3I(items[0].GetInt32(), items[1].GetInt32(), items[2].GetInt32());
    }
}
