using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Runtime;
using SandboxPolyGame.World;

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
    /// Любой id, заведомо не совпадающий ни с одним реальным блоком каталога (сейчас их всего 10) — используется
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
        test.RunFunctionalBlockPendingSizeTests();
        test.RunFunctionalBlockGeometryTests();
        test.RunNodeAndBehaviorTests();
        test.RunFootprintRotationTests();
        test.RunRotatedResizableBlockTests(editor);
        test.RunPaintRegionTests();
        test.RunConstructionTests();
        test.RunUndoHistoryTests();
        await test.RunNetworkingTests(editor);
        await test.RunPlayerAndWorldTests(editor);
        await test.RunEditorTests(editor);
        await test.RunPlacementConflictTests(editor);
        await test.RunBlockPrefabEditorTests(editor);
        test.RunVehicleSpawnerCollisionTests(editor);
        test.RunFunctionalRuntimeSpawnTest(editor);
        test.RunButtonVisualTests(editor);
        // Обязательно ПОСЛЕДним - реально трогает SceneTree.Multiplayer (Core.NetHub.Host), см. class doc про то,
        // что "отключение" оставляет NetHub.LocalPeerId нерабочим до конца процесса (не наша логика - особенность
        // движка, см. RunHostedWorldPlayerTest) - ни один тест после него не должен полагаться на Multiplayer.
        await test.RunHostedWorldPlayerTest(editor);

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
        Check(catalog.All.Count >= 4,
            "catalog loaded the block definitions from blocks/ (at least the 4 shape blocks; the functional blocks are user-editable data, so no exact count is pinned)",
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

        RunFunctionalBlockCatalogTests(catalog);
    }

    /// <summary>Функциональные блоки (мотор/батарея/бак/труба/кабель/вал, см. Docs/05-world-and-vehicle-systems.md,
    /// «Функциональные блоки») — расширение того же data-driven каталога через <see cref="FunctionalBlockComponent"/>,
    /// взаимоисключающий с <see cref="BuildingBlockComponent"/> (ни у одного из этих блоков его нет).</summary>
    private void RunFunctionalBlockCatalogTests(BlockCatalog catalog)
    {
        GD.Print("-- block catalog: functional blocks (FunctionalBlockComponent, ResourceType/ResourcePort)");

        // Состав функциональных блоков - пользовательские данные (их правят через --blockeditor, переименовывают, удаляют), поэтому
        // проверяются ИНВАРИАНТЫ каждого функционального блока настоящего каталога, а не конкретные слаги/числа.
        var functionalBlocks = catalog.All.Where(d => d.HasComponent<FunctionalBlockComponent>()).ToList();
        Check(functionalBlocks.Count > 0, "the catalog has functional blocks", $"{functionalBlocks.Count}");
        foreach (var definition in functionalBlocks)
        {
            var fn = FunctionalOf(definition)!;
            Check(definition.GetComponent<BuildingBlockComponent>() == null, $"'{definition.Slug}' is a FunctionalBlock without BuildingBlock (not resizable)");
            Check(fn.Footprint.X >= 1 && fn.Footprint.Y >= 1 && fn.Footprint.Z >= 1, $"'{definition.Slug}': footprint is at least 1x1x1", $"{fn.Footprint}");
            Check(FunctionalBlockComponent.FindNodeConflict(fn.Nodes) == null, $"'{definition.Slug}': no two nodes of the same type share a cell");
            Check(fn.Ports.All(p => p.Resource is ResourceType.Fluid or ResourceType.Torque), $"'{definition.Slug}': physical ports are only Fluid/Torque");
        }

        Check(!Enum.GetNames<ResourceType>().Contains("Electricity"), "ResourceType has no Electricity - only physical resources (Fluid, Torque) remain");
        // Блок "cable" удалён (2026-10-04): электричество идёт нодами логики, не физическими кабелями-блоками.
        Check(!catalog.TryGetBySlug("cable", out _), "'cable' is gone from the catalog (electricity goes through logic nodes, not blocks)");
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

        // Размер формы - ЛОКАЛЬНЫЙ (2026-10-05): блок поворачивается ВМЕСТЕ со своим размером как жёсткое тело (см. BlockFootprint), а не
        // растягивается заново в осях мира. Меш повёрнутого несимметричного клина (3x1x1) должен ровно укладываться в повёрнутый занятый
        // бокс (BlockFootprint.RotatedSize: 3x1x1 -> 1x1x3 при повороте на 90 вокруг Y) И быть именно повёрнутой копией неповёрнутого меша.
        var asymmetricSize = new Vector3I(3, 1, 1);
        var unrotatedMesh = ShapeMeshBuilder.BuildData(BlockShape.Slope, asymmetricSize, Vector3I.Zero, Vector3I.Zero, Colors.White)!;
        var localCenter = new Vector3(asymmetricSize.X, asymmetricSize.Y, asymmetricSize.Z) * BuildSpace.CellSize * 0.5f;
        foreach (var rot in new[] { Vector3I.Zero, new Vector3I(0, 1, 0), new Vector3I(0, 2, 0), new Vector3I(0, 3, 0), new Vector3I(1, 0, 0), new Vector3I(0, 0, 1) })
        {
            var rotatedSize = BlockFootprint.RotatedSize(asymmetricSize, rot);
            var expectedExtent = new Vector3(rotatedSize.X, rotatedSize.Y, rotatedSize.Z) * BuildSpace.CellSize;
            var rotatedResized = ShapeMeshBuilder.BuildData(BlockShape.Slope, asymmetricSize, rot, Vector3I.Zero, Colors.White)!;
            Vector3 min = rotatedResized.Vertices[0], max = rotatedResized.Vertices[0];
            foreach (var v in rotatedResized.Vertices)
            {
                min = new Vector3(Mathf.Min(min.X, v.X), Mathf.Min(min.Y, v.Y), Mathf.Min(min.Z, v.Z));
                max = new Vector3(Mathf.Max(max.X, v.X), Mathf.Max(max.Y, v.Y), Mathf.Max(max.Z, v.Z));
            }

            bool fitsFootprint = min.DistanceTo(Vector3.Zero) < 1e-4f && max.DistanceTo(expectedExtent) < 1e-4f;
            Check(fitsFootprint, $"wedge 3x1x1 rotated ({rot.X},{rot.Y},{rot.Z}) bounds exactly to its ROTATED occupied box (0..{expectedExtent})",
                $"min={min} max={max}");

            // Жёсткость: каждая вершина повёрнутого меша, развёрнутая обратно, совпадает с вершиной неповёрнутого.
            var basis = ShapeMeshBuilder.ComposeRotation(rot);
            bool rigid = true;
            foreach (var v in rotatedResized.Vertices)
            {
                var back = basis.Inverse() * (v - expectedExtent * 0.5f) + localCenter;
                bool known = false;
                foreach (var u in unrotatedMesh.Vertices) { if (u.DistanceTo(back) < 1e-4f) { known = true; break; } }
                rigid &= known;
            }

            Check(rigid, $"...and it is a rigid rotation of the unrotated 3x1x1 wedge (the size turns with the block, not re-stretched in world axes)");
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

        // --- баг, найденный пользователем (2026-10-01 (8)): оси вращения должны быть ГЛОБАЛЬНЫМИ (мировыми,
        // фиксированными), а не локальными (вокруг уже повёрнутой оси блока). Раньше ориентация пересобиралась
        // заново из трёх НЕЗАВИСИМЫХ счётчиков в ФИКСИРОВАННОМ порядке X→Y→Z при КАЖДОМ нажатии - пока оси
        // нажимались строго по одной или в порядке X,Y,Z, результат случайно совпадал с ожидаемым (см. тесты выше),
        // но при ЧЕРЕДОВАНИИ (например, сначала Y, потом X) пересборка в фиксированном порядке давала СОВСЕМ другую
        // ориентацию, чем "доверни ещё на 90° вокруг МИРОВОЙ X от того, что уже есть" - выглядело как будто кнопки
        // вращения "меняются местами". Проверяем это напрямую: поворот вокруг X ПОСЛЕ поворота вокруг Y обязан
        // совпадать с ПРЕДУМНОЖЕНИЕМ на фиксированный поворот вокруг мировой X (глобальная композиция), а не с
        // послеумножением (которое было бы вращением вокруг уже повёрнутой ЛОКАЛЬНОЙ оси блока).
        var globalAxisState = new EditorState();
        globalAxisState.RotatePendingY();
        var basisAfterY = globalAxisState.PendingRotationBasis;
        globalAxisState.RotatePendingX();
        var expectedGlobalX = new Basis(Vector3.Right, Mathf.Pi / 2f) * basisAfterY;
        var unexpectedLocalX = basisAfterY * new Basis(Vector3.Right, Mathf.Pi / 2f);
        Check(globalAxisState.PendingRotationBasis.IsEqualApprox(expectedGlobalX),
            "rotating around X after already rotating around Y composes GLOBALLY (pre-multiplied by a fixed-axis " +
            "rotation) - not around the block's own already-rotated local X axis",
            $"got={globalAxisState.PendingRotationBasis} expectedGlobal={expectedGlobalX} (local would be {unexpectedLocalX})");
    }

    // ================================================================== функциональные блоки: фиксированный footprint, Resize недоступен

    /// <summary>
    /// Блок с <see cref="FunctionalBlockComponent"/> (мотор/батарея/бак/труба/кабель/вал) не резинится - в отличие
    /// от <see cref="BuildingBlockComponent"/>, у него нет MinSize/MaxSize, он всегда ставится ровно своим
    /// <see cref="FunctionalBlockComponent.Footprint"/>, и Resize-панель на тулбаре для него не действует ни на
    /// одну ось (см. <see cref="EditorState"/> - приватный ClampToSelectedBlock).
    /// </summary>
    private void RunFunctionalBlockPendingSizeTests()
    {
        GD.Print("-- functional blocks: PendingSize is pinned to the block's fixed Footprint, Resize has no effect");

        var state = new EditorState();
        Check(state.SelectedBlockSlug == "block", "setup: default hotbar slot 0 is 'block' (shape blocks sort before functional ones)", state.SelectedBlockSlug);

        state.SelectedSlot = 0;
        state.AdjustPendingSize(0, 4);
        Check(state.PendingSize == new Vector3I(5, 1, 1), "setup: growing a resizable block's PendingSize still works as before");

        var fixedBlock = FindFunctional();
        if (fixedBlock == null)
        {
            Check(false, "setup: the catalog has a functional block");
            return;
        }

        string fixedSlug = fixedBlock.Slug;
        var fixedFootprint = FunctionalOf(fixedBlock)!.Footprint; // у блока может быть любой footprint - его правят в --blockeditor

        state.SetSlot(1, fixedSlug);
        state.SelectedSlot = 1;
        Check(state.PendingSize == fixedFootprint,
            "selecting a functional block (fixed Footprint) immediately resets PendingSize to its Footprint", $"{state.PendingSize} vs {fixedFootprint}");

        state.AdjustPendingSize(0, 7);
        Check(state.PendingSize == fixedFootprint,
            "Resize has no effect on a functional block - PendingSize stays pinned to Footprint", $"{state.PendingSize}");

        // PendingSize - одно общее значение на весь редактор, не память на слот (см. class doc EditorState.PendingSize) -
        // переключение на функциональный блок уже перезаписало его на его Footprint, поэтому рост отсюда начинается заново.
        state.SelectedSlot = 0;
        state.AdjustPendingSize(1, 3);
        Check(state.PendingSize == fixedFootprint + new Vector3I(0, 3, 0), "setup: back on slot 0 ('block'), PendingSize resizes normally again", $"{state.PendingSize}");

        state.SetSlot(0, fixedSlug);
        Check(state.PendingSize == fixedFootprint,
            "SetSlot on the CURRENTLY selected slot re-resolves PendingSize immediately, not just on the next SelectedSlot change", $"{state.PendingSize}");
    }

    // ================================================================== функциональные блоки: подгонка настоящей модели под размер клетки

    /// <summary>
    /// <see cref="FunctionalBlockGeometry"/> — общая геометрия для <c>Editor.FunctionalBlockView</c> (размещённые
    /// мотор/вал в мире) и <c>Editor.Ui.BlockIconView</c> (их иконка в хотбаре). Чистая математика, без загрузки
    /// настоящих .glb — <see cref="FunctionalBlockGeometry.ComputeLocalAabb"/> проверяется на синтетическом дереве
    /// узлов (вложенный <see cref="MeshInstance3D"/> со своим трансформом), <see cref="FunctionalBlockGeometry.ComputeFitTransform"/> —
    /// на синтетическом <see cref="Aabb"/>.
    /// </summary>
    private void RunFunctionalBlockGeometryTests()
    {
        GD.Print("-- functional block geometry: ComputeLocalAabb (nested transforms) + ComputeFitTransform (uniform scale/center/rotate)");

        // ComputeLocalAabb: дочерний Node3D сдвинут на (1,0,0), внутри него MeshInstance3D с BoxMesh 2x2x2 (центрирован
        // на СВОЁМ происхождении, как и положено BoxMesh) - итоговый AABB в пространстве root должен учитывать сдвиг
        // ребёнка, а не просто вернуть AABB меша как есть.
        var root = new Node3D();
        var child = new Node3D { Transform = new Transform3D(Basis.Identity, new Vector3(1, 0, 0)) };
        child.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2, 2, 2) } });
        root.AddChild(child);

        var aabb = FunctionalBlockGeometry.ComputeLocalAabb(root);
        Check(aabb.Position.IsEqualApprox(new Vector3(0, -1, -1)) && aabb.Size.IsEqualApprox(new Vector3(2, 2, 2)),
            "ComputeLocalAabb: combines a nested MeshInstance3D's mesh AABB with its ancestor's own transform",
            $"pos={aabb.Position} size={aabb.Size}");
        root.Free();

        // ComputeFitTransform: внецентренный AABB модели 2x4x2 (центр в (2,3,2)), вписываем в клетку 0.25^3 с
        // центром в (0.5,0.5,0.5).
        var modelAabb = new Aabb(new Vector3(1, 1, 1), new Vector3(2, 4, 2)); // охватывает (1,1,1)..(3,5,3)
        var targetExtent = new Vector3(0.25f, 0.25f, 0.25f);
        var targetCenter = new Vector3(0.5f, 0.5f, 0.5f);
        var transform = FunctionalBlockGeometry.ComputeFitTransform(modelAabb, targetExtent, targetCenter, Vector3I.Zero, Vector3.One);

        var mappedCenter = transform * new Vector3(2, 3, 2);
        Check(mappedCenter.IsEqualApprox(targetCenter),
            "ComputeFitTransform: the model's own AABB center maps exactly onto the target center", $"{mappedCenter}");

        // Масштаб РАВНОМЕРНЫЙ (минимум из трёх осей: 0.25/2, 0.25/4, 0.25/2 = 0.0625, выигрывает Y) - X/Z тоже
        // используют 0.0625, хотя сами по себе влезли бы с 0.125 - пропорции модели не искажаются.
        var mappedMinCorner = transform * new Vector3(1, 1, 1);
        var expectedMinCorner = targetCenter + new Vector3(-1, -2, -1) * 0.0625f;
        Check(mappedMinCorner.IsEqualApprox(expectedMinCorner),
            "ComputeFitTransform: scale is uniform (the tightest axis wins) - proportions are preserved, not stretched per-axis",
            $"got={mappedMinCorner} expected={expectedMinCorner}");

        // Поворот крутится вокруг ЦЕНТРА модели (та же конвенция, что и ShapeMeshBuilder.ComposeRotation) - центр
        // по-прежнему должен лечь точно в targetCenter, какой бы ни была сама матрица поворота.
        var rotatedTransform = FunctionalBlockGeometry.ComputeFitTransform(modelAabb, targetExtent, targetCenter, new Vector3I(0, 1, 0), Vector3.One);
        var rotatedCenter = rotatedTransform * new Vector3(2, 3, 2);
        Check(rotatedCenter.IsEqualApprox(targetCenter),
            "ComputeFitTransform: rotation pivots around the model's own center - still maps exactly onto the target center", $"{rotatedCenter}");

        // extraScale (Blocks.FunctionalBlockComponent.ModelScale, "Model stretch" в --blockeditor) - поправка
        // ПОВЕРХ равномерного масштаба, покомпонентно: (1,1,1) не меняет ничего (уже проверено выше), а, например,
        // (2,1,1) должен ровно удвоить расстояние от центра вдоль X и оставить Y/Z как есть.
        var stretchedTransform = FunctionalBlockGeometry.ComputeFitTransform(modelAabb, targetExtent, targetCenter, Vector3I.Zero, new Vector3(2f, 1f, 1f));
        var stretchedMinCorner = stretchedTransform * new Vector3(1, 1, 1);
        var expectedStretchedMinCorner = targetCenter + new Vector3(-1 * 2f, -2, -1) * 0.0625f;
        Check(stretchedMinCorner.IsEqualApprox(expectedStretchedMinCorner),
            "ComputeFitTransform: extraScale stretches a single axis on top of the uniform fit, independently of the others",
            $"got={stretchedMinCorner} expected={expectedStretchedMinCorner}");

        // modelOffset (Blocks.FunctionalBlockComponent.ModelOffset, "Model offset" в --blockeditor) - сдвиг в МЕТРАХ в
        // осях неповёрнутого блока: центр модели уходит ровно на offset от центра цели, не зависит от масштаба, а при
        // повороте блока на 90° вокруг Y поворачивается вместе с ним (+X -> -Z).
        var offset = new Vector3(0.1f, 0f, 0f);
        var offsetTransform = FunctionalBlockGeometry.ComputeFitTransform(modelAabb, targetExtent, targetCenter, Vector3I.Zero, Vector3.One, offset);
        var offsetCenter = offsetTransform * new Vector3(2, 3, 2);
        Check(offsetCenter.IsEqualApprox(targetCenter + offset),
            "ComputeFitTransform: modelOffset shifts the model center by exactly that many meters", $"{offsetCenter}");
        var rotatedOffsetCenter = FunctionalBlockGeometry.ComputeFitTransform(modelAabb, targetExtent, targetCenter, new Vector3I(0, 1, 0), Vector3.One, offset) * new Vector3(2, 3, 2);
        var expectedRotatedOffset = targetCenter + ShapeMeshBuilder.ComposeRotation(new Vector3I(0, 1, 0)) * offset;
        Check(rotatedOffsetCenter.IsEqualApprox(expectedRotatedOffset),
            "ComputeFitTransform: modelOffset rotates together with the block", $"got={rotatedOffsetCenter} expected={expectedRotatedOffset}");

        RunResourcePortGeometryTests();
        RunHotModelLoadTest();
    }

    /// <summary>
    /// <see cref="FunctionalBlockGeometry.GetOrLoadScene"/> должен загрузить модель даже БЕЗ готового кэша импорта
    /// (<c>res://.godot/imported/*.scn</c>) — именно это и даёт "горячую" загрузку сразу по выбору файла в
    /// <c>Dev.BlockPrefabEditorUi.BrowseForModel</c> (по запросу пользователя), без ожидания `--import`/перезапуска.
    /// Копируем РЕАЛЬНЫЙ `.glb` под заведомо НОВЫМ именем (ни разу не виденным движком, значит у него точно нет
    /// `.import`-кэша) и проверяем, что <c>GD.Load</c> внутри промахнётся, а запасной путь через
    /// <see cref="GltfDocument"/> (см. <see cref="FunctionalBlockGeometry"/> doc) всё равно вернёт настоящую модель
    /// с ПРАВДОПОДОБНЫМ bounding box'ом (не единичный куб-заглушка на пустую/битую сцену).
    /// </summary>
    private void RunHotModelLoadTest()
    {
        GD.Print("-- hot model loading: GetOrLoadScene reads a never-imported .glb directly via GltfDocument");

        const string tempPath = "res://meshes/selftest_hotload_tmp.glb";
        if (FileAccess.FileExists(tempPath)) DirAccess.RemoveAbsolute(tempPath);
        var copyErr = DirAccess.CopyAbsolute("res://meshes/motor_small.glb", tempPath);
        Check(copyErr == Error.Ok, "setup: copied motor_small.glb under a brand-new filename (no .import cache possible for it yet)", $"{copyErr}");

        var (scene, aabb) = FunctionalBlockGeometry.GetOrLoadScene(tempPath);
        Check(scene != null, "a never-imported .glb still loads (GltfDocument fallback, not just GD.Load)");
        Check(aabb.Size.Length() > 0.5f, "...with a real bounding box from its actual geometry, not the empty-scene unit-cube fallback", $"{aabb.Size}");

        if (FileAccess.FileExists(tempPath)) DirAccess.RemoveAbsolute(tempPath);
    }

    /// <summary>
    /// <see cref="FunctionalBlockGeometry.ComputePortAnchor"/>/<see cref="FunctionalBlockGeometry.FaceDimensions"/> —
    /// чистая математика размещения порта на стороне footprint'а, см. <see cref="ResourcePort"/> class doc: footprint
    /// НЕ ограничен 1x1x1 (проверяем явно на 2x3x1), порт может сидеть на любой из 6 сторон в любой клетке этой
    /// стороны, и несколько портов МОГУТ делить одну и ту же (Face, FaceCell) без ошибок/дедупликации.
    /// </summary>
    private void RunResourcePortGeometryTests()
    {
        GD.Print("-- resource port placement: ComputePortAnchor/FaceDimensions for non-cubic footprints");

        var footprint = new Vector3I(2, 3, 1); // не кубический - X и Y разного размера, Z = 1 (плоский)
        const float cell = BuildSpace.CellSize;

        // FaceDimensions: ±X используют (Y,Z) footprint'а, ±Y используют (X,Z), ±Z используют (X,Y).
        Check(FunctionalBlockGeometry.FaceDimensions(BlockFace.PosX, footprint) == (3, 1), "FaceDimensions: ±X face uses (footprint.Y, footprint.Z)");
        Check(FunctionalBlockGeometry.FaceDimensions(BlockFace.PosY, footprint) == (2, 1), "FaceDimensions: ±Y face uses (footprint.X, footprint.Z)");
        Check(FunctionalBlockGeometry.FaceDimensions(BlockFace.PosZ, footprint) == (2, 3), "FaceDimensions: ±Z face uses (footprint.X, footprint.Y)");

        // ComputePortAnchor: клетка (0,0) на PosZ (передняя грань, X/Y-сетка) - центр первой клетки, на самой
        // плоскости z=footprint.Z (в метрах), нормаль наружу (0,0,1).
        var (posZOrigin, posZNormal) = FunctionalBlockGeometry.ComputePortAnchor(BlockFace.PosZ, Vector2I.Zero, footprint, cell);
        Check(posZOrigin.IsEqualApprox(new Vector3(cell * 0.5f, cell * 0.5f, cell * footprint.Z)) && posZNormal.IsEqualApprox(new Vector3(0, 0, 1)),
            "ComputePortAnchor: PosZ face, cell (0,0) sits at the center of the first X/Y cell, on the z=footprint.Z plane, normal (0,0,1)",
            $"pos={posZOrigin} normal={posZNormal}");

        // Последняя клетка на NegX (боковая грань, Y/Z-сетка размером 3x1) - cell (2,0), на плоскости x=0.
        var (negXOrigin, negXNormal) = FunctionalBlockGeometry.ComputePortAnchor(BlockFace.NegX, new Vector2I(2, 0), footprint, cell);
        Check(negXOrigin.IsEqualApprox(new Vector3(0, cell * 2.5f, cell * 0.5f)) && negXNormal.IsEqualApprox(new Vector3(-1, 0, 0)),
            "ComputePortAnchor: NegX face, last Y cell (index 2 of 3) sits on the x=0 plane, normal (-1,0,0)",
            $"pos={negXOrigin} normal={negXNormal}");

        // Клетка за пределами реальной сетки грани (PosY грань тут только 2x1, индекс 5 не существует) клампится в
        // границы, а не кидает исключение/вылетает за пределы footprint'а - позиция, а не сами данные, см. doc.
        var (clampedOrigin, _) = FunctionalBlockGeometry.ComputePortAnchor(BlockFace.PosY, new Vector2I(5, 0), footprint, cell);
        var (lastValidOrigin, _) = FunctionalBlockGeometry.ComputePortAnchor(BlockFace.PosY, new Vector2I(1, 0), footprint, cell);
        Check(clampedOrigin.IsEqualApprox(lastValidOrigin),
            "ComputePortAnchor: an out-of-range face cell clamps to the face's actual last cell instead of extrapolating past it",
            $"clamped={clampedOrigin} lastValid={lastValidOrigin}");

        // Несколько портов делят одну и ту же (Face, FaceCell) - не ошибка, просто одна и та же точка дважды.
        var portA = new ResourcePort { Id = "a", Resource = ResourceType.Torque, Direction = PortDirection.In, Face = BlockFace.PosZ, FaceCell = new Vector2I(1, 1) };
        var portB = new ResourcePort { Id = "b", Resource = ResourceType.Fluid, Direction = PortDirection.Out, Face = BlockFace.PosZ, FaceCell = new Vector2I(1, 1) };
        var (anchorA, _) = FunctionalBlockGeometry.ComputePortAnchor(portA.Face, portA.FaceCell, footprint, cell);
        var (anchorB, _) = FunctionalBlockGeometry.ComputePortAnchor(portB.Face, portB.FaceCell, footprint, cell);
        Check(anchorA.IsEqualApprox(anchorB), "two ports sharing the same (Face, FaceCell) resolve to the exact same point - allowed, not an error");

        // Ноды: ЦЕНТР своей клетки внутри блока (не на грани), клетка вне footprint'а клампится к ближайшей.
        Check(FunctionalBlockGeometry.ComputeNodeAnchor(Vector3I.Zero, footprint, cell).IsEqualApprox(new Vector3(0.5f, 0.5f, 0.5f) * cell),
            "ComputeNodeAnchor: cell (0,0,0) is the CENTER of the first cell (half a cell in from the block's min corner on every axis)");
        Check(FunctionalBlockGeometry.ComputeNodeAnchor(new Vector3I(1, 2, 0), footprint, cell).IsEqualApprox(new Vector3(1.5f, 2.5f, 0.5f) * cell),
            "ComputeNodeAnchor: a node in another cell of a multi-cell footprint sits in the center of THAT cell");
        Check(FunctionalBlockGeometry.ComputeNodeAnchor(new Vector3I(9, -4, 5), footprint, cell).IsEqualApprox(new Vector3(1.5f, 0.5f, 0.5f) * cell),
            "ComputeNodeAnchor: a cell outside the footprint clamps to the nearest real cell (stored data is untouched)");
        var singleCellCenter = FunctionalBlockGeometry.ComputeNodeAnchor(Vector3I.Zero, Vector3I.One, cell);
        Check(singleCellCenter.IsEqualApprox(new Vector3(cell, cell, cell) * 0.5f), "...and in a 1x1x1 block the node is the exact center of the block");
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
        RunConstructionStorageTests(block);
    }

    /// <summary>
    /// Именованные сохранения (<see cref="ConstructionStorage"/>, см. Docs/05-world-and-vehicle-systems.md) — не
    /// произвольный путь через FileDialog, а метаданные (имя/описание/даты) + автоматический путь в настоящей папке
    /// "Документы" ОС. Пишет и удаляет ровно один реальный тестовый файл там же, где будут лежать настоящие
    /// сохранения игрока (тот же путь кода) — имя специально узнаваемое (<c>__selftest_storage__</c>), чтобы не
    /// перепутать с сохранением игрока, и подчищается в конце независимо от результата проверок выше.
    /// </summary>
    private void RunConstructionStorageTests(BlockDefinition block)
    {
        GD.Print("-- construction storage: named saves with metadata (name/description/dates), auto-managed paths");

        Check(ConstructionStorage.SanitizeFileName("My Boat!") == "My Boat",
            "SanitizeFileName keeps letters/digits/space/-/_, strips characters unsafe for a file name");
        Check(ConstructionStorage.SanitizeFileName("???") == "construction",
            "SanitizeFileName falls back to a default name when nothing safe is left");

        Check(DirAccess.DirExistsAbsolute(ConstructionStorage.Directory), "Directory exists (auto-created) after being accessed");

        const string testName = "__selftest_storage__";
        string path = ConstructionStorage.ResolveNewPath(testName);
        // На случай, если прошлый прогон упал до очистки в конце - не даём этому тесту зависеть от состояния диска.
        if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
        path = ConstructionStorage.ResolveNewPath(testName);

        try
        {
            var construction = new Construction(new VoxelGrid());
            construction.Place(Vector3I.Zero, block, block.DefaultColor);

            var errorA = ConstructionStorage.Save(construction, path, testName, "first description");
            Check(errorA == Error.Ok && FileAccess.FileExists(path), "Save writes a JSON file at the resolved path");

            var metaA = ConstructionIO.ReadMetadata(FileAccess.GetFileAsString(path));
            Check(metaA.Name == testName && metaA.Description == "first description"
                  && !string.IsNullOrEmpty(metaA.CreatedUtc) && metaA.CreatedUtc == metaA.ModifiedUtc,
                "first save: name/description stored, createdUtc == modifiedUtc", $"{metaA}");

            var errorB = ConstructionStorage.Save(construction, path, testName, "updated description");
            var metaB = ConstructionIO.ReadMetadata(FileAccess.GetFileAsString(path));
            Check(errorB == Error.Ok && metaB.CreatedUtc == metaA.CreatedUtc,
                "re-saving the same path preserves the original createdUtc (only modifiedUtc/description change)");
            Check(metaB.Description == "updated description", "re-saving updates the description");

            var found = ConstructionStorage.List().FirstOrDefault(c => c.Path == path);
            Check(found.Path == path && found.Name == testName, "List() finds the saved construction by name/path", $"{found}");

            string collidingPath = ConstructionStorage.ResolveNewPath(testName);
            Check(collidingPath != path && !FileAccess.FileExists(collidingPath),
                "ResolveNewPath avoids colliding with an existing file of the same sanitized name",
                $"{collidingPath}");
        }
        finally
        {
            if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);
        }
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

    // ================================================================== сеть (мультиплеер)

    /// <summary>
    /// Мультиплеер (см. Docs/05-world-and-vehicle-systems.md, «Мультиплеер», <see cref="NetHub"/>) — НЕ сквозной
    /// тест двух игроков (для этого нужны два реальных процесса игры, что этот харнесс не умеет — см. class doc
    /// <see cref="NetHub"/> про то, что именно поэтому это требует ручной проверки пользователем). Вместо этого
    /// несколько независимых, но каждая по-настоящему работающих проверок: (1) <see cref="NetEditOps.Apply"/> —
    /// ровно тот код, которым и сервер, и каждый клиент применяют одну и ту же правку (все 6 видов, включая полную
    /// покраску по грани/региону — паритет с одиночным редактированием, см. 05); (2) <see cref="UndoHistory"/> с
    /// авторством записей — правило "откатить можно только своё последнее действие, пока никто другой не построил
    /// поверх" (см. 05), полностью тестируется без единого сетевого вызова (авторы — просто разные <c>long</c> id, не
    /// настоящие peer); (3) настоящее ENet-рукопожатие сервер+клиент по локальной петле — не через <c>NetHub</c>/
    /// <c>SceneTree.Multiplayer</c> (тот один на дерево, а тут нужны сразу два конца), а два независимых "сырых"
    /// <see cref="ENetMultiplayerPeer"/> — подтверждает, что модуль ENet не вырезан из сборки движка (открытый
    /// вопрос, отмеченный в доке при проектировании мультиплеера), не рискуя задеть единственный реальный
    /// <see cref="MultiplayerApi"/> сцены.
    /// </summary>
    private async Task RunNetworkingTests(BuildEditor host)
    {
        GD.Print("-- networking: shared-edit application logic (NetEditOps), shared undo authorship, real ENet loopback handshake");

        RunNetEditOpsTests();
        RunSharedUndoAuthorshipTests();
        await RunEnetLoopbackTest(host);
    }

    /// <summary>
    /// Диагностика бага, найденного пользователем: "на хосте, даже без подключённых клиентов, не появляется
    /// 'Press E' у верстака". Реально переводит <see cref="NetHub"/> в сетевой режим — настоящий
    /// <see cref="ENetMultiplayerPeer.CreateServer"/> на СЕАНСОВЫЙ <c>SceneTree.Multiplayer</c>, не отдельный "сырой"
    /// peer, как в <see cref="RunEnetLoopbackTest"/> — тут важно воспроизвести именно то, что видит настоящий
    /// <see cref="World.GameWorld"/>. Затем собирает тестовый <see cref="World.GameWorld"/> той же техникой, что и
    /// <see cref="RunPlayerAndWorldTests"/> (поверх дерева <paramref name="host"/>, не как главная сцена — самой
    /// <see cref="World.Ui.NetworkLobbyUi"/> это не касается, она показывается только для настоящей главной сцены).
    /// Ноль подключённых клиентов - ровно сценарий "хост открыл игру и пока один".
    /// <para/>
    /// <b>Обязательно ПОСЛЕДНИЙ тест во всём прогоне</b> (см. вызов в <see cref="RunAsync"/>) — реально трогает
    /// <c>SceneTree.Multiplayer</c> вызовом <see cref="NetHub.Host"/>, и "отключение" в конце
    /// (<see cref="NetHub.Disconnect"/>) оставляет движковый <c>get_unique_id()</c> нерабочим до конца процесса
    /// (проверено: `Multiplayer.MultiplayerPeer = null` после того, как peer уже был назначен хоть раз, — это НЕ то
    /// же самое, что "peer никогда не назначался" — судя по всему, `SceneTree` держит какой-то ненулевой peer по
    /// умолчанию, пока его не тронули явно, а вот после явного сброса в null это скрытое умолчание не
    /// восстанавливается; движковая ошибка "No multiplayer peer is assigned" в консоли — это оно). Ни один тест
    /// ПОСЛЕ этого не должен полагаться на <c>Node.IsMultiplayerAuthority</c>/<c>Multiplayer.GetUniqueId</c>.
    /// </summary>
    private async Task RunHostedWorldPlayerTest(BuildEditor host)
    {
        GD.Print("-- networking: hosted GameWorld spawns a local player for the host with zero clients connected");

        var hostErr = NetHub.Instance.Host(37999);
        Check(hostErr == Error.Ok, "NetHub.Host succeeds (sets up the 'host, zero clients connected' scenario)");

        var world = new GameWorld { Name = "SelfTestHostedWorld" };
        host.AddChild(world);
        await Frames(host, 10);

        Check(world.Player != null, "hosting a networked game (even with zero connected clients) still spawns a local Player for the host");
        Player? firstPlayerNode = world.Player;
        if (world.Player != null)
        {
            Check(world.Player.Camera != null && world.Player.Camera.Current,
                "the host's own player camera is the active one in a networked game (not stuck non-authoritative)");
            Check(world.Workbenches.Count == 3, "the hosted world still builds its 3 workbenches (world building isn't networked/gated)");
        }

        // Баг, найденный пользователем: "Create vehicle" было недоступно, если кто-то уже редактировал через тот же
        // физический верстак - теперь каждый вызов RequestOpenSession начинает СВОЮ, независимую сессию (уникальный
        // ключ, см. NetHub.ServerOpenSession), и ни один не отклоняется, сколько бы их ни было на одном верстаке.
        // Хост - единственный подключённый peer, поэтому оба запроса идут от его же LocalPeerId, но сервер всё равно
        // не делит их на одну сессию (ключ строится из счётчика, не только из имени верстака).
        var readySessionKeys = new List<string>();
        void CaptureSessionReady(string sessionKey, string workbenchName, string json) => readySessionKeys.Add(sessionKey);
        NetHub.Instance.SessionReadyForMe += CaptureSessionReady;

        const string benchName = "WorkbenchLarge";
        NetHub.Instance.RequestOpenSession(benchName, null);
        NetHub.Instance.RequestOpenSession(benchName, null);
        await Frames(host, 1);

        NetHub.Instance.SessionReadyForMe -= CaptureSessionReady;
        Check(readySessionKeys.Count == 2 && readySessionKeys[0] != readySessionKeys[1],
            "two Create-vehicle requests on the SAME workbench both succeed, with two distinct session keys (no more 'one session per workbench' limit)",
            $"keys=[{string.Join(", ", readySessionKeys)}]");

        // Баг, найденный пользователем: раньше "есть активная сессия" рассылалось ОДИН раз (broadcast) в момент
        // открытия сессии - кто узнал об этом ПОЗЖЕ (например, подключился к сети уже после), никогда не видел,
        // что можно присоединиться. Обе сессии выше уже открыты К ЭТОМУ МОМЕНТУ - запрос ниже имитирует именно
        // "узнал о них только сейчас, открыв меню" (см. GameWorld case Key.E) - должен увидеть ОБЕ, не только ту,
        // что существовала на момент какого-то более раннего broadcast.
        string[] sessionKeys = Array.Empty<string>();
        long[] adminIds = Array.Empty<long>();
        void CaptureSessionList(string wbName, string[] keys, long[] admins)
        {
            if (wbName != benchName) return;
            sessionKeys = keys;
            adminIds = admins;
        }

        NetHub.Instance.WorkbenchSessionsForMe += CaptureSessionList;
        NetHub.Instance.RequestWorkbenchSessions(benchName);
        await Frames(host, 1);
        NetHub.Instance.WorkbenchSessionsForMe -= CaptureSessionList;

        Check(sessionKeys.Length == 2 && sessionKeys[0] != sessionKeys[1] && adminIds.Length == 2,
            "querying the workbench's sessions on demand returns BOTH already-open sessions, not just the one active at some earlier broadcast moment (the actual bug)",
            $"keys=[{string.Join(", ", sessionKeys)}]");
        Check(Array.IndexOf(sessionKeys, readySessionKeys[0]) >= 0 && Array.IndexOf(sessionKeys, readySessionKeys[1]) >= 0,
            "...and the returned keys are exactly the two sessions created above");

        // То же самое через реальный флоу открытия меню верстака (GameWorld.case Key.E): открыть меню, запросить
        // список, дождаться ответа - Create/Join должны выставиться корректно.
        world.WorkbenchMenu.Open(benchName);
        NetHub.Instance.RequestWorkbenchSessions(benchName);
        await Frames(host, 1);
        Check(!world.WorkbenchMenu.IsCreateButtonDisabled,
            "Create vehicle stays enabled on a workbench that already has active sessions (the actual bug - it used to be disabled here)");
        Check(!world.WorkbenchMenu.IsJoinButtonDisabled && world.WorkbenchMenu.JoinableSessionCount == 2,
            "Join is enabled and the dropdown lists both sessions, now that the menu has queried the server fresh",
            $"count={world.WorkbenchMenu.JoinableSessionCount}");
        world.WorkbenchMenu.Close();

        // Регрессия на баг, найденный пользователем: раньше игроки жили ПОД GameWorld - когда хост (сервер, значит
        // авторитативен над самим существованием этих узлов) входил в свой редактор, его собственная пересборка
        // сцены попутно уничтожала ВСЕХ реплицированных игроков у ВСЕХ клиентов разом (серый экран,
        // ObjectDisposedException у остальных). Симулируем ровно этот переход - GameWorld этого же пира
        // освобождается и пересобирается заново (как при возврате из BuildEditor), NetHub переживает это как
        // автозагрузка (см. её EnsurePlayerReplication class doc). Заодно симулируем то, что реально делает
        // GameWorld.EnterEditor перед уходом (SetActive(false) + спрятать PlayersRoot - см. её же баг "в редакторе
        // отображается персонаж хоста"), чтобы проверить, что возврат в мир корректно отменяет оба эффекта.
        firstPlayerNode?.SetActive(false);
        if (NetHub.Instance.PlayersRoot != null) NetHub.Instance.PlayersRoot.Visible = false;

        // Баг, найденный пользователем: и у хоста, и у клиента вращение камеры переставало работать после выхода с
        // верстака - BuildEditor оставляет мышь видимой (Input.MouseMode.Visible, см. Editor.FlyCamera - СКМ только
        // на время поворота), а для переиспользованного (не пересозданного) узла игрока ничего не возвращало её в
        // Captured, от которого зависит Player.Look. Симулируем ровно это состояние перед "возвратом в мир".
        Input.MouseMode = Input.MouseModeEnum.Visible;

        world.QueueFree();
        await Frames(host, 1);

        var world2 = new GameWorld { Name = "SelfTestHostedWorld2" };
        host.AddChild(world2);
        await Frames(host, 3);

        // В headless-режиме дисплейный сервер не хранит режим мыши (та же оговорка, что и у теста "holding MMB
        // captures the mouse..." ниже) - там, где он реально хранится (оконный запуск), это и есть регрессия на
        // баг пользователя: camera rotation переставала работать после возврата из редактора, пока не нажат Esc.
        Check(DisplayServer.GetName() == "headless" || Input.MouseMode == Input.MouseModeEnum.Captured,
            "returning to the networked world re-captures the mouse even for a REUSED player node (bug fix - camera rotation used to stay broken until Esc was pressed)",
            $"mode={Input.MouseMode}");

        Check(world2.Player == firstPlayerNode,
            "returning to the networked world (same peer, e.g. after entering/exiting the editor) reconnects to the SAME persistent player node, not a fresh one");
        Check(firstPlayerNode != null && GodotObject.IsInstanceValid(firstPlayerNode),
            "...and that node is still valid - NOT freed by the first GameWorld's own teardown (this was the actual crash for other players)");
        Check(firstPlayerNode != null && firstPlayerNode.IsProcessing() && firstPlayerNode.IsPhysicsProcessing(),
            "...and processing is (re)enabled on return (Player.SetActive) - not left paused after simulating an editor visit");
        Check(NetHub.Instance.PlayersRoot is { Visible: true },
            "...and PlayersRoot is visible again on return (was hidden to simulate the editor visit) - other players no longer bleed into whatever scene isn't GameWorld");
        Check(firstPlayerNode != null && firstPlayerNode.Camera.Current,
            "...and the player's camera is re-activated as current (the editor's own camera would have taken over and then been freed with its scene)");

        world2.QueueFree();
        await Frames(host, 1);
        host.EditorCamera.Current = true; // тестовый мир выше забрал "текущую" камеру вьюпорта - вернуть редакторскую
        NetHub.Instance.Disconnect(); // см. doc выше - безопасно только потому, что это последний тест в прогоне

        // Регрессия на баг, найденный пользователем: "Couldn't create an ENet host" при повторном Host() после
        // Exit to menu - Disconnect() раньше только обнулял ссылку на peer, не закрывая её явно, из-за чего
        // нижележащий ENet-сокет (UDP-порт) мог остаться занятым. Тот же порт, что и выше - если он не освободился,
        // повторный Host() тут же это подтвердит.
        var rehostErr = NetHub.Instance.Host(37999);
        Check(rehostErr == Error.Ok, "hosting again on the same port right after Disconnect() succeeds - the previous ENet peer released the port (Disconnect calls Close(), not just clears the reference)");
        NetHub.Instance.Disconnect();
    }

    private void RunNetEditOpsTests()
    {
        var construction = new Construction(new VoxelGrid());

        bool placedRoot = NetEditOps.Apply(construction, NetEditKind.Place, Vector3I.Zero, Vector3I.One, "block", Colors.White, Vector3I.Zero, Vector3I.Zero);
        Check(placedRoot, "NetEditOps.Place places the first block when the grid is empty");
        Check(construction.Grid.IsSolid(Vector3I.Zero), "...and the grid actually reflects it");

        bool placedAdjacent = NetEditOps.Apply(construction, NetEditKind.Place, new Vector3I(1, 0, 0), Vector3I.One, "block", Colors.White, Vector3I.Zero, Vector3I.Zero);
        Check(placedAdjacent, "NetEditOps.Place accepts a block touching an existing one");

        bool placedFarAway = NetEditOps.Apply(construction, NetEditKind.Place, new Vector3I(5, 5, 5), Vector3I.One, "block", Colors.White, Vector3I.Zero, Vector3I.Zero);
        Check(!placedFarAway, "NetEditOps.Place rejects a block touching nothing - same adjacency rule as solo editing (PlacementRules)");

        bool placedUnknownSlug = NetEditOps.Apply(construction, NetEditKind.Place, new Vector3I(2, 0, 0), Vector3I.One, "not-a-real-slug", Colors.White, Vector3I.Zero, Vector3I.Zero);
        Check(!placedUnknownSlug, "NetEditOps.Place rejects an unknown block slug");

        // PaintInstance - весь экземпляр целиком (кубу принадлежит клетка (0,0,0), поставленная выше).
        bool paintedInstance = NetEditOps.Apply(construction, NetEditKind.PaintInstance, Vector3I.Zero, Vector3I.One, "", Colors.Red, Vector3I.Zero, Vector3I.Zero);
        Check(paintedInstance, "NetEditOps.PaintInstance recolors the owned instance at that cell");
        Check(construction.GetOwner(Vector3I.Zero)?.Color == CellColor.Pack(Colors.Red), "...and the instance's own representative color actually changed");

        // PaintFace - ровно одна грань решётки (кубы FullCoverage на всех 6 сторонах - берём X+, axis=0, positive=true).
        bool paintedFace = NetEditOps.Apply(construction, NetEditKind.PaintFace, Vector3I.Zero, Vector3I.One, "", Colors.Blue, Vector3I.Zero, Vector3I.Zero, extraInt: 0, extraBool: true);
        Check(paintedFace, "NetEditOps.PaintFace paints exactly one grid face");
        Check(construction.Grid.GetFaceColor(Vector3I.Zero, 0, true) == CellColor.Pack(Colors.Blue), "...that face is now blue");
        Check(construction.Grid.GetFaceColor(Vector3I.Zero, 0, false) == CellColor.Pack(Colors.Red), "...the opposite face (X-) is untouched - still the whole-instance red from PaintInstance above");

        // PaintCell - голая клетка решётки, залитая в обход Construction (как Dev.DemoBuilds) - PaintInstance/PaintRegion
        // работают только через владеющий экземпляр, PaintCell - единственный вид, который красит такую клетку.
        var bareCell = new Vector3I(20, 0, 0);
        construction.Grid.TrySet(bareCell, Block, CellColor.Pack(Colors.White));
        bool paintedBareCell = NetEditOps.Apply(construction, NetEditKind.PaintCell, bareCell, Vector3I.One, "", Colors.Green, Vector3I.Zero, Vector3I.Zero);
        Check(paintedBareCell, "NetEditOps.PaintCell paints a bare grid cell with no owning Construction instance");
        Check(construction.Grid.GetColor(bareCell) == CellColor.Pack(Colors.Green), "...its color actually changed");

        bool paintedEmptyCell = NetEditOps.Apply(construction, NetEditKind.PaintCell, new Vector3I(9, 9, 9), Vector3I.One, "", Colors.Blue, Vector3I.Zero, Vector3I.Zero);
        Check(!paintedEmptyCell, "NetEditOps.PaintCell on a genuinely empty (never placed/filled) cell is a no-op (false)");

        // PaintRegion - наклонная/треугольная грань не-кубической формы (Wedge, регион 2 = рампа, см. RunPaintRegionTests).
        if (BlockCatalog.Instance.TryGetBySlug("wedge", out var wedgeDef))
        {
            var wedgeCell = new Vector3I(0, 0, 5);
            construction.PlaceBlock(wedgeCell, Vector3I.One, wedgeDef, Colors.White);
            bool paintedRegion = NetEditOps.Apply(construction, NetEditKind.PaintRegion, wedgeCell, Vector3I.One, "", Colors.Yellow, Vector3I.Zero, Vector3I.Zero, extraInt: 2);
            Check(paintedRegion, "NetEditOps.PaintRegion paints a Wedge's ramp region (index 2)");
            Check(construction.GetOwner(wedgeCell)?.RegionColors?.GetValueOrDefault(2) == CellColor.Pack(Colors.Yellow), "...the region color is recorded on the instance");
        }
        else
        {
            Check(false, "NetEditOps setup: 'wedge' slug resolves (needed for the PaintRegion test)");
        }

        bool removed = NetEditOps.Apply(construction, NetEditKind.Remove, new Vector3I(1, 0, 0), Vector3I.One, "", Colors.White, Vector3I.Zero, Vector3I.Zero);
        Check(removed, "NetEditOps.Remove clears the instance owning that cell");
        Check(!construction.Grid.IsSolid(new Vector3I(1, 0, 0)), "...and the grid cell is actually empty again");
    }

    /// <summary>
    /// Общая на сессию <see cref="UndoHistory"/> с авторством записей (см. class doc) — правило "откатить можно
    /// только своё последнее действие, и только пока сверху никто другой не построил" (выбранный вариант, см.
    /// Docs/05-world-and-vehicle-systems.md, «Мультиплеер»). Полностью логика, без единого сетевого вызова: авторы
    /// (peer A = 100, peer B = 200) - просто разные <c>long</c>, ровно так же сервер (<c>NetHub.ServerApplyEdit</c>)
    /// передаёт настоящий <c>Multiplayer.GetRemoteSenderId()</c>.
    /// </summary>
    private void RunSharedUndoAuthorshipTests()
    {
        const long peerA = 100, peerB = 200;
        var construction = new Construction(new VoxelGrid());
        var catalog = BlockCatalog.Instance;
        var history = new UndoHistory();

        Check(history.PeekUndoAuthor == null && history.PeekRedoAuthor == null, "fresh shared history has no undo/redo author");

        // Peer A ставит блок.
        var beforeA = history.Capture(construction);
        NetEditOps.Apply(construction, NetEditKind.Place, Vector3I.Zero, Vector3I.One, "block", Colors.White, Vector3I.Zero, Vector3I.Zero);
        history.RecordIfChanged(beforeA, construction, peerA);
        Check(history.PeekUndoAuthor == peerA, "the top undo entry is authored by whoever just acted (peer A)");

        // Peer B ставит блок поверх - теперь верхняя запись должна принадлежать B, а не A.
        var beforeB = history.Capture(construction);
        NetEditOps.Apply(construction, NetEditKind.Place, new Vector3I(1, 0, 0), Vector3I.One, "block", Colors.White, Vector3I.Zero, Vector3I.Zero);
        history.RecordIfChanged(beforeB, construction, peerB);
        Check(history.PeekUndoAuthor == peerB, "after peer B acts, the top undo entry is now B's, not A's");

        // Peer A пытается откатить - НЕ должно сработать (сервер отказал бы: PeekUndoAuthor != peerA), правило
        // проверяется ДО вызова Undo (см. NetHub.ServerUndo), поэтому здесь просто проверяем сам признак отказа.
        Check(history.PeekUndoAuthor != peerA, "peer A's own request would be rejected - the tip belongs to B, not A (server checks PeekUndoAuthor before calling Undo)");

        // Peer B (настоящий автор верхней записи) откатывает - должно сработать.
        Check(history.PeekUndoAuthor == peerB && history.Undo(construction, catalog), "peer B (the real author of the tip) can undo it");
        Check(!construction.Grid.IsSolid(new Vector3I(1, 0, 0)), "...and B's block is actually gone again");
        Check(history.PeekUndoAuthor == peerA, "the tip is now A's placement - A could undo next, back to an empty grid");

        // После отмены B верхняя запись redo-стека тоже должна принадлежать B (переживает того же автора).
        Check(history.PeekRedoAuthor == peerB, "the redo entry produced by undoing B's action is still tagged as B's");
        Check(history.Redo(construction, catalog), "peer B can redo their own undone action");
        Check(construction.Grid.IsSolid(new Vector3I(1, 0, 0)), "...and B's block is back");

        // Новое действие (A ставит ещё один блок) обрывает redo-ветку целиком, как и в одиночной истории.
        var beforeA2 = history.Capture(construction);
        NetEditOps.Apply(construction, NetEditKind.Place, new Vector3I(2, 0, 0), Vector3I.One, "block", Colors.White, Vector3I.Zero, Vector3I.Zero);
        history.RecordIfChanged(beforeA2, construction, peerA);
        Check(history.PeekRedoAuthor == null, "a new action clears the redo stack (and its authorship with it)");

        // Одиночная игра (без authorId) - поведение не меняется: Undo/Redo безусловны, PeekUndoAuthor всегда null.
        var soloConstruction = new Construction(new VoxelGrid());
        var soloHistory = new UndoHistory();
        var beforeSolo = soloHistory.Capture(soloConstruction);
        NetEditOps.Apply(soloConstruction, NetEditKind.Place, Vector3I.Zero, Vector3I.One, "block", Colors.White, Vector3I.Zero, Vector3I.Zero);
        soloHistory.RecordIfChanged(beforeSolo, soloConstruction); // без authorId - как в Editor.BuildEditor
        Check(soloHistory.PeekUndoAuthor == null, "solo editing never tags an author - RecordIfChanged without authorId");
        Check(soloHistory.Undo(soloConstruction, catalog), "solo Undo is unconditional regardless of PeekUndoAuthor");
    }

    private async Task RunEnetLoopbackTest(Node host)
    {
        const int port = 27099; // маловероятно занят чем-то ещё на машине разработчика
        var server = new ENetMultiplayerPeer();
        var client = new ENetMultiplayerPeer();
        try
        {
            Error serverErr = server.CreateServer(port, 1);
            Error clientErr = client.CreateClient("127.0.0.1", port);
            Check(serverErr == Error.Ok && clientErr == Error.Ok,
                "ENetMultiplayerPeer.CreateServer/CreateClient both succeed (this engine build has NOT stripped the ENet module)",
                $"server={serverErr} client={clientErr}");

            bool connected = false;
            for (int i = 0; i < 120 && !connected; i++)
            {
                server.Poll();
                client.Poll();
                connected = client.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;
                if (!connected) await Frames(host, 1);
            }

            Check(connected, "a real ENet client completes a loopback handshake with a real ENet server within 120 frames");
        }
        finally
        {
            client.Close();
            server.Close();
        }
    }

    // ================================================================== открытый мир: игрок, террейн-заглушка

    /// <summary>
    /// Заготовка на время прототипа (см. Docs/05-world-and-vehicle-systems.md) — плоская плитка 25×25 м + игрок от
    /// первого лица, без входа в редактор и без спавна построек (это отдельные, ещё не сделанные шаги). Сначала
    /// чистая логика движения (не требует сцены), потом сборка <see cref="GameWorld"/> целиком поверх дерева
    /// <paramref name="editor"/> (та же техника, что и остальные интеграционные тесты ниже — сцена не обязана быть
    /// главной, чтобы её можно было собрать и проверить).
    /// </summary>
    private async Task RunPlayerAndWorldTests(BuildEditor editor)
    {
        GD.Print("-- open world: movement math, flat terrain tile placeholder, basic scene wiring");

        Check(Player.ComputeWalkVelocity(Vector2.Zero, 0, 5) == Vector3.Zero, "no input -> zero velocity");

        var forward = Player.ComputeWalkVelocity(new Vector2(0, -1), 0, 5);
        Check(forward.DistanceTo(new Vector3(0, 0, -5)) < 1e-4, "W at yaw 0 moves along -Z (forward)", $"{forward}");

        var right = Player.ComputeWalkVelocity(new Vector2(1, 0), 0, 5);
        Check(right.DistanceTo(new Vector3(5, 0, 0)) < 1e-4, "D at yaw 0 moves along +X (right)", $"{right}");

        var diagonal = Player.ComputeWalkVelocity(new Vector2(1, -1), 0, 5);
        Check(Math.Abs(diagonal.Length() - 5) < 1e-4,
            "diagonal input (W+D together) is normalized first - same speed as a single direction, not faster",
            $"{diagonal}");

        var turned = Player.ComputeWalkVelocity(new Vector2(0, -1), Math.PI / 2, 5);
        Check(turned.DistanceTo(new Vector3(-5, 0, 0)) < 1e-4,
            "W at yaw +90 degrees follows the body's turned forward direction, not the original -Z",
            $"{turned}");

        // GameWorld собирает террейн-плитку, верстаки+зоны спавна и игрока сама в _Ready - никакой отдельной
        // "инициализации" не нужно.
        var world = new GameWorld { Name = "SelfTestWorld" };
        editor.AddChild(world);
        await Frames(editor, 2);

        Check(world.Terrain != null && world.Terrain.Size == GameWorld.TileSize,
            "GameWorld builds a terrain tile placeholder on _Ready", $"size={world.Terrain?.Size}");
        Check(world.Terrain!.GetNodeOrNull("Collision") is CollisionShape3D, "the terrain tile has a collision shape (not visual-only)");
        Check(world.Player != null, "GameWorld spawns a player on _Ready");
        Check(world.Player!.Camera != null && world.Player.Camera.Current, "the player's first-person camera is the active one");

        // Три пары верстак+зона спавна (большая/средняя/маленькая, см. задачу) - структура, не интерактивность
        // (наведение взглядом/E/R/F1 не проверены самотестами - требуют настоящего рейкаста по физике в сцене,
        // которая тут живёт только пару кадров ради проверки, см. Docs/05-world-and-vehicle-systems.md).
        Check(world.Workbenches.Count == 3, "GameWorld builds 3 workbench+spawn-area pairs (large/medium/small)", $"count={world.Workbenches.Count}");
        var large = world.Workbenches.FirstOrDefault(w => w.Name == "WorkbenchLarge");
        var medium = world.Workbenches.FirstOrDefault(w => w.Name == "WorkbenchMedium");
        var small = world.Workbenches.FirstOrDefault(w => w.Name == "WorkbenchSmall");
        Check(large != null && medium != null && small != null, "all three workbenches are present by name");
        Check(large?.SpawnArea != null && medium?.SpawnArea != null && small?.SpawnArea != null,
            "every workbench has a linked SpawnArea (many workbenches CAN share one area, but each has at least its own)");
        Check(large != null && medium != null && small != null
              && Mathf.IsEqualApprox(large.SpawnArea.Size, 16f)
              && Mathf.IsEqualApprox(medium.SpawnArea.Size, 8f)
              && Mathf.IsEqualApprox(small.SpawnArea.Size, 4f),
            "spawn area sizes follow large / half (medium) / quarter (small)",
            $"{large?.SpawnArea.Size}/{medium?.SpawnArea.Size}/{small?.SpawnArea.Size}");
        Check(large?.GetNodeOrNull("Collision") is CollisionShape3D,
            "a workbench has a collision shape (needed for look-based interaction - see GameWorld.RaycastFromCamera)");

        // Баг, найденный пользователем: ожидающий ответа на Join игрок не мог двигаться вообще - плашка ожидания
        // не должна замораживать WASD, в отличие от остальных модальных окон (меню верстака и т.п.), см.
        // GameWorld.MovementBlockingModalOpen/AnyModalOpen. Не требует настоящей сети - это чистая проверка того,
        // какие модалки какой флаг двигают, сама плашка показывается/прячется напрямую.
        await Frames(editor, 1);
        Check(world.Player!.MovementEnabled, "movement starts enabled with no modal open");

        world.JoinWaitingUi.Show();
        await Frames(editor, 2);
        Check(world.Player!.MovementEnabled, "the join-waiting panel does NOT freeze player movement (bug fix - the player can walk around while waiting for the admin's response)");
        world.JoinWaitingUi.Hide();
        await Frames(editor, 2);
        Check(world.Player!.MovementEnabled, "movement stays enabled once the join-waiting panel closes");

        world.WorkbenchMenu.Open("WorkbenchLarge");
        await Frames(editor, 2);
        Check(!world.Player!.MovementEnabled, "the workbench menu itself still freezes movement, unlike the join-waiting panel");
        world.WorkbenchMenu.Close();
        await Frames(editor, 2);
        Check(world.Player!.MovementEnabled, "movement is re-enabled once the workbench menu closes");

        world.QueueFree();
        await Frames(editor, 1);
        editor.EditorCamera.Current = true; // world.Player.Camera выше отобрал "текущую" камеру вьюпорта - вернуть редакторскую
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
        Check(blockCount >= 4, "catalog has at least the 4 shape blocks", $"got {blockCount}");
        int filledSlots = 0;
        for (int i = 0; i < EditorState.HotbarSize; i++)
        {
            if (!string.IsNullOrEmpty(state.GetSlot(i))) filledSlots++;
        }

        // 11 блоков > 9 слотов хотбара - лишние (алфавитно последние функциональные) не попадают в хотбар по
        // умолчанию, но остаются доступны через полный список блоков (Tab). Первые 4 слота - по-прежнему
        // block/inverse_pyramid/pyramid/wedge (см. EditorState - резиновые блоки идут в хотбар раньше функциональных).
        int expectedFilled = Math.Min(blockCount, EditorState.HotbarSize);
        Check(filledSlots == expectedFilled, $"hotbar is pre-filled with the first {expectedFilled} blocks (shape blocks first, then functional)", $"filled={filledSlots}");

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

        // Соседство (BuildEditor.CanPlaceFootprint/TouchesExistingBlock): непустая постройка - блок ставится только
        // рядом с уже стоящим, ровно гранью - даже клетка по диагонали через угол (0 общих граней с постройкой,
        // хотя и касается по ребру/углу) не считается. Постройка сейчас занимает (0,0,0)/(0,1,0)/(0,2,0).
        int blocksBeforeAdjacency = grid.BlockCount;
        var diagonalGround = Screen(new Vector3(0.375f, 0f, 0.375f)); // клетка (1,0,1) - по диагонали от (0,0,0)
        await Move(editor, diagonalGround);
        Check(editor.Hover.Found && !editor.Hover.IsBlock && editor.Hover.PlaceCell == new Vector3I(1, 0, 1),
            "adjacency setup: hovering a ground cell diagonal to the build (shares no face with it)", $"{editor.Hover}");
        Check(!editor.Ghost.Visible, "placement ghost is hidden over a cell that only touches diagonally, not face-to-face");
        await Click(editor, diagonalGround, MouseButton.Left);
        Check(grid.BlockCount == blocksBeforeAdjacency,
            "LMB over a cell touching the build only diagonally places nothing",
            $"blocks={grid.BlockCount}");
        // Соседство лицом-к-лицу по-прежнему работает как раньше - следующий блок теста (клик по верху (0,2,0))
        // это же и демонстрирует.

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

        // 2. Хотбар: клавиши 1-9, Tab. Колесо мыши больше не листает хотбар - зумит камеру (см.
        // BuildEditor._UnhandledInput/FlyCamera.Zoom).
        await PressKey(editor, Godot.Key.Key3);
        Check(state.SelectedSlot == 2, "key 3 selects hotbar slot 3");

        var zoomStart = camera.GlobalPosition;
        var zoomForward = -camera.GlobalTransform.Basis.Z;
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Position = new Vector2(400, 300) });
        await Frames(editor, 2);
        Check(state.SelectedSlot == 2, "mouse wheel no longer changes the hotbar slot");
        var afterZoomIn = camera.GlobalPosition;
        Check(afterZoomIn.DistanceTo(zoomStart) > 0.01 && (afterZoomIn - zoomStart).Normalized().Dot(zoomForward) > 0.99,
            "mouse wheel up zooms the camera in (moves forward along its view direction)", $"{zoomStart} -> {afterZoomIn}");

        Send(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true, Position = new Vector2(400, 300) });
        Send(new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown, Pressed = true, Position = new Vector2(400, 300) });
        await Frames(editor, 2);
        var afterZoomOut = camera.GlobalPosition;
        Check(afterZoomOut.DistanceTo(afterZoomIn) > 0.01 && (afterZoomOut - afterZoomIn).Normalized().Dot(zoomForward) < -0.99,
            "mouse wheel down zooms the camera back out (moves backward)", $"{afterZoomIn} -> {afterZoomOut}");

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

        // --- баг, найденный пользователем: призрак поворачивался МГНОВЕННО, без анимации, когда на том же верстаке
        // пользователь попросил именно её (см. BuildEditor._ghostVisualBasis/EditorState.PendingRotationBasis).
        // PendingRotationBasis обновляется мгновенно (как и раньше PendingRotationSteps), а ВИЗУАЛЬНАЯ ориентация
        // призрака должна ещё быть "в пути" сразу после нажатия и нагнать цель только спустя какое-то время.
        await Move(editor, ground); // постройка только что очищена - призрак снова над свободной клеткой
        var targetBeforeExtraSpin = state.PendingRotationBasis;
        await PressKeyDown(editor, Godot.Key.J);
        Check(!state.PendingRotationBasis.IsEqualApprox(targetBeforeExtraSpin),
            "J updates PendingRotationBasis instantly (the logical/placement value is never animated itself)", $"{state.PendingRotationBasis}");
        Check(!editor.GhostVisualBasis.IsEqualApprox(state.PendingRotationBasis),
            "...but the ghost's VISUAL orientation has not caught up yet right after the press - it animates smoothly, not an instant snap",
            $"visual={editor.GhostVisualBasis} target={state.PendingRotationBasis}");
        await PressKeyUp(editor, Godot.Key.J);
        await Frames(editor, 90);
        Check(editor.GhostVisualBasis.IsEqualApprox(state.PendingRotationBasis),
            "...and settles exactly on the target orientation after enough time has passed",
            $"visual={editor.GhostVisualBasis} target={state.PendingRotationBasis}");

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

        // --- баг (исправлен, найден пользователем): призрак функционального блока со своей моделью (мотор/вал) не
        // отображался вообще - UpdateGhostMesh трактовал ЛЮБОЙ блок без BuildingBlockComponent как куб (BoxMesh) и
        // писал его в editor.Ghost.Mesh, что для настоящей многоузловой glTF-сцены не работает (один MeshInstance3D
        // не может держать произвольное дерево узлов). Теперь такой блок показывает отдельный узел-призрак
        // (_ghostModel, см. UpdateGhostModel) с настоящей моделью, а не куб/ничего.
        // Слот 6 к этому моменту теста уже мог быть переопределён более ранними проверками (например, кликом по
        // карточке "Wedge" в списке блоков) - выставляем содержимое явно, не полагаясь на дефолтное заполнение хотбара.
        // Берётся любой функциональный блок каталога, чья модель реально загружается (состав блоков - пользовательские данные).
        var modelBlock = FindFunctional(ModelLoads);
        if (modelBlock != null)
        {
            string modelSlug = modelBlock.Slug;
            state.SelectedSlot = 6;
            state.SetSlot(6, modelSlug);
            Check(state.SelectedBlockSlug == modelSlug, $"setup: hotbar slot 6 now holds '{modelSlug}'", state.SelectedBlockSlug);
            await Move(editor, ground);
            Check(!editor.Ghost.Visible, $"'{modelSlug}' ghost: the cube/shape ghost (editor.Ghost) is hidden - a real model is shown instead");
            Check(editor.GhostModelVisible, $"'{modelSlug}' ghost: the model ghost IS visible over empty ground", $"{editor.GhostModelVisible}");
        }
        else
        {
            Check(true, "ghost-model checks skipped: no functional block with a loadable model in the catalog right now");
        }

        state.SelectedSlot = 3; // wedge - обратно на куб/форму, проверить, что призрак модели прячется обратно
        // PendingSize - одно общее значение на весь редактор: выбор функционального блока с footprint'ом больше 1x1x1 оставил его
        // таким же при возврате на резиновый блок - возвращаем 1x1x1, чтобы дальнейшие тесты ставили одиночные блоки.
        for (int axis = 0; axis < 3; axis++) state.SetPendingSizeAxis(axis, 1);
        await Move(editor, ground);
        Check(editor.Ghost.Visible && !editor.GhostModelVisible,
            "switching back to a shape block hides the model ghost again and shows the cube/shape ghost");

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

        // Диалог сохранения (кнопка Save на тулбаре, см. Ui.SaveDialogUi/Core.ConstructionStorage): имя/описание,
        // Done сохраняет с метаданными под автоматическим путём (не FileDialog, как раньше), Escape/Cancel закрывает
        // без сохранения, горячие клавиши редактора не срабатывают, пока диалог открыт (иначе, например, "1" в
        // названии заодно переключал бы слот хотбара — см. фикс в BuildEditor.HandleKey). Само превью (PNG) не
        // проверяем — требует нескольких реально отрисованных кадров, ненадёжно в этом headless-окружении разработки
        // (см. class doc ConstructionPreviewRenderer); проверяется только то, что запись файла с метаданными и
        // блокировка ввода работают.
        editor.World.Construction.Place(new Vector3I(0, 0, 0), BlockCatalog.Instance.Get("block"), Colors.White);
        var saveButton = FindButton("Save...");
        Check(saveButton != null, "toolbar: Save button exists");
        await Click(editor, CenterOf(saveButton!), MouseButton.Left);
        Check(editor.Ui.SaveDialogOpen, "clicking Save opens the save dialog");

        state.SelectedSlot = 3;
        Send(new InputEventKey { PhysicalKeycode = Godot.Key.Key1, Keycode = Godot.Key.Key1, Pressed = true });
        await Frames(editor, 1);
        Check(state.SelectedSlot == 3,
            "pressing '1' while the save dialog is open does not change the hotbar slot (would otherwise fight with typing a name)");

        Send(new InputEventKey { PhysicalKeycode = Godot.Key.Escape, Keycode = Godot.Key.Escape, Pressed = true });
        await Frames(editor, 1);
        Check(!editor.Ui.SaveDialogOpen, "Escape closes the save dialog without saving (like Cancel)");

        await Click(editor, CenterOf(saveButton!), MouseButton.Left);
        var nameField = editor.GetTree().Root.FindChildren("*", "LineEdit", true, false)
            .OfType<LineEdit>().First(f => f.IsVisibleInTree() && f.PlaceholderText == "My Boat");
        var descriptionField = editor.GetTree().Root.FindChildren("*", "TextEdit", true, false)
            .OfType<TextEdit>().First(f => f.IsVisibleInTree());
        var doneButton = FindButtons(editor).Find(b => b.Text == "Done");
        Check(doneButton != null, "save dialog: Done button exists");

        const string testVehicleName = "__selftest_vehicle__";
        nameField.Text = "";
        await Click(editor, CenterOf(doneButton!), MouseButton.Left);
        Check(editor.Ui.SaveDialogOpen, "Done with an empty name does not close the dialog (a name is required)");

        nameField.Text = testVehicleName;
        descriptionField.Text = "a self-test vehicle";
        await Click(editor, CenterOf(doneButton!), MouseButton.Left);
        Check(!editor.Ui.SaveDialogOpen, "Done with a name closes the dialog");

        var savedEntry = ConstructionStorage.List().FirstOrDefault(c => c.Name == testVehicleName);
        Check(savedEntry.Path != null && FileAccess.FileExists(savedEntry.Path) && savedEntry.Description == "a self-test vehicle",
            "Done writes a named save file with the entered name/description, discoverable via ConstructionStorage.List",
            $"{savedEntry}");
        if (savedEntry.Path != null) DirAccess.RemoveAbsolute(savedEntry.Path);

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

    // ================================================================== дебаг-редактор блоков (--blockeditor)

    /// <summary>
    /// <see cref="Dev.BlockPrefabEditor"/> — отдельный инструмент (не <see cref="Editor.BuildEditor"/>), поэтому
    /// добавляется как временный ребёнок уже загруженного <paramref name="editor"/> (та же техника, что и у других
    /// интеграционных тестов, собирающих свой узел поверх дерева <c>BuildEditor</c> — сцена не обязана быть главной,
    /// чтобы её можно было собрать и проверить). Главный риск этого инструмента — сериализация в/из XML (UI-часть
    /// глазами не проверить без живого дисплея), поэтому тест целится именно в неё: загрузка реального
    /// функционального блока из каталога + полный цикл запись→чтение с диска для нового.
    /// </summary>
    private async Task RunBlockPrefabEditorTests(BuildEditor editor)
    {
        GD.Print("-- block prefab editor (--blockeditor): load existing + new/save/reload round-trip");

        var tool = new BlockPrefabEditor();
        editor.AddChild(tool);
        await Frames(editor, 2);

        // Загрузка существующего функционального блока - поля редактора должны совпасть с тем, что прочитал каталог. Какой именно
        // блок - не важно (их состав пользователь меняет сам), берём первый подходящий.
        var referenceBlock = FindFunctional(f => f.Ports.Count > 0 || f.Nodes.Count > 0) ?? FindFunctional();
        if (referenceBlock != null)
        {
            var reference = FunctionalOf(referenceBlock)!;
            tool.LoadSlug(referenceBlock.Slug);
            Check(tool.Ui.Footprint == reference.Footprint, $"loading '{referenceBlock.Slug}' reads its footprint", $"{tool.Ui.Footprint} vs {reference.Footprint}");
            Check(tool.Ui.ScenePath == (reference.ScenePath ?? ""), "...and its scene path", tool.Ui.ScenePath);
            Check(tool.Ui.Behavior == reference.Behavior, "...and its behavior string", tool.Ui.Behavior);
            Check(tool.Ui.Ports.Count == reference.Ports.Count && tool.Ui.Ports.Select(p => (p.Id, p.Resource, p.Direction, p.Face, p.FaceCell))
                      .SequenceEqual(reference.Ports.Select(p => (p.Id, p.Resource, p.Direction, p.Face, p.FaceCell))),
                "...and its physical ports, field for field", $"{tool.Ui.Ports.Count} vs {reference.Ports.Count}");
            Check(tool.Ui.Nodes.Select(n => (n.Id, n.Type, n.Direction, n.Cell)).SequenceEqual(reference.Nodes.Select(n => (n.Id, n.Type, n.Direction, n.Cell))),
                "...and its logic nodes (id/type/direction/cell), field for field - nodes are shown as nodes, not as ports", $"{tool.Ui.Nodes.Count} vs {reference.Nodes.Count}");
        }

        // Тот же круг для КАЖДОГО функционального блока каталога: редактор читает ровно то, что прочитал каталог, ничего не теряя
        // (в т.ч. params - иначе пересохранение блока в редакторе стёрло бы настройки поведения).
        foreach (var definition in BlockCatalog.Instance.All.Where(d => d.HasComponent<FunctionalBlockComponent>()).OrderBy(d => d.Slug))
        {
            var fn = FunctionalOf(definition)!;
            tool.LoadSlug(definition.Slug);
            Check(tool.Ui.Nodes.Count == fn.Nodes.Count && tool.Ui.Ports.Count == fn.Ports.Count && (tool.Ui.ParamsJson.Length > 0) == (fn.BehaviorParams.Count > 0),
                $"the block editor loads '{definition.Slug}' without losing nodes, ports or behavior params");
        }

        // Обратная совместимость со старым форматом (без "face"/"position") проверяется на ОДНОРАЗОВОМ файле,
        // который сам тест пишет и чистит - не на редактируемом пользователем blocks/electric_motor.xml.
        const string legacySlug = "selftest_blockprefab_legacy_ports";
        string legacyPath = $"res://blocks/{legacySlug}.xml";
        using (var legacyFile = FileAccess.Open(legacyPath, FileAccess.ModeFlags.Write))
        {
            legacyFile.StoreString(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                $"<Block id=\"{legacySlug}\" name=\"Legacy\">\n" +
                "  <Color>#ffffff</Color>\n" +
                "  <Component type=\"FunctionalBlock\">{ \"footprint\": [1,1,1], \"ports\": [ " +
                "{ \"id\": \"p\", \"resource\": \"Fluid\", \"direction\": \"In\" } ] }</Component>\n" +
                "</Block>\n");
        }

        tool.LoadSlug(legacySlug);
        Check(tool.Ui.Ports.Count == 1 && tool.Ui.Ports[0].Face == BlockFace.PosZ && tool.Ui.Ports[0].FaceCell == Vector2I.Zero,
            "a port without 'face'/'position' in the XML (old hand-written format) defaults to (PosZ, (0,0))",
            tool.Ui.Ports.Count > 0 ? $"{tool.Ui.Ports[0].Face}/{tool.Ui.Ports[0].FaceCell}" : "no ports");
        if (FileAccess.FileExists(legacyPath)) DirAccess.RemoveAbsolute(legacyPath);

        // Новый блок + бокс коллизии + сохранение + перечитывание с диска - полный цикл записи/чтения XML (главный
        // риск этого инструмента, см. class doc) на ОДНОРАЗОВОМ тестовом слаге, не трогающем настоящий каталог.
        const string testSlug = "selftest_blockprefab_tmp";
        string path = $"res://blocks/{testSlug}.xml";
        if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path);

        tool.ResetToDefaults(testSlug);
        tool.AddCollisionBox();
        Check(tool.Ui.CollisionBoxes.Count == 1, "AddCollisionBox adds exactly one row", $"{tool.Ui.CollisionBoxes.Count}");
        tool.Ui.SetModelScaleForTesting(new Vector3(2f, 1f, 1.5f)); // "Model stretch" - должен тоже пережить save+reload
        tool.Ui.SetModelOffsetForTesting(new Vector3(0.125f, -0.0625f, 0f)); // "Model offset" - тоже

        // Два порта на РАЗНЫХ сторонах + два порта, делящие ОДНУ И ТУ ЖЕ (Face, FaceCell) - проверяем именно то, что
        // запросил пользователь: несколько ресурсов/портов, несколько портов в одном месте, порт не только на одной
        // стороне. AddPort() добавляет ряд с заглушками по умолчанию; SetPortForTesting заполняет его целевыми значениями.
        tool.AddPort();
        tool.AddPort();
        tool.AddPort();
        Check(tool.Ui.Ports.Count == 3, "AddPort adds exactly one row each call", $"{tool.Ui.Ports.Count}");
        tool.Ui.SetPortForTesting(0, "fluid_in", ResourceType.Fluid, PortDirection.In, BlockFace.NegX, new Vector2I(0, 0));
        tool.Ui.SetPortForTesting(1, "shaft_out", ResourceType.Torque, PortDirection.Out, BlockFace.PosZ, new Vector2I(1, 2));
        tool.Ui.SetPortForTesting(2, "shaft_out_2", ResourceType.Torque, PortDirection.Out, BlockFace.PosZ, new Vector2I(1, 2)); // той же (Face, FaceCell), что и предыдущий - намеренно

        // Логические ноды (добавляются кнопкой Add node) и параметры поведения (сырой JSON) - должны пережить save+reload и
        // не теряться при сохранении любого блока. Три ноды РАЗНЫХ типов в одной клетке (допустимо) + одна отдельно.
        tool.AddNode();
        tool.AddNode();
        tool.AddNode();
        Check(tool.Ui.Nodes.Count == 3 && tool.Ui.Nodes.Select(n => n.Type).Distinct().Count() == 3,
            "Add node x3 gives three nodes of three DIFFERENT types in one cell - the editor never creates a same-type conflict by itself",
            string.Join(",", tool.Ui.Nodes.Select(n => n.Type)));
        tool.Ui.SetNodeForTesting(0, "power_in", NodeType.Electricity, PortDirection.In, new Vector3I(0, 0, 0));
        tool.Ui.SetNodeForTesting(1, "flag_out", NodeType.Boolean, PortDirection.Out, new Vector3I(0, 0, 0));
        tool.Ui.SetNodeForTesting(2, "rpm_in", NodeType.Number, PortDirection.In, new Vector3I(0, 0, 0));
        tool.Ui.SetParamsForTesting("{ \"mode\": \"toggle\", \"glowNode\": \"Lid\", \"glowColor\": \"#ff8800\" }");

        tool.Save();
        Check(FileAccess.FileExists(path), "Save() writes blocks/<slug>.xml", path);

        tool.ResetToDefaults(""); // сбросить поля, чтобы следующая загрузка проверяла реально ПРОЧИТАННОЕ, не старое
        tool.LoadSlug(testSlug);
        Check(tool.Ui.Footprint == Vector3I.One, "round-trip: footprint (default 1,1,1) survives save+reload", $"{tool.Ui.Footprint}");
        Check(tool.Ui.CollisionBoxes.Count == 1, "round-trip: the collision box survives save+reload", $"{tool.Ui.CollisionBoxes.Count}");
        Check(tool.Ui.CollisionBoxes.Count == 1 && tool.Ui.CollisionBoxes[0].Size.IsEqualApprox(new Vector3(0.25f, 0.25f, 0.25f)),
            "round-trip: the collision box's size (default = footprint in meters) survives exactly",
            tool.Ui.CollisionBoxes.Count > 0 ? $"{tool.Ui.CollisionBoxes[0].Size}" : "no boxes");
        Check(tool.Ui.ModelScale.IsEqualApprox(new Vector3(2f, 1f, 1.5f)),
            "round-trip: the manual 'Model stretch' survives save+reload", $"{tool.Ui.ModelScale}");
        Check(tool.Ui.ModelOffset.IsEqualApprox(new Vector3(0.125f, -0.0625f, 0f)),
            "round-trip: the manual 'Model offset' survives save+reload", $"{tool.Ui.ModelOffset}");

        Check(tool.Ui.Ports.Count == 3, "round-trip: all 3 ports survive save+reload", $"{tool.Ui.Ports.Count}");
        var reloadedPowerIn = tool.Ui.Ports.FirstOrDefault(p => p.Id == "fluid_in");
        Check(reloadedPowerIn != null && reloadedPowerIn.Resource == ResourceType.Fluid && reloadedPowerIn.Direction == PortDirection.In
            && reloadedPowerIn.Face == BlockFace.NegX && reloadedPowerIn.FaceCell == Vector2I.Zero,
            "round-trip: a port's resource/direction/face/cell all survive exactly", reloadedPowerIn == null ? "not found" : $"{reloadedPowerIn.Resource}/{reloadedPowerIn.Direction}/{reloadedPowerIn.Face}/{reloadedPowerIn.FaceCell}");

        var sharedLocationPorts = tool.Ui.Ports.Where(p => p.Face == BlockFace.PosZ && p.FaceCell == new Vector2I(1, 2)).ToList();
        Check(sharedLocationPorts.Count == 2 && sharedLocationPorts.Select(p => p.Id).ToHashSet().SetEquals(new[] { "shaft_out", "shaft_out_2" }),
            "round-trip: two ports sharing the same (Face, FaceCell) both survive - no dedup/overwrite",
            $"{sharedLocationPorts.Count} ports at that spot: {string.Join(",", sharedLocationPorts.Select(p => p.Id))}");

        var reloadedNodes = tool.Ui.Nodes;
        var reloadedPower = reloadedNodes.FirstOrDefault(n => n.Id == "power_in");
        Check(reloadedNodes.Count == 3 && reloadedPower != null && reloadedPower.Type == NodeType.Electricity && reloadedPower.Direction == PortDirection.In
              && reloadedNodes.Any(n => n.Id == "flag_out" && n.Type == NodeType.Boolean && n.Direction == PortDirection.Out)
              && reloadedNodes.Any(n => n.Id == "rpm_in" && n.Type == NodeType.Number && n.Direction == PortDirection.In)
              && reloadedNodes.All(n => n.Cell == Vector3I.Zero),
            "round-trip: all three nodes (id/type/direction/cell), sharing one cell with different types, survive save+reload",
            string.Join(",", reloadedNodes.Select(n => $"{n.Id}:{n.Type}:{n.Direction}:{n.Cell}")));
        Check(tool.Ui.ParamsJson == "{\"mode\":\"toggle\",\"glowNode\":\"Lid\",\"glowColor\":\"#ff8800\"}",
            "round-trip: behavior params (mode, glowNode, glowColor) survive save+reload without loss", tool.Ui.ParamsJson);

        // Конфликт нод и битый params не должны затирать файл на диске (каталог бросает на ошибке разбора блока).
        string savedText = FileAccess.GetFileAsString(path);
        tool.Ui.SetNodeForTesting(2, "rpm_in", NodeType.Boolean, PortDirection.In, new Vector3I(0, 0, 0)); // 2 Boolean в одной клетке (In и Out)
        tool.Save();
        Check(FileAccess.GetFileAsString(path) == savedText,
            "Save() with two nodes of the SAME type in one cell (even In vs Out) refuses to write - the file on disk is untouched");
        tool.Ui.SetNodeForTesting(2, "rpm_in", NodeType.Number, PortDirection.In, new Vector3I(0, 0, 0)); // чиним конфликт
        tool.Ui.SetParamsForTesting("[1, 2]");
        tool.Save();
        Check(FileAccess.GetFileAsString(path) == savedText, "...and so does a params value that is not a JSON object");
        tool.Ui.SetParamsForTesting("{ \"mode\": \"toggle\", \"glowNode\": \"Lid\", \"glowColor\": \"#ff8800\" }");

        // Remove node убирает ровно один ряд; тот же Undo-стек, что и у остальных списков.
        int nodesBeforeRemove = tool.Ui.Nodes.Count;
        tool.RemoveNode(1);
        Check(tool.Ui.Nodes.Count == nodesBeforeRemove - 1 && tool.Ui.Nodes.All(n => n.Id != "flag_out"), "Remove node removes exactly that node", $"{tool.Ui.Nodes.Count}");
        tool.Undo();
        Check(tool.Ui.Nodes.Count == nodesBeforeRemove && tool.Ui.Nodes.Any(n => n.Id == "flag_out"), "Ctrl+Z restores the removed node", $"{tool.Ui.Nodes.Count}");

        tool.LoadSlug(testSlug);

        // --- Undo/Redo (Ctrl+Z/Ctrl+Y), по запросу пользователя - снимок ДО действия, восстанавливается целиком.
        int portsBeforeAdd = tool.Ui.Ports.Count; // 3, с перезагрузки выше
        tool.AddPort();
        Check(tool.Ui.Ports.Count == portsBeforeAdd + 1, "setup: AddPort adds a 4th port", $"{tool.Ui.Ports.Count}");
        tool.Undo();
        Check(tool.Ui.Ports.Count == portsBeforeAdd && tool.Ui.Ports.Any(p => p.Id == "fluid_in"),
            "Ctrl+Z undoes AddPort - back to exactly the 3 ports from before, not just the same COUNT by accident",
            $"{tool.Ui.Ports.Count}: {string.Join(",", tool.Ui.Ports.Select(p => p.Id))}");
        tool.Redo();
        Check(tool.Ui.Ports.Count == portsBeforeAdd + 1, "Ctrl+Y redoes it", $"{tool.Ui.Ports.Count}");

        int boxesBeforeAdd = tool.Ui.CollisionBoxes.Count; // 1, из AddCollisionBox выше
        tool.AddCollisionBox();
        tool.Undo();
        Check(tool.Ui.CollisionBoxes.Count == boxesBeforeAdd, "Ctrl+Z also undoes AddCollisionBox (same Undo stack, not a separate one per list)", $"{tool.Ui.CollisionBoxes.Count}");

        Check(tool.Ui.ModelScale.IsEqualApprox(new Vector3(2f, 1f, 1.5f)),
            "...and fields untouched by either action (Model stretch from earlier) survive the round trip through the snapshot unchanged", $"{tool.Ui.ModelScale}");

        // Загрузка ДРУГОГО блока (Load/New) начинает Undo/Redo с чистого листа - нельзя "отменой" дотянуться до
        // правок уже закрытого документа.
        string otherSlug = referenceBlock?.Slug ?? "block";
        tool.LoadSlug(otherSlug);
        var footprintAfterLoad = tool.Ui.Footprint;
        var scenePathAfterLoad = tool.Ui.ScenePath;
        tool.Undo(); // стек пуст после ClearUndoHistory в LoadSlug - должен быть no-op, не откатывать к testSlug
        Check(tool.Ui.Footprint == footprintAfterLoad && tool.Ui.ScenePath == scenePathAfterLoad && tool.Ui.Slug == otherSlug,
            "loading a different block (LoadSlug) clears Undo/Redo history - Ctrl+Z here is a no-op, not a jump back into the previous block's edits",
            $"{tool.Ui.Footprint} {tool.Ui.ScenePath} {tool.Ui.Slug}");

        if (FileAccess.FileExists(path)) DirAccess.RemoveAbsolute(path); // не мусорим в blocks/ настоящим файлом
        tool.QueueFree();
    }

    /// <summary>
    /// Баг, найденный пользователем: раньше ЛЮБОЙ блок без собственных боксов коллизии (включая функциональные)
    /// автоматически получал один бокс на весь экземпляр - у некоторых моделей есть выпирающие за footprint детали,
    /// которым коллизия не нужна, а автоматический бокс делал "совсем без коллизии" недостижимым. Теперь это
    /// различается по наличию <see cref="FunctionalBlockComponent"/>: у обычного блока (куб) коллизия по-прежнему
    /// автоматическая, у функционального (electric_motor, без собственных боксов в каталоге) - её нет вообще.
    /// </summary>
    private void RunVehicleSpawnerCollisionTests(Node host)
    {
        GD.Print("-- VehicleSpawner collision: ordinary blocks keep the automatic box, functional blocks get EXACTLY their own boxes (no automatic fallback)");

        var grid = new VoxelGrid();
        var construction = new Construction(grid);
        var catalog = BlockCatalog.Instance;
        var functionalDef = FindFunctional();
        if (functionalDef == null)
        {
            Check(true, "spawner collision check skipped: no functional block in the catalog right now");
            return;
        }

        var functionalFn = FunctionalOf(functionalDef)!;
        construction.Place(new Vector3I(0, 0, 0), catalog.Get("block"), Colors.White);
        construction.PlaceBlock(new Vector3I(5, 0, 0), functionalFn.Footprint, functionalDef, Colors.White);

        // Функциональные блоки правятся через --blockeditor в любой момент - не зашиваем "у него N боксов", а берём РЕАЛЬНОЕ
        // текущее число. Проверяемый инвариант - не "сколько именно", а что функциональный блок получает РОВНО столько
        // CollisionShape3D, сколько у него своих боксов, и ни одного автоматического "на всякий случай" сверху.
        int motorOwnBoxes = functionalFn.CollisionBoxes.Count;

        string json = ConstructionIO.Serialize(construction);
        var parent = new Node3D();
        host.AddChild(parent);

        var body = VehicleSpawner.Spawn(parent, json, Vector3.Zero);
        int shapeCount = 0;
        foreach (var child in body.GetChildren())
        {
            if (child is CollisionShape3D) shapeCount++;
        }

        int expected = 1 + motorOwnBoxes; // 1 - автоматический бокс 'block', + сколько реально задано у мотора
        Check(shapeCount == expected,
            "'block' gets its automatic box (1) + a functional block gets EXACTLY its own CollisionBoxes.Count, no automatic fallback on top",
            $"got {shapeCount}, expected {expected} (1 + '{functionalDef.Slug}'.CollisionBoxes.Count={motorOwnBoxes})");

        body.QueueFree();
        parent.QueueFree();
    }

    // ================================================================== сигнальные порты, параметры поведения, рантайм функциональных блоков

    private static FunctionalBlockComponent? FunctionalOf(BlockDefinition definition) => definition.GetComponent<FunctionalBlockComponent>();

    /// <summary>Первый (по слагу) функциональный блок НАСТОЯЩЕГО каталога, подходящий под условие. Состав функциональных блоков —
    /// редактируемые пользователем данные (blocks/*.xml переименовывают, удаляют, пересоздают через --blockeditor), поэтому тесты
    /// берут любой подходящий, а не блок с конкретным слагом.</summary>
    private static BlockDefinition? FindFunctional(Func<FunctionalBlockComponent, bool>? predicate = null) =>
        BlockCatalog.Instance.All.OrderBy(d => d.Slug).FirstOrDefault(d => FunctionalOf(d) is { } f && (predicate?.Invoke(f) ?? true));

    private static bool ModelLoads(FunctionalBlockComponent f) =>
        !string.IsNullOrEmpty(f.ScenePath) && FunctionalBlockGeometry.GetOrLoadScene(f.ScenePath!).Scene != null;

    /// <summary>
    /// ФИКСТУРНЫЙ каталог из трёх временных блоков (простой куб, кнопка с поведением Button, мотор без реализованного поведения),
    /// записанных в user://selftest_blocks, загруженных отдельным <see cref="BlockCatalog.Load"/> и тут же удалённых — тесты
    /// рантайма не зависят от того, какие функциональные блоки сейчас лежат в настоящем blocks/. Годится там, где каталог нужен
    /// только как источник <see cref="BlockDefinition"/> (Construction/FunctionalBlockRuntime); для рендера/VehicleSpawner —
    /// нет (они читают <see cref="BlockCatalog.Instance"/> по RuntimeId).
    /// </summary>
    private static BlockCatalog BuildFixtureCatalog()
    {
        const string dir = "user://selftest_blocks";
        DirAccess.MakeDirRecursiveAbsolute(dir);
        var files = new Dictionary<string, string>
        {
            ["plain"] = "<Block id=\"plain\" name=\"Plain\"><Color>#ffffff</Color>" +
                "<Component type=\"BaseComponent\">{ \"mass\": 10 }</Component>" +
                "<Component type=\"BuildingBlock\">{ \"shape\": \"Cube\", \"minSize\": [1,1,1], \"maxSize\": [8,8,8] }</Component></Block>",
            ["fx_button"] = "<Block id=\"fx_button\" name=\"Button\"><Color>#c0392b</Color>" +
                "<Component type=\"BaseComponent\">{ \"mass\": 2 }</Component>" +
                "<Component type=\"FunctionalBlock\">{ \"footprint\": [1,1,1], \"behavior\": \"Button\", " +
                "\"params\": {\"mode\":\"momentary\",\"glowNode\":\"Cap\",\"glowColor\":\"#33ff55\"}, " +
                "\"collision\": [ { \"position\": [0,0,0], \"size\": [0.25,0.25,0.25] } ], \"ports\": [], " +
                "\"nodes\": [ { \"id\": \"power_in\", \"type\": \"Electricity\", \"direction\": \"In\", \"position\": [0,0,0] }, " +
                "{ \"id\": \"signal_out\", \"type\": \"Boolean\", \"direction\": \"Out\", \"position\": [0,0,0] } ] }</Component></Block>",
            ["fx_motor"] = "<Block id=\"fx_motor\" name=\"Motor\"><Color>#39506b</Color>" +
                "<Component type=\"BaseComponent\">{ \"mass\": 60 }</Component>" +
                "<Component type=\"FunctionalBlock\">{ \"footprint\": [1,1,1], \"behavior\": \"ElectricMotor\", " +
                "\"ports\": [ { \"id\": \"shaft_out\", \"resource\": \"Torque\", \"direction\": \"Out\", \"face\": \"PosY\", \"position\": [0,0] } ], " +
                "\"nodes\": [ { \"id\": \"power_in\", \"type\": \"Electricity\", \"direction\": \"In\", \"position\": [0,0,0] } ] }</Component></Block>",
        };

        foreach (var (slug, xml) in files)
        {
            using var file = FileAccess.Open($"{dir}/{slug}.xml", FileAccess.ModeFlags.Write);
            file.StoreString("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + xml);
        }

        var catalog = BlockCatalog.Load(dir);
        foreach (string slug in files.Keys) DirAccess.RemoveAbsolute($"{dir}/{slug}.xml");
        return catalog;
    }

    private static FunctionalBlockComponent MakeFunctional(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var component = new FunctionalBlockComponent();
        component.LoadFromJson(doc.RootElement);
        return component;
    }

    /// <summary>
    /// Шаги 1-2 кнопок: (1) сигнальный порт как тип данных (НЕ ресурс), параметры поведения, блок <c>button</c> в
    /// каталоге с явным боксом коллизии; (2) рантайм-слой — поведение Button (оба режима) и <see cref="FunctionalBlockRuntime"/>
    /// (состояние на экземпляр отдельно от <see cref="BlockInstance"/>). Режим РЕАЛЬНОГО blocks/button.xml не зашит
    /// (его можно поменять в файле) — режимы проверяются на синтетических определениях.
    /// </summary>
    private void RunNodeAndBehaviorTests()
    {
        GD.Print("-- logic nodes (Electricity/Boolean/Number), placement rule, behavior params, Button behavior, FunctionalBlockRuntime");

        // ---- данные: ноды и params
        var parsed = MakeFunctional(
            "{ \"footprint\": [2,1,1], \"params\": { \"mode\": \"toggle\", \"n\": 3 }, \"nodes\": [" +
            "{ \"id\": \"a\", \"type\": \"Number\", \"direction\": \"In\", \"position\": [1, 0, 0] }," +
            "{ \"id\": \"b\", \"type\": \"boolean\", \"direction\": \"out\" }," +
            "{ \"id\": \"c\", \"type\": \"Electricity\", \"direction\": \"In\" } ] }");
        Check(parsed.Nodes.Count == 3 && parsed.Ports.Count == 0,
            "\"nodes\" are parsed into Nodes and are NOT mixed into the physical Ports list", $"nodes={parsed.Nodes.Count} ports={parsed.Ports.Count}");
        var nodeA = parsed.Nodes[0];
        Check(nodeA.Id == "a" && nodeA.Type == NodeType.Number && nodeA.Direction == PortDirection.In && nodeA.Cell == new Vector3I(1, 0, 0),
            "a node reads id/type/direction/cell", $"{nodeA.Id}/{nodeA.Type}/{nodeA.Direction}/{nodeA.Cell}");
        var nodeB = parsed.Nodes[1];
        Check(nodeB.Type == NodeType.Boolean && nodeB.Direction == PortDirection.Out && nodeB.Cell == Vector3I.Zero,
            "enum values are case-insensitive; an omitted position defaults to cell (0,0,0)", $"{nodeB.Type}/{nodeB.Direction}/{nodeB.Cell}");
        Check(parsed.Nodes[2].Type == NodeType.Electricity && parsed.Nodes[2].Cell == nodeB.Cell,
            "Electricity is a node type; nodes of DIFFERENT types (Boolean + Electricity) may share a cell");

        bool Throws(string json)
        {
            try { MakeFunctional(json); return false; }
            catch (Exception) { return true; }
        }

        Check(Throws("{ \"nodes\": [ { \"id\": \"x\", \"type\": \"Fluid\", \"direction\": \"In\" } ] }"),
            "an unknown node type is a loud data error (Fluid is a physical resource, not a node type)");
        Check(Throws("{ \"nodes\": [ { \"id\": \"x\", \"type\": \"Boolean\", \"direction\": \"In\", \"position\": [0, 0] } ] }"),
            "a node position that is not [x, y, z] is a loud data error (a 2D port position can't be pasted into a node by mistake)");

        // Правило размещения: в одной клетке - только разные типы (направление не важно).
        Check(Throws("{ \"nodes\": [ { \"id\": \"x\", \"type\": \"Boolean\", \"direction\": \"In\" }, { \"id\": \"y\", \"type\": \"Boolean\", \"direction\": \"Out\" } ] }"),
            "two nodes of the SAME type in one cell are rejected at load, even one In and one Out");
        Check(!Throws("{ \"footprint\": [2,1,1], \"nodes\": [ { \"id\": \"x\", \"type\": \"Boolean\", \"direction\": \"In\" }, { \"id\": \"y\", \"type\": \"Boolean\", \"direction\": \"In\", \"position\": [1, 0, 0] } ] }"),
            "...but the same type in DIFFERENT cells is fine");
        var conflictNodes = new[]
        {
            new LogicNode { Id = "p", Type = NodeType.Number, Direction = PortDirection.In },
            new LogicNode { Id = "q", Type = NodeType.Number, Direction = PortDirection.In },
        };
        Check(FunctionalBlockComponent.FindNodeConflict(conflictNodes) is { } message && message.Contains("'p'") && message.Contains("'q'"),
            "FindNodeConflict names both offending nodes");
        Check(FunctionalBlockComponent.FindNodeConflict(parsed.Nodes) == null, "FindNodeConflict is null for a valid layout");

        // Электричество больше не физический ресурс: старая запись порта объясняет, куда его перенести.
        bool electricityPortExplains = false;
        try { MakeFunctional("{ \"ports\": [ { \"id\": \"p\", \"resource\": \"Electricity\", \"direction\": \"In\" } ] }"); }
        catch (InvalidOperationException ex) { electricityPortExplains = ex.Message.Contains("nodes"); }
        Check(electricityPortExplains, "an old-style Electricity physical port fails with a message pointing at \"nodes\" (no silent acceptance)");

        Check(parsed.GetParam("mode") == "toggle" && parsed.GetParam("n") == "3",
            "behavior \"params\": a string value is returned as is, a number as its JSON text", $"mode={parsed.GetParam("mode")} n={parsed.GetParam("n")}");
        Check(parsed.GetParam("missing", "fallback") == "fallback" && MakeFunctional("{}").BehaviorParams.Count == 0,
            "a missing param falls back; a block without \"params\" has none");

        // ---- блок кнопки (фикстура: настоящий blocks/ - пользовательские данные, кнопку там могли переименовать/пересоздать)
        var fixture = BuildFixtureCatalog();
        if (!fixture.TryGetBySlug("fx_button", out var button))
        {
            Check(false, "the fixture catalog has the 'fx_button' block");
            return;
        }

        var buttonFn = button.GetComponent<FunctionalBlockComponent>();
        Check(buttonFn != null && button.GetComponent<BuildingBlockComponent>() == null,
            "'button' is a FunctionalBlock (not resizable)");
        if (buttonFn == null) return;

        Check(buttonFn.Behavior == ButtonBehavior.Key && BlockBehaviorRegistry.TryGet(buttonFn.Behavior, out _),
            "'button' declares behavior \"Button\", and that key resolves in the behavior registry", buttonFn.Behavior);
        string mode = buttonFn.GetParam("mode");
        Check(mode == "momentary" || mode == "toggle", "'button' has a valid \"mode\" param (momentary/toggle)", mode);
        Check(buttonFn.Ports.Count == 0, "'button' has no PHYSICAL ports (power and state are both nodes)", $"{buttonFn.Ports.Count}");
        Check(buttonFn.Nodes.Count(n => n.Type == NodeType.Electricity && n.Direction == PortDirection.In) == 1
              && buttonFn.Nodes.Count(n => n.Type == NodeType.Boolean && n.Direction == PortDirection.Out) == 1,
            "'button' has an Electricity INPUT node (power) and a Boolean OUTPUT node (its state)", $"{buttonFn.Nodes.Count} nodes");
        Check(FunctionalBlockComponent.FindNodeConflict(buttonFn.Nodes) == null, "...and they sit in one cell legitimately - different types");
        var realSettings = ButtonSettings.From(buttonFn);
        Check(realSettings.GlowNode.Length > 0 && buttonFn.GetParam("glowNode").Length > 0 && Color.HtmlIsValid(buttonFn.GetParam("glowColor")),
            "'button' declares glowNode and a readable glowColor in its params (exact values are editable data, not pinned here)",
            $"node='{buttonFn.GetParam("glowNode")}' color='{buttonFn.GetParam("glowColor")}'");
        Check(buttonFn.CollisionBoxes.Count > 0, "'button' has an explicit collision box (functional blocks get no automatic one)", $"{buttonFn.CollisionBoxes.Count}");

        // ---- поведение Button на синтетических определениях (режим реального файла не зашит)
        BlockBehaviorRegistry.TryGet(ButtonBehavior.Key, out var behavior);
        bool Read(BlockState s, string node = "out") => behavior.TryReadNode(s, node, out var v) && v.IsOn;
        const string signalOut = "\"nodes\": [ { \"id\": \"out\", \"type\": \"Boolean\", \"direction\": \"Out\" } ]";

        var toggleState = behavior.CreateState(MakeFunctional("{ \"params\": { \"mode\": \"toggle\" }, " + signalOut + " }"));
        Check(!Read(toggleState), "toggle button: starts off");
        behavior.Interact(toggleState, BlockInteraction.Press);
        Check(Read(toggleState), "toggle button: first press turns it on");
        behavior.Interact(toggleState, BlockInteraction.Press);
        Check(Read(toggleState), "toggle button: a repeated Press without Release is ignored (no flicker)");
        behavior.Interact(toggleState, BlockInteraction.Release);
        Check(Read(toggleState), "toggle button: release keeps it on");
        behavior.Interact(toggleState, BlockInteraction.Press);
        Check(!Read(toggleState), "toggle button: second press turns it off");
        behavior.Interact(toggleState, BlockInteraction.Release);
        Check(!Read(toggleState), "toggle button: release keeps it off");

        behavior.Interact(toggleState, BlockInteraction.Press);
        var toggleTyped = (ButtonState)toggleState;
        behavior.Interact(toggleState, BlockInteraction.Release);
        Check(toggleTyped.Toggled && !toggleTyped.Pressed && toggleTyped.Active && Read(toggleState),
            "toggle button: after press+release it is Toggled (latched) though no longer Pressed - Active/signal follow Toggled");

        var momentaryState = behavior.CreateState(MakeFunctional("{ \"params\": { \"mode\": \"momentary\" }, " + signalOut + " }"));
        behavior.Interact(momentaryState, BlockInteraction.Press);
        Check(Read(momentaryState), "momentary button: on while held");
        behavior.Interact(momentaryState, BlockInteraction.Release);
        Check(!Read(momentaryState), "momentary button: off on release");
        behavior.Interact(momentaryState, BlockInteraction.Release);
        Check(!Read(momentaryState), "momentary button: a stray Release changes nothing");

        var defaultState = (ButtonState)behavior.CreateState(MakeFunctional("{ " + signalOut + " }"));
        Check(defaultState.Mode == ButtonMode.Momentary, "no \"mode\" param: defaults to momentary");

        // ---- ButtonSettings: умолчания и явные значения (на синтетических определениях)
        var defaultSettings = ButtonSettings.From(MakeFunctional("{}"));
        Check(defaultSettings.Mode == ButtonMode.Momentary && defaultSettings.GlowNode == "Cap" && defaultSettings.GlowColor.IsEqualApprox(Color.FromHtml("#33ff55")),
            "ButtonSettings defaults: momentary, glowNode \"Cap\", glowColor #33ff55", $"{defaultSettings.Mode}/{defaultSettings.GlowNode}/{defaultSettings.GlowColor}");
        var explicitSettings = ButtonSettings.From(MakeFunctional("{ \"params\": { \"mode\": \"Toggle\", \"glowNode\": \"Lid\", \"glowColor\": \"#ff0000\" } }"));
        Check(explicitSettings.Mode == ButtonMode.Toggle && explicitSettings.GlowNode == "Lid" && explicitSettings.GlowColor.IsEqualApprox(Colors.Red),
            "ButtonSettings reads explicit mode (case-insensitive)/glowNode/glowColor", $"{explicitSettings.Mode}/{explicitSettings.GlowNode}/{explicitSettings.GlowColor}");
        var brokenSettings = ButtonSettings.From(MakeFunctional("{ \"params\": { \"glowNode\": \"  \", \"glowColor\": \"not-a-color\" } }"));
        Check(brokenSettings.GlowNode == "Cap" && brokenSettings.GlowColor.IsEqualApprox(ButtonSettings.DefaultGlowColor),
            "blank glowNode / unreadable glowColor fall back to the defaults instead of failing");
        var bogusState = (ButtonState)behavior.CreateState(MakeFunctional("{ \"params\": { \"mode\": \"bogus\" }, " + signalOut + " }"));
        Check(bogusState.Mode == ButtonMode.Momentary, "unknown \"mode\": falls back to momentary (with a logged warning)");

        behavior.Interact(momentaryState, BlockInteraction.Press);
        Check(!Read(momentaryState, "no_such_port"), "reading a node the block doesn't have reports false, not a value");
        var silentState = (ButtonState)behavior.CreateState(MakeFunctional("{ \"params\": { \"mode\": \"momentary\" } }"));
        behavior.Interact(silentState, BlockInteraction.Press);
        Check(silentState.OutputNodeId == null && !Read(silentState), "a button with no Out/Boolean node works but outputs nothing");

        Check(!BlockBehaviorRegistry.TryGet("ElectricMotor", out _) && !BlockBehaviorRegistry.TryGet("", out _),
            "behavior keys without an implementation (and the empty key) are simply not in the registry");

        // ---- FunctionalBlockRuntime: состояние на экземпляр, отдельно от BlockInstance
        var catalog = fixture;
        var construction = new Construction(new VoxelGrid());
        var plain = construction.Place(new Vector3I(0, 0, 0), catalog.Get("plain"), Colors.White)!;
        var motor = construction.Place(new Vector3I(2, 0, 0), catalog.Get("fx_motor"), Colors.White)!;
        var btnA = construction.Place(new Vector3I(4, 0, 0), catalog.Get("fx_button"), Colors.White)!;

        using var runtime = new FunctionalBlockRuntime(construction, catalog);
        Check(runtime.Count == 1 && runtime.HasState(btnA.InstanceId) && !runtime.HasState(plain.InstanceId) && !runtime.HasState(motor.InstanceId),
            "runtime keeps state only for blocks with a registered behavior (button), not for plain blocks or behaviors without an implementation",
            $"count={runtime.Count}");
        Check(runtime.GetState<ButtonState>(btnA.InstanceId) != null && runtime.GetState<ButtonState>(plain.InstanceId) == null,
            "GetState<ButtonState> returns the button's state, null for instances without one");
        Check(!runtime.Interact(plain.InstanceId, BlockInteraction.Press) && !runtime.Interact(12345, BlockInteraction.Press),
            "Interact on an instance without state (or an unknown id) reports false");

        bool Signal(int id) => runtime.TryReadNode(id, "signal_out", out var v) && v.IsOn;
        Check(!Signal(btnA.InstanceId), "a fresh button's output is off");
        Check(runtime.Interact(btnA.InstanceId, BlockInteraction.Press) && Signal(btnA.InstanceId), "Press via the runtime turns the output on (both modes)");

        var btnB = construction.Place(new Vector3I(6, 0, 0), catalog.Get("fx_button"), Colors.White)!;
        Check(runtime.Count == 2 && Signal(btnA.InstanceId) && !Signal(btnB.InstanceId),
            "a second button gets its own state; placing it did not reset the first (state is per instance)", $"count={runtime.Count}");

        runtime.Interact(btnA.InstanceId, BlockInteraction.Release);
        bool toggleMode = runtime.GetState<ButtonState>(btnA.InstanceId)!.Mode == ButtonMode.Toggle;
        Check(Signal(btnA.InstanceId) == toggleMode, "Release: momentary -> off, toggle -> stays on (whatever mode blocks/button.xml has)", $"mode={mode}");
        runtime.Tick(0.016); // шаг без поведения Tick у кнопки - просто не должен ничего ломать
        Check(Signal(btnA.InstanceId) == toggleMode, "Tick does not change the button's signal");

        // Питание: ЗАГЛУШКА (ResolvePowered -> DebugForcePowered), состояние powered живёт в ButtonState и обновляется на Tick.
        var btnBState = runtime.GetState<ButtonState>(btnB.InstanceId)!;
        Check(!btnBState.Powered, "a fresh button is unpowered (the power stub is off by default)");
        runtime.DebugForcePowered = true;
        runtime.Tick(0.016);
        Check(btnBState.Powered && runtime.GetState<ButtonState>(btnA.InstanceId)!.Powered, "DebugForcePowered powers every stateful block on the next Tick");
        Check(Signal(btnA.InstanceId) == toggleMode, "...and power does not change the signal (it only drives the glow)");
        runtime.DebugForcePowered = false;
        runtime.Tick(0.016);
        Check(!btnBState.Powered, "...and switching it off unpowers them again");

        runtime.InteractAll<ButtonBehavior>(BlockInteraction.Press);
        Check(Signal(btnA.InstanceId) && Signal(btnB.InstanceId), "InteractAll<ButtonBehavior>(Press) presses every button (debug F3)");
        runtime.InteractAll<ButtonBehavior>(BlockInteraction.Release);
        Check(!Signal(btnB.InstanceId) || btnBState.Mode == ButtonMode.Toggle, "InteractAll(Release) releases them (a toggle button stays latched)");

        construction.Remove(btnA);
        Check(runtime.Count == 1 && !runtime.HasState(btnA.InstanceId) && runtime.HasState(btnB.InstanceId),
            "removing the instance drops its state; the other button keeps its own", $"count={runtime.Count}");

        var btnC = construction.Place(new Vector3I(4, 0, 0), catalog.Get("fx_button"), Colors.White)!;
        Check(btnC.InstanceId != btnA.InstanceId && runtime.HasState(btnC.InstanceId) && !Signal(btnC.InstanceId),
            "a new button on the freed cell is a new instance with a fresh (off) state - nothing inherited from the removed one");

        runtime.Dispose();
        construction.Place(new Vector3I(8, 0, 0), catalog.Get("fx_button"), Colors.White);
        Check(runtime.Count == 2, "after Dispose the runtime no longer follows the construction", $"count={runtime.Count}");
    }

    /// <summary>Спавн постройки с кнопкой: у тела есть рантайм с состоянием кнопки, а коллизия кнопки — ровно её явные боксы.</summary>
    private void RunFunctionalRuntimeSpawnTest(Node host)
    {
        GD.Print("-- VehicleSpawner: the spawned body carries a FunctionalBlockRuntime");

        var catalog = BlockCatalog.Instance;
        // Кнопка (поведение Button), если в каталоге она есть; иначе - любой функциональный блок. Что именно - пользовательские данные.
        var def = FindFunctional(f => f.Behavior == ButtonBehavior.Key) ?? FindFunctional();
        if (def == null)
        {
            Check(true, "runtime spawn check skipped: no functional block in the catalog right now");
            return;
        }

        var fn = FunctionalOf(def)!;
        bool hasBehavior = BlockBehaviorRegistry.TryGet(fn.Behavior, out _);
        var source = new Construction(new VoxelGrid());
        source.Place(new Vector3I(0, 0, 0), catalog.Get("block"), Colors.White);
        source.PlaceBlock(new Vector3I(3, 0, 0), fn.Footprint, def, Colors.White);

        var parent = new Node3D();
        host.AddChild(parent);
        var body = VehicleSpawner.Spawn(parent, ConstructionIO.Serialize(source), Vector3.Zero);

        Check(body.Runtime != null && body.Runtime.Count == (hasBehavior ? 1 : 0),
            "the spawned vehicle has a runtime holding state exactly for blocks whose behavior is implemented", $"count={body.Runtime?.Count} hasBehavior={hasBehavior}");

        var spawnedWorld = body.GetChildren().OfType<VoxelWorld>().FirstOrDefault();
        var spawnedInstance = spawnedWorld?.Construction.Instances.FirstOrDefault(i => i.BlockSlug == def.Slug);
        Check(spawnedInstance != null && body.Runtime != null && body.Runtime.HasState(spawnedInstance.InstanceId) == hasBehavior,
            "...and the state is keyed by the block's instance id in the spawned construction");

        var outNode = fn.Nodes.FirstOrDefault(n => n.Direction == PortDirection.Out && n.Type == NodeType.Boolean);
        if (hasBehavior && fn.Behavior == ButtonBehavior.Key && spawnedInstance != null && body.Runtime != null && outNode != null)
        {
            body.Runtime.Interact(spawnedInstance.InstanceId, BlockInteraction.Press);
            Check(body.Runtime.TryReadNode(spawnedInstance.InstanceId, outNode.Id, out var pressed) && pressed.IsOn,
                "pressing the spawned button raises its output node");
        }

        int shapeCount = body.GetChildren().Count(c => c is CollisionShape3D);
        Check(shapeCount == 1 + fn.CollisionBoxes.Count, "collision: 1 automatic box for 'block' + exactly the functional block's own explicit boxes",
            $"got {shapeCount}, expected {1 + fn.CollisionBoxes.Count}");

        body.QueueFree();
        parent.QueueFree();
    }

    // ================================================================== визуал кнопки (ButtonVisual): ход нажатия + подсветка

    /// <summary>Модель кнопки из кода (без ассетов): корень, узел-крышка <paramref name="capName"/> с общим материалом и
    /// (опционально) AnimationPlayer с клипами, двигающими крышку вниз на <c>endY</c> за 0.2 с.</summary>
    private static Node3D BuildButtonTestModel(StandardMaterial3D sharedMaterial, string capName = "Cap", params (string Clip, float EndY)[] clips)
    {
        var root = new Node3D { Name = "ButtonModel" };
        root.AddChild(new MeshInstance3D { Name = capName, Mesh = new BoxMesh { Size = new Vector3(0.1f, 0.02f, 0.1f), Material = sharedMaterial } });

        if (clips.Length > 0)
        {
            var library = new AnimationLibrary();
            foreach (var (clip, endY) in clips)
            {
                var animation = new Animation { Length = 0.2f };
                int track = animation.AddTrack(Animation.TrackType.Value);
                animation.TrackSetPath(track, new NodePath($"{capName}:position"));
                animation.TrackInsertKey(track, 0.0, Vector3.Zero);
                animation.TrackInsertKey(track, 0.2, new Vector3(0, endY, 0));
                library.AddAnimation(clip, animation);
            }

            var player = new AnimationPlayer { Name = "AnimationPlayer" };
            root.AddChild(player);
            player.AddAnimationLibrary("", library);
        }

        return root;
    }

    private void RunButtonVisualTests(Node host)
    {
        GD.Print("-- ButtonVisual: press progress drives one clip, glow = powered x progress (separate channels)");

        var settings = new ButtonSettings(ButtonMode.Momentary, "Cap", ButtonSettings.DefaultGlowColor);
        var shared = new StandardMaterial3D { AlbedoColor = Colors.Gray };
        const double step = 0.025; // 0.025 * PressSpeed(8) = 0.2 прогресса на шаг
        const float endY = -0.01f;
        var created = new List<Node>();

        (Node3D Root, ButtonVisual Visual) Make(StandardMaterial3D material, string cap = "Cap", params (string, float)[] clips)
        {
            var root = BuildButtonTestModel(material, cap, clips);
            host.AddChild(root);
            created.Add(root);
            return (root, new ButtonVisual(root, settings));
        }

        double CapY(Node3D root) => ((Node3D)root.GetNode("Cap")).Position.Y;
        var glowOf = (ButtonVisual v) => v.GlowMaterials[0].EmissionEnergyMultiplier;

        // ---- обесточенная кнопка: ход есть, свечения нет
        var (rootA, a) = Make(shared, "Cap", ("press", endY));
        Check(a.HasAnimation && a.HasGlow && a.PressProgress == 0 && a.GlowBrightness == 0, "a fresh visual has the clip and the cap material, at rest (progress 0, glow 0)");
        Check(!ReferenceEquals(a.GlowMaterials[0], shared) && !shared.EmissionEnabled && a.GlowMaterials[0].EmissionEnabled,
            "the cap material is a per-instance COPY (the shared material of the imported scene is left untouched)");
        Check(a.GlowMaterials[0].Emission.IsEqualApprox(settings.GlowColor), "...with the configured glow color as Emission");

        a.Update(step, false, false);
        Check(a.PressProgress == 0, "not pressed: progress stays at 0");

        a.Update(step, true, false);
        Check(Math.Abs(a.PressProgress - 0.2) < 1e-9, "pressing moves progress toward 1 at PressSpeed, not instantly", $"{a.PressProgress}");
        Check(Math.Abs(CapY(rootA) - endY * 0.2) < 1e-5, "...and Seek(progress x length) puts the clip at that fraction (cap moved 20% of the way down)", $"{CapY(rootA)}");
        for (int i = 0; i < 6; i++) a.Update(step, true, false);
        Check(a.PressProgress == 1.0 && Math.Abs(CapY(rootA) - endY) < 1e-5, "fully pressed: progress 1, cap at the clip's end pose", $"{a.PressProgress} {CapY(rootA)}");
        Check(a.GlowBrightness == 0 && glowOf(a) == 0f, "UNPOWERED: the cap is pressed all the way but does not glow (emission stays 0)");

        for (int i = 0; i < 7; i++) a.Update(step, false, false);
        Check(a.PressProgress == 0 && Math.Abs(CapY(rootA)) < 1e-5, "released: progress returns to 0 and the clip plays back to the rest pose");

        // ---- прерванное нажатие разворачивается плавно, без рывка
        var (rootC, c) = Make(shared, "Cap", ("press", endY));
        c.Update(step, true, false);
        c.Update(step, true, false);
        double beforeReverse = c.PressProgress;
        c.Update(step, false, false);
        Check(Math.Abs(beforeReverse - 0.4) < 1e-9 && Math.Abs(c.PressProgress - 0.2) < 1e-9 && Math.Abs(CapY(rootC) - endY * 0.2) < 1e-5,
            "an interrupted press reverses from where it is (0.4 -> 0.2), no jump to either end", $"{beforeReverse} -> {c.PressProgress}");

        // ---- запитанная: свечение растёт вместе с прогрессом, но не обгоняет его
        var (_, b) = Make(shared, "Cap", ("press", endY));
        bool glowFollows = true;
        double previousGlow = 0;
        for (int i = 0; i < 6; i++)
        {
            b.Update(step, true, true);
            glowFollows &= b.GlowBrightness > previousGlow && b.GlowBrightness <= b.PressProgress + 1e-9;
            previousGlow = b.GlowBrightness;
        }

        Check(glowFollows, "POWERED + pressed: glow grows every step and never runs ahead of the press progress");
        for (int i = 0; i < 10; i++) b.Update(step, true, true);
        Check(b.GlowBrightness == 1.0 && Math.Abs(glowOf(b) - ButtonVisual.MaxEmissionEnergy) < 1e-4,
            "...and settles at full brightness = EmissionEnergyMultiplier MaxEmissionEnergy", $"{b.GlowBrightness} {glowOf(b)}");

        // ---- питание пропало посреди нажатия: свечение гаснет плавно (progress остаётся 1)
        b.Update(step, true, false);
        Check(b.PressProgress == 1.0 && b.GlowBrightness > 0.5 && b.GlowBrightness < 1.0, "power lost while still pressed: glow starts fading, it does not snap to 0", $"{b.GlowBrightness}");
        for (int i = 0; i < 12; i++) b.Update(step, true, false);
        Check(b.GlowBrightness == 0 && b.PressProgress == 1.0, "...and fades out completely while the cap stays pressed");

        // ---- два экземпляра с ОБЩИМ материалом светятся независимо
        var (_, d) = Make(shared, "Cap", ("press", endY));
        var (_, e) = Make(shared, "Cap", ("press", endY));
        for (int i = 0; i < 10; i++) d.Update(step, true, true);
        for (int i = 0; i < 10; i++) e.Update(step, false, true);
        Check(glowOf(d) > 1f && glowOf(e) == 0f && !ReferenceEquals(d.GlowMaterials[0], e.GlowMaterials[0]),
            "two instances built from the same shared material glow independently (one lit, one dark, separate material copies)", $"{glowOf(d)} / {glowOf(e)}");
        Check(shared.EmissionEnergyMultiplier == 1f && !shared.EmissionEnabled, "...and the shared original never changed");

        // ---- клип: "press" имеет приоритет, иначе первый не-RESET
        var (rootP, p) = Make(shared, "Cap", ("foo", -0.5f), ("press", endY));
        for (int i = 0; i < 7; i++) p.Update(step, true, false);
        Check(Math.Abs(CapY(rootP) - endY) < 1e-5, "with both \"foo\" and \"press\" present, \"press\" is the clip that is driven", $"{CapY(rootP)}");
        var (rootF, f) = Make(shared, "Cap", ("RESET", 0f), ("foo", -0.02f));
        for (int i = 0; i < 7; i++) f.Update(step, true, false);
        Check(f.HasAnimation && Math.Abs(CapY(rootF) + 0.02) < 1e-5, "without \"press\", the first non-RESET clip is used", $"{CapY(rootF)}");

        // ---- нет AnimationPlayer / нет узла-крышки: просто без соответствующего канала, без ошибок
        var (_, bare) = Make(shared, "Other");
        Check(!bare.HasAnimation && !bare.HasGlow, "no AnimationPlayer and no glowNode: the visual reports no animation and no glow");
        bare.Update(step, true, true);
        bare.Update(step, true, true);
        Check(Math.Abs(bare.PressProgress - 0.4) < 1e-9, "...yet it still updates without errors and keeps counting progress", $"{bare.PressProgress}");
        var (_, noPlayer) = Make(shared, "Cap");
        Check(!noPlayer.HasAnimation && noPlayer.HasGlow, "a model with a cap but no AnimationPlayer still glows (channels are independent)");

        foreach (var node in created) node.QueueFree();
    }

    // ================================================================== поворот блока с фиксированным footprint'ом вокруг корневой клетки

    /// <summary>
    /// Блок с footprint'ом больше одной клетки (батарея 2×1×1) крутится вокруг КОРНЕВОЙ клетки (под курсором), а не вокруг
    /// центра своего хитбокса, и занятые клетки поворачиваются вместе с ним (<see cref="BlockFootprint"/>). Проверяется на
    /// чистой математике для всех 64 комбинаций ступеней (в каталоге нет блока с footprint'ом > 1, а каталог менять
    /// в тесте нельзя): занятые клетки, рамка модели/коллизии и жёсткий поворот призрака вокруг корня.
    /// </summary>
    private void RunFootprintRotationTests()
    {
        GD.Print("-- fixed-footprint blocks rotate around the ROOT cell (occupied cells rotate with the block)");

        var rootCell = new Vector3I(5, 3, 7);
        const float cell = BuildSpace.CellSize;
        var footprints = new[] { new Vector3I(2, 1, 1), new Vector3I(3, 2, 1), new Vector3I(2, 1, 3), Vector3I.One };

        bool cellsMatch = true, rootInside = true, volumeKept = true, sizePermutes = true, frameAgrees = true, cellCentersMap = true, staysInside = true;
        string firstProblem = "";
        void Note(ref bool flag, bool ok, string what)
        {
            if (ok) return;
            if (flag) firstProblem = what;
            flag = false;
        }

        foreach (var footprint in footprints)
        for (int x = 0; x < 4; x++)
        for (int y = 0; y < 4; y++)
        for (int z = 0; z < 4; z++)
        {
            var steps = new Vector3I(x, y, z);
            var (origin, size) = BlockFootprint.PlaceBox(rootCell, footprint, steps);
            string tag = $"footprint={footprint} steps={steps}";

            // Занятые клетки = корень + повёрнутые локальные клетки.
            var expected = new HashSet<Vector3I>();
            for (int cz = 0; cz < footprint.Z; cz++)
            for (int cy = 0; cy < footprint.Y; cy++)
            for (int cx = 0; cx < footprint.X; cx++)
            {
                expected.Add(rootCell + BlockFootprint.Rotate(new Vector3I(cx, cy, cz), steps));
            }

            var occupied = new HashSet<Vector3I>();
            for (int oz = 0; oz < size.Z; oz++)
            for (int oy = 0; oy < size.Y; oy++)
            for (int ox = 0; ox < size.X; ox++) occupied.Add(origin + new Vector3I(ox, oy, oz));

            Note(ref cellsMatch, occupied.SetEquals(expected), tag);
            Note(ref rootInside, occupied.Contains(rootCell), tag);
            Note(ref volumeKept, size.X * size.Y * size.Z == footprint.X * footprint.Y * footprint.Z, tag);
            var sortedSize = new[] { size.X, size.Y, size.Z }.OrderBy(v => v).ToArray();
            var sortedFootprint = new[] { footprint.X, footprint.Y, footprint.Z }.OrderBy(v => v).ToArray();
            Note(ref sizePermutes, sortedSize.SequenceEqual(sortedFootprint), tag);

            // Рамка ПОСТАВЛЕННОГО экземпляра (по центру занятого бокса) совпадает с рамкой призрака (по корню) - тот же результат.
            var rotation = ShapeMeshBuilder.ComposeRotation(steps);
            var instanceFrame = FunctionalBlockGeometry.InstanceFrame(origin, size, footprint, steps);
            var rootFrame = FunctionalBlockGeometry.RootFrame(rootCell, rotation);
            Note(ref frameAgrees, instanceFrame.IsEqualApprox(rootFrame), tag);

            // Центр КАЖДОЙ локальной клетки модели/коллизии попадает в центр соответствующей занятой клетки.
            for (int cz = 0; cz < footprint.Z; cz++)
            for (int cy = 0; cy < footprint.Y; cy++)
            for (int cx = 0; cx < footprint.X; cx++)
            {
                var local = new Vector3I(cx, cy, cz);
                var worldPoint = instanceFrame * ((new Vector3(cx, cy, cz) + new Vector3(0.5f, 0.5f, 0.5f)) * cell);
                Note(ref cellCentersMap, worldPoint.IsEqualApprox(BuildSpace.CellCenter(rootCell + BlockFootprint.Rotate(local, steps))), tag + $" cell={local}");
            }

            // Весь неповёрнутый хитбокс после поворота лежит внутри занятого бокса (ничего не торчит в соседние клетки).
            var boxMin = BuildSpace.CellMin(origin);
            var boxMax = BuildSpace.CellMin(origin + size);
            for (int corner = 0; corner < 8; corner++)
            {
                var localCorner = new Vector3(
                    (corner & 1) != 0 ? footprint.X : 0, (corner & 2) != 0 ? footprint.Y : 0, (corner & 4) != 0 ? footprint.Z : 0) * cell;
                var w = instanceFrame * localCorner;
                const float eps = 1e-4f;
                bool inside = w.X >= boxMin.X - eps && w.Y >= boxMin.Y - eps && w.Z >= boxMin.Z - eps
                              && w.X <= boxMax.X + eps && w.Y <= boxMax.Y + eps && w.Z <= boxMax.Z + eps;
                Note(ref staysInside, inside, tag);
            }
        }

        Check(cellsMatch, "all 64 rotations x 4 footprints: occupied cells == root + rotated local cells", firstProblem);
        Check(rootInside, "the root cell is always occupied (it is the pivot and never moves out of the block)", firstProblem);
        Check(volumeKept && sizePermutes, "the occupied box is the footprint with its axes permuted - same volume, no stretching", firstProblem);
        Check(frameAgrees, "the frame of a PLACED instance (centered on its occupied box) equals the ghost's root-pivot frame", firstProblem);
        Check(cellCentersMap, "every local cell of the model/collision lands exactly in the center of its occupied cell", firstProblem);
        Check(staysInside, "the whole rotated hitbox stays inside the occupied box (nothing sticks out into neighbor cells)", firstProblem);

        // То же правило для РЕЗИНОВЫХ блоков (любой локальный размер): повёрнутый размер и обратное преобразование согласованы.
        bool sizesRoundTrip = true, boxMatchesRotatedSize = true;
        foreach (var localSize in new[] { new Vector3I(3, 1, 1), new Vector3I(2, 3, 4), new Vector3I(1, 5, 2), Vector3I.One })
        for (int x = 0; x < 4; x++)
        for (int y = 0; y < 4; y++)
        for (int z = 0; z < 4; z++)
        {
            var steps = new Vector3I(x, y, z);
            sizesRoundTrip &= BlockFootprint.UnrotatedSize(BlockFootprint.RotatedSize(localSize, steps), steps) == localSize;
            boxMatchesRotatedSize &= BlockFootprint.PlaceBox(rootCell, localSize, steps).Size == BlockFootprint.RotatedSize(localSize, steps);
        }

        Check(sizesRoundTrip, "UnrotatedSize(RotatedSize(size)) == size for 4 sizes x 64 rotations (the local size is always recoverable from the occupied box)");
        Check(boxMatchesRotatedSize, "PlaceBox(...).Size == RotatedSize(...) - one rule for the occupied box of every block");

        // Конкретные случаи.
        var identityBox = BlockFootprint.PlaceBox(rootCell, new Vector3I(2, 1, 1), Vector3I.Zero);
        Check(identityBox.Origin == rootCell && identityBox.Size == new Vector3I(2, 1, 1), "no rotation: occupied box is exactly (root, footprint), as before");
        var aboutX = BlockFootprint.PlaceBox(rootCell, new Vector3I(2, 1, 1), new Vector3I(1, 0, 0));
        Check(aboutX.Origin == rootCell && aboutX.Size == new Vector3I(2, 1, 1), "a 2x1x1 block turned around its own long axis (X) occupies the same cells");
        var aboutY = BlockFootprint.PlaceBox(rootCell, new Vector3I(2, 1, 1), new Vector3I(0, 1, 0));
        Check(aboutY.Size == new Vector3I(1, 1, 2) && (aboutY.Origin.Z == rootCell.Z || aboutY.Origin.Z + 1 == rootCell.Z) && aboutY.Origin.X == rootCell.X,
            "a 2x1x1 block turned 90 degrees around Y becomes 1x1x2 and keeps the root cell as one of its two cells", $"{aboutY}");
        var aboutYTwice = BlockFootprint.PlaceBox(rootCell, new Vector3I(2, 1, 1), new Vector3I(0, 2, 0));
        Check(aboutYTwice.Size == new Vector3I(2, 1, 1) && aboutYTwice.Origin.X == rootCell.X - 1,
            "turned 180 degrees it extends the OTHER way from the root (X-1..X), not around its center", $"{aboutYTwice}");

        // Призрак: поворот жёсткий вокруг корня - при ЛЮБОМ (в т.ч. не кратном 90°) промежуточном базисе центр корневой клетки на месте.
        var halfway = new Basis(Vector3.Up, Mathf.Pi / 4f);
        var rootCenterLocal = new Vector3(0.5f, 0.5f, 0.5f) * cell;
        Check((FunctionalBlockGeometry.RootFrame(rootCell, halfway) * rootCenterLocal).IsEqualApprox(BuildSpace.CellCenter(rootCell)),
            "ghost mid-animation (45 degrees): the root cell center does not move - the block swings around it");
        var farLocal = new Vector3(1.5f, 0.5f, 0.5f) * cell;
        Check(!(FunctionalBlockGeometry.RootFrame(rootCell, halfway) * farLocal).IsEqualApprox(BuildSpace.CellCenter(rootCell + new Vector3I(1, 0, 0))),
            "...while the second cell has swung away from its unrotated position");
    }

    // ================================================================== резиновые блоки поворачиваются вместе с размером; их меш и TrySetSize

    private void RunRotatedResizableBlockTests(Node host)
    {
        GD.Print("-- resizable blocks (cube/wedge) turn together with their size around the root cell");

        var catalog = BlockCatalog.Instance;
        var wedge = catalog.Get("wedge");
        var root = new Vector3I(2, 0, 5);
        var steps = new Vector3I(0, 1, 0);
        var box = BlockFootprint.PlaceBox(root, new Vector3I(3, 1, 1), steps);
        Check(box.Size == new Vector3I(1, 1, 3), "a 3x1x1 wedge turned 90 degrees around Y occupies 1x1x3", $"{box}");

        // Поставленный повёрнутый клин рисуется ShapeInstanceView ровно в занятом боксе (а не растянутым заново в осях мира).
        var world = new VoxelWorld();
        host.AddChild(world);
        var instance = world.Construction.PlaceBlock(box.Origin, box.Size, wedge, Colors.White, steps);
        Check(instance != null, "setup: the rotated 3x1x1 wedge is placed with its rotated box");
        world.RebuildDirty();

        var shapeMeshes = world.FindChildren("Shape_*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToList();
        Check(shapeMeshes.Count == 1, "...and has one shape mesh", $"{shapeMeshes.Count}");
        if (shapeMeshes.Count == 1 && shapeMeshes[0].Mesh != null)
        {
            var aabb = shapeMeshes[0].Mesh.GetAabb();
            var expectedExtent = new Vector3(box.Size.X, box.Size.Y, box.Size.Z) * BuildSpace.CellSize;
            Check(aabb.Size.IsEqualApprox(expectedExtent) && shapeMeshes[0].Position.IsEqualApprox(BuildSpace.CellMin(box.Origin)),
                "the placed wedge's mesh fills exactly the rotated occupied box, at its origin",
                $"size={aabb.Size} expected={expectedExtent} pos={shapeMeshes[0].Position}");
        }

        world.QueueFree();

        // TrySetSize на повёрнутом блоке: новый размер - ЗАНЯТОГО бокса, границы Min/MaxSize применяются к ЛОКАЛЬНОМУ размеру.
        var construction = new Construction(new VoxelGrid());
        var rotated = construction.PlaceBlock(root, new Vector3I(1, 1, 3), wedge, Colors.White, steps)!;
        Check(construction.TrySetSize(rotated, wedge, new Vector3I(1, 1, 5)) && rotated.Size == new Vector3I(1, 1, 5),
            "TrySetSize on a rotated block takes the occupied box (grows along the rotated axis)", $"{rotated.Size}");
        var maxLocal = wedge.GetComponent<BuildingBlockComponent>()!.MaxSize;
        construction.TrySetSize(rotated, wedge, new Vector3I(1, 1, 100));
        Check(BlockFootprint.UnrotatedSize(rotated.Size, steps).X == Math.Max(1, maxLocal.X),
            "...and MaxSize is applied to the LOCAL size (the rotated Z axis is the block's X)", $"{rotated.Size} max={maxLocal}");
    }

    // ================================================================== призрак: конфликт подсвечивается красным, а не пропадает

    /// <summary>
    /// Баг (найден пользователем): повернув блок так, что его бокс упирался в другие блоки, призрак просто пропадал. Теперь призрак
    /// остаётся на месте, а конфликтные клетки подсвечиваются красным полупрозрачным оверлеем. Заодно сквозная проверка поворота
    /// резинового блока вокруг корневой клетки: призрак занимает ровно <see cref="BlockFootprint.PlaceBox"/>, ЛКМ ставит блок в
    /// этот бокс. Ожидания считаются из текущей сетки и PlaceBox, а не зашиты.
    /// </summary>
    private async Task RunPlacementConflictTests(BuildEditor editor)
    {
        GD.Print("-- editor: a conflicting ghost stays visible with a red overlay; rotation turns a resized block around the root cell");

        var state = editor.State;
        var camera = editor.EditorCamera;
        var construction = editor.World.Construction;
        var catalog = BlockCatalog.Instance;
        bool accumulated = Input.UseAccumulatedInput;
        Input.UseAccumulatedInput = false;

        construction.Clear();
        state.Tool = ToolMode.None;
        state.ResetPendingRotation();
        construction.Place(Vector3I.Zero, catalog.Get("block"), Colors.White);
        construction.Place(new Vector3I(1, 1, 0), catalog.Get("block"), Colors.White); // препятствия вокруг клетки (0,1,0) над корнем
        construction.Place(new Vector3I(0, 1, 1), catalog.Get("block"), Colors.White);

        state.SetSlot(0, "block");
        state.SelectedSlot = 0;
        state.SetPendingSizeAxis(0, 3);
        state.SetPendingSizeAxis(1, 1);
        state.SetPendingSizeAxis(2, 1);
        var localSize = new Vector3I(3, 1, 1);
        var rootCell = new Vector3I(0, 1, 0); // корневая клетка призрака: над блоком (0,0,0)

        // Камера строго сверху на верхнюю грань корневого блока: курсор целится в клетку (0,1,0).
        camera.LookAtPoint(new Vector3(0.125f, 5f, 0.125f), new Vector3(0.125f, 0f, 0.125f));
        await Frames(editor, 2);
        var aim = camera.UnprojectPosition(new Vector3(0.125f, 0.25f, 0.125f));
        await Move(editor, aim);
        Check(editor.Hover.Found && editor.Hover.PlaceCell == rootCell, "setup: the cursor aims at the cell above the root block", $"{editor.Hover}");

        bool sawConflict = false, sawClear = false, ghostAlwaysVisible = true, overlayMatches = true, ghostFitsBox = true;
        string firstProblem = "";
        async Task CheckCurrentOrientation(string label)
        {
            // Ждём, пока анимация поворота призрака осядет на целевой ориентации.
            for (int i = 0; i < 60 && !editor.GhostVisualBasis.IsEqualApprox(state.PendingRotationBasis); i++) await Frames(editor, 1);
            await Frames(editor, 2);

            var box = BlockFootprint.PlaceBox(rootCell, localSize, state.PendingRotationSteps);
            var expectedBlocked = new HashSet<Vector3I>();
            for (int oz = 0; oz < box.Size.Z; oz++)
            for (int oy = 0; oy < box.Size.Y; oy++)
            for (int ox = 0; ox < box.Size.X; ox++)
            {
                var c = box.Origin + new Vector3I(ox, oy, oz);
                if (!BuildSpace.InBounds(c) || construction.Grid.IsSolid(c)) expectedBlocked.Add(c);
            }

            if (expectedBlocked.Count > 0) sawConflict = true; else sawClear = true;
            if (!editor.Ghost.Visible) { if (ghostAlwaysVisible) firstProblem = label; ghostAlwaysVisible = false; }
            bool overlayOk = editor.ConflictOverlayVisible == (expectedBlocked.Count > 0) && expectedBlocked.SetEquals(editor.PlacementConflictCells);
            if (!overlayOk) { if (overlayMatches) firstProblem = label; overlayMatches = false; }

            var worldAabb = editor.Ghost.GlobalTransform * editor.Ghost.Mesh.GetAabb();
            var expectedExtent = new Vector3(box.Size.X, box.Size.Y, box.Size.Z) * BuildSpace.CellSize;
            bool fits = worldAabb.Position.IsEqualApprox(BuildSpace.CellMin(box.Origin)) && worldAabb.Size.IsEqualApprox(expectedExtent);
            if (!fits) { if (ghostFitsBox) firstProblem = label + $" ghost={worldAabb} expected={BuildSpace.CellMin(box.Origin)}+{expectedExtent}"; ghostFitsBox = false; }
        }

        int blocksBefore = construction.Instances.Count;
        await CheckCurrentOrientation("unrotated");
        Check(editor.ConflictOverlayVisible, "unrotated 3x1x1 runs into a neighbor: the overlay is shown and the ghost is still there", $"ghost={editor.Ghost.Visible}");
        await Click(editor, aim, MouseButton.Left);
        Check(construction.Instances.Count == blocksBefore, "LMB on a conflicting ghost places nothing");

        var turns = new Action[]
        {
            state.RotatePendingY, state.RotatePendingY, state.RotatePendingY, state.RotatePendingY, state.RotatePendingX, state.RotatePendingZ,
        };
        for (int i = 0; i < turns.Length; i++)
        {
            turns[i]();
            await CheckCurrentOrientation($"turn #{i + 1} -> steps {state.PendingRotationSteps}");
        }

        Check(ghostAlwaysVisible, "through every rotation the ghost never disappears, conflict or not", firstProblem);
        Check(overlayMatches, "...and the red overlay marks exactly the occupied/out-of-area cells of the rotated box", firstProblem);
        Check(ghostFitsBox, "...and the ghost fills exactly the rotated occupied box around the root cell (size turns with the block)", firstProblem);
        Check(sawConflict && sawClear, "the sequence covered both a conflicting and a free orientation");

        // Поставить в свободной ориентации: блок встаёт в повёрнутый бокс.
        state.ResetPendingRotation();
        state.RotatePendingY();
        await CheckCurrentOrientation("final");
        var expectedBox = BlockFootprint.PlaceBox(rootCell, localSize, state.PendingRotationSteps);
        bool clear = editor.PlacementConflictCells.Count == 0;
        Check(clear, "setup: a free orientation for the final placement", string.Join(",", editor.PlacementConflictCells));
        if (clear)
        {
            await Click(editor, aim, MouseButton.Left);
            var placed = construction.GetOwner(expectedBox.Origin);
            Check(placed != null && placed.Origin == expectedBox.Origin && placed.Size == expectedBox.Size && placed.RotationSteps == state.PendingRotationSteps,
                "LMB places the resized block into the rotated occupied box with its rotation", placed == null ? "nothing placed" : $"{placed.Origin} {placed.Size}");
        }

        // Восстановить состояние для остальных тестов.
        state.ResetPendingRotation();
        for (int axis = 0; axis < 3; axis++) state.SetPendingSizeAxis(axis, 1);
        construction.Clear();
        Input.UseAccumulatedInput = accumulated;

        // --- вход в редактор: курсор до первого события мыши берётся у ОС (баг "курсор не виден, пока не шевельнёшь колесом")
        var viewport = new Rect2(0, 0, 1600, 900);
        Check(BuildEditor.CursorPositionInViewport(new Vector2I(1100, 700), new Vector2I(100, 50), viewport) == new Vector2(1000, 650),
            "cursor polling: OS screen position minus the window origin gives the viewport position");
        Check(BuildEditor.CursorPositionInViewport(new Vector2I(-30, 20), new Vector2I(100, 50), viewport) == null
              && BuildEditor.CursorPositionInViewport(new Vector2I(5000, 20), Vector2I.Zero, viewport) == null,
            "...and a cursor outside the window gives null (the previous position is kept)");

        // --- цвет редактора и освещение
        var environment = editor.GetNodeOrNull<WorldEnvironment>("Environment")?.Environment;
        var skyMaterial = environment?.Sky?.SkyMaterial as ProceduralSkyMaterial;
        Check(skyMaterial != null && skyMaterial.SkyTopColor.IsEqualApprox(Color.FromHtml("#238baf")) && skyMaterial.GroundBottomColor.IsEqualApprox(Color.FromHtml("#238baf")),
            "the editor background is #238baf (flat sky and ground)");
        Check(environment != null && environment.AmbientLightSource == Godot.Environment.AmbientSource.Color && environment.AmbientLightColor.IsEqualApprox(Colors.White),
            "the editor's ambient light is white, not tinted by the sky");
        Check(editor.GetNodeOrNull<DirectionalLight3D>("Sun") is { } sun && sun.LightColor.IsEqualApprox(Colors.White), "...and so is the sun");

        var probe = new Node3D();
        editor.AddChild(probe);
        EnvironmentBuilder.BuildFlatSkyAndSun(probe, Colors.Red);
        var worldEnvironment = probe.GetNodeOrNull<WorldEnvironment>("Environment")?.Environment;
        Check(worldEnvironment != null && worldEnvironment.AmbientLightSource == Godot.Environment.AmbientSource.Sky,
            "the shared sky builder still takes ambient light from the sky by default (the open world is unchanged)");
        probe.QueueFree();
    }
}
