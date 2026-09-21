using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SwV2.Core;
using SwV2.Editor;

namespace SwV2.Dev;

/// <summary>
/// Самотесты редактора. Запуск: <c>godot --headless --path . -- --selftest</c> (код выхода 0 = все тесты прошли).
/// Часть 1 — чистая логика (меширование, каркас, рейкаст, границы чанков), часть 2 — интеграционные проверки
/// через имитацию реального ввода (Input.ParseInputEvent): ПКМ/ЛКМ, Tab, 1–9, колесо, WASD, СКМ, блокировка UI.
/// </summary>
public sealed class SelfTest
{
    private const ushort Steel = 1;
    private const ushort Aluminium = 2;

    private int _passed;
    private int _failed;

    public static async Task Frames(Node node, int count)
    {
        for (int i = 0; i < count; i++) await node.ToSignal(node.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    public static async Task RunAsync(BuildEditor editor)
    {
        var test = new SelfTest();
        GD.Print("=== SW_V2 self-test ===");

        test.RunMesherTests();
        test.RunIncrementalTests();
        test.RunRaycastTests();
        await test.RunEditorTests(editor);

        GD.Print($"=== self-test finished: {test._passed} passed, {test._failed} failed ===");
        editor.GetTree().Quit(test._failed == 0 ? 0 : 1);
    }

    private void Check(bool ok, string name, string detail = "")
    {
        if (ok)
        {
            _passed++;
            GD.Print($"  PASS  {name}");
        }
        else
        {
            _failed++;
            GD.PrintErr($"  FAIL  {name} {detail}");
        }
    }

    // ================================================================== меширование

    private readonly record struct Stats(int Faces, int Quads, int Segments, double EdgeUnits, int BadWinding, double QuadArea);

    private static IEnumerable<Vector3I> ChunksToBuild(VoxelGrid grid)
    {
        // Чанк без блоков тоже может владеть рёбрами (на границе с соседом), поэтому берём соседей всех непустых.
        var coords = new HashSet<Vector3I>();
        foreach (var c in grid.ChunkCoords)
        {
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                coords.Add(c + new Vector3I(dx, dy, dz));
            }
        }

        return coords;
    }

    private static Stats Measure(VoxelGrid grid)
    {
        int faces = 0, quads = 0, segments = 0, bad = 0;
        double units = 0, area = 0;

        foreach (var coord in ChunksToBuild(grid))
        {
            var d = ChunkMesher.Build(grid, coord);
            faces += d.VisibleFaces;
            quads += d.Quads;
            segments += d.LineSegments;

            for (int i = 0; i < d.LineVertices.Count; i += 2)
            {
                units += (d.LineVertices[i + 1] - d.LineVertices[i]).Length() / BuildSpace.CellSize;
            }

            for (int t = 0; t < d.Indices.Count; t += 3)
            {
                var a = d.Vertices[d.Indices[t]];
                var b = d.Vertices[d.Indices[t + 1]];
                var c = d.Vertices[d.Indices[t + 2]];
                // Godot: лицевая сторона — по часовой стрелке, т.е. (b-a)x(c-a) направлен ПРОТИВ нормали.
                if ((b - a).Cross(c - a).Dot(d.Normals[d.Indices[t]]) >= 0) bad++;
            }

            for (int q = 0; q < d.Vertices.Count; q += 4)
            {
                area += (d.Vertices[q + 1] - d.Vertices[q]).Length() * (d.Vertices[q + 3] - d.Vertices[q]).Length()
                        / (BuildSpace.CellSize * BuildSpace.CellSize);
            }
        }

        return new Stats(faces, quads, segments, units, bad, area);
    }

    /// <summary>Независимый «в лоб» подсчёт видимых граней и характерных рёбер прямо по сетке (без чанков и паддинга).</summary>
    private static (int Faces, int EdgeUnits) BruteForce(VoxelGrid g, Vector3I min, Vector3I max)
    {
        int faces = 0, edges = 0;
        var dirs = new[] { Vector3I.Right, Vector3I.Left, Vector3I.Up, Vector3I.Down, Vector3I.Back, Vector3I.Forward };

        for (int z = min.Z - 1; z <= max.Z + 1; z++)
        for (int y = min.Y - 1; y <= max.Y + 1; y++)
        for (int x = min.X - 1; x <= max.X + 1; x++)
        {
            var cell = new Vector3I(x, y, z);
            if (g.IsSolid(cell))
            {
                foreach (var dir in dirs)
                {
                    if (!g.IsSolid(cell + dir)) faces++;
                }
            }

            for (int axis = 0; axis < 3; axis++)
            {
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                bool Solid(int du, int dv)
                {
                    var c = cell;
                    c[u] += du;
                    c[v] += dv;
                    return g.IsSolid(c);
                }

                bool s0 = Solid(-1, -1), s1 = Solid(0, -1), s2 = Solid(-1, 0), s3 = Solid(0, 0);
                int count = (s0 ? 1 : 0) + (s1 ? 1 : 0) + (s2 ? 1 : 0) + (s3 ? 1 : 0);
                if (count == 1 || count == 3 || (count == 2 && s0 == s3)) edges++;
            }
        }

        return (faces, edges);
    }

    private static VoxelGrid Box(Vector3I min, Vector3I max, ushort id = Steel)
    {
        var grid = new VoxelGrid();
        DemoBuilds.Fill(grid, min, max, id);
        return grid;
    }

    private void CheckShape(string name, VoxelGrid grid, int faces, int quads, int segments, int edgeUnits)
    {
        var s = Measure(grid);
        Check(s.Faces == faces && s.Quads == quads && s.Segments == segments && Math.Abs(s.EdgeUnits - edgeUnits) < 1e-3
              && s.BadWinding == 0 && Math.Abs(s.QuadArea - s.Faces) < 1e-3,
            name,
            $"got faces={s.Faces} quads={s.Quads} segments={s.Segments} edgeUnits={s.EdgeUnits:0.###} badWinding={s.BadWinding} area={s.QuadArea:0.###}; " +
            $"expected faces={faces} quads={quads} segments={segments} edgeUnits={edgeUnits}");
    }

    private void RunMesherTests()
    {
        GD.Print("-- mesher: face culling, polygon merging, unified wireframe");

        CheckShape("single block: 6 faces -> 6 quads, cube wireframe (12 edges)", Box(new(0, 0, 0), new(0, 0, 0)), 6, 6, 12, 12);
        CheckShape("2 blocks glued: hidden inner faces removed, 10 faces -> 6 quads, no seam in wireframe",
            Box(new(0, 0, 0), new(1, 0, 0)), 10, 6, 12, 16);
        CheckShape("3x3x3 cube: 54 faces -> 6 quads, 12 long edges", Box(new(0, 0, 0), new(2, 2, 2)), 54, 6, 12, 36);
        CheckShape("block at negative coordinates (chunk -1)", Box(new(-1, -1, -1), new(-1, -1, -1)), 6, 6, 12, 12);

        var lShape = new VoxelGrid();
        foreach (var c in new[] { new Vector3I(0, 0, 0), new Vector3I(1, 0, 0), new Vector3I(0, 0, 1) })
        {
            lShape.TrySet(c, Steel, CellColor.Pack(Colors.Gray));
        }

        CheckShape("L-shape: concave corner edge is kept, coplanar seams are not", lShape, 14, 10, 18, 22);

        var painted = Box(new(0, 0, 0), new(2, 2, 2));
        painted.TryPaint(new Vector3I(1, 2, 1), CellColor.Pack(Colors.Red));
        CheckShape("painted top center: faces split by color (10 quads) but wireframe unchanged", painted, 54, 10, 12, 36);

        var mixed = new VoxelGrid();
        uint gray = CellColor.Pack(Colors.Gray);
        mixed.TrySet(new Vector3I(0, 0, 0), Steel, gray);
        mixed.TrySet(new Vector3I(1, 0, 0), Aluminium, gray);
        CheckShape("different block types with the same color merge into common polygons", mixed, 10, 6, 12, 16);

        CheckShape("chunk boundary (x=15|16): wireframe still unified, no seam edge",
            Box(new(15, 0, 0), new(16, 0, 0)), 10, 10, 16, 16);

        // Случайная постройка через границы чанков и нулевые/отрицательные координаты: сверка с независимым подсчётом.
        var rng = new Random(12345);
        var blob = new VoxelGrid();
        for (int z = -10; z <= 12; z++)
        for (int y = -3; y <= 12; y++)
        for (int x = -10; x <= 12; x++)
        {
            if (rng.NextDouble() < 0.4) blob.TrySet(new Vector3I(x, y, z), Steel, CellColor.Pack(rng.Next(3) == 0 ? Colors.Red : Colors.Gray));
        }

        var expected = BruteForce(blob, new Vector3I(-10, -3, -10), new Vector3I(12, 12, 12));
        var actual = Measure(blob);
        Check(actual.Faces == expected.Faces, $"random blob ({blob.BlockCount} blocks): visible faces match brute force",
            $"got {actual.Faces}, expected {expected.Faces}");
        Check(Math.Abs(actual.EdgeUnits - expected.EdgeUnits) < 1e-3,
            "random blob: wireframe edges (across chunk borders) match brute force, none duplicated or missing",
            $"got {actual.EdgeUnits}, expected {expected.EdgeUnits}");
        Check(Math.Abs(actual.QuadArea - actual.Faces) < 1e-3, "random blob: merging is lossless (quad area == visible faces)");
        Check(actual.BadWinding == 0, "random blob: all triangles have front-face winding matching their normals");
        Check(actual.Quads < actual.Faces, $"random blob: merging reduces polygon count ({actual.Faces} faces -> {actual.Quads} quads)");
    }

    // ================================================================== инкрементальные обновления

    private static int Signature(VoxelGrid grid, Vector3I chunk)
    {
        var d = ChunkMesher.Build(grid, chunk);
        var hash = new HashCode();
        foreach (var v in d.Vertices) { hash.Add(v.X); hash.Add(v.Y); hash.Add(v.Z); }
        foreach (var c in d.Colors) { hash.Add(c.R); hash.Add(c.G); hash.Add(c.B); }
        foreach (var i in d.Indices) hash.Add(i);
        foreach (var v in d.LineVertices) { hash.Add(v.X); hash.Add(v.Y); hash.Add(v.Z); }
        return hash.ToHashCode();
    }

    private void RunIncrementalTests()
    {
        GD.Print("-- incremental updates: every chunk whose mesh changes must be marked dirty");

        var rng = new Random(777);
        var grid = new VoxelGrid();
        for (int z = -20; z <= 20; z++)
        for (int y = -5; y <= 20; y++)
        for (int x = -20; x <= 20; x++)
        {
            if (rng.NextDouble() < 0.3) grid.TrySet(new Vector3I(x, y, z), Steel, CellColor.Pack(Colors.Gray));
        }

        int[] interesting = { -17, -16, -15, -1, 0, 1, 14, 15, 16, 17 };
        var scratch = new List<Vector3I>();
        int violations = 0, edits = 0;

        for (int n = 0; n < 80; n++)
        {
            var cell = new Vector3I(interesting[rng.Next(interesting.Length)], interesting[rng.Next(interesting.Length)] % 8, interesting[rng.Next(interesting.Length)]);

            var watch = new HashSet<Vector3I>();
            foreach (var c in grid.ChunkCoords)
            {
                for (int dz = -2; dz <= 2; dz++)
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    watch.Add(c + new Vector3I(dx, dy, dz));
                }
            }

            var before = new Dictionary<Vector3I, int>();
            foreach (var c in watch) before[c] = Signature(grid, c);

            bool changed = grid.IsSolid(cell) ? grid.TryRemove(cell) : grid.TrySet(cell, Steel, CellColor.Pack(Colors.Gray));
            if (!changed) continue;
            edits++;

            VoxelGrid.GetAffectedChunks(cell, scratch);
            foreach (var c in watch)
            {
                if (Signature(grid, c) != before[c] && !scratch.Contains(c)) violations++;
            }
        }

        Check(edits > 30 && violations == 0, $"dirty-chunk propagation is complete ({edits} boundary edits, {violations} missed chunks)");
    }

