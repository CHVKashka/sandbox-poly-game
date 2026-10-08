using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.Dev;

public sealed partial class SelfTest
{
    // ================================================================== настоящие блоки из blocks/: контракт данных и работающая сборка

    /// <summary>
    /// Проверяет НАСТОЯЩИЙ каталог (blocks/ пользователя), а не фикстуры: что блоки описаны согласованно с поведениями и что собранная из них машина работает.
    /// Универсальные инварианты (для ЛЮБОГО функционального блока, в том числе будущего): ноды и ячейки портов лежат внутри footprint'а, заявленное поведение реализовано,
    /// параметры в схеме корректны. Конкретные контракты (id нод сиденья, типы нод мотора/аккумулятора/кнопки) и сквозная сборка — по блокам, которые есть в каталоге;
    /// если блока нет (пользователь переименовал/удалил) — соответствующая часть пропускается с пометкой, а не падает.
    /// </summary>
    private void RunRealBlocksTests()
    {
        GD.Print("-- real blocks (blocks/): data contracts and a working machine built from them");

        var catalog = BlockCatalog.Instance;
        foreach (var definition in catalog.All.Where(d => d.HasComponent<FunctionalBlockComponent>()).OrderBy(d => d.Slug))
        {
            var block = FunctionalOf(definition)!;
            string who = $"'{definition.Slug}'";

            bool nodesInside = block.Nodes.All(n => InsideFootprint(block, n.Cell));
            Check(nodesInside, $"{who}: every node cell lies INSIDE the footprint (a cell outside is silently clamped onto another one)",
                string.Join(", ", block.Nodes.Where(n => !InsideFootprint(block, n.Cell)).Select(n => $"{n.Id}@{n.Cell}")));

            bool portsInside = block.Ports.All(p =>
            {
                var (u, v) = FunctionalBlockGeometry.FaceAxes(p.Face);
                return p.FaceCell.X >= block.FootprintMin[u] && p.FaceCell.X < block.FootprintMin[u] + block.Footprint[u]
                       && p.FaceCell.Y >= block.FootprintMin[v] && p.FaceCell.Y < block.FootprintMin[v] + block.Footprint[v];
            });
            Check(portsInside, $"{who}: every port cell lies inside its face of the footprint");

            Check(block.Behavior.Length == 0 || BlockBehaviorRegistry.TryGet(block.Behavior, out _),
                $"{who}: the declared behavior '{block.Behavior}' has an implementation (a typo would silently leave the block dead)");

            Check(FunctionalBlockComponent.FindNodeConflict(block.Nodes) == null, $"{who}: no two nodes of one type in one cell");
            Check(block.Ports.All(p => p.Resource == ResourceType.Torque || p.Resource == ResourceType.Fluid), $"{who}: ports are physical (Torque/Fluid) only");
            Check(block.Nodes.Select(n => n.Id).Distinct().Count() == block.Nodes.Count, $"{who}: node ids are unique (a wire addresses a node by id)");
        }

        // ---- контракты по блокам
        BlockDefinition? Real(string slug) => catalog.TryGetBySlug(slug, out var d) ? d : null;
        var seatDef = Real("pilot_seat");
        var motorDef = Real("electric_motor_small");
        var batteryDef = Real("battery_small");
        var buttonDef = Real("button");
        var shaftDef = Real("shaft_straight");

        if (seatDef != null)
        {
            var seat = FunctionalOf(seatDef)!;
            Check(seat.Behavior == PilotSeatBehavior.Key, "pilot_seat: behavior is PilotSeat");
            Check(PilotSeatBehavior.AxisNodeIds.All(id => seat.Nodes.Any(n => n.Id == id && n.Type == NodeType.Number && n.Direction == PortDirection.Out)),
                "pilot_seat: the four axis nodes (ad_out, ws_out, lf_out, ud_out) are Number OUTPUTS");
            Check(Enumerable.Range(0, PilotSeatBehavior.HotkeyCount).All(i => seat.Nodes.Any(n => n.Id == PilotSeatBehavior.HotkeyNodeId(i) && n.Type == NodeType.Boolean && n.Direction == PortDirection.Out)),
                "pilot_seat: hotkey_1..hotkey_6 are Boolean outputs");
            Check(new[] { PilotSeatBehavior.OccupiedNodeId, PilotSeatBehavior.Trigger1NodeId, PilotSeatBehavior.Trigger2NodeId }
                      .All(id => seat.Nodes.Any(n => n.Id == id && n.Type == NodeType.Boolean && n.Direction == PortDirection.Out)),
                "pilot_seat: occuped, triger_1 and triger_2 are Boolean outputs");
            Check(seat.Nodes.All(n => n.Direction == PortDirection.Out), "pilot_seat: every node is an output (the seat only reports the pilot's input)");

            var schema = Construction.ParametersOf(seatDef);
            Check(schema != null && PilotSeatBehavior.AxisNames.All(a => schema.TryGet($"{a}_mode", out var m) && m.Type == ParameterType.Enum && m.Options.Contains("reset") && m.Options.Contains("sticky")
                                                                 && schema.TryGet($"{a}_sensitivity", out var s) && s.Type == ParameterType.Float),
                "pilot_seat: every axis declares a reset/sticky mode and a sensitivity in its Parameters schema");
            var (eyeHeight, _) = PilotSeatBehavior.ReadSeatSettings(seat);
            Check(eyeHeight > 0 && seat.Nodes.Any(n => n.Id == PilotSeatBehavior.OccupiedNodeId), "pilot_seat: has an 'occuped' node (its cell is where the pilot sits) and a positive eye height", $"{eyeHeight}");
        }
        else
        {
            Check(true, "pilot_seat contract skipped: no such block in blocks/ right now");
        }

        if (motorDef != null)
        {
            var motor = FunctionalOf(motorDef)!;
            Check(motor.Behavior == ElectricMotorBehavior.Key, "electric_motor_small: behavior is ElectricMotor");
            Check(motor.Nodes.Any(n => n.Direction == PortDirection.In && n.Type == NodeType.Number) && motor.Nodes.Any(n => n.Direction == PortDirection.In && n.Type == NodeType.Electricity),
                "electric_motor_small: has a Number input (control signal) and an Electricity input (power)");
            Check(motor.Ports.Any(p => p.Resource == ResourceType.Torque && p.Direction == PortDirection.Out), "electric_motor_small: has a Torque OUT port (the shaft)");
            Check(Construction.ParametersOf(motorDef) is { } motorSchema && motorSchema.TryGet("maxPower", out _) && motorSchema.TryGet("maxRpm", out _), "electric_motor_small: maxPower and maxRpm are configurable");
        }
        else
        {
            Check(true, "electric_motor_small contract skipped: no such block in blocks/ right now");
        }

        if (batteryDef != null)
        {
            var battery = FunctionalOf(batteryDef)!;
            Check(battery.Behavior == BatteryBehavior.Key && battery.Capacity > 0, "battery_small: behavior is Battery and it has a capacity", $"{battery.Capacity}");
            Check(battery.Nodes.Any(n => n.Direction == PortDirection.Out && n.Type == NodeType.Number) && battery.Nodes.Any(n => n.Direction == PortDirection.Out && n.Type == NodeType.Electricity),
                "battery_small: has a Number output (charge level) and an Electricity output (power)");
            Check(Construction.ParametersOf(batteryDef) is { } batterySchema && batterySchema.TryGet("levelOutput", out _) && batterySchema.TryGet("initialCharge", out _), "battery_small: level output mode and initial charge are configurable");
        }
        else
        {
            Check(true, "battery_small contract skipped: no such block in blocks/ right now");
        }

        if (buttonDef != null)
        {
            var button = FunctionalOf(buttonDef)!;
            Check(button.Behavior == ButtonBehavior.Key, "button: behavior is Button (without it the block is a dead cube)");
            Check(button.Nodes.Any(n => n.Direction == PortDirection.Out && n.Type == NodeType.Boolean) && Construction.ParametersOf(buttonDef) is { } buttonSchema && buttonSchema.TryGet("mode", out _),
                "button: has a Boolean output and a configurable mode");
        }

        if (shaftDef != null)
        {
            var shaft = FunctionalOf(shaftDef)!;
            Check(shaft.Ports.Count >= 2 && shaft.Ports.All(p => p.Resource == ResourceType.Torque), "shaft_straight: carries Torque ports");
        }

        // ---- сквозная сборка: аккумулятор + мотор + вал над мотором + сиденье (сигнал) + кнопка (питание)
        if (seatDef == null || motorDef == null || batteryDef == null || buttonDef == null || shaftDef == null)
        {
            Check(true, "the real-blocks machine was skipped: one of pilot_seat/electric_motor_small/battery_small/button/shaft_straight is missing from blocks/ right now");
            return;
        }

        var c = new Construction(new VoxelGrid());
        var battery0 = PlaceFixture(c, "battery_small", new Vector3I(0, 0, 0));
        var motor0 = PlaceFixture(c, "electric_motor_small", new Vector3I(6, 0, 0));
        var shaft0 = PlaceFixture(c, "shaft_straight", new Vector3I(6, 1, 0));
        var shaft1 = PlaceFixture(c, "shaft_straight", new Vector3I(6, 2, 0));
        var seat0 = PlaceFixture(c, "pilot_seat", new Vector3I(10, 0, 0));
        var button0 = PlaceFixture(c, "button", new Vector3I(0, 3, 0));
        Check(new[] { battery0, motor0, shaft0, shaft1, seat0, button0 }.All(i => i != null), "(setup) the real blocks were all placed without overlapping");

        string electricityOut = FunctionalOf(batteryDef)!.Nodes.First(n => n.Direction == PortDirection.Out && n.Type == NodeType.Electricity).Id;
        string motorPower = FunctionalOf(motorDef)!.Nodes.First(n => n.Direction == PortDirection.In && n.Type == NodeType.Electricity).Id;
        string motorSignal = FunctionalOf(motorDef)!.Nodes.First(n => n.Direction == PortDirection.In && n.Type == NodeType.Number).Id;
        string buttonPower = FunctionalOf(buttonDef)!.Nodes.First(n => n.Direction == PortDirection.In && n.Type == NodeType.Electricity).Id;
        string levelNode = FunctionalOf(batteryDef)!.Nodes.First(n => n.Direction == PortDirection.Out && n.Type == NodeType.Number).Id;

        Check(c.TryConnect(battery0.InstanceId, electricityOut, motor0.InstanceId, motorPower, out string e1), "battery -> motor power wire is accepted", e1);
        Check(c.TryConnect(seat0.InstanceId, "ws_out", motor0.InstanceId, motorSignal, out string e2), "seat W/S axis -> motor signal wire is accepted", e2);
        Check(c.TryConnect(battery0.InstanceId, electricityOut, button0.InstanceId, buttonPower, out string e3), "battery -> button power wire is accepted", e3);

        using var runtime = new FunctionalBlockRuntime(c, catalog);
        var seatState = runtime.GetState<PilotSeatState>(seat0.InstanceId)!;
        var batteryState = runtime.GetState<BatteryState>(battery0.InstanceId)!;
        var motorState = runtime.GetState<ElectricMotorState>(motor0.InstanceId)!;
        var buttonState = runtime.GetState<ButtonState>(button0.InstanceId)!;

        Run(runtime, 0.5);
        Check(buttonState.Powered && motorState.Rpm == 0 && batteryState.Charge == batteryState.Capacity,
            "idle machine: the button is powered by the battery, the motor waits for a signal, nothing is spent");
        Check(runtime.TryReadNode(battery0.InstanceId, levelNode, out var fullLevel) && fullLevel.Number > 0, "the battery reports its charge on its level node", $"{fullLevel.Number}");

        seatState.SetOccupied(true);
        seatState.SetInput(new SeatInput(new bool[6], false, false, new[] { 0.0, 1.0, 0.0, 0.0 })); // W
        Run(runtime, 2.0);
        Check(motorState.Rpm > 0 && Math.Abs(motorState.Rpm - motorState.MaxRpm) < 1e-6, "pilot holds W: the motor reaches its max RPM", $"{motorState.Rpm}/{motorState.MaxRpm}");
        Check(Math.Abs(motorState.Rpm - 3600) < 1e-6, "the real motor at signal 1 turns 3600 RPM = 60 revolutions per second", $"{motorState.Rpm}");
        double chargeBefore = batteryState.Charge;
        Run(runtime, 10.0);
        Check(Math.Abs((chargeBefore - batteryState.Charge) - 1.0) < 0.01, "...and at that signal it consumes 0.1 units of electricity per second (1.0 over 10 s)", $"{chargeBefore - batteryState.Charge}");
        Check(batteryState.Charge < batteryState.Capacity, "...and the battery charge is being spent", $"{batteryState.Charge}");
        Check(runtime.TryReadNode(battery0.InstanceId, levelNode, out var lowerLevel) && lowerLevel.Number < fullLevel.Number, "...which the level node shows");
        Check(Math.Abs(runtime.GetNetworkRpm(shaft0.InstanceId) - motorState.Rpm) < 1e-6 && Math.Abs(runtime.GetNetworkRpm(shaft1.InstanceId) - motorState.Rpm) < 1e-6,
            "the two shafts stacked above the motor port turn with it (shaft_straight ports meet the motor's port)");

        seatState.SetInput(new SeatInput(new bool[6], false, false, new[] { 0.0, -1.0, 0.0, 0.0 })); // S
        Run(runtime, 3.0);
        Check(motorState.Rpm < 0 && Math.Abs(runtime.GetNetworkRpm(shaft1.InstanceId) - motorState.Rpm) < 1e-6, "pilot holds S: the motor turns the other way and the shafts follow");

        seatState.SetOccupied(false);
        Run(runtime, 3.0);
        Check(Math.Abs(motorState.Rpm) < 1e-6 && Math.Abs(runtime.GetNetworkRpm(shaft0.InstanceId)) < 1e-6, "pilot stands up: the W/S axis resets, the motor and shafts stop");
    }

