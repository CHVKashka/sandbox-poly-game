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
/// <c>{ "footprint": [1,1,1], "behavior": "ElectricMotor", "capacity": 100, "scene": "res://meshes/x.glb",
///   "collision": [ { "position": [0,0,0], "size": [0.25,0.25,0.25] } ], "ports": [
///   { "id": "power_in", "resource": "Electricity", "direction": "In", "face": "NegZ", "position": [0,0] } ] }</c>.
/// Все, кроме <c>footprint</c>, опциональны. <c>behavior</c> — строковый ключ будущего поведения (пока только
/// хранится, ни на что не влияет — см. class doc). У порта <c>face</c>/<c>position</c> тоже опциональны (старые,
/// написанные руками до появления этих полей блоки по-прежнему парсятся — см. <see cref="ResourcePort.Face"/>/
/// <see cref="ResourcePort.FaceCell"/> про значения по умолчанию).
/// </remarks>
public sealed class FunctionalBlockComponent : BlockComponent
{
    public const string ComponentType = "FunctionalBlock";

    public Vector3I Footprint { get; private set; } = Vector3I.One;

    /// <summary>Строковый ключ поведения блока (например, "ElectricMotor", "Battery") — зарезервирован на будущее
    /// (см. class doc), сейчас ни на что не влияет.</summary>
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

    public IReadOnlyList<ResourcePort> Ports { get; private set; } = Array.Empty<ResourcePort>();

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

        if (json.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
        {
            Ports = ports.EnumerateArray().Select(ReadPort).ToArray();
        }

        if (json.TryGetProperty("collision", out var collision) && collision.ValueKind == JsonValueKind.Array)
        {
            CollisionBoxes = collision.EnumerateArray().Select(ReadCollisionBox).ToArray();
        }
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
        var resource = Enum.Parse<ResourceType>(json.GetProperty("resource").GetString()!, ignoreCase: true);
        var direction = Enum.Parse<PortDirection>(json.GetProperty("direction").GetString()!, ignoreCase: true);

        var face = json.TryGetProperty("face", out var faceJson) && faceJson.ValueKind == JsonValueKind.String
            ? Enum.Parse<BlockFace>(faceJson.GetString()!, ignoreCase: true)
            : BlockFace.PosZ;

        var faceCell = Vector2I.Zero;
        if (json.TryGetProperty("position", out var positionJson) && positionJson.ValueKind == JsonValueKind.Array
            && positionJson.GetArrayLength() == 2)
        {
            var items = positionJson.EnumerateArray().ToArray();
            faceCell = new Vector2I(items[0].GetInt32(), items[1].GetInt32());
        }

        return new ResourcePort { Id = id, Resource = resource, Direction = direction, Face = face, FaceCell = faceCell };
    }

    private static Vector3I ReadVector(JsonElement array, Vector3I fallback)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 3) return fallback;
        var items = array.EnumerateArray().ToArray();
        return new Vector3I(items[0].GetInt32(), items[1].GetInt32(), items[2].GetInt32());
    }
}
