using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Blocks;

/// <summary>
/// Компонент функционального блока (мотор, труба, вал, батарея, бак, кнопка и т.п.) — в отличие от
/// <see cref="BuildingBlockComponent"/>, занимает фиксированную (не резинящуюся инструментом Resize) область клеток
/// <see cref="Footprint"/> и имеет набор ресурсных портов (<see cref="Ports"/>) и логических нод (<see cref="Nodes"/>).
/// Взаимоисключающий с <see cref="BuildingBlockComponent"/> на одном <see cref="BlockDefinition"/>.
/// <para/>
/// <b>Новый формат</b> (экспериментальный редактор блоков, <c>--blockeditor</c>): модель (<see cref="ScenePath"/>) рисуется с ЯВНЫМ
/// масштабом по осям (<see cref="ModelScale"/>, по умолчанию <see cref="BlockModelLayout.DefaultScale"/>) и якорем
/// (<see cref="Anchor"/>) — автоподгонки по bbox нет; <see cref="Footprint"/>/<see cref="FootprintMin"/> — клетки, в которые попал
/// bbox модели (считает и записывает редактор, см. <see cref="BlockModelLayout.ComputeFootprint"/>); коллизия — список КЛЕТОК
/// (<see cref="CollisionCells"/>), а не боксов в метрах: при загрузке они сливаются в минимальный набор боксов
/// (<see cref="CollisionBoxes"/>, см. <see cref="Blocks.CollisionCells.Merge"/>). Все индексы клеток (коллизия, ноды, ячейки граней
/// портов) — в рамке блока: клетка (0,0,0) — корневая, индексы могут быть отрицательными (см. <see cref="BlockModelLayout"/>).
/// <para/>
/// Без модели (<see cref="ScenePath"/> пуст) блок рисуется обычным цветным кубом размером <see cref="Footprint"/> (тот же путь
/// <see cref="ChunkMesher"/>, что и у <see cref="BlockShape.Cube"/>) — это плейсхолдер. С моделью рисует
/// <see cref="Editor.FunctionalBlockView"/> (маска покрытия куба отключается, см. <see cref="Construction"/>).
/// <para/>
/// <see cref="Capacity"/> — для блоков-хранилищ (батарея/бак), максимум запасённого ресурса; 0 у блоков, которые ничего не хранят.
/// </summary>
/// <remarks>
/// JSON-параметры:
/// <c>{ "scene": "res://meshes/x.glb", "scale": [0.125,0.125,0.125], "anchor": [0,0,0], "footprint": [2,1,1], "footprintMin": [0,0,0],
///   "behavior": "Button", "params": { "mode": "toggle" }, "capacity": 100,
///   "collision": [[0,0,0],[1,0,0]],
///   "ports": [ { "id": "shaft_out", "resource": "Torque", "direction": "Out", "face": "PosY", "position": [0,0] } ],
///   "nodes": [ { "id": "power_in", "type": "Electricity", "direction": "In", "position": [0,0,0] } ] }</c>.
/// Все ключи опциональны (<c>footprint</c> по умолчанию 1×1×1, <c>footprintMin</c> — 0,0,0). <c>ports</c> — только ФИЗИЧЕСКИЕ
/// (вал/труба, <see cref="ResourceType"/>), <c>nodes</c> — логические ноды (электричество/булево/число, <see cref="LogicNode"/>).
/// Ключи прежнего формата (<c>modelScale</c>, <c>modelOffset</c>, коллизия боксами в метрах) НЕ поддерживаются: блок с ними не
/// загрузится, с понятной ошибкой — откройте его в редакторе блоков и сохраните заново.
/// </remarks>
public sealed class FunctionalBlockComponent : BlockComponent
{
    public const string ComponentType = "FunctionalBlock";

    /// <summary>Размер занятой области в клетках (≥ 1 по каждой оси) — клетки, в которые попал bbox модели.</summary>
    public Vector3I Footprint { get; private set; } = Vector3I.One;

    /// <summary>Минимальная клетка footprint'а в рамке блока (корневая клетка — (0,0,0); может быть отрицательной).</summary>
    public Vector3I FootprintMin { get; private set; } = Vector3I.Zero;

