using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Editor.Ui;
using SandboxPolyGame.Runtime;
using SandboxPolyGame.World;

namespace SandboxPolyGame.Dev;

public sealed partial class SelfTest
{
    // ================================================================== игрок-клиент на сервере: реплика не толкает постройки

    /// <summary>
    /// Баг (найден пользователем): проблема с коллизией игрока была решена для хоста, но не для клиента. На сервере КАЖДЫЙ клиент представлен репликой-<see cref="Player"/> с капсулой
    /// (позицию ей ставит <see cref="MultiplayerSynchronizer"/>, она телепортируется); когда клиент садится на сиденье или встаёт, его реплика оказывается ВНУТРИ динамической постройки сервера,
    /// и физика выталкивает постройку с огромной скоростью. У хоста от этого защищает отключаемая капсула собственного игрока (сидит - выключена, встал - включается через
    /// <see cref="Player.StandSettleTicks"/> тиков), но на реплику это не распространялось. Теперь реплика (не авторитет) ни с чем не сталкивается и ничем не сталкивается.
    /// </summary>
    private async Task RunRemotePlayerReplicaTests(BuildEditor editor)
    {
        GD.Print("-- multiplayer: a remote player's replica on the server never pushes a dynamic vehicle (seated or standing inside it)");

        var floor = new StaticBody3D { Name = "ReplicaFloor", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(80, 1, 80) } });
        editor.AddChild(floor);
        var parent = new Node3D();
        editor.AddChild(parent);

        var plate = new Construction(new VoxelGrid());
        plate.PlaceBlock(Vector3I.Zero, new Vector3I(12, 4, 12), BlockCatalog.Instance.Get("block"), Colors.White);
        var vehicle = VehicleSpawner.Spawn(parent, ConstructionIO.Serialize(plate), new Vector3(0, 0.02f, 0));
        await PhysicsFrames(editor, 90); // осесть
        var rest = vehicle.GlobalPosition;

        // Реплика клиента: авторитет у другого пира (77), узел строится в _EnterTree по правилам мультиплеера.
        var replica = new Player { Name = "77" };
        replica.SetMultiplayerAuthority(77);
        editor.AddChild(replica);
        await Frames(editor, 1);
        Check(!replica.IsMultiplayerAuthority(), "(setup) the replica belongs to another peer");
        Check(replica.CollisionLayer != 1 || replica.CollisionMask == 0, "the replica is off the world's collision layer / scans nothing (it is a puppet of the client's real body)", $"layer={replica.CollisionLayer} mask={replica.CollisionMask}");

        // Клиент «сел»: реплика внутри постройки, в её центре (глаза сиденья внутри тела). Синхронизатор телепортирует её каждый кадр.
        var inside = vehicle.ToGlobal(new Vector3(1.5f, 0.5f, 1.5f));
        double maxSpeed = 0, maxSpin = 0;
        for (int i = 0; i < 120; i++)
        {
            replica.GlobalPosition = inside - new Vector3(0, 0.9f, 0);
            await PhysicsFrames(editor, 1);
            maxSpeed = Math.Max(maxSpeed, vehicle.LinearVelocity.Length());
            maxSpin = Math.Max(maxSpin, vehicle.AngularVelocity.Length());
        }

        Check(maxSpeed < 0.5 && maxSpin < 0.5 && vehicle.GlobalPosition.DistanceTo(rest) < 0.05, "a client seated inside the vehicle does not fling it: the replica's capsule is not part of the server's physics", $"max speed {maxSpeed:0.000} m/s, spin {maxSpin:0.000}, moved {vehicle.GlobalPosition.DistanceTo(rest):0.000} m");

        // Клиент «встал» рядом и ходит впритык к борту: тоже не толкает.
        maxSpeed = 0;
        for (int i = 0; i < 90; i++)
        {
            replica.GlobalPosition = vehicle.ToGlobal(new Vector3(-0.2f + 0.002f * i, 0, 1.5f));
            await PhysicsFrames(editor, 1);
            maxSpeed = Math.Max(maxSpeed, vehicle.LinearVelocity.Length());
        }

        Check(maxSpeed < 0.5, "a client walking up against the hull does not push it either", $"max speed {maxSpeed:0.000} m/s");

        // Свой (авторитетный) игрок по-прежнему сталкивается с миром: стоит на полу и на постройке, а не проваливается.
        replica.QueueFree();
        var local = new Player { Name = "Local" };
        editor.AddChild(local);
        await Frames(editor, 1);
        Check(local.IsMultiplayerAuthority() && local.CollisionLayer == 1 && (local.CollisionMask & 1) != 0, "(control) the player this peer controls keeps colliding with the world", $"layer={local.CollisionLayer} mask={local.CollisionMask}");
        local.GlobalPosition = new Vector3(20, 2, 0);
        await PhysicsFrames(editor, 60);
        Check(local.GlobalPosition.Y > -0.1 && local.GlobalPosition.Y < 0.5, "...and stands on the floor", $"y={local.GlobalPosition.Y}");

        local.QueueFree();
        parent.QueueFree();
        floor.QueueFree();
        await Frames(editor, 2);
    }

    // ================================================================== инструмент «Parameters» настоящим вводом

    /// <summary>
    /// Раньше инструмент проверялся вызовом <c>ParametersToolClickAt</c> в обход событий мыши; здесь тот же сценарий проходит ВЕСЬ путь, как у игрока: клавиша P, движение мыши, настоящий ЛКМ,
    /// правка в панели. Два блока: без модели (куб-плейсхолдер) и с моделью, повёрнутый и с ручным footprint'ом (как колесо).
    /// </summary>
    private async Task RunParametersToolRealInputTests(BuildEditor editor)
    {
        GD.Print("-- Parameters tool through REAL input: key P, mouse move, left click on a block, the panel opens and an edit reaches the block");

        const string modelPath = "res://meshes/selftest_params_model.gltf";
        WriteTestGltf(modelPath, (new Vector3(-1, -1, -1), new Vector3(1, 1, 1)));
        FunctionalBlockGeometry.ClearSceneCache(modelPath);
        string schema = "{ \"parameters\": [ { \"id\": \"gain\", \"label\": \"Gain\", \"type\": \"Float\", \"min\": 0, \"max\": 10, \"default\": 2 } ] }";

        using var scope = UseCatalogWith(
            new FixtureBlock("zz_cfg_plain", "{ \"footprint\": [1,1,1] }", schema),
            new FixtureBlock("zz_cfg_model", "{ \"scene\": \"" + modelPath + "\", \"footprint\": [2,3,3], \"footprintMin\": [0,-1,-1], \"footprintManual\": true, \"attachCell\": [1,0,0], \"collision\": [[1,0,0]] }", schema));
        var catalog = BlockCatalog.Instance;
        var state = editor.State;
        var camera = editor.EditorCamera;
        var construction = editor.World.Construction;
        bool accumulated = Input.UseAccumulatedInput;
        Input.UseAccumulatedInput = false;

        construction.Clear();
        state.Tool = ToolMode.None;
        state.ResetPendingRotation();
        construction.PlaceBlock(Vector3I.Zero, new Vector3I(14, 1, 8), catalog.Get("block"), Colors.White);
        var plain = PlaceFixture(construction, "zz_cfg_plain", new Vector3I(2, 1, 3));
        var model = PlaceFixture(construction, "zz_cfg_model", new Vector3I(8, 2, 3), new Vector3I(0, 0, 1));
        await Frames(editor, 3);

        camera.LookAtPoint(new Vector3(1.75f, 4f, 0.8f), new Vector3(1.75f, 0f, 0.8f));
        await Frames(editor, 2);
        editor.GetViewport().GuiReleaseFocus();

        // ---- включить инструмент клавишей P
        await PressKey(editor, Godot.Key.P);
        Check(state.Tool == ToolMode.Parameters, "key P switches the Parameters tool on");

        async Task<bool> ClickBlock(BlockInstance instance, string what)
        {
            var center = BuildSpace.CellCenter(instance.Origin);
            var aim = camera.UnprojectPosition(center + new Vector3(0, BuildSpace.CellSize * 0.5f, 0));
            await Move(editor, aim);
            await Frames(editor, 2);
            string hover = $"hover={editor.Hover} configurable={editor.ParametersHoverInstanceId}";
            await Click(editor, aim, MouseButton.Left);
            await Frames(editor, 1);
            bool opened = editor.Ui.ParametersPanel.IsOpen && editor.Ui.ParametersPanel.InstanceId == instance.InstanceId;
            Check(opened, $"a real left click on the {what} opens its parameter panel", hover);
            return opened;
        }

        if (await ClickBlock(plain, "model-less block"))
        {
            var panel = editor.Ui.ParametersPanel;
            Check(panel.ParameterIds.SequenceEqual(new[] { "gain" }), "...with the block's settings");
            panel.EditForTesting("gain", "7.5");
            Check(construction.GetInstance(plain.InstanceId)!.Parameters is { } stored && stored["gain"] == "7.5", "...and an edit in the panel reaches the block");
            await Click(editor, camera.UnprojectPosition(BuildSpace.CellCenter(new Vector3I(6, 0, 10))), MouseButton.Left); // пустая земля за плитой
            Check(!panel.IsOpen, "a click on empty space closes the panel");
        }

        var modelFresh = construction.GetInstance(model.InstanceId)!;
        var modelFn = FunctionalOf(catalog.Get("zz_cfg_model"))!;
        var modelAim = BuildSpace.CellCenter(BlockFootprint.RootCell(modelFresh.Origin, modelFn.FootprintMin, modelFn.Footprint, modelFresh.RotationSteps) + BlockFootprint.Rotate(modelFn.AttachCell, modelFresh.RotationSteps));
        var modelScreen = camera.UnprojectPosition(modelAim + new Vector3(0, BuildSpace.CellSize * 0.5f, 0));
        await Move(editor, modelScreen);
        await Frames(editor, 2);
        Check(editor.Hover.IsBlock && editor.World.Construction.GetOwner(editor.Hover.BlockCell)?.InstanceId == model.InstanceId, "the cursor over a rotated block with a model and a manual footprint hits that block", $"{editor.Hover}");
        await Click(editor, modelScreen, MouseButton.Left);
        await Frames(editor, 1);
        Check(editor.Ui.ParametersPanel.IsOpen && editor.Ui.ParametersPanel.InstanceId == model.InstanceId, "a real left click on a block with a model opens its panel too", $"open={editor.Ui.ParametersPanel.IsOpen} id={editor.Ui.ParametersPanel.InstanceId}");


        // ---- настоящие блоки каталога (инвариант, а не конкретные слаги: пользователь их правит): у каждого блока со схемой параметров клик открывает панель
        construction.Clear();
        construction.PlaceBlock(new Vector3I(-95, 0, -4), new Vector3I(190, 1, 16), catalog.Get("block"), Colors.White);
        var realWithSettings = catalog.All.Where(d => d.GetComponent<FunctionalBlockComponent>() != null && Construction.ParametersOf(d) != null && !d.Slug.StartsWith("zz_")).ToList();
        int column = 0;
        foreach (var definition in realWithSettings)
        {
            var fn = FunctionalOf(definition)!;
            var cell = new Vector3I(-90 + column * 12, 4, 2);
            column++;
            var instance = PlaceFixture(construction, definition.Slug, cell);
            if (instance == null) { Check(false, $"(setup) real block '{definition.Slug}' was placed on the test plate"); continue; }
            await Frames(editor, 2);
            var look = BuildSpace.CellCenter(cell);
            camera.LookAtPoint(new Vector3(look.X, 9f, look.Z), new Vector3(look.X, 0f, look.Z));
            await Frames(editor, 2);
            var click = camera.UnprojectPosition(look);
            await Move(editor, click);
            await Frames(editor, 2);
            string hover = $"hover={editor.Hover} configurable={editor.ParametersHoverInstanceId}";
            await Click(editor, click, MouseButton.Left);
            await Frames(editor, 1);
            bool open = editor.Ui.ParametersPanel.IsOpen && editor.Ui.ParametersPanel.InstanceId == instance.InstanceId;
            Check(open, $"real block '{definition.Slug}': a real click opens its parameter panel", hover);
            await Frames(editor, 2);
            if (open) Check(editor.Ui.ParametersPanel.RowFullyVisible(editor.Ui.ParametersPanel.ParameterIds[0]) && editor.Ui.ParametersPanel.ListGlobalRect.Size.Y > 60, $"real block '{definition.Slug}': its parameters are actually visible in the menu (the list has height)", $"{editor.Ui.ParametersPanel.ListGlobalRect}");
            if (open) Check(editor.Ui.ParametersPanel.ParameterIds.Count == Construction.ParametersOf(definition)!.Parameters.Count, $"real block '{definition.Slug}': the panel lists every parameter of its schema");
            await PressKey(editor, Godot.Key.Escape);
        }

        await PressKey(editor, Godot.Key.P);
        Check(state.Tool == ToolMode.None && !editor.Ui.ParametersPanel.IsOpen, "key P again switches the tool off and closes the panel");

        construction.Clear();
        Input.UseAccumulatedInput = accumulated;
        if (FileAccess.FileExists(modelPath)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(modelPath));
        FunctionalBlockGeometry.ClearSceneCache(modelPath);
        await Frames(editor, 2);
    }

    // ================================================================== подсказка в редакторе (инструмент) и в мире (значения нод)

    private async Task RunEditorTooltipToolTests(BuildEditor editor)
    {
        GD.Print("-- editor tooltip as a tool (button / key T), right-click quick properties");

        string schemaJson = "{ \"parameters\": [ { \"id\": \"gain\", \"label\": \"Gain\", \"type\": \"Float\", \"min\": 0, \"max\": 10, \"default\": 2 }, " +
            "{ \"id\": \"mode\", \"label\": \"Mode\", \"type\": \"Enum\", \"options\": [\"a\", \"b\"], \"default\": \"a\" } ] }";
        using var scope = UseCatalogWith(
            new FixtureBlock("zz_tip_func", "{ \"footprint\": [1,1,1] }", schemaJson),
            new FixtureBlock("zz_tip_bare", "{ \"footprint\": [1,1,1] }", NoBase: true));
        var catalog = BlockCatalog.Instance;
        var definition = catalog.Get("zz_tip_func");
        var schema = Construction.ParametersOf(definition)!;

        var state = editor.State;
        var camera = editor.EditorCamera;
        var build = editor.World.Construction;
        bool accumulated = Input.UseAccumulatedInput;
        Input.UseAccumulatedInput = false;
        build.Clear();
        state.Tool = ToolMode.None;
        state.TooltipEnabled = true;
        state.ResetPendingRotation();
        build.PlaceBlock(Vector3I.Zero, new Vector3I(8, 1, 8), catalog.Get("block"), Colors.White);
        var tip = PlaceFixture(build, "zz_tip_func", new Vector3I(2, 1, 3));
        PlaceFixture(build, "zz_tip_bare", new Vector3I(5, 1, 3));
        await Frames(editor, 3);
        camera.LookAtPoint(new Vector3(1.75f, 4f, 0.8f), new Vector3(1.75f, 0f, 0.8f));
        await Frames(editor, 2);
        editor.GetViewport().GuiReleaseFocus();
        var tooltip = editor.Ui.BlockTooltip;

        async Task<Vector2> HoverCell(Vector3I cell, float lift = 0.125f)
        {
            var at = camera.UnprojectPosition(BuildSpace.CellCenter(cell) + new Vector3(0, lift, 0));
            await Move(editor, at);
            await Frames(editor, 2);
            return at;
        }

        // ---- содержимое
        var mouse = await HoverCell(new Vector3I(2, 1, 3));
        Check(state.TooltipEnabled && tooltip.IsVisible && tooltip.Title == "zz_tip_func", "the tooltip is on by default: hovering a functional block with settings shows its name", $"visible={tooltip.IsVisible} '{tooltip.Title}'");
        Check(tooltip.Lines.SequenceEqual(new[] { ("Gain", "2"), ("Mode", "a") }), "...and ONLY its parameters with their current values (the defaults until changed)", string.Join("; ", tooltip.Lines.Select(l => l.Label + "=" + l.Value)));
        Check(Math.Abs(tooltip.GlobalRect.Position.X - (mouse.X + 18)) < 1.5 && Math.Abs(tooltip.GlobalRect.Position.Y - (mouse.Y + 18)) < 1.5, "...next to the cursor", $"{tooltip.GlobalRect} mouse={mouse}");
        Check(tooltip.Hint.Contains("RMB"), "...with a hint that the right mouse button edits the settings", tooltip.Hint);

        build.TrySetParameter(tip, "gain", "7.5");
        build.TrySetParameter(tip, "mode", "b");
        await Frames(editor, 2);
        Check(tooltip.Lines.SequenceEqual(new[] { ("Gain", "7.5"), ("Mode", "b") }), "after the parameters are changed the tooltip shows the new values", string.Join("; ", tooltip.Lines.Select(l => l.Label + "=" + l.Value)));

        // ---- включается и выключается как инструмент
        await PressKey(editor, Godot.Key.T);
        await Frames(editor, 2);
        Check(!state.TooltipEnabled && !tooltip.IsVisible, "key T switches the tooltip OFF: it disappears even though the cursor is still over the block");
        await HoverCell(new Vector3I(2, 1, 3));
        Check(!tooltip.IsVisible, "...and stays off while hovering");
        await PressKey(editor, Godot.Key.T);
        await Frames(editor, 2);
        Check(state.TooltipEnabled && tooltip.IsVisible, "key T again switches it back ON");

        var button = editor.Ui.Toolbar.TooltipButton;
        Check(button.ButtonPressed, "the toolbar's Tooltip button shows it as on");
        button.ButtonPressed = false;
        await Frames(editor, 2);
        Check(!state.TooltipEnabled && !tooltip.IsVisible, "turning the toolbar button off disables the tooltip");
        state.TooltipEnabled = true;
        await Frames(editor, 2);
        Check(button.ButtonPressed && tooltip.IsVisible, "...and the button follows the state when it is switched on from elsewhere");

        // ---- работает с любым инструментом, но не там, где нечего показать
        state.Tool = ToolMode.Parameters;
        await Frames(editor, 2);
        await HoverCell(new Vector3I(2, 1, 3));
        Check(tooltip.IsVisible && tooltip.Hint.Contains("LMB"), "the tooltip also shows under the Parameters tool (hint: LMB)");
        state.Tool = ToolMode.None;
        await HoverCell(new Vector3I(5, 1, 3));
        Check(!tooltip.IsVisible, "a functional block with no settings has no tooltip");
        await HoverCell(new Vector3I(6, 0, 5), 0.05f);
        Check(!tooltip.IsVisible, "a plain rubber block has none");
        await Move(editor, camera.UnprojectPosition(BuildSpace.CellCenter(new Vector3I(6, 0, 12))));
        await Frames(editor, 2);
        Check(!tooltip.IsVisible, "empty ground has none");

        // ---- у границ окна
        var instance = build.GetInstance(tip.InstanceId)!;
        var viewport = new Vector2(1000, 600);
        tooltip.Show(instance, definition, schema, new Vector2(990, 100), viewport);
        var rightEdge = tooltip.GlobalRect;
        tooltip.Show(instance, definition, schema, new Vector2(100, 595), viewport);
        var bottomEdge = tooltip.GlobalRect;
        Check(rightEdge.End.X <= viewport.X + 0.5 && rightEdge.Position.X < 990, "at the right edge the tooltip flips to the left of the cursor and stays inside the window", $"{rightEdge}");
        Check(bottomEdge.End.Y <= viewport.Y + 0.5, "at the bottom edge it is pulled up inside the window", $"{bottomEdge}");
        tooltip.Hide();

        // ---- ПКМ по блоку: свойства, как в Stormworks
        await HoverCell(new Vector3I(2, 1, 3));
        Check(state.Tool == ToolMode.None && !editor.Ui.ParametersPanel.IsOpen, "(setup) no tool is on and no panel is open");
        await Click(editor, camera.UnprojectPosition(BuildSpace.CellCenter(new Vector3I(2, 1, 3)) + new Vector3(0, 0.125f, 0)), MouseButton.Right);
        await Frames(editor, 2);
        Check(state.Tool == ToolMode.Parameters && editor.Ui.ParametersPanel.IsOpen && editor.Ui.ParametersPanel.InstanceId == tip.InstanceId && editor.Ui.ParametersPanel.ParameterIds.SequenceEqual(new[] { "gain", "mode" }),
            "a right click on a block with settings opens its properties panel without switching tools by hand (Stormworks-style)", $"tool={state.Tool} open={editor.Ui.ParametersPanel.IsOpen}");
        editor.Ui.ParametersPanel.EditForTesting("gain", "9");
        Check(build.GetInstance(tip.InstanceId)!.Parameters is { } stored && stored["gain"] == "9", "...and an edit there reaches the block");
        await PressKey(editor, Godot.Key.P);
        Check(state.Tool == ToolMode.None && !editor.Ui.ParametersPanel.IsOpen, "P leaves the tool (and closes the panel)");
        int instancesBefore = build.Instances.Count;
        await Click(editor, camera.UnprojectPosition(BuildSpace.CellCenter(new Vector3I(6, 0, 12))), MouseButton.Right);
        await Frames(editor, 2);
        Check(state.Tool == ToolMode.None && build.Instances.Count == instancesBefore, "a right click over nothing configurable changes nothing");

        build.Clear();
        state.TooltipEnabled = true;
        Input.UseAccumulatedInput = accumulated;
        await Frames(editor, 2);
    }

    /// <summary>Значения нод функциональных блоков: рантайм (<see cref="FunctionalBlockRuntime.ReadNodes"/>) и подсказка прицела в игровом мире (только значения нод, без параметров).</summary>
    private async Task RunWorldNodeTooltipTests(BuildEditor editor)
    {
        GD.Print("-- game world: aiming at a functional block shows ONLY its node values (outputs, inputs with their wire value, power); no parameters");

        using var scope = UseLogicFixtures();
        var catalog = BlockCatalog.Instance;

        // ---- рантайм: значения нод
        var wired = new Construction(new VoxelGrid());
        var seat = PlaceFixture(wired, "zz_seat", new Vector3I(0, 10, 0));
        var motor = PlaceFixture(wired, "zz_motor", new Vector3I(0, 0, 0));
        var battery = PlaceFixture(wired, "zz_bat", new Vector3I(10, 0, 0));
        var button = PlaceFixture(wired, "zz_button", new Vector3I(20, 0, 0));
        wired.TryConnect(seat.InstanceId, "ws_out", motor.InstanceId, "sig", out _);
        wired.TryConnect(battery.InstanceId, "pwr_out", motor.InstanceId, "pwr", out _);
        using var runtime = new FunctionalBlockRuntime(wired, catalog);
        var seatState = runtime.GetState<PilotSeatState>(seat.InstanceId)!;
        seatState.Modes[1] = SeatAxisMode.Sticky;
        seatState.AxisValue[1] = 0.5;
        runtime.Tick(0.016);
        runtime.Tick(0.016);

        string Text(int id, string node) => runtime.ReadNodes(id).First(r => r.Id == node).Text;
        Check(Text(seat.InstanceId, "ws_out") == "0.5" && Text(seat.InstanceId, "hotkey_1") == "OFF", "outputs: a Number shows its value, a Boolean shows ON/OFF");
        Check(Text(motor.InstanceId, "sig") == "0.5", "an input shows the value on its wire", Text(motor.InstanceId, "sig"));
        Check(Text(button.InstanceId, "signal") == "OFF" && Text(motor.InstanceId, "pwr").StartsWith("powered"), "the motor's electricity input is powered by the wired battery", Text(motor.InstanceId, "pwr"));
        Check(Text(battery.InstanceId, "pwr_out").StartsWith("stored"), "a battery's electricity output shows its stored charge", Text(battery.InstanceId, "pwr_out"));
        Check(Text(button.InstanceId, "electricity") == "no power", "an unwired electricity input: no power", Text(button.InstanceId, "electricity"));
        runtime.Interact(button.InstanceId, BlockInteraction.Press);
        Check(Text(button.InstanceId, "signal") == "ON", "a pressed button's output shows ON");
        var unwiredSeat = runtime.ReadNodes(motor.InstanceId).First(r => r.Id == "sig");
        Check(unwiredSeat.Label == "sig (Number in)", "the line label is: id (type in/out)", unwiredSeat.Label);
        Check(runtime.ReadNodes(12345).Count == 0, "an unknown instance has no readings");

        // ---- мир: прицел на блок
        var world = new GameWorld { Name = "SelfTestNodeTooltipWorld" };
        editor.AddChild(world);
        await Frames(editor, 2);
        var source = new Construction(new VoxelGrid());
        PlaceFixture(source, "zz_seat", new Vector3I(0, 0, 0));
        PlaceFixture(source, "zz_button", new Vector3I(10, 0, 0));
        var vehicle = VehicleSpawner.Spawn(world, ConstructionIO.Serialize(source), new Vector3(0, 5, 0));
        vehicle.Freeze = true;
        await Frames(editor, 4);

        var spawnedButton = vehicle.Construction!.Instances.First(i => i.BlockSlug == "zz_button");
        var player = world.PlayerForTesting;
        var tooltip = world.NodeTooltipForTesting;
        var buttonCenter = vehicle.ToGlobal(BuildSpace.CellCenter(new Vector3I(10, 0, 0)));
        player.SetPose(buttonCenter + new Vector3(0, -1.6f, 1.0f), 0);
        await Frames(editor, 4);

        Check(tooltip.IsVisible && tooltip.Title == "zz_button", "aiming at a button in the world shows a tooltip with its name", $"visible={tooltip.IsVisible} '{tooltip.Title}'");
        Check(tooltip.Lines.Select(l => l.Label).SequenceEqual(new[] { "signal (Boolean out)", "electricity (Electricity in)" }) && tooltip.Lines[0].Value == "OFF" && tooltip.Lines[1].Value == "no power",
            "...listing its NODES with their values", string.Join("; ", tooltip.Lines.Select(l => l.Label + "=" + l.Value)));
        Check(tooltip.Lines.All(l => l.Label != "Mode"), "...and no parameters (they are for the editor)");
        vehicle.Runtime!.Interact(spawnedButton.InstanceId, BlockInteraction.Press);
        vehicle.SetDebugPowered(true);
        await PhysicsFrames(editor, 3);
        await Frames(editor, 3);
        Check(tooltip.Lines[0].Value == "ON" && tooltip.Lines[1].Value.StartsWith("powered"), "the values follow the runtime live: pressed -> ON, powered", string.Join("; ", tooltip.Lines.Select(l => l.Label + "=" + l.Value)));

        player.SetPose(buttonCenter + new Vector3(0, 20, 0), 0);
        await Frames(editor, 4);
        Check(!tooltip.IsVisible, "aiming away hides it");

        world.QueueFree();
        await Frames(editor, 2);
        editor.EditorCamera.Current = true;
    }
}
