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
    /// градиента к горизонту) и <see cref="DirectionalLight3D"/> с тенями в <paramref name="parent"/>.
    /// <paramref name="whiteLighting"/> — белое освещение независимо от цвета неба: по умолчанию ambient-свет берётся ИЗ неба
    /// (<see cref="Godot.Environment.AmbientSource.Sky"/>), и цветное небо красит тени/неосвещённые стороны в свой оттенок; с флагом
    /// ambient — белый (<see cref="Godot.Environment.AmbientSource.Color"/>), как и сам направленный свет.</summary>
    public static void BuildFlatSkyAndSun(Node parent, Color skyGroundColor, bool whiteLighting = false)
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
                AmbientLightSource = whiteLighting ? Godot.Environment.AmbientSource.Color : Godot.Environment.AmbientSource.Sky,
                AmbientLightColor = Colors.White,
                AmbientLightEnergy = whiteLighting ? 0.6f : 1f,
            },
        });

        parent.AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-55, -35, 0),
            LightColor = Colors.White,
            LightEnergy = 1.1f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = 40f,
        });
    }
}
