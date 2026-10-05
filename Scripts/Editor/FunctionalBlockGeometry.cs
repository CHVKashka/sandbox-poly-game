using System;
using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Editor;

/// <summary>
/// Общая геометрия для отображения функциональных блоков с настоящей 3D-моделью (<see cref="Blocks.FunctionalBlockComponent.ScenePath"/>,
/// например мотор/вал) — используется и <see cref="FunctionalBlockView"/> (размещённые блоки в мире/редакторе), и
/// <see cref="Ui.BlockIconView"/> (иконка в хотбаре/списке блоков).
/// <para/>
/// Модель нормализуется под целевой размер АВТОМАТИЧЕСКИ, по реальному bounding box геометрии
/// (<see cref="ComputeLocalAabb"/>), а не по заявленному размеру в Blender — на практике разошлось: исходный
/// <c>motor_small.glb</c> заявлен как "1×1×1 метр", но фактический bounding box геометрии в самом файле — около
/// 2×2.15×2 (классическое расхождение Blender Unit Scale/экспорта между "что показывает редактор" и "что реально
/// попало в glTF"). Доверять заявленному размеру и жёстко зашивать коэффициент масштаба — хрупко и ломается молча
/// при следующей такой же модели; вместо этого <see cref="ComputeFitTransform"/> считает РЕАЛЬНЫЙ bounding box и
/// всегда вписывает его в целевую область точно, независимо от того, в каких единицах оказался экспорт.
/// </summary>
public static class FunctionalBlockGeometry
{
    // Сцена+её bounding box грузятся и измеряются один раз на уникальный путь (не на каждый вызывающий сайт) -
    // общий кэш между Editor.FunctionalBlockView (уже поставленные блоки), Editor.BuildEditor (призрак установки)
    // и Editor.Ui.BlockIconView (иконка в хотбаре) - без этого один и тот же .glb измерялся бы заново в каждом из них.
    private static readonly Dictionary<string, (PackedScene? Scene, Aabb Aabb)> SceneCache = new();

    /// <summary>
    /// Загружает <c>res://meshes/*.glb</c> по пути из <see cref="Blocks.FunctionalBlockComponent.ScenePath"/> и
    /// измеряет его bounding box (<see cref="ComputeLocalAabb"/>) — кэшировано по пути, измерение инстанцирует ОДИН
    /// пробный узел и сразу синхронно освобождает (<see cref="Node.Free"/>, не <see cref="Node.QueueFree"/> —
    /// пробный узел никогда не входит в дерево сцены). <c>Scene == null</c> — файл не найден/не загрузился
    /// (залогировано один раз, при первом обращении к этому пути).
    /// <para/>
    /// Если <see cref="GD.Load{T}(string)"/> не нашёл готовый импорт (<c>res://.godot/imported/*.scn</c> ещё не
    /// создан движком — свежескопированный файл, например через <c>Dev.BlockPrefabEditorUi.BrowseForModel</c>,
    /// до ближайшего <c>--import</c>/перезапуска), запасным путём разбираем `.glb`/`.gltf` НАПРЯМУЮ через
    /// <see cref="LoadGltfDirectly"/>, в обход конвейера импорта редактора — это и даёт "горячую" загрузку модели
    /// сразу по выбору файла, по запросу пользователя, без ожидания импорта.
    /// </summary>
    public static (PackedScene? Scene, Aabb Aabb) GetOrLoadScene(string path)
    {
        if (SceneCache.TryGetValue(path, out var cached)) return cached;

        var scene = GD.Load<PackedScene>(path) ?? LoadGltfDirectly(path);
        if (scene == null)
        {
            GD.PrintErr($"[functional-block] scene not found: {path}");
            cached = (null, default);
        }
        else
        {
            var probe = scene.Instantiate<Node3D>();
            var aabb = ComputeLocalAabb(probe);
            probe.Free();
            cached = (scene, aabb);
        }

        SceneCache[path] = cached;
        return cached;
    }

