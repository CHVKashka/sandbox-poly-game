using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace SandboxPolyGame.Blocks;

/// <summary>
/// Компонент функционального блока (мотор, труба, кабель, вал, батарея, бак и т.п.) — в отличие от
/// <see cref="BuildingBlockComponent"/>, занимает фиксированную (не резинящуюся инструментом Resize) область клеток
/// <see cref="Footprint"/> и имеет набор ресурсных портов (<see cref="Ports"/>). Взаимоисключающий с
/// <see cref="BuildingBlockComponent"/> на одном <see cref="BlockDefinition"/> — блок либо резиновая форма, либо
/// функциональный блок, не оба сразу (см. <see cref="BlockCatalog.CreateComponent"/>).
/// <para/>
/// Без своей модели (<see cref="ScenePath"/> пуст — большинство функциональных блоков пока) блок рисуется обычным
/// цветным кубом размером <see cref="Footprint"/> (тот же путь <see cref="Core.ChunkMesher"/>, что и у
/// <see cref="BlockShape.Cube"/> — блок без <see cref="BuildingBlockComponent"/> получает полную маску покрытия по
/// умолчанию, см. <see cref="Core.Construction"/>) — это и есть плейсхолдер. С моделью (<see cref="ScenePath"/>
/// задан — мотор/вал) рисует <see cref="Editor.FunctionalBlockView"/> (маска покрытия куба отключается, см.
/// <see cref="Core.Construction"/>), вписывая реальный bounding box сцены в <see cref="Footprint"/> клеток.
/// <para/>
/// <see cref="Capacity"/> — для блоков-хранилищ (батарея/бак), максимум запасённого ресурса; 0 у блоков, которые
/// ничего не хранят (мотор, труба, кабель, вал). Само текущее запасённое количество, баланс потребления/подачи
/// между соединёнными блоками (сеть ресурсов, передача по трубам/кабелям/валам) — ещё не реализованы, это только
/// data-driven ОПИСАНИЕ возможностей блока (см. «Ресурсы»/«Соединения» в доке выше) — следующий шаг.
/// </summary>
/// <remarks>
/// JSON-параметры:
/// <c>{ "footprint": [1,1,1], "behavior": "ElectricMotor", "params": { "mode": "toggle" }, "capacity": 100, "scene": "res://meshes/x.glb",
///   "collision": [ { "position": [0,0,0], "size": [0.25,0.25,0.25] } ], "ports": [
///   { "id": "shaft_out", "resource": "Torque", "direction": "Out", "face": "PosY", "position": [0,0] } ],
///   "nodes": [ { "id": "power_in", "type": "Electricity", "direction": "In", "position": [0,0,0] } ] }</c>.
/// <c>ports</c> — только ФИЗИЧЕСКИЕ (вал/труба, <see cref="ResourceType"/>), <c>nodes</c> — логические ноды
/// (электричество/булево/число, <see cref="LogicNode"/>).
/// Все, кроме <c>footprint</c>, опциональны. <c>behavior</c> — строковый ключ будущего поведения (пока только
/// хранится, ни на что не влияет — см. class doc). У порта <c>face</c>/<c>position</c> тоже опциональны (старые,
/// написанные руками до появления этих полей блоки по-прежнему парсятся — см. <see cref="ResourcePort.Face"/>/
/// <see cref="ResourcePort.FaceCell"/> про значения по умолчанию).
/// </remarks>
public sealed class FunctionalBlockComponent : BlockComponent
{
    public const string ComponentType = "FunctionalBlock";

    public Vector3I Footprint { get; private set; } = Vector3I.One;

    /// <summary>Строковый ключ поведения блока (например, "Button", "ElectricMotor") — по нему рантайм находит
    /// реализацию (<see cref="Runtime.BlockBehaviorRegistry"/>). Ключ без зарегистрированной реализации (пока все,
    /// кроме "Button") просто хранится и ни на что не влияет.</summary>
    public string Behavior { get; private set; } = "";

    /// <summary>Максимальная ёмкость хранилища (батарея/бак); 0 — блок не хранит ресурс.</summary>
    public float Capacity { get; private set; }

    /// <summary>
    /// Путь к импортированной glTF-сцене (<c>res://meshes/*.glb</c>) — настоящая модель блока вместо куба-плейсхолдера.
    /// null/пусто (пока нет модели — большинство функциональных блоков) — блок по-прежнему рисуется цветным кубом
    /// (см. <see cref="Core.Construction"/>). Модель нормализуется под размер клетки АВТОМАТИЧЕСКИ по реальному
    /// bounding box геометрии, не по заявленному размеру в Blender (см. <see cref="Editor.FunctionalBlockGeometry"/>
    /// class doc — заявленный и фактический размер экспорта на практике разошлись).
    /// </summary>
    public string? ScenePath { get; private set; }

    /// <summary>
    /// Ручной множитель поверх автоматической равномерной подгонки модели (<see cref="Editor.FunctionalBlockGeometry.ComputeFitTransform"/>) —
    /// (1,1,1) по умолчанию, то есть ничего не меняет (чистая равномерная подгонка, как раньше). Нужен, когда
    /// автоматическая подгонка не годится: например, у модели есть выпирающая деталь, из-за которой "самая тесная
    /// ось" оставляет остальные оси визуально мельче, чем хотелось бы — растянуть их вручную отдельными числами на
    /// каждую ось (см. <c>Dev.BlockPrefabEditor</c>, "Model stretch"), подобрав глазами под границы хитбокса.
    /// Применяется КОМПОНЕНТНО (X/Y/Z независимо), поверх уже посчитанного равномерного масштаба — не замена
    /// автоподгонке, а поправка сверху.
    /// </summary>
    public Vector3 ModelScale { get; private set; } = Vector3.One;

