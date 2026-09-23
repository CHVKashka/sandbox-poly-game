using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;

namespace SandboxPolyGame.Dev;

/// <summary>
/// Самотесты редактора. Запуск: <c>godot --headless --path . -- --selftest</c> (код выхода 0 = все тесты прошли).
/// Часть 1 — чистая логика (меширование, каркас, рейкаст, границы чанков, каталог блоков, Construction/Resize/JSON),
/// часть 2 — интеграционные проверки через имитацию реального ввода (Input.ParseInputEvent): ЛКМ/ПКМ, Tab, 1–9,
/// колесо, WASD, СКМ, блокировка UI, диалог Resize.
/// </summary>
public sealed class SelfTest
{
    private static readonly ushort Block = BlockCatalog.Instance.Get("block").RuntimeId;

    /// <summary>
    /// Любой id, заведомо не совпадающий ни с одним реальным блоком каталога (сейчас их всего 4) — используется
    /// только там, где тесту нужны два РАЗНЫХ числовых id, а не конкретный настоящий блок (ChunkMesher не знает
    /// про каталог блоков вообще, поэтому для чистого меширования подходит любое число).
    /// </summary>
    private const ushort OtherType = 250;

    private int _passed;
    private int _failed;

    public static async Task Frames(Node node, int count)
    {
        for (int i = 0; i < count; i++) await node.ToSignal(node.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    public static async Task RunAsync(BuildEditor editor)
    {
        var test = new SelfTest();
        GD.Print("=== sandbox-poly-game self-test ===");

        test.RunMesherTests();
        test.RunIncrementalTests();
        test.RunRaycastTests();
        test.RunBlockCatalogTests();
        test.RunShapeGeometryTests();
        test.RunNonCubeMeshingTests();
        test.RunRotationStateTests();
        test.RunConstructionTests();
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

    private readonly record struct Stats(int Faces, int Quads, int Segments, int BadWinding, double QuadArea);

    private static IEnumerable<Vector3I> ChunksToBuild(VoxelGrid grid)
    {
        // Чанк без блоков тоже может владеть гранями соседа (на границе), поэтому берём соседей всех непустых.
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
        double area = 0;

        foreach (var coord in ChunksToBuild(grid))
        {
            var d = ChunkMesher.Build(grid, coord);
            faces += d.VisibleFaces;
            quads += d.Quads;
            segments += d.LineSegments;

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

        return new Stats(faces, quads, segments, bad, area);
    }

    /// <summary>Независимый «в лоб» подсчёт видимых граней прямо по сетке (без чанков и паддинга).</summary>
    private static int BruteForceFaces(VoxelGrid g, Vector3I min, Vector3I max)
    {
        int faces = 0;
        var dirs = new[] { Vector3I.Right, Vector3I.Left, Vector3I.Up, Vector3I.Down, Vector3I.Back, Vector3I.Forward };

        for (int z = min.Z - 1; z <= max.Z + 1; z++)
        for (int y = min.Y - 1; y <= max.Y + 1; y++)
        for (int x = min.X - 1; x <= max.X + 1; x++)
        {
            var cell = new Vector3I(x, y, z);
            if (!g.IsSolid(cell)) continue;
            foreach (var dir in dirs)
            {
                if (!g.IsSolid(cell + dir)) faces++;
            }
        }

        return faces;
    }

    private static VoxelGrid Box(Vector3I min, Vector3I max) => Box(min, max, Block);

    private static VoxelGrid Box(Vector3I min, Vector3I max, ushort id)
    {
        var grid = new VoxelGrid();
        DemoBuilds.Fill(grid, min, max, id);
        return grid;
    }

    /// <summary>
    /// Каркас — полное отображение полигонов: каждый склеенный прямоугольник рисует свой контур + диагональ
    /// (5 отрезков), поэтому Segments всегда равен Quads * 5 — это и проверяется здесь как инвариант вместо
    /// заранее вычисленного числа отрезков.
    /// </summary>
    private void CheckShape(string name, VoxelGrid grid, int faces, int quads)
    {
        var s = Measure(grid);
        int expectedSegments = quads * 5;
        Check(s.Faces == faces && s.Quads == quads && s.Segments == expectedSegments
              && s.BadWinding == 0 && Math.Abs(s.QuadArea - s.Faces) < 1e-3,
            name,
            $"got faces={s.Faces} quads={s.Quads} segments={s.Segments} badWinding={s.BadWinding} area={s.QuadArea:0.###}; " +
            $"expected faces={faces} quads={quads} segments={expectedSegments}");
    }

    private void RunMesherTests()
    {
        GD.Print("-- mesher: face culling, polygon merging, full-polygon wireframe");

        CheckShape("single block: 6 faces -> 6 quads", Box(new(0, 0, 0), new(0, 0, 0)), 6, 6);
        CheckShape("2 blocks glued: hidden inner faces removed, 10 faces -> 6 quads", Box(new(0, 0, 0), new(1, 0, 0)), 10, 6);
        CheckShape("3x3x3 cube: 54 faces -> 6 quads", Box(new(0, 0, 0), new(2, 2, 2)), 54, 6);
        CheckShape("block at negative coordinates (chunk -1)", Box(new(-1, -1, -1), new(-1, -1, -1)), 6, 6);

        var lShape = new VoxelGrid();
        foreach (var c in new[] { new Vector3I(0, 0, 0), new Vector3I(1, 0, 0), new Vector3I(0, 0, 1) })
        {
            lShape.TrySet(c, Block, CellColor.Pack(Colors.Gray));
        }

        CheckShape("L-shape: non-rectangular footprint still merges greedily", lShape, 14, 10);

        var painted = Box(new(0, 0, 0), new(2, 2, 2));
        painted.TryPaint(new Vector3I(1, 2, 1), CellColor.Pack(Colors.Red));
        CheckShape("painted top center: recoloring one cell splits its quad by color (10 quads)", painted, 54, 10);

        var mixed = new VoxelGrid();
        uint gray = CellColor.Pack(Colors.Gray);
        mixed.TrySet(new Vector3I(0, 0, 0), Block, gray);
        mixed.TrySet(new Vector3I(1, 0, 0), OtherType, gray);
        CheckShape("different block types with the same color merge into common polygons", mixed, 10, 6);

        CheckShape("chunk boundary (x=15|16): each chunk meshes its own half independently", Box(new(15, 0, 0), new(16, 0, 0)), 10, 10);

        // Случайная постройка через границы чанков и нулевые/отрицательные координаты: сверка с независимым подсчётом.
        var rng = new Random(12345);
        var blob = new VoxelGrid();
        for (int z = -10; z <= 12; z++)
        for (int y = -3; y <= 12; y++)
        for (int x = -10; x <= 12; x++)
        {
            if (rng.NextDouble() < 0.4) blob.TrySet(new Vector3I(x, y, z), Block, CellColor.Pack(rng.Next(3) == 0 ? Colors.Red : Colors.Gray));
        }

        int expectedFaces = BruteForceFaces(blob, new Vector3I(-10, -3, -10), new Vector3I(12, 12, 12));
        var actual = Measure(blob);
        Check(actual.Faces == expectedFaces, $"random blob ({blob.BlockCount} blocks): visible faces match brute force",
            $"got {actual.Faces}, expected {expectedFaces}");
        Check(actual.Segments == actual.Quads * 5,
            "random blob: every merged quad draws exactly its boundary + diagonal (full-polygon wireframe)",
            $"segments={actual.Segments} quads={actual.Quads}");
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
            if (rng.NextDouble() < 0.3) grid.TrySet(new Vector3I(x, y, z), Block, CellColor.Pack(Colors.Gray));
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

            bool changed = grid.IsSolid(cell) ? grid.TryRemove(cell) : grid.TrySet(cell, Block, CellColor.Pack(Colors.Gray));
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

    // ================================================================== каталог блоков (data-driven, blocks/*.xml)

    private void RunBlockCatalogTests()
    {
        GD.Print("-- block catalog: XML block definitions loaded from blocks/");

        var catalog = BlockCatalog.Instance;
        Check(catalog.All.Count == 4, "catalog loaded 4 block definitions from blocks/ (block, wedge, pyramid, inverse_pyramid)",
            $"got {catalog.All.Count}");

        Check(catalog.TryGetBySlug("block", out var block) && block.Name == "Block" && block.RuntimeId != 0,
            "block 'block' resolves by slug with a non-zero runtime id");
        Check(!catalog.TryGetBySlug("__missing__", out _), "unknown slug is reported, not thrown");
        Check(catalog.Get(block.RuntimeId) == block, "block also resolves by its runtime id (used by VoxelGrid)");

        var baseComponent = block.GetComponent<BaseComponent>();
        Check(baseComponent != null && baseComponent.Mass > 0 && baseComponent.Durability > 0,
            "BaseComponent (mass/durability/damageResistance) is parsed from the <Component> JSON body");

        var building = block.GetComponent<BuildingBlockComponent>();
        Check(building != null && building.Shape == BlockShape.Cube && building.MinSize == Vector3I.One && building.MaxSize == new Vector3I(8, 8, 8),
            "BuildingBlock component (shape/minSize/maxSize) is parsed from the <Component> JSON body");

        Check(catalog.TryGetBySlug("wedge", out var wedge) && wedge.GetComponent<BuildingBlockComponent>()!.Shape == BlockShape.Slope,
            "block 'wedge' has BuildingBlock.Shape = Slope");
        Check(catalog.TryGetBySlug("pyramid", out var pyramid) && pyramid.GetComponent<BuildingBlockComponent>()!.Shape == BlockShape.Pyramid,
            "block 'pyramid' has BuildingBlock.Shape = Pyramid");
        Check(catalog.TryGetBySlug("inverse_pyramid", out var inv) && inv.GetComponent<BuildingBlockComponent>()!.Shape == BlockShape.InvertedPyramid,
            "block 'inverse_pyramid' has BuildingBlock.Shape = InvertedPyramid");
    }

    // ================================================================== геометрия форм (вершины Wedge/Pyramid/InvertedPyramid)

    private void RunShapeGeometryTests()
    {
        GD.Print("-- shape geometry: vertex-based meshes for Wedge/Pyramid/InvertedPyramid");

        foreach (var shape in new[] { BlockShape.Slope, BlockShape.Pyramid, BlockShape.InvertedPyramid })
        {
            var data = ShapeMeshBuilder.BuildData(shape, Vector3I.One, Vector3I.Zero, Colors.White);
            Check(data != null && data.Indices.Count > 0, $"{shape}: mesh has triangles");
            if (data == null) continue;

            int bad = 0;
            for (int t = 0; t < data.Indices.Count; t += 3)
            {
                var a = data.Vertices[data.Indices[t]];
                var b = data.Vertices[data.Indices[t + 1]];
                var c = data.Vertices[data.Indices[t + 2]];
                // Та же конвенция, что и в ChunkMesher/Measure(): (b-a)x(c-a) должен указывать ПРОТИВ нормали.
                if ((b - a).Cross(c - a).Dot(data.Normals[data.Indices[t]]) >= 0) bad++;
            }

            Check(bad == 0, $"{shape}: all triangles have correct front-face winding", $"{bad} bad of {data.Indices.Count / 3}");

            bool inBounds = true;
            foreach (var v in data.Vertices)
            {
                if (v.X < -1e-4f || v.X > BuildSpace.CellSize + 1e-4f
                    || v.Y < -1e-4f || v.Y > BuildSpace.CellSize + 1e-4f
                    || v.Z < -1e-4f || v.Z > BuildSpace.CellSize + 1e-4f)
                {
                    inBounds = false;
                }
            }

            Check(inBounds, $"{shape}: all vertices stay within the block's 1x1x1 bounding box");
        }

        // Пирамида — тетраэдр (4 треугольные грани); Скос — 3 прямоугольника + 2 треугольных борта (8 треугольников);
        // Инвертированная пирамида — 3 прямоугольника + 4 треугольника (10 треугольников).
        Check(ShapeMeshBuilder.BuildData(BlockShape.Pyramid, Vector3I.One, Vector3I.Zero, Colors.White)!.Indices.Count / 3 == 4,
            "Pyramid: 4 triangles (tetrahedron, matches the 4 given vertices)");
        Check(ShapeMeshBuilder.BuildData(BlockShape.Slope, Vector3I.One, Vector3I.Zero, Colors.White)!.Indices.Count / 3 == 8,
            "Wedge: 8 triangles (3 rectangular faces + 2 triangular sides)");
        Check(ShapeMeshBuilder.BuildData(BlockShape.InvertedPyramid, Vector3I.One, Vector3I.Zero, Colors.White)!.Indices.Count / 3 == 10,
            "InvertedPyramid: 10 triangles (3 rectangular faces + 4 triangular faces)");
        Check(ShapeMeshBuilder.BuildData(BlockShape.Cube, Vector3I.One, Vector3I.Zero, Colors.White) == null,
            "Cube: ShapeMeshBuilder returns null (cubes are meshed by ChunkMesher instead)");

        // Resize «двигает вершины»: bounding box формы масштабируется вместе с размером экземпляра.
        var scaled = ShapeMeshBuilder.BuildData(BlockShape.Pyramid, new Vector3I(2, 3, 4), Vector3I.Zero, Colors.White)!;
        double maxX = 0, maxY = 0, maxZ = 0;
        foreach (var v in scaled.Vertices)
        {
            if (v.X > maxX) maxX = v.X;
            if (v.Y > maxY) maxY = v.Y;
            if (v.Z > maxZ) maxZ = v.Z;
        }
        bool boundsScaled = Mathf.Abs(maxX - 2 * BuildSpace.CellSize) < 1e-4f
                             && Mathf.Abs(maxY - 3 * BuildSpace.CellSize) < 1e-4f
                             && Mathf.Abs(maxZ - 4 * BuildSpace.CellSize) < 1e-4f;
        Check(boundsScaled, "resize moves the shape's vertices: bounding box scales with instance size", $"max=({maxX},{maxY},{maxZ})");

        // Вращение: 4 четверти вокруг одной оси = полный оборот = исходная форма; 90° меняет нормали граней.
        var baseData = ShapeMeshBuilder.BuildData(BlockShape.Pyramid, Vector3I.One, Vector3I.Zero, Colors.White)!;
        var fullTurn = ShapeMeshBuilder.BuildData(BlockShape.Pyramid, Vector3I.One, new Vector3I(4, 0, 0), Colors.White)!;
        bool sameAfterFullTurn = true;
        for (int i = 0; i < baseData.Vertices.Count; i++)
        {
            if (baseData.Vertices[i].DistanceTo(fullTurn.Vertices[i]) > 1e-3f) sameAfterFullTurn = false;
        }

        Check(sameAfterFullTurn, "rotating 4 quarter-turns around one axis returns to the original orientation");

        var rotated90 = ShapeMeshBuilder.BuildData(BlockShape.Pyramid, Vector3I.One, new Vector3I(0, 1, 0), Colors.White)!;
        bool normalsChanged = false;
        for (int i = 0; i < baseData.Normals.Count; i++)
        {
            if (baseData.Normals[i].DistanceTo(rotated90.Normals[i]) > 1e-3f) normalsChanged = true;
        }

        Check(normalsChanged, "rotating 90 degrees around Y visibly changes the shape's face normals");
    }

    // ================================================================== не-кубические блоки в ChunkMesher

    private void RunNonCubeMeshingTests()
    {
        GD.Print("-- non-cube blocks: ChunkMesher skips their own faces but still culls neighbors");

        var wedge = BlockCatalog.Instance.Get("wedge");
        var solo = new VoxelGrid();
        solo.TrySet(new Vector3I(0, 0, 0), wedge.RuntimeId, CellColor.Pack(Colors.Gray));
        var soloStats = Measure(solo);
        Check(soloStats.Faces == 0 && soloStats.Quads == 0,
            "a lone non-cube cell contributes no faces/quads to ChunkMesher (its own mesh is drawn separately)",
            $"faces={soloStats.Faces} quads={soloStats.Quads}");

        var block = BlockCatalog.Instance.Get("block");
        var mixed = new VoxelGrid();
        mixed.TrySet(new Vector3I(0, 0, 0), block.RuntimeId, CellColor.Pack(Colors.Gray));
        mixed.TrySet(new Vector3I(1, 0, 0), wedge.RuntimeId, CellColor.Pack(Colors.Gray));
        var mixedStats = Measure(mixed);
        Check(mixedStats.Faces == 5 && mixedStats.Quads == 5,
            "a cube next to a non-cube block still culls the touching face (non-cube cells still count as solid)",
            $"faces={mixedStats.Faces} quads={mixedStats.Quads}");
    }

    // ================================================================== вращение перед установкой (J/K/I)

    private void RunRotationStateTests()
    {
        GD.Print("-- pending rotation: J/K/I increment X/Y/Z mod 4");

        var state = new EditorState();
        Check(state.PendingRotationSteps == Vector3I.Zero, "EditorState starts with no rotation");
        state.RotatePendingX();
        Check(state.PendingRotationSteps == new Vector3I(1, 0, 0), "RotatePendingX increments X");
        for (int i = 0; i < 3; i++) state.RotatePendingX();
        Check(state.PendingRotationSteps == Vector3I.Zero, "4 rotations around X wrap back to 0 (full turn)");
        state.RotatePendingY();
        state.RotatePendingZ();
        Check(state.PendingRotationSteps == new Vector3I(0, 1, 1), "RotatePendingY/Z increment Y/Z independently");
    }

    // ================================================================== постройка: экземпляры блоков, Resize, JSON

    private void RunConstructionTests()
    {
        GD.Print("-- construction: placed block instances, resize (origin-fixed), save/load");

        var catalog = BlockCatalog.Instance;
        var block = catalog.Get("block");
        var wedge = catalog.Get("wedge");
        var pyramid = catalog.Get("pyramid");

        var grid = new VoxelGrid();
        var construction = new Construction(grid);

        // Place: создаёт экземпляр 1x1x1 и владение клеткой.
        var a = construction.Place(new Vector3I(0, 0, 0), block, block.DefaultColor);
        Check(a != null && grid.GetId(new Vector3I(0, 0, 0)) == block.RuntimeId,
            "Place: creates a 1x1x1 instance and fills the grid cell");
        Check(construction.GetOwner(new Vector3I(0, 0, 0)) == a, "Place: the cell is owned by the new instance");
        Check(construction.Place(new Vector3I(0, 0, 0), wedge, wedge.DefaultColor) == null,
            "Place: fails on an already occupied cell");

        // TrySetSize: растёт от Origin только в положительную сторону; Origin никогда не двигается.
        bool grew = construction.TrySetSize(a!, block, new Vector3I(3, 1, 1));
        Check(grew && a!.Origin == Vector3I.Zero && a!.Size == new Vector3I(3, 1, 1)
              && grid.GetId(new Vector3I(1, 0, 0)) == block.RuntimeId && grid.GetId(new Vector3I(2, 0, 0)) == block.RuntimeId,
            "TrySetSize: grows from origin towards +X, origin stays put", $"origin={a!.Origin} size={a!.Size}");
        Check(construction.GetOwner(new Vector3I(2, 0, 0)) == a, "TrySetSize: new cells are owned by the same instance");

        var blocker = construction.Place(new Vector3I(5, 0, 0), wedge, wedge.DefaultColor);
        Check(!construction.TrySetSize(a!, block, new Vector3I(6, 1, 1)) && a!.Size == new Vector3I(3, 1, 1),
            "TrySetSize: fails atomically when a cell in the new footprint is occupied (size unchanged)");
        construction.Remove(blocker!);

        bool shrunk = construction.TrySetSize(a!, block, Vector3I.One);
        Check(shrunk && a!.Origin == Vector3I.Zero && a!.Size == Vector3I.One
              && grid.GetId(new Vector3I(1, 0, 0)) == 0 && grid.GetId(new Vector3I(2, 0, 0)) == 0 && grid.GetId(new Vector3I(0, 0, 0)) == block.RuntimeId,
            "TrySetSize: shrinking clears the freed cells and keeps origin", $"origin={a!.Origin} size={a!.Size}");

        Check(!construction.TrySetSize(a!, block, new Vector3I(0, 1, 1)) && a!.Size == Vector3I.One,
            "TrySetSize: a requested size below 1 is clamped to 1, so shrinking below that is a no-op");

        // MaxSize: рост останавливается на границе, заданной BuildingBlock-компонентом (block: maxSize=[8,8,8]).
        var c = construction.Place(new Vector3I(20, 0, 0), block, block.DefaultColor);
        Check(construction.TrySetSize(c!, block, new Vector3I(20, 1, 1)) && c!.Size == new Vector3I(8, 1, 1),
            "TrySetSize: clamps to MaxSize (8) even when a larger value is requested", $"size={c!.Size}");

        // Блок без BuildingBlock-компонента не резинится.
        var noResize = new BlockDefinition(new Dictionary<Type, BlockComponent>())
            { Slug = "no_resize", Name = "NoResize", DefaultColor = Colors.White };
        var d = construction.Place(new Vector3I(30, 0, 0), block, block.DefaultColor);
        Check(!construction.TrySetSize(d!, noResize, new Vector3I(2, 1, 1)),
            "TrySetSize: fails for a block definition without a BuildingBlock component");

        // Paint/Remove действуют на все клетки многоклеточного экземпляра разом.
        var e = construction.Place(new Vector3I(40, 0, 0), block, block.DefaultColor);
        construction.TrySetSize(e!, block, new Vector3I(2, 1, 1));
        construction.Paint(e!, Colors.Red);
        Check(grid.GetColor(new Vector3I(40, 0, 0)) == CellColor.Pack(Colors.Red) && grid.GetColor(new Vector3I(41, 0, 0)) == CellColor.Pack(Colors.Red),
            "Paint: recolors every cell of a multi-cell instance");
        bool removed = construction.Remove(e!);
        Check(removed && grid.GetId(new Vector3I(40, 0, 0)) == 0 && grid.GetId(new Vector3I(41, 0, 0)) == 0 && construction.GetOwner(new Vector3I(40, 0, 0)) == null,
            "Remove: clears every cell of the instance and its ownership");

        // PlaceBlock: ставит блок сразу заданного размера (используется загрузкой построек).
        var f = construction.PlaceBlock(new Vector3I(50, 0, 0), new Vector3I(2, 3, 1), pyramid, pyramid.DefaultColor);
        Check(f != null && grid.GetId(new Vector3I(51, 2, 0)) == pyramid.RuntimeId, "PlaceBlock: places a block of arbitrary size in one call");
        Check(construction.PlaceBlock(new Vector3I(50, 0, 0), Vector3I.One, block, block.DefaultColor) == null,
            "PlaceBlock: fails if any covered cell is already occupied");

        // Вращение: Place/PlaceBlock принимают и хранят RotationSteps (по умолчанию — [0,0,0]).
        var g = construction.Place(new Vector3I(60, 0, 0), wedge, wedge.DefaultColor);
        Check(g != null && g!.RotationSteps == Vector3I.Zero, "Place: defaults to no rotation when omitted");
        var h = construction.Place(new Vector3I(65, 0, 0), wedge, wedge.DefaultColor, new Vector3I(0, 1, 2));
        Check(h != null && h!.RotationSteps == new Vector3I(0, 1, 2), "Place: stores the given rotation steps on the new instance");

        RunSaveLoadTests(catalog, block, wedge, pyramid);
    }

    private void RunSaveLoadTests(BlockCatalog catalog, BlockDefinition block, BlockDefinition wedge, BlockDefinition pyramid)
    {
        var saved = new Construction(new VoxelGrid());
        saved.Place(new Vector3I(0, 0, 0), block, Colors.Red);
        saved.PlaceBlock(new Vector3I(5, 0, 0), new Vector3I(2, 1, 3), wedge, Colors.Green, new Vector3I(1, 2, 3));
        string json = ConstructionIO.Serialize(saved);

        var loaded = new Construction(new VoxelGrid());
        var (loadedCount, skippedCount) = ConstructionIO.Deserialize(loaded, json, catalog);
        Check(loadedCount == 2 && skippedCount == 0, $"Save/Load: round-trips {loadedCount} block(s) through JSON with 0 skipped");

        bool blockOk = loaded.Instances.Any(i =>
            i.BlockSlug == "block" && i.Origin == new Vector3I(0, 0, 0) && i.Size == Vector3I.One && i.Color == CellColor.Pack(Colors.Red));
        bool wedgeOk = loaded.Instances.Any(i =>
            i.BlockSlug == "wedge" && i.Origin == new Vector3I(5, 0, 0) && i.Size == new Vector3I(2, 1, 3)
            && i.Color == CellColor.Pack(Colors.Green) && i.RotationSteps == new Vector3I(1, 2, 3));
        Check(blockOk && wedgeOk, "Save/Load: slug, origin, size, color and rotation survive the round-trip");

        const string badJson = "{\"version\":1,\"blocks\":[{\"id\":\"__unknown__\",\"origin\":[0,0,0],\"size\":[1,1,1],\"color\":\"#ffffff\"}]}";
        var skipTarget = new Construction(new VoxelGrid());
        var (loadedBad, skippedBad) = ConstructionIO.Deserialize(skipTarget, badJson, catalog);
        Check(loadedBad == 0 && skippedBad == 1, "Load: an unknown block slug is skipped, not thrown");

        // Реальный файл (user://) — тот же путь кода, что и кнопки Save/Load в EditorUi.
        string path = ProjectSettings.GlobalizePath("user://selftest_construction.json");
        var fileSource = new Construction(new VoxelGrid());
        fileSource.Place(new Vector3I(1, 1, 1), pyramid, pyramid.DefaultColor);
        var saveError = ConstructionIO.SaveToFile(fileSource, path);

        var fileTarget = new Construction(new VoxelGrid());
        var (loadError, loadedFile, skippedFile) = ConstructionIO.LoadFromFile(fileTarget, path, catalog);
        Check(saveError == Error.Ok && loadError == Error.Ok && loadedFile == 1 && skippedFile == 0
              && fileTarget.Instances.First().BlockSlug == "pyramid",
            "Save/Load: round-trips through an actual file (user://)", $"saveError={saveError} loadError={loadError}");

        DirAccess.RemoveAbsolute(path);
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
        await PressKeyDown(editor, key);
        await PressKeyUp(editor, key);
    }

    /// <summary>Нажимает клавишу без отпускания (для модификаторов, удерживаемых во время клика мышью).</summary>
    private static async Task PressKeyDown(BuildEditor editor, Godot.Key key)
    {
        Send(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
        await Frames(editor, 1);
    }

    private static async Task PressKeyUp(BuildEditor editor, Godot.Key key)
    {
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

        int blockCount = BlockCatalog.Instance.All.Count;
        Check(blockCount == 4, "catalog has 4 shape blocks (block, wedge, pyramid, inverse_pyramid)", $"got {blockCount}");
        int filledSlots = 0;
        for (int i = 0; i < EditorState.HotbarSize; i++)
        {
            if (!string.IsNullOrEmpty(state.GetSlot(i))) filledSlots++;
        }

        Check(filledSlots == blockCount, $"hotbar is pre-filled with all {blockCount} blocks (rest of the 9 slots stay empty)", $"filled={filledSlots}");

        camera.LookAtPoint(new Vector3(1.5f, 2f, 2.5f), new Vector3(0.125f, 0.0f, 0.125f));
        await Frames(editor, 2);

        Vector2 Screen(Vector3 world) => camera.UnprojectPosition(world);
        Vector3 TopOf(Vector3I cell) => BuildSpace.CellCenter(cell) + new Vector3(0, BuildSpace.CellSize / 2, 0);

        // 4. ЛКМ ставит блок: сначала на землю, затем на верхнюю грань блока.
        state.SelectedSlot = 0;
        var ground = Screen(new Vector3(0.125f, 0f, 0.125f));
        await Move(editor, ground);
        Check(editor.Hover.Found && !editor.Hover.IsBlock && editor.Hover.PlaceCell == new Vector3I(0, 0, 0), "cursor over ground targets cell (0,0,0)", $"{editor.Hover} ground={ground} overUi={editor.Ui.IsPointOverUi(ground)} cam={camera.GlobalPosition}");

        await Click(editor, ground, MouseButton.Left);
        Check(grid.GetId(new Vector3I(0, 0, 0)) == BlockCatalog.Instance.Get(state.SelectedBlockSlug).RuntimeId, "LMB places the selected hotbar block on the ground");

        var top = Screen(TopOf(new Vector3I(0, 0, 0)));
        await Click(editor, top, MouseButton.Left);
        Check(grid.GetId(new Vector3I(0, 1, 0)) != 0, "LMB on a block face places a block adjacent to it");

        state.SelectedSlot = 1;
        await Click(editor, Screen(TopOf(new Vector3I(0, 1, 0))), MouseButton.Left);
        Check(grid.GetId(new Vector3I(0, 2, 0)) == BlockCatalog.Instance.Get(state.GetSlot(1)).RuntimeId, "block type follows the selected hotbar slot");
        await Click(editor, Screen(TopOf(new Vector3I(0, 2, 0))), MouseButton.Right);
        Check(grid.BlockCount == 3, "RMB with no tool selected does nothing");

        // 3. Инструменты тулбара на ПКМ.
        state.PaintColor = Colors.Red;
        state.Tool = ToolMode.Paint;
        var target = new Vector3I(0, 2, 0);
        await Click(editor, Screen(TopOf(target)), MouseButton.Right);
        Check(grid.GetColor(target) == CellColor.Pack(Colors.Red) && grid.GetId(target) != 0, "paint tool recolors the block under the cursor");

        state.Tool = ToolMode.Delete;
        await Click(editor, Screen(TopOf(target)), MouseButton.Right);
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
        await Click(editor, ground, MouseButton.Left);
        Check(grid.BlockCount == blocksBefore, "world clicks are blocked while the block list is open");
        await PressKey(editor, Godot.Key.Tab);
        Check(!editor.Ui.PickerOpen, "Tab closes the block list");

        // Интерфейс не пропускает клики в мир.
        var view = editor.GetViewport().GetVisibleRect().Size;
        var overHotbar = new Vector2(view.X / 2, view.Y - 50);
        var overToolbar = new Vector2(view.X - 90, view.Y / 2);
        Check(editor.Ui.IsPointOverUi(overHotbar) && editor.Ui.IsPointOverUi(overToolbar), "hotbar and toolbar areas are recognized as UI");
        await Click(editor, overHotbar, MouseButton.Left);
        Check(grid.BlockCount == blocksBefore && !editor.Hover.Found, "clicking on the hotbar does not place blocks in the world");

        // Клики по элементам интерфейса (хотбар/тулбар/список блоков всегда реагируют на ЛКМ, это стандартная
        // UI-конвенция и не зависит от того, чем ставится блок/применяется инструмент в мире).
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
        Check(FindButton("+ Save color") != null, "toolbar: the paint panel (sliders/palette) is visible while Paint is active");
        await Click(editor, CenterOf(FindButton("Delete")!), MouseButton.Left);
        Check(state.Tool == ToolMode.Delete, "toolbar: tools are mutually exclusive");
        Check(FindButton("+ Save color") == null, "toolbar: the paint panel hides again once Paint is no longer active");
        await Click(editor, CenterOf(FindButton("Resize")!), MouseButton.Left);
        Check(state.Tool == ToolMode.Resize, "toolbar: Resize button activates the resize tool");
        state.Tool = ToolMode.None;
        await Click(editor, CenterOf(FindButton("Wireframe: off")!), MouseButton.Left);
        Check(state.Wire == WireMode.Overlay, "toolbar: wireframe button switches the display mode");
        state.Wire = WireMode.Off;

        await PressKey(editor, Godot.Key.Tab);
        await Click(editor, CenterOf(FindButton("Wedge")!), MouseButton.Left);
        Check(state.GetSlot(state.SelectedSlot) == "wedge", "block list: clicking a block puts it into the selected hotbar slot");
        await PressKey(editor, Godot.Key.Escape);
        Check(!editor.Ui.PickerOpen, "Esc closes the block list");

        // Paint-панель: ползунки RGB и сохранение цвета.
        await Click(editor, CenterOf(FindButton("Paint")!), MouseButton.Left);
        var sliders = new List<HSlider>();
        foreach (var node in editor.GetTree().Root.FindChildren("*", "HSlider", true, false))
        {
            if (node is HSlider slider && slider.IsVisibleInTree()) sliders.Add(slider);
        }

        Check(sliders.Count == 3, "paint panel: 3 RGB sliders are present", $"found {sliders.Count}");
        if (sliders.Count == 3)
        {
            sliders[0].Value = 10;
            sliders[1].Value = 200;
            sliders[2].Value = 30;
            await Frames(editor, 2);
            var expected = new Color(10 / 255f, 200 / 255f, 30 / 255f);
            bool close = Math.Abs(state.PaintColor.R - expected.R) < 0.01f
                         && Math.Abs(state.PaintColor.G - expected.G) < 0.01f
                         && Math.Abs(state.PaintColor.B - expected.B) < 0.01f;
            Check(close, "paint panel: dragging an RGB slider updates the paint color", $"{state.PaintColor} vs {expected}");
        }

        int savedBefore = FindButtons(editor).Count(b => b.Text == "");
        await Click(editor, CenterOf(FindButton("+ Save color")!), MouseButton.Left);
        int savedAfter = FindButtons(editor).Count(b => b.Text == "");
        Check(savedAfter == savedBefore + 1, "paint panel: 'Save color' adds a new swatch to Saved colors", $"{savedBefore} -> {savedAfter}");
        state.Tool = ToolMode.None;

        // 5. Resize через реальный пайплайн: ЛКМ ставит блок, ПКМ с инструментом Resize открывает диалог X/Y/Z,
        // кнопки "+"/"-" в диалоге меняют размер (растёт только от origin в положительную сторону).
        grid.Clear();
        camera.LookAtPoint(new Vector3(1.5f, 2f, 2.5f), new Vector3(0.125f, 0.0f, 0.125f));
        await Frames(editor, 2);
        state.SelectedSlot = 0;
        await Click(editor, Screen(new Vector3(0.125f, 0f, 0.125f)), MouseButton.Left);
        var resizeTarget = editor.World.Construction.GetOwner(new Vector3I(0, 0, 0));
        Check(resizeTarget != null && resizeTarget.Size == Vector3I.One, "resize setup: LMB placed a fresh 1x1x1 instance");

        state.Tool = ToolMode.Resize;
        await Click(editor, Screen(TopOf(new Vector3I(0, 0, 0))), MouseButton.Right);
        Check(editor.Ui.ResizeDialogOpen, "resize tool: RMB on a block opens the resize dialog");

        var plusButtons = FindButtons(editor).Where(b => b.Text == "+").ToList();
        Check(plusButtons.Count == 3, "resize dialog: 3 '+' buttons (X/Y/Z)", $"found {plusButtons.Count}");
        await Click(editor, plusButtons[0].GetGlobalRect().GetCenter(), MouseButton.Left);
        Check(resizeTarget!.Size == new Vector3I(2, 1, 1) && grid.IsSolid(new Vector3I(1, 0, 0)) && resizeTarget!.Origin == Vector3I.Zero,
            "resize dialog: '+' on X grows the block by 1 cell along X, origin unchanged", $"size={resizeTarget!.Size}");

        var minusButtons = FindButtons(editor).Where(b => b.Text == "-").ToList();
        await Click(editor, minusButtons[0].GetGlobalRect().GetCenter(), MouseButton.Left);
        Check(resizeTarget!.Size == Vector3I.One && !grid.IsSolid(new Vector3I(1, 0, 0)),
            "resize dialog: '-' on X shrinks the block back down", $"size={resizeTarget!.Size}");

        await PressKey(editor, Godot.Key.Escape);
        Check(!editor.Ui.ResizeDialogOpen, "Esc closes the resize dialog");
        state.Tool = ToolMode.None;
        grid.Clear();

        // Вращение перед установкой (J/K/I): меняет ориентацию следующего блока, вращение сохраняется в поставленном
        // экземпляре. 0=block, 1=inverse_pyramid, 2=pyramid, 3=wedge — алфавитный порядок каталога (см. блочные тесты).
        state.SelectedSlot = 3;
        Check(state.SelectedBlockSlug == "wedge", "rotation setup: hotbar slot 3 is 'wedge'", state.SelectedBlockSlug);
        await PressKey(editor, Godot.Key.J);
        await PressKey(editor, Godot.Key.K);
        await PressKey(editor, Godot.Key.K);
        Check(state.PendingRotationSteps == new Vector3I(1, 2, 0), "J/K real key presses rotate the pending placement orientation");

        await Click(editor, ground, MouseButton.Left);
        var rotatedInstance = editor.World.Construction.GetOwner(new Vector3I(0, 0, 0));
        Check(rotatedInstance != null && rotatedInstance.RotationSteps == new Vector3I(1, 2, 0),
            "the placed block bakes in the pending rotation steps");
        grid.Clear();

        // Перетаскивание инструмента с зажатым ПКМ: три блока в ряд, «проедания насквозь» без движения мыши нет.
        DemoBuilds.Fill(grid, new Vector3I(0, 0, 0), new Vector3I(2, 0, 0), Block);
        DemoBuilds.Fill(grid, new Vector3I(0, 0, 1), new Vector3I(2, 0, 1), Block);
        camera.LookAtPoint(new Vector3(0.4f, 2.5f, 2.0f), new Vector3(0.4f, 0.0f, 0.2f));
        await Frames(editor, 2);
        var p0 = Screen(TopOf(new Vector3I(0, 0, 0)));
        var p1 = Screen(TopOf(new Vector3I(1, 0, 0)));
        var p2 = Screen(TopOf(new Vector3I(2, 0, 0)));
        state.PaintColor = Colors.Blue;
        state.Tool = ToolMode.Paint;
        await Move(editor, p0);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = p0, GlobalPosition = p0 });
        await Frames(editor, 1);
        await Move(editor, p1);
        await Move(editor, p2);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = p2, GlobalPosition = p2 });
        await Frames(editor, 2);
        uint blue = CellColor.Pack(Colors.Blue);
        Check(grid.GetColor(new Vector3I(0, 0, 0)) == blue && grid.GetColor(new Vector3I(1, 0, 0)) == blue && grid.GetColor(new Vector3I(2, 0, 0)) == blue,
            "holding RMB and dragging paints every block passed over");
        Check(grid.GetColor(new Vector3I(1, 0, 1)) != blue, "drag painting does not touch blocks that were not under the cursor");

        int before = grid.BlockCount;
        state.Tool = ToolMode.Delete;
        await Move(editor, p1);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = p1, GlobalPosition = p1 });
        await Frames(editor, 30); // мышь неподвижна: должен удалиться ровно один блок
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = p1, GlobalPosition = p1 });
        await Frames(editor, 2);
        Check(grid.BlockCount == before - 1 && grid.GetId(new Vector3I(1, 0, 0)) == 0,
            "holding RMB without moving deletes exactly one block (no tunnelling through the build)", $"blocks {before} -> {grid.BlockCount}");
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
        // "Save color" выше дописал user://custom_palette.cfg реальным пользовательским файлом — не мусорим.
        string customPalettePath = ProjectSettings.GlobalizePath("user://custom_palette.cfg");
        if (FileAccess.FileExists(customPalettePath)) DirAccess.RemoveAbsolute(customPalettePath);
        grid.Clear();
        editor.World.RebuildDirty();
    }
}