    /// <summary>Строковый ключ поведения блока (например, "Button", "ElectricMotor") — по нему рантайм находит
    /// реализацию (<see cref="Runtime.BlockBehaviorRegistry"/>). Ключ без зарегистрированной реализации (пока все,
    /// кроме "Button") просто хранится и ни на что не влияет.</summary>
    public string Behavior { get; private set; } = "";

    /// <summary>Максимальная ёмкость хранилища (батарея/бак); 0 — блок не хранит ресурс.</summary>
    public float Capacity { get; private set; }

    /// <summary>Путь к glTF-сцене (<c>res://meshes/*.glb</c>) — настоящая модель блока вместо куба-плейсхолдера.
    /// null/пусто — блок рисуется цветным кубом.</summary>
    public string? ScenePath { get; private set; }

    /// <summary>Масштаб модели по осям (<c>"scale"</c>) — по умолчанию <see cref="BlockModelLayout.DefaultScaleVector"/>
    /// (2 м в Blender = 1 клетка). Автоподгонки по bbox нет.</summary>
    public Vector3 ModelScale { get; private set; } = BlockModelLayout.DefaultScaleVector;

    /// <summary>Якорь модели (<c>"anchor"</c>) — доли bbox (каждая из 0 / 0.5 / 1), точка которых встаёт в клетку (0,0,0),
    /// см. <see cref="BlockModelLayout"/>. (0,0,0) по умолчанию — нижний задний левый угол bbox в точке (0,0,0).</summary>
    public Vector3 Anchor { get; private set; } = Vector3.Zero;

    /// <summary>Физические порты — ТОЛЬКО вал и труба (<see cref="ResourceType"/>); электричество и логика — в <see cref="Nodes"/>.</summary>
    public IReadOnlyList<ResourcePort> Ports { get; private set; } = Array.Empty<ResourcePort>();

    /// <summary>
    /// Логические ноды блока (электричество/булево/число, см. <see cref="LogicNode"/>/<see cref="NodeType"/>) — JSON
    /// <c>"nodes"</c>. Нод может быть несколько; в одной клетке могут сидеть только ноды РАЗНЫХ типов
    /// (<see cref="FindNodeConflict"/> — нарушение бросает при загрузке).
    /// </summary>
    public IReadOnlyList<LogicNode> Nodes { get; private set; } = Array.Empty<LogicNode>();

    /// <summary>
    /// Параметры поведения (<see cref="Behavior"/>) — произвольный JSON-объект <c>"params"</c> (например, у кнопки
    /// <c>{ "mode": "toggle" }</c>). Что в нём значит, решает само поведение (см. <see cref="Runtime.IBlockBehavior"/>) —
    /// компонент только хранит. Значения — клоны <see cref="JsonElement"/>, читать удобнее через <see cref="GetParam"/>.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> BehaviorParams { get; private set; } = new Dictionary<string, JsonElement>();

    /// <summary>Строковое значение параметра поведения <paramref name="key"/> — строка как есть, любое другое значение
    /// (число/bool) — его JSON-текст; <paramref name="fallback"/>, если параметра нет.</summary>
    public string GetParam(string key, string fallback = "") =>
        BehaviorParams.TryGetValue(key, out var value)
            ? (value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.GetRawText())
            : fallback;

    /// <summary>Клетки коллизии (рамка блока, без повторов, канонический порядок) — ИСТОЧНИК ПРАВДЫ коллизии, как в XML.
    /// Пусто — коллизии у блока нет вообще (никакого автоматического бокса на весь footprint).</summary>
    public IReadOnlyList<Vector3I> CollisionCells { get; private set; } = Array.Empty<Vector3I>();

    /// <summary>Боксы коллизии — <see cref="CollisionCells"/>, слитые в минимальный набор (см. <see cref="Blocks.CollisionCells.Merge"/>),
    /// считаются один раз при загрузке. Их и использует физика (<c>World.VehicleSpawner</c>).</summary>
    public IReadOnlyList<CellBox> CollisionBoxes { get; private set; } = Array.Empty<CellBox>();

