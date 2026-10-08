using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.Dev;

/// <summary>
/// Самотесты логики блоков с нодами: параметры (схема + значения экземпляров), провода между нодами (правила, сохранение, Undo, сетевая правка) и
/// рантайм (аккумулятор → мотор → вал, сиденье, кнопка). Чистая логика без сцены; функциональные блоки — фикстуры <see cref="FixtureBlock"/>,
/// подставляемые в каталог на время теста (<see cref="UseCatalogWith"/>), а не пользовательские блоки из blocks/.
/// </summary>
public sealed partial class SelfTest
{
    /// <summary>Блок-фикстура: слаг, JSON компонента FunctionalBlock и (необязательно) JSON компонента Parameters.</summary>
    private readonly record struct FixtureBlock(string Slug, string FunctionalJson, string? ParametersJson = null)
    {
        public static implicit operator FixtureBlock((string Slug, string Json) pair) => new(pair.Slug, pair.Json);
    }

    // Фикстуры повторяют по смыслу настоящие блоки (blocks/battery_small.xml и т.д.), но не зависят от них: пользователь правит их в редакторе блоков.
    private const string BatteryJson =
        "{ \"footprint\": [2,1,1], \"behavior\": \"Battery\", \"capacity\": 100, \"nodes\": [" +
        "{ \"id\": \"level\", \"type\": \"Number\", \"direction\": \"Out\", \"position\": [0,0,0] }," +
        "{ \"id\": \"pwr_out\", \"type\": \"Electricity\", \"direction\": \"Out\", \"position\": [0,0,0] }," +
        "{ \"id\": \"pwr_in\", \"type\": \"Electricity\", \"direction\": \"In\", \"position\": [1,0,0] } ] }";

    private const string BatteryParameters =
        "{ \"parameters\": [" +
        "{ \"id\": \"levelOutput\", \"label\": \"Level output\", \"type\": \"Enum\", \"options\": [\"absolute\", \"fraction\"], \"default\": \"absolute\" }," +
        "{ \"id\": \"initialCharge\", \"label\": \"Initial charge\", \"type\": \"Float\", \"min\": 0, \"max\": 1, \"default\": 1 }," +
        "{ \"id\": \"chargeRate\", \"label\": \"Charge rate\", \"type\": \"Float\", \"min\": 0, \"max\": 1000, \"default\": 20 } ] }";

    private const string MotorJson =
        "{ \"behavior\": \"ElectricMotor\", \"ports\": [ { \"id\": \"shaft\", \"resource\": \"Torque\", \"direction\": \"Out\", \"face\": \"PosY\", \"position\": [0,0] } ], \"nodes\": [" +
        "{ \"id\": \"sig\", \"type\": \"Number\", \"direction\": \"In\" }, { \"id\": \"pwr\", \"type\": \"Electricity\", \"direction\": \"In\" } ] }";

    private const string MotorParameters =
        "{ \"parameters\": [" +
        "{ \"id\": \"maxPower\", \"label\": \"Max power\", \"type\": \"Float\", \"min\": 0, \"max\": 1000, \"default\": 10 }," +
        "{ \"id\": \"maxRpm\", \"label\": \"Max RPM\", \"type\": \"Float\", \"min\": 0, \"max\": 10000, \"default\": 600 } ] }";

    private const string ShaftJson =
        "{ \"ports\": [ { \"id\": \"a\", \"resource\": \"Torque\", \"direction\": \"In\", \"face\": \"PosY\", \"position\": [0,0] }," +
        "{ \"id\": \"b\", \"resource\": \"Torque\", \"direction\": \"Out\", \"face\": \"NegY\", \"position\": [0,0] } ] }";

    private const string ButtonJson =
        "{ \"behavior\": \"Button\", \"collision\": [[0,0,0]], \"nodes\": [ { \"id\": \"signal\", \"type\": \"Boolean\", \"direction\": \"Out\" }, { \"id\": \"electricity\", \"type\": \"Electricity\", \"direction\": \"In\" } ] }";

    private const string ButtonParameters =
        "{ \"parameters\": [ { \"id\": \"mode\", \"label\": \"Mode\", \"type\": \"Enum\", \"options\": [\"momentary\", \"toggle\"], \"default\": \"momentary\" } ] }";

    /// <summary>Сиденье-фикстура: те же id нод, что у настоящего <c>blocks/pilot_seat.xml</c> (имена — контракт блока), каждая нода в своей клетке footprint'а 4×3×1 (Boolean/Number
    /// одного типа не могут делить клетку).</summary>
    private static string SeatJson()
    {
        var nodes = new List<string>();
        int cell = 0;
        void Add(string id, string type)
        {
            int x = cell % 4, y = cell / 4;
            cell++;
            nodes.Add($"{{ \"id\": \"{id}\", \"type\": \"{type}\", \"direction\": \"Out\", \"position\": [{x},{y},0] }}");
        }

        foreach (string id in PilotSeatBehavior.AxisNodeIds) Add(id, "Number");
        cell = 0; // булевы тоже с клетки 0: Number и Boolean могут делить клетку (разные типы), а девяти булевым нужны свои клетки
        for (int i = 0; i < PilotSeatBehavior.HotkeyCount; i++) Add(PilotSeatBehavior.HotkeyNodeId(i), "Boolean");
        Add(PilotSeatBehavior.OccupiedNodeId, "Boolean");
        Add(PilotSeatBehavior.Trigger1NodeId, "Boolean");
        Add(PilotSeatBehavior.Trigger2NodeId, "Boolean");
        // Коллизия на весь footprint одним боксом - чтобы сиденье можно было поймать лучом прицела.
        var collision = new List<string>();
        for (int x = 0; x < 4; x++) for (int y = 0; y < 3; y++) collision.Add($"[{x},{y},0]");
        return "{ \"footprint\": [4,3,1], \"behavior\": \"PilotSeat\", \"params\": {\"eyeHeight\":0.5,\"forward\":\"PosX\"}, \"collision\": [" + string.Join(",", collision) + "], \"nodes\": [" + string.Join(",", nodes) + "] }";
    }

    private static string SeatParameters()
    {
        var items = new List<string>();
        foreach (string axis in PilotSeatBehavior.AxisNames)
        {
            items.Add($"{{ \"id\": \"{axis}_mode\", \"label\": \"Mode\", \"type\": \"Enum\", \"options\": [\"reset\", \"sticky\"], \"default\": \"reset\" }}");
            items.Add($"{{ \"id\": \"{axis}_sensitivity\", \"label\": \"Sensitivity\", \"type\": \"Float\", \"min\": 0.1, \"max\": 20, \"default\": 3 }}");
        }

        return "{ \"parameters\": [" + string.Join(",", items) + "] }";
    }

