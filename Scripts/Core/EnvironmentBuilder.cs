using Godot;

namespace SandboxPolyGame.Core;

/// <summary>
/// Общая заготовка окружения сцены (небо+земля одним плоским цветом, направленный свет с тенями) — раньше жила
/// только внутри <c>Editor.BuildEditor</c>, вынесена сюда, когда появилась вторая сцена с тем же освещением
/// (<c>World.GameWorld</c>), чтобы не дублировать код.
/// </summary>
public static class EnvironmentBuilder
{
    /// <summary>Добавляет <see cref="WorldEnvironment"/> (небо+земля цвета <paramref name="skyGroundColor"/>, без
    /// градиента к горизонту) и <see cref="DirectionalLight3D"/> с тенями в <paramref name="parent"/>.</summary>
    public static void BuildFlatSkyAndSun(Node parent, Color skyGroundColor)
    {
        var sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = skyGroundColor,
                SkyHorizonColor = skyGroundColor,
                GroundHorizonColor = skyGroundColor,
                GroundBottomColor = skyGroundColor,
            },
        };

        parent.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = sky,
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            },
        });

        parent.AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-55, -35, 0),
            LightEnergy = 1.1f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 40f,
        });
    }
}
