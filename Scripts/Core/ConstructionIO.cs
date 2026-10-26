using System.Collections.Generic;
using System.Linq;
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

        /// <summary>Значения настраиваемых параметров, отличающиеся от умолчания (см. <see cref="BlockInstance.Parameters"/>). Не обязательно.</summary>
        [JsonPropertyName("params")] public Dictionary<string, string>? Parameters { get; set; }
    }

    /// <summary>Провод между нодами: блоки — индексы в списке <c>blocks</c> (идентификаторы экземпляров не переживают загрузку).</summary>
    private sealed class WireEntry
    {
        [JsonPropertyName("from")] public int From { get; set; }
        [JsonPropertyName("fromNode")] public string FromNode { get; set; } = "";
        [JsonPropertyName("to")] public int To { get; set; }
        [JsonPropertyName("toNode")] public string ToNode { get; set; } = "";
    }

    /// <summary>
    /// Формат файла постройки (опциональные ключи — <c>rotation</c>, <c>mirror</c>, блок <c>params</c> с изменёнными параметрами и список
    /// <c>wires</c> с проводами между нодами; без них файл читается как раньше):
    /// <code>
    /// {
    ///   "version": 1,
    ///   "name": "My Boat",
    ///   "description": "A small fishing boat",
    ///   "createdUtc": "2026-09-29T12:00:00.0000000Z",
    ///   "modifiedUtc": "2026-09-29T12:05:00.0000000Z",
    ///   "blocks": [
    ///     { "id": "wedge", "origin": [0, 0, 0], "size": [1, 1, 1], "color": "#808890", "rotation": [0, 1, 0], "mirror": [1, 0, 0] }
    ///   ]
    /// }
    /// </code>
    /// <c>id</c> — слаг блока (<see cref="BlockDefinition.Slug"/>), не числовой рантайм-id (он не стабилен между запусками).
    /// <c>rotation</c>/<c>mirror</c> опциональны (по умолчанию [0,0,0]). <c>name</c>/<c>description</c>/
    /// <c>createdUtc</c>/<c>modifiedUtc</c> — метаданные именованного сохранения (см. <see cref="ConstructionStorage"/>),
    /// тоже опциональны: отсутствуют у файлов, сохранённых через голый <see cref="Serialize(Construction)"/>
    /// (в частности — снэпшоты <c>UndoHistory</c>, которым метаданные не нужны и не должны на них влиять).
    /// </summary>
    private sealed class Document
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("createdUtc")] public string? CreatedUtc { get; set; }
        [JsonPropertyName("modifiedUtc")] public string? ModifiedUtc { get; set; }
        [JsonPropertyName("blocks")] public List<BlockEntry> Blocks { get; set; } = new();
        [JsonPropertyName("wires")] public List<WireEntry>? Wires { get; set; }
    }

    /// <summary>Метаданные именованного сохранения (см. <see cref="ReadMetadata"/>) — без блоков, дёшево читать для
    /// списка построек на верстаке (см. <see cref="ConstructionStorage.List"/>).</summary>
    public readonly record struct SaveMetadata(string? Name, string? Description, string? CreatedUtc, string? ModifiedUtc);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Сериализация без метаданных — блоки только. Используется там, где имя/описание/даты не нужны и не
    /// должны запоминаться (в первую очередь — снэпшоты <c>UndoHistory</c>); для именованных сохранений на
    /// диск см. перегрузку с метаданными и/или <see cref="ConstructionStorage"/>.</summary>
    public static string Serialize(Construction construction) => Serialize(construction, null, null, null, null);

    public static string Serialize(Construction construction, string? name, string? description, string? createdUtc, string? modifiedUtc)
    {
        var document = new Document { Name = name, Description = description, CreatedUtc = createdUtc, ModifiedUtc = modifiedUtc };
        var indexOf = new Dictionary<int, int>();
        foreach (var instance in construction.Instances)
        {
            indexOf[instance.InstanceId] = document.Blocks.Count;
            document.Blocks.Add(new BlockEntry
            {
                Parameters = instance.Parameters is { Count: > 0 } ? new Dictionary<string, string>(instance.Parameters) : null,
                Id = instance.BlockSlug,
                Origin = new[] { instance.Origin.X, instance.Origin.Y, instance.Origin.Z },
                Size = new[] { instance.Size.X, instance.Size.Y, instance.Size.Z },
                Color = "#" + CellColor.Unpack(instance.Color).ToHtml(false),
                Rotation = new[] { instance.RotationSteps.X, instance.RotationSteps.Y, instance.RotationSteps.Z },
                Mirror = new[] { instance.Mirror.X, instance.Mirror.Y, instance.Mirror.Z },
            });
        }

        if (construction.Wires.Count > 0)
        {
            document.Wires = construction.Wires
                .Where(w => indexOf.ContainsKey(w.FromInstance) && indexOf.ContainsKey(w.ToInstance))
                .Select(w => new WireEntry { From = indexOf[w.FromInstance], FromNode = w.FromNode, To = indexOf[w.ToInstance], ToNode = w.ToNode })
                .ToList();
        }

        return JsonSerializer.Serialize(document, Options);
    }

    /// <summary>Читает только метаданные (имя/описание/даты), не трогая <see cref="Construction"/> — не нужно
    /// разбирать/размещать блоки только чтобы показать файл в списке построек на верстаке.</summary>
    public static SaveMetadata ReadMetadata(string json)
    {
        var document = JsonSerializer.Deserialize<Document>(json, Options) ?? new Document();
        return new SaveMetadata(document.Name, document.Description, document.CreatedUtc, document.ModifiedUtc);
    }

    /// <summary>Полностью заменяет содержимое постройки данными из JSON. Неизвестные блоки/некорректные записи пропускаются.</summary>
    public static (int Loaded, int Skipped) Deserialize(Construction construction, string json, BlockCatalog catalog)
    {
        construction.Clear();
        var document = JsonSerializer.Deserialize<Document>(json, Options) ?? new Document();

        int loaded = 0, skipped = 0;
        var placed = new BlockInstance?[document.Blocks.Count]; // индекс в файле -> экземпляр (null - пропущенный блок; провода к нему отбрасываются)
        for (int entryIndex = 0; entryIndex < document.Blocks.Count; entryIndex++)
        {
            var entry = document.Blocks[entryIndex];
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

            var instance = construction.PlaceBlock(origin, size, definition, color, rotation, mirror);
            if (instance == null) { skipped++; continue; }

            // Параметры проходят ту же нормализацию, что и ввод игрока: неизвестный параметр/мусорное значение молча отбрасываются.
            if (entry.Parameters is { Count: > 0 })
            {
                foreach (var (parameterId, text) in entry.Parameters) construction.TrySetParameter(instance, parameterId, text, catalog);
            }

            placed[entryIndex] = instance;
            loaded++;
        }

        bool wiresAdded = false;
        if (document.Wires != null)
        {
            foreach (var wire in document.Wires)
            {
                if (wire.From < 0 || wire.From >= placed.Length || wire.To < 0 || wire.To >= placed.Length) continue;
                if (placed[wire.From] is not { } from || placed[wire.To] is not { } to) continue;
                if (construction.CheckWire(from.InstanceId, wire.FromNode, to.InstanceId, wire.ToNode, out var checkedWire, out _, catalog)
                    && checkedWire.FromInstance == from.InstanceId)
                {
                    wiresAdded |= construction.AddWireUnchecked(checkedWire);
                }
            }
        }

        if (wiresAdded) construction.NotifyChanged();
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