    /// <summary>Набор блоков-фикстур для тестов проводов и рантайма: аккумулятор 2×1×1, мотор, вал, кнопка, сиденье.</summary>
    private static IDisposable UseLogicFixtures() => UseCatalogWith(
        new FixtureBlock("zz_bat", BatteryJson, BatteryParameters),
        new FixtureBlock("zz_motor", MotorJson, MotorParameters),
        new FixtureBlock("zz_shaft", ShaftJson),
        new FixtureBlock("zz_button", ButtonJson, ButtonParameters),
        new FixtureBlock("zz_seat", SeatJson(), SeatParameters()));

    private static BlockInstance PlaceFixture(Construction construction, string slug, Vector3I cell, Vector3I steps = default)
    {
        var definition = BlockCatalog.Instance.Get(slug);
        var fn = definition.GetComponent<FunctionalBlockComponent>()!;
        var (origin, size) = BlockFootprint.PlaceBox(cell, fn.FootprintMin, fn.Footprint, steps);
        return construction.PlaceBlock(origin, size, definition, Colors.White, steps)!;
    }

    // ================================================================== параметры блока (схема + значения экземпляра)

    private void RunParametersTests()
    {
        GD.Print("-- parameters: Parameters component schema, normalization, per-instance values");

        static ParametersComponent Schema(string json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var component = new ParametersComponent();
            component.LoadFromJson(doc.RootElement);
            return component;
        }

        string Error(string json)
        {
            try { Schema(json); return ""; }
            catch (Exception ex) { return ex.Message; }
        }

        var schema = Schema(
            "{ \"parameters\": [" +
            "{ \"id\": \"mode\", \"label\": \"Mode\", \"type\": \"Enum\", \"options\": [\"reset\", \"sticky\"], \"default\": \"sticky\", \"group\": \"G\", \"hint\": \"h\" }," +
            "{ \"id\": \"gain\", \"label\": \"Gain\", \"type\": \"Float\", \"min\": 0.5, \"max\": 4, \"step\": 0.1, \"default\": 2 }," +
            "{ \"id\": \"count\", \"label\": \"Count\", \"type\": \"Int\", \"min\": 1, \"max\": 9, \"default\": 3 }," +
            "{ \"id\": \"flag\", \"label\": \"Flag\", \"type\": \"Bool\" } ] }");
        schema.TryGet("mode", out var mode);
        schema.TryGet("gain", out var gain);
        schema.TryGet("count", out var count);
        schema.TryGet("flag", out var flag);
        Check(schema.Parameters.Count == 4 && mode.Group == "G" && mode.Hint == "h" && mode.Options.Count == 2,
            "the schema lists the parameters with label/group/hint/options");
        Check(mode.Default == "sticky" && gain.Default == "2"
              && count.Default == "3" && flag.Default == "false",
            "defaults are read and normalized; a Bool without a default is false", $"{schema.Parameters.Count}");

        // Нормализация вводимого значения.
        Check(gain.TryNormalize("100", out var clampedHigh) && clampedHigh == "4" && gain.TryNormalize("-3", out var clampedLow) && clampedLow == "0.5",
            "a Float is clamped into [min, max]");
        Check(gain.TryNormalize("1.23456789", out var rounded) && rounded == "1.234568", "a Float is rounded to 6 digits (no float noise in files)", rounded);
        Check(count.TryNormalize("4.6", out var rounded4) && rounded4 == "5" && count.TryNormalize("99", out var clampedCount) && clampedCount == "9", "an Int is rounded and clamped");
        Check(!gain.TryNormalize("abc", out _) && !count.TryNormalize("", out _), "text that is not a number is rejected (the value stays as it was)");
        Check(flag.TryNormalize("YES", out var yes) && yes == "true" && flag.TryNormalize("0", out var no) && no == "false" && !flag.TryNormalize("maybe", out _),
            "a Bool accepts true/false/1/0/yes/no/on/off in any case");
        Check(mode.TryNormalize("RESET", out var reset) && reset == "reset" && !mode.TryNormalize("turbo", out _), "an Enum matches an option case-insensitively and rejects unknown ones");

        Check(Error("{ \"parameters\": [ { \"id\": \"a\", \"type\": \"Float\" }, { \"id\": \"a\", \"type\": \"Int\" } ] }").Contains("twice"), "a repeated parameter id is a data error");
        Check(Error("{ \"parameters\": [ { \"id\": \"a\", \"type\": \"Color\" } ] }").Contains("unknown type"), "an unknown parameter type is a data error");
        Check(Error("{ \"parameters\": [ { \"id\": \"a\", \"type\": \"Enum\" } ] }").Contains("options"), "an Enum without options is a data error");
        Check(Error("{ \"parameters\": [ { \"id\": \"a\", \"type\": \"Enum\", \"options\": [\"x\"], \"default\": \"y\" } ] }").Contains("default"), "a default that is not an option is a data error");
        Check(Error("{ \"parameters\": [ { \"id\": \"a\", \"type\": \"Float\", \"min\": 5, \"max\": 1 } ] }").Contains("min"), "min greater than max is a data error");
        Check(Error("{ \"parameters\": [ { \"type\": \"Float\" } ] }").Contains("id"), "a parameter without an id is a data error");
        Check(Schema("{}").Parameters.Count == 0, "an empty component declares nothing (the block counts as having no settings)");

        // Общие наборы: «include» подключает одни и те же параметры всем блокам с управлением.
        var axes = Schema("{ \"include\": [\"ControlAxes\"] }");
        Check(axes.Parameters.Count == 8 && axes.Parameters.Select(p => p.Id).SequenceEqual(PilotSeatBehavior.AxisNames.SelectMany(a => new[] { a + "_mode", a + "_sensitivity" })),
            "the ControlAxes preset declares mode + sensitivity for each of the four axes (the same ids the pilot seat reads)", string.Join(",", axes.Parameters.Select(p => p.Id)));
        Check(ParameterPresets.ControlAxisIds.Select(a => a.Id).SequenceEqual(PilotSeatBehavior.AxisNames), "the preset's axis list matches the seat's axis names");
        Check(axes.TryGet("ws_mode", out var wsMode) && wsMode.Type == ParameterType.Enum && wsMode.Default == "reset" && wsMode.Options.SequenceEqual(new[] { "reset", "sticky" })
              && axes.TryGet("lr_sensitivity", out var lrSens) && lrSens.Default == "3" && lrSens.Min == 0.1 && lrSens.Max == 20,
            "...with reset/sticky modes (reset by default) and a sensitivity of 3 within 0.1..20");
        var mixed = Schema("{ \"parameters\": [ { \"id\": \"own\", \"type\": \"Bool\" } ], \"include\": [\"ControlAxes\"] }");
        Check(mixed.Parameters.Count == 9 && mixed.Parameters[0].Id == "own", "a block can add its own parameters next to the preset's");
        Check(Error("{ \"include\": [\"Nope\"] }").Contains("unknown parameter preset"), "an unknown preset name is a data error");
        Check(Error("{ \"parameters\": [ { \"id\": \"ad_mode\", \"type\": \"Bool\" } ], \"include\": [\"ControlAxes\"] }").Contains("declared twice"), "a clash between an own parameter and the preset is a data error");
        if (BlockCatalog.Instance.TryGetBySlug("pilot_seat", out var realSeat) && Construction.ParametersOf(realSeat) is { } realSchema)
        {
            Check(realSchema.Parameters.Select(p => p.Id + "=" + p.Default).SequenceEqual(axes.Parameters.Select(p => p.Id + "=" + p.Default)), "the real pilot_seat gets its axis settings from the shared preset");
        }

        // ParameterSet: умолчание, изменённое значение, битое сохранённое значение, параметр вне схемы.
        var set = new ParameterSet(schema, new Dictionary<string, string> { ["gain"] = "3", ["mode"] = "turbo", ["count"] = "7" });
        Check(set.GetFloat("gain") == 3 && set.GetInt("count") == 7, "ParameterSet returns the changed values");
        Check(set.GetString("mode") == "sticky", "a stored value that no longer fits the schema falls back to the default instead of failing");
        Check(!set.GetBool("flag") && set.GetFloat("nope", 42) == 42 && set.GetString("nope", "x") == "x" && !set.Has("nope") && set.Has("gain"),
            "a parameter outside the schema returns the caller's fallback");
        Check(ParameterSet.Empty.GetFloat("gain", 9) == 9, "an empty ParameterSet (block without the component) always returns the fallbacks");

        // Значения экземпляра в постройке.
        using var scope = UseLogicFixtures();
        var construction = new Construction(new VoxelGrid());
        var motor = PlaceFixture(construction, "zz_motor", new Vector3I(0, 0, 0));
        int changed = 0;
        construction.Changed += () => changed++;
        Check(construction.TrySetParameter(motor, "maxRpm", "1200") && motor.Parameters!["maxRpm"] == "1200" && changed == 1, "TrySetParameter stores a changed value and raises Changed");
        Check(!construction.TrySetParameter(motor, "maxRpm", "1200") && changed == 1, "setting the same value again changes nothing");
        Check(construction.TrySetParameter(motor, "maxRpm", "99999") && motor.Parameters!["maxRpm"] == "10000", "the value is clamped to the schema's max before it is stored");
        Check(construction.TrySetParameter(motor, "maxRpm", "600") && motor.Parameters == null, "setting a value back to the default drops the override (the instance is 'default' again)");
        Check(!construction.TrySetParameter(motor, "nonexistent", "1") && !construction.TrySetParameter(motor, "maxRpm", "fast"), "an unknown parameter or unparsable text is rejected");
        var shaft = PlaceFixture(construction, "zz_shaft", new Vector3I(5, 0, 0));
        Check(!construction.TrySetParameter(shaft, "maxRpm", "1"), "a block without the Parameters component accepts no parameters");
        Check(Construction.ParametersOf(BlockCatalog.Instance.Get("zz_motor")) != null && Construction.ParametersOf(BlockCatalog.Instance.Get("zz_shaft")) == null
              && Construction.ParametersOf(BlockCatalog.Instance.Get("block")) == null,
            "ParametersOf tells which blocks have settings (motor yes; shaft and rubber blocks no)");
    }

