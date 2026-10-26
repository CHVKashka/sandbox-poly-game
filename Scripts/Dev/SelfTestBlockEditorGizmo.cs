using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;

namespace SandboxPolyGame.Dev;

public sealed partial class SelfTest
{
    // ================================================================== поворот модели и ручной объём блока (чистая математика)

    private void RunModelRotationAndVolumeLayoutTests()
    {
        GD.Print("-- model rotation and manual volume: rotated bbox/anchor math, Euler round-trip, footprint face moves, format keys");
        const float cell = BuildSpace.CellSize;
        var model = new Aabb(new Vector3(-2, -1, -1), new Vector3(4, 2, 2)); // 4x2x2 ед. с началом координат в центре
        var scale = BlockModelLayout.DefaultScaleVector;

        var someAnchor = new Vector3(1, 0, 0.5f);
        Check(BlockModelLayout.ModelTransform(model, scale, someAnchor, Basis.Identity).IsEqualApprox(BlockModelLayout.ModelTransform(model, scale, someAnchor)),
            "no rotation: the rotated overload is exactly the old transform (existing blocks look the same)");
        Check(BlockModelLayout.BoundsInBlockFrame(model, scale, someAnchor, Basis.Identity).IsEqualApprox(BlockModelLayout.BoundsInBlockFrame(model, scale, someAnchor)),
            "...and the same bounds");

        // 90 градусов вокруг Y: модель 2x1x1 клеток становится 1x1x2, угол bbox по-прежнему в (0,0,0).
        var yaw90 = BlockModelLayout.RotationBasis(new Vector3(0, 90, 0));
        var turned = BlockModelLayout.BoundsInBlockFrame(model, scale, Vector3.Zero, yaw90);
        Check(turned.Size.IsEqualApprox(new Vector3(cell, cell, 2 * cell)) && turned.Position.IsEqualApprox(Vector3.Zero),
            "a 2x1x1-cell model turned 90 degrees about Y is a 1x1x2 bbox with its min corner still on (0,0,0)", $"{turned}");
        var (turnedMin, turnedSize) = BlockModelLayout.ComputeFootprint(turned);
        Check(turnedMin == Vector3I.Zero && turnedSize == new Vector3I(1, 1, 2), "...so the automatic footprint is 1x1x2", $"{turnedMin} {turnedSize}");

        // Якорь стоит на месте при любом повороте, а вся модель лежит внутри рамки.
        bool anchorsPinned = true, insideBounds = true;
        var rotations = new[] { new Vector3(30, 45, 60), new Vector3(0, 90, 0), new Vector3(90, 0, 0), new Vector3(-20, 135, 15), new Vector3(0, 0, 90) };
        foreach (var degrees in rotations)
        {
            var rotation = BlockModelLayout.RotationBasis(degrees);
            foreach (float fx in new[] { 0f, 0.5f, 1f })
            foreach (float fy in new[] { 0f, 0.5f, 1f })
            foreach (float fz in new[] { 0f, 0.5f, 1f })
            {
                var anchor = new Vector3(fx, fy, fz);
                var bounds = BlockModelLayout.BoundsInBlockFrame(model, scale, anchor, rotation);
                anchorsPinned &= BlockModelLayout.PointOnBounds(bounds, anchor).IsEqualApprox(BlockModelLayout.AnchorTarget(anchor));

                var transform = BlockModelLayout.ModelTransform(model, scale, anchor, rotation);
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = transform * (model.Position + new Vector3((corner & 1) != 0 ? model.Size.X : 0, (corner & 2) != 0 ? model.Size.Y : 0, (corner & 4) != 0 ? model.Size.Z : 0));
                    insideBounds &= bounds.Grow(1e-4f).HasPoint(point);
                }
            }
        }

        Check(anchorsPinned, "for 5 rotations x 27 anchors: the chosen bbox point of the ROTATED model lands exactly on that point of cell (0,0,0) (the anchor stays put)");
        Check(insideBounds, "...and every corner of the transformed model lies inside the reported bounds");

