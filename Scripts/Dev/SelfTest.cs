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
/// колесо, WASD, СКМ, блокировка UI, панель Resize на тулбаре.
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
        test.RunBuildSpaceTests();
        test.RunBlockCatalogTests();
        test.RunShapeGeometryTests();
        test.RunNonCubeMeshingTests();
        test.RunResizeMeshingTests();
        test.RunRotationStateTests();
        test.RunPaintRegionTests();
        test.RunConstructionTests();
        test.RunUndoHistoryTests();
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

    // ================================================================== область построек (BuildSpace)

    /// <summary>Границы области построек симметричны относительно центра (0,0,0): 25 м влево/вправо (X) и вверх/вниз
    /// (Y), 50 м вперёд/назад (Z) - см. class doc <see cref="BuildSpace"/>.</summary>
    private void RunBuildSpaceTests()
    {
        GD.Print("-- build space: symmetric bounds around the center (25 m sides/height, 50 m forward/back)");

        Check(Mathf.Abs(-BuildSpace.MinCell.X * BuildSpace.CellSize - 25f) < 1e-4f
              && Mathf.Abs((BuildSpace.MaxCell.X + 1) * BuildSpace.CellSize - 25f) < 1e-4f,
            "X (left/right) spans exactly 25 m on each side of the center", $"{BuildSpace.MinCell.X}..{BuildSpace.MaxCell.X}");
        Check(Mathf.Abs(-BuildSpace.MinCell.Y * BuildSpace.CellSize - 25f) < 1e-4f
              && Mathf.Abs((BuildSpace.MaxCell.Y + 1) * BuildSpace.CellSize - 25f) < 1e-4f,
            "Y (up/down) spans exactly 25 m on each side of the center", $"{BuildSpace.MinCell.Y}..{BuildSpace.MaxCell.Y}");
        Check(Mathf.Abs(-BuildSpace.MinCell.Z * BuildSpace.CellSize - 50f) < 1e-4f
              && Mathf.Abs((BuildSpace.MaxCell.Z + 1) * BuildSpace.CellSize - 50f) < 1e-4f,
            "Z (forward/back) spans exactly 50 m on each side of the center", $"{BuildSpace.MinCell.Z}..{BuildSpace.MaxCell.Z}");

        Check(BuildSpace.InBounds(BuildSpace.MinCell) && BuildSpace.InBounds(BuildSpace.MaxCell),
            "the corner cells themselves are in bounds (inclusive range)");
        Check(!BuildSpace.InBounds(BuildSpace.MinCell - Vector3I.One) && !BuildSpace.InBounds(BuildSpace.MaxCell + Vector3I.One),
            "one cell past either corner is out of bounds");

        Check(BuildSpace.WorldMin.DistanceTo(new Vector3(-25f, -25f, -50f)) < 1e-3f,
            "WorldMin is the outer corner of the area in meters", $"{BuildSpace.WorldMin}");
        Check(BuildSpace.WorldMax.DistanceTo(new Vector3(25f, 25f, 50f)) < 1e-3f,
            "WorldMax is the outer corner of the area in meters", $"{BuildSpace.WorldMax}");
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
            var data = ShapeMeshBuilder.BuildData(shape, Vector3I.One, Vector3I.Zero, Vector3I.Zero, Colors.White);
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

        // Пирамида — тетраэдр (4 треугольные грани, ни одна не FullCoverage - см. BlockGeometry). У Скоса и
        // Инвертированной пирамиды FullCoverage-грани (низ+задняя стенка у Скоса; x=0/y=0/z=0 у InvertedPyramid)
        // теперь рисует ChunkMesher вместе с кубами (см. ShapeMeshBuilder.FullCoverageMask) - ShapeMeshBuilder их
        // больше не строит, поэтому у обеих форм остались только их наклонные/треугольные (не FullCoverage) грани.
        Check(ShapeMeshBuilder.BuildData(BlockShape.Pyramid, Vector3I.One, Vector3I.Zero, Vector3I.Zero, Colors.White)!.Indices.Count / 3 == 4,
            "Pyramid: 4 triangles (tetrahedron, matches the 4 given vertices)");
        Check(ShapeMeshBuilder.BuildData(BlockShape.Slope, Vector3I.One, Vector3I.Zero, Vector3I.Zero, Colors.White)!.Indices.Count / 3 == 4,
            "Wedge: 4 triangles left (ramp + 2 triangular sides) - bottom/back are FullCoverage, drawn by ChunkMesher instead");
        Check(ShapeMeshBuilder.BuildData(BlockShape.InvertedPyramid, Vector3I.One, Vector3I.Zero, Vector3I.Zero, Colors.White)!.Indices.Count / 3 == 4,
            "InvertedPyramid: 4 triangles left (the 3 truncated corners + the slice) - x=0/y=0/z=0 are FullCoverage, drawn by ChunkMesher instead");
        Check(ShapeMeshBuilder.BuildData(BlockShape.Cube, Vector3I.One, Vector3I.Zero, Vector3I.Zero, Colors.White) == null,
            "Cube: ShapeMeshBuilder returns null (cubes are meshed by ChunkMesher instead)");

        // Resize «двигает вершины»: bounding box формы масштабируется вместе с размером экземпляра.
        var scaled = ShapeMeshBuilder.BuildData(BlockShape.Pyramid, new Vector3I(2, 3, 4), Vector3I.Zero, Vector3I.Zero, Colors.White)!;
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
        var baseData = ShapeMeshBuilder.BuildData(BlockShape.Pyramid, Vector3I.One, Vector3I.Zero, Vector3I.Zero, Colors.White)!;
        var fullTurn = ShapeMeshBuilder.BuildData(BlockShape.Pyramid, Vector3I.One, new Vector3I(4, 0, 0), Vector3I.Zero, Colors.White)!;
        bool sameAfterFullTurn = true;
        for (int i = 0; i < baseData.Vertices.Count; i++)
        {
            if (baseData.Vertices[i].DistanceTo(fullTurn.Vertices[i]) > 1e-3f) sameAfterFullTurn = false;
        }

        Check(sameAfterFullTurn, "rotating 4 quarter-turns around one axis returns to the original orientation");

        var rotated90 = ShapeMeshBuilder.BuildData(BlockShape.Pyramid, Vector3I.One, new Vector3I(0, 1, 0), Vector3I.Zero, Colors.White)!;
        bool normalsChanged = false;
        for (int i = 0; i < baseData.Normals.Count; i++)
        {
            if (baseData.Normals[i].DistanceTo(rotated90.Normals[i]) > 1e-3f) normalsChanged = true;
        }

        Check(normalsChanged, "rotating 90 degrees around Y visibly changes the shape's face normals");

        // Отражение (Mirror): координата унитарного пространства заменяется на 1-c для отмеченных осей ДО поворота
        // (см. ShapeMeshBuilder.BuildData) - меняет форму (не просто сдвигает её), и, как и поворот, не ломает
        // обход треугольников (EmitFace сам чинит винд под пересчитанную нормаль).
        var mirroredX = ShapeMeshBuilder.BuildData(BlockShape.Pyramid, Vector3I.One, Vector3I.Zero, new Vector3I(1, 0, 0), Colors.White)!;
        bool verticesChanged = false;
        for (int i = 0; i < baseData.Vertices.Count; i++)
        {
            if (baseData.Vertices[i].DistanceTo(mirroredX.Vertices[i]) > 1e-3f) verticesChanged = true;
        }

        Check(verticesChanged, "mirroring around X visibly changes the shape's vertex positions");

        int mirrorBad = 0;
        for (int t = 0; t < mirroredX.Indices.Count; t += 3)
        {
            var a = mirroredX.Vertices[mirroredX.Indices[t]];
            var b = mirroredX.Vertices[mirroredX.Indices[t + 1]];
            var c = mirroredX.Vertices[mirroredX.Indices[t + 2]];
            if ((b - a).Cross(c - a).Dot(mirroredX.Normals[mirroredX.Indices[t]]) >= 0) mirrorBad++;
        }

        Check(mirrorBad == 0, "mirrored triangles still have correct front-face winding", $"{mirrorBad} bad of {mirroredX.Indices.Count / 3}");

        // Регрессия: поворот несимметрично растянутого (Resize) блока должен укладываться РОВНО в занятые клетки
        // (Construction.Size, зафиксированный в осях СЕТКИ), а не поворачиваться вместе с формой. Раньше вращение
        // применялось К УЖЕ растянутой фигуре, поэтому при повороте на 90°/270° вокруг оси, меняющей местами две
        // разные по размеру грани, bounding box съезжал с границ клеток ("плавал по середине сетки" в редакторе).
        var asymmetricSize = new Vector3I(3, 1, 1);
        var expectedExtent = new Vector3(asymmetricSize.X, asymmetricSize.Y, asymmetricSize.Z) * BuildSpace.CellSize;
        foreach (var rot in new[] { Vector3I.Zero, new Vector3I(0, 1, 0), new Vector3I(0, 2, 0), new Vector3I(0, 3, 0), new Vector3I(1, 0, 0) })
        {
            var rotatedResized = ShapeMeshBuilder.BuildData(BlockShape.Slope, asymmetricSize, rot, Vector3I.Zero, Colors.White)!;
            Vector3 min = rotatedResized.Vertices[0], max = rotatedResized.Vertices[0];
            foreach (var v in rotatedResized.Vertices)
            {
                min = new Vector3(Mathf.Min(min.X, v.X), Mathf.Min(min.Y, v.Y), Mathf.Min(min.Z, v.Z));
                max = new Vector3(Mathf.Max(max.X, v.X), Mathf.Max(max.Y, v.Y), Mathf.Max(max.Z, v.Z));
            }

            bool fitsFootprint = min.DistanceTo(Vector3.Zero) < 1e-4f && max.DistanceTo(expectedExtent) < 1e-4f;
            Check(fitsFootprint, $"rotated ({rot.X},{rot.Y},{rot.Z}) 3x1x1 wedge still bounds exactly to its grid footprint (0..{expectedExtent})",
                $"min={min} max={max}");
        }
    }

    // ================================================================== не-кубические блоки в ChunkMesher

    private void RunNonCubeMeshingTests()
    {
        GD.Print("-- non-cube blocks: their full-coverage sides join the same ChunkMesher pass as cubes (merge/cull), partial sides never do");

        // Тесты ниже пишут прямо в VoxelGrid (в обход Construction), поэтому маску полного покрытия граней
        // (VoxelChunk.FaceMask) нужно посчитать и передать явно - обычно это делает Construction.PlaceBlock при
        // установке блока игроком (см. Construction.FullCoverageMask).
        var wedge = BlockCatalog.Instance.Get("wedge");
        var block = BlockCatalog.Instance.Get("block");
        byte wedgeMask = ShapeMeshBuilder.FullCoverageMask(BlockShape.Slope, Vector3I.Zero, Vector3I.Zero);
        Check(wedgeMask == 0b010100, "Wedge (no rotation) fully covers exactly Y- (bottom) and Z- (back)", $"got {Convert.ToString(wedgeMask, 2)}");

        // Одинокий скос: рисует ChunkMesher только те 2 грани из 6, что покрывает ЦЕЛИКОМ (низ+задняя стенка) -
        // остальное (рампа, 2 треугольных борта) по-прежнему рисует его собственный меш (ShapeMeshBuilder), не этот.
        var solo = new VoxelGrid();
        solo.TrySet(new Vector3I(0, 0, 0), wedge.RuntimeId, CellColor.Pack(Colors.Gray), wedgeMask);
        var soloStats = Measure(solo);
        Check(soloStats.Faces == 2 && soloStats.Quads == 2,
            "a lone non-cube cell contributes only its full-coverage sides to ChunkMesher (2 for Wedge: bottom + back)",
            $"faces={soloStats.Faces} quads={soloStats.Quads}");

        // Раньше грань куба, обращённая к любому занятому соседу (кубу ИЛИ форме), всегда скрывалась - но форма не
        // обязательно покрывает всю грань клетки целиком (Wedge не покрывает свои X-стороны - треугольные борта),
        // поэтому такое скрытие оставляло настоящую дыру в стыке. Теперь грань куба скрывается, только если сосед
        // тоже покрывает СВОЮ обращённую сюда сторону целиком (маска) - Wedge справа не покрывает X-, так что куб
        // не культится там. Зато обе фигуры одного цвета ДЕЛЯТ низ (Y-) и заднюю стенку (Z-) - раньше эти стороны
        // не сливались (Wedge их вообще не рисовал через ChunkMesher), теперь они склеиваются в общие полигоны
        // (пункт 1 из ROADMAP.md - "прямые плоскости не-кубических блоков между собой/с кубами не склеивались").
        var mixed = new VoxelGrid();
        mixed.TrySet(new Vector3I(0, 0, 0), block.RuntimeId, CellColor.Pack(Colors.Gray));
        mixed.TrySet(new Vector3I(1, 0, 0), wedge.RuntimeId, CellColor.Pack(Colors.Gray), wedgeMask);
        var mixedStats = Measure(mixed);
        Check(mixedStats.Faces == 8 && mixedStats.Quads == 6,
            "a cube next to a non-cube block does not cull their touching (partially covered) side, but their shared " +
            "bottom/back planes (both fully covered, same color) merge into single quads across the seam",
            $"faces={mixedStats.Faces} quads={mixedStats.Quads}");

        // Два одинаковых скоса впритык вдоль X: оба открытых низа (Y-) сливаются в один прямоугольник, и обе задние
        // стенки (Z-) - в другой. 4 отдельные грани -> 2 склеенных полигона (снова пункт 1 из ROADMAP.md, теперь
        // между двумя не-кубическими формами, а не формой и кубом).
        var merging = new VoxelGrid();
        merging.TrySet(new Vector3I(0, 0, 0), wedge.RuntimeId, CellColor.Pack(Colors.Gray), wedgeMask);
        merging.TrySet(new Vector3I(1, 0, 0), wedge.RuntimeId, CellColor.Pack(Colors.Gray), wedgeMask);
        var mergingStats = Measure(merging);
        Check(mergingStats.Faces == 4 && mergingStats.Quads == 2,
            "two adjacent same-colored shapes merge their touching full-coverage sides into a single quad each",
            $"faces={mergingStats.Faces} quads={mergingStats.Quads}");

        // Два скоса, повёрнутые так, что их полные грани обращены друг к другу (A: Y-/Z- смотрят "вниз/назад";
        // B повёрнут на 180° вокруг X, поэтому его полные грани смотрят Y+/Z+ - "вверх/вперёд", то есть НА A) -
        // взаимно культят стык, вместо того чтобы обе рисовать невидимую снаружи внутреннюю грань (пункт 2 из
        // ROADMAP.md - "поверхности блоков, стоящие вплотную и недоступные для взгляда игроку, всё равно
        // отрисовываются"). Остаются только внешние стороны каждого (Z- у A, Z+ у B).
        byte wedgeMaskFlipped = ShapeMeshBuilder.FullCoverageMask(BlockShape.Slope, new Vector3I(2, 0, 0), Vector3I.Zero);
        Check(wedgeMaskFlipped == 0b101000, "Wedge rotated 180 around X fully covers Y+ and Z+ instead", $"got {Convert.ToString(wedgeMaskFlipped, 2)}");
        var touching = new VoxelGrid();
        touching.TrySet(new Vector3I(0, 0, 0), wedge.RuntimeId, CellColor.Pack(Colors.Gray), wedgeMask);
        touching.TrySet(new Vector3I(0, -1, 0), wedge.RuntimeId, CellColor.Pack(Colors.Gray), wedgeMaskFlipped);
        var touchingStats = Measure(touching);
        Check(touchingStats.Faces == 2 && touchingStats.Quads == 2,
            "two shapes whose full-coverage sides face each other mutually cull that shared, invisible-from-outside face",
            $"faces={touchingStats.Faces} quads={touchingStats.Quads}");

        // Куб рядом с кубом по-прежнему культится нормально (сосед гарантированно закрывает всю грань).
        var cubes = new VoxelGrid();
        cubes.TrySet(new Vector3I(0, 0, 0), block.RuntimeId, CellColor.Pack(Colors.Gray));
        cubes.TrySet(new Vector3I(1, 0, 0), block.RuntimeId, CellColor.Pack(Colors.Gray));
        var cubesStats = Measure(cubes);
        Check(cubesStats.Faces == 10 && cubesStats.Quads == 6,
            "two adjacent cubes still cull their shared touching faces (both count as full coverage)",
            $"faces={cubesStats.Faces} quads={cubesStats.Quads}");
    }

    // ================================================================== Resize многоклеточных форм: баг с ложными
    // внутренними стенками (см. Construction.BoundaryFaceMask) и отсечение спрятанных частичных граней
    // (см. ShapeMeshBuilder.ComputeOcclusionMask) — оба найдены на реальной постройке пользователя (растянутая
    // InvertedPyramid у предела MaxSize давала торчащие "перпендикулярные стенки" по каждой внутренней границе
    // клеток), см. Docs/WORKLOG.md.

    private void RunResizeMeshingTests()
    {
        GD.Print("-- resize meshing: stretched non-cube instances must not draw spurious internal walls, and must hide partial faces fully backed by a neighbor");

        var catalog = BlockCatalog.Instance;
        var block = catalog.Get("block");
        var wedge = catalog.Get("wedge");
        var invPyramid = catalog.Get("inverse_pyramid");

        // --- баг 3 (PlaceBlock): Wedge растянут вдоль Z (его "задняя стенка" Z- есть только у формы САМОЙ ПО СЕБЕ,
        // а не у каждой клетки растяжения) на 5 клеток. Ожидаемо: низ (Y-, у ВСЕХ 5 клеток — размер по Y всё ещё 1,
        // так что тут они одновременно и Origin, и MaxCell) сливается в 1 полосу; задняя стенка — только у самой
        // первой (Origin.Z) клетки, ни одной лишней внутренней стенки на границах клеток 0|1, 1|2, 2|3, 3|4.
        // (до фикса Construction.BoundaryFaceMask здесь было faces=10 quads=6 — по лишней стенке на каждой из
        // 4 внутренних границ, ни одна не сливалась с соседней, т.к. каждая была на своём Z-срезе.)
        var gridA = new VoxelGrid();
        var conA = new Construction(gridA);
        conA.PlaceBlock(new Vector3I(0, 0, 0), new Vector3I(1, 1, 5), wedge, wedge.DefaultColor);
        var statsA = Measure(gridA);
        Check(statsA.Faces == 6 && statsA.Quads == 2,
            "PlaceBlock: a Wedge stretched 5 cells along its closed axis (Z-) draws that wall only once (at the true " +
            "boundary), not once per internal cell edge — bottom merges into 1 strip, back stays 1 face",
            $"faces={statsA.Faces} quads={statsA.Quads}");

        // --- баг 3 (TrySetSize, тот же сценарий, но через рост уже стоящего блока, а не PlaceBlock за один раз —
        // раньше маска у уже стоявших клеток при росте не пересчитывалась вообще, см. комментарий в TrySetSize).
        var gridB = new VoxelGrid();
        var conB = new Construction(gridB);
        var grown = conB.Place(new Vector3I(0, 0, 0), wedge, wedge.DefaultColor);
        conB.TrySetSize(grown!, wedge, new Vector3I(1, 1, 5));
        var statsB = Measure(gridB);
        Check(statsB.Faces == 6 && statsB.Quads == 2,
            "TrySetSize: growing a Wedge to span 5 cells along Z- has the same fix applied as PlaceBlock " +
            "(existing cells' masks are recomputed too, not just the newly added ones)",
            $"faces={statsB.Faces} quads={statsB.Quads}");

        // --- воспроизведение бага именно с InvertedPyramid у большого размера (тот случай, что видел пользователь):
        // 3 полные стороны (x=0,y=0,z=0 локально) должны остаться РОВНО 3 склеенными прямоугольниками на весь
        // bounding box, без единой лишней "перпендикулярной" стенки внутри.
        var gridC = new VoxelGrid();
        var conC = new Construction(gridC);
        conC.PlaceBlock(new Vector3I(0, 0, 0), new Vector3I(3, 2, 8), invPyramid, invPyramid.DefaultColor);
        var statsC = Measure(gridC);
        int expectedFacesC = 2 * 8 /* x=0: Y*Z */ + 3 * 8 /* y=0: X*Z */ + 3 * 2 /* z=0: X*Y */;
        Check(statsC.Faces == expectedFacesC && statsC.Quads == 3,
            "PlaceBlock: a 3x2x8 InvertedPyramid (matches the size that showed the bug in-game) merges each of its " +
            "3 full-coverage sides into exactly 1 quad each, with zero extra internal walls",
            $"faces={statsC.Faces} quads={statsC.Quads} (expected faces={expectedFacesC} quads=3)");

        // Ни одна КЛЕТКА этого экземпляра, кроме самой Origin, не должна нести бит Z- (иначе где-то среди её
        // внутренних соседей возникла бы точка для ложной стенки) - прямая проверка данных, а не только их следствия.
        for (int z = 0; z <= 7; z++)
        {
            byte mask = gridC.GetFaceMask(new Vector3I(0, 0, z));
            bool hasZMinus = (mask & (1 << 4)) != 0;
            Check(hasZMinus == (z == 0), $"InvertedPyramid cell z={z}: Z- bit set only at the true boundary (z=0)", $"mask={Convert.ToString(mask, 2)}");
        }

        // --- баг 2: частичная (не FullCoverage) грань формы, полностью спрятанная за соседом, который целиком
        // закрывает СВОЮ обращённую сюда сторону, больше не рисуется. Wedge в (0,0,0), куб в (1,0,0) - правый
        // треугольный борт Wedge (нормаль +X) полностью загорожен левой стороной куба.
        var gridD = new VoxelGrid();
        gridD.TrySet(new Vector3I(1, 0, 0), block.RuntimeId, CellColor.Pack(Colors.Gray));
        byte occludedD = ShapeMeshBuilder.ComputeOcclusionMask(gridD, Vector3I.Zero, Vector3I.Zero);
        Check((occludedD & (1 << 1)) != 0, "ComputeOcclusionMask: +X is occluded when a solid cube sits fully closing that side", $"mask={Convert.ToString(occludedD, 2)}");
        var wedgeMeshBaseline = ShapeMeshBuilder.BuildData(BlockShape.Slope, Vector3I.One, Vector3I.Zero, Vector3I.Zero, Colors.White)!;
        var wedgeMeshOccluded = ShapeMeshBuilder.BuildData(BlockShape.Slope, Vector3I.One, Vector3I.Zero, Vector3I.Zero, Colors.White, occludedD)!;
        Check(wedgeMeshBaseline.Indices.Count / 3 == 4 && wedgeMeshOccluded.Indices.Count / 3 == 3,
            "BuildData: occluding +X drops the right triangular bort (1 of Wedge's 4 partial triangles), the ramp " +
            "and left bort are untouched",
            $"baseline={wedgeMeshBaseline.Indices.Count / 3} occluded={wedgeMeshOccluded.Indices.Count / 3}");

        // --- не-регрессия: сосед есть, но НЕ закрывает свою сторону целиком (другой Wedge своим треугольным
        // бортом, а не полной гранью) - occludedMask должен остаться 0 для этого направления (иначе там появилась
        // бы настоящая дыра, а не спрятанная-и-безопасно-убранная грань).
        var gridE = new VoxelGrid();
        byte otherWedgeMask = ShapeMeshBuilder.FullCoverageMask(BlockShape.Slope, Vector3I.Zero, Vector3I.Zero);
        gridE.TrySet(new Vector3I(1, 0, 0), wedge.RuntimeId, CellColor.Pack(Colors.Gray), otherWedgeMask);
        byte occludedE = ShapeMeshBuilder.ComputeOcclusionMask(gridE, Vector3I.Zero, Vector3I.Zero);
        Check((occludedE & (1 << 1)) == 0,
            "ComputeOcclusionMask: a neighbor that does NOT fully close its facing side (another Wedge's triangular " +
            "bort, not a full face) never occludes - stays conservative, no hole is created",
            $"mask={Convert.ToString(occludedE, 2)}");

        // --- не-регрессия: у растянутого (не 1x1x1) экземпляра, где только ЧАСТЬ границы имеет закрывающего
        // соседа, направление НЕ считается закрытым целиком - иначе часть, которая реально открыта, потеряла бы
        // свою грань (дыра). Wedge растянут на 3 клетки по Y (тут X=0 - боковой борт по всей длине), куб стоит
        // только рядом с одной из трёх клеток.
        var gridF = new VoxelGrid();
        gridF.TrySet(new Vector3I(1, 1, 0), block.RuntimeId, CellColor.Pack(Colors.Gray)); // сосед только у средней клетки (y=1)
        byte occludedF = ShapeMeshBuilder.ComputeOcclusionMask(gridF, Vector3I.Zero, new Vector3I(0, 2, 0));
        Check((occludedF & (1 << 1)) == 0,
            "ComputeOcclusionMask: a closing neighbor next to only part of a stretched instance's boundary does not " +
            "occlude that whole side - the still-open part would otherwise get a hole",
            $"mask={Convert.ToString(occludedF, 2)}");
    }

    // ================================================================== вращение перед установкой (J/K/L)

    private void RunRotationStateTests()
    {
        GD.Print("-- pending rotation: J/K/L increment X/Y/Z mod 4");

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

    // ================================================================== точечная покраска наклонных/треугольных граней

    /// <summary>
    /// Было "сознательно отложено" (см. Docs/ROADMAP.md), затем стало багом ("красятся все подобные поверхности, а
    /// не конкретно выбранная") - раньше <c>BuildEditor.UseToolAtHover</c> красил ВЕСЬ экземпляр целиком в ответ на
    /// клик по любой наклонной/треугольной грани (ту же рампу, оба борта, низ и заднюю стенку разом), т.к.
    /// <see cref="ShapeMeshBuilder"/> принимал ровно один цвет на весь меш. Теперь <see cref="BlockInstance.RegionColors"/> +
    /// <see cref="ShapeMeshBuilder.TryFindPaintRegion"/> красят РОВНО ту грань формы, в которую попал луч.
    /// </summary>
    private void RunPaintRegionTests()
    {
        GD.Print("-- point paint of non-cube shape faces (ramp/triangular sides), not the whole instance");

        // TryRaycastFace: НАСТОЯЩЕЕ пересечение луча с реальной геометрией формы - в отличие от TryFindPaintRegion
        // (приближение по осевому направлению попадания в ограничивающий куб клетки), может попасть НАПРЯМУЮ в
        // диагональную грань. Срез InvertedPyramid (грань 6, плоскость x+y+z=2, вершины (1,1,0)/(1,0,1)/(0,1,1)) —
        // раньше был принципиально недостижим через TryFindPaintRegion (все 6 осевых направлений её куба заняты
        // другими гранями формы, см. её doc-комментарий) - с точным рейкастом луч, направленный точно в центр этого
        // треугольника, попадает в него напрямую.
        var invSliceCentroidUnit = new Vector3(2f / 3f, 2f / 3f, 2f / 3f); // среднее вершин (1,1,0),(1,0,1),(0,1,1)
        var invSliceCentroidWorld = invSliceCentroidUnit * BuildSpace.CellSize; // extent = CellSize при size=1x1x1
        var rayFromOutsideCorner = new Vector3(2f, 2f, 2f);
        var towardSlice = (invSliceCentroidWorld - rayFromOutsideCorner).Normalized();
        Check(ShapeMeshBuilder.TryRaycastFace(BlockShape.InvertedPyramid, Vector3I.One, Vector3I.Zero, Vector3I.Zero, Vector3.Zero, rayFromOutsideCorner, towardSlice, out int invSliceRegion) && invSliceRegion == 6,
            "InvertedPyramid: a ray aimed at the diagonal slice's centroid hits it directly (face index 6) - previously unreachable via the axis-bit fallback alone",
            $"region={invSliceRegion}");

        var awayFromSlice = new Vector3(1f, 1f, 1f).Normalized(); // прочь от среза, не к нему
        Check(!ShapeMeshBuilder.TryRaycastFace(BlockShape.InvertedPyramid, Vector3I.One, Vector3I.Zero, Vector3I.Zero, Vector3.Zero, rayFromOutsideCorner, awayFromSlice, out _),
            "InvertedPyramid: a ray pointed away from the shape hits nothing (no false positive behind the ray origin)");

        // Тот же точный рейкаст даёт то же самое, что осевое приближение, там, где оно и так было прямым попаданием
        // (левый треугольный борт Wedge, x=0) - не регрессия для уже работавшего случая.
        var leftBortCentroidWorld = new Vector3(0f, 1f / 3f, 1f / 3f) * BuildSpace.CellSize; // среднее (0,0,0),(0,1,0),(0,0,1)
        var rayFromOutsideLeft = new Vector3(-5f, leftBortCentroidWorld.Y, leftBortCentroidWorld.Z);
        Check(ShapeMeshBuilder.TryRaycastFace(BlockShape.Slope, Vector3I.One, Vector3I.Zero, Vector3I.Zero, Vector3.Zero, rayFromOutsideLeft, Vector3.Right, out int wedgeLeftRegion) && wedgeLeftRegion == 3,
            "Wedge: a ray aimed at the left bort's centroid hits it directly (face index 3), matching TryFindPaintRegion for this case");

        const byte xMinus = 1 << 0, xPlus = 1 << 1, yMinus = 1 << 2, yPlus = 1 << 3, zMinus = 1 << 4, zPlus = 1 << 5;

        // Запасной вариант (TryFindPaintRegion, используется только когда TryRaycastFace не находит настоящего
        // пересечения): Wedge: X-/X+ - его собственные треугольные борта, попадание бьёт напрямую по индексу. Y+/Z+ у формы вообще
        // нет своей грани (см. BlockGeometry: WedgeFaces не содержит нормали (0,1,0)/(0,0,1)) - на самом деле там
        // видна диагональная рампа, поэтому оба откатываются на её индекс (2).
        Check(ShapeMeshBuilder.TryFindPaintRegion(BlockShape.Slope, Vector3I.Zero, Vector3I.Zero, xMinus, out int leftBort) && leftBort == 3,
            "Wedge: hitting X- maps directly to its left triangular bort (face index 3)");
        Check(ShapeMeshBuilder.TryFindPaintRegion(BlockShape.Slope, Vector3I.Zero, Vector3I.Zero, xPlus, out int rightBort) && rightBort == 4,
            "Wedge: hitting X+ maps directly to its right triangular bort (face index 4)");
        Check(ShapeMeshBuilder.TryFindPaintRegion(BlockShape.Slope, Vector3I.Zero, Vector3I.Zero, yPlus, out int rampFromTop) && rampFromTop == 2,
            "Wedge: hitting Y+ (no face of its own there - the ramp is what's actually visible) falls back to the ramp (face index 2)");
        Check(ShapeMeshBuilder.TryFindPaintRegion(BlockShape.Slope, Vector3I.Zero, Vector3I.Zero, zPlus, out int rampFromFront) && rampFromFront == 2,
            "Wedge: hitting Z+ falls back to the same ramp (face index 2)");

        // Pyramid: базовая/z=0/x=0 грани - осеориентированные (хоть и треугольные, не покрывают грань клетки
        // целиком - см. BlockGeometry) - попадание бьёт напрямую; X+/Y+/Z+ не имеют своей грани - откат на срез (3).
        Check(ShapeMeshBuilder.TryFindPaintRegion(BlockShape.Pyramid, Vector3I.Zero, Vector3I.Zero, yMinus, out int pyramidBase) && pyramidBase == 0,
            "Pyramid: hitting Y- maps directly to its base (face index 0)");
        Check(ShapeMeshBuilder.TryFindPaintRegion(BlockShape.Pyramid, Vector3I.Zero, Vector3I.Zero, zMinus, out int pyramidBack) && pyramidBack == 1,
            "Pyramid: hitting Z- maps directly to its z=0 face (face index 1)");
        Check(ShapeMeshBuilder.TryFindPaintRegion(BlockShape.Pyramid, Vector3I.Zero, Vector3I.Zero, xMinus, out int pyramidSide) && pyramidSide == 2,
            "Pyramid: hitting X- maps directly to its x=0 face (face index 2)");
        Check(ShapeMeshBuilder.TryFindPaintRegion(BlockShape.Pyramid, Vector3I.Zero, Vector3I.Zero, xPlus, out int pyramidSlice) && pyramidSlice == 3,
            "Pyramid: hitting X+ (no face of its own - only the diagonal slice is there) falls back to it (face index 3)");

        // InvertedPyramid: ВСЕ 6 осевых направлений заняты своими гранями (3 FullCoverage + 3 усечённых треугольных) -
        // диагональный срез (индекс 6) в принципе не достижим таким рейкастом (см. doc-комментарий TryFindPaintRegion,
        // ROADMAP.md, оставшийся scoped-гэп) - тут проверяем только 3 усечённых грани, попадание в которые как раз
        // возможно (X-/Y-/Z- у неё FullCoverage, красятся через VoxelGrid, сюда не попадают вовсе).
        Check(ShapeMeshBuilder.TryFindPaintRegion(BlockShape.InvertedPyramid, Vector3I.Zero, Vector3I.Zero, xPlus, out int invTruncX) && invTruncX == 3,
            "InvertedPyramid: hitting X+ maps directly to its truncated corner there (face index 3)");
        Check(ShapeMeshBuilder.TryFindPaintRegion(BlockShape.InvertedPyramid, Vector3I.Zero, Vector3I.Zero, yPlus, out int invTruncY) && invTruncY == 4,
            "InvertedPyramid: hitting Y+ maps directly to its truncated corner there (face index 4)");
        Check(ShapeMeshBuilder.TryFindPaintRegion(BlockShape.InvertedPyramid, Vector3I.Zero, Vector3I.Zero, zPlus, out int invTruncZ) && invTruncZ == 5,
            "InvertedPyramid: hitting Z+ maps directly to its truncated corner there (face index 5)");

        Check(!ShapeMeshBuilder.TryFindPaintRegion(BlockShape.Cube, Vector3I.Zero, Vector3I.Zero, xMinus, out _),
            "Cube has no ShapeMeshBuilder geometry at all - TryFindPaintRegion correctly refuses it (a cube face paints through VoxelGrid instead)");

        // BuildData: regionColors переопределяет цвет РОВНО указанной грани (по стабильному индексу, не по
        // геометрии) - остальные грани красятся в общий color, как и раньше.
        var green = CellColor.Pack(Colors.Green);
        var overridden = ShapeMeshBuilder.BuildData(BlockShape.Slope, Vector3I.One, Vector3I.Zero, Vector3I.Zero, Colors.White,
            regionColors: new Dictionary<int, uint> { [2] = green })!;
        int greenVerts = overridden.Colors.Count(c => c == Colors.Green);
        int whiteVerts = overridden.Colors.Count(c => c == Colors.White);
        Check(greenVerts == 4 && whiteVerts == 6,
            "Wedge BuildData: regionColors[2] (ramp, 4 vertices) paints only the ramp green, the 2 triangular borts (3+3 vertices) stay white",
            $"green={greenVerts} white={whiteVerts}");

        // Construction: PaintRegion меняет только BlockInstance.RegionColors (не VoxelGrid, не представительный
        // Color); Paint (весь экземпляр) сбрасывает точечные правки - иначе "перекрасить целиком" не выглядело бы
        // таковым, если старые точечные акценты продолжали бы проступать поверх нового цвета.
        var construction = new Construction(new VoxelGrid());
        var wedge = construction.Place(new Vector3I(0, 0, 0), BlockCatalog.Instance.Get("wedge"), Colors.Gray)!;
        Check(construction.PaintRegion(wedge, 2, Colors.Green), "PaintRegion returns true when the region's color actually changes");
        Check(!construction.PaintRegion(wedge, 2, Colors.Green), "PaintRegion returns false when called again with the same color (no-op)");
        Check(construction.PaintRegion(wedge, 3, Colors.Blue), "PaintRegion on a second, different region also succeeds independently");
        Check(wedge.Color == CellColor.Pack(Colors.Gray), "PaintRegion never touches the instance's own representative Color");
        Check(wedge.RegionColors != null && wedge.RegionColors.Count == 2
              && wedge.RegionColors[2] == green && wedge.RegionColors[3] == CellColor.Pack(Colors.Blue),
            "PaintRegion keeps both region overrides side by side (ramp green, left bort blue), not overwriting one with the other");

        construction.Paint(wedge, Colors.Red);
        Check(wedge.Color == CellColor.Pack(Colors.Red) && wedge.RegionColors is not { Count: > 0 },
            "whole-instance Paint clears any region overrides - a full repaint should look like one, not show old accents through it");
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

        // Отражение: Place/PlaceBlock принимают и хранят Mirror (по умолчанию — [0,0,0]), независимо от RotationSteps.
        var m = construction.Place(new Vector3I(70, 0, 0), wedge, wedge.DefaultColor);
        Check(m != null && m!.Mirror == Vector3I.Zero, "Place: defaults to no mirror when omitted");
        var n = construction.Place(new Vector3I(75, 0, 0), wedge, wedge.DefaultColor, mirror: new Vector3I(1, 0, 1));
        Check(n != null && n!.Mirror == new Vector3I(1, 0, 1), "Place: stores the given mirror flags on the new instance");

        RunSaveLoadTests(catalog, block, wedge, pyramid);
    }

    private void RunSaveLoadTests(BlockCatalog catalog, BlockDefinition block, BlockDefinition wedge, BlockDefinition pyramid)
    {
        var saved = new Construction(new VoxelGrid());
        saved.Place(new Vector3I(0, 0, 0), block, Colors.Red);
        saved.PlaceBlock(new Vector3I(5, 0, 0), new Vector3I(2, 1, 3), wedge, Colors.Green, new Vector3I(1, 2, 3), new Vector3I(1, 0, 1));
        string json = ConstructionIO.Serialize(saved);

        var loaded = new Construction(new VoxelGrid());
        var (loadedCount, skippedCount) = ConstructionIO.Deserialize(loaded, json, catalog);
        Check(loadedCount == 2 && skippedCount == 0, $"Save/Load: round-trips {loadedCount} block(s) through JSON with 0 skipped");

        bool blockOk = loaded.Instances.Any(i =>
            i.BlockSlug == "block" && i.Origin == new Vector3I(0, 0, 0) && i.Size == Vector3I.One && i.Color == CellColor.Pack(Colors.Red));
        bool wedgeOk = loaded.Instances.Any(i =>
            i.BlockSlug == "wedge" && i.Origin == new Vector3I(5, 0, 0) && i.Size == new Vector3I(2, 1, 3)
            && i.Color == CellColor.Pack(Colors.Green) && i.RotationSteps == new Vector3I(1, 2, 3) && i.Mirror == new Vector3I(1, 0, 1));
        Check(blockOk && wedgeOk, "Save/Load: slug, origin, size, color, rotation and mirror survive the round-trip");

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

    // ================================================================== история отмены/повтора (Ctrl+Z/Ctrl+Y)

    private void RunUndoHistoryTests()
    {
        GD.Print("-- undo history: snapshot-based Ctrl+Z/Ctrl+Y over Construction");

        var catalog = BlockCatalog.Instance;
        var block = catalog.Get("block");
        var construction = new Construction(new VoxelGrid());
        var history = new UndoHistory();

        Check(!history.CanUndo && !history.CanRedo, "UndoHistory starts empty");

        // Действие, которое ничего не меняет (клетка уже занята), не должно создавать запись в истории.
        construction.Place(new Vector3I(0, 0, 0), block, block.DefaultColor);
        var beforeNoop = history.Capture(construction);
        Check(construction.PlaceBlock(new Vector3I(0, 0, 0), Vector3I.One, block, block.DefaultColor) == null,
            "undo setup: placing on an occupied cell fails");
        history.RecordIfChanged(beforeNoop, construction);
        Check(!history.CanUndo, "a no-op action (nothing actually changed) is not recorded");
        construction.Clear();

        // Основной цикл: место -> Undo -> Redo.
        var empty = history.Capture(construction);
        construction.Place(new Vector3I(2, 0, 0), block, Colors.Red);
        history.RecordIfChanged(empty, construction);
        Check(history.CanUndo && !history.CanRedo, "placing a block records one undo entry");

        Check(history.Undo(construction, catalog), "Undo succeeds");
        Check(construction.Instances.Count == 0 && history.CanRedo, "Undo removes the placed block and enables Redo");

        Check(history.Redo(construction, catalog), "Redo succeeds");
        Check(construction.Instances.Count == 1 && construction.Instances.First().Color == CellColor.Pack(Colors.Red),
            "Redo restores the block (with its color)");

        // Новое действие после Undo обрывает redo-ветку (стандартное поведение истории в любом редакторе).
        Check(history.Undo(construction, catalog) && history.CanRedo, "undo again to set up a redo branch");
        var beforeNewAction = history.Capture(construction);
        construction.Place(new Vector3I(9, 0, 0), block, block.DefaultColor);
        history.RecordIfChanged(beforeNewAction, construction);
        Check(!history.CanRedo, "a new action after Undo clears the redo stack");

        Check(!history.Redo(construction, catalog), "Redo on an empty redo stack does nothing and returns false");

        // Точечная покраска ГРАНИ переживает Undo/Redo не хуже установки/удаления - раньше снэпшот истории был
        // просто ConstructionIO.Serialize (только per-instance представительный цвет), который точечных перекрасок
        // граней вообще не видел (см. class doc UndoHistory) - Undo "терял" бы такую покраску.
        construction.Clear();
        construction.Place(new Vector3I(20, 0, 0), block, block.DefaultColor);
        var beforeFacePaint = history.Capture(construction);
        uint faceRed = CellColor.Pack(Colors.Red);
        construction.Grid.TryPaintFace(new Vector3I(20, 0, 0), 1, true, faceRed);
        history.RecordIfChanged(beforeFacePaint, construction);
        Check(history.CanUndo, "painting a single face records an undo entry even though no instance changed");

        Check(history.Undo(construction, catalog) && construction.Grid.GetFaceColor(new Vector3I(20, 0, 0), 1, true) != faceRed,
            "Undo restores the face's previous color");
        Check(history.Redo(construction, catalog) && construction.Grid.GetFaceColor(new Vector3I(20, 0, 0), 1, true) == faceRed,
            "Redo re-applies the face paint");

        // Точечная покраска ГРАНИ ФОРМЫ (Construction.PaintRegion, см. BlockInstance.RegionColors) тоже переживает
        // Undo/Redo - хранится по Origin экземпляра, а не по InstanceId (см. class doc: Deserialize строит экземпляры
        // заново, с новыми id по порядку, Origin же остаётся тем же).
        construction.Clear();
        var wedgeForUndo = construction.Place(new Vector3I(30, 0, 0), catalog.Get("wedge"), Colors.Gray)!;
        var beforeRegionPaint = history.Capture(construction);
        construction.PaintRegion(wedgeForUndo, 2, Colors.Green); // рампа
        history.RecordIfChanged(beforeRegionPaint, construction);
        Check(history.CanUndo, "painting a shape's region records an undo entry even though the instance's own Color did not change");

        Check(history.Undo(construction, catalog), "Undo (region paint) succeeds");
        var wedgeAfterUndo = construction.Instances.First();
        Check(wedgeAfterUndo.RegionColors is not { Count: > 0 }, "Undo removes the region color override");

        Check(history.Redo(construction, catalog), "Redo (region paint) succeeds");
        var wedgeAfterRedo = construction.Instances.First();
        Check(wedgeAfterRedo.RegionColors != null && wedgeAfterRedo.RegionColors.TryGetValue(2, out uint restoredRegion) && restoredRegion == CellColor.Pack(Colors.Green),
            "Redo re-applies the region color override");
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

        // Редактор сам ставит корневой блок 1x1 в центральную клетку (0,0,0) при входе (см. BuildEditor.PlaceRootBlock) -
        // проверяем это здесь, ДО того как остальные тесты ниже расчистят сцену под себя (Construction.Clear()).
        var rootOwner = editor.World.Construction.GetOwner(Vector3I.Zero);
        Check(rootOwner != null && rootOwner.BlockSlug == "block" && rootOwner.Size == Vector3I.One,
            "the editor places a 1x1x1 root block in the center cell (0,0,0) on start", $"{rootOwner}");

        // Без окна (headless) корневой вьюпорт всего 64x64 — задаём реальный размер, чтобы вёрстка UI и проекции были осмысленными.
        editor.GetTree().Root.Size = new Vector2I(1600, 900);
        await Frames(editor, 3);
        GD.Print($"  info  viewport={editor.GetViewport().GetVisibleRect().Size} display={DisplayServer.GetName()} window={DisplayServer.WindowGetSize()}");
        // Construction.Clear(), не голый grid.Clear(): редактор при входе сам ставит корневой блок 1x1 в центр
        // (см. BuildEditor.PlaceRootBlock) - это настоящий экземпляр Construction, просто обнулить сетку недостаточно
        // (Construction продолжала бы считать эту клетку занятой её экземпляром - расхождение с VoxelGrid).
        editor.World.Construction.Clear();
        state.Tool = ToolMode.None;
        state.Wireframe = false;
        state.Borders = true;

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

        var ghostMaterial = (StandardMaterial3D)editor.Ghost.MaterialOverride;
        Check(editor.Ghost.Visible && ghostMaterial.Transparency == BaseMaterial3D.TransparencyEnum.Disabled && ghostMaterial.AlbedoColor.A >= 0.999f,
            "placement ghost is opaque, not a translucent preview (looks like an installed block)",
            $"visible={editor.Ghost.Visible} transparency={ghostMaterial.Transparency} alpha={ghostMaterial.AlbedoColor.A}");

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

        // 3. Инструменты тулбара: и Paint, и Delete — на ЛКМ (см. BuildEditor.ButtonFor); не конфликтуют, т.к.
        // взаимоисключающие режимы. ПКМ инструментам не назначена вообще. Цель - свежий обычный куб (не только что
        // поставленный на (0,2,0) InvertedPyramid - у него "верх" не FullCoverage, эта механика отдельно проверена
        // ниже, в тесте про покраску скошенной поверхности формы), чтобы "покрашена ровно одна грань, остальные не
        // тронуты" проверялось однозначно, на форме, где это в принципе применимо.
        state.SelectedSlot = 0;
        await Click(editor, Screen(TopOf(new Vector3I(0, 2, 0))), MouseButton.Left);
        var target = new Vector3I(0, 3, 0);
        Check(grid.GetId(target) == BlockCatalog.Instance.Get("block").RuntimeId, "paint/delete test setup: a plain cube sits at the test target cell");

        state.PaintColor = Colors.Red;
        state.Tool = ToolMode.Paint;
        await Move(editor, Screen(TopOf(target)));
        Check(!editor.Ghost.Visible, "paint tool active: placement ghost is hidden even over a free cell");
        await Click(editor, Screen(TopOf(target)), MouseButton.Right);
        Check(grid.GetFaceColor(target, 1, true) != CellColor.Pack(Colors.Red), "paint tool active: RMB does nothing (Paint is on LMB)");
        int blocksBeforePaint = grid.BlockCount;
        await Click(editor, Screen(TopOf(target)), MouseButton.Left);
        Check(grid.BlockCount == blocksBeforePaint && grid.GetId(new Vector3I(0, 4, 0)) == 0,
            "paint tool active: LMB does not place a block (it paints instead)");
        // По грани, а не по всему блоку: TopOf наводит на верхнюю (Y+) грань - красится ровно она, остальные
        // 5 граней клетки (в т.ч. "представительная" грань X-, которую отдаёт GetColor) остаются как были.
        Check(grid.GetFaceColor(target, 1, true) == CellColor.Pack(Colors.Red) && grid.GetId(target) != 0,
            "paint tool recolors exactly the face under the cursor (top, Y+), not the whole block");
        Check(grid.GetColor(target) != CellColor.Pack(Colors.Red),
            "paint tool leaves the other faces of the same cell untouched (representative X- face still the block's default color)");

        state.Tool = ToolMode.Delete;
        await Move(editor, Screen(TopOf(target)));
        Check(!editor.Ghost.Visible, "delete tool active: placement ghost is hidden too");
        await Click(editor, Screen(TopOf(target)), MouseButton.Right);
        Check(grid.GetId(target) != 0, "delete tool active: RMB does nothing (Delete is on LMB, RMB is unused by tools)");
        await Click(editor, Screen(TopOf(target)), MouseButton.Left);
        Check(grid.GetId(target) == 0 && grid.BlockCount == 3, "delete tool removes the block under the cursor (LMB)");
        state.Tool = ToolMode.None;
        await Move(editor, Screen(TopOf(new Vector3I(0, 2, 0)))); // target (0,3,0) только что удалён - точно свободен
        Check(editor.Ghost.Visible, "no tool active: placement ghost is visible again");

        // `X` — горячая клавиша Delete, эквивалент клика по кнопке на тулбаре (повторное нажатие выключает).
        await PressKey(editor, Godot.Key.X);
        Check(state.Tool == ToolMode.Delete, "X activates the Delete tool");
        await PressKey(editor, Godot.Key.X);
        Check(state.Tool == ToolMode.None, "X again deactivates it");

        // Wireframe и Borders — независимые переключатели (не цикл): Wireframe скрывает сплошные грани, Borders
        // не зависит ни от Wireframe, ни от сплошных граней.
        editor.World.RebuildDirty();

        (int solid, int wire, int border) CountVisible()
        {
            int solidVisible = 0, wireVisible = 0, borderVisible = 0;
            foreach (var child in editor.World.GetChildren())
            {
                if (child is not MeshInstance3D mesh) continue;
                if (mesh.Name.ToString().StartsWith("Solid_") && mesh.Visible) solidVisible++;
                if (mesh.Name.ToString().StartsWith("Wire_") && mesh.Visible) wireVisible++;
                if (mesh.Name.ToString().StartsWith("Border_") && mesh.Visible) borderVisible++;
            }

            return (solidVisible, wireVisible, borderVisible);
        }

        state.Wireframe = false;
        state.Borders = true;
        var v1 = CountVisible();
        Check(v1.solid == 1 && v1.wire == 0 && v1.border == 1, "default: solid + borders, no wireframe", $"{v1}");

        state.Wireframe = true;
        var v2 = CountVisible();
        Check(v2.solid == 0 && v2.wire == 1 && v2.border == 1, "wireframe on hides solid, borders stay independent", $"{v2}");

        state.Borders = false;
        var v3 = CountVisible();
        Check(v3.solid == 0 && v3.wire == 1 && v3.border == 0, "borders off while wireframe stays on", $"{v3}");

        state.Wireframe = false;
        var v4 = CountVisible();
        Check(v4.solid == 1 && v4.wire == 0 && v4.border == 0, "wireframe off restores solid, borders still off", $"{v4}");

        state.Borders = true;

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
        bool HasActiveIndicator(Button b) => b.GetChildren().OfType<Panel>().Any(p => p.Visible);

        await Click(editor, CenterOf(FindButton("Delete")!), MouseButton.Left);
        Check(state.Tool == ToolMode.Delete, "toolbar: Delete button activates the delete tool");
        Check(HasActiveIndicator(FindButton("Delete")!), "toolbar: active tool button shows its indicator dot");
        await Click(editor, CenterOf(FindButton("Delete")!), MouseButton.Left);
        Check(state.Tool == ToolMode.None, "toolbar: pressing the active tool again deactivates it");
        Check(!HasActiveIndicator(FindButton("Delete")!), "toolbar: indicator dot hides once the tool deactivates");
        await Click(editor, CenterOf(FindButton("Paint")!), MouseButton.Left);
        Check(state.Tool == ToolMode.Paint, "toolbar: Paint button activates the paint tool");
        Check(FindButton("+ Save color") != null, "toolbar: the paint panel (sliders/palette) is visible while Paint is active");
        await Click(editor, CenterOf(FindButton("Delete")!), MouseButton.Left);
        Check(state.Tool == ToolMode.Delete, "toolbar: tools are mutually exclusive");
        Check(FindButton("+ Save color") == null, "toolbar: the paint panel hides again once Paint is no longer active");
        await Click(editor, CenterOf(FindButton("Resize")!), MouseButton.Left);
        Check(state.ResizePanelOpen, "toolbar: Resize button opens the resize panel (does not touch state.Tool)");
        Check(state.Tool == ToolMode.Delete, "toolbar: Resize does not affect the Paint/Delete tool selection");
        await Click(editor, CenterOf(FindButton("Resize")!), MouseButton.Left);
        Check(!state.ResizePanelOpen, "toolbar: pressing Resize again closes the panel");
        state.Tool = ToolMode.None;
        await Frames(editor, 1); // даём контейнеру пересчитать позиции после скрытия панели Resize

        await Click(editor, CenterOf(FindButton("Wireframe")!), MouseButton.Left);
        Check(state.Wireframe, "toolbar: Wireframe button toggles state.Wireframe");
        await Click(editor, CenterOf(FindButton("Borders")!), MouseButton.Left);
        Check(!state.Borders, "toolbar: Borders button toggles state.Borders (starts on, so this turns it off)");
        await Click(editor, CenterOf(FindButton("Wireframe")!), MouseButton.Left);
        await Click(editor, CenterOf(FindButton("Borders")!), MouseButton.Left);
        Check(!state.Wireframe && state.Borders, "toolbar: Wireframe/Borders are independent toggles, not a shared cycle");

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

        // 5. Resize настраивает ПРИЗРАК (EditorState.PendingSize), а не уже поставленные блоки: панель на тулбаре
        // всегда активна (не нужна цель/ПКМ), ЛКМ ставит блок сразу такого размера. Поле принимает только целые
        // положительные значения.
        grid.Clear();
        camera.LookAtPoint(new Vector3(1.5f, 2f, 2.5f), new Vector3(0.125f, 0.0f, 0.125f));
        await Frames(editor, 2);
        state.SelectedSlot = 0;

        List<LineEdit> ResizeFields() =>
            editor.GetTree().Root.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Where(f => f.IsVisibleInTree()).ToList();

        Check(state.PendingSize == Vector3I.One, "resize: PendingSize starts at 1x1x1");
        state.ResizePanelOpen = true;
        await Frames(editor, 1);
        var fields = ResizeFields();
        Check(fields.Count == 3 && fields.All(f => f.Editable) && fields[0].Text == "1" && fields[1].Text == "1" && fields[2].Text == "1",
            "resize panel: 3 editable fields, always active (no RMB target needed), show PendingSize",
            string.Join(",", fields.Select(f => f.Text)));

        var plusButtons = FindButtons(editor).Where(b => b.Text == "+").ToList();
        Check(plusButtons.Count == 3, "resize panel: 3 '+' buttons (X/Y/Z)", $"found {plusButtons.Count}");
        await Click(editor, plusButtons[0].GetGlobalRect().GetCenter(), MouseButton.Left);
        Check(state.PendingSize == new Vector3I(2, 1, 1), "resize panel: '+' on X grows PendingSize.X", $"{state.PendingSize}");

        var minusButtons = FindButtons(editor).Where(b => b.Text == "-").ToList();
        await Click(editor, minusButtons[0].GetGlobalRect().GetCenter(), MouseButton.Left);
        Check(state.PendingSize == Vector3I.One, "resize panel: '-' on X shrinks PendingSize.X back down", $"{state.PendingSize}");

        // Прямой ввод в текстовое поле: целое положительное значение применяется, всё остальное откатывается.
        var fieldX = ResizeFields()[0];
        fieldX.Text = "3";
        fieldX.EmitSignal(LineEdit.SignalName.TextSubmitted, "3");
        Check(state.PendingSize == new Vector3I(3, 1, 1), "resize field: typing a positive integer and submitting updates PendingSize", $"{state.PendingSize}");

        fieldX.Text = "0";
        fieldX.EmitSignal(LineEdit.SignalName.TextSubmitted, "0");
        Check(state.PendingSize == new Vector3I(3, 1, 1) && fieldX.Text == "3",
            "resize field: 0 is rejected, the field reverts to the last valid size", $"{state.PendingSize} text={fieldX.Text}");

        fieldX.Text = "-5";
        fieldX.EmitSignal(LineEdit.SignalName.TextChanged, "-5");
        Check(fieldX.Text == "5", "resize field: '-' is not a digit, filtered out as it's typed (values are never negative)", $"text={fieldX.Text}");
        fieldX.Text = "3";
        fieldX.EmitSignal(LineEdit.SignalName.TextSubmitted, "3"); // возвращаем валидный текст, PendingSize уже 3x1x1

        state.ResizePanelOpen = false;
        await Frames(editor, 1);
        Check(ResizeFields().Count == 0, "resize panel: hides once closed");

        // ЛКМ ставит блок СРАЗУ размером PendingSize (3x1x1, а не 1x1x1 + отдельный шаг растягивания).
        await Click(editor, Screen(new Vector3(0.125f, 0f, 0.125f)), MouseButton.Left);
        var placedAt3x1x1 = editor.World.Construction.GetOwner(new Vector3I(0, 0, 0));
        Check(placedAt3x1x1 != null && placedAt3x1x1.Size == new Vector3I(3, 1, 1) && grid.IsSolid(new Vector3I(2, 0, 0)),
            "LMB places a block directly at PendingSize - no separate resize-after-placing step needed", $"size={placedAt3x1x1?.Size}");

        // Дальнейшие правки PendingSize НЕ меняют уже поставленный блок (это и был баг: Resize раньше действовал
        // на блоки под курсором, а не на призрак следующей установки).
        state.ResizePanelOpen = true;
        await Frames(editor, 1);
        await Click(editor, FindButtons(editor).Where(b => b.Text == "+").ToList()[1].GetGlobalRect().GetCenter(), MouseButton.Left);
        Check(state.PendingSize == new Vector3I(3, 2, 1) && placedAt3x1x1!.Size == new Vector3I(3, 1, 1),
            "resize: growing PendingSize.Y afterwards does not resize the block already placed", $"pending={state.PendingSize} placed={placedAt3x1x1!.Size}");

        state.SetPendingSizeAxis(0, 1);
        state.SetPendingSizeAxis(1, 1);
        state.ResizePanelOpen = false;
        // Construction.Clear(), не голый grid.Clear(): последний чистит только клетки, а не владение Construction —
        // оставшаяся запись "клетка (1,0,0)/(2,0,0) принадлежит этому 3x1x1 экземпляру" пережила бы очистку сетки
        // и позже, при удалении инструментом Delete чего-то совсем другого на этих же координатах, задела бы их тоже.
        editor.World.Construction.Clear();

        // Вращение перед установкой (J/K/L): меняет ориентацию следующего блока, вращение сохраняется в поставленном
        // экземпляре. 0=block, 1=inverse_pyramid, 2=pyramid, 3=wedge — алфавитный порядок каталога (см. блочные тесты).
        state.SelectedSlot = 3;
        Check(state.SelectedBlockSlug == "wedge", "rotation setup: hotbar slot 3 is 'wedge'", state.SelectedBlockSlug);
        await PressKey(editor, Godot.Key.J);
        await PressKey(editor, Godot.Key.K);
        await PressKey(editor, Godot.Key.K);
        await PressKey(editor, Godot.Key.L);
        Check(state.PendingRotationSteps == new Vector3I(1, 2, 1), "J/K/L real key presses rotate the pending placement orientation around X/Y/Z");

        await Click(editor, ground, MouseButton.Left);
        var rotatedInstance = editor.World.Construction.GetOwner(new Vector3I(0, 0, 0));
        Check(rotatedInstance != null && rotatedInstance.RotationSteps == new Vector3I(1, 2, 1),
            "the placed block bakes in the pending rotation steps");
        editor.World.Construction.Clear();

        // Отражение (U/I/O): переключает EditorState.PendingMirror для СЛЕДУЮЩЕГО ставящегося блока/призрака — как
        // и вращение (J/K/L) выше, а не двигает уже поставленный блок под курсором. Раньше (баг) эти клавиши двигали
        // наведённый блок на +1 клетку вместо отражения вершин — фикс проверяется явно ниже.
        state.SelectedSlot = 3; // wedge
        Check(state.PendingMirror == Vector3I.Zero, "mirror: PendingMirror starts at (0,0,0)");

        await PressKey(editor, Godot.Key.U);
        Check(state.PendingMirror == new Vector3I(1, 0, 0), "U toggles mirror around X");
        await PressKey(editor, Godot.Key.I);
        Check(state.PendingMirror == new Vector3I(1, 1, 0), "I toggles mirror around Y");
        await PressKey(editor, Godot.Key.O);
        Check(state.PendingMirror == new Vector3I(1, 1, 1), "O toggles mirror around Z");
        await PressKey(editor, Godot.Key.U);
        Check(state.PendingMirror == new Vector3I(0, 1, 1), "pressing U again toggles X back off");

        await Click(editor, ground, MouseButton.Left);
        var mirroredInstance = editor.World.Construction.GetOwner(new Vector3I(0, 0, 0));
        Check(mirroredInstance != null && mirroredInstance.Mirror == new Vector3I(0, 1, 1),
            "the placed block bakes in the pending mirror flags", $"mirror={mirroredInstance?.Mirror}");

        // U/I/O не двигают и не меняют уже поставленный блок (это и была ошибка в прежней версии).
        var originBeforeMirrorPress = mirroredInstance!.Origin;
        var mirrorBeforeMirrorPress = mirroredInstance!.Mirror;
        await Move(editor, Screen(TopOf(new Vector3I(0, 0, 0))));
        await PressKey(editor, Godot.Key.U);
        Check(mirroredInstance!.Origin == originBeforeMirrorPress && mirroredInstance!.Mirror == mirrorBeforeMirrorPress,
            "U/I/O never move or reflect an already-placed block, even while hovering it - only the next placement's ghost");

        if (state.PendingMirror.X != 0) state.ToggleMirrorX();
        if (state.PendingMirror.Y != 0) state.ToggleMirrorY();
        if (state.PendingMirror.Z != 0) state.ToggleMirrorZ();
        while (state.PendingRotationSteps.X != 0) state.RotatePendingX();
        while (state.PendingRotationSteps.Y != 0) state.RotatePendingY();
        while (state.PendingRotationSteps.Z != 0) state.RotatePendingZ();
        editor.World.Construction.Clear();

        // --- баг: призрак Wedge/Pyramid/InvertedPyramid не показывал свои FullCoverage-стороны (у Wedge - низ и
        // заднюю стенку) - у настоящего поставленного блока их дорисовывает ChunkMesher по данным VoxelGrid, но
        // призрак никогда в неё не попадает (это только превью), поэтому без явного флага
        // ShapeMeshBuilder.BuildData.includeFullCoverageFaces у него был виден только "дырявый" силуэт (рампа + 2
        // треугольных борта - 4 треугольника вместо 8).
        state.SelectedSlot = 3; // wedge
        await Move(editor, ground);
        Check(editor.Ghost.Visible, "wedge ghost setup: placement ghost is visible over empty ground");
        int ghostTriangles = editor.Ghost.Mesh.GetFaces().Length / 3;
        Check(ghostTriangles == 8,
            "wedge ghost includes its 2 FullCoverage sides (bottom+back, 2 quads = 4 triangles) in addition to the " +
            "4 partial ones (ramp + 2 triangular borts) - not just the partial ones, so its silhouette is solid",
            $"got {ghostTriangles} triangles");

        // --- баг (исправлен): покраска стороны формы, которую она НЕ закрывает целиком (Wedge закрывает только
        // низ/заднюю стенку - "верх", Y+, никогда не FullCoverage), раньше перекрашивала ВЕСЬ экземпляр целиком
        // ("красятся все подобные поверхности, а не конкретно выбранная") - теперь красит РОВНО ту грань формы,
        // в которую попал луч (см. ShapeMeshBuilder.TryFindPaintRegion/Construction.PaintRegion), а не всё сразу.
        var wedgeCell = new Vector3I(0, 0, 0);
        var wedgeInstance = editor.World.Construction.Place(wedgeCell, BlockCatalog.Instance.Get("wedge"), Colors.Gray);
        camera.LookAtPoint(new Vector3(0.125f, 5f, 0.125f), new Vector3(0.125f, 0f, 0.125f)); // прямо вниз на клетку
        await Frames(editor, 2);
        var wedgeTop = Screen(new Vector3(0.125f, 0.25f, 0.125f));
        await Move(editor, wedgeTop);
        Check(editor.Hover.IsBlock && editor.Hover.BlockCell == wedgeCell && editor.Hover.Normal == Vector3I.Up,
            "paint-region setup: looking straight down hits the Wedge's Y+ bounding-cube side (not one of its 2 " +
            "FullCoverage sides)", $"{editor.Hover}");

        state.Tool = ToolMode.Paint;
        state.PaintColor = Colors.Green;
        await Click(editor, wedgeTop, MouseButton.Left);
        Check(wedgeInstance!.Color == CellColor.Pack(Colors.Gray),
            "painting the Wedge's Y+ side (no face of its own there - the ramp is what's actually visible) leaves " +
            "the instance's own representative Color untouched",
            $"color={Convert.ToString(wedgeInstance!.Color, 16)}");
        Check(wedgeInstance.RegionColors != null && wedgeInstance.RegionColors.TryGetValue(2, out uint rampColor) && rampColor == CellColor.Pack(Colors.Green),
            "...instead it paints exactly the ramp (face index 2), the only geometry actually visible from that side");
        Check(grid.GetFaceColor(wedgeCell, 2, false) != CellColor.Pack(Colors.Green),
            "the FullCoverage back face (Z-) is untouched by painting the ramp - no more \"paints every similar surface\"");

        // Тот же экземпляр, но клик по левому борту (X-, своя осеориентированная грань формы, не откат на рампу) -
        // другой цвет, другой индекс: оба точечных цвета должны сосуществовать, не перезаписывая друг друга.
        camera.LookAtPoint(new Vector3(-5f, 0.125f, 0.125f), new Vector3(0.125f, 0.125f, 0.125f)); // смотрим вдоль +X
        await Frames(editor, 2);
        var wedgeLeft = Screen(new Vector3(0f, 0.125f, 0.125f));
        await Move(editor, wedgeLeft);
        Check(editor.Hover.IsBlock && editor.Hover.BlockCell == wedgeCell && editor.Hover.Normal == Vector3I.Left,
            "paint-region setup: looking along +X hits the Wedge's X- bounding-cube side (its own left triangular bort)",
            $"{editor.Hover}");

        state.PaintColor = Colors.Blue;
        await Click(editor, wedgeLeft, MouseButton.Left);
        Check(wedgeInstance!.RegionColors!.TryGetValue(3, out uint bortColor) && bortColor == CellColor.Pack(Colors.Blue),
            "painting the X- side (its own left bort, face index 3) paints exactly that region");
        Check(wedgeInstance.RegionColors.TryGetValue(2, out uint rampStillGreen) && rampStillGreen == CellColor.Pack(Colors.Green),
            "...without touching the ramp painted a moment ago (both region overrides coexist side by side)");
        Check(wedgeInstance.Color == CellColor.Pack(Colors.Gray), "...and still without touching the instance's representative Color");

        state.Tool = ToolMode.None;
        editor.World.Construction.Clear();
        camera.LookAtPoint(new Vector3(1.5f, 2f, 2.5f), new Vector3(0.125f, 0.0f, 0.125f));
        await Frames(editor, 2);
        state.SelectedSlot = 0;

        // Перетаскивание инструмента с зажатой ЛКМ: три блока в ряд, «проедания насквозь» без движения мыши нет.
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
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = p0, GlobalPosition = p0 });
        await Frames(editor, 1);
        await Move(editor, p1);
        await Move(editor, p2);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = p2, GlobalPosition = p2 });
        await Frames(editor, 2);
        uint blue = CellColor.Pack(Colors.Blue);
        // TopOf наводит на верхнюю (Y+) грань каждого блока - по грани красится именно она.
        Check(grid.GetFaceColor(new Vector3I(0, 0, 0), 1, true) == blue && grid.GetFaceColor(new Vector3I(1, 0, 0), 1, true) == blue
              && grid.GetFaceColor(new Vector3I(2, 0, 0), 1, true) == blue,
            "holding LMB with Paint active drags the paint across every block passed over");
        Check(grid.GetFaceColor(new Vector3I(1, 0, 1), 1, true) != blue, "drag painting does not touch blocks that were not under the cursor");

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

        // Ctrl+Z/Ctrl+Y через реальные события клавиатуры. Полная очистка: Construction.Clear() (Undo снэпшотит
        // именно Construction, см. ниже) И grid.Clear() (предыдущий тест выше заливал часть блоков напрямую в
        // VoxelGrid в обход Construction, см. DemoBuilds.Fill — те клетки Construction.Clear() не видит).
        editor.World.Construction.Clear();
        grid.Clear();
        state.SelectedSlot = 0;
        await Click(editor, ground, MouseButton.Left);
        Check(grid.BlockCount == 1, "undo setup: LMB placed one block");

        Send(new InputEventKey { PhysicalKeycode = Godot.Key.Z, Keycode = Godot.Key.Z, CtrlPressed = true, Pressed = true });
        await Frames(editor, 2);
        Check(grid.BlockCount == 0, "Ctrl+Z undoes the last placement");

        Send(new InputEventKey { PhysicalKeycode = Godot.Key.Y, Keycode = Godot.Key.Y, CtrlPressed = true, Pressed = true });
        await Frames(editor, 2);
        Check(grid.BlockCount == 1, "Ctrl+Y redoes it");

        // Ctrl+Z не должен перехватываться, если фокус на текстовом поле (иначе конфликтовал бы с правкой текста
        // в панели Resize вместо отмены последней постройки).
        state.ResizePanelOpen = true;
        await Frames(editor, 1);
        var resizeField = editor.GetTree().Root.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().First(f => f.IsVisibleInTree());
        resizeField.GrabFocus();
        await Frames(editor, 1);
        Send(new InputEventKey { PhysicalKeycode = Godot.Key.Z, Keycode = Godot.Key.Z, CtrlPressed = true, Pressed = true });
        await Frames(editor, 2);
        Check(grid.BlockCount == 1, "Ctrl+Z is ignored while a text field has focus (does not undo the placement)");
        resizeField.ReleaseFocus();
        state.ResizePanelOpen = false;

        // Перетаскивание Paint/Delete отменяется ОДНИМ шагом Ctrl+Z, а не по клетке. Undo снэпшотит Construction
        // (см. UndoHistory), поэтому блоки для этой проверки нужно ставить по-настоящему (ЛКМ), а не заливкой
        // DemoBuilds.Fill в обход Construction, как соседние тесты перетаскивания выше — иначе отменять нечего:
        // Construction их вообще не видит.
        editor.World.Construction.Clear();
        grid.Clear();
        camera.LookAtPoint(new Vector3(0.4f, 2.5f, 2.0f), new Vector3(0.4f, 0.0f, 0.2f));
        await Frames(editor, 2);
        var u0 = Screen(TopOf(new Vector3I(0, 0, 0)));
        var u1 = Screen(TopOf(new Vector3I(1, 0, 0)));
        var u2 = Screen(TopOf(new Vector3I(2, 0, 0)));
        await Click(editor, u0, MouseButton.Left);
        await Click(editor, u1, MouseButton.Left);
        await Click(editor, u2, MouseButton.Left);
        Check(grid.BlockCount == 3, "undo setup: LMB placed 3 real (Construction-owned) blocks in a row", $"blocks={grid.BlockCount}");

        state.Tool = ToolMode.Delete;
        await Move(editor, u0);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = u0, GlobalPosition = u0 });
        await Frames(editor, 1);
        await Move(editor, u1);
        await Move(editor, u2);
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = u2, GlobalPosition = u2 });
        await Frames(editor, 2);
        Check(grid.BlockCount == 0, "undo setup: LMB drag deleted all 3 blocks");

        Send(new InputEventKey { PhysicalKeycode = Godot.Key.Z, Keycode = Godot.Key.Z, CtrlPressed = true, Pressed = true });
        await Frames(editor, 2);
        Check(grid.BlockCount == 3, "Ctrl+Z undoes a whole LMB (Delete) drag stroke in one step, not per cell", $"blocks={grid.BlockCount}");
        state.Tool = ToolMode.None;
        editor.World.Construction.Clear();

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