    // ================================================================== рейкаст

    private void RunRaycastTests()
    {
        GD.Print("-- raycast");
        var empty = new VoxelGrid();
        var one = Box(new(0, 0, 0), new(0, 0, 0));
        var stack = Box(new(0, 0, 0), new(0, 1, 0));

        var hit = VoxelRaycaster.Cast(empty, new Vector3(0.125f, 2f, 0.125f), Vector3.Down);
        Check(hit.Found && !hit.IsBlock && hit.PlaceCell == new Vector3I(0, 0, 0) && hit.Normal == Vector3I.Up,
            "ray down onto the ground grid: place cell (0,0,0) above the plane", hit.ToString());

        hit = VoxelRaycaster.Cast(one, new Vector3(0.125f, 2f, 0.125f), Vector3.Down);
        Check(hit.IsBlock && hit.BlockCell == new Vector3I(0, 0, 0) && hit.PlaceCell == new Vector3I(0, 1, 0),
            "ray down onto a block: hits its top, place cell above", hit.ToString());

        hit = VoxelRaycaster.Cast(one, new Vector3(-1f, 0.125f, 0.125f), Vector3.Right);
        Check(hit.IsBlock && hit.BlockCell == new Vector3I(0, 0, 0) && hit.Normal == new Vector3I(-1, 0, 0) && hit.PlaceCell == new Vector3I(-1, 0, 0),
            "horizontal ray: hits the side face, place cell beside the block", hit.ToString());

        hit = VoxelRaycaster.Cast(empty, new Vector3(0.125f, -2f, 0.125f), Vector3.Up);
        Check(hit.Found && !hit.IsBlock && hit.PlaceCell == new Vector3I(0, -1, 0) && hit.Normal == Vector3I.Down,
            "ray up from below the ground: place cell (0,-1,0) under the plane", hit.ToString());

        hit = VoxelRaycaster.Cast(empty, new Vector3(0f, 2f, 0f), Vector3.Up);
        Check(!hit.Found, "ray into the sky hits nothing");

        hit = VoxelRaycaster.Cast(empty, new Vector3(100f, 2f, 0f), Vector3.Down);
        Check(!hit.Found, "ground hit outside the build area is ignored");

        hit = VoxelRaycaster.Cast(one, new Vector3(2f, 1f, 2f), (new Vector3(0.125f, 0.125f, 0.125f) - new Vector3(2f, 1f, 2f)).Normalized());
        Check(hit.IsBlock && hit.BlockCell == new Vector3I(0, 0, 0), "oblique ray hits the block", hit.ToString());

        // Регрессия: при многих шагах DDA ошибка округления давала «землю» на клетку ниже плоскости (y = -1).
        var rng = new Random(99);
        int wrong = 0, groundHits = 0;
        for (int n = 0; n < 2000; n++)
        {
            var origin = new Vector3((float)(rng.NextDouble() * 40 - 20), (float)(rng.NextDouble() * 20 + 0.3), (float)(rng.NextDouble() * 40 - 20));
            var target = new Vector3((float)(rng.NextDouble() * 40 - 20), 0f, (float)(rng.NextDouble() * 40 - 20));
            var dir = (target - origin).Normalized();
            var h = VoxelRaycaster.Cast(empty, origin, dir);
            var expected = new Vector3I(
                Mathf.FloorToInt(target.X / BuildSpace.CellSize), 0, Mathf.FloorToInt(target.Z / BuildSpace.CellSize));
            if (!BuildSpace.InBounds(expected)) continue;
            groundHits++;
            if (!(h.Found && !h.IsBlock && h.PlaceCell == expected && h.Normal == Vector3I.Up)) wrong++;
        }

        Check(groundHits > 1500 && wrong == 0, $"{groundHits} random rays onto the ground always hit the correct cell above the plane ({wrong} wrong)");

        hit = VoxelRaycaster.Cast(stack, new Vector3(0.125f, 3f, 0.125f), Vector3.Down);
        Check(hit.IsBlock && hit.BlockCell == new Vector3I(0, 1, 0), "ray hits the nearest block of a stack first", hit.ToString());
    }