    /// <summary>
    /// Разбирает <c>.glb</c>/<c>.gltf</c> напрямую по байтам файла через <see cref="GltfDocument"/>/<see cref="GltfState"/>
    /// (тот же класс, которым ПОЛЬЗУЕТСЯ САМ импортёр редактора внутри — доступен и без него) — минует кэш импорта
    /// <c>res://.godot/imported/*.scn</c> полностью, поэтому работает для файла, который движок ещё НИ РАЗУ не
    /// импортировал (включая только что скопированный `.glb`, который физически лежит в <c>res://meshes/</c>, но не
    /// успел получить `.import`-кэш). <see cref="GltfDocument.AppendFromFile"/> читает <paramref name="path"/> через
    /// обычный <see cref="FileAccess"/>, поэтому принимает и <c>res://...</c>, и нативный OS-путь одинаково.
    /// <see cref="GltfDocument.GenerateScene"/> возвращает уже готовый, проставленный <see cref="Node.Owner"/> по
    /// всему поддереву узел (сам генератор так и делает — ровно то, что нужно <see cref="PackedScene.Pack"/>, иначе
    /// упаковались бы не все дочерние узлы). <c>null</c> — не удалось прочитать/разобрать файл (не глтф/битый файл).
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
    /// Bounding box всей видимой геометрии поддерева (все <see cref="VisualInstance3D"/>, рекурсивно, с учётом
    /// трансформов каждого промежуточного узла) в локальном пространстве <paramref name="root"/> — то есть ДО
    /// применения трансформа самого <paramref name="root"/> в его будущем родителе. Не требует, чтобы узел был
    /// в дереве сцены (используется и на "пробном" инстансе, который тут же освобождается, см. <see cref="FunctionalBlockView"/>).
    /// Vector3.One (единичный куб) — если в поддереве вообще нет видимой геометрии (пустая/битая сцена).
    /// </summary>
    public static Aabb ComputeLocalAabb(Node root)
    {
        bool any = false;
        Vector3 min = Vector3.Zero, max = Vector3.Zero;

        void Visit(Node node, Transform3D toRoot)
        {
            if (node is Node3D node3D) toRoot *= node3D.Transform;

            if (node is VisualInstance3D visual)
            {
                var local = visual.GetAabb();
                for (int i = 0; i < 8; i++)
                {
                    var corner = local.Position + new Vector3(
                        (i & 1) != 0 ? local.Size.X : 0,
                        (i & 2) != 0 ? local.Size.Y : 0,
                        (i & 4) != 0 ? local.Size.Z : 0);
                    var world = toRoot * corner;
                    if (!any) { min = world; max = world; any = true; }
                    else
                    {
                        min = new Vector3(Mathf.Min(min.X, world.X), Mathf.Min(min.Y, world.Y), Mathf.Min(min.Z, world.Z));
                        max = new Vector3(Mathf.Max(max.X, world.X), Mathf.Max(max.Y, world.Y), Mathf.Max(max.Z, world.Z));
                    }
                }
            }

            foreach (var child in node.GetChildren()) Visit(child, toRoot);
        }

        Visit(root, Transform3D.Identity);
        return any ? new Aabb(min, max - min) : new Aabb(Vector3.Zero, Vector3.One);
    }

    /// <summary>
    /// Трансформ, который вписывает <paramref name="modelAabb"/> (см. <see cref="ComputeLocalAabb"/>) в область
    /// размером <paramref name="targetExtent"/>, центрированную в <paramref name="targetCenter"/>, повёрнутую на
    /// <paramref name="rotationSteps"/> четвертей оборота вокруг X/Y/Z (та же конвенция, что и
    /// <see cref="ShapeMeshBuilder.ComposeRotation"/>/<see cref="Core.BlockInstance.RotationSteps"/>) — базовый
    /// масштаб РАВНОМЕРНЫЙ (по оси, которой не хватает места больше всего), пропорции модели не искажаются; оси, где
    /// модель меньше целевого размера, просто не касаются границ (центрировано, не растянуто).
    /// <paramref name="extraScale"/> — ручная поправка ПОВЕРХ этого равномерного масштаба, покомпонентно
    /// (см. <see cref="Blocks.FunctionalBlockComponent.ModelScale"/>) — <see cref="Vector3.One"/> ничего не меняет.
    /// <paramref name="modelOffset"/> — ручной сдвиг модели внутри клетки в МЕТРАХ, в осях НЕповёрнутого блока (см.
    /// <see cref="Blocks.FunctionalBlockComponent.ModelOffset"/>), поворачивается вместе с блоком; по умолчанию ноль.
    /// </summary>
    public static Transform3D ComputeFitTransform(Aabb modelAabb, Vector3 targetExtent, Vector3 targetCenter, Vector3I rotationSteps, Vector3 extraScale, Vector3 modelOffset = default) =>
        ComputeFitTransform(modelAabb, targetExtent, targetCenter, ShapeMeshBuilder.ComposeRotation(rotationSteps), extraScale, modelOffset);