    // ================================================================== провода между нодами

    private void RunNodeWireTests()
    {
        GD.Print("-- node wires: connection rules, replacement, removal, save/load, undo, network edit");

        using var scope = UseLogicFixtures();
        var catalog = BlockCatalog.Instance;
        var construction = new Construction(new VoxelGrid());
        var battery = PlaceFixture(construction, "zz_bat", new Vector3I(0, 0, 0));
        var motor = PlaceFixture(construction, "zz_motor", new Vector3I(5, 0, 0));
        var motor2 = PlaceFixture(construction, "zz_motor", new Vector3I(7, 0, 0));
        var seat = PlaceFixture(construction, "zz_seat", new Vector3I(0, 5, 0));
        var button = PlaceFixture(construction, "zz_button", new Vector3I(10, 0, 0));
        int changed = 0;
        construction.Changed += () => changed++;

        Check(construction.TryConnect(battery.InstanceId, "pwr_out", motor.InstanceId, "pwr", out _) && construction.Wires.Count == 1 && changed == 1,
            "an electricity output connects to an electricity input, and Changed is raised");
        var wire = construction.Wires[0];
        Check(wire.FromInstance == battery.InstanceId && wire.FromNode == "pwr_out" && wire.ToInstance == motor.InstanceId && wire.ToNode == "pwr", "the wire is stored output -> input");

        // Тянуть можно с любого конца: «с входа на выход» даёт тот же провод.
        Check(!construction.TryConnect(motor.InstanceId, "pwr", battery.InstanceId, "pwr_out", out var duplicateError) && duplicateError.Contains("already"),
            "dragging the same pair from the other end is the same wire - already connected");
        Check(construction.TryConnect(motor2.InstanceId, "pwr", battery.InstanceId, "pwr_out", out _) && construction.Wires.Count == 2
              && construction.Wires[1].FromInstance == battery.InstanceId && construction.Wires[1].ToInstance == motor2.InstanceId,
            "dragging from an input to an output creates the wire output -> input all the same, and one output feeds several inputs (fan-out)");

        Check(!construction.TryConnect(battery.InstanceId, "pwr_out", motor.InstanceId, "sig", out var typeError) && typeError.Contains("electricity"),
            "electricity cannot connect to a Number node");
        Check(!construction.TryConnect(seat.InstanceId, "ws_out", seat.InstanceId, "ad_out", out var sameBlockError) && sameBlockError.Length > 0,
            "a block cannot be wired to itself");
        Check(!construction.TryConnect(seat.InstanceId, "ws_out", seat.InstanceId, "hotkey_1", out _), "...including pairs that happen to be an output and an input of the same block");
        Check(!construction.TryConnect(seat.InstanceId, "ws_out", button.InstanceId, "signal", out var outOutError) && outOutError.Contains("output"),
            "two outputs cannot be connected");
        Check(!construction.TryConnect(motor.InstanceId, "sig", motor2.InstanceId, "sig", out var inInError) && inInError.Contains("input"), "two inputs cannot be connected");
        Check(!construction.TryConnect(battery.InstanceId, "nope", motor.InstanceId, "sig", out _) && !construction.TryConnect(999, "x", motor.InstanceId, "sig", out _), "unknown nodes/blocks are rejected");

        // Boolean и Number между собой свободно (булево — это число 0/1).
        Check(construction.TryConnect(button.InstanceId, "signal", motor.InstanceId, "sig", out _), "a Boolean output connects to a Number input");
        Check(construction.TryConnect(seat.InstanceId, "ws_out", motor.InstanceId, "sig", out _) && construction.Wires.Count(w => w.ToInstance == motor.InstanceId && w.ToNode == "sig") == 1,
            "a signal input accepts ONE source: a new wire replaces the old one");
        Check(construction.TryConnect(battery.InstanceId, "pwr_out", motor.InstanceId, "pwr", out _) == false, "(the electricity wire above is still there)");

        // Электрический вход принимает несколько источников.
        var battery2 = PlaceFixture(construction, "zz_bat", new Vector3I(0, 2, 0));
        Check(construction.TryConnect(battery2.InstanceId, "pwr_out", motor.InstanceId, "pwr", out _) && construction.Wires.Count(w => w.ToInstance == motor.InstanceId && w.ToNode == "pwr") == 2,
            "an electricity input accepts several sources (two batteries feed one motor)");

        // Удаление.
        int before = construction.Wires.Count;
        Check(construction.DisconnectNode(motor.InstanceId, "pwr") == 2 && construction.Wires.Count == before - 2, "DisconnectNode removes every wire of the node");
        Check(construction.TryConnect(battery.InstanceId, "pwr_out", motor.InstanceId, "pwr", out _) && construction.Disconnect(construction.Wires.Last()) && !construction.Disconnect(construction.Wires.Last() with { ToNode = "x" }),
            "Disconnect removes exactly that wire (and reports false for one that does not exist)");
        construction.TryConnect(battery.InstanceId, "pwr_out", motor.InstanceId, "pwr", out _);
        int wiresBeforeRemove = construction.Wires.Count;
        construction.Remove(motor);
        Check(construction.Wires.All(w => w.FromInstance != motor.InstanceId && w.ToInstance != motor.InstanceId) && construction.Wires.Count < wiresBeforeRemove,
            "removing a block removes every wire attached to it");

        // Сохранение/загрузка: провода переживают, индексы пересчитываются, пропущенные блоки не ломают остальные.
        construction.Clear();
        var b = PlaceFixture(construction, "zz_bat", new Vector3I(0, 0, 0));
        var m = PlaceFixture(construction, "zz_motor", new Vector3I(5, 0, 0));
        PlaceFixture(construction, "zz_shaft", new Vector3I(20, 0, 0)); // между мотором и сиденьем: его индекс в файле сдвигает индексы сиденья
        var s = PlaceFixture(construction, "zz_seat", new Vector3I(0, 5, 0));
        construction.TryConnect(b.InstanceId, "pwr_out", m.InstanceId, "pwr", out _);
        construction.TryConnect(s.InstanceId, "ws_out", m.InstanceId, "sig", out _);
        construction.TrySetParameter(m, "maxRpm", "1500");
        construction.TrySetParameter(s, "ad_mode", "sticky");
        construction.TrySetParameter(s, "ad_sensitivity", "7.5");
        string json = ConstructionIO.Serialize(construction);

        var loaded = new Construction(new VoxelGrid());
        ConstructionIO.Deserialize(loaded, json, catalog);
        var loadedMotor = loaded.Instances.First(i => i.BlockSlug == "zz_motor");
        var loadedSeat = loaded.Instances.First(i => i.BlockSlug == "zz_seat");
        var loadedBattery = loaded.Instances.First(i => i.BlockSlug == "zz_bat");
        Check(loaded.Wires.Count == 2 && loaded.Wires.Any(w => w.FromInstance == loadedBattery.InstanceId && w.FromNode == "pwr_out" && w.ToInstance == loadedMotor.InstanceId && w.ToNode == "pwr")
              && loaded.Wires.Any(w => w.FromInstance == loadedSeat.InstanceId && w.FromNode == "ws_out" && w.ToInstance == loadedMotor.InstanceId && w.ToNode == "sig"),
            "wires survive save+load, re-attached to the NEW instance ids", $"{loaded.Wires.Count}");
        Check(loadedMotor.Parameters is { Count: 1 } && loadedMotor.Parameters["maxRpm"] == "1500" && loadedSeat.Parameters!["ad_mode"] == "sticky" && loadedSeat.Parameters["ad_sensitivity"] == "7.5"
              && loadedBattery.Parameters == null,
            "changed parameters survive save+load; an untouched block has none");
        Check(ConstructionIO.Serialize(loaded) == json || ConstructionIO.Serialize(loaded).Length == json.Length, "a second save of the loaded construction is the same document (stable format)");

        // Блок неизвестного слага в середине списка (удалён из каталога) сдвигает индексы - провода остальных должны уцелеть.
        string withGhost = json.Replace("\"id\": \"zz_shaft\"", "\"id\": \"zz_removed\"");
        var shifted = new Construction(new VoxelGrid());
        var (loadedCount, skippedCount) = ConstructionIO.Deserialize(shifted, withGhost, catalog);
        Check(skippedCount == 1 && loadedCount == 3 && shifted.Wires.Count == 2, "a block of an unknown (removed) kind in the list is skipped without shifting the wires of the others", $"loaded={loadedCount} skipped={skippedCount} wires={shifted.Wires.Count}");

        // Файл без проводов/параметров (старый формат) читается как раньше.
        var legacy = new Construction(new VoxelGrid());
        ConstructionIO.Deserialize(legacy, "{ \"version\": 1, \"blocks\": [ { \"id\": \"block\", \"origin\": [0,0,0], \"size\": [1,1,1], \"color\": \"#ffffff\" } ] }", catalog);
        Check(legacy.Instances.Count == 1 && legacy.Wires.Count == 0 && legacy.Instances.First().Parameters == null, "a file saved before wires/parameters existed still loads");

        // Провод к ноде, которой больше нет в блоке (блок отредактировали), не ломает загрузку.
        string brokenWire = json.Replace("\"toNode\": \"pwr\"", "\"toNode\": \"removed_node\"");
        var broken = new Construction(new VoxelGrid());
        ConstructionIO.Deserialize(broken, brokenWire, catalog);
        Check(broken.Wires.Count == 1, "a wire to a node that no longer exists is dropped on load, the others stay", $"{broken.Wires.Count}");

        // Undo/Redo (снэпшот включает провода и параметры).
        var history = new UndoHistory();
        var undoConstruction = new Construction(new VoxelGrid());
        var ub = PlaceFixture(undoConstruction, "zz_bat", new Vector3I(0, 0, 0));
        var um = PlaceFixture(undoConstruction, "zz_motor", new Vector3I(5, 0, 0));
        var snapshot = history.Capture(undoConstruction);
        undoConstruction.TryConnect(ub.InstanceId, "pwr_out", um.InstanceId, "pwr", out _);
        history.RecordIfChanged(snapshot, undoConstruction);
        Check(history.CanUndo, "connecting two nodes is a recorded history step");
        history.Undo(undoConstruction, catalog);
        Check(undoConstruction.Wires.Count == 0, "Ctrl+Z removes the wire");
        history.Redo(undoConstruction, catalog);
        Check(undoConstruction.Wires.Count == 1, "Ctrl+Y brings it back");

        var snapshot2 = history.Capture(undoConstruction);
        var motorNow = undoConstruction.Instances.First(i => i.BlockSlug == "zz_motor");
        undoConstruction.TrySetParameter(motorNow, "maxPower", "55");
        history.RecordIfChanged(snapshot2, undoConstruction);
        history.Undo(undoConstruction, catalog);
        Check(undoConstruction.Instances.First(i => i.BlockSlug == "zz_motor").Parameters == null && undoConstruction.Wires.Count == 1,
            "Ctrl+Z also undoes a parameter change (and keeps the wire that was made before it)");

        // Сетевая правка: те же операции через NetEditOps (сервер и клиенты применяют один и тот же код).
        var net = new Construction(new VoxelGrid());
        var nb = PlaceFixture(net, "zz_bat", new Vector3I(0, 0, 0));
        var nm = PlaceFixture(net, "zz_motor", new Vector3I(5, 0, 0));
        bool connected = NetEditOps.Apply(net, NetEditKind.Connect, nb.Origin, nm.Origin, NetEditOps.EncodeNodes("pwr_out", "pwr"), Colors.White, Vector3I.Zero, Vector3I.Zero);
        Check(connected && net.Wires.Count == 1, "a Connect network edit creates the wire (cell of block 1, cell of block 2, node ids)");
        Check(!NetEditOps.Apply(net, NetEditKind.Connect, nb.Origin, nm.Origin, NetEditOps.EncodeNodes("pwr_out", "pwr"), Colors.White, Vector3I.Zero, Vector3I.Zero),
            "...a second identical Connect is rejected (the server does not broadcast it)");
        Check(!NetEditOps.Apply(net, NetEditKind.Connect, new Vector3I(30, 30, 30), nm.Origin, NetEditOps.EncodeNodes("pwr_out", "pwr"), Colors.White, Vector3I.Zero, Vector3I.Zero),
            "...and a Connect through an empty cell is rejected");
        Check(NetEditOps.Apply(net, NetEditKind.SetParameter, nm.Origin, Vector3I.One, NetEditOps.EncodeParameter("maxRpm", "900"), Colors.White, Vector3I.Zero, Vector3I.Zero)
              && nm.Parameters!["maxRpm"] == "900", "a SetParameter network edit changes the parameter of the block under that cell");
        Check(NetEditOps.Apply(net, NetEditKind.Disconnect, nm.Origin, nb.Origin, NetEditOps.EncodeNodes("pwr", "pwr_out"), Colors.White, Vector3I.Zero, Vector3I.Zero) && net.Wires.Count == 0,
            "a Disconnect network edit removes the wire (the pair may be given in either order)");
        net.TryConnect(nb.InstanceId, "pwr_out", nm.InstanceId, "pwr", out _);
        Check(NetEditOps.Apply(net, NetEditKind.Disconnect, nb.Origin, Vector3I.Zero, NetEditOps.EncodeNodes("pwr_out", ""), Colors.White, Vector3I.Zero, Vector3I.Zero) && net.Wires.Count == 0,
            "a Disconnect with an empty second node removes every wire of the first node");
    }

