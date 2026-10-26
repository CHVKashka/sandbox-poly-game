using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Runtime;
using SandboxPolyGame.World;

namespace SandboxPolyGame.Dev;

public sealed partial class SelfTest
{
    // Колесо-фикстура: footprint 2x3x3 (диск 3x3 + слой оси), корень = ступица (0,0,0), к раме крепится ось (1,0,0). Без модели - призрак рисуется боксом.
    private const string AttachWheelJson =
        "{ \"footprint\": [2,3,3], \"footprintMin\": [0,-1,-1], \"footprintManual\": true, \"attachCell\": [1,0,0], \"behavior\": \"Wheel\", \"collision\": [[1,0,0]], " +
        "\"ports\": [ { \"id\": \"RPS\", \"resource\": \"Torque\", \"direction\": \"In\", \"face\": \"PosX\", \"position\": [0,0] } ], " +
        "\"nodes\": [ { \"id\": \"Brake\", \"type\": \"Boolean\", \"direction\": \"In\", \"position\": [1,0,0] }, " +
        "{ \"id\": \"Variable brake\", \"type\": \"Number\", \"direction\": \"In\", \"position\": [0,0,1] }, " +
        "{ \"id\": \"Steering\", \"type\": \"Number\", \"direction\": \"In\", \"position\": [0,0,-1] } ] }";

    // ================================================================== attachCell: данные и чистая геометрия

    private void RunAttachCellGeometryTests()
    {
        GD.Print("-- attachCell: data, root from the place cell, pivot frame == placed frame for all 64 rotations");
        const float cell = BuildSpace.CellSize;

        // ---- данные
        Check(MakeFunctional("{}").AttachCell == Vector3I.Zero, "attachCell defaults to (0,0,0) - the root, so every existing block behaves exactly as before");
        var wheel = MakeFunctional(AttachWheelJson);
        Check(wheel.AttachCell == new Vector3I(1, 0, 0), "attachCell is read as written", $"{wheel.AttachCell}");

        string Error(string json)
        {
            try { MakeFunctional(json); return ""; }
            catch (Exception ex) { return ex.Message; }
        }

        Check(Error("{ \"footprint\": [2,3,3], \"footprintMin\": [0,-1,-1], \"attachCell\": [2,0,0] }").Contains("outside the footprint"), "an attachCell past the footprint's max is a data error with a clear message");
        Check(Error("{ \"footprint\": [2,3,3], \"footprintMin\": [0,-1,-1], \"attachCell\": [0,-2,0] }").Contains("attachCell"), "...and one before its min too");
        Check(Error("{ \"attachCell\": [1,0,0] }").Contains("outside the footprint"), "...and the default 1x1x1 footprint only holds (0,0,0)");
        Check(Error("{ \"attachCell\": [0,0] }").Contains("[x, y, z]"), "a malformed attachCell (not three numbers) is a data error, not a silent default");
        Check(Error("{ \"footprint\": [2,3,3], \"footprintMin\": [0,-1,-1], \"attachCell\": [1,1,1] }") == "" && Error("{ \"footprint\": [2,3,3], \"footprintMin\": [0,-1,-1], \"attachCell\": [0,-1,-1] }") == ""
              && Error("{ \"footprint\": [3,1,1], \"footprintMin\": [-2,0,0], \"attachCell\": [-2,0,0] }") == "",
            "...but every corner cell of the footprint is fine, negative indices included");

        // ---- все 64 поворота
        var min = new Vector3I(0, -1, -1);
        var size = new Vector3I(2, 3, 3);
        var attach = new Vector3I(1, 0, 0);
        var place = new Vector3I(5, -2, 7);
        bool attachOnPlace = true, insideBox = true, rootComputed = true, framesAgree = true, plainSame = true;
        string detail = "";
        for (int x = 0; x < 4; x++)
        for (int y = 0; y < 4; y++)
        for (int z = 0; z < 4; z++)
        {
            var steps = new Vector3I(x, y, z);
            var root = BlockFootprint.RootForPlaceCell(place, attach, steps);
            var (origin, boxSize) = BlockFootprint.PlaceBox(root, min, size, steps);

            rootComputed &= root == place - BlockFootprint.Rotate(attach, steps) && BlockFootprint.RootCell(origin, min, size, steps) == root;
            attachOnPlace &= root + BlockFootprint.Rotate(attach, steps) == place;
            bool inside = true;
            for (int axis = 0; axis < 3; axis++) inside &= place[axis] >= origin[axis] && place[axis] < origin[axis] + boxSize[axis];
            insideBox &= inside;

            var rotation = ShapeMeshBuilder.ComposeRotation(steps);
            bool agree = FunctionalBlockGeometry.PivotFrame(place, attach, rotation).IsEqualApprox(FunctionalBlockGeometry.InstanceFrame(origin, min, size, steps));
            if (!agree && framesAgree) detail = $"steps={steps}";
            framesAgree &= agree;

            // attachCell = 0: ровно прежнее поведение (корень = клетка под курсором, рамка = RootFrame).
            plainSame &= BlockFootprint.RootForPlaceCell(place, Vector3I.Zero, steps) == place
                         && FunctionalBlockGeometry.PivotFrame(place, Vector3I.Zero, rotation).IsEqualApprox(FunctionalBlockGeometry.RootFrame(place, rotation));
        }

        Check(attachOnPlace, "all 64 rotations: root + R * attachCell == the cell under the cursor (the attach cell lands exactly on PlaceCell)");
        Check(rootComputed, "...root = placeCell - Rotate(attachCell), and RootCell() recovers it from the placed box");
        Check(insideBox, "...and the PlaceCell lies inside the occupied box");
        Check(framesAgree, "all 64 rotations: PivotFrame(place, attach, R) == the frame of the placed instance (InstanceFrame) - the ghost and the placed block coincide", detail);
        Check(plainSame, "attachCell (0,0,0): the root is the PlaceCell and PivotFrame == RootFrame for all 64 rotations (no change for existing blocks)");

        // Центр клетки attachCell ровно в центре PlaceCell при произвольном (анимируемом) базисе.
        var arbitrary = new Basis(new Vector3(1, 2, 3).Normalized(), 0.7f);
        var pivot = FunctionalBlockGeometry.PivotFrame(place, attach, arbitrary) * ((new Vector3(attach.X, attach.Y, attach.Z) + new Vector3(0.5f, 0.5f, 0.5f)) * cell);
        Check(pivot.IsEqualApprox(BuildSpace.CellCenter(place)), "for ANY basis (the J/K/L animation) the centre of the attach cell stays in the centre of the place cell - the ghost turns around it");
    }

