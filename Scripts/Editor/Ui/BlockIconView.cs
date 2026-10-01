using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Editor.Ui;

/// <summary>
/// Иконка блока для хотбара/списка блоков: миниатюрная 3D-сцена (свой <see cref="SubViewport"/> с изолированным
/// <c>World3D</c>), а не цветная плашка-плейсхолдер. Геометрия — та же, что и у настоящего блока (<see cref="BoxMesh"/>
/// для куба, <see cref="ShapeMeshBuilder"/> для остальных форм), поэтому иконка всегда отражает актуальную форму,
/// включая новые блоки из <c>blocks/*.xml</c> без правок кода здесь. Камера ортографическая под классическим
/// изометрическим углом (направление (1,1,1)), свет + слабый общий свет — чтобы грани куба различались без плоской заливки.
/// </summary>
internal static class BlockIconView
{
    public static Control Create(BlockDefinition definition, int pixelSize)
    {
        var container = new SubViewportContainer
        {
            CustomMinimumSize = new Vector2(pixelSize, pixelSize),
            Stretch = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        var viewport = new SubViewport
        {
            Size = new Vector2I(pixelSize, pixelSize),
            TransparentBg = true,
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        container.AddChild(viewport);

        var root = new Node3D();
        viewport.AddChild(root);

        root.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0f, 0f, 0f, 0f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = Colors.White,
                AmbientLightEnergy = 0.8f,
            },
        });

        root.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, -35, 0),
            LightEnergy = 1.1f,
        });

        root.AddChild(BuildMeshInstance(definition));

        // Node3D.LookAt требует, чтобы узел уже был внутри дерева сцены (использует GlobalTransform) — а этот
        // поддерева ещё не добавлен никуда (это сделает вызывающий код, добавив возвращённый Control). Поэтому
        // ориентация считается через Transform3D.LookingAt, которому дерево не нужно.
        var cameraPosition = new Vector3(1f, 1f, 1f).Normalized() * 3f;
        var camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = 1.7f,
            Current = true,
            Transform = new Transform3D(Basis.Identity, cameraPosition).LookingAt(Vector3.Zero, Vector3.Up),
        };
        root.AddChild(camera);

        return container;
    }

    /// <summary>Куб — простой центрированный <see cref="BoxMesh"/> цвета <see cref="BlockDefinition.DefaultColor"/>
    /// (грани различаются освещением, не вершинными цветами). Формы (Wedge/Pyramid/InvertedPyramid) —
    /// <see cref="ShapeMeshBuilder"/> без поворота/отражения/растяжения, отцентрированные так же, как куб (координаты
    /// формы — от угла 0..CellSize, сдвигаем на -0.5 и масштабируем в 1/CellSize, чтобы получить такой же единичный
    /// куб с центром в начале координат). Функциональный блок со своей моделью (<see cref="FunctionalBlockComponent.ScenePath"/>,
    /// например мотор/вал) — настоящая glTF-сцена, вписанная в тот же единичный куб тем же приёмом, что и в мире
    /// (<see cref="FunctionalBlockGeometry"/>) — иначе иконка (цветной куб) не совпадала бы с тем, что реально стоит
    /// в постройке.</summary>
    private static Node3D BuildMeshInstance(BlockDefinition definition)
    {
        var functional = definition.GetComponent<FunctionalBlockComponent>();
        if (functional != null && !string.IsNullOrEmpty(functional.ScenePath))
        {
            var (scene, aabb) = FunctionalBlockGeometry.GetOrLoadScene(functional.ScenePath);
            if (scene != null)
            {
                var instance = scene.Instantiate<Node3D>();
                instance.Transform = FunctionalBlockGeometry.ComputeFitTransform(aabb, Vector3.One, Vector3.Zero, Vector3I.Zero, functional.ModelScale);
                return instance;
            }
        }

        var building = definition.GetComponent<BuildingBlockComponent>();
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
            Roughness = 0.85f,
            Metallic = 0f,
        };

        if (building == null || building.Shape == BlockShape.Cube)
        {
            material.AlbedoColor = definition.DefaultColor;
            return new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One }, MaterialOverride = material };
        }

        material.VertexColorUseAsAlbedo = true;
        var (solid, _, _) = ShapeMeshBuilder.Build(building.Shape, Vector3I.One, Vector3I.Zero, Vector3I.Zero, definition.DefaultColor);
        return new MeshInstance3D
        {
            Mesh = solid,
            MaterialOverride = material,
            Position = new Vector3(-0.5f, -0.5f, -0.5f),
            Scale = Vector3.One / BuildSpace.CellSize,
        };
    }
}
