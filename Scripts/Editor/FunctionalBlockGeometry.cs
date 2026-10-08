using System;
using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Editor;

/// <summary>
/// Общая геометрия для отображения функциональных блоков с настоящей 3D-моделью (<see cref="Blocks.FunctionalBlockComponent.ScenePath"/>) —
/// используется и <see cref="FunctionalBlockView"/> (размещённые блоки в мире/редакторе), и <see cref="Ui.BlockIconView"/> (иконка в
/// хотбаре/списке блоков), и редактором блоков (<c>Dev.BlockEditor</c>).
/// <para/>
/// Автоподгонки модели по bbox больше нет: масштаб и якорь задаются явно (<see cref="BlockModelLayout"/>), а здесь — загрузка и кэш
/// сцен, измерение bbox (<see cref="ComputeLocalAabb"/>), рамки блока (<see cref="RootFrame"/>/<see cref="InstanceFrame"/>) и положение
/// портов/нод на хитбоксе.
/// </summary>
public static class FunctionalBlockGeometry
{
    /// <summary>Запись кэша сцены. <paramref name="Modified"/>/<paramref name="Length"/> — время изменения и размер файла на момент
    /// загрузки (по ним узнаём, что файл переэкспортирован), <paramref name="CheckedAtMs"/> — когда файл проверяли в последний раз.</summary>
    private sealed record SceneEntry(PackedScene? Scene, Aabb Aabb, ulong Modified, long Length, ulong CheckedAtMs);

    // Сцена+её bounding box грузятся и измеряются один раз на уникальный путь (не на каждый вызывающий сайт) - общий кэш между
    // FunctionalBlockView (уже поставленные блоки), BuildEditor (призрак установки), BlockIconView (иконка) и редактором блоков.
    // Кэш НЕ вечный: если файл изменился на диске (время изменения/размер), сцена перечитывается - иначе перезагрузка модели
    // после экспорта из Blender не сработала бы (см. GetOrLoadScene).
    private static readonly Dictionary<string, SceneEntry> SceneCache = new();

    /// <summary>Как часто (мс) проверять время изменения файла у уже закэшированной сцены: это системный вызов, а
    /// <see cref="GetOrLoadScene"/> дёргают на каждый поставленный блок при каждом изменении постройки.</summary>
    private const ulong RecheckIntervalMs = 500;

    /// <summary>
    /// Загружает <c>res://meshes/*.glb</c> по пути из <see cref="Blocks.FunctionalBlockComponent.ScenePath"/> и измеряет его bounding
    /// box (<see cref="ComputeLocalAabb"/>) — кэшировано по пути, измерение инстанцирует ОДИН пробный узел и сразу синхронно
    /// освобождает (<see cref="Node.Free"/> — пробный узел никогда не входит в дерево сцены). <c>Scene == null</c> — файл не
    /// найден/не загрузился (залогировано один раз на каждую версию файла).
    /// <para/>
    /// <b>Перечитывание при изменении файла.</b> При каждом обращении (не чаще раза в <see cref="RecheckIntervalMs"/> мс, либо сразу при
    /// <paramref name="forceRecheck"/> — кнопка Reload в редакторе) сравнивается время изменения и размер файла с теми, что были при
    /// загрузке: изменился — сцена и bbox читаются заново, новая версия вытесняет старую (уже инстанцированные узлы продолжают
    /// жить со старой <see cref="PackedScene"/>, новые инстансы берут свежую). Размер учитывается вместе со временем, потому что время
    /// изменения хранится с точностью до секунды: пересохранение другого содержимого за ту же секунду иначе осталось бы незамеченным.
    /// <para/>
    /// <b>Горячая загрузка.</b> Если файл лежит на диске (разработка), <c>.glb</c>/<c>.gltf</c> читается НАПРЯМУЮ через
    /// <see cref="GltfDocument"/> (<see cref="LoadGltfDirectly"/>), минуя кэш импорта <c>res://.godot/imported/*.scn</c> — так модель
    /// видна сразу по выбору файла/после переэкспорта, без <c>--import</c> и перезапуска, и никогда не устаревает относительно
    /// файла. Нет файла на диске (собранная игра хранит только импортированные ресурсы) или прямое чтение не удалось —
    /// обычный <see cref="GD.Load{T}(string)"/>.
    /// </summary>
    public static (PackedScene? Scene, Aabb Aabb) GetOrLoadScene(string path, bool forceRecheck = false)
    {
        ulong now = Time.GetTicksMsec();
        ulong modified = 0;
        long length = 0;

        if (SceneCache.TryGetValue(path, out var cached))
        {
            if (!forceRecheck && now - cached.CheckedAtMs < RecheckIntervalMs) return (cached.Scene, cached.Aabb);

            (modified, length) = ReadFileStamp(path);
            if (modified == cached.Modified && length == cached.Length)
            {
                SceneCache[path] = cached with { CheckedAtMs = now };
                return (cached.Scene, cached.Aabb);
            }
        }
        else
        {
            (modified, length) = ReadFileStamp(path);
        }

        PackedScene? scene = null;
        bool isGltf = path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase);
        if (length > 0 && isGltf) scene = LoadGltfDirectly(path);
        scene ??= GD.Load<PackedScene>(path);

