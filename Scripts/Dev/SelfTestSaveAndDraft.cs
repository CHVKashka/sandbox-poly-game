using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Editor.Ui;
using SandboxPolyGame.World;

namespace SandboxPolyGame.Dev;

public sealed partial class SelfTest
{
    // ================================================================== окно Save (список построек), черновик редактора, R по постройке

    private const string SaveTestPrefix = "zz_savetest_";

    /// <summary>Файл постройки из <paramref name="blocks"/> кубов в ряд, записанный в настоящий каталог сохранений (чистится <see cref="RemoveSaveTestFiles"/>).</summary>
    private static string MakeSavedConstruction(string name, int blocks, string description)
    {
        var construction = new Construction(new VoxelGrid());
        construction.PlaceBlock(Vector3I.Zero, new Vector3I(blocks, 1, 1), BlockCatalog.Instance.Get("block"), Colors.White);
        string path = ConstructionStorage.ResolveNewPath(name);
        ConstructionStorage.Save(construction, path, name, description);
        return path;
    }

    private static int BlocksInFile(string path)
    {
        var construction = new Construction(new VoxelGrid());
        ConstructionIO.LoadFromFile(construction, path, BlockCatalog.Instance);
        return construction.Grid.BlockCount;
    }

    private static int BlocksInJson(string json)
    {
        var construction = new Construction(new VoxelGrid());
        ConstructionIO.Deserialize(construction, json, BlockCatalog.Instance);
        return construction.Grid.BlockCount;
    }

    private static void RemoveSaveTestFiles()
    {
        foreach (var entry in ConstructionStorage.List().Where(c => c.Name.StartsWith(SaveTestPrefix)))
        {
            DirAccess.RemoveAbsolute(entry.Path);
            if (FileAccess.FileExists(ConstructionStorage.PreviewPathFor(entry.Path))) DirAccess.RemoveAbsolute(ConstructionStorage.PreviewPathFor(entry.Path));
        }
    }