    // ================================================================== интеграция: реальный ввод

    private static void Send(InputEvent e) => Input.ParseInputEvent(e);

    private static async Task Move(BuildEditor editor, Vector2 p)
    {
        Send(new InputEventMouseMotion { Position = p, GlobalPosition = p });
        await Frames(editor, 2);
    }

    private static async Task Click(BuildEditor editor, Vector2 p, MouseButton button)
    {
        await Move(editor, p);
        Send(new InputEventMouseButton { ButtonIndex = button, Pressed = true, Position = p, GlobalPosition = p });
        await Frames(editor, 1);
        Send(new InputEventMouseButton { ButtonIndex = button, Pressed = false, Position = p, GlobalPosition = p });
        await Frames(editor, 2);
    }

    private static async Task PressKey(BuildEditor editor, Godot.Key key)
    {
        Send(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
        await Frames(editor, 1);
        Send(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
        await Frames(editor, 1);
    }

    private static List<Button> FindButtons(Node root)
    {
        var result = new List<Button>();
        foreach (var node in root.GetTree().Root.FindChildren("*", "Button", true, false))
        {
            if (node is Button button && button.IsVisibleInTree()) result.Add(button);
        }

        return result;
    }

    private async Task RunEditorTests(BuildEditor editor)
    {
        GD.Print("-- editor: real input events through the whole pipeline");
        var grid = editor.World.Grid;
        var state = editor.State;
        var camera = editor.EditorCamera;
        bool accumulated = Input.UseAccumulatedInput;
        Input.UseAccumulatedInput = false;

        // Без окна (headless) корневой вьюпорт всего 64x64 — задаём реальный размер, чтобы вёрстка UI и проекции были осмысленными.
        editor.GetTree().Root.Size = new Vector2I(1600, 900);
        await Frames(editor, 3);
        GD.Print($"  info  viewport={editor.GetViewport().GetVisibleRect().Size} display={DisplayServer.GetName()} window={DisplayServer.WindowGetSize()}");
        grid.Clear();
        state.Tool = ToolMode.None;
        state.Wire = WireMode.Off;

        Check(BlockRegistry.All.Count >= EditorState.HotbarSize, $"{BlockRegistry.All.Count} blocks available for a {EditorState.HotbarSize}-slot hotbar");
        bool filled = true;
        for (int i = 0; i < EditorState.HotbarSize; i++) filled &= state.GetSlot(i) != BlockRegistry.None;
        Check(filled, "hotbar has 9 slots, all filled by default");

        camera.LookAtPoint(new Vector3(1.5f, 2f, 2.5f), new Vector3(0.125f, 0.0f, 0.125f));
        await Frames(editor, 2);

        Vector2 Screen(Vector3 world) => camera.UnprojectPosition(world);
        Vector3 TopOf(Vector3I cell) => BuildSpace.CellCenter(cell) + new Vector3(0, BuildSpace.CellSize / 2, 0);

        // 4. ПКМ ставит блок: сначала на землю, затем на верхнюю грань блока.
        state.SelectedSlot = 0;
        var ground = Screen(new Vector3(0.125f, 0f, 0.125f));
        await Move(editor, ground);
        Check(editor.Hover.Found && !editor.Hover.IsBlock && editor.Hover.PlaceCell == new Vector3I(0, 0, 0), "cursor over ground targets cell (0,0,0)", $"{editor.Hover} ground={ground} overUi={editor.Ui.IsPointOverUi(ground)} cam={camera.GlobalPosition}");

        await Click(editor, ground, MouseButton.Right);
        Check(grid.GetId(new Vector3I(0, 0, 0)) == state.SelectedBlockId, "RMB places the selected hotbar block on the ground");

        var top = Screen(TopOf(new Vector3I(0, 0, 0)));
        await Click(editor, top, MouseButton.Right);
        Check(grid.GetId(new Vector3I(0, 1, 0)) != 0, "RMB on a block face places a block adjacent to it");

        state.SelectedSlot = 5;
        await Click(editor, Screen(TopOf(new Vector3I(0, 1, 0))), MouseButton.Right);
        Check(grid.GetId(new Vector3I(0, 2, 0)) == state.GetSlot(5), "block type follows the selected hotbar slot");
        await Click(editor, Screen(TopOf(new Vector3I(0, 2, 0))), MouseButton.Left);
        Check(grid.BlockCount == 3, "LMB with no tool selected does nothing");

        // 3. Инструменты тулбара на ЛКМ.
        state.PaintColor = Colors.Red;
        state.Tool = ToolMode.Paint;
        var target = new Vector3I(0, 2, 0);
        await Click(editor, Screen(TopOf(target)), MouseButton.Left);
        Check(grid.GetColor(target) == CellColor.Pack(Colors.Red) && grid.GetId(target) != 0, "paint tool recolors the block under the cursor");

        state.Tool = ToolMode.Delete;
        await Click(editor, Screen(TopOf(target)), MouseButton.Left);
        Check(grid.GetId(target) == 0 && grid.BlockCount == 2, "delete tool removes the block under the cursor");
        state.Tool = ToolMode.None;

        // wireframe-инструмент: три режима
        editor.World.RebuildDirty();
        var seen = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            int solidVisible = 0, wireVisible = 0;
            foreach (var child in editor.World.GetChildren())
            {
                if (child is not MeshInstance3D mesh) continue;
                if (mesh.Name.ToString().StartsWith("Solid_") && mesh.Visible) solidVisible++;
                if (mesh.Name.ToString().StartsWith("Wire_") && mesh.Visible) wireVisible++;
            }

            seen.Add($"{state.Wire}:solid={solidVisible},wire={wireVisible}");
            state.CycleWire();
        }

        Check(seen[0].EndsWith("solid=1,wire=0") && seen[1].EndsWith("solid=1,wire=1") && seen[2].EndsWith("solid=0,wire=1") && state.Wire == WireMode.Off,
            "wireframe tool cycles solid -> solid+wire -> wire only -> solid", string.Join(" | ", seen));

        // 2. Хотбар: клавиши 1-9, колесо, Tab.
        await PressKey(editor, Godot.Key.Key3);
        Check(state.SelectedSlot == 2, "key 3 selects hotbar slot 3");
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true, Position = new Vector2(400, 300) });
        await Frames(editor, 2);
        Check(state.SelectedSlot == 3, "mouse wheel down selects the next slot");
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Position = new Vector2(400, 300) });
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Position = new Vector2(400, 300) });
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Position = new Vector2(400, 300) });
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Position = new Vector2(400, 300) });
        await Frames(editor, 2);
        Check(state.SelectedSlot == 8, "mouse wheel wraps around the hotbar (slot 4 -> 4 steps back -> slot 9)");

        int blocksBefore = grid.BlockCount;
        await PressKey(editor, Godot.Key.Tab);
        Check(editor.Ui.PickerOpen, "Tab opens the block list");
        await Click(editor, ground, MouseButton.Right);
        Check(grid.BlockCount == blocksBefore, "world clicks are blocked while the block list is open");
        await PressKey(editor, Godot.Key.Tab);
        Check(!editor.Ui.PickerOpen, "Tab closes the block list");

        // Интерфейс не пропускает клики в мир.
        var view = editor.GetViewport().GetVisibleRect().Size;
        var overHotbar = new Vector2(view.X / 2, view.Y - 50);
        var overToolbar = new Vector2(view.X - 90, view.Y / 2);
        Check(editor.Ui.IsPointOverUi(overHotbar) && editor.Ui.IsPointOverUi(overToolbar), "hotbar and toolbar areas are recognized as UI");
        await Click(editor, overHotbar, MouseButton.Right);
        Check(grid.BlockCount == blocksBefore && !editor.Hover.Found, "clicking on the hotbar does not place blocks in the world");

        // Клики по элементам интерфейса: слоты хотбара, кнопки тулбара, карточки блоков.
        await Click(editor, editor.Ui.GetHotbarSlotCenter(6), MouseButton.Left);
        Check(state.SelectedSlot == 6, "clicking a hotbar slot selects it");

        Button? FindButton(string text) => FindButtons(editor).Find(b => b.Text == text || b.TooltipText == text);
        Vector2 CenterOf(Button b) => b.GetGlobalRect().GetCenter();

        await Click(editor, CenterOf(FindButton("Delete")!), MouseButton.Left);
        Check(state.Tool == ToolMode.Delete, "toolbar: Delete button activates the delete tool");
        await Click(editor, CenterOf(FindButton("Delete")!), MouseButton.Left);
        Check(state.Tool == ToolMode.None, "toolbar: pressing the active tool again deactivates it");
        await Click(editor, CenterOf(FindButton("Paint")!), MouseButton.Left);
        Check(state.Tool == ToolMode.Paint, "toolbar: Paint button activates the paint tool");
        await Click(editor, CenterOf(FindButton("Delete")!), MouseButton.Left);
        Check(state.Tool == ToolMode.Delete, "toolbar: tools are mutually exclusive");
        state.Tool = ToolMode.None;
        await Click(editor, CenterOf(FindButton("Wireframe: off")!), MouseButton.Left);
        Check(state.Wire == WireMode.Overlay, "toolbar: wireframe button switches the display mode");
        state.Wire = WireMode.Off;

        await PressKey(editor, Godot.Key.Tab);
        await Click(editor, CenterOf(FindButton("Copper")!), MouseButton.Left);
        Check(state.GetSlot(state.SelectedSlot) == 4, "block list: clicking a block puts it into the selected hotbar slot");
        await PressKey(editor, Godot.Key.Escape);
        Check(!editor.Ui.PickerOpen, "Esc closes the block list");

        // Перетаскивание инструмента с зажатой ЛКМ: три блока в ряд, «проедания насквозь» без движения мыши нет.
        grid.Clear();
        DemoBuilds.Fill(grid, new Vector3I(0, 0, 0), new Vector3I(2, 0, 0), Steel);
        DemoBuilds.Fill(grid, new Vector3I(0, 0, 1), new Vector3I(2, 0, 1), Steel);
        camera.LookAtPoint(new Vector3(0.4f, 2.5f, 2.0f), new Vector3(0.4f, 0.0f, 0.2f));
        await Frames(editor, 2);
        var p0 = Screen(TopOf(new Vector3I(0, 0, 0)));
        var p1 = Screen(TopOf(new Vector3I(1, 0, 0)));
        var p2 = Screen(TopOf(new Vector3I(2, 0, 0)));
        state.PaintColor = Colors.Blue;
        state.Tool = ToolMode.Paint;
        await Move(editor, p0);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = p0, GlobalPosition = p0 });
        await Frames(editor, 1);
        await Move(editor, p1);
        await Move(editor, p2);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = p2, GlobalPosition = p2 });
        await Frames(editor, 2);
        uint blue = CellColor.Pack(Colors.Blue);
        Check(grid.GetColor(new Vector3I(0, 0, 0)) == blue && grid.GetColor(new Vector3I(1, 0, 0)) == blue && grid.GetColor(new Vector3I(2, 0, 0)) == blue,
            "holding LMB and dragging paints every block passed over");
        Check(grid.GetColor(new Vector3I(1, 0, 1)) != blue, "drag painting does not touch blocks that were not under the cursor");

        int before = grid.BlockCount;
        state.Tool = ToolMode.Delete;
        await Move(editor, p1);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = p1, GlobalPosition = p1 });
        await Frames(editor, 30); // мышь неподвижна: должен удалиться ровно один блок
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = p1, GlobalPosition = p1 });
        await Frames(editor, 2);
        Check(grid.BlockCount == before - 1 && grid.GetId(new Vector3I(1, 0, 0)) == 0,
            "holding LMB without moving deletes exactly one block (no tunnelling through the build)", $"blocks {before} -> {grid.BlockCount}");
        state.Tool = ToolMode.None;

        // 1. Камера: WASD и поворот по СКМ.
        camera.LookAtPoint(new Vector3(1.5f, 2f, 2.5f), new Vector3(0.125f, 0.0f, 0.125f));
        await Frames(editor, 2);
        var start = camera.GlobalPosition;
        var forward = -camera.GlobalTransform.Basis.Z;
        Send(new InputEventKey { Keycode = Godot.Key.W, PhysicalKeycode = Godot.Key.W, Pressed = true });
        await Frames(editor, 15);
        Send(new InputEventKey { Keycode = Godot.Key.W, PhysicalKeycode = Godot.Key.W, Pressed = false });
        await Frames(editor, 2);
        var moved = camera.GlobalPosition - start;
        Check(moved.Length() > 0.01 && moved.Normalized().Dot(forward) > 0.99, "W moves the camera forward along its view direction", $"moved {moved}");

        start = camera.GlobalPosition;
        var right = camera.GlobalTransform.Basis.X;
        Send(new InputEventKey { Keycode = Godot.Key.D, PhysicalKeycode = Godot.Key.D, Pressed = true });
        await Frames(editor, 10);
        Send(new InputEventKey { Keycode = Godot.Key.D, PhysicalKeycode = Godot.Key.D, Pressed = false });
        await Frames(editor, 2);
        moved = camera.GlobalPosition - start;
        Check(moved.Length() > 0.01 && moved.Normalized().Dot(right) > 0.99, "D strafes the camera right", $"moved {moved}");

        double yawBefore = camera.Yaw;
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = true, Position = ground, GlobalPosition = ground });
        await Frames(editor, 2);
        Send(new InputEventMouseMotion { Relative = new Vector2(120, 0), Position = ground });
        await Frames(editor, 2);
        // В headless-режиме дисплейный сервер не хранит режим мыши — проверяем захват только в оконном режиме.
        bool captured = DisplayServer.GetName() == "headless" || Input.MouseMode == Input.MouseModeEnum.Captured;
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = false, Position = ground, GlobalPosition = ground });
        await Frames(editor, 2);
        Check(captured && Math.Abs(camera.Yaw - yawBefore) > 0.1 && (DisplayServer.GetName() == "headless" || Input.MouseMode == Input.MouseModeEnum.Visible),
            "holding MMB captures the mouse and rotates the camera; release restores the cursor");

        Input.UseAccumulatedInput = accumulated;
        grid.Clear();
        editor.World.RebuildDirty();
    }
}
