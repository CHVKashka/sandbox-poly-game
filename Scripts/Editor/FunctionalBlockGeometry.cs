using System.Collections.Generic;
using Godot;
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
    /// </summary>
    public static (PackedScene? Scene, Aabb Aabb) GetOrLoadScene(string path)
    {
        if (SceneCache.TryGetValue(path, out var cached)) return cached;

        var scene = GD.Load<PackedScene>(path);
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
    /// </summary>
    public static Transform3D ComputeFitTransform(Aabb modelAabb, Vector3 targetExtent, Vector3 targetCenter, Vector3I rotationSteps, Vector3 extraScale) =>
        ComputeFitTransform(modelAabb, targetExtent, targetCenter, ShapeMeshBuilder.ComposeRotation(rotationSteps), extraScale);

    /// <summary>То же самое, но поворот — уже готовый произвольный <see cref="Basis"/>, не только 0..3 ступени —
    /// используется анимацией поворота призрака (см. <c>Editor.BuildEditor</c>, плавный довод через
    /// <see cref="Basis.Slerp"/>), настоящие поставленные блоки всегда используют целую перегрузку выше.</summary>
    public static Transform3D ComputeFitTransform(Aabb modelAabb, Vector3 targetExtent, Vector3 targetCenter, Basis rotation, Vector3 extraScale)
    {
        var size = modelAabb.Size;
        // var, не float - сборка движка double-precision (real_t = double), Mathf.Min(double, double) возвращает double.
        var scale = Mathf.Min(targetExtent.X / size.X, Mathf.Min(targetExtent.Y / size.Y, targetExtent.Z / size.Z));

        var basis = rotation * Basis.Identity.Scaled(new Vector3(scale, scale, scale) * extraScale);

        var modelCenter = modelAabb.Position + size * 0.5f;
        var origin = targetCenter - basis * modelCenter;
        return new Transform3D(basis, origin);
    }
}
