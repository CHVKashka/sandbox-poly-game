using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Номер тела в сетевой игре (одинаковый у сервера и всех клиентов, см. <see cref="Core.NetHub"/>); 0 — одиночная игра.</summary>
    public int NetId { get; set; }

    /// <summary>Имя верстака, с которого заспавнили (строкой: в сетевой игре у каждого пира свой <see cref="GameWorld"/> и свои узлы верстаков).</summary>
    public string SourceWorkbenchName { get; set; } = "";

    /// <summary>
    /// Копия-наблюдатель серверного тела (клиент в сетевой игре): не симулируется (<c>Freeze</c> в кинематическом режиме), положение плавно догоняет то, что присылает сервер
    /// (<see cref="SetPuppetTarget"/>), рантайм — зеркало (<see cref="FunctionalBlockRuntime.IsMirror"/>). Игрок может стоять на нём и ездить (<see cref="Player.Platform"/>).
    /// </summary>
    public bool IsPuppet { get; private set; }

    private Transform3D _puppetTarget;
    private Vector3 _puppetVelocity;
    private double _puppetAge;
    private bool _hasPuppetTarget;

    /// <summary>Максимальное время экстраполяции по скорости без новых пакетов, с (потом тело стоит на последней известной позе).</summary>
    private const double PuppetMaxExtrapolation = 0.3;

    public void MakePuppet()
    {
        IsPuppet = true;
        FreezeMode = FreezeModeEnum.Kinematic;
        Freeze = true;
        if (Runtime != null) Runtime.IsMirror = true;
    }

    /// <summary>Новая поза с сервера: <paramref name="snap"/> — встать сразу (первый пакет), иначе плавно догнать за несколько тиков.</summary>
    public void SetPuppetTarget(Transform3D target, Vector3 velocity, bool snap = false)
    {
        _puppetTarget = target;
        _puppetVelocity = velocity;
        _puppetAge = 0;
        _hasPuppetTarget = true;
        if (snap) GlobalTransform = target;
    }

    /// <summary>Поза, к которой тянется копия-наблюдатель (для самотестов).</summary>
    public Transform3D PuppetTarget => _puppetTarget;

    public override void _PhysicsProcess(double delta)
    {
        if (!IsPuppet)
        {
            Runtime?.Tick(delta);
            return;
        }

        if (!_hasPuppetTarget) return;

        _puppetAge += delta;
        if (_puppetAge < PuppetMaxExtrapolation) _puppetTarget = new Transform3D(_puppetTarget.Basis, _puppetTarget.Origin + _puppetVelocity * (float)delta);
        var current = GlobalTransform;
        GlobalTransform = current.Origin.DistanceTo(_puppetTarget.Origin) > 5 ? _puppetTarget : current.InterpolateWith(_puppetTarget, 0.5f);
    }

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

    /// <summary>Постройка (набор блоков) этого тела — null, пока не зарегистрирован визуал (<see cref="RegisterVisual"/>).</summary>
    public Core.Construction? Construction => _visual?.Construction;

    /// <summary>
    /// Какой блок постройки занимает точка столкновения луча: <paramref name="worldPoint"/>/<paramref name="worldNormal"/> — попадание по коллайдеру тела
    /// (<see cref="PhysicsDirectSpaceState3D.IntersectRay"/>). Точка чуть углубляется внутрь по нормали, переводится в локальные координаты тела (оно же
    /// пространство клеток постройки, см. <see cref="VehicleSpawner"/>) и ищется владелец клетки. null — не попали ни в один блок.
    /// </summary>
    public Core.BlockInstance? TryGetInstanceAt(Vector3 worldPoint, Vector3 worldNormal)
    {
        if (Construction == null) return null;
        var local = ToLocal(worldPoint - worldNormal * 0.02f);
        var cell = new Vector3I(
            Mathf.FloorToInt(local.X / Core.BuildSpace.CellSize), Mathf.FloorToInt(local.Y / Core.BuildSpace.CellSize), Mathf.FloorToInt(local.Z / Core.BuildSpace.CellSize));
        return Construction.GetOwner(cell);
    }

    /// <summary>
    /// Положение глаз пилота на сиденье <paramref name="instanceId"/> (мировые координаты) и направление «вперёд» сиденья в мире (горизонтальный вектор).
    /// Глаза — над центром клетки ноды <c>occuped</c> на <c>eyeHeight</c> метров вверх (в осях блока, поворачивается вместе с ним); нет такой ноды — над центром
    /// footprint'а. Направление — грань <c>forward</c> блока (<see cref="PilotSeatBehavior.ReadSeatSettings"/>). null — это не сиденье.
    /// </summary>
    public (Vector3 Eye, Vector3 Forward)? GetSeatPose(int instanceId)
    {
        var instance = Construction?.GetInstance(instanceId);
        if (instance == null || !Blocks.BlockCatalog.Instance.TryGetBySlug(instance.BlockSlug, out var definition)) return null;
        var block = definition.GetComponent<Blocks.FunctionalBlockComponent>();
        if (block == null || block.Behavior != PilotSeatBehavior.Key) return null;

        var (eyeHeight, forwardFace) = PilotSeatBehavior.ReadSeatSettings(block);
        var seatNode = block.Nodes.FirstOrDefault(n => n.Id == PilotSeatBehavior.OccupiedNodeId);
        var cellCenter = seatNode != null
            ? Editor.FunctionalBlockGeometry.ComputeNodeAnchor(seatNode.Cell, block.FootprintMin, block.Footprint, Core.BuildSpace.CellSize)
            : (new Vector3(block.FootprintMin.X, block.FootprintMin.Y, block.FootprintMin.Z) + new Vector3(block.Footprint.X, block.Footprint.Y, block.Footprint.Z) * 0.5f) * Core.BuildSpace.CellSize;

        var frame = Editor.FunctionalBlockGeometry.InstanceFrame(instance.Origin, block.FootprintMin, block.Footprint, instance.RotationSteps);
        var eyeLocal = frame * (cellCenter + new Vector3(0, (float)eyeHeight, 0));
        var normalLocal = Vector3.Zero;
        normalLocal[(int)forwardFace / 2] = ((int)forwardFace & 1) == 1 ? 1 : -1;
        var forwardWorld = GlobalTransform.Basis * (frame.Basis * normalLocal);
        forwardWorld.Y = 0;
        return (ToGlobal(eyeLocal), forwardWorld.LengthSquared() > 1e-6 ? forwardWorld.Normalized() : Vector3.Forward);
    }

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