    public override void LoadFromJson(JsonElement json)
    {
        if (json.TryGetProperty("modelScale", out _) || json.TryGetProperty("modelOffset", out _))
        {
            throw new InvalidOperationException("legacy keys \"modelScale\"/\"modelOffset\" are not supported any more - open the block in the block editor and save it again (new keys: \"scale\", \"anchor\")");
        }

        if (json.TryGetProperty("footprint", out var footprint)) Footprint = ReadVector(footprint, Footprint);
        if (Footprint.X < 1 || Footprint.Y < 1 || Footprint.Z < 1)
        {
            throw new InvalidOperationException($"\"footprint\" must be at least 1 in every axis, got {Footprint}");
        }

        if (json.TryGetProperty("footprintMin", out var footprintMin)) FootprintMin = ReadVector(footprintMin, FootprintMin);
        if (json.TryGetProperty("behavior", out var behavior)) Behavior = behavior.GetString() ?? "";
        if (json.TryGetProperty("capacity", out var capacity)) Capacity = capacity.GetSingle();
        if (json.TryGetProperty("scene", out var scene)) ScenePath = scene.GetString();

        if (json.TryGetProperty("scale", out var scale))
        {
            ModelScale = ReadVector3(scale, ModelScale);
            if (ModelScale.X <= 0 || ModelScale.Y <= 0 || ModelScale.Z <= 0)
            {
                throw new InvalidOperationException($"\"scale\" must be positive in every axis, got {ModelScale}");
            }
        }

        if (json.TryGetProperty("anchor", out var anchor))
        {
            var raw = ReadVector3(anchor, Anchor);
            Anchor = new Vector3(Math.Clamp(raw.X, 0, 1), Math.Clamp(raw.Y, 0, 1), Math.Clamp(raw.Z, 0, 1));
        }

        if (json.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
        {
            Ports = ports.EnumerateArray().Select(ReadPort).ToArray();
        }

        if (json.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            Nodes = ParseNodes(nodes);
            string? conflict = FindNodeConflict(Nodes);
            if (conflict != null) throw new InvalidOperationException(conflict);
        }

        if (json.TryGetProperty("params", out var behaviorParams) && behaviorParams.ValueKind == JsonValueKind.Object)
        {
            BehaviorParams = behaviorParams.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        }

        if (json.TryGetProperty("collision", out var collision) && collision.ValueKind == JsonValueKind.Array)
        {
            CollisionCells = Blocks.CollisionCells.Normalize(ParseCollisionCells(collision));
            CollisionBoxes = Blocks.CollisionCells.Merge(CollisionCells);
        }
    }

    /// <summary>Разбирает <c>"collision"</c>: массив клеток <c>[x, y, z]</c> (целые индексы). Запись прежнего формата (объекты
    /// <c>{ "position", "size" }</c> в метрах) — понятная ошибка. Публичный, чтобы тем же разбором пользовался редактор блоков.</summary>
    public static IReadOnlyList<Vector3I> ParseCollisionCells(JsonElement array)
    {
        var cells = new List<Vector3I>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 3)
            {
                throw new InvalidOperationException("\"collision\" must be a list of cells [x, y, z] (integer indices) - legacy boxes in meters are not supported, re-save the block in the block editor");
            }

            var items = item.EnumerateArray().ToArray();
            cells.Add(new Vector3I(items[0].GetInt32(), items[1].GetInt32(), items[2].GetInt32()));
        }

