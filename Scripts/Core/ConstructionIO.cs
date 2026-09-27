using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Core;

/// <summary>Сохранение/загрузка <see cref="Construction"/> в формате JSON (см. <see cref="Document"/>).</summary>
public static class ConstructionIO
{
    private sealed class BlockEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("origin")] public int[] Origin { get; set; } = new int[3];
        [JsonPropertyName("size")] public int[] Size { get; set; } = { 1, 1, 1 };
        [JsonPropertyName("color")] public string Color { get; set; } = "#ffffff";

        /// <summary>Три четверть-поворота вокруг X/Y/Z (см. <see cref="BlockInstance.RotationSteps"/>). Поле не
        /// обязательно — отсутствует в файлах, сохранённых до появления вращения, тогда считается [0,0,0].</summary>
        [JsonPropertyName("rotation")] public int[]? Rotation { get; set; }

        /// <summary>Отражение по X/Y/Z, 0 или 1 на компоненту (см. <see cref="BlockInstance.Mirror"/>). Поле не
        /// обязательно — отсутствует в файлах, сохранённых до появления отражения, тогда считается [0,0,0].</summary>
        [JsonPropertyName("mirror")] public int[]? Mirror { get; set; }
    }

    /// <summary>
    /// Формат файла постройки:
    /// <code>
    /// {
    ///   "version": 1,
    ///   "blocks": [
    ///     { "id": "wedge", "origin": [0, 0, 0], "size": [1, 1, 1], "color": "#808890", "rotation": [0, 1, 0], "mirror": [1, 0, 0] }
    ///   ]
    /// }
    /// </code>
    /// <c>id</c> — слаг блока (<see cref="BlockDefinition.Slug"/>), не числовой рантайм-id (он не стабилен между запусками).
    /// <c>rotation</c>/<c>mirror</c> опциональны (по умолчанию [0,0,0]).
    /// </summary>
    private sealed class Document
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("blocks")] public List<BlockEntry> Blocks { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Serialize(Construction construction)
    {
        var document = new Document();
        foreach (var instance in construction.Instances)
        {
            document.Blocks.Add(new BlockEntry
            {
                Id = instance.BlockSlug,
                Origin = new[] { instance.Origin.X, instance.Origin.Y, instance.Origin.Z },
                Size = new[] { instance.Size.X, instance.Size.Y, instance.Size.Z },
                Color = "#" + CellColor.Unpack(instance.Color).ToHtml(false),
                Rotation = new[] { instance.RotationSteps.X, instance.RotationSteps.Y, instance.RotationSteps.Z },
                Mirror = new[] { instance.Mirror.X, instance.Mirror.Y, instance.Mirror.Z },
            });
        }

        return JsonSerializer.Serialize(document, Options);
    }

    /// <summary>Полностью заменяет содержимое постройки данными из JSON. Неизвестные блоки/некорректные записи пропускаются.</summary>
    public static (int Loaded, int Skipped) Deserialize(Construction construction, string json, BlockCatalog catalog)
    {
        construction.Clear();
        var document = JsonSerializer.Deserialize<Document>(json, Options) ?? new Document();

        int loaded = 0, skipped = 0;
        foreach (var entry in document.Blocks)
        {
            if (entry.Origin.Length != 3 || entry.Size.Length != 3 || !catalog.TryGetBySlug(entry.Id, out var definition))
            {
                skipped++;
                continue;
            }

            var origin = new Vector3I(entry.Origin[0], entry.Origin[1], entry.Origin[2]);
            var size = new Vector3I(
                Mathf.Max(1, entry.Size[0]), Mathf.Max(1, entry.Size[1]), Mathf.Max(1, entry.Size[2]));

            Color color;
            try { color = Color.FromHtml(entry.Color); }
            catch { color = definition.DefaultColor; }

            var rotation = entry.Rotation is { Length: 3 } r ? new Vector3I(r[0], r[1], r[2]) : Vector3I.Zero;
            var mirror = entry.Mirror is { Length: 3 } m ? new Vector3I(m[0], m[1], m[2]) : Vector3I.Zero;

            if (construction.PlaceBlock(origin, size, definition, color, rotation, mirror) == null) { skipped++; continue; }
            loaded++;
        }

        return (loaded, skipped);
    }

    public static Error SaveToFile(Construction construction, string path)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file == null) return FileAccess.GetOpenError();

        file.StoreString(Serialize(construction));
        return Error.Ok;
    }

    public static (Error Error, int Loaded, int Skipped) LoadFromFile(Construction construction, string path, BlockCatalog catalog)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file == null) return (FileAccess.GetOpenError(), 0, 0);

        var (loaded, skipped) = Deserialize(construction, file.GetAsText(), catalog);
        return (Error.Ok, loaded, skipped);
    }
}