    // ================================================================== attachCell в редакторе построек

    private async Task RunAttachCellPlacementTests(BuildEditor editor)
    {
        GD.Print("-- editor: a block with attachCell is placed by its attach cell (tire out / tire into the frame), regression for attachCell 0, wheel hub");

        var state = editor.State;
        var camera = editor.EditorCamera;
        var construction = editor.World.Construction;
        bool accumulated = Input.UseAccumulatedInput;
        Input.UseAccumulatedInput = false;

        using var scope = UseCatalogWith(("zz_attach_wheel", AttachWheelJson), ("zz_plain_foot", "{ \"footprint\": [2,1,1], \"collision\": [[0,0,0]] }"));
        var catalog = BlockCatalog.Instance;
        var wheelDef = catalog.Get("zz_attach_wheel");
        var wheelFn = FunctionalOf(wheelDef)!;

        construction.Clear();
        state.Tool = ToolMode.None;
        state.ResetPendingRotation();
        construction.PlaceBlock(Vector3I.Zero, new Vector3I(8, 1, 8), catalog.Get("block"), Colors.White); // рама: плита 8x1x8 на y = 0
        state.SetSlot(0, "zz_attach_wheel");
        state.SelectedSlot = 0;

        camera.LookAtPoint(new Vector3(1.125f, 5f, 1.125f), new Vector3(1.125f, 0f, 1.125f));
        await Frames(editor, 2);
        var aim = camera.UnprojectPosition(new Vector3(1.125f, 0.25f, 1.125f));
        await Move(editor, aim);
        var place = new Vector3I(4, 1, 4);
        Check(editor.Hover.Found && editor.Hover.IsBlock && editor.Hover.PlaceCell == place, "setup: the cursor aims at the top face of the frame plate, place cell (4,1,4)", $"{editor.Hover}");

        // Повороты: ось колеса (локальная +X) смотрит вниз (в плиту: ступица/шина над плитой) или вверх (ступица в плите).
        bool RotateTo(Func<Vector3I, bool> wanted)
        {
            for (int rz = 0; rz < 4; rz++)
            for (int rx = 0; rx < 4; rx++)
            for (int ry = 0; ry < 4; ry++)
            {
                state.ResetPendingRotation();
                for (int i = 0; i < rz; i++) state.RotatePendingZ();
                for (int i = 0; i < rx; i++) state.RotatePendingX();
                for (int i = 0; i < ry; i++) state.RotatePendingY();
                if (wanted(state.PendingRotationSteps)) return true;
            }

            return false;
        }

        async Task Settle()
        {
            for (int i = 0; i < 60 && !editor.GhostVisualBasis.IsEqualApprox(state.PendingRotationBasis); i++) await Frames(editor, 1);
            await Frames(editor, 2);
        }

        // ---- шина наружу: ось (локальный +X) смотрит ВНИЗ, корень-ступица выше оси - диск стоит над плитой
        Check(RotateTo(steps => BlockFootprint.Rotate(Vector3I.Right, steps) == Vector3I.Down), "(setup) found a rotation that turns the axle direction downwards");
        await Settle();
        var steps1 = state.PendingRotationSteps;
        var root1 = BlockFootprint.RootForPlaceCell(place, wheelFn.AttachCell, steps1);
        var box1 = BlockFootprint.PlaceBox(root1, wheelFn.FootprintMin, wheelFn.Footprint, steps1);
        Check(root1 != place && root1 == place + Vector3I.Up, "tire out: the root (hub) is computed one cell ABOVE the cursor cell - the axle cell sits under the cursor", $"root={root1}");
        Check(editor.PlacementConflictCells.Count == 0 && !editor.ConflictOverlayVisible && editor.Ghost.Visible, "...the box is free (nothing red) and the ghost is shown", $"{editor.PlacementConflictCells.Count} blocked");
        var ghostBox = editor.Ghost.GlobalTransform * editor.Ghost.Mesh.GetAabb();
        Check(ghostBox.Position.IsEqualApprox(BuildSpace.CellMin(box1.Origin)) && ghostBox.Size.IsEqualApprox(new Vector3(box1.Size.X, box1.Size.Y, box1.Size.Z) * BuildSpace.CellSize),
            "...and the ghost fills exactly the box computed from the attach cell (PivotFrame in the ghost)", $"{ghostBox} vs {BuildSpace.CellMin(box1.Origin)}");

        // ---- шина в плиту: ось смотрит ВВЕРХ, ступица в плите - красные клетки, призрак на месте, ЛКМ ничего не ставит
        Check(RotateTo(steps => BlockFootprint.Rotate(Vector3I.Right, steps) == Vector3I.Up), "(setup) found a rotation that turns the axle direction upwards");
        await Settle();
        var steps2 = state.PendingRotationSteps;
        var box2 = BlockFootprint.PlaceBox(BlockFootprint.RootForPlaceCell(place, wheelFn.AttachCell, steps2), wheelFn.FootprintMin, wheelFn.Footprint, steps2);
        Check(editor.PlacementConflictCells.Count > 0 && editor.ConflictOverlayVisible && editor.Ghost.Visible, "tire into the frame: the disc overlaps the plate - red cells, the ghost stays visible", $"{editor.PlacementConflictCells.Count} blocked");
        bool allInsidePlate = editor.PlacementConflictCells.All(c => c.Y == 0 && c.X >= box2.Origin.X && c.X < box2.Origin.X + box2.Size.X);
        Check(allInsidePlate && editor.PlacementConflictCells.Count == 9, "...exactly the 9 plate cells under the disc are blocked", string.Join(",", editor.PlacementConflictCells));
        int before = construction.Instances.Count;
        await Click(editor, aim, MouseButton.Left);
        Check(construction.Instances.Count == before, "LMB on the conflicting position places nothing");

        // ---- поставить в свободной ориентации
        RotateTo(steps => BlockFootprint.Rotate(Vector3I.Right, steps) == Vector3I.Down);
        await Settle();
        await Click(editor, aim, MouseButton.Left);
        var placed = construction.Instances.FirstOrDefault(i => i.BlockSlug == "zz_attach_wheel");
        Check(placed != null && placed.Origin == box1.Origin && placed.Size == box1.Size && placed.RotationSteps == steps1,
            "LMB places the block into the box computed from the attach cell, with its rotation (the stored data is the ordinary origin/size/rotation)", placed == null ? "nothing placed" : $"{placed.Origin} {placed.Size}");
        if (placed != null)
        {
            var root = BlockFootprint.RootCell(placed.Origin, wheelFn.FootprintMin, wheelFn.Footprint, placed.RotationSteps);
            var axle = root + BlockFootprint.Rotate(wheelFn.AttachCell, placed.RotationSteps);
            Check(axle == place && construction.Grid.IsSolid(axle + Vector3I.Down) && construction.GetOwner(axle) == placed,
                "...the axle cell is the cursor cell and it borders the plate (the tire is outside, the axle hangs on the frame)", $"axle={axle}");

            // Физика колеса: ступица - центр корневой клетки (корень вычислен, а не равен клетке под курсором).
            var frame = FunctionalBlockGeometry.InstanceFrame(placed.Origin, wheelFn.FootprintMin, wheelFn.Footprint, placed.RotationSteps);
            var hubLocal = FunctionalBlockGeometry.ComputeNodeAnchor(Vector3I.Zero, wheelFn.FootprintMin, wheelFn.Footprint, BuildSpace.CellSize);
            Check(root != place && (frame * hubLocal).IsEqualApprox(BuildSpace.CellCenter(root)) && !(frame * hubLocal).IsEqualApprox(BuildSpace.CellCenter(place)),
                "wheel hub: the centre of the (computed) root cell, one cell away from the cursor cell - the physics takes it from there", $"{frame * hubLocal}");

            var parent = new Node3D();
            editor.AddChild(parent);
            var vehicle = VehicleSpawner.Spawn(parent, ConstructionIO.Serialize(construction), new Vector3(0, 30, 0));
            await PhysicsFrames(editor, 3);
            Check(vehicle.Wheels is { WheelCount: 1 }, "a vehicle spawned from the construction has exactly one wheel in the wheel physics", $"{vehicle.Wheels?.WheelCount}");
            parent.QueueFree();
        }

        // ---- регрессия: блок без attachCell ставится ровно как раньше (корень под курсором)
        construction.Clear();
        construction.PlaceBlock(Vector3I.Zero, new Vector3I(8, 1, 8), catalog.Get("block"), Colors.White);
        state.SetSlot(0, "zz_plain_foot");
        state.ResetPendingRotation();
        await Settle();
        await Click(editor, aim, MouseButton.Left);
        var plain = construction.Instances.FirstOrDefault(i => i.BlockSlug == "zz_plain_foot");
        var plainBox = BlockFootprint.PlaceBox(place, Vector3I.Zero, new Vector3I(2, 1, 1), state.PendingRotationSteps);
        Check(plain != null && plain.Origin == plainBox.Origin && plain.Size == plainBox.Size && plain.Origin == place,
            "regression: a block without attachCell is placed with its root on the cursor cell, exactly as before", plain == null ? "nothing placed" : $"{plain.Origin}");

        state.SetSlot(0, "block");
        state.ResetPendingRotation();
        construction.Clear();
        Input.UseAccumulatedInput = accumulated;
        await Frames(editor, 1);
    }