        return cells;
    }

    /// <summary>Разбирает JSON-массив <c>"nodes"</c> (<c>{ "id", "type": "Electricity"|"Boolean"|"Number", "direction":
    /// "In"|"Out", "position": [x, y, z] }</c>, позиция — клетка в рамке блока, по умолчанию [0,0,0]) — публичный, чтобы тем же
    /// разбором пользовался и редактор блоков. Бросает исключение на неверное значение enum/отсутствующее
    /// обязательное поле/позицию не из трёх целых. Правило "один тип на клетку" проверяет <see cref="FindNodeConflict"/>.</summary>
    public static IReadOnlyList<LogicNode> ParseNodes(JsonElement array) =>
        array.EnumerateArray().Select(ReadNode).ToArray();

    private static LogicNode ReadNode(JsonElement json) => new()
    {
        Id = json.GetProperty("id").GetString() ?? "",
        Type = Enum.Parse<NodeType>(json.GetProperty("type").GetString()!, ignoreCase: true),
        Direction = Enum.Parse<PortDirection>(json.GetProperty("direction").GetString()!, ignoreCase: true),
        Cell = ReadNodeCell(json),
    };

    private static Vector3I ReadNodeCell(JsonElement json)
    {
        if (!json.TryGetProperty("position", out var position)) return Vector3I.Zero;
        if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() != 3)
        {
            throw new InvalidOperationException("node \"position\" must be [x, y, z] - the block cell the node sits in the center of");
        }

        var items = position.EnumerateArray().ToArray();
        return new Vector3I(items[0].GetInt32(), items[1].GetInt32(), items[2].GetInt32());
    }

    /// <summary>
    /// Правило размещения: в одной клетке не могут сидеть две ноды ОДНОГО типа (<see cref="LogicNode.Type"/>) — независимо
    /// от направления (вход и выход Boolean в одной клетке — тоже конфликт); ноды РАЗНЫХ типов в одной клетке допустимы.
    /// Сравниваются сохранённые клетки как есть (не зажатые к footprint'у). Возвращает описание первого конфликта или null.
    /// </summary>
    public static string? FindNodeConflict(IReadOnlyList<LogicNode> nodes)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                if (nodes[i].Type == nodes[j].Type && nodes[i].Cell == nodes[j].Cell)
                {
                    return $"nodes '{nodes[i].Id}' and '{nodes[j].Id}' are both {nodes[i].Type} in cell {nodes[i].Cell} - " +
                           "only nodes of different types may share a cell";
                }
            }
        }

        return null;
    }

    /// <summary>Тип физического ресурса порта по имени. <c>Electricity</c> больше не ресурс — отдельная понятная ошибка
    /// (подсказка перенести в <c>"nodes"</c>) вместо безликого "значение не найдено".</summary>
    public static ResourceType ParseResourceType(string name)
    {
        if (string.Equals(name, "Electricity", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Electricity is not a physical resource any more - declare it as a logic node in \"nodes\" (type \"Electricity\")");
        }

        return Enum.Parse<ResourceType>(name, ignoreCase: true);
    }

    private static BlockFace ReadFace(JsonElement json) =>
        json.TryGetProperty("face", out var faceJson) && faceJson.ValueKind == JsonValueKind.String
            ? Enum.Parse<BlockFace>(faceJson.GetString()!, ignoreCase: true)
            : BlockFace.PosZ;

    private static Vector2I ReadFaceCell(JsonElement json)
    {
        if (json.TryGetProperty("position", out var positionJson) && positionJson.ValueKind == JsonValueKind.Array
            && positionJson.GetArrayLength() == 2)
        {
            var items = positionJson.EnumerateArray().ToArray();
            return new Vector2I(items[0].GetInt32(), items[1].GetInt32());
        }

        return Vector2I.Zero;
    }

    private static Vector3 ReadVector3(JsonElement array, Vector3 fallback = default)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 3) return fallback;
        var items = array.EnumerateArray().ToArray();
        return new Vector3(items[0].GetSingle(), items[1].GetSingle(), items[2].GetSingle());
    }

    private static ResourcePort ReadPort(JsonElement json)
    {
        string id = json.GetProperty("id").GetString() ?? "";
        var resource = ParseResourceType(json.GetProperty("resource").GetString()!);
        var direction = Enum.Parse<PortDirection>(json.GetProperty("direction").GetString()!, ignoreCase: true);

        return new ResourcePort { Id = id, Resource = resource, Direction = direction, Face = ReadFace(json), FaceCell = ReadFaceCell(json) };
    }

    private static Vector3I ReadVector(JsonElement array, Vector3I fallback)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 3) return fallback;
        var items = array.EnumerateArray().ToArray();
        return new Vector3I(items[0].GetInt32(), items[1].GetInt32(), items[2].GetInt32());
    }
}