    private async Task RunSaveDialogAndDraftTests(BuildEditor editor)
    {
        GD.Print("-- editor: Save opens the list of saved constructions (New vehicle first; save as new or overwrite), the editor remembers the construction");

        var state = editor.State;
        var build = editor.World.Construction;
        var catalog = BlockCatalog.Instance;
        var ui = editor.Ui;
        bool accumulated = Input.UseAccumulatedInput;
        Input.UseAccumulatedInput = false;
        RemoveSaveTestFiles();
        state.Tool = ToolMode.None;

        string pathA = MakeSavedConstruction(SaveTestPrefix + "A", 1, "first");
        await Frames(editor, 2); // у файлов разное время изменения (сортировка списка по нему)
        string pathB = MakeSavedConstruction(SaveTestPrefix + "B", 2, "second");
        var entryA = ConstructionStorage.List().First(c => c.Path == pathA);
        int listedBefore = ConstructionStorage.List().Count;

        // Постройка в редакторе: 3 блока, не связана ни с каким файлом.
        build.Clear();
        build.PlaceBlock(Vector3I.Zero, new Vector3I(3, 1, 1), catalog.Get("block"), Colors.White);
        editor.World.RebuildDirty();
        ui.ResumeFrom(editor, new EditorDraft(ConstructionIO.Serialize(build), null, "", ""));
        Check(ui.CurrentSavedPath == null && ui.CurrentName == "" && editor.World.Grid.BlockCount == 3, "(setup) the editor holds an unsaved 3-block construction");

        var saveButton = FindButtons(editor).Find(b => b.Text == "Save...")!;
        var dialog = ui.SaveDialog;

        // ---- открытие: список, New vehicle первой строкой, без кнопок верстака
        await Click(editor, saveButton.GetGlobalRect().GetCenter(), MouseButton.Left);
        Check(ui.SaveDialogOpen, "the Save button opens the save window");
        var texts = dialog.ListTexts;
        Check(texts.Count >= 3 && texts[0] == "New vehicle", "the list starts with the 'New vehicle' button", string.Join(" | ", texts.Take(3).Select(t => t.Replace('\n', '/'))));
        Check(texts.Skip(1).Any(t => t.StartsWith(SaveTestPrefix + "A\n")) && texts.Skip(1).Any(t => t.StartsWith(SaveTestPrefix + "B\n")),
            "...followed by the saved constructions (name and dates)", string.Join(" | ", texts.Select(t => t.Replace('\n', '/'))));
        Check(texts.Count == 1 + listedBefore, "...all of them, one row per file", $"rows={texts.Count} files={listedBefore}");
        Check(texts[1].StartsWith(SaveTestPrefix + "B\n"), "...the most recently modified first (right after New vehicle)", texts[1].Replace('\n', '/'));
        var overlayButtons = ui.SaveDialog.AllButtons.Select(b => b.Text).ToList();
        Check(!overlayButtons.Any(t => t is "Create vehicle" or "Join to workbench" or "Open" or "Spawn" || t.StartsWith("Continue editing")),
            "...and there are no workbench buttons (Create / Join / Open / Spawn) in this window", string.Join(", ", overlayButtons.Where(t => !t.Contains('\n'))));
        Check(dialog.SelectedPath == null && dialog.PressedRowCount == 1 && dialog.NewButton.ButtonPressed && dialog.ActionButton.Text == "Save",
            "an unsaved construction opens the window on 'New vehicle' (the action is Save)", $"selected={dialog.SelectedPath} action={dialog.ActionButton.Text}");
        var rowB = dialog.EntryButton(pathB)!;
        Check(dialog.NewButton.GetGlobalRect().Size.Y > 30 && rowB.GetGlobalRect().Size.Y > 30 && rowB.GetGlobalRect().End.Y <= editor.GetViewport().GetVisibleRect().Size.Y,
            "the list rows really take space on screen (a scroll list can collapse to zero height)", $"{rowB.GetGlobalRect()}");

        // ---- выбор сохранённой: подсветка, поля, Overwrite (настоящий клик)
        await Click(editor, dialog.EntryButton(pathA)!.GetGlobalRect().GetCenter(), MouseButton.Left);
        Check(dialog.SelectedPath == pathA && dialog.PressedRowCount == 1 && !dialog.NewButton.ButtonPressed && dialog.ActionButton.Text == "Overwrite",
            "clicking a saved construction selects it (one highlighted row) and turns the action into Overwrite", $"selected={dialog.SelectedPath} action={dialog.ActionButton.Text}");
        Check(dialog.NameField.Text == SaveTestPrefix + "A" && dialog.DescriptionField.Text == "first", "...its name and description are shown in the fields", $"{dialog.NameField.Text} / {dialog.DescriptionField.Text}");
        await Click(editor, dialog.NewButton.GetGlobalRect().GetCenter(), MouseButton.Left);
        Check(dialog.SelectedPath == null && dialog.PressedRowCount == 1 && dialog.ActionButton.Text == "Save", "...and 'New vehicle' selects the new-file mode again");

        // ---- Esc: ничего не пишет
        Send(new InputEventKey { PhysicalKeycode = Godot.Key.Escape, Keycode = Godot.Key.Escape, Pressed = true });
        await Frames(editor, 1);
        Check(!ui.SaveDialogOpen && ConstructionStorage.List().Count == listedBefore && BlocksInFile(pathA) == 1, "Escape closes the window without touching any file");

        // ---- Overwrite существующей: тот же файл, дата создания сохранена, имя/описание правятся
        await Click(editor, saveButton.GetGlobalRect().GetCenter(), MouseButton.Left);
        await Click(editor, dialog.EntryButton(pathA)!.GetGlobalRect().GetCenter(), MouseButton.Left);
        dialog.NameField.Text = SaveTestPrefix + "A2";
        dialog.DescriptionField.Text = "changed";
        await Click(editor, dialog.ActionButton.GetGlobalRect().GetCenter(), MouseButton.Left);
        var afterOverwrite = ConstructionStorage.List();
        var overwritten = afterOverwrite.FirstOrDefault(c => c.Path == pathA);
        Check(!ui.SaveDialogOpen && afterOverwrite.Count == listedBefore, "Overwrite closes the window and creates no new file", $"files={afterOverwrite.Count} (was {listedBefore})");
        Check(BlocksInFile(pathA) == 3, "...the chosen file now holds the CURRENT construction (3 blocks, was 1)", $"blocks={BlocksInFile(pathA)}");
        Check(overwritten.Name == SaveTestPrefix + "A2" && overwritten.Description == "changed", "...with the edited name and description", $"{overwritten.Name} / {overwritten.Description}");
        Check(overwritten.CreatedUtc == entryA.CreatedUtc && string.CompareOrdinal(overwritten.ModifiedUtc, entryA.ModifiedUtc) > 0, "...the creation date is kept, the modification date moves", $"created {entryA.CreatedUtc} -> {overwritten.CreatedUtc}");
        Check(BlocksInFile(pathB) == 2, "...and the other saved constructions are untouched");
        Check(ui.CurrentSavedPath == pathA && ui.Status.Contains("Overwrote"), "...the construction is now linked to that file", $"{ui.CurrentSavedPath} / {ui.Status}");

        // ---- повторное открытие: сразу на «своей» постройке
        await Click(editor, saveButton.GetGlobalRect().GetCenter(), MouseButton.Left);
        Check(dialog.SelectedPath == pathA && dialog.ActionButton.Text == "Overwrite" && dialog.PressedRowCount == 1, "reopening Save is already on the construction being edited (re-saving is one click)", $"selected={dialog.SelectedPath}");
        Send(new InputEventKey { PhysicalKeycode = Godot.Key.Escape, Keycode = Godot.Key.Escape, Pressed = true });
        await Frames(editor, 1);

        // ---- несохранённые правки -> черновик, спавн-JSON и возврат
        build.PlaceBlock(new Vector3I(0, 0, 3), new Vector3I(1, 1, 1), catalog.Get("block"), Colors.White);
        editor.World.RebuildDirty();
        Check(editor.World.Grid.BlockCount == 4 && BlocksInFile(pathA) == 3, "(setup) one more block is added and NOT saved: the editor has 4, the file 3");

        EditorHandoff.Draft = null;
        ui.StoreDraft(editor);
        var draft = EditorHandoff.Draft;
        Check(draft != null && draft.SavedPath == pathA && draft.Name == SaveTestPrefix + "A2" && draft.Description == "changed",
            "leaving the editor remembers the construction with its file link, name and description", $"{draft}");
        Check(draft != null && BlocksInJson(draft.Json) == 4, "...with the UNSAVED block too (not just what is on disk)", $"blocks={(draft == null ? -1 : BlocksInJson(draft.Json))}");

        string spawnJson = ui.SerializeCurrent(editor);
        var spawnMeta = ConstructionIO.ReadMetadata(spawnJson);
        Check(BlocksInJson(spawnJson) == 4 && spawnMeta.Name == SaveTestPrefix + "A2" && spawnMeta.CreatedUtc == overwritten.CreatedUtc,
            "the JSON handed to Spawn has every change and remembers whose construction it is (name, file creation time)", $"{spawnMeta}");
        var fromSpawn = EditorDraft.FromSpawnJson(spawnJson);
        Check(fromSpawn.SavedPath == pathA && fromSpawn.Name == SaveTestPrefix + "A2" && fromSpawn.Description == "changed" && BlocksInJson(fromSpawn.Json) == 4,
            "...so coming back from the world finds the file again (for Overwrite)", $"{fromSpawn.SavedPath}");

        build.Clear();
        editor.World.RebuildDirty();
        ui.ResumeFrom(editor, draft!);
        Check(editor.World.Grid.BlockCount == 4 && ui.CurrentSavedPath == pathA && ui.CurrentName == SaveTestPrefix + "A2",
            "resuming restores the blocks (with the unsaved one) and the link to the file", $"blocks={editor.World.Grid.BlockCount} path={ui.CurrentSavedPath}");
        await Click(editor, saveButton.GetGlobalRect().GetCenter(), MouseButton.Left);
        Check(dialog.SelectedPath == pathA, "...so Save opens right on that file");
        Send(new InputEventKey { PhysicalKeycode = Godot.Key.Escape, Keycode = Godot.Key.Escape, Pressed = true });
        await Frames(editor, 1);

        // файл успели удалить: постройка несохранённая, имя осталось
        string pathC = MakeSavedConstruction(SaveTestPrefix + "C", 1, "gone soon");
        string jsonC = EditorDraft.Serialize(build, pathC, SaveTestPrefix + "C", "gone soon");
        Check(EditorDraft.FromSpawnJson(jsonC).SavedPath == pathC, "(setup) a spawn JSON of a saved construction resolves to its file");
        DirAccess.RemoveAbsolute(pathC);
        var orphan = EditorDraft.FromSpawnJson(jsonC);
        Check(orphan.SavedPath == null && orphan.Name == SaveTestPrefix + "C", "a construction whose file was deleted comes back as unsaved (name kept)", $"{orphan.SavedPath} '{orphan.Name}'");
        ui.ResumeFrom(editor, orphan);
        await Click(editor, saveButton.GetGlobalRect().GetCenter(), MouseButton.Left);
        Check(ui.CurrentSavedPath == null && dialog.SelectedPath == null && dialog.NameField.Text == SaveTestPrefix + "C", "...Save opens on 'New vehicle' with that name prefilled", $"name='{dialog.NameField.Text}'");

        // ---- New vehicle: имя обязательно; занятое имя не затирает чужой файл
        dialog.NameField.Text = "";
        await Click(editor, dialog.ActionButton.GetGlobalRect().GetCenter(), MouseButton.Left);
        Check(ui.SaveDialogOpen, "Save as new with an empty name keeps the window open (a name is required)");
        dialog.NameField.Text = SaveTestPrefix + "A2";
        dialog.DescriptionField.Text = "a copy";
        await Click(editor, dialog.ActionButton.GetGlobalRect().GetCenter(), MouseButton.Left);
        var afterNew = ConstructionStorage.List();
        var copy = afterNew.FirstOrDefault(c => c.Description == "a copy");
        Check(!ui.SaveDialogOpen && afterNew.Count == listedBefore + 1 && copy.Path != null && copy.Path != pathA,
            "saving as new with an already used name makes a NEW file (the name gets a suffix) instead of replacing the old one", $"files={afterNew.Count} path={copy.Path}");
        Check(BlocksInFile(pathA) == 3 && ConstructionStorage.List().First(c => c.Path == pathA).Description == "changed", "...the existing construction is not touched");
        Check(copy.Path != null && BlocksInFile(copy.Path) == 4 && ui.CurrentSavedPath == copy.Path, "...the new file holds the current construction and becomes the linked one", $"{ui.CurrentSavedPath}");

        // ---- что подставить в пустой редактор: файл / черновик / ничего
        bool ps(string? path, bool resume, EditorDraft? d)
        {
            EditorHandoff.PendingConstructionPath = path;
            EditorHandoff.ResumeDraft = resume;
            EditorHandoff.Draft = d;
            return BuildEditor.ShouldPlaceRootBlock();
        }

        bool fresh = ps(null, false, null);
        bool withFile = ps("some.json", false, draft);
        bool withDraft = ps(null, true, draft);
        bool draftFlagButNoDraft = ps(null, true, null);
        Check(fresh && !withFile && !withDraft && draftFlagButNoDraft,
            "a new editor gets its root block only when there is no file and no draft to fill it", $"fresh={fresh} file={withFile} draft={withDraft} noDraft={draftFlagButNoDraft}");
        EditorHandoff.PendingConstructionPath = null;
        EditorHandoff.ResumeDraft = false;
        EditorHandoff.Draft = null;

        await Frames(editor, 5); // превью (PNG) дорисовываются в фоне - дать им закончиться до уборки
        RemoveSaveTestFiles();
        ui.ResumeFrom(editor, new EditorDraft(ConstructionIO.Serialize(build), null, "", "")); // связь с удалёнными файлами не нужна следующим тестам
        Input.UseAccumulatedInput = accumulated;
        build.Clear();
        editor.World.RebuildDirty();
        await Frames(editor, 2);
    }

