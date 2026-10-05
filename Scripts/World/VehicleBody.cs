using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.World;

/// <summary>
/// <see cref="RigidBody3D"/> постройки, заспавненной из верстака (см. <see cref="VehicleSpawner"/>) — тонкая
/// обёртка над обычным телом, чтобы помнить, с какого верстака она появилась (для возврата по `R`, см.
/// <see cref="GameWorld"/>), и переключать вид solid/коллизия по `F1` (отладочное меню, см. Docs/05, «Дебаг меню»).
/// </summary>
public partial class VehicleBody : RigidBody3D
{
    /// <summary>Верстак, с которого была заспавнена эта постройка — <c>R</c> по наведению возвращает игрока сюда
    /// и убирает саму постройку (см. <see cref="GameWorld"/>).</summary>
    public Workbench? SourceWorkbench { get; set; }

    /// <summary>Рантайм функциональных блоков этой постройки (состояния кнопок и т.п., см. <see cref="FunctionalBlockRuntime"/>) —
    /// создаётся <see cref="VehicleSpawner"/>, шагается здесь каждый физический тик. Состояния не сохраняются и не
    /// реплицируются (см. Docs/05, «Рантайм функциональных блоков»).</summary>
    public FunctionalBlockRuntime? Runtime { get; set; }

    public override void _PhysicsProcess(double delta) => Runtime?.Tick(delta);

    /// <summary>Дебаг-меню (F2): принудительно запитать все блоки этой постройки (заглушка питания, см.
    /// <see cref="FunctionalBlockRuntime.DebugForcePowered"/>).</summary>
    public void SetDebugPowered(bool powered)
    {
        if (Runtime != null) Runtime.DebugForcePowered = powered;
    }

    /// <summary>Дебаг-меню (F3): нажать (true) / отпустить (false) все кнопки постройки — пока единственный способ
    /// нажать кнопку, определения "в какую целится игрок" ещё нет.</summary>
    public void SetDebugButtonsPressed(bool pressed) =>
        Runtime?.InteractAll<ButtonBehavior>(pressed ? BlockInteraction.Press : BlockInteraction.Release);

    public override void _ExitTree() => Runtime?.Dispose();

    private VoxelWorld? _visual;
    private readonly List<MeshInstance3D> _collisionOverlays = new();
    private bool _showingCollision;

    /// <summary>Обычный solid-рендер постройки (см. <see cref="VehicleSpawner"/>) — скрывается, пока показан вид
    /// коллизии (<see cref="SetDebugCollisionView"/>).</summary>
    public void RegisterVisual(VoxelWorld visual) => _visual = visual;

    /// <summary>
    /// <paramref name="show"/> = true — видны только полупрозрачные коробки коллизии (по одной на каждый
    /// <see cref="CollisionShape3D"/>, см. <see cref="VehicleSpawner"/> — там ровно по одной на экземпляр блока),
    /// solid-меш скрыт; false — обычный вид. Коробки строятся один раз лениво при первом включении, дальше только
    /// переключается видимость — не пересобираются каждый раз.
    /// </summary>
    public void SetDebugCollisionView(bool show)
    {
        if (_showingCollision == show) return;
        _showingCollision = show;

        if (_visual != null) _visual.Visible = !show;

        if (show && _collisionOverlays.Count == 0) BuildCollisionOverlays();
        foreach (var overlay in _collisionOverlays) overlay.Visible = show;
    }

    private void BuildCollisionOverlays()
    {
        foreach (var child in GetChildren())
        {
            if (child is not CollisionShape3D { Shape: BoxShape3D box } shape) continue;

            var overlay = new MeshInstance3D
            {
                Name = $"{shape.Name}DebugOverlay",
                Mesh = new BoxMesh { Size = box.Size },
                Transform = shape.Transform, // с поворотом: коллизия функционального блока может быть повёрнута (см. VehicleSpawner)
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = new Color(0.25f, 1f, 0.35f, 0.45f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                },
                Visible = false,
            };
            AddChild(overlay);
            _collisionOverlays.Add(overlay);
        }
    }
}