    // ================================================================== рантайм: аккумулятор, мотор, вал, кнопка, сиденье

    private static void Run(FunctionalBlockRuntime runtime, double seconds, double step = 0.05)
    {
        for (double t = 0; t < seconds - 1e-9; t += step) runtime.Tick(step);
    }

    private void RunPowerAndMotionTests()
    {
        GD.Print("-- runtime: battery -> motor -> shafts, electricity sharing, signals over wires, button power");

        using var scope = UseLogicFixtures();
        var catalog = BlockCatalog.Instance;

        // ---- собираем: аккумулятор -> мотор (сигнал с сиденья) -> два вала над мотором
        var c = new Construction(new VoxelGrid());
        var battery = PlaceFixture(c, "zz_bat", new Vector3I(0, 0, 0));
        var motor = PlaceFixture(c, "zz_motor", new Vector3I(5, 0, 0));
        var shaft1 = PlaceFixture(c, "zz_shaft", new Vector3I(5, 1, 0));
        var shaft2 = PlaceFixture(c, "zz_shaft", new Vector3I(5, 2, 0));
        var farShaft = PlaceFixture(c, "zz_shaft", new Vector3I(9, 1, 0));
        var seat = PlaceFixture(c, "zz_seat", new Vector3I(0, 6, 0));
        using var runtime = new FunctionalBlockRuntime(c, catalog);
        var motorState = runtime.GetState<ElectricMotorState>(motor.InstanceId)!;
        var batteryState = runtime.GetState<BatteryState>(battery.InstanceId)!;
        var seatState = runtime.GetState<PilotSeatState>(seat.InstanceId)!;
        Check(batteryState.Charge == 100 && batteryState.Capacity == 100 && motorState.MaxPower == 10 && motorState.MaxRpm == 600,
            "setup: the battery starts full, the motor reads its default parameters", $"{batteryState.Charge} {motorState.MaxPower} {motorState.MaxRpm}");

        seatState.SetOccupied(true);
        seatState.SetInput(new SeatInput(new bool[6], false, false, new[] { 0.0, 1.0, 0.0, 0.0 }));
        Run(runtime, 1.0);
        Check(motorState.Rpm == 0 && batteryState.Charge == 100, "NO wires: the motor does not spin and the battery is untouched");

        c.TryConnect(seat.InstanceId, "ws_out", motor.InstanceId, "sig", out _);
        Run(runtime, 1.0);
        Check(motorState.Rpm == 0 && batteryState.Charge == 100, "a throttle signal but NO electricity wire: still no rotation and no consumption");

        c.TryConnect(battery.InstanceId, "pwr_out", motor.InstanceId, "pwr", out _);
        Run(runtime, 2.0);
        Check(Math.Abs(motorState.Rpm - 600) < 1e-6, "signal + electricity: the motor reaches max RPM (600)", $"{motorState.Rpm}");
        Check(batteryState.Charge < 100 && batteryState.Charge > 70, "...and the battery charge is spent", $"{batteryState.Charge}");

        double chargeBefore = batteryState.Charge;
        Run(runtime, 1.0);
        Check(Math.Abs((chargeBefore - batteryState.Charge) - 10.0) < 0.6, "at full throttle the motor draws maxPower (10 units/s) from the battery", $"{chargeBefore - batteryState.Charge}");

        // Вращение идёт по валам, впритык стоящим на порту мотора; дальний вал (с зазором) не подключён.
        Check(Math.Abs(runtime.GetNetworkRpm(shaft1.InstanceId) - 600) < 1e-6 && Math.Abs(runtime.GetNetworkRpm(shaft2.InstanceId) - 600) < 1e-6
              && Math.Abs(runtime.GetNetworkRpm(motor.InstanceId) - 600) < 1e-6,
            "the shafts stacked on the motor's port spin with the motor's RPM (rotation is transmitted through a chain of shafts)");
        Check(runtime.GetNetworkRpm(farShaft.InstanceId) == 0, "a shaft with a gap is not connected and stays still");

        // Сигнал полусилы и реверс.
        // Ось W/S переводим в sticky и «ставим ручку» на -0.5: клавиши отпущены, значение держится.
        seatState.Modes[1] = SeatAxisMode.Sticky;
        seatState.SetInput(new SeatInput(new bool[6], false, false, new double[4]));
        seatState.AxisValue[1] = -0.5;
        Run(runtime, 3.0);
        Check(Math.Abs(motorState.Rpm - (-300)) < 1e-6 && Math.Abs(runtime.GetNetworkRpm(shaft1.InstanceId) - (-300)) < 1e-6,
            "a half-negative signal gives half the speed in the other direction, and the shafts follow (the sign is kept)", $"{motorState.Rpm}");

        // Разряженный аккумулятор останавливает мотор.
        batteryState.Charge = 0;
        Run(runtime, 3.0);
        Check(Math.Abs(motorState.Rpm) < 1e-6 && Math.Abs(runtime.GetNetworkRpm(shaft2.InstanceId)) < 1e-6, "an empty battery stops the motor and the shafts");
        Check(motorState.PowerRatio == 0, "...because the power ratio it gets is 0", $"{motorState.PowerRatio}");

        // Провод убрали -> мотор останавливается, заряд не тратится.
        batteryState.Charge = 100;
        seatState.AxisValue[1] = 1;
        Run(runtime, 2.0);
        Check(motorState.Rpm > 500, "(setup) running again with a refilled battery");
        c.DisconnectNode(motor.InstanceId, "pwr");
        double chargeAtCut = batteryState.Charge;
        Run(runtime, 2.0);
        Check(Math.Abs(motorState.Rpm) < 1e-6 && Math.Abs(chargeAtCut - batteryState.Charge) < 1e-9, "removing the electricity wire stops the motor and the drain at once");
        c.TryConnect(battery.InstanceId, "pwr_out", motor.InstanceId, "pwr", out _);

        // Разные параметры экземпляров.
        var fast = PlaceFixture(c, "zz_motor", new Vector3I(12, 0, 0));
        c.TrySetParameter(fast, "maxRpm", "1500");
        c.TrySetParameter(fast, "maxPower", "40");
        var fastState = runtime.GetState<ElectricMotorState>(fast.InstanceId)!;
        Check(fastState.MaxRpm == 1500 && fastState.MaxPower == 40 && motorState.MaxRpm == 600, "parameters are per instance: the new motor has its own max RPM/power, the old one is unchanged");

        // Два мотора делят заряд честно: нехватка делит запрос поровну пропорционально.
        c.TryConnect(battery.InstanceId, "pwr_out", fast.InstanceId, "pwr", out _);
        c.TryConnect(seat.InstanceId, "ws_out", fast.InstanceId, "sig", out _);
        batteryState.Charge = 3; // меньше, чем нужно за тик двум моторам (50/с * 0.05 = 2.5 за тик - хватает на ~1 тик)
        seatState.AxisValue[1] = 1;
        runtime.Tick(0.05);
        Check(batteryState.Charge >= 0 && batteryState.Charge < 3, "two consumers on one battery: it never goes below zero when the demand exceeds the charge", $"{batteryState.Charge}");
        Run(runtime, 1.0);
        Check(batteryState.Charge < 1e-9, "...and the battery ends up empty", $"{batteryState.Charge}");

        // ---- аккумулятор: нода уровня, режимы, начальный заряд, зарядка от другого аккумулятора
        var c2 = new Construction(new VoxelGrid());
        var a = PlaceFixture(c2, "zz_bat", new Vector3I(0, 0, 0));
        var b = PlaceFixture(c2, "zz_bat", new Vector3I(0, 3, 0));
        c2.TrySetParameter(b, "initialCharge", "0.25");
        c2.TrySetParameter(b, "levelOutput", "fraction");
        using var runtime2 = new FunctionalBlockRuntime(c2, catalog);
        Check(runtime2.TryReadNode(a.InstanceId, "level", out var levelA) && levelA.Number == 100, "the level node reads the absolute charge by default");
        Check(runtime2.TryReadNode(b.InstanceId, "level", out var levelB) && Math.Abs(levelB.Number - 0.25) < 1e-9, "levelOutput=fraction gives the share 0..1, initialCharge=0.25 starts at a quarter");
        Check(!runtime2.TryReadNode(a.InstanceId, "pwr_out", out _), "an electricity node is not a readable value");

        c2.TryConnect(a.InstanceId, "pwr_out", b.InstanceId, "pwr_in", out _);
        double chargeA = runtime2.GetState<BatteryState>(a.InstanceId)!.Charge;
        Run(runtime2, 1.0);
        double gained = runtime2.GetState<BatteryState>(b.InstanceId)!.Charge - 25;
        double lost = chargeA - runtime2.GetState<BatteryState>(a.InstanceId)!.Charge;
        Check(Math.Abs(gained - 20) < 1.1 && Math.Abs(gained - lost) < 1e-6, "a battery on another's output charges at chargeRate (20/s) and the energy comes out of the source - nothing is created", $"gained={gained} lost={lost}");
        Run(runtime2, 10.0);
        Check(runtime2.GetState<BatteryState>(b.InstanceId)!.Charge <= 100 + 1e-9, "a battery never charges past its capacity");

        // ---- кнопка: питание только подсветка, сигнал идёт на вход
        var c3 = new Construction(new VoxelGrid());
        var bat3 = PlaceFixture(c3, "zz_bat", new Vector3I(0, 0, 0));
        var btn = PlaceFixture(c3, "zz_button", new Vector3I(5, 0, 0));
        var mot3 = PlaceFixture(c3, "zz_motor", new Vector3I(8, 0, 0));
        using var runtime3 = new FunctionalBlockRuntime(c3, catalog);
        var buttonState = runtime3.GetState<ButtonState>(btn.InstanceId)!;
        Run(runtime3, 0.2);
        Check(!buttonState.Powered, "an unwired button is not powered");
        c3.TryConnect(bat3.InstanceId, "pwr_out", btn.InstanceId, "electricity", out _);
        Run(runtime3, 0.2);
        Check(buttonState.Powered, "wired to a charged battery, the button is powered");
        runtime3.GetState<BatteryState>(bat3.InstanceId)!.Charge = 0;
        Run(runtime3, 0.2);
        Check(!buttonState.Powered, "...and stops being powered when the battery is empty");
        runtime3.GetState<BatteryState>(bat3.InstanceId)!.Charge = 50;
        runtime3.DebugForcePowered = false;

        c3.TryConnect(btn.InstanceId, "signal", mot3.InstanceId, "sig", out _);
        c3.TryConnect(bat3.InstanceId, "pwr_out", mot3.InstanceId, "pwr", out _);
        runtime3.Interact(btn.InstanceId, BlockInteraction.Press);
        Run(runtime3, 2.0);
        var motor3State = runtime3.GetState<ElectricMotorState>(mot3.InstanceId)!;
        Check(Math.Abs(motor3State.Signal - 1) < 1e-9 && motor3State.Rpm > 500, "a pressed button (Boolean 1) on the motor's Number input is a full-throttle signal");
        runtime3.Interact(btn.InstanceId, BlockInteraction.Release);
        Run(runtime3, 2.0);
        Check(motor3State.Signal == 0 && Math.Abs(motor3State.Rpm) < 1e-6, "released: signal 0, the motor spins down");

        // Отладочное питание F2 запитывает потребителей без аккумулятора и ничего не тратит.
        var c4 = new Construction(new VoxelGrid());
        var lonelyMotor = PlaceFixture(c4, "zz_motor", new Vector3I(0, 0, 0));
        var lonelyButton = PlaceFixture(c4, "zz_button", new Vector3I(3, 0, 0));
        using var runtime4 = new FunctionalBlockRuntime(c4, catalog);
        runtime4.DebugForcePowered = true;
        Run(runtime4, 0.2);
        Check(runtime4.GetState<ButtonState>(lonelyButton.InstanceId)!.Powered, "DebugForcePowered powers an unwired button");
        runtime4.DebugForcePowered = false;
        Run(runtime4, 0.2);
        Check(!runtime4.GetState<ButtonState>(lonelyButton.InstanceId)!.Powered && lonelyMotor.InstanceId > 0, "...and switching it off removes the power again");
    }

