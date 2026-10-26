using System;
using System.Collections.Generic;
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
    // Колесо, у которого порт вала смотрит вниз (NegY): мотор ставится под ним на оси вращения; ноды как у настоящего.
    private const string MirrorWheelJson =
        "{ \"footprint\": [1,3,3], \"footprintMin\": [0,-1,-1], \"footprintManual\": true, \"behavior\": \"Wheel\", " +
        "\"ports\": [ { \"id\": \"RPS\", \"resource\": \"Torque\", \"direction\": \"In\", \"face\": \"NegY\", \"position\": [0,0] } ], " +
        "\"nodes\": [ { \"id\": \"Brake\", \"type\": \"Boolean\", \"direction\": \"In\", \"position\": [0,0,0] }, " +
        "{ \"id\": \"Variable brake\", \"type\": \"Number\", \"direction\": \"In\", \"position\": [0,0,1] }, " +
        "{ \"id\": \"Steering\", \"type\": \"Number\", \"direction\": \"In\", \"position\": [0,0,-1] } ] }";

    private static BlockInstance PlaceMirrored(Construction construction, string slug, Vector3I cell, Vector3I mirror)
    {
        var definition = BlockCatalog.Instance.Get(slug);
        var fn = definition.GetComponent<FunctionalBlockComponent>()!;
        var (origin, size) = BlockFootprint.PlaceBox(cell, fn.FootprintMin, fn.Footprint, Vector3I.Zero);
        return construction.PlaceBlock(origin, size, definition, Colors.White, Vector3I.Zero, mirror)!;
    }

    /// <summary>Ожидаемое отражение указателей, посчитанное НЕЗАВИСИМО от кода: Translate(c) · Scale(sx,1,sz) · Translate(−c), c — центр клетки (0,0,0) в рамке блока.</summary>
    private static Transform3D ExpectedDecorMirror(Vector3I mirror)
    {
        float half = BuildSpace.CellSize * 0.5f;
        var center = new Vector3(half, half, half);
        var scale = new Vector3(mirror.X != 0 ? -1 : 1, 1, mirror.Z != 0 ? -1 : 1);
        return new Transform3D(Basis.Identity, center) * new Transform3D(Basis.FromScale(scale), Vector3.Zero) * new Transform3D(Basis.Identity, -center);
    }

    // ================================================================== поведение колеса при отражении (чистая логика)

    private void RunWheelMirrorBehaviorTests()
    {
        GD.Print("-- wheel mirror: Z flips the rolling direction, X flips the steering side, Y is ignored; only behaviors that declare support");

        BlockBehaviorRegistry.TryGet(WheelBehavior.Key, out var behavior);
        var definition = MakeFunctional("{ \"behavior\": \"Wheel\" }");
        var reverseSchema = ParseSchema("{ \"parameters\": [ { \"id\": \"reverse\", \"type\": \"Bool\" } ] }");

        WheelState Make(bool reverse, Vector3I mirror)
        {
            var set = new ParameterSet(reverseSchema, new Dictionary<string, string> { ["reverse"] = reverse ? "true" : "false" });
            var state = (WheelState)behavior.CreateState(definition, set);
            behavior.SetMirror(state, mirror);
            return state;
        }

        double OmegaSign(WheelState state)
        {
            behavior.SetTorqueNetwork(state, true, 600);
            return Math.Sign(state.NetworkOmega);
        }

        double SteerSign(WheelState state)
        {
            behavior.SetInput(state, "Steering", new NodeValue(1));
            for (int i = 0; i < 10; i++) behavior.Tick(state, 0.016);
            return Math.Sign(state.SteerAngle);
        }

        Check(behavior.SupportsMirror, "the wheel declares support for mirroring");
        foreach (string key in new[] { ButtonBehavior.Key, BatteryBehavior.Key, ElectricMotorBehavior.Key, PilotSeatBehavior.Key, FunctionBehavior.Key })
        {
            BlockBehaviorRegistry.TryGet(key, out var other);
            Check(!other.SupportsMirror, $"{key}: mirroring is not supported (ignored, as before)");
        }

        Check(OmegaSign(Make(false, Vector3I.Zero)) == 1, "no mirror: positive rpm turns the wheel positively");
        Check(OmegaSign(Make(false, new Vector3I(0, 0, 1))) == -1, "mirror Z (O): the rolling direction is reversed - the sign of the network rotation flips");
        Check(OmegaSign(Make(true, Vector3I.Zero)) == -1, "(control) the reverse parameter alone flips it too");
        Check(OmegaSign(Make(true, new Vector3I(0, 0, 1))) == 1, "reverse + mirror Z cancel out: the original direction (reverse XOR mirror Z)");
        Check(OmegaSign(Make(false, new Vector3I(1, 0, 0))) == 1 && OmegaSign(Make(false, new Vector3I(0, 1, 0))) == 1, "mirror X and Y do not touch the rolling direction");

        Check(SteerSign(Make(false, Vector3I.Zero)) == 1, "no mirror: steering +1 turns the wheel to the positive side");
        Check(SteerSign(Make(false, new Vector3I(1, 0, 0))) == -1, "mirror X (U): the steering side is reversed - the same signal turns the wheel the other way");
        Check(SteerSign(Make(false, new Vector3I(0, 0, 1))) == 1, "mirror Z does not touch the steering side");
        Check(SteerSign(Make(true, new Vector3I(1, 0, 0))) == -1, "the reverse parameter does not touch the steering side either (only mirror X does)");

        var y = Make(false, new Vector3I(0, 1, 0));
        Check(OmegaSign(y) == 1 && SteerSign(y) == 1 && !y.MirrorForward && !y.MirrorSteer, "mirror Y (I) changes nothing for the wheel (the rays go along the local -Y)");
        var both = Make(false, new Vector3I(1, 0, 1));
        Check(OmegaSign(both) == -1 && SteerSign(both) == -1 && both.MirrorForward && both.MirrorSteer, "mirror X+Z combines: rolling and steering sides both reversed");

        Check(Make(false, new Vector3I(1, 1, 1)).CaptureNet().Length == 5, "the mirror flags are instance constants - the replicated state (CaptureNet) is unchanged: 5 numbers");
    }

    // ================================================================== рантайм: колесо на сети вращения, сохранение отражения

    private void RunWheelMirrorRuntimeTests()
    {
        GD.Print("-- wheel mirror: through the runtime (motor -> shaft network -> wheel), and the mirror survives save/load");

        using var scope = UseCatalogWith(
            new FixtureBlock("zz_seat", SeatJson(), SeatParameters()),
            new FixtureBlock("zz_motor", MotorJson, MotorParameters),
            new FixtureBlock("zz_mwheel", MirrorWheelJson));
        var construction = new Construction(new VoxelGrid());
        var seat = PlaceFixture(construction, "zz_seat", new Vector3I(0, 20, 0));

        // Четыре колеса, каждое на своём моторе (мотор под колесом, ось вращения вертикальна): без отражения, Z, X, Y.
        var mirrors = new[] { Vector3I.Zero, new Vector3I(0, 0, 1), new Vector3I(1, 0, 0), new Vector3I(0, 1, 0) };
        var wheels = new List<BlockInstance>();
        for (int i = 0; i < mirrors.Length; i++)
        {
            var motor = PlaceFixture(construction, "zz_motor", new Vector3I(10 + i * 10, -2, 0));
            wheels.Add(PlaceMirrored(construction, "zz_mwheel", new Vector3I(10 + i * 10, 0, 0), mirrors[i]));
            Check(construction.TryConnect(seat.InstanceId, "ws_out", motor.InstanceId, "sig", out _), $"(setup) throttle wired to the motor of wheel #{i}");
        }

        void Drive(FunctionalBlockRuntime runtime, int seatId)
        {
            runtime.DebugForcePowered = true;
            var seatState = runtime.GetState<PilotSeatState>(seatId)!;
            seatState.Modes[1] = SeatAxisMode.Sticky;
            seatState.AxisValue[1] = 1.0;
            for (int tick = 0; tick < 40; tick++) runtime.Tick(0.016);
        }

        double[] OmegaOf(FunctionalBlockRuntime runtime, Construction source) =>
            wheels.Select(w => runtime.GetState<WheelState>(source.Instances.First(i => i.Origin == w.Origin).InstanceId)!.Omega).ToArray();

        using var runtimeA = new FunctionalBlockRuntime(construction, BlockCatalog.Instance);
        Drive(runtimeA, seat.InstanceId);
        var omega = OmegaOf(runtimeA, construction);
        Check(wheels.All(w => runtimeA.GetState<WheelState>(w.InstanceId)!.InNetwork), "every wheel is on a rotation network with its motor");
        Check(omega[0] > 1 && omega[1] < -1 && Math.Abs(omega[0] + omega[1]) < 1e-6, "mirror Z: the wheel turns the OPPOSITE way to the unmirrored one at the same throttle (same speed, opposite sign)", string.Join(", ", omega));
        Check(omega[2] > 1 && Math.Abs(omega[2] - omega[0]) < 1e-6 && omega[3] > 1 && Math.Abs(omega[3] - omega[0]) < 1e-6, "mirror X and mirror Y leave the rolling direction as it was");
        Check(wheels.Select(w => w.Mirror).SequenceEqual(mirrors), "(setup) the instances carry their mirror");

        // Сохранение: отражение переживает запись и чтение, поведение после загрузки то же.
        string json = ConstructionIO.Serialize(construction);
        var loaded = new Construction(new VoxelGrid());
        ConstructionIO.Deserialize(loaded, json, BlockCatalog.Instance);
        var loadedWheels = wheels.Select(w => loaded.Instances.First(i => i.Origin == w.Origin)).ToList();
        Check(loadedWheels.Select(w => w.Mirror).SequenceEqual(mirrors), "the mirror is saved with the construction and read back (the format is unchanged)", string.Join(" ", loadedWheels.Select(w => w.Mirror)));
        using var runtimeB = new FunctionalBlockRuntime(loaded, BlockCatalog.Instance);
        Drive(runtimeB, loaded.Instances.First(i => i.BlockSlug == "zz_seat").InstanceId);
        var loadedOmega = OmegaOf(runtimeB, loaded);
        Check(loadedOmega[0] > 1 && loadedOmega[1] < -1 && Math.Abs(loadedOmega[1] - omega[1]) < 1e-6, "after loading, the mirrored wheel still turns the opposite way", string.Join(", ", loadedOmega));

        // Состояние пересоздаётся при смене отражения экземпляра (Undo/загрузка пересоздают экземпляры).
        var instanceB = loaded.Instances.First(i => i.Origin == wheels[1].Origin);
        var stateBefore = runtimeB.GetState<WheelState>(instanceB.InstanceId)!;
        instanceB.Mirror = Vector3I.Zero;
        runtimeB.Sync();
        var stateAfter = runtimeB.GetState<WheelState>(instanceB.InstanceId)!;
        Check(!ReferenceEquals(stateBefore, stateAfter) && !stateAfter.MirrorForward, "changing an instance's mirror rebuilds its state (the key includes the mirror)");
    }

    // ================================================================== указатели и призрак при отражении

    private async Task RunDecorMirrorTests(BuildEditor editor)
    {
        GD.Print("-- decor mirror: placed pointers and the ghost's pointers are mirrored (X and Z) for a wheel, ignored for blocks that do not support it");

        const string modelPath = "res://meshes/selftest_mirror_arrow.gltf";
        WriteTestGltf(modelPath, (new Vector3(-1, -1, -1), new Vector3(1, 1, 1)));
        FunctionalBlockGeometry.ClearSceneCache(modelPath);

        string Decor(string behavior) =>
            "{ \"footprint\": [1,1,1], \"behavior\": \"" + behavior + "\", \"decor\": [ " +
            "{ \"scene\": \"" + modelPath + "\", \"position\": [0.2, 0.05, 0.1], \"rotation\": [0, 30, 0], \"show\": \"Always\" }, " +
            "{ \"scene\": \"" + modelPath + "\", \"position\": [0.05, 0.2, 0.2], \"show\": \"Ghost\" } ] }";

        using var scope = UseCatalogWith(("zz_decor_wheel", Decor("Wheel")), ("zz_decor_button", Decor("Button")));
        var catalog = BlockCatalog.Instance;
        var wheelFn = FunctionalOf(catalog.Get("zz_decor_wheel"))!;
        var buttonFn = FunctionalOf(catalog.Get("zz_decor_button"))!;

        // ---- поставленные блоки (режим Always)
        var construction = new Construction(new VoxelGrid());
        var view = new FunctionalBlockView { ShowDecor = true };
        editor.AddChild(view);
        var wheelPlain = PlaceMirrored(construction, "zz_decor_wheel", new Vector3I(0, 0, 0), Vector3I.Zero);
        var wheelXZ = PlaceMirrored(construction, "zz_decor_wheel", new Vector3I(3, 0, 0), new Vector3I(1, 0, 1));
        var wheelY = PlaceMirrored(construction, "zz_decor_wheel", new Vector3I(6, 0, 0), new Vector3I(0, 1, 0));
        var wheelX = PlaceMirrored(construction, "zz_decor_wheel", new Vector3I(9, 0, 0), new Vector3I(1, 0, 0));
        var wheelZ = PlaceMirrored(construction, "zz_decor_wheel", new Vector3I(12, 0, 0), new Vector3I(0, 0, 1));
        var buttonXZ = PlaceMirrored(construction, "zz_decor_button", new Vector3I(15, 0, 0), new Vector3I(1, 0, 1));
        var buttonPlain = PlaceMirrored(construction, "zz_decor_button", new Vector3I(18, 0, 0), Vector3I.Zero);
        view.Sync(construction);

        Transform3D Frame(BlockInstance instance, FunctionalBlockComponent fn) => FunctionalBlockGeometry.InstanceFrame(instance.Origin, fn.FootprintMin, fn.Footprint, instance.RotationSteps);
        var local = wheelFn.Decor[0].LocalTransform;

        Check(view.DecorCount(wheelXZ.InstanceId) == 1 && view.DecorTransform(wheelXZ.InstanceId, 0).IsEqualApprox(Frame(wheelXZ, wheelFn) * ExpectedDecorMirror(new Vector3I(1, 0, 1)) * local),
            "wheel, mirror X+Z: the pointer stands at frame * M * local (M mirrors about the centre of the root cell)");
        Check(view.DecorTransform(wheelX.InstanceId, 0).IsEqualApprox(Frame(wheelX, wheelFn) * ExpectedDecorMirror(new Vector3I(1, 0, 0)) * local)
              && view.DecorTransform(wheelZ.InstanceId, 0).IsEqualApprox(Frame(wheelZ, wheelFn) * ExpectedDecorMirror(new Vector3I(0, 0, 1)) * local), "mirror X alone and mirror Z alone each mirror their own axis");
        double determinantX = view.DecorTransform(wheelX.InstanceId, 0).Basis.Determinant(), determinantZ = view.DecorTransform(wheelZ.InstanceId, 0).Basis.Determinant(), determinantXZ = view.DecorTransform(wheelXZ.InstanceId, 0).Basis.Determinant();
        Check(determinantX < 0 && determinantZ < 0 && determinantXZ > 0, "a single-axis mirror flips the basis (negative determinant - the engine turns the face winding itself); X+Z together is a 180 degree turn about the vertical (positive determinant)",
            $"X {determinantX:0.###} Z {determinantZ:0.###} XZ {determinantXZ:0.###}");
        Check(!view.DecorTransform(wheelXZ.InstanceId, 0).IsEqualApprox(view.DecorTransform(wheelPlain.InstanceId, 0) * Transform3D.Identity), "(control) the mirrored pointer really stands elsewhere than the unmirrored one's frame-relative pose");
        Check(view.DecorTransform(wheelY.InstanceId, 0).IsEqualApprox(Frame(wheelY, wheelFn) * local) && view.DecorTransform(wheelPlain.InstanceId, 0).IsEqualApprox(Frame(wheelPlain, wheelFn) * local),
            "mirror Y is ignored for the wheel, including its pointers (they stand exactly as without a mirror)");
        Check(view.DecorTransform(buttonXZ.InstanceId, 0).IsEqualApprox(Frame(buttonXZ, buttonFn) * buttonFn.Decor[0].LocalTransform) && view.DecorTransform(buttonPlain.InstanceId, 0).IsEqualApprox(Frame(buttonPlain, buttonFn) * buttonFn.Decor[0].LocalTransform),
            "a block whose behavior does not support mirroring ignores it: the pointer stands as before");
        view.QueueFree();

        // ---- призрак в редакторе построек: настоящие клавиши U / I / O
        var state = editor.State;
        var camera = editor.EditorCamera;
        var editorConstruction = editor.World.Construction;
        bool accumulated = Input.UseAccumulatedInput;
        Input.UseAccumulatedInput = false;
        editorConstruction.Clear();
        state.Tool = ToolMode.None;
        state.ResetPendingRotation();
        while (state.PendingMirror.X != 0) state.ToggleMirrorX();
        while (state.PendingMirror.Y != 0) state.ToggleMirrorY();
        while (state.PendingMirror.Z != 0) state.ToggleMirrorZ();
        editorConstruction.PlaceBlock(Vector3I.Zero, new Vector3I(8, 1, 8), catalog.Get("block"), Colors.White);
        state.SetSlot(0, "zz_decor_wheel");
        state.SelectedSlot = 0;
        camera.LookAtPoint(new Vector3(1.125f, 5f, 1.125f), new Vector3(1.125f, 0f, 1.125f));
        await Frames(editor, 2);
        var aim = camera.UnprojectPosition(new Vector3(1.125f, 0.25f, 1.125f));
        await Move(editor, aim);
        editor.GetViewport().GuiReleaseFocus();
        await Frames(editor, 2);
        var place = new Vector3I(4, 1, 4);

        Transform3D GhostExpected(int index, Vector3I mirror) =>
            FunctionalBlockGeometry.PivotFrame(place, wheelFn.AttachCell, editor.GhostVisualBasis) * ExpectedDecorMirror(mirror) * wheelFn.Decor[index].LocalTransform;

        var ghostBox = editor.Ghost.GlobalTransform * editor.Ghost.Mesh.GetAabb();
        int blockedBefore = editor.PlacementConflictCells.Count;
        Check(editor.Hover.PlaceCell == place && editor.GhostDecorVisibleCount == 2 && editor.GhostDecorTransform(0).IsEqualApprox(GhostExpected(0, Vector3I.Zero)) && editor.GhostDecorTransform(1).IsEqualApprox(GhostExpected(1, Vector3I.Zero)),
            "(setup) the wheel's ghost shows both pointers unmirrored", $"{editor.GhostDecorVisibleCount}");

        await PressKey(editor, Godot.Key.U);
        await Frames(editor, 2);
        Check(state.PendingMirror == new Vector3I(1, 0, 0) && editor.GhostDecorTransform(0).IsEqualApprox(GhostExpected(0, new Vector3I(1, 0, 0))) && editor.GhostDecorTransform(1).IsEqualApprox(GhostExpected(1, new Vector3I(1, 0, 0))),
            "U: both ghost pointers are mirrored about X (PivotFrame * M * LocalTransform)");
        await PressKey(editor, Godot.Key.I);
        await Frames(editor, 2);
        Check(state.PendingMirror == new Vector3I(1, 1, 0) && editor.GhostDecorTransform(0).IsEqualApprox(GhostExpected(0, new Vector3I(1, 0, 0))), "I: the wheel ignores the Y mirror - the pointers do not move");
        await PressKey(editor, Godot.Key.O);
        await Frames(editor, 2);
        Check(state.PendingMirror == new Vector3I(1, 1, 1) && editor.GhostDecorTransform(1).IsEqualApprox(GhostExpected(1, new Vector3I(1, 0, 1))), "O: now mirrored about X and Z");
        await PressKey(editor, Godot.Key.U);
        await Frames(editor, 2);
        Check(editor.GhostDecorTransform(0).IsEqualApprox(GhostExpected(0, new Vector3I(0, 0, 1))), "U again: back to Z only");

        var ghostBoxAfter = editor.Ghost.GlobalTransform * editor.Ghost.Mesh.GetAabb();
        Check(ghostBoxAfter.IsEqualApprox(ghostBox) && editor.PlacementConflictCells.Count == blockedBefore, "the ghost's box and the conflict cells do not depend on the mirror (the occupied cells are not mirrored)");

        // Поставить с отражением: экземпляр хранит его, поставленный указатель (Always) отражён, бокс обычный.
        await Click(editor, aim, MouseButton.Left);
        var placed = editorConstruction.Instances.FirstOrDefault(i => i.BlockSlug == "zz_decor_wheel");
        Check(placed != null && placed.Mirror == state.PendingMirror, "the placed wheel stores the mirror of the moment (the data and the format are the ordinary ones)", placed == null ? "nothing placed" : $"{placed.Mirror}");
        if (placed != null)
        {
            var expectedBox = BlockFootprint.PlaceBox(BlockFootprint.RootForPlaceCell(place, wheelFn.AttachCell, placed.RotationSteps), wheelFn.FootprintMin, wheelFn.Footprint, placed.RotationSteps);
            Check(placed.Origin == expectedBox.Origin && placed.Size == expectedBox.Size, "...and it occupies the same box as without the mirror");
            Check(editor.World.FunctionalBlocks.DecorTransform(placed.InstanceId, 0).IsEqualApprox(Frame(placed, wheelFn) * ExpectedDecorMirror(placed.Mirror) * wheelFn.Decor[0].LocalTransform),
                "...and its Always pointer in the editor world is mirrored the same way");
        }

        // ---- блок без поддержки отражения: призрак его игнорирует
        editorConstruction.Clear();
        state.SetSlot(0, "zz_decor_button");
        await Frames(editor, 3);
        var buttonExpected = FunctionalBlockGeometry.PivotFrame(place, buttonFn.AttachCell, editor.GhostVisualBasis) * buttonFn.Decor[0].LocalTransform;
        editorConstruction.PlaceBlock(Vector3I.Zero, new Vector3I(8, 1, 8), catalog.Get("block"), Colors.White);
        await Frames(editor, 3);
        Check(state.PendingMirror != Vector3I.Zero && editor.GhostDecorVisibleCount == 2 && editor.GhostDecorTransform(0).IsEqualApprox(buttonExpected),
            "regression: for a block that does not support mirroring the ghost pointers ignore the pending mirror", $"{editor.GhostDecorVisibleCount}");

        state.SetSlot(0, "block");
        while (state.PendingMirror.X != 0) state.ToggleMirrorX();
        while (state.PendingMirror.Y != 0) state.ToggleMirrorY();
        while (state.PendingMirror.Z != 0) state.ToggleMirrorZ();
        editorConstruction.Clear();
        Input.UseAccumulatedInput = accumulated;
        if (FileAccess.FileExists(modelPath)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(modelPath));
        FunctionalBlockGeometry.ClearSceneCache(modelPath);
        await Frames(editor, 2);
    }
}
