using System;
using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.World;

/// <summary>Итог последнего шага по одному колесу (для самотестов и отладки).</summary>
/// <param name="Grounded">Хоть один луч веера попал в землю/постройку.</param>
/// <param name="Compression">Глубина вдавливания в точке контакта, м (0, если не касается).</param>
/// <param name="NormalForce">Вертикальная сила пружины N, Н — она же даёт предел трения μ·N.</param>
/// <param name="Point">Точка контакта в мире.</param>
/// <param name="Normal">Нормаль поверхности в точке контакта.</param>
public readonly record struct WheelContact(int InstanceId, bool Grounded, double Compression, double NormalForce, Vector3 Point, Vector3 Normal);

/// <summary>
/// Физика колёс ОДНОГО тела постройки (<see cref="VehicleBody"/>): лучи, пружина и силы трения. Рантайм (<see cref="WheelBehavior"/>) только ведёт состояние колеса; всё, что
/// требует мира, считается здесь раз в физический тик и прикладывается к телу как силы в точках контакта (<see cref="RigidBody3D.ApplyForce(Vector3, Vector3)"/>) —
/// «raycast-колесо», а не тело на шарнире (см. Docs/05, «Колесо»).
/// <para/>
/// <b>Контакт.</b> Из центра колеса (центр корневой клетки блока) в плоскости колеса (перпендикулярной оси, повёрнутой рулём) веером в <see cref="RayCount"/> лучей на
/// ±<see cref="FanHalfAngleDegrees"/> от «вниз колеса» бьют лучи длиной R (радиус шины, у пробитой — диска). Контакт — по самому глубокому лучу: глубина = R − расстояние от центра
/// до плоскости попадания (по нормали попавшей поверхности, так край ступеньки и склон даёт честную глубину). Маска — всё, кроме своего тела и игроков
/// (<see cref="CharacterBody3D"/>): земля и чужие постройки.
/// <para/>
/// <b>Силы</b> (в системе контакта: <c>f</c> = ось × нормаль — «вперёд», <c>s</c> — вдоль оси, <c>n</c> — нормаль):
/// <list type="number">
/// <item><b>Нормальная</b> — жёсткая пружина с демпфированием вдоль нормали: <c>N = k·глубина + c·скорость_сближения</c>, не меньше 0. Жёсткость подобрана так, чтобы при равной
/// нагрузке на все колёса ход контакта (упругость шины) был <see cref="ContactTravelMeters"/> = 3 см: <c>k = m·g/(n·0.03)</c>, демпфирование — доля <see cref="DampingRatio"/> от критического.
/// Подвески нет: колёса жёстко сидят на теле.</item>
/// <item><b>Продольная</b> — привод/качение: <c>μN·sat((ω·R − v_f)/vref)</c>; плюс торможение <c>−тормоз·μN·sat(v_f/vref)</c> и малое сопротивление качению.</item>
/// <item><b>Боковая</b> — против бокового скольжения: <c>−μN·sat(v_s/vref)</c>.</item>
/// </list>
/// <item><b>Трение покоя</b> (колесо под тормозом и почти стоит): вместо вязкого трения — пружина к точке, где колесо «прилипло» к земле (см. <see cref="Rig.Anchor"/>). Вязкая сила пропорциональна скорости,
/// поэтому на склоне машина с зажатым тормозом ползла бы со скоростью, при которой вязкая сила уравновешивает скатывающую (≈5 см/с на 10°); пружина даёт силу от СМЕЩЕНИЯ, и машина стоит.</item>
/// Продольная и боковая делят один круг трения (суммарный вектор не длиннее <c>μN</c>): юз при резком руле или торможении получается сам. Каждая составляющая дополнительно
/// ограничена так, чтобы за один тик не перепрыгнуть скорость, к которой стремится (<c>0.5·m/(n·dt)·|ошибка|</c>) — явная схема остаётся устойчивой. Свободное колесо
/// (не ведомое) подстраивает обороты под скорость земли (<see cref="FreeSpinTime"/>); в воздухе крутится по инерции с малым затуханием. Реакция прикладывается и к чужому
/// <see cref="RigidBody3D"/>, на котором колесо стоит.
/// </summary>
public sealed class WheelPhysics : IDisposable
{
    public const int RayCount = 25;
    public const double FanHalfAngleDegrees = 80;