    /// <summary>
    /// Ручной сдвиг модели относительно центра клетки (footprint'а) в МЕТРАХ, в осях НЕповёрнутого блока — (0,0,0) по
    /// умолчанию (модель по центру bounding box'а, как раньше). Нужен, когда геометрия модели асимметрична внутри
    /// своего bounding box'а (например, угловая труба: габарит симметричен, а ось трубы смещена к углу) и после
    /// автоцентровки не совпадает с центрами клетки/портов. Применяется ПОСЛЕ масштаба (метры не зависят от
    /// <see cref="ModelScale"/>) и поворачивается вместе с блоком (см. <see cref="Editor.FunctionalBlockGeometry.ComputeFitTransform"/>);
    /// двигает только визуальную модель, не коллизию и не порты. JSON: <c>"modelOffset": [x, y, z]</c>.
    /// </summary>
    public Vector3 ModelOffset { get; private set; } = Vector3.Zero;

    /// <summary>Физические порты — ТОЛЬКО вал и труба (<see cref="ResourceType"/>); электричество и логика — в <see cref="Nodes"/>.</summary>
    public IReadOnlyList<ResourcePort> Ports { get; private set; } = Array.Empty<ResourcePort>();

    /// <summary>
    /// Логические ноды блока (электричество/булево/число, см. <see cref="LogicNode"/>/<see cref="NodeType"/>) — JSON
    /// <c>"nodes"</c>. Отдельный список от физических <see cref="Ports"/>. Нод может быть несколько; в одной клетке
    /// могут сидеть только ноды РАЗНЫХ типов (<see cref="FindNodeConflict"/> — нарушение бросает при загрузке). Пусто у
    /// блоков без электричества и логики.
    /// </summary>
    public IReadOnlyList<LogicNode> Nodes { get; private set; } = Array.Empty<LogicNode>();

    /// <summary>
    /// Параметры поведения (<see cref="Behavior"/>) — произвольный JSON-объект <c>"params"</c> (например, у кнопки
    /// <c>{ "mode": "toggle" }</c>). Что в нём значит, решает само поведение (см. <see cref="Runtime.IBlockBehavior"/>) —
    /// компонент только хранит. Пусто по умолчанию. Значения — клоны <see cref="JsonElement"/> (живут независимо от
    /// разобранного документа), читать удобнее через <see cref="GetParam"/>.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> BehaviorParams { get; private set; } = new Dictionary<string, JsonElement>();

    /// <summary>Строковое значение параметра поведения <paramref name="key"/> — строка как есть, любое другое значение
    /// (число/bool) — его JSON-текст; <paramref name="fallback"/>, если параметра нет.</summary>
    public string GetParam(string key, string fallback = "") =>
        BehaviorParams.TryGetValue(key, out var value)
            ? (value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.GetRawText())
            : fallback;

    /// <summary>
    /// Боксы коллизии блока в его СОБСТВЕННЫХ локальных координатах (метры, см. <see cref="CollisionBox"/> class
    /// doc) — пусто (по умолчанию) означает, что у блока НЕТ коллизии вообще, никакого автоматического бокса "на
    /// всякий случай" (см. <see cref="World.VehicleSpawner"/> — это отличается от обычного блока без
    /// <see cref="FunctionalBlockComponent"/>, у которого коллизия всегда есть). Заполняется инструментом
    /// <c>Dev.BlockPrefabEditor</c> (<c>--blockeditor</c>) — см. Docs/05-world-and-vehicle-systems.md.
    /// </summary>
    public IReadOnlyList<CollisionBox> CollisionBoxes { get; private set; } = Array.Empty<CollisionBox>();

    public override void LoadFromJson(JsonElement json)
    {
        if (json.TryGetProperty("footprint", out var footprint)) Footprint = ReadVector(footprint, Footprint);
        if (json.TryGetProperty("behavior", out var behavior)) Behavior = behavior.GetString() ?? "";
        if (json.TryGetProperty("capacity", out var capacity)) Capacity = capacity.GetSingle();
        if (json.TryGetProperty("scene", out var scene)) ScenePath = scene.GetString();
        if (json.TryGetProperty("modelScale", out var modelScale)) ModelScale = ReadVector3(modelScale, ModelScale);
        if (json.TryGetProperty("modelOffset", out var modelOffset)) ModelOffset = ReadVector3(modelOffset, ModelOffset);

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
            CollisionBoxes = collision.EnumerateArray().Select(ReadCollisionBox).ToArray();
        }
    }

    /// <summary>Разбирает JSON-массив <c>"nodes"</c> (<c>{ "id", "type": "Electricity"|"Boolean"|"Number", "direction":
    /// "In"|"Out", "position": [x, y, z] }</c>, позиция — клетка блока, по умолчанию [0,0,0]) — публичный, чтобы тем же
    /// разбором пользовался и <c>Dev.BlockPrefabEditor</c>. Бросает исключение на неверное значение enum/отсутствующее
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

    private static CollisionBox ReadCollisionBox(JsonElement json) => new()
    {
        Position = ReadVector3(json.GetProperty("position")),
        Size = ReadVector3(json.GetProperty("size")),
    };

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