    // ================================================================== attachCell в редакторе блоков

    private async Task RunBlockEditorAttachCellTests(BuildEditor editor)
    {
        GD.Print("-- block editor: Attach cell field, marker, XML only when non-zero, round-trip, Save refuses a cell outside the footprint, Undo");

        const string dir = "user://selftest_blockeditor_attach";
        const string slug = "selftest_attach_block";
        const float cell = BuildSpace.CellSize;
        DirAccess.MakeDirRecursiveAbsolute(dir);
        string path = $"{dir}/{slug}.xml";
        if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);

        var tool = new BlockEditor { BlocksDirectory = dir };
        editor.AddChild(tool);
        await Frames(editor, 2);
        tool.ResetToDefaults(slug);

        Check(tool.AttachCell == Vector3I.Zero && !tool.BuildXml(slug).Contains("attachCell"), "a new block: attach cell (0,0,0), and the XML has no attachCell key (old files stay untouched)");

        tool.Ui.SetFootprint(true, new Vector3I(0, -1, -1), new Vector3I(2, 3, 3));
        tool.Ui.SetAttachCell(new Vector3I(1, 0, 0));
        tool.RefreshPreview();
        Check(tool.AttachCell == new Vector3I(1, 0, 0) && tool.AttachCellInsideFootprint, "attach cell (1,0,0) inside the manual footprint 2x3x3");
        Check(tool.AttachMarkerPosition.DistanceTo(new Vector3(cell * 0.98, -cell * 0.02, -cell * 0.02)) < 1e-6, "the yellow marker wraps the attach cell", $"{tool.AttachMarkerPosition}");
        string xml = tool.BuildXml(slug);
        Check(xml.Contains("\"attachCell\": [1, 0, 0]") && xml.Contains("\"footprintManual\": true"), "the XML carries attachCell when it is not (0,0,0)");