        // Углы Эйлера <-> матрица.
        bool roundTrip = true;
        foreach (var degrees in new[] { Vector3.Zero, new Vector3(30, 45, 60), new Vector3(90, 0, 0), new Vector3(0, -90, 0), new Vector3(15, 15, 15), new Vector3(-45, 120, 10) })
        {
            var original = BlockModelLayout.RotationBasis(degrees);
            var back = BlockModelLayout.RotationBasis(BlockModelLayout.RotationDegrees(original));
            roundTrip &= (original.Inverse() * back).GetRotationQuaternion().GetAngle() < 0.001;
        }

        Check(roundTrip, "RotationDegrees(RotationBasis(angles)) describes the same rotation (to 0.06 degrees) - the gizmo can write its result into the degree fields");
        var yawDegrees = BlockModelLayout.RotationDegrees(yaw90);
        Check(Math.Abs(yawDegrees.X) < 1e-3 && Math.Abs(yawDegrees.Y - 90) < 1e-3 && Math.Abs(yawDegrees.Z) < 1e-3 && yawDegrees.X == 0 && yawDegrees.Z == 0,
            "a pure 90 degree turn comes back as exactly (0, 90, 0) - clean numbers in the file, no -0 and no float noise", $"{yawDegrees}");

        // Ручной объём.
        var (m1, s1) = BlockModelLayout.SanitizeFootprint(new Vector3I(5, 0, 0), new Vector3I(2, 1, 1));
        Check(m1 == new Vector3I(0, 0, 0) && s1 == new Vector3I(7, 1, 1), "SanitizeFootprint: a box that does not contain the root cell is stretched to it (5..6 -> 0..6)", $"{m1} {s1}");
        var (m2, s2) = BlockModelLayout.SanitizeFootprint(new Vector3I(-5, 0, 0), new Vector3I(2, 1, 1));
        Check(m2 == new Vector3I(-5, 0, 0) && s2 == new Vector3I(6, 1, 1), "...and a box entirely on the negative side is stretched up to the root (-5..-4 -> -5..0)", $"{m2} {s2}");
        var (m3, s3) = BlockModelLayout.SanitizeFootprint(Vector3I.Zero, new Vector3I(0, -3, 2));
        Check(m3 == Vector3I.Zero && s3 == new Vector3I(1, 1, 2), "...and a size below 1 becomes 1", $"{m3} {s3}");

        var (a1, b1) = BlockModelLayout.MoveFootprintFace(Vector3I.Zero, Vector3I.One, 0, true, 2);
        Check(a1 == Vector3I.Zero && b1 == new Vector3I(3, 1, 1), "MoveFootprintFace: the +X face dragged 2 cells out -> size 3, min unchanged", $"{a1} {b1}");
        var (a2, b2) = BlockModelLayout.MoveFootprintFace(Vector3I.Zero, Vector3I.One, 0, false, -1);
        Check(a2 == new Vector3I(-1, 0, 0) && b2 == new Vector3I(2, 1, 1), "...the -X face dragged 1 cell out -> min -1, size 2 (the opposite face stays)", $"{a2} {b2}");
        var (a3, b3) = BlockModelLayout.MoveFootprintFace(Vector3I.Zero, new Vector3I(4, 1, 1), 0, false, 10);
        Check(a3.X <= 0 && a3.X + b3.X - 1 >= 0 && b3.X >= 1, "...a -X face dragged across the whole box never turns it inside out and the root cell stays inside", $"{a3} {b3}");
        var (a4, b4) = BlockModelLayout.MoveFootprintFace(new Vector3I(-2, 0, 0), new Vector3I(4, 1, 1), 0, true, -9);
        Check(a4 == new Vector3I(-2, 0, 0) && b4 == new Vector3I(3, 1, 1), "...a +X face dragged left of the root stops at the root (cells -2..0)", $"{a4} {b4}");

