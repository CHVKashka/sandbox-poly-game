using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.World;

namespace SandboxPolyGame.Dev;

public sealed partial class SelfTest
{
    // ================================================================== формат "decor"

    private void RunDecorFormatTests()
    {
        GD.Print("-- decor meshes: the \"decor\" format, defaults, errors, the local transform");
        const float cell = BuildSpace.CellSize;

        Check(MakeFunctional("{}").Decor.Count == 0, "a block without \"decor\" has no pointer meshes");
        var one = MakeFunctional("{ \"decor\": [ { \"scene\": \"res://meshes/arrow.glb\" } ] }").Decor[0];
        Check(one.Scene == "res://meshes/arrow.glb" && one.ShowWhen == DecorShowWhen.Ghost && one.Rotation == Vector3.Zero
              && one.Position.IsEqualApprox(new Vector3(cell / 2, cell / 2, cell / 2)) && one.Scale.IsEqualApprox(BlockModelLayout.DefaultScaleVector),
            "only \"scene\" is required: the pointer sits in the centre of the root cell, unrotated, scale 0.125, shown while placing (Ghost)", $"{one}");

        var full = MakeFunctional(
            "{ \"decor\": [ { \"scene\": \"res://a.glb\", \"position\": [0.5, 0.25, 0], \"rotation\": [0, 90, 0], \"scale\": [0.25, 0.125, 0.5], \"show\": \"always\" }, { \"scene\": \"res://b.glb\" } ] }");
        Check(full.Decor.Count == 2 && full.Decor[0].ShowWhen == DecorShowWhen.Always && full.Decor[0].Position.IsEqualApprox(new Vector3(0.5f, 0.25f, 0))
              && full.Decor[0].Rotation.IsEqualApprox(new Vector3(0, 90, 0)) && full.Decor[0].Scale.IsEqualApprox(new Vector3(0.25f, 0.125f, 0.5f)), "position, rotation, scale and show are read as written (show is case-insensitive); several pointers per block");

        string Error(string json)
        {
            try { MakeFunctional(json); return ""; }
            catch (Exception ex) { return ex.Message; }
        }

        Check(Error("{ \"decor\": [ { \"position\": [0,0,0] } ] }").Contains("scene") && Error("{ \"decor\": [ { \"scene\": \"\" } ] }").Contains("scene"), "a decor entry without a scene is a data error");
        Check(Error("{ \"decor\": [ { \"scene\": \"res://a.glb\", \"show\": \"sometimes\" } ] }").Contains("Ghost or Always"), "an unknown \"show\" is a data error");
        Check(Error("{ \"decor\": [ { \"scene\": \"res://a.glb\", \"scale\": [0.1, 0, 0.1] } ] }").Contains("positive"), "a non-positive decor scale is a data error");

        // Положение: масштаб по осям модели, потом поворот, потом сдвиг.
        var decor = new DecorMesh("x", new Vector3(1, 2, 3), new Vector3(0, 90, 0), new Vector3(2, 1, 1), DecorShowWhen.Ghost);
        var mapped = decor.LocalTransform * new Vector3(1, 0, 0); // (1,0,0) -> масштаб (2,0,0) -> поворот на 90 вокруг Y: +X в -Z -> (0,0,-2) -> сдвиг
        Check(mapped.IsEqualApprox(new Vector3(1, 2, 1)), "LocalTransform: scale along the model axes, then rotate, then move to the position", $"{mapped}");
    }

    // ================================================================== редактор блоков и редактор построек

