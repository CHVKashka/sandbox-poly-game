using Godot;

namespace SandboxPolyGame.World;

/// <summary>
/// Временная плоская плитка террейна — заглушка на время прототипа (см. Docs/05-world-and-vehicle-systems.md,
/// раздел «Террейн»): один квад + статический коллайдер, ничего похожего на будущий чанкованный хайтмап-рельеф.
/// Нужна только чтобы было на чём стоять игроку/постройкам, пока остальные системы (игрок, спавн построек,
/// функциональные блоки) не готовы — полноценный террейн заменит её позже без изменений в остальном мире
/// (тайл адресуется как обычный узел сцены, ни на что структурно не завязан).
/// </summary>
public partial class TerrainTile : StaticBody3D
{
    // Толщина коллайдера под поверхностью — коробка, а не бесконечная плоскость (WorldBoundaryShape3D), чтобы
    // плитки в будущем можно было стыковать край-в-край без взаимного наложения коллизии.
    private const float Thickness = 1f;

    /// <summary>Сторона плитки в метрах (по умолчанию — 25×25, см. задачу).</summary>
    public float Size { get; set; } = 25f;

    public override void _Ready()
    {
        AddChild(new MeshInstance3D
        {
            Name = "Visual",
            Mesh = new PlaneMesh { Size = new Vector2(Size, Size) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromHtml("#5a8f4f"), Roughness = 0.95f },
        });

        AddChild(new CollisionShape3D
        {
            Name = "Collision",
            Shape = new BoxShape3D { Size = new Vector3(Size, Thickness, Size) },
            Position = new Vector3(0, -Thickness / 2f, 0), // верх коробки совпадает с y=0 (поверхность плитки)
        });
    }
}