        // Формат.
        var plain = MakeFunctional("{}");
        Check(plain.ModelRotation == Vector3.Zero && !plain.FootprintManual && plain.ModelRotationBasis.IsEqualApprox(Basis.Identity), "defaults: no model rotation, footprint is automatic");
        var set = MakeFunctional("{ \"rotation\": [0, 90, 0], \"footprintManual\": true, \"footprint\": [3, 1, 1], \"footprintMin\": [-1, 0, 0] }");
        Check(set.ModelRotation.IsEqualApprox(new Vector3(0, 90, 0)) && set.ModelRotationBasis.IsEqualApprox(yaw90) && set.FootprintManual && set.Footprint == new Vector3I(3, 1, 1),
            "\"rotation\" (degrees) and \"footprintManual\" are read as written", $"{set.ModelRotation} {set.FootprintManual}");
        Check(MakeFunctional("{ \"rotation\": [1, 2] }").ModelRotation == Vector3.Zero, "a malformed \"rotation\" (not three numbers) falls back to no rotation, like the other vector keys");
    }

    // ================================================================== гизмо поворота и объёма в редакторе блоков

    private async Task RunBlockEditorGizmoTests(BuildEditor editor)
    {
        GD.Print("-- block editor gizmos: Scale/Rotate/Volume tools, rotating the model, manual footprint, XML round-trip, game-side model transform");

        const string modelPath = "res://meshes/selftest_blockeditor_gizmo_model.gltf";
        const string dir = "user://selftest_blockeditor_gizmo";
        const string slug = "selftest_gizmo_block";
        const float cell = BuildSpace.CellSize;
        DirAccess.MakeDirRecursiveAbsolute(dir);
        if (FileAccess.FileExists($"{dir}/{slug}.xml")) DirAccess.RemoveAbsolute($"{dir}/{slug}.xml");

        // 4x2x2 ед. с началом координат в центре: при масштабе 0.125 это 2x1x1 клетки.
        WriteTestGltf(modelPath, (new Vector3(-2, -1, -1), new Vector3(2, 1, 1)));
        FunctionalBlockGeometry.ClearSceneCache(modelPath);

        var tool = new BlockEditor { BlocksDirectory = dir };
        editor.AddChild(tool);
        await Frames(editor, 2);
        tool.ResetToDefaults(slug);
        tool.Ui.ApplyChosenModelPath(ProjectSettings.GlobalizePath(modelPath));
        var camera = tool.Camera;
        Check(tool.HasModel && tool.FootprintSize == new Vector3I(2, 1, 1) && tool.Ui.Rotation == Vector3.Zero && !tool.Ui.FootprintManual,
            "setup: a 2x1x1 model, no rotation, automatic footprint", $"{tool.FootprintSize}");

        // ---- инструменты и их гизмо
        Check(tool.Tool == BlockEditorTool.Scale && tool.ScaleGizmoVisible && !tool.RotateGizmoVisible && !tool.VolumeHandlesVisible, "the Scale tool is the default: only the scale arrows are shown");
        tool.SetTool(BlockEditorTool.Rotate);
        Check(tool.Tool == BlockEditorTool.Rotate && !tool.ScaleGizmoVisible && tool.RotateGizmoVisible && !tool.VolumeHandlesVisible, "the Rotate tool shows only the rotation rings");
        tool.SetTool(BlockEditorTool.Volume);
        Check(tool.Tool == BlockEditorTool.Volume && !tool.ScaleGizmoVisible && !tool.RotateGizmoVisible && tool.VolumeHandlesVisible, "the Volume tool shows only the footprint handles");
        editor.GetViewport().GuiReleaseFocus();
        await PressKey(editor, Key.Key2);
        Check(tool.Tool == BlockEditorTool.Rotate, "key 2 selects Rotate");
        await PressKey(editor, Key.Key1);
        Check(tool.Tool == BlockEditorTool.Scale, "key 1 selects Scale");
        await PressKey(editor, Key.Key3);
        Check(tool.Tool == BlockEditorTool.Volume, "key 3 selects Volume");
        tool.SetTool(BlockEditorTool.Rotate);

        // ---- поворот числами: рамка и footprint следуют за повёрнутой моделью
        tool.Ui.SetRotation(new Vector3(0, 0, 90));
        tool.RefreshPreview();
        Check(tool.Bounds.Size.IsEqualApprox(new Vector3(cell, 2 * cell, cell)) && tool.FootprintSize == new Vector3I(1, 2, 1),
            "rotating the model 90 degrees about Z turns its 2x1x1 bbox into 1x2x1, and the automatic footprint follows", $"{tool.Bounds.Size} {tool.FootprintSize}");
        tool.Ui.SetRotation(Vector3.Zero);
        tool.RefreshPreview();

        // ---- чистая математика кольца
        var straightDown = BlockEditor.RingAngle(Vector3.Zero, 1, new Vector3(0.2f, 5f, 0f), Vector3.Down);
        Check(straightDown.HasValue && Math.Abs(straightDown.Value - Math.PI / 2) < 1e-6, "RingAngle: a ray hitting the Y ring plane at +X reads the angle 90 degrees (Y ring: from Z toward X)", $"{straightDown}");
        Check(BlockEditor.RingAngle(Vector3.Zero, 1, new Vector3(0, 1, 0), Vector3.Right) == null, "...and a ray parallel to the ring plane has no angle (the drag simply does nothing)");
        Check(BlockEditor.SnapRotation(17, false) == 15 && BlockEditor.SnapRotation(23, false) == 30 && BlockEditor.SnapRotation(-8, false) == -15 && BlockEditor.SnapRotation(-7, false) == 0,
            "SnapRotation: steps of 15 degrees");
        Check(BlockEditor.SnapRotation(17.4, true) == 17 && BlockEditor.SnapRotation(-3.6, true) == -4, "...and 1 degree with Shift");

        // ---- выбор кольца по экранным координатам
        var pivot = tool.GizmoPivot;
        Vector3 RingPointAt(int axis, double angle)
        {
            var u = BlockEditor.AxisDirection((axis + 1) % 3);
            var v = BlockEditor.AxisDirection((axis + 2) % 3);
            return pivot + (u * Math.Cos(angle) + v * Math.Sin(angle)) * BlockEditor.RotateGizmoRadius;
        }

        bool ringsPicked = true;
        for (int axis = 0; axis < 3; axis++) ringsPicked &= tool.PickRotationAxis(camera.UnprojectPosition(RingPointAt(axis, 0.8))) == axis;
        Check(ringsPicked, "PickRotationAxis: a click on each of the three rings picks that axis (X, Y, Z)");
        Check(tool.PickRotationAxis(new Vector2(-5000, -5000)) == -1, "a click away from the rings picks no axis");

        // ---- перетаскивание кольца: обход по кольцу на 90 градусов = поворот на 90 вокруг оси
        bool dragsRight = true;
        string dragDetail = "";
        for (int axis = 0; axis < 3; axis++)
        {
            tool.Ui.SetRotation(Vector3.Zero);
            tool.RefreshPreview();
            tool.BeginRotateDrag(axis, camera.UnprojectPosition(RingPointAt(axis, 0)));
            tool.UpdateRotateDrag(camera.UnprojectPosition(RingPointAt(axis, Math.PI / 2)), fine: false);
            var expected = new Vector3(axis == 0 ? 90 : 0, axis == 1 ? 90 : 0, axis == 2 ? 90 : 0);
            dragsRight &= tool.Ui.Rotation.IsEqualApprox(expected) && tool.IsDraggingRotation;
            dragDetail += $" axis{axis}={tool.Ui.Rotation}";
            tool.EndRotateDrag();
        }

        Check(dragsRight, "dragging a ring a quarter of the way round turns the model exactly 90 degrees about that axis (X, Y and Z)", dragDetail);

        tool.Ui.SetRotation(Vector3.Zero);
        tool.RefreshPreview();
        tool.BeginRotateDrag(1, camera.UnprojectPosition(RingPointAt(1, 0)));
        tool.UpdateRotateDrag(camera.UnprojectPosition(RingPointAt(1, 0.3)), fine: false); // 17.2 градуса
        Check(tool.Ui.Rotation.IsEqualApprox(new Vector3(0, 15, 0)), "a 17 degree drag snaps to 15", $"{tool.Ui.Rotation}");
        tool.UpdateRotateDrag(camera.UnprojectPosition(RingPointAt(1, 0.3)), fine: true);
        Check(tool.Ui.Rotation.IsEqualApprox(new Vector3(0, 17, 0)), "...and to 17 with Shift (1 degree step)", $"{tool.Ui.Rotation}");
        tool.EndRotateDrag();
        tool.Undo();
        Check(tool.Ui.Rotation == Vector3.Zero, "the whole drag is ONE undo step: Ctrl+Z restores the rotation from before it", $"{tool.Ui.Rotation}");

        // Повороты складываются вокруг осей рамки блока поверх текущего.
        tool.Ui.SetRotation(new Vector3(0, 90, 0));
        tool.RefreshPreview();
        tool.BeginRotateDrag(0, camera.UnprojectPosition(RingPointAt(0, 0)));
        tool.UpdateRotateDrag(camera.UnprojectPosition(RingPointAt(0, Math.PI / 2)), fine: false);
        tool.EndRotateDrag();
        var composed = new Basis(Vector3.Right, Mathf.Pi / 2) * BlockModelLayout.RotationBasis(new Vector3(0, 90, 0));
        Check((composed.Inverse() * BlockModelLayout.RotationBasis(tool.Ui.Rotation)).GetRotationQuaternion().GetAngle() < 0.001,
            "a ring drag is applied on top of the current rotation about the block frame's axis (X 90 after Y 90)", $"{tool.Ui.Rotation}");
        tool.Ui.SetRotation(Vector3.Zero);
        tool.RefreshPreview();

        // ---- ручной объём: ручки на гранях
        tool.SetTool(BlockEditorTool.Volume);
        Check(tool.FootprintMin == Vector3I.Zero && tool.FootprintSize == new Vector3I(2, 1, 1) && !tool.Ui.FootprintManual, "(setup) automatic footprint 2x1x1");
        bool handlesPicked = true;
        for (int axis = 0; axis < 3; axis++)
        {
            foreach (bool positive in new[] { false, true })
            {
                var picked = tool.PickVolumeHandle(camera.UnprojectPosition(tool.VolumeHandlePosition(axis, positive)));
                handlesPicked &= picked.HasValue && picked.Value.Axis == axis && picked.Value.Positive == positive;
            }
        }

        Check(handlesPicked, "PickVolumeHandle: a click on each of the six face handles picks that face (axis and side)");
        Check(tool.PickVolumeHandle(new Vector2(-5000, -5000)) == null, "a click away from the handles picks nothing");
        var faceCenter = tool.VolumeHandlePosition(0, true);
        Check(faceCenter.IsEqualApprox(new Vector3(2 * cell, cell / 2, cell / 2)), "the +X handle sits on the middle of the +X face of the footprint", $"{faceCenter}");

        var modelBoundsBefore = tool.Bounds;
        tool.BeginVolumeDrag(0, true, camera.UnprojectPosition(faceCenter));
        tool.UpdateVolumeDrag(camera.UnprojectPosition(faceCenter + Vector3.Right * (2 * cell)));
        Check(tool.Ui.FootprintManual && tool.IsDraggingVolume && tool.FootprintMin == Vector3I.Zero && tool.FootprintSize == new Vector3I(4, 1, 1),
            "dragging the +X face two cells out makes the footprint manual and 4x1x1", $"{tool.FootprintMin} {tool.FootprintSize} manual={tool.Ui.FootprintManual}");
        Check(tool.Bounds.IsEqualApprox(modelBoundsBefore), "...the MODEL stays exactly where it was (a part may stick out of / stay inside the occupied cells)");
        tool.EndVolumeDrag();

        tool.BeginVolumeDrag(0, false, camera.UnprojectPosition(tool.VolumeHandlePosition(0, false)));
        tool.UpdateVolumeDrag(camera.UnprojectPosition(tool.VolumeHandlePosition(0, false) - Vector3.Right * cell));
        Check(tool.FootprintMin == new Vector3I(-1, 0, 0) && tool.FootprintSize == new Vector3I(5, 1, 1), "dragging the -X face one cell out -> min -1, size 5 (the +X face stays)", $"{tool.FootprintMin} {tool.FootprintSize}");
        tool.EndVolumeDrag();
        tool.Undo();
        Check(tool.FootprintMin == Vector3I.Zero && tool.FootprintSize == new Vector3I(4, 1, 1) && tool.Ui.FootprintManual, "each face drag is one undo step (back to 4x1x1, still manual)", $"{tool.FootprintMin} {tool.FootprintSize}");
        tool.Undo();
        Check(!tool.Ui.FootprintManual && tool.FootprintSize == new Vector3I(2, 1, 1), "...and one more undo returns to the automatic footprint", $"{tool.FootprintSize}");
        editor.GetViewport().GuiReleaseFocus();

        // Ручной footprint переживает смену масштаба; корень остаётся внутри; Fit возвращает значения расчёта.
        tool.Ui.SetFootprint(true, new Vector3I(-1, 0, 0), new Vector3I(3, 2, 1));
        tool.Ui.SetModelScale(new Vector3(0.25f, 0.25f, 0.25f));
        tool.RefreshPreview();
        Check(tool.FootprintMin == new Vector3I(-1, 0, 0) && tool.FootprintSize == new Vector3I(3, 2, 1), "a manual footprint stays put when the model is scaled (the automatic one would follow)", $"{tool.FootprintSize}");
        tool.Ui.SetFootprint(true, new Vector3I(5, 0, 0), new Vector3I(2, 1, 1));
        tool.RefreshPreview();
        Check(tool.FootprintMin == Vector3I.Zero && tool.FootprintSize == new Vector3I(7, 1, 1) && tool.Ui.ManualFootprint.Size == new Vector3I(7, 1, 1),
            "a typed footprint that excludes the root cell is stretched to include it, and the corrected numbers are written back to the fields", $"{tool.FootprintMin} {tool.FootprintSize}");
        tool.FitFootprintToModel();
        var autoNow = BlockModelLayout.ComputeFootprint(tool.Bounds);
        Check(tool.Ui.FootprintManual && tool.FootprintMin == autoNow.Min && tool.FootprintSize == autoNow.Size, "Fit to model: the manual footprint takes the values the automatic calculation gives", $"{tool.FootprintSize} vs {autoNow.Size}");
        tool.Ui.SetFootprint(false, Vector3I.Zero, Vector3I.One);
        tool.RefreshPreview();
        Check(!tool.Ui.FootprintManual && tool.FootprintSize == autoNow.Size, "turning manual mode off returns to the automatic footprint");

        // Галочка «Manual volume»: переключение — точка Undo с прежним режимом.
        tool.Ui.SetModelScale(BlockModelLayout.DefaultScaleVector);
        tool.RefreshPreview();
        tool.Ui.SetFootprintManualForTesting(true);
        Check(tool.Ui.FootprintManual, "ticking Manual volume switches to the manual footprint");
        tool.Undo();
        Check(!tool.Ui.FootprintManual, "...and Ctrl+Z un-ticks it (the toggle is an undo step)");

        // ---- XML: ключи пишутся только когда нужны; полный круг сохранение -> каталог игры -> редактор
        var plainXml = tool.BuildXml(slug);
        Check(!plainXml.Contains("\"rotation\"") && !plainXml.Contains("footprintManual"), "a block with no rotation and an automatic footprint writes neither key (old files stay as they are)");

        tool.Ui.SetModelScale(new Vector3(0.125f, 0.125f, 0.125f));
        tool.Ui.SetRotation(new Vector3(0, 90, 0));
        tool.Ui.SetFootprint(true, new Vector3I(-1, 0, 0), new Vector3I(4, 2, 1));
        tool.RefreshPreview();
        string xml = tool.BuildXml(slug);
        Check(xml.Contains("\"rotation\": [0, 90, 0]") && xml.Contains("\"footprintManual\": true") && xml.Contains("\"footprint\": [4, 2, 1]") && xml.Contains("\"footprintMin\": [-1, 0, 0]"),
            "the XML carries rotation (degrees), footprintManual and the manual footprint", xml);

        tool.Save();
        var saved = BlockCatalog.Load(dir);
        var savedFn = saved.TryGetBySlug(slug, out var savedDef) ? FunctionalOf(savedDef) : null;
        Check(savedFn != null && savedFn.ModelRotation.IsEqualApprox(new Vector3(0, 90, 0)) && savedFn.FootprintManual && savedFn.Footprint == new Vector3I(4, 2, 1) && savedFn.FootprintMin == new Vector3I(-1, 0, 0),
            "the game's catalog reads the saved rotation, the manual flag and the manual footprint (NOT recomputed from the bbox)");

        tool.ResetToDefaults("");
        tool.LoadSlug(slug);
        Check(tool.Ui.Rotation.IsEqualApprox(new Vector3(0, 90, 0)) && tool.Ui.FootprintManual && tool.FootprintMin == new Vector3I(-1, 0, 0) && tool.FootprintSize == new Vector3I(4, 2, 1),
            "round-trip: the editor reloads the rotation and keeps the manual footprint instead of recomputing it", $"{tool.Ui.Rotation} {tool.FootprintSize}");
        Check(tool.Bounds.Size.IsEqualApprox(new Vector3(cell, cell, 2 * cell)), "...and the reloaded model is turned (a 2x1x1 model turned 90 degrees about Y is 1x1x2)", $"{tool.Bounds.Size}");

        // ---- игровой слой: модель блока в мире стоит с тем же поворотом
        using (UseCatalogWith(("zz_rotmodel", $"{{ \"scene\": \"{modelPath}\", \"rotation\": [0, 90, 0], \"footprint\": [1, 1, 2], \"footprintManual\": true }}")))
        {
            var construction = new Construction(new VoxelGrid());
            var placed = PlaceFixture(construction, "zz_rotmodel", new Vector3I(2, 0, 0));
            var view = new FunctionalBlockView();
            editor.AddChild(view);
            view.Sync(construction);

            var fn = FunctionalOf(BlockCatalog.Instance.Get("zz_rotmodel"))!;
            var (_, aabb) = FunctionalBlockGeometry.GetOrLoadScene(modelPath);
            var frame = FunctionalBlockGeometry.InstanceFrame(placed.Origin, fn.FootprintMin, fn.Footprint, placed.RotationSteps);
            var expected = frame * BlockModelLayout.ModelTransform(aabb, fn.ModelScale, fn.Anchor, fn.ModelRotationBasis);
            var unrotated = frame * BlockModelLayout.ModelTransform(aabb, fn.ModelScale, fn.Anchor);
            var node = view.GetView(placed.InstanceId);
            Check(node != null && node.Transform.IsEqualApprox(expected) && !node.Transform.IsEqualApprox(unrotated),
                "in the game the model of a block with \"rotation\" is placed turned (and differs from the unrotated placement)");

            // Размер модели по X (4 ед. * 0.125 = 2 клетки) после поворота на 90 вокруг Y лежит вдоль Z блока.
            var farEnd = node!.Transform * (aabb.Position + new Vector3(aabb.Size.X, 0, 0));
            var nearEnd = node.Transform * aabb.Position;
            Check(Math.Abs((farEnd - nearEnd).Z) > 1.5 * cell && Math.Abs((farEnd - nearEnd).X) < 0.1 * cell, "...so the model's long axis now runs along the block's Z", $"{farEnd - nearEnd}");

            view.QueueFree();
        }

        if (FileAccess.FileExists($"{dir}/{slug}.xml")) DirAccess.RemoveAbsolute($"{dir}/{slug}.xml");
        string modelFile = ProjectSettings.GlobalizePath(modelPath);
        if (FileAccess.FileExists(modelPath)) DirAccess.RemoveAbsolute(modelFile);
        FunctionalBlockGeometry.ClearSceneCache(modelPath);
        tool.QueueFree();
        await Frames(editor, 1);
    }
}