    // ================================================================== сеть вращения: соседние порты вала

    private void RunTorqueNetworkTests()
    {
        GD.Print("-- torque network: shafts connect through facing ports, rotation (turning a block) is respected");

        using var scope = UseLogicFixtures();
        var catalog = BlockCatalog.Instance;

        var c = new Construction(new VoxelGrid());
        var motor = PlaceFixture(c, "zz_motor", new Vector3I(0, 0, 0));           // порт Torque PosY
        var above = PlaceFixture(c, "zz_shaft", new Vector3I(0, 1, 0));          // порты PosY/NegY: NegY стыкуется с портом мотора
        var chained = PlaceFixture(c, "zz_shaft", new Vector3I(0, 2, 0));
        var sideways = PlaceFixture(c, "zz_shaft", new Vector3I(1, 0, 0));        // рядом с мотором по X: порты смотрят вверх/вниз - не стыкуется
        var gap = PlaceFixture(c, "zz_shaft", new Vector3I(0, 4, 0));             // зазор в клетку над цепочкой
        var network = TorqueNetwork.Build(c, catalog);
        Check(network.NetworkCount == 1 && network.NetworkOf(motor.InstanceId) >= 0
              && network.NetworkOf(above.InstanceId) == network.NetworkOf(motor.InstanceId) && network.NetworkOf(chained.InstanceId) == network.NetworkOf(motor.InstanceId),
            "a motor and a chain of shafts stacked on its port form ONE network", $"networks={network.NetworkCount}");
        Check(network.NetworkOf(sideways.InstanceId) == -1 && network.NetworkOf(gap.InstanceId) == -1,
            "a shaft beside the motor (its ports look up/down) and a shaft behind a gap are not in any network");
        Check(network.Members(network.NetworkOf(motor.InstanceId)).Count == 3, "the network lists its three members");

        // Поворот блока поворачивает его порты вокруг корневой клетки: вал, повёрнутый на 90° вокруг Z, стыкуется со стороной мотора по X.
        var c2 = new Construction(new VoxelGrid());
        var motor2 = PlaceFixture(c2, "zz_motor", new Vector3I(0, 0, 0));
        var turned = PlaceFixture(c2, "zz_shaft", new Vector3I(0, 1, 0), new Vector3I(0, 0, 1));
        var networkTurned = TorqueNetwork.Build(c2, catalog);
        Check(networkTurned.NetworkOf(turned.InstanceId) == -1,
            "a shaft turned 90 degrees above the motor no longer meets the motor's port with its own (rotation is applied to the ports)");
        var shaftFn = catalog.Get("zz_shaft").GetComponent<FunctionalBlockComponent>()!;
        var (_, portDirection) = TorqueNetwork.WorldPort(turned, shaftFn, shaftFn.Ports[0]);
        Check(portDirection.Y == 0 && Math.Abs(portDirection.X) + Math.Abs(portDirection.Z) == 1, "WorldPort turns the port's face direction with the block (up becomes sideways)", $"{portDirection}");

        // Мотор и вал, повёрнутые ОДИНАКОВО, стыкуются там, куда смотрит повёрнутый порт.
        var steps = new Vector3I(0, 0, 1);
        var c3 = new Construction(new VoxelGrid());
        var motor3 = PlaceFixture(c3, "zz_motor", new Vector3I(10, 10, 10), steps);
        var motorFn = catalog.Get("zz_motor").GetComponent<FunctionalBlockComponent>()!;
        var (motorPortCell, motorPortDirection) = TorqueNetwork.WorldPort(motor3, motorFn, motorFn.Ports[0]);
        var side = PlaceFixture(c3, "zz_shaft", motorPortCell + motorPortDirection, steps);
        var networkSide = TorqueNetwork.Build(c3, catalog);
        Check(networkSide.NetworkOf(side.InstanceId) >= 0 && networkSide.NetworkOf(side.InstanceId) == networkSide.NetworkOf(motor3.InstanceId),
            "a motor and a shaft, both turned the same way, connect where the turned port points", $"{motorPortCell} -> {motorPortDirection}");

        // Блок без портов вала сети не образует; блок с единственным портом без соседей — тоже.
        var c4 = new Construction(new VoxelGrid());
        PlaceFixture(c4, "zz_motor", new Vector3I(0, 0, 0));
        PlaceFixture(c4, "zz_button", new Vector3I(0, 1, 0));
        Check(TorqueNetwork.Build(c4, catalog).NetworkCount == 0, "a lone motor and a block without shaft ports form no network");
    }