    /// <summary>Ход контакта (вдавливание шины) при равной нагрузке на колёса, м.</summary>
    public const double ContactTravelMeters = 0.03;

    /// <summary>Доля критического демпфирования вертикальной пружины.</summary>
    public const double DampingRatio = 0.6;

    public const double TireFriction = 1.1;

    /// <summary>Трение диска по земле (пробитая шина) — заметно ниже, чем у шины.</summary>
    public const double RimFriction = 0.45;

    /// <summary>Во сколько раз трение скольжения (заблокированное колесо) ниже трения качения.</summary>
    public const double SlideFactor = 0.85;

    /// <summary>Скорость проскальзывания (м/с), при которой сила трения доходит до предела.</summary>
    public const double SlipReferenceSpeed = 0.25;

    public const double RollingResistance = 0.02;

    /// <summary>Постоянная времени подстройки свободного колеса под скорость земли, с.</summary>
    public const double FreeSpinTime = 0.04;

    /// <summary>Затухание оборотов колеса в воздухе, 1/с.</summary>
    public const double AirSpinDrag = 0.3;

    /// <summary>Глубина вдавливания не берётся больше этой доли радиуса (защита от гигантских сил при почти-пробое).</summary>
    public const double MaxDepthFraction = 0.6;

    /// <summary>В нормаль контакта входят лучи, чьё вдавливание больше этой доли глубочайшего (см. <see cref="Step"/>).</summary>
    public const double BlendDepthFraction = 0.6;

    /// <summary>Сила торможения (Variable brake) от этого значения и полный тормоз «держат» колесо: на почти стоящем колесе включается трение покоя.</summary>
    public const double HoldBrakeThreshold = 0.99;

    /// <summary>Скорость точки контакта по земле, м/с, ниже которой колесо под тормозом «прилипает».</summary>
    public const double StickEngageSpeed = 0.05;

    /// <summary>Скорость, м/с, выше которой прилипшее колесо отпускается (его тянут/толкают сильнее, чем держит трение).</summary>
    public const double StickReleaseSpeed = 0.3;

    /// <summary>Жёсткость пружины прилипания в долях <c>m/dt²</c> (масса на колесо): 0.1 - явная схема остаётся устойчивой с большим запасом.</summary>
    public const double StickStiffnessFactor = 0.1;

    private sealed class Rig
    {
        public required int InstanceId { get; init; }
        public required WheelState State { get; init; }
        public required Vector3 HubLocal { get; init; }
        public required Basis FrameBasis { get; init; }

        /// <summary>Точка «прилипания» колеса к земле (трение покоя): в мировых координатах или, если колесо стоит на чужом теле, в его локальных. null - колесо скользит/катится.</summary>
        public Vector3? Anchor { get; set; }
        public GodotObject? AnchorCollider { get; set; }
    }

    private readonly VehicleBody _body;
    private readonly Construction _construction;
    private readonly List<Rig> _rigs = new();
    private readonly List<WheelContact> _contacts = new();
    private readonly double[] _rayDepth = new double[RayCount];
    private readonly Vector3[] _rayNormal = new Vector3[RayCount];
    private readonly PhysicsRayQueryParameters3D _query = PhysicsRayQueryParameters3D.Create(Vector3.Zero, Vector3.Zero);
    private bool _dirty = true;

    public WheelPhysics(VehicleBody body, Construction construction)
    {
        _body = body;
        _construction = construction;
        _construction.Changed += MarkDirty;
    }

    public void Dispose() => _construction.Changed -= MarkDirty;

    private void MarkDirty() => _dirty = true;

    /// <summary>Сколько колёс сейчас участвует в физике.</summary>
    public int WheelCount
    {
        get
        {
            if (_dirty) Rebuild();
            return _rigs.Count;
        }
    }