    private async Task RunRecallToEditorTests(BuildEditor editor)
    {
        GD.Print("-- open world: Continue editing in the workbench menu, R returns a spawned construction to the editor with its unsaved changes");

        RemoveSaveTestFiles();
        string pathR = MakeSavedConstruction(SaveTestPrefix + "R", 1, "recall");
        var catalog = BlockCatalog.Instance;
        EditorHandoff.Draft = null;
        EditorHandoff.ResumeDraft = false;
        EditorHandoff.PendingSpawnWorkbenchName = null;
        EditorHandoff.PendingPlayerPosition = null;

        // ---- меню верстака: Continue editing
        var world = new GameWorld { Name = "SelfTestContinueWorld" };
        editor.AddChild(world);
        await Frames(editor, 2);
        var bench = world.Workbenches[0];
        world.OpenWorkbenchMenuForTesting(bench); // как E по верстаку: запоминает верстак и открывает меню
        await Frames(editor, 2);
        Check(world.WorkbenchMenu.IsOpen, "(setup) the workbench menu is open");
        Check(!world.WorkbenchMenu.IsContinueButtonVisible, "with nothing left in the editor there is no 'Continue editing' button");

        var source = new Construction(new VoxelGrid());
        source.PlaceBlock(Vector3I.Zero, new Vector3I(2, 1, 1), catalog.Get("block"), Colors.White);
        world.WorkbenchMenu.Close();
        EditorHandoff.Draft = new EditorDraft(EditorDraft.Serialize(source, pathR, SaveTestPrefix + "R", "recall"), pathR, SaveTestPrefix + "R", "recall");
        world.WorkbenchMenu.Open(bench.Name);
        await Frames(editor, 2);
        Check(world.WorkbenchMenu.IsContinueButtonVisible && world.WorkbenchMenu.ContinueButtonText.Contains(SaveTestPrefix + "R"),
            "once a construction was left in the editor the menu offers 'Continue editing: <name>'", world.WorkbenchMenu.ContinueButtonText);
        world.WorkbenchMenu.Close();
        EditorHandoff.Draft = new EditorDraft(EditorDraft.Serialize(source, null, "", ""), null, "", "");
        world.WorkbenchMenu.Open(bench.Name);
        Check(world.WorkbenchMenu.ContinueButtonText.Contains("unsaved vehicle"), "...an unnamed one is called 'unsaved vehicle'", world.WorkbenchMenu.ContinueButtonText);

        world.WorkbenchMenu.Close();
        world.OpenWorkbenchMenuForTesting(bench);
        await Frames(editor, 2);
        var continueButton = FindButtons(editor).Find(b => b.Text.StartsWith("Continue editing"));
        Check(continueButton != null && world.EditorEntryCount == 0, "(setup) the menu is open with the button visible");
        continueButton?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(editor, 2);
        Check(world.EditorEntryCount == 1 && EditorHandoff.ResumeDraft && EditorHandoff.PendingConstructionPath == null && EditorHandoff.PendingSpawnWorkbenchName == bench.Name,
            "'Continue editing' enters the editor in resume mode (not a new vehicle, not a file) through this workbench",
            $"entries={world.EditorEntryCount} resume={EditorHandoff.ResumeDraft} bench={EditorHandoff.PendingSpawnWorkbenchName}");
        Check(EditorHandoff.Draft != null, "...and the remembered construction is still there");
        world.QueueFree();
        await Frames(editor, 2);

        // ---- R по постройке
        EditorHandoff.Draft = null;
        EditorHandoff.ResumeDraft = false;
        EditorHandoff.PendingSpawnWorkbenchName = null;
        EditorHandoff.PendingPlayerPosition = null;
        world = new GameWorld { Name = "SelfTestRecallWorld" };
        editor.AddChild(world);
        await Frames(editor, 2);
        bench = world.Workbenches[1];
        var player = world.PlayerForTesting;

        // Постройка заспавнена ИЗ ФАЙЛА R (1 блок), но перед спавном в редакторе добавили ещё два блока и не сохранили: в JSON спавна их три.
        source.Clear();
        source.PlaceBlock(Vector3I.Zero, new Vector3I(3, 1, 1), catalog.Get("block"), Colors.White);
        string json = EditorDraft.Serialize(source, pathR, SaveTestPrefix + "R", "recall");
        var vehicle = VehicleSpawner.Spawn(world, json, new Vector3(0, 5, 0), bench);
        vehicle.SourceWorkbenchName = bench.Name;
        vehicle.Freeze = true; // не падать на землю - тест целится лучом
        await Frames(editor, 4);
        Check(BlocksInFile(pathR) == 1 && BlocksInJson(vehicle.SourceJson) == 3 && vehicle.SourceJson == json, "(setup) the body remembers the JSON it was built from (3 blocks), the file on disk has only 1");

        var center = vehicle.ToGlobal(BuildSpace.CellCenter(Vector3I.Zero));
        player.SetPose(center + new Vector3(0, 20, 0), 0); // прицел в пустоту
        await Frames(editor, 4);
        Send(new InputEventKey { PhysicalKeycode = Godot.Key.R, Keycode = Godot.Key.R, Pressed = true });
        await Frames(editor, 2);
        Send(new InputEventKey { PhysicalKeycode = Godot.Key.R, Keycode = Godot.Key.R, Pressed = false });
        Check(world.EditorEntryCount == 0 && GodotObject.IsInstanceValid(vehicle) && EditorHandoff.Draft == null, "R aimed at nothing does nothing");

        player.SetPose(center + new Vector3(0, -1.6f, 1.5f), 0); // камера в 1.5 м перед постройкой, смотрит на неё
        await Frames(editor, 4);
        Send(new InputEventKey { PhysicalKeycode = Godot.Key.R, Keycode = Godot.Key.R, Pressed = true });
        await Frames(editor, 2);
        Send(new InputEventKey { PhysicalKeycode = Godot.Key.R, Keycode = Godot.Key.R, Pressed = false });
        await Frames(editor, 2);

        Check(world.EditorEntryCount == 1 && (!GodotObject.IsInstanceValid(vehicle) || vehicle.IsQueuedForDeletion()), "R aimed at a construction takes it away from the world and goes to the editor",
            $"entries={world.EditorEntryCount}");
        var draft = EditorHandoff.Draft;
        Check(draft != null && BlocksInJson(draft.Json) == 3, "...the editor gets the construction exactly as it was spawned: 3 blocks, including the ones never saved to disk", $"blocks={(draft == null ? -1 : BlocksInJson(draft.Json))}");
        Check(draft != null && draft.SavedPath == pathR && draft.Name == SaveTestPrefix + "R" && draft.Description == "recall", "...linked to its save file (so Save can overwrite it) with its name and description", $"{draft}");
        Check(EditorHandoff.ResumeDraft && EditorHandoff.PendingConstructionPath == null && EditorHandoff.PendingSpawnWorkbenchName == bench.Name,
            "...in resume mode, through the workbench it was spawned from (Spawn puts it back into the same zone)", $"resume={EditorHandoff.ResumeDraft} bench={EditorHandoff.PendingSpawnWorkbenchName}");
        Check(EditorHandoff.PendingPlayerPosition is { } back && back.DistanceTo(bench.GlobalPosition) < 4, "...and the player comes back next to that workbench", $"{EditorHandoff.PendingPlayerPosition}");

        // Дальше - ровно то, что делает редактор при входе.
        editor.World.Construction.Clear();
        editor.Ui.ResumeFrom(editor, draft!);
        Check(editor.World.Grid.BlockCount == 3 && editor.Ui.CurrentSavedPath == pathR, "the editor opens with those 3 blocks and Save already points at the old file", $"blocks={editor.World.Grid.BlockCount} path={editor.Ui.CurrentSavedPath}");

        editor.World.Construction.Clear();
        editor.World.RebuildDirty();
        editor.Ui.ResumeFrom(editor, new EditorDraft(ConstructionIO.Serialize(editor.World.Construction), null, "", ""));
        EditorHandoff.Draft = null;
        EditorHandoff.ResumeDraft = false;
        EditorHandoff.PendingSpawnWorkbenchName = null;
        EditorHandoff.PendingPlayerPosition = null;
        RemoveSaveTestFiles();
        world.QueueFree();
        await Frames(editor, 2);
        editor.EditorCamera.Current = true;
    }
}