    /// <summary>То же самое, но поворот — уже готовый произвольный <see cref="Basis"/>, не только 0..3 ступени —
    /// используется анимацией поворота призрака (см. <c>Editor.BuildEditor</c>, плавный довод через
    /// <see cref="Basis.Slerp"/>), настоящие поставленные блоки всегда используют целую перегрузку выше.</summary>
    public static Transform3D ComputeFitTransform(Aabb modelAabb, Vector3 targetExtent, Vector3 targetCenter, Basis rotation, Vector3 extraScale, Vector3 modelOffset = default)
    {
        var size = modelAabb.Size;
        // var, не float - сборка движка double-precision (real_t = double), Mathf.Min(double, double) возвращает double.
        var scale = Mathf.Min(targetExtent.X / size.X, Mathf.Min(targetExtent.Y / size.Y, targetExtent.Z / size.Z));

        var basis = rotation * Basis.Identity.Scaled(new Vector3(scale, scale, scale) * extraScale);

        var modelCenter = modelAabb.Position + size * 0.5f;
        // Сдвиг задан в осях неповёрнутого блока и не зависит от масштаба модели - поворачивается как сам блок.
        var origin = targetCenter + rotation * modelOffset - basis * modelCenter;
        return new Transform3D(basis, origin);
    }

    /// <summary>
    /// «Рамка» блока с фиксированным footprint'ом, который крутится вокруг КОРНЕВОЙ клетки (см. <see cref="BlockFootprint"/>):
    /// переводит локальные координаты неповёрнутого блока (метры от минимального угла footprint'а = угла корневой клетки) в мир.
    /// Корневая клетка (<paramref name="rootCell"/>) стоит на месте, остальное поворачивается вокруг её ЦЕНТРА. Принимает
    /// произвольный <see cref="Basis"/>, а не только кратный 90° — по ней призрак установки плавно доворачивается на J/K/L
    /// (см. <c>Editor.BuildEditor</c>), жёстко вращаясь вокруг корня; всё локальное (модель, коллизия, ноды, порты) задаётся
    /// в неповёрнутом блоке и просто умножается на эту рамку.
    /// </summary>
    public static Transform3D RootFrame(Vector3I rootCell, Basis rotation)
    {
        var pivot = new Vector3(0.5f, 0.5f, 0.5f) * BuildSpace.CellSize; // центр корневой клетки в локальных метрах
        return new Transform3D(rotation, BuildSpace.CellCenter(rootCell) - rotation * pivot);
    }

    /// <summary>
    /// Та же рамка для УЖЕ поставленного экземпляра: считается от занятого бокса (<paramref name="origin"/>/<paramref name="size"/> —
    /// <see cref="Core.BlockInstance.Origin"/>/<see cref="Core.BlockInstance.Size"/>, для повёрнутого блока это повёрнутый бокс),
    /// <paramref name="footprint"/> — неповёрнутый footprint из определения блока. Поворот на 90° переводит бокс в бокс, поэтому
    /// центр занятого бокса — это и есть центр повёрнутого footprint'а: рамка совпадает с <see cref="RootFrame"/> того блока, что
    /// ставился через <see cref="BlockFootprint.PlaceBox"/> (проверяется самотестом). Считается по центру бокса, а не по корню,
    /// намеренно: так же отображается и экземпляр из старого сохранения, где <c>Size</c> ещё не повёрнут (он остаётся там же, где
    /// был, не прыгает).
    /// </summary>
    public static Transform3D InstanceFrame(Vector3I origin, Vector3I size, Vector3I footprint, Vector3I rotationSteps)
    {
        var rotation = ShapeMeshBuilder.ComposeRotation(rotationSteps);
        var boxCenter = (BuildSpace.CellMin(origin) + BuildSpace.CellMin(origin + size)) * 0.5f;
        var footprintCenter = new Vector3(footprint.X, footprint.Y, footprint.Z) * BuildSpace.CellSize * 0.5f;
        return new Transform3D(rotation, boxCenter - rotation * footprintCenter);
    }