    private async Task RunDecorEditorTests(BuildEditor editor)
    {
        GD.Print("-- decor meshes: the block editor (add/edit/save/load/undo), the build editor (ghost and placed pointers), and none in the game world");

        const string modelPath = "res://meshes/selftest_decor_arrow.gltf";
        const string dir = "user://selftest_blockeditor_decor";
        const string slug = "selftest_decor_block";
        const float cell = BuildSpace.CellSize;
        DirAccess.MakeDirRecursiveAbsolute(dir);
        if (FileAccess.FileExists($"{dir}/{slug}.xml")) DirAccess.RemoveAbsolute($"{dir}/{slug}.xml");
        WriteTestGltf(modelPath, (new Vector3(-1, -1, -1), new Vector3(1, 1, 1)));
        FunctionalBlockGeometry.ClearSceneCache(modelPath);

        // ---- редактор блоков
        var tool = new BlockEditor { BlocksDirectory = dir };
        editor.AddChild(tool);
        await Frames(editor, 2);
        tool.ResetToDefaults(slug);
        Check(tool.Ui.Decor.Count == 0 && !tool.BuildXml(slug).Contains("\"decor\""), "a new block has no decor and the XML has no \"decor\" key");

        tool.AddDecor();
        var added = tool.Ui.Decor;
        Check(added.Count == 1 && added[0].Scene == "" && added[0].ShowWhen == DecorShowWhen.Ghost && added[0].Position.IsEqualApprox(DecorMesh.DefaultPosition), "+ Add decor adds one row with the defaults (centre of the root cell, Ghost)");
        Check(tool.DecorVisualCount == 0, "a row without a model draws nothing");
        tool.Save();
        Check(!FileAccess.FileExists($"{dir}/{slug}.xml") && tool.Ui.Status.Contains("decor #0"), "Save refuses a decor row without a scene and says which", tool.Ui.Status);

        tool.Ui.SetDecorForTesting(0, new DecorMesh(modelPath, new Vector3(0.5f, 0.125f, 0.125f), new Vector3(0, 90, 0), new Vector3(0.25f, 0.125f, 0.125f), DecorShowWhen.Always));
        tool.RefreshPreview();
        Check(tool.DecorVisualCount == 1 && tool.DecorVisualTransform(0).IsEqualApprox(tool.Ui.Decor[0].LocalTransform), "the pointer is drawn in the block frame with its position/rotation/scale", $"{tool.DecorVisualCount}");

        string xml = tool.BuildXml(slug);
        Check(xml.Contains("\"decor\": [") && xml.Contains($"\"scene\": \"{modelPath}\"") && xml.Contains("\"rotation\": [0, 90, 0]") && xml.Contains("\"show\": \"Always\""), "the XML carries the decor entry", xml);

        tool.Save();
        Check(FileAccess.FileExists($"{dir}/{slug}.xml"), "Save writes the block once every row has a scene");
        var catalog = BlockCatalog.Load(dir);
        var savedFn = catalog.TryGetBySlug(slug, out var savedDef) ? FunctionalOf(savedDef) : null;
        Check(savedFn != null && savedFn.Decor.Count == 1 && savedFn.Decor[0].Scene == modelPath && savedFn.Decor[0].ShowWhen == DecorShowWhen.Always
              && savedFn.Decor[0].Rotation.IsEqualApprox(new Vector3(0, 90, 0)) && savedFn.Decor[0].Scale.IsEqualApprox(new Vector3(0.25f, 0.125f, 0.125f)), "the game's catalog reads the saved decor");

        tool.ResetToDefaults("");
        tool.LoadSlug(slug);
        Check(tool.Ui.Decor.Count == 1 && tool.Ui.Decor[0].Scene == modelPath && tool.Ui.Decor[0].Position.IsEqualApprox(new Vector3(0.5f, 0.125f, 0.125f)) && tool.DecorVisualCount == 1, "round-trip: the editor reopens the block with its pointer (and draws it)");

        tool.AddDecor();
        Check(tool.Ui.Decor.Count == 2, "(setup) a second row");
        tool.Undo();
        Check(tool.Ui.Decor.Count == 1, "Ctrl+Z undoes adding a row");
        tool.RemoveDecor(0);
        Check(tool.Ui.Decor.Count == 0 && tool.DecorVisualCount == 0, "Remove drops the row and its drawing");
        tool.Undo();
        Check(tool.Ui.Decor.Count == 1 && tool.Ui.Decor[0].Scene == modelPath, "...and Ctrl+Z brings it back with all its values");

        // ---- редактор построек: призрак и поставленные блоки
        string decorBlock =
            "{ \"footprint\": [1,1,1], \"decor\": [ { \"scene\": \"" + modelPath + "\", \"show\": \"Ghost\" }, " +
            "{ \"scene\": \"" + modelPath + "\", \"position\": [0.125, 0.5, 0.125], \"rotation\": [0, 45, 0], \"show\": \"Always\" } ] }";
        using var scope = UseCatalogWith(("zz_decor_block", decorBlock));
        var state = editor.State;
        var camera = editor.EditorCamera;
        var construction = editor.World.Construction;
        bool accumulated = Input.UseAccumulatedInput;
        Input.UseAccumulatedInput = false;

        construction.Clear();
        state.Tool = ToolMode.None;
        state.ResetPendingRotation();
        construction.PlaceBlock(Vector3I.Zero, new Vector3I(8, 1, 8), BlockCatalog.Instance.Get("block"), Colors.White);
        state.SetSlot(0, "zz_decor_block");
        state.SelectedSlot = 0;
        camera.LookAtPoint(new Vector3(1.125f, 5f, 1.125f), new Vector3(1.125f, 0f, 1.125f));
        await Frames(editor, 2);
        var aim = camera.UnprojectPosition(new Vector3(1.125f, 0.25f, 1.125f));
        await Move(editor, aim);
        await Frames(editor, 2);
        var place = new Vector3I(4, 1, 4);
        var blockFn = FunctionalOf(BlockCatalog.Instance.Get("zz_decor_block"))!;

        Check(editor.Hover.PlaceCell == place && editor.GhostDecorVisibleCount == 2, "while a block with decor is being placed, both of its pointers are shown with the ghost", $"{editor.GhostDecorVisibleCount}");
        Check(editor.GhostDecorTransform(0).IsEqualApprox(FunctionalBlockGeometry.PivotFrame(place, blockFn.AttachCell, editor.GhostVisualBasis) * blockFn.Decor[0].LocalTransform),
            "...each in the ghost's frame (position in the block frame, turning with the block)");
        state.RotatePendingY();
        for (int i = 0; i < 60 && !editor.GhostVisualBasis.IsEqualApprox(state.PendingRotationBasis); i++) await Frames(editor, 1);
        await Frames(editor, 2);
        Check(editor.GhostDecorTransform(1).IsEqualApprox(FunctionalBlockGeometry.PivotFrame(place, blockFn.AttachCell, state.PendingRotationBasis) * blockFn.Decor[1].LocalTransform), "after a rotation the pointers have turned with the block");
        state.ResetPendingRotation();
        for (int i = 0; i < 60 && !editor.GhostVisualBasis.IsEqualApprox(state.PendingRotationBasis); i++) await Frames(editor, 1);

        state.Tool = ToolMode.Delete;
        await Frames(editor, 2);
        Check(editor.GhostDecorVisibleCount == 0, "with another tool active (no ghost) the pointers are hidden");
        state.Tool = ToolMode.None;
        await Frames(editor, 2);
        Check(editor.GhostDecorVisibleCount == 2, "...and back with the ghost");

        await Click(editor, aim, MouseButton.Left);
        var placed = construction.Instances.FirstOrDefault(i => i.BlockSlug == "zz_decor_block");
        Check(placed != null, "(setup) the block is placed");
        if (placed != null)
        {
            Check(editor.World.FunctionalBlocks.DecorCount(placed.InstanceId) == 1, "the placed block keeps only its Always pointer (the Ghost one was for placing)", $"{editor.World.FunctionalBlocks.DecorCount(placed.InstanceId)}");

            // В игре (заспавненная постройка) указателей нет вовсе.
            var parent = new Node3D();
            editor.AddChild(parent);
            var vehicle = VehicleSpawner.Spawn(parent, ConstructionIO.Serialize(construction), new Vector3(0, 30, 0));
            await Frames(editor, 2);
            var spawnedView = vehicle.GetChildren().OfType<VoxelWorld>().First().FunctionalBlocks;
            var spawned = vehicle.Construction!.Instances.First(i => i.BlockSlug == "zz_decor_block");
            Check(!spawnedView.ShowDecor && spawnedView.DecorCount(spawned.InstanceId) == 0, "in the game world (a spawned vehicle) there are no pointer meshes at all - they exist only in the editor");
            parent.QueueFree();

            construction.Remove(placed);
            Check(editor.World.FunctionalBlocks.DecorCount(placed.InstanceId) == 0, "removing the block removes its pointers");
        }

        state.SetSlot(0, "block");
        construction.Clear();
        Input.UseAccumulatedInput = accumulated;
        if (FileAccess.FileExists($"{dir}/{slug}.xml")) DirAccess.RemoveAbsolute($"{dir}/{slug}.xml");
        if (FileAccess.FileExists(modelPath)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(modelPath));
        FunctionalBlockGeometry.ClearSceneCache(modelPath);
        tool.QueueFree();
        await Frames(editor, 2);
    }
}
