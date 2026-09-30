using Godot;

namespace SandboxPolyGame.World;

/// <summary>
/// Зона, где материализуются постройки, спавненные с верстака (см. <see cref="VehicleSpawner"/>,
/// <see cref="Workbench.SpawnArea"/>) — к одной зоне может быть привязано несколько верстаков (см.
/// Docs/05-world-and-vehicle-systems.md). Пока только позиция + размер площадки (квадрат) и её визуальная разметка
/// (полупрозрачный квад чуть выше земли) — никакой логики поиска свободного места внутри зоны ещё нет: одна зона
/// сейчас даёт РОВНО одну точку спавна (свой центр), это отдельная будущая задача, если в одной зоне одновременно
/// окажется несколько построек.
/// </summary>
public partial class SpawnArea : Node3D
{
    /// <summary>Сторона квадратной площадки в метрах.</summary>
    public float Size { get; set; } = 16f;

    public override void _Ready()
    {
        AddChild(new MeshInstance3D
        {
            Name = "Marker",
            Mesh = new PlaneMesh { Size = new Vector2(Size, Size) },
            Position = new Vector3(0, 0.02f, 0), // чуть выше земли - не сливается с ней (z-fighting)
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.25f, 0.65f, 1f, 0.25f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        });
    }
}