    /// <summary>
    /// Для стороны <paramref name="face"/> НЕповёрнутого footprint'а размером <paramref name="footprint"/> (в
    /// клетках) — размер (ширина, высота) её собственной 2D-сетки, в которой задаётся <see cref="ResourcePort.FaceCell"/>.
    /// X/Y/Z-грани (±X) используют (Y,Z) footprint'а, Y-грани (±Y) — (X,Z), Z-грани (±Z) — (X,Y): на каждой стороне
    /// "ширина/высота" — это два измерения footprint'а, ЛЕЖАЩИЕ В ПЛОСКОСТИ этой стороны (ось, перпендикулярную ей,
    /// координата порта не имеет — порт сидит прямо НА плоскости грани).
    /// </summary>
    public static (int Width, int Height) FaceDimensions(BlockFace face, Vector3I footprint) => face switch
    {
        BlockFace.NegX or BlockFace.PosX => (footprint.Y, footprint.Z),
        BlockFace.NegY or BlockFace.PosY => (footprint.X, footprint.Z),
        _ => (footprint.X, footprint.Y),
    };

    /// <summary>
    /// Точка логической ноды (<see cref="Blocks.LogicNode"/>): ЦЕНТР её клетки <paramref name="cell"/> в локальных координатах
    /// НЕповёрнутого блока (метры, от минимального угла footprint'а — как <see cref="Blocks.CollisionBox.Position"/>). В отличие от
    /// <see cref="ComputePortAnchor"/> нода сидит внутри блока, а не на грани. Клетка КЛАМПИТСЯ к footprint'у (сохранённые данные
    /// не меняются) — как и у портов, на случай если footprint уменьшили после размещения ноды.
    /// </summary>
    public static Vector3 ComputeNodeAnchor(Vector3I cell, Vector3I footprint, float cellSize)
    {
        int cx = Math.Clamp(cell.X, 0, Math.Max(footprint.X - 1, 0));
        int cy = Math.Clamp(cell.Y, 0, Math.Max(footprint.Y - 1, 0));
        int cz = Math.Clamp(cell.Z, 0, Math.Max(footprint.Z - 1, 0));
        return new Vector3(cx + 0.5f, cy + 0.5f, cz + 0.5f) * cellSize;
    }

    /// <summary>
    /// Переводит (<see cref="ResourcePort.Face"/>, <see cref="ResourcePort.FaceCell"/>) в точку (локальные координаты
    /// НЕповёрнутого блока, метры, от минимального угла footprint'а — та же конвенция, что и <see cref="Blocks.CollisionBox.Position"/>)
    /// и направленную наружу нормаль этой стороны. <paramref name="faceCell"/> КЛАМПИТСЯ в границы реальной сетки
    /// грани (<see cref="FaceDimensions"/>) на случай, если footprint блока уменьшили ПОСЛЕ того, как порт разместили
    /// на клетке, которой больше не существует — само сохранённое значение при этом не меняется (клампится только
    /// вычисляемая точка, не данные), чтобы не терять позицию порта молча при временном несоответствии в редакторе.
    /// Намеренно не проверяет и не мешает НЕСКОЛЬКИМ портам иметь одну и ту же (Face, FaceCell) — см.
    /// <see cref="ResourcePort"/> class doc.
    /// </summary>
    public static (Vector3 Position, Vector3 Normal) ComputePortAnchor(BlockFace face, Vector2I faceCell, Vector3I footprint, float cellSize)
    {
        var (width, height) = FaceDimensions(face, footprint);
        int cx = Math.Clamp(faceCell.X, 0, Math.Max(width - 1, 0));
        int cy = Math.Clamp(faceCell.Y, 0, Math.Max(height - 1, 0));
        var a = (cx + 0.5f) * cellSize;
        var b = (cy + 0.5f) * cellSize;
        var maxX = footprint.X * cellSize;
        var maxY = footprint.Y * cellSize;
        var maxZ = footprint.Z * cellSize;

        return face switch
        {
            BlockFace.NegX => (new Vector3(0, a, b), new Vector3(-1, 0, 0)),
            BlockFace.PosX => (new Vector3(maxX, a, b), new Vector3(1, 0, 0)),
            BlockFace.NegY => (new Vector3(a, 0, b), new Vector3(0, -1, 0)),
            BlockFace.PosY => (new Vector3(a, maxY, b), new Vector3(0, 1, 0)),
            BlockFace.NegZ => (new Vector3(a, b, 0), new Vector3(0, 0, -1)),
            _ => (new Vector3(a, b, maxZ), new Vector3(0, 0, 1)),
        };
    }
}