    /// <summary>Контакты последнего шага (по одному на колесо).</summary>
    public IReadOnlyList<WheelContact> Contacts => _contacts;

    private void Rebuild()
    {
        _dirty = false;
        _rigs.Clear();
        var runtime = _body.Runtime;
        if (runtime == null) return;

        foreach (var instance in _construction.Instances)
        {
            if (!BlockCatalog.Instance.TryGetBySlug(instance.BlockSlug, out var definition)) continue;
            var block = definition.GetComponent<FunctionalBlockComponent>();
            if (block == null || block.Behavior != WheelBehavior.Key) continue;
            var state = runtime.GetState<WheelState>(instance.InstanceId);
            if (state == null) continue;

            // Центр колеса — центр корневой клетки блока (модель рисуется так, чтобы узел Spin стоял в нём), оси — локальные оси блока в его рамке.
            var frame = FunctionalBlockGeometry.InstanceFrame(instance.Origin, block.FootprintMin, block.Footprint, instance.RotationSteps);
            _rigs.Add(new Rig
            {
                InstanceId = instance.InstanceId,
                State = state,
                HubLocal = frame * FunctionalBlockGeometry.ComputeNodeAnchor(Vector3I.Zero, block.FootprintMin, block.Footprint, BuildSpace.CellSize),
                FrameBasis = frame.Basis,
            });
        }

        if (_rigs.Count > 0) _body.CanSleep = false; // силы прикладываются каждый тик - спящее тело проспало бы разгон колёс
    }

    private static double Sat(double x) => Math.Clamp(x, -1.0, 1.0);

    /// <summary>Значение <paramref name="value"/>, не больше по модулю <paramref name="limit"/> (limit ≥ 0).</summary>
    private static double Cap(double value, double limit) => Math.Clamp(value, -limit, limit);