        Aabb aabb = default;
        if (scene == null)
        {
            GD.PrintErr($"[functional-block] scene not found: {path}");
        }
        else
        {
            var probe = scene.Instantiate<Node3D>();
            aabb = ComputeLocalAabb(probe);
            probe.Free();
        }

        SceneCache[path] = new SceneEntry(scene, aabb, modified, length, now);
        return (scene, aabb);
    }

    /// <summary>Время изменения и размер файла; (0, 0) — файла нет на диске (в собранной игре остаются только импортированные ресурсы).</summary>
    private static (ulong Modified, long Length) ReadFileStamp(string path)
    {
        if (!FileAccess.FileExists(path)) return (0, 0);
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        return (FileAccess.GetModifiedTime(path), file == null ? 0 : (long)file.GetLength());
    }

    /// <summary>Сбрасывает кэш сцены (для самотестов и принудительной перезагрузки).</summary>
    public static void ClearSceneCache(string? path = null)
    {
        if (path == null) SceneCache.Clear();
        else SceneCache.Remove(path);
    }

    /// <summary>
    /// Разбирает <c>.glb</c>/<c>.gltf</c> напрямую по байтам файла через <see cref="GltfDocument"/>/<see cref="GltfState"/>
    /// (тот же класс, которым ПОЛЬЗУЕТСЯ САМ импортёр редактора внутри — доступен и без него) — минует кэш импорта
    /// <c>res://.godot/imported/*.scn</c> полностью, поэтому работает для файла, который движок ещё НИ РАЗУ не импортировал.
    /// <see cref="GltfDocument.AppendFromFile"/> читает <paramref name="path"/> через обычный <see cref="FileAccess"/>, поэтому
    /// принимает и <c>res://...</c>, и нативный OS-путь одинаково. <see cref="GltfDocument.GenerateScene"/> возвращает уже готовый,
    /// проставленный <see cref="Node.Owner"/> по всему поддереву узел — ровно то, что нужно <see cref="PackedScene.Pack"/>.
    /// <c>null</c> — не удалось прочитать/разобрать файл (не glTF/битый файл).
    /// </summary>
    private static PackedScene? LoadGltfDirectly(string path)
    {
        using var document = new GltfDocument();
        using var state = new GltfState();
        if (document.AppendFromFile(path, state) != Error.Ok) return null;

        var root = document.GenerateScene(state);
        var packed = new PackedScene();
        bool packedOk = packed.Pack(root) == Error.Ok;
        root.QueueFree(); // уже упакован в PackedScene - сам живой узел больше не нужен
        return packedOk ? packed : null;
    }

    /// <summary>
    /// Bounding box всей видимой геометрии поддерева (все <see cref="MeshInstance3D"/> по их ВЕРШИНАМ, прочие <see cref="GeometryInstance3D"/> — по
    /// AABB; свет и т.п. не считаются; рекурсивно, с учётом трансформов каждого промежуточного узла) в локальном пространстве <paramref name="root"/> — то есть ДО
    /// применения трансформа самого <paramref name="root"/> в его будущем родителе. Не требует, чтобы узел был
    /// в дереве сцены. Единичный куб — если в поддереве вообще нет видимой геометрии (пустая/битая сцена).
    /// </summary>
    public static Aabb ComputeLocalAabb(Node root)
    {
        bool any = false;
        Vector3 min = Vector3.Zero, max = Vector3.Zero;

        void Include(Vector3 world)
        {
            if (!any) { min = world; max = world; any = true; return; }
            min = new Vector3(Mathf.Min(min.X, world.X), Mathf.Min(min.Y, world.Y), Mathf.Min(min.Z, world.Z));
            max = new Vector3(Mathf.Max(max.X, world.X), Mathf.Max(max.Y, world.Y), Mathf.Max(max.Z, world.Z));
        }

        void Visit(Node node, Transform3D toRoot)
        {
            if (node is Node3D node3D) toRoot *= node3D.Transform;

            // Только ГЕОМЕТРИЯ: у света/зонда отражений и т.п. (тоже VisualInstance3D) AABB — это область действия, а не модель.
            if (node is MeshInstance3D { Mesh: { } mesh })
            {
                // Точный габарит — по самим вершинам. AABB меша, прогнанный через поворот узла, раздувает габарит: повёрнутая «коробка вокруг
                // меша» больше самой геометрии (у повёрнутого на 22°/45° вала выходило 2.0×2.9×2.7 вместо реальных 2.0×2.0×1.3 - модель
                // казалась «сжатой» внутри своей рамки).
                var faces = mesh.GetFaces();
                if (faces.Length > 0)
                {
                    foreach (var vertex in faces) Include(toRoot * vertex);
                }
                else
                {
                    IncludeAabbCorners(((VisualInstance3D)node).GetAabb(), toRoot);
                }
            }
            else if (node is GeometryInstance3D geometry)
            {
                IncludeAabbCorners(geometry.GetAabb(), toRoot);
            }

            foreach (var child in node.GetChildren()) Visit(child, toRoot);
        }

        void IncludeAabbCorners(Aabb local, Transform3D toRoot)
        {
            for (int i = 0; i < 8; i++)
            {
                Include(toRoot * (local.Position + new Vector3(
                    (i & 1) != 0 ? local.Size.X : 0,
                    (i & 2) != 0 ? local.Size.Y : 0,
                    (i & 4) != 0 ? local.Size.Z : 0)));
            }
        }

        Visit(root, Transform3D.Identity);
        return any ? new Aabb(min, max - min) : new Aabb(Vector3.Zero, Vector3.One);
    }

    /// <summary>
    /// «Рамка» блока, который крутится вокруг КОРНЕВОЙ клетки (см. <see cref="BlockFootprint"/>): переводит координаты рамки блока
    /// (метры от минимального угла корневой клетки (0,0,0), см. <see cref="BlockModelLayout"/>) в мир. Корневая клетка
    /// (<paramref name="rootCell"/>) стоит на месте, остальное поворачивается вокруг её ЦЕНТРА. Принимает произвольный
    /// <see cref="Basis"/>, а не только кратный 90° — по ней призрак установки плавно доворачивается на J/K/L (см.
    /// <c>Editor.BuildEditor</c>), жёстко вращаясь вокруг корня; всё локальное (модель, коллизия, ноды, порты) задаётся в
    /// неповёрнутом блоке и просто умножается на эту рамку.
    /// </summary>
    public static Transform3D RootFrame(Vector3I rootCell, Basis rotation)
    {
        var pivot = new Vector3(0.5f, 0.5f, 0.5f) * BuildSpace.CellSize; // центр корневой клетки в локальных метрах
        return new Transform3D(rotation, BuildSpace.CellCenter(rootCell) - rotation * pivot);
    }

    /// <summary>
    /// Та же рамка для УЖЕ поставленного экземпляра: корневая клетка восстанавливается из занятого бокса
    /// (<paramref name="origin"/> — <see cref="Core.BlockInstance.Origin"/>, для повёрнутого блока это угол повёрнутого бокса) и
    /// локального бокса блока (<paramref name="footprintMin"/>/<paramref name="footprint"/> из определения блока, неповёрнутые) —
    /// см. <see cref="BlockFootprint.RootCell"/>; результат совпадает с <see cref="RootFrame"/> того блока, что ставился через
    /// <see cref="BlockFootprint.PlaceBox(Vector3I, Vector3I, Vector3I, Vector3I)"/> (проверяется самотестом).
    /// </summary>
    public static Transform3D InstanceFrame(Vector3I origin, Vector3I footprintMin, Vector3I footprint, Vector3I rotationSteps) =>
        RootFrame(BlockFootprint.RootCell(origin, footprintMin, footprint, rotationSteps), ShapeMeshBuilder.ComposeRotation(rotationSteps));

    /// <summary>
    /// Для стороны <paramref name="face"/> размер (ширина, высота) её собственной 2D-сетки клеток для footprint'а размером
    /// <paramref name="footprint"/>, в которой задаётся <see cref="ResourcePort.FaceCell"/>. ±X используют (Y,Z) footprint'а, ±Y —
    /// (X,Z), ±Z — (X,Y): два измерения, ЛЕЖАЩИЕ В ПЛОСКОСТИ этой стороны (ось, перпендикулярную ей, координата порта не имеет —
    /// порт сидит прямо НА плоскости грани).
    /// </summary>
    public static (int Width, int Height) FaceDimensions(BlockFace face, Vector3I footprint)
    {
        var (u, v) = FaceAxes(face);
        return (footprint[u], footprint[v]);
    }

    /// <summary>Индексы осей (0=X, 1=Y, 2=Z), по которым идёт 2D-сетка стороны: (u, v) = первая и вторая координата
    /// <see cref="ResourcePort.FaceCell"/>.</summary>
    public static (int U, int V) FaceAxes(BlockFace face) => face switch
    {
        BlockFace.NegX or BlockFace.PosX => (1, 2),
        BlockFace.NegY or BlockFace.PosY => (0, 2),
        _ => (0, 1),
    };

    /// <summary>
    /// Точка логической ноды (<see cref="Blocks.LogicNode"/>): ЦЕНТР её клетки <paramref name="cell"/> в рамке блока (метры от
    /// минимального угла корневой клетки). В отличие от <see cref="ComputePortAnchor"/> нода сидит внутри блока, а не на грани. Клетка
    /// КЛАМПИТСЯ к footprint'у (<paramref name="footprintMin"/>/<paramref name="footprint"/>; сохранённые данные не меняются) — на
    /// случай, если footprint изменился после размещения ноды.
    /// </summary>
    public static Vector3 ComputeNodeAnchor(Vector3I cell, Vector3I footprintMin, Vector3I footprint, float cellSize)
    {
        int cx = Math.Clamp(cell.X, footprintMin.X, footprintMin.X + Math.Max(footprint.X - 1, 0));
        int cy = Math.Clamp(cell.Y, footprintMin.Y, footprintMin.Y + Math.Max(footprint.Y - 1, 0));
        int cz = Math.Clamp(cell.Z, footprintMin.Z, footprintMin.Z + Math.Max(footprint.Z - 1, 0));
        return new Vector3(cx + 0.5f, cy + 0.5f, cz + 0.5f) * cellSize;
    }

    /// <summary>Смещение маркеров нод РАЗНЫХ типов в одной клетке друг от друга вдоль оси X блока (метры): в клетке могут сидеть до трёх нод (по одной каждого типа), и без
    /// разноса их маркеры совпали бы в одну точку — выбрать нужную нельзя. Электричество −, Boolean 0, Number +.</summary>
    public const float NodeSpread = 0.06f;

    /// <summary>Точка ноды в рамке блока для отрисовки/выбора мышью: центр клетки + разнос по типу (<see cref="NodeSpread"/>).</summary>
    public static Vector3 NodeLocalPosition(FunctionalBlockComponent block, LogicNode node) =>
        ComputeNodeAnchor(node.Cell, block.FootprintMin, block.Footprint, BuildSpace.CellSize) + new Vector3(((int)node.Type - 1) * NodeSpread, 0, 0);

    /// <summary>Точка ноды экземпляра в мире постройки (координаты клеток × размер клетки): рамка экземпляра (<see cref="InstanceFrame"/>) · точка в рамке блока.</summary>
    public static Vector3 NodeWorldPosition(BlockInstance instance, FunctionalBlockComponent block, LogicNode node) =>
        InstanceFrame(instance.Origin, block.FootprintMin, block.Footprint, instance.RotationSteps) * NodeLocalPosition(block, node);

    /// <summary>
    /// Переводит (<see cref="ResourcePort.Face"/>, <see cref="ResourcePort.FaceCell"/>) в точку (рамка блока, метры от минимального
    /// угла корневой клетки) и направленную наружу нормаль этой стороны. <see cref="ResourcePort.FaceCell"/> — АБСОЛЮТНЫЕ индексы
    /// клетки по двум осям грани (<see cref="FaceAxes"/>) в рамке блока, а не отсчёт от угла footprint'а: у блока без смещения
    /// footprint'а (<paramref name="footprintMin"/> = 0) они совпадают. Индексы КЛАМПЯТСЯ в границы footprint'а по этим осям (на
    /// случай, если footprint изменили ПОСЛЕ размещения порта) — клампится только вычисляемая точка, не сохранённые данные.
    /// Намеренно не мешает НЕСКОЛЬКИМ портам иметь одну и ту же (Face, FaceCell) — см. <see cref="ResourcePort"/> class doc.
    /// </summary>
    public static (Vector3 Position, Vector3 Normal) ComputePortAnchor(BlockFace face, Vector2I faceCell, Vector3I footprintMin, Vector3I footprint, float cellSize)
    {
        var (u, v) = FaceAxes(face);
        int cu = Math.Clamp(faceCell.X, footprintMin[u], footprintMin[u] + Math.Max(footprint[u] - 1, 0));
        int cv = Math.Clamp(faceCell.Y, footprintMin[v], footprintMin[v] + Math.Max(footprint[v] - 1, 0));

        int normalAxis = 3 - u - v;
        bool positive = ((int)face & 1) == 1;
        float planeCell = positive ? footprintMin[normalAxis] + footprint[normalAxis] : footprintMin[normalAxis];

        var position = Vector3.Zero;
        position[u] = (cu + 0.5f) * cellSize;
        position[v] = (cv + 0.5f) * cellSize;
        position[normalAxis] = planeCell * cellSize;

        var normal = Vector3.Zero;
        normal[normalAxis] = positive ? 1 : -1;
        return (position, normal);
    }
}