        tool.Save();
        Check(FileAccess.FileExists(path), "Save writes the block");
        var saved = BlockCatalog.Load(dir);
        var savedFn = saved.TryGetBySlug(slug, out var savedDef) ? FunctionalOf(savedDef) : null;
        Check(savedFn != null && savedFn.AttachCell == new Vector3I(1, 0, 0), "the game's catalog reads the saved attachCell");

        tool.ResetToDefaults("");
        tool.LoadSlug(slug);
        Check(tool.AttachCell == new Vector3I(1, 0, 0) && tool.AttachCellInsideFootprint, "round-trip: the editor reopens the block with its attach cell");

        // Undo: поле входит в снэпшот.
        tool.AddNode(); // точка Undo со снэпшотом, где attach = (1,0,0)
        tool.Ui.SetAttachCell(Vector3I.Zero);
        tool.RefreshPreview();
        Check(tool.AttachCell == Vector3I.Zero, "(setup) the attach cell was changed after the undo point");
        tool.Undo();
        Check(tool.AttachCell == new Vector3I(1, 0, 0), "Ctrl+Z restores the attach cell (it is part of the undo snapshot)", $"{tool.AttachCell}");

        // Save отказывает, если клетка вне footprint (в том числе после уменьшения объёма вручную).
        DirAccess.RemoveAbsolute(path);
        tool.Ui.SetAttachCell(new Vector3I(5, 0, 0));
        tool.RefreshPreview();
        tool.Save();
        Check(!FileAccess.FileExists(path) && tool.Ui.Status.Contains("attach cell"), "Save refuses an attach cell outside the footprint and says why, nothing is written", tool.Ui.Status);

        tool.Ui.SetAttachCell(new Vector3I(1, 0, 0));
        tool.Ui.SetFootprint(true, Vector3I.Zero, Vector3I.One); // объём уменьшили вручную, клетка (1,0,0) осталась снаружи
        tool.RefreshPreview();
        Check(!tool.AttachCellInsideFootprint, "shrinking the manual volume leaves the attach cell outside");
        tool.Save();
        Check(!FileAccess.FileExists(path) && tool.Ui.Status.Contains("attach cell"), "...and Save refuses that too");

        tool.Ui.SetFootprint(true, new Vector3I(0, -1, -1), new Vector3I(2, 3, 3));
        tool.RefreshPreview();
        tool.Save();
        Check(FileAccess.FileExists(path), "with the attach cell back inside the footprint Save works again");

        if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
        tool.QueueFree();
        await Frames(editor, 1);
    }
}