    private static bool InsideFootprint(FunctionalBlockComponent block, Vector3I cell) =>
        cell.X >= block.FootprintMin.X && cell.X < block.FootprintMin.X + block.Footprint.X
        && cell.Y >= block.FootprintMin.Y && cell.Y < block.FootprintMin.Y + block.Footprint.Y
        && cell.Z >= block.FootprintMin.Z && cell.Z < block.FootprintMin.Z + block.Footprint.Z;

    // ================================================================== редактор блоков сохраняет схему параметров

    private async Task RunBlockEditorSchemaTests(BuildEditor editor)
    {
        GD.Print("-- block editor keeps the Parameters schema (a re-save must not wipe the settings of a block)");

        const string dir = "user://selftest_blockeditor";
        const string slug = "selftest_editor_schema";
        DirAccess.MakeDirRecursiveAbsolute(dir);
        string path = $"{dir}/{slug}.xml";
        const string schema =
            "{ \"parameters\": [ { \"id\": \"gain\", \"label\": \"Gain\", \"type\": \"Float\", \"min\": 0, \"max\": 2, \"default\": 1, \"hint\": \"a < b & c > d\" } ] }";
        using (var file = FileAccess.Open(path, FileAccess.ModeFlags.Write))
        {
            file.StoreString(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + $"<Block id=\"{slug}\" name=\"Schema\">\n  <Color>#ffffff</Color>\n" +
                "  <Component type=\"Parameters\">{ \"parameters\": [ { \"id\": \"gain\", \"label\": \"Gain\", \"type\": \"Float\", \"min\": 0, \"max\": 2, \"default\": 1, \"hint\": \"a &lt; b &amp; c &gt; d\" } ] }</Component>\n" +
                "  <Component type=\"FunctionalBlock\">{ \"footprint\": [1,1,1] }</Component>\n</Block>\n");
        }

        var tool = new BlockEditor { BlocksDirectory = dir };
        editor.AddChild(tool);
        await Frames(editor, 2);

        tool.LoadSlug(slug);
        Check(tool.Ui.ParametersSchemaJson.Contains("\"gain\"") && tool.Ui.ParametersSchemaJson.Contains("a < b & c > d"), "loading a block shows its Parameters schema (XML entities decoded)", tool.Ui.ParametersSchemaJson);

        tool.Save();
        string xml = FileAccess.GetFileAsString(path);
        Check(xml.Contains("<Component type=\"Parameters\">") && xml.Contains("&lt; b &amp; c &gt;"), "re-saving writes the Parameters component back, with XML-escaped text");
        var catalog = BlockCatalog.Load(dir);
        var reloaded = catalog.TryGetBySlug(slug, out var def) ? Construction.ParametersOf(def) : null;
        Check(reloaded != null && reloaded.TryGet("gain", out var gain) && gain.Max == 2 && gain.Hint == "a < b & c > d", "the game's catalog reads the schema back exactly after the editor's re-save");

        tool.Ui.SetParametersSchemaForTesting("{ \"parameters\": [ { \"id\": \"x\", \"type\": \"Enum\" } ] }");
        string before = FileAccess.GetFileAsString(path);
        tool.Save();
        Check(FileAccess.GetFileAsString(path) == before && tool.Ui.Status.Contains("parameters schema"), "a schema that does not parse blocks Save (the file on disk is untouched)", tool.Ui.Status);

        tool.Ui.SetParametersSchemaForTesting("");
        tool.Save();
        Check(!FileAccess.GetFileAsString(path).Contains("Parameters"), "an empty schema field means the block has no settings: the component is not written");

        if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
        tool.QueueFree();
    }
}
