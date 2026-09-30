using System;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Editor;

/// <summary>
/// Рендерит превью постройки в PNG (для окна выбора построек на верстаке — см.
/// Docs/05-world-and-vehicle-systems.md). Тот же приём, что у <c>Ui.BlockIconView</c> (изолированный
/// <see cref="SubViewport"/> со своим <c>World3D</c>, изометрическая камера) — но не постоянный UI-элемент, а один
/// снимок в файл; временный <see cref="SubViewport"/> уничтожается сразу после. Best-effort: постройка сохраняется
/// (см. <c>ConstructionStorage.Save</c>) независимо от того, получилось ли превью — картинка вторична и не должна
/// мешать основному сохранению.
/// <para/>
/// Не покрыто самотестами: рендер требует нескольких реально отрисованных кадров (тот же паттерн, что у
/// <c>Dev.DevHarness</c>'s <c>--screenshot</c>), а этот путь в headless-самотестах в текущем окружении разработки
/// ненадёжен (зависает — см. заметку в истории проекта); ручная проверка — на обычном запуске игры.
/// </summary>
public static class ConstructionPreviewRenderer
{
    private const int PixelSize = 256;

    public static async Task RenderAsync(Node host, Construction construction, string outputPath)
    {
        if (construction.Instances.Count == 0) return;

        var viewport = new SubViewport
        {
            Size = new Vector2I(PixelSize, PixelSize),
            TransparentBg = true,
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        host.AddChild(viewport);

        try
        {
            var root = new Node3D();
            viewport.AddChild(root);

            root.AddChild(new WorldEnvironment
            {
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Color,
                    BackgroundColor = new Color(0.35f, 0.40f, 0.47f, 1f),
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = Colors.White,
                    AmbientLightEnergy = 0.9f,
                },
            });
            root.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, -35, 0), LightEnergy = 1.1f });

            // Копируем постройку в изолированный VoxelWorld этого снимка (через тот же JSON, что и обычное
            // сохранение) — не трогаем сцену/Construction редактора.
            var world = new VoxelWorld();
            root.AddChild(world);
            ConstructionIO.Deserialize(world.Construction, ConstructionIO.Serialize(construction), BlockCatalog.Instance);
            world.RebuildDirty();

            var (min, max) = construction.ComputeBounds();
            var center = (new Vector3(min.X, min.Y, min.Z) + new Vector3(max.X + 1, max.Y + 1, max.Z + 1)) * 0.5f * BuildSpace.CellSize;
            var extent = new Vector3(max.X - min.X + 1, max.Y - min.Y + 1, max.Z - min.Z + 1) * BuildSpace.CellSize;
            double radius = Math.Max(0.5, extent.Length() * 0.5);

            root.AddChild(new Camera3D
            {
                Projection = Camera3D.ProjectionType.Orthogonal,
                Size = (float)(radius * 2.2),
                Current = true,
                Transform = new Transform3D(Basis.Identity, center + new Vector3(1, 1, 1).Normalized() * (float)(radius * 3.0))
                    .LookingAt(center, Vector3.Up),
            });

            // Кадр должен успеть реально отрисоваться, прежде чем читать текстуру — тот же приём, что у
            // Dev.DevHarness's --screenshot.
            for (int i = 0; i < 3; i++) await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

            using var image = viewport.GetTexture().GetImage();
            var error = image.SavePng(outputPath);
            if (error != Error.Ok) GD.PrintErr($"[preview] SavePng failed ({error}): {outputPath}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[preview] failed to render construction preview: {ex.Message}");
        }
        finally
        {
            viewport.QueueFree();
        }
    }
}