    /// <summary>Один физический тик: лучи → силы → обороты свободных колёс. Вызывается из <see cref="VehicleBody"/> после шага рантайма.</summary>
    public void Step(double delta)
    {
        if (_dirty) Rebuild();
        _contacts.Clear();
        if (_rigs.Count == 0 || delta <= 0) return;

        var space = _body.GetWorld3D().DirectSpaceState;
        var transform = _body.GlobalTransform;
        var centerOfMass = transform * _body.CenterOfMass;
        double mass = _body.Mass;
        double gravity = ProjectSettings.GetSetting("physics/3d/default_gravity").AsDouble() * _body.GravityScale;
        int count = _rigs.Count;
        double stiffness = mass * gravity / (count * ContactTravelMeters);
        double damping = 2 * DampingRatio * Math.Sqrt(stiffness * mass / count);
        double wheelMass = mass / count;
        var exclude = new Godot.Collections.Array<Rid> { _body.GetRid() };

        foreach (var rig in _rigs)
        {
            var state = rig.State;
            double radius = state.ContactRadius;
            var hub = transform * rig.HubLocal;
            var basis = transform.Basis * rig.FrameBasis * new Basis(Vector3.Up, state.SteerAngle);
            var axle = basis.X.Normalized();
            var up = basis.Y.Normalized();
            var forward = basis.Z.Normalized();

            // ---- веер лучей: самый глубокий
            double bestDepth = 0;
            Vector3 bestPoint = Vector3.Zero, bestNormal = Vector3.Up;
            GodotObject? bestCollider = null;
            Array.Clear(_rayDepth);
            for (int i = 0; i < RayCount; i++)
            {
                double angle = (-FanHalfAngleDegrees + 2 * FanHalfAngleDegrees * i / (RayCount - 1)) * Math.PI / 180;
                var direction = -up * Math.Cos(angle) + forward * Math.Sin(angle);
                if (!TryCast(space, hub, hub + direction * radius, exclude, out var point, out var normal, out var collider)) continue;

                double cosine = -direction.Dot(normal);
                if (cosine < 0.05) continue; // скользящее попадание: нормаль почти вдоль луча - глубина не определена
                double depth = radius - (point - hub).Length(); // вдавливание ВДОЛЬ луча: плоскость верха ступеньки не «подпирает» колесо раньше, чем ребро реально под ним
                _rayDepth[i] = depth;
                _rayNormal[i] = normal;
                if (depth > bestDepth)
                {
                    bestDepth = depth;
                    bestPoint = point;
                    bestNormal = normal;
                    bestCollider = collider;
                }
            }

            if (bestDepth > 1e-6)
            {
                // Нормаль контакта - среднее нормалей лучей, почти такой же глубины (у ребра ступеньки грань и верх дают диагональ, как у настоящего колеса на углу);
                // на ровной земле соседние лучи заметно мельче и в среднее не входят.
                var blended = Vector3.Zero;
                for (int i = 0; i < RayCount; i++) blended += _rayNormal[i] * Math.Max(0, _rayDepth[i] - BlendDepthFraction * bestDepth);
                if (blended.LengthSquared() > 1e-12) bestNormal = blended.Normalized();
            }

            if (bestDepth <= 1e-6)
            {
                // В воздухе: колесо крутится по инерции, полный тормоз его всё равно держит.
                if (state.FullBrake) state.Omega = 0;
                else if (!state.Driven) state.Omega *= Math.Exp(-AirSpinDrag * delta);
                _contacts.Add(new WheelContact(rig.InstanceId, false, 0, 0, Vector3.Zero, Vector3.Up));
                continue;
            }

            double compression = Math.Min(bestDepth, MaxDepthFraction * radius);

            // Точка контакта - проекция центра колеса на плоскость попадания, а не точка попадания выбранного луча: на ровной земле глубину дают несколько лучей веера с одинаковой
            // (до шума) глубиной, выбор между ними прыгал бы от тика к тику, а с ним - точка приложения силы на ±12 см вдоль колеса (момент тангажа, машина дрожит).
            bestPoint = hub - bestNormal * (radius - bestDepth);
            var other = bestCollider as RigidBody3D;
            if (other == _body) other = null;

            // ---- скорость точки контакта относительно поверхности
            var bodyVelocity = _body.LinearVelocity + _body.AngularVelocity.Cross(bestPoint - centerOfMass);
            var groundVelocity = Vector3.Zero;
            if (other != null)
            {
                var otherCenter = other.GlobalTransform * other.CenterOfMass;
                groundVelocity = other.LinearVelocity + other.AngularVelocity.Cross(bestPoint - otherCenter);
            }

            var relative = bodyVelocity - groundVelocity;

            // ---- нормальная сила: пружина + демпфер
            double approach = -relative.Dot(bestNormal);
            double normalForce = Math.Max(0, stiffness * compression + damping * approach);
            var force = bestNormal * normalForce;

            // ---- трение в плоскости контакта
            var longitudinal = axle.Cross(bestNormal);
            if (longitudinal.LengthSquared() > 0.01)
            {
                longitudinal = longitudinal.Normalized();
                var lateral = bestNormal.Cross(longitudinal);
                double speedForward = relative.Dot(longitudinal);
                double speedSide = relative.Dot(lateral);

                // Свободное колесо подстраивается под скорость земли; ведомое держит обороты сети (их ставит рантайм), заблокированное стоит.
                if (!state.FullBrake && !state.Driven) state.Omega = speedForward / radius; // свободное колесо катится без проскальзывания: тормозит его только сопротивление качению

                double staticLimit = (state.TireBroken ? RimFriction : TireFriction) * normalForce;
                double limit = staticLimit * (state.FullBrake ? SlideFactor : 1.0);

                // ---- трение покоя: колесо под тормозом, почти стоящее - пружина к точке прилипания
                bool held = state.FullBrake || state.BrakeInput >= HoldBrakeThreshold;
                double tangentSpeed = Math.Sqrt(speedForward * speedForward + speedSide * speedSide);
                if (!held || tangentSpeed > StickReleaseSpeed || (rig.Anchor != null && !ReferenceEquals(rig.AnchorCollider, bestCollider))) rig.Anchor = null;
                else if (rig.Anchor == null && tangentSpeed < StickEngageSpeed)
                {
                    rig.Anchor = other != null ? other.ToLocal(bestPoint) : bestPoint;
                    rig.AnchorCollider = bestCollider;
                }

                if (rig.Anchor is { } anchorStored)
                {
                    var anchorWorld = other != null ? other.ToGlobal(anchorStored) : anchorStored;
                    var offset = bestPoint - anchorWorld;
                    offset -= bestNormal * offset.Dot(bestNormal);

                    double stiffnessStick = StickStiffnessFactor * wheelMass / (delta * delta);
                    double dampingStick = 2 * 0.7 * Math.Sqrt(stiffnessStick * wheelMass);
                    if (offset.Length() * stiffnessStick > staticLimit && staticLimit > 1e-9)
                    {
                        // Тянут сильнее, чем держит трение: точка прилипания тащится за колесом (колесо проскальзывает, но не дальше предела).
                        offset *= staticLimit / (offset.Length() * stiffnessStick);
                        anchorWorld = bestPoint - offset;
                        rig.Anchor = other != null ? other.ToLocal(anchorWorld) : anchorWorld;
                    }

                    var tangentVelocity = relative - bestNormal * relative.Dot(bestNormal);
                    var stick = -offset * stiffnessStick - tangentVelocity * dampingStick;
                    if (stick.Length() > staticLimit && staticLimit > 1e-9) stick *= staticLimit / stick.Length();
                    force += stick;
                    _body.ApplyForce(force, bestPoint - _body.GlobalPosition);
                    other?.ApplyForce(-force, bestPoint - other.GlobalPosition);
                    _contacts.Add(new WheelContact(rig.InstanceId, true, compression, normalForce, bestPoint, bestNormal));
                    continue;
                }

                double slip = state.Omega * radius - speedForward;
                double ceiling = 0.5 * wheelMass / delta;

                double forwardForce = Cap(limit * Sat(slip / SlipReferenceSpeed), ceiling * Math.Abs(slip));
                double slowing = (state.FullBrake ? 0 : state.BrakeInput * limit) + RollingResistance * normalForce;
                forwardForce -= Cap(slowing * Sat(speedForward / SlipReferenceSpeed), ceiling * Math.Abs(speedForward));
                double sideForce = -Cap(limit * Sat(speedSide / SlipReferenceSpeed), ceiling * Math.Abs(speedSide));

                // Круг трения: суммарное усилие в плоскости контакта не больше μN.
                double magnitude = Math.Sqrt(forwardForce * forwardForce + sideForce * sideForce);
                if (magnitude > limit && magnitude > 1e-9)
                {
                    forwardForce *= limit / magnitude;
                    sideForce *= limit / magnitude;
                }

                force += longitudinal * forwardForce + lateral * sideForce;
            }

            _body.ApplyForce(force, bestPoint - _body.GlobalPosition);
            other?.ApplyForce(-force, bestPoint - other.GlobalPosition);
            _contacts.Add(new WheelContact(rig.InstanceId, true, compression, normalForce, bestPoint, bestNormal));
        }
    }

    /// <summary>Луч от <paramref name="from"/> до <paramref name="to"/>; игроки (<see cref="CharacterBody3D"/>) пропускаются — луч идёт дальше сквозь них.</summary>
    private bool TryCast(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to, Godot.Collections.Array<Rid> exclude, out Vector3 point, out Vector3 normal, out GodotObject? collider)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            _query.From = from;
            _query.To = to;
            _query.Exclude = exclude;
            var hit = space.IntersectRay(_query);
            if (hit.Count == 0) break;

            var hitCollider = hit["collider"].AsGodotObject();
            if (hitCollider is CharacterBody3D)
            {
                exclude.Add(hit["rid"].AsRid());
                continue;
            }

            point = hit["position"].AsVector3();
            normal = hit["normal"].AsVector3();
            collider = hitCollider;
            return true;
        }

        point = Vector3.Zero;
        normal = Vector3.Up;
        collider = null;
        return false;
    }
}