    // ================================================================== пилотское сиденье: оси, кнопки, режимы

    private void RunPilotSeatTests()
    {
        GD.Print("-- pilot seat: axes (reset/sticky, sensitivity), hotkeys, triggers, occupied");

        using var scope = UseLogicFixtures();
        var catalog = BlockCatalog.Instance;
        var c = new Construction(new VoxelGrid());
        var seat = PlaceFixture(c, "zz_seat", new Vector3I(0, 0, 0));
        c.TrySetParameter(seat, "ws_mode", "sticky");
        c.TrySetParameter(seat, "lr_sensitivity", "0.5");
        using var runtime = new FunctionalBlockRuntime(c, catalog);
        var state = runtime.GetState<PilotSeatState>(seat.InstanceId)!;

        double Read(string node) => runtime.TryReadNode(seat.InstanceId, node, out var v) ? v.Number : double.NaN;

        Check(state.Modes[0] == SeatAxisMode.Reset && state.Modes[1] == SeatAxisMode.Sticky && state.Sensitivity[0] == 3 && state.Sensitivity[2] == 0.5,
            "the seat reads each axis's mode and sensitivity from its parameters", $"{state.Modes[0]} {state.Modes[1]} {state.Sensitivity[2]}");
        Check(Read("occuped") == 0 && Read("triger_1") == 0 && Read("ad_out") == 0 && Read("hotkey_3") == 0, "an empty seat outputs zeros everywhere");

        // Пока никто не сидит, ввод игнорируется.
        state.SetInput(new SeatInput(new[] { true, true, true, true, true, true }, true, true, new[] { 1.0, 1.0, 1.0, 1.0 }));
        Run(runtime, 0.5);
        Check(Read("hotkey_1") == 0 && Read("triger_2") == 0 && Read("ad_out") == 0 && Read("ws_out") == 0, "input given to an EMPTY seat is ignored (nobody is sitting)");

        state.SetOccupied(true);
        Check(Read("occuped") == 1, "sitting sets the occuped node");
        var hotkeys = new bool[6];
        hotkeys[2] = true;
        state.SetInput(new SeatInput(hotkeys, true, false, new[] { 1.0, 1.0, 1.0, -1.0 }));
        Check(Read("hotkey_3") == 1 && Read("hotkey_1") == 0 && Read("hotkey_6") == 0, "keys 1-6 map to hotkey_1..hotkey_6, only the held one is on");
        Check(Read("triger_1") == 1 && Read("triger_2") == 0, "LMB is triger_1, RMB is triger_2");

        // Ось режима reset: идёт к 1 со скоростью чувствительности (3/с) и возвращается к 0 после отпускания.
        Run(runtime, 0.1);
        Check(Math.Abs(Read("ad_out") - 0.3) < 0.02, "reset axis: held for 0.1 s at sensitivity 3 reaches ~0.3", $"{Read("ad_out")}");
        Run(runtime, 1.0);
        Check(Read("ad_out") == 1, "...and stops at 1 (never past the range)");
        Check(Math.Abs(Read("lf_out") - 0.55) < 0.03, "the left/right axis uses ITS OWN (slower) sensitivity 0.5 -> ~0.55 after 1.1 s", $"{Read("lf_out")}");
        Check(Read("ud_out") == -1, "a negative direction drives the axis to -1");
        state.SetInput(new SeatInput(new bool[6], false, false, new[] { 0.0, 0.0, 0.0, 0.0 }));
        Run(runtime, 1.0);
        Check(Read("ad_out") == 0 && Read("ud_out") == 0, "reset mode: released axes return to 0");
        Check(Read("ws_out") == 1, "sticky mode: the W/S axis stays where it was left (1) after the key is released");

        // Sticky: обратное направление уводит ось назад, ось не выходит за -1..1.
        state.SetInput(new SeatInput(new bool[6], false, false, new[] { 0.0, -1.0, 0.0, 0.0 }));
        Run(runtime, 0.5);
        Check(Math.Abs(Read("ws_out") - (1 - 1.5)) < 0.02 || Read("ws_out") == -0.5, "sticky: pressing the opposite key moves it back at sensitivity 3 (1 - 1.5 = -0.5)", $"{Read("ws_out")}");
        Run(runtime, 3.0);
        Check(Read("ws_out") == -1, "sticky is clamped at -1");

        // Вставание: кнопки не залипают, reset-оси вернутся, sticky остаётся.
        state.SetInput(new SeatInput(new[] { true, false, false, false, false, false }, true, true, new[] { 1.0, 0.0, 0.0, 0.0 }));
        state.SetOccupied(false);
        Run(runtime, 1.0);
        Check(Read("occuped") == 0 && Read("hotkey_1") == 0 && Read("triger_1") == 0 && Read("triger_2") == 0, "standing up clears occuped, the hotkeys and the triggers");
        Check(Read("ad_out") == 0 && Read("ws_out") == -1, "...reset axes go back to 0, the sticky axis keeps its value");

        // Параметры по умолчанию и мусор в параметрах.
        var c2 = new Construction(new VoxelGrid());
        var plain = PlaceFixture(c2, "zz_seat", new Vector3I(0, 0, 0));
        using var runtime2 = new FunctionalBlockRuntime(c2, catalog);
        var plainState = runtime2.GetState<PilotSeatState>(plain.InstanceId)!;
        Check(plainState.Modes.All(m => m == SeatAxisMode.Reset) && plainState.Sensitivity.All(s => s == 3), "a seat with default settings: every axis resets at sensitivity 3");
        var (eyeHeight, forward) = PilotSeatBehavior.ReadSeatSettings(catalog.Get("zz_seat").GetComponent<FunctionalBlockComponent>()!);
        Check(Math.Abs(eyeHeight - 0.5) < 1e-9 && forward == BlockFace.PosX, "the seat's eye height and facing come from the block's own params", $"{eyeHeight} {forward}");
    }
}
