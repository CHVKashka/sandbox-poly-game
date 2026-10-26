using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Runtime;
using SandboxPolyGame.World;

namespace SandboxPolyGame.Dev;

/// <summary>
/// Колесо на настоящей физике: езда, реверс/отражение, руль, тормоз, склон, ступенька. Машина — фикстуры (шасси 8x1x12, четыре колеса, два мотора на задней оси, сиденье как источник сигналов),
/// провода как у настоящей постройки (сиденье → моторы / руль / тормоз), питание — отладочный флаг. Никаких блоков из blocks/. Все ожидания — числа, посчитанные из параметров фикстур.
/// Проверено ТОЛЬКО числами: глазами машину никто не видел, по сети (физика на сервере, копии у клиентов) она не гонялась. Пробитая шина и поломка колеса не проверяются (нечем вызвать, нет системы урона).
/// </summary>
public sealed partial class SelfTest
{
    private const string DriveWheelJson =
        "{ \"footprint\": [1,3,3], \"footprintMin\": [0,-1,-1], \"footprintManual\": true, \"behavior\": \"Wheel\", " +
        "\"ports\": [ { \"id\": \"RPS\", \"resource\": \"Torque\", \"direction\": \"In\", \"face\": \"PosX\", \"position\": [0,0] } ], " +
        "\"nodes\": [ { \"id\": \"Brake\", \"type\": \"Boolean\", \"direction\": \"In\", \"position\": [0,0,0] }, " +
        "{ \"id\": \"Variable brake\", \"type\": \"Number\", \"direction\": \"In\", \"position\": [0,0,1] }, " +
        "{ \"id\": \"Steering\", \"type\": \"Number\", \"direction\": \"In\", \"position\": [0,0,-1] } ] }";

    // Те же параметры и границы, что у blocks/wheel_3x3.xml (контракт блока), но фикстурой.
    private const string DriveWheelParameters =
        "{ \"parameters\": [" +
        "{ \"id\": \"tireRadius\", \"label\": \"Tire radius\", \"type\": \"Float\", \"min\": 1.5, \"max\": 2.4, \"default\": 1.5 }," +
        "{ \"id\": \"rimRadius\", \"label\": \"Rim radius\", \"type\": \"Float\", \"min\": 1.0, \"max\": 1.6, \"default\": 1.0 }," +
        "{ \"id\": \"maxSteerDegrees\", \"label\": \"Max steer angle\", \"type\": \"Float\", \"min\": 5, \"max\": 90, \"default\": 60 }," +
        "{ \"id\": \"steerRateDegrees\", \"label\": \"Steer speed\", \"type\": \"Float\", \"min\": 30, \"max\": 720, \"default\": 180 }," +
        "{ \"id\": \"reverse\", \"label\": \"Reverse\", \"type\": \"Bool\", \"default\": false } ] }";

    // Мотор с валом в обе стороны по X: слева от шасси он касается колеса грани NegX, справа - PosX.
    private const string DriveMotorJson =
        "{ \"behavior\": \"ElectricMotor\", \"ports\": [ " +
        "{ \"id\": \"left\", \"resource\": \"Torque\", \"direction\": \"Out\", \"face\": \"NegX\", \"position\": [0,0] }, " +
        "{ \"id\": \"right\", \"resource\": \"Torque\", \"direction\": \"Out\", \"face\": \"PosX\", \"position\": [0,0] } ], " +
        "\"nodes\": [ { \"id\": \"sig\", \"type\": \"Number\", \"direction\": \"In\" }, { \"id\": \"pwr\", \"type\": \"Electricity\", \"direction\": \"In\" } ] }";

    private static string DriveChassisJson()
    {
        var cells = new List<string>();
        for (int x = 0; x < 8; x++) for (int z = 0; z < 12; z++) cells.Add($"[{x},0,{z}]");
        return "{ \"footprint\": [8,1,12], \"collision\": [" + string.Join(",", cells) + "] }";
    }

    private static IDisposable UseDriveFixtures() => UseCatalogWith(
        new FixtureBlock("zz_drv_chassis", DriveChassisJson()),
        new FixtureBlock("zz_drv_wheel", DriveWheelJson, DriveWheelParameters),
        new FixtureBlock("zz_drv_motor", DriveMotorJson, MotorParameters),
        new FixtureBlock("zz_drv_seat", SeatJson(), SeatParameters()));

    /// <summary>Настройки тестовой машины. Нос машины - +Z; передние колёса (ряд клеток z=10) рулевые и свободные, задние (z=1) ведущие; левые колёса стоят как есть, правые повёрнуты на 180° вокруг Y и имеют reverse.</summary>
    private sealed class DriveOptions
    {
        public int PlateLevel = 1;
        public bool Motors = true;
        public bool FlipReverse;
        public Vector3I FrontMirror, RearMirror;
        public double MaxRpm = 120;
        public double MaxSteerDegrees = 30;
        public double SteerRateDegrees = 60;
        public string? TireRadiusCells;
    }

    /// <summary>Собранная и заспавненная тестовая машина.</summary>
    private sealed class DriveCar
    {
        public required VehicleBody Body { get; init; }
        public required PilotSeatState Seat { get; init; }
        public required List<(Vector3I Root, WheelState State)> Wheels { get; init; }

        public IEnumerable<WheelState> Front => Wheels.Where(w => w.Root.Z == 10).Select(w => w.State);
        public IEnumerable<WheelState> Rear => Wheels.Where(w => w.Root.Z == 1).Select(w => w.State);

        public double Throttle { set => Seat.AxisValue[1] = value; }
        public double Steering { set => Seat.AxisValue[0] = value; }
        public double VariableBrake { set => Seat.AxisValue[3] = value; }
        public bool FullBrake { set { for (int i = 0; i < Seat.Hotkeys.Length; i++) Seat.Hotkeys[i] = value; } }

        public Basis Basis => Body.GlobalTransform.Basis;
        public double Forward => Body.LinearVelocity.Dot(Basis.Z);
        public double Side => Body.LinearVelocity.Dot(Basis.X);
        public double Yaw => Math.Atan2(Basis.Z.X, Basis.Z.Z);

        public Vector3 Hub(int x, int z) => Body.ToGlobal(BuildSpace.CellCenter(new Vector3I(x, 0, z)));
        public Vector3 FrontHub => Hub(-1, 10);
        public Vector3 RearHub => Hub(-1, 1);
    }

    private static BlockInstance PlaceDriveWheel(Construction construction, Vector3I cell, Vector3I steps, Vector3I mirror)
    {
        var definition = BlockCatalog.Instance.Get("zz_drv_wheel");
        var fn = definition.GetComponent<FunctionalBlockComponent>()!;
        var (origin, size) = BlockFootprint.PlaceBox(cell, fn.FootprintMin, fn.Footprint, steps);
        return construction.PlaceBlock(origin, size, definition, Colors.White, steps, mirror)!;
    }

    private string BuildDriveCarJson(DriveOptions options)
    {
        var construction = new Construction(new VoxelGrid());
        PlaceFixture(construction, "zz_drv_chassis", new Vector3I(0, options.PlateLevel, 0));
        var seat = PlaceFixture(construction, "zz_drv_seat", new Vector3I(2, options.PlateLevel + 1, 5));
        string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        var errors = new List<string>();

        void Wire(BlockInstance from, string fromNode, BlockInstance to, string toNode)
        {
            if (!construction.TryConnect(from.InstanceId, fromNode, to.InstanceId, toNode, out string error)) errors.Add($"{fromNode}->{toNode}: {error}");
        }

        foreach (int z in new[] { 1, 10 })
        {
            foreach (bool right in new[] { false, true })
            {
                var wheel = PlaceDriveWheel(construction, new Vector3I(right ? 8 : -1, 0, z), right ? new Vector3I(0, 2, 0) : Vector3I.Zero, z == 10 ? options.FrontMirror : options.RearMirror);
                if (right ^ options.FlipReverse) construction.TrySetParameter(wheel, "reverse", "true");
                construction.TrySetParameter(wheel, "maxSteerDegrees", Number(options.MaxSteerDegrees));
                construction.TrySetParameter(wheel, "steerRateDegrees", Number(options.SteerRateDegrees));
                if (options.TireRadiusCells != null) construction.TrySetParameter(wheel, "tireRadius", options.TireRadiusCells);
                Wire(seat, "ud_out", wheel, "Variable brake");
                Wire(seat, "hotkey_1", wheel, "Brake");
                if (z == 10) Wire(seat, "ad_out", wheel, "Steering");

                if (z == 1 && options.Motors)
                {
                    var motor = PlaceFixture(construction, "zz_drv_motor", new Vector3I(right ? 7 : 0, 0, z));
                    construction.TrySetParameter(motor, "maxRpm", Number(options.MaxRpm));
                    Wire(seat, "ws_out", motor, "sig");
                }
            }
        }

        Check(errors.Count == 0, "(setup) the test car's wires are accepted (throttle -> motors, steering -> front wheels, brakes -> all)", string.Join("; ", errors));
        return ConstructionIO.Serialize(construction);
    }

    private async Task<DriveCar> SpawnDriveCar(BuildEditor editor, Node3D parent, DriveOptions options, Vector3? at = null)
    {
        var body = VehicleSpawner.Spawn(parent, BuildDriveCarJson(options), at ?? new Vector3(0, 0.02f, 0));
        body.SetDebugPowered(true);
        await PhysicsFrames(editor, 2);

        var wheels = new List<(Vector3I, WheelState)>();
        PilotSeatState? seat = null;
        foreach (var instance in body.Construction!.Instances)
        {
            if (instance.BlockSlug == "zz_drv_seat") seat = body.Runtime!.GetState<PilotSeatState>(instance.InstanceId);
            if (instance.BlockSlug != "zz_drv_wheel") continue;
            var fn = BlockCatalog.Instance.Get(instance.BlockSlug).GetComponent<FunctionalBlockComponent>()!;
            var root = BlockFootprint.RootCell(instance.Origin, fn.FootprintMin, fn.Footprint, instance.RotationSteps);
            wheels.Add((root, body.Runtime!.GetState<WheelState>(instance.InstanceId)!));
        }

        for (int i = 0; i < PilotSeatBehavior.AxisCount; i++) seat!.Modes[i] = SeatAxisMode.Sticky; // оси держат выставленное значение, как «ручка газа»
        return new DriveCar { Body = body, Seat = seat!, Wheels = wheels };
    }

    private static async Task Step(BuildEditor editor, int frames) => await PhysicsFrames(editor, frames);

    private static StaticBody3D MakeFloor(Node parent, Basis? basis = null)
    {
        var floor = new StaticBody3D { Name = "WheelDriveFloor", Transform = new Transform3D(basis ?? Basis.Identity, Vector3.Zero) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(200, 1, 200) }, Position = new Vector3(0, -0.5f, 0) });
        parent.AddChild(floor);
        return floor;
    }

    private async Task RunWheelDrivingTests(BuildEditor editor)
    {
        GD.Print("-- wheel physics: driving, reverse and mirror, steering, brakes, slope, step (numbers only; fixtures, no real blocks)");

        using var scope = UseDriveFixtures();
        var world = new Node3D { Name = "WheelDriveWorld" };
        editor.AddChild(world);
        var floor = MakeFloor(world);
        double dt = 1.0 / Engine.PhysicsTicksPerSecond;
        int Sec(double seconds) => (int)Math.Round(seconds / dt);

        await RunWheelDriveStraightTests(editor, world, Sec);
        await RunWheelReverseMirrorTests(editor, world, Sec);
        await RunWheelSteeringTests(editor, world, Sec);
        await RunWheelBrakeTests(editor, world, Sec);
        await RunWheelSlopeTests(editor, world, floor, Sec);
        await RunWheelStepTests(editor, world, Sec);

        floor.QueueFree();
        world.QueueFree();
        await Frames(editor, 2);
    }

    // ------------------------------------------------------------------ а) покой и езда

    private async Task RunWheelDriveStraightTests(BuildEditor editor, Node3D world, Func<double, int> sec)
    {
        var car = await SpawnDriveCar(editor, world, new DriveOptions());
        Check(car.Body.Wheels is { WheelCount: 4 } && car.Wheels.Count == 4 && car.Rear.All(w => w.InNetwork) && car.Front.All(w => !w.InNetwork),
            "(setup) four wheels; the rear ones sit on a shaft network with their motors, the front ones are free", $"{car.Body.Wheels?.WheelCount}");
        double tireRadius = car.Wheels[0].State.TireRadius;
        double wheelBase = car.FrontHub.Z - car.RearHub.Z;
        Check(Math.Abs(tireRadius - 0.375) < 1e-9 && Math.Abs(wheelBase - 2.25) < 1e-6, "(setup) tire radius 1.5 cells = 0.375 m, wheelbase 9 cells = 2.25 m", $"R={tireRadius} L={wheelBase}");

        await Step(editor, sec(4)); // осесть

        // ---- ж) покой: сжатие шины, прыжки, дрейф
        double minY = double.MaxValue, maxY = double.MinValue, minCompression = double.MaxValue, maxCompression = 0, maxSpeed = 0;
        var restStart = car.Body.GlobalPosition;
        for (int i = 0; i < sec(2); i++)
        {
            await Step(editor, 1);
            minY = Math.Min(minY, car.Body.GlobalPosition.Y); maxY = Math.Max(maxY, car.Body.GlobalPosition.Y);
            maxSpeed = Math.Max(maxSpeed, car.Body.LinearVelocity.Length());
            foreach (var contact in car.Body.Wheels!.Contacts)
            {
                minCompression = Math.Min(minCompression, contact.Compression);
                maxCompression = Math.Max(maxCompression, contact.Compression);
            }
        }

        double drift = (car.Body.GlobalPosition - restStart).Length();
        string rest = $"compression {minCompression * 1000:0.0}..{maxCompression * 1000:0.0} mm, height range {(maxY - minY) * 1000:0.000} mm, drift {drift * 1000:0.000} mm, max speed {maxSpeed:0.0000} m/s";
        GD.Print("  drive car at rest: " + rest);
        Check(minCompression >= 0.010 && maxCompression <= 0.050, "at rest every tire is pressed 10..50 mm (the loaded car, with a seat on top and the motors on one side)", rest);
        Check(maxY - minY < 0.0005 && drift < 0.005 && maxSpeed < 0.01, "...it does not bounce (< 0.5 mm), wander (< 5 mm in 2 s) or creep (speed < 1 cm/s)", rest);

        // ---- а) езда: газ на ведущие колёса
        car.Throttle = 1.0;
        var samples = new List<double>();
        double cruiseMinY = double.MaxValue, cruiseMaxY = double.MinValue, maxSide = 0, maxYawDeg = 0;
        var start = car.Body.GlobalPosition;
        double yawStart = car.Yaw;
        int total = sec(3.0);
        for (int i = 1; i <= total; i++)
        {
            await Step(editor, 1);
            if (i % sec(0.5) == 0) samples.Add(car.Forward);
            if (i > sec(1.5)) { cruiseMinY = Math.Min(cruiseMinY, car.Body.GlobalPosition.Y); cruiseMaxY = Math.Max(cruiseMaxY, car.Body.GlobalPosition.Y); }
            maxSide = Math.Max(maxSide, Math.Abs(car.Side));
            maxYawDeg = Math.Max(maxYawDeg, Math.Abs(Mathf.RadToDeg(car.Yaw - yawStart)));
        }

        double topSpeed = 120 * 2 * Math.PI / 60 * tireRadius; // обороты мотора * радиус шины
        var travelled = car.Body.GlobalPosition - start;
        string drive = $"forward speed every 0.5 s: {string.Join(", ", samples.Select(s => s.ToString("0.00")))} m/s (wheel speed {topSpeed:0.00}), side max {maxSide:0.000} m/s, yaw {maxYawDeg:0.00} deg, travelled {travelled.Z:0.00} m along Z and {travelled.X * 1000:0} mm sideways, height range at cruise {(cruiseMaxY - cruiseMinY) * 1000:0.00} mm";
        GD.Print("  drive: " + drive);
        Check(samples[0] > 0.5 && samples[1] > samples[0] + 0.5 && samples[2] > samples[1] - 0.05, "full throttle: the car speeds up along its nose (rising at 0.5 and 1.0 s, then holding the wheel speed)", drive);
        Check(samples[^1] > 0.85 * topSpeed && samples[^1] < 1.05 * topSpeed, "...and settles at the wheel speed (motor rpm * 2pi/60 * tire radius), not above it", drive);
        Check(car.Rear.All(w => Math.Abs(Math.Abs(w.Omega) - 120 * 2 * Math.PI / 60) < 0.3), "...the driven wheels spin at the motor speed (12.57 rad/s)", string.Join(", ", car.Rear.Select(w => w.Omega.ToString("0.00"))));
        Check(car.Front.All(w => Math.Abs(Math.Abs(w.Omega) * tireRadius - car.Forward) < 0.3), "...the free front wheels roll with the ground (|omega| * R = speed; the right-hand wheels are turned around, so their sign is opposite)", string.Join(", ", car.Front.Select(w => (w.Omega * tireRadius).ToString("0.00"))) + $" vs {car.Forward:0.00}");
        Check(travelled.Z > 6 && maxSide < 0.05 && Math.Abs(travelled.X) < 0.03 && maxYawDeg < 1.0, "...in a straight line: side speed < 5 cm/s, sideways shift < 3 cm, heading change < 1 degree", drive);
        Check(cruiseMaxY - cruiseMinY < 0.005, "...without hopping: the height changes by less than 5 mm while cruising", drive);

        car.Body.QueueFree();
        await Frames(editor, 2);
    }

    // ------------------------------------------------------------------ б) реверс и отражение

    private async Task RunWheelReverseMirrorTests(BuildEditor editor, Node3D world, Func<double, int> sec)
    {
        async Task<double> Speed(DriveOptions options)
        {
            var car = await SpawnDriveCar(editor, world, options);
            await Step(editor, sec(2));
            car.Throttle = 1.0;
            await Step(editor, sec(2.5));
            double speed = car.Forward;
            car.Body.QueueFree();
            await Frames(editor, 2);
            return speed;
        }

        double forward = await Speed(new DriveOptions());
        double reversed = await Speed(new DriveOptions { FlipReverse = true });
        double mirrored = await Speed(new DriveOptions { RearMirror = new Vector3I(0, 0, 1) });
        double both = await Speed(new DriveOptions { FlipReverse = true, RearMirror = new Vector3I(0, 0, 1) });
        double mirroredX = await Speed(new DriveOptions { RearMirror = new Vector3I(1, 0, 0) });
        string numbers = $"base {forward:0.00}, reverse flipped {reversed:0.00}, mirror Z {mirrored:0.00}, both {both:0.00}, mirror X {mirroredX:0.00} m/s";
        GD.Print("  reverse/mirror: " + numbers);
        Check(forward > 3, "reference: the car drives forward (+Z, along its nose) at full throttle", numbers);
        Check(reversed < -3 && Math.Abs(reversed + forward) < 0.6, "the 'reverse' parameter on the driven wheels turns the drive around: the same speed backwards", numbers);
        Check(mirrored < -3 && Math.Abs(mirrored + forward) < 0.6, "mirror Z (key O) does the same: the same speed backwards", numbers);
        Check(both > 3 && Math.Abs(both - forward) < 0.6, "'reverse' together with mirror Z cancel each other: forward again", numbers);
        Check(mirroredX > 3 && Math.Abs(mirroredX - forward) < 0.6, "mirror X (key U) does not change the direction of travel", numbers);
    }

    // ------------------------------------------------------------------ в) руль

    private async Task RunWheelSteeringTests(BuildEditor editor, Node3D world, Func<double, int> sec)
    {
        // Едем с постоянной скоростью (газ 0.6 -> 0.6 * скорость колеса), потом руль.
        async Task<(double YawRate, double Speed, double Front, double Rear, double SteerAt0_25, double Side, double HalfTurnYaw)> Turn(DriveOptions options, double steering)
        {
            var car = await SpawnDriveCar(editor, world, options);
            await Step(editor, sec(2));
            car.Throttle = 0.6;
            await Step(editor, sec(2));
            car.Steering = steering;
            await Step(editor, sec(0.25));
            double steerQuarter = car.Front.First().SteerAngle;
            await Step(editor, sec(1.0));
            double yawBefore = car.Yaw;
            double speedSum = 0, yawRateSum = 0, sideSum = 0;
            int n = sec(0.5);
            for (int i = 0; i < n; i++)
            {
                await Step(editor, 1);
                speedSum += car.Forward; yawRateSum += car.Body.AngularVelocity.Y; sideSum += Math.Abs(car.Side);
            }

            var result = (yawRateSum / n, speedSum / n, car.Front.First().SteerAngle, car.Rear.First().SteerAngle, steerQuarter, sideSum / n, car.Yaw - yawBefore);
            car.Body.QueueFree();
            await Frames(editor, 2);
            return result;
        }

        var straight = await Turn(new DriveOptions(), 0);
        var left = await Turn(new DriveOptions(), 1);
        var right = await Turn(new DriveOptions(), -1);
        var mirrored = await Turn(new DriveOptions { FrontMirror = new Vector3I(1, 0, 0) }, 1);

        double maxSteer = Mathf.DegToRad(30);
        double wheelBase = 9 * 0.25;
        double kinematic = left.Speed * Math.Tan(maxSteer) / wheelBase; // велосипедная модель: ω = v·tg(δ)/L
        string numbers = $"straight: yaw rate {straight.YawRate:0.000} rad/s; +1: yaw rate {left.YawRate:0.000} (kinematic {kinematic:0.000}) at {left.Speed:0.00} m/s, front wheel {Mathf.RadToDeg(left.Front):0.0} deg ({Mathf.RadToDeg(left.SteerAt0_25):0.0} deg after 0.25 s), rear wheel {Mathf.RadToDeg(left.Rear):0.0} deg; -1: {right.YawRate:0.000}; mirror X +1: {mirrored.YawRate:0.000}";
        GD.Print("  steering: " + numbers);
        Check(Math.Abs(straight.YawRate) < 0.02 && Math.Abs(straight.Front) < 1e-9, "steering 0: the car goes straight (yaw rate ~ 0) and the front wheels stay at 0", numbers);
        Check(Math.Abs(left.SteerAt0_25 - Mathf.DegToRad(15)) < Mathf.DegToRad(1.0), "steering +1 turns the front wheels at steerRateDegrees = 60 deg/s: 15 degrees after 0.25 s", numbers);
        Check(Math.Abs(left.Front - maxSteer) < 1e-6 && Math.Abs(right.Front + maxSteer) < 1e-6, "...and stops at maxSteerDegrees = 30 degrees on either side", numbers);
        Check(Math.Abs(left.Rear) < 1e-9, "the rear wheels (not wired to the steering) stay straight", numbers);
        Check(left.YawRate > 0.2 && right.YawRate < -0.2, "+1 turns the car towards +X (positive yaw), -1 the other way", numbers);
        Check(left.YawRate > 0.6 * kinematic && left.YawRate < 1.15 * kinematic, "...on an arc close to the geometric one: yaw rate within -40%..+15% of v*tan(30 deg)/wheelbase (tire slip makes it understeer)", numbers);
        Check(Math.Abs(left.YawRate + right.YawRate) < 0.15 * Math.Abs(left.YawRate), "...the two directions are symmetric (within 15%)", numbers);
        Check(mirrored.YawRate < -0.2 && Math.Abs(mirrored.YawRate + left.YawRate) < 0.15 * Math.Abs(left.YawRate), "mirror X (key U) on the front wheels turns the same signal the other way, with the same strength", numbers);
    }

    // ------------------------------------------------------------------ г) тормоз

    private async Task RunWheelBrakeTests(BuildEditor editor, Node3D world, Func<double, int> sec)
    {
        // Тормозной путь с заданной скоростью без мощности: время до остановки и путь.
        async Task<(double Time, double Distance, double Drift, double After, double V0, double V1)> Brake(Action<DriveCar> apply, double seconds = 3)
        {
            var car = await SpawnDriveCar(editor, world, new DriveOptions());
            await Step(editor, sec(2));
            // Машина уже едет: скорость и обороты колёс выставлены согласованно (иначе первые тики колёса «раскручиваются» тормозным трением и скорость теряется само собой).
            car.Body.LinearVelocity = car.Basis.Z * 3.0;
            foreach (var (root, wheel) in car.Wheels) wheel.Omega = (root.X > 0 ? -1 : 1) * 3.0 / wheel.TireRadius; // правые колёса повёрнуты на 180°: их «вперёд» - к -Z
            await Step(editor, sec(0.25));
            var start = car.Body.GlobalPosition;
            double v0 = car.Forward;
            apply(car);
            double stopped = -1;
            int n = sec(seconds);
            for (int i = 1; i <= n && stopped < 0; i++)
            {
                await Step(editor, 1);
                if (car.Forward < 0.05) stopped = i * (1.0 / Engine.PhysicsTicksPerSecond);
            }

            double v1 = car.Forward;
            double distance = (car.Body.GlobalPosition - start).Dot(car.Basis.Z);
            // Раскачка корпуса после резкой остановки гаснет за секунду: ползучесть меряем за следующие 2 с при зажатом тормозе.
            await Step(editor, sec(1));
            var parked = car.Body.GlobalPosition;
            await Step(editor, sec(2));
            double after = (car.Body.GlobalPosition - parked).Length();
            double drift = Math.Abs((car.Body.GlobalPosition - start).Dot(car.Basis.X));
            car.Body.QueueFree();
            await Frames(editor, 2);
            return (stopped, distance, drift, after, v0, v1);
        }

        var coast = await Brake(_ => { }, 3);
        var light = await Brake(c => c.VariableBrake = 0.3);
        var medium = await Brake(c => c.VariableBrake = 0.6);
        var variableFull = await Brake(c => c.VariableBrake = 1.0);
        var locked = await Brake(c => c.FullBrake = true);

        string Row(string name, (double Time, double Distance, double Drift, double After, double V0, double V1) r) => $"{name}: from {r.V0:0.00} m/s {(r.Time < 0 ? $"to {r.V1:0.00} m/s in 3 s, no stop" : "stops in " + r.Time.ToString("0.00") + " s")}, {r.Distance:0.00} m, creeps {r.After * 1000:0.0} mm in 2 s afterwards";
        string numbers = string.Join(" | ", new[] { Row("coast", coast), Row("variable 0.3", light), Row("variable 0.6", medium), Row("variable 1.0", variableFull), Row("full brake", locked) });
        GD.Print("  brakes: " + numbers);
        double g = ProjectSettings.GetSetting("physics/3d/default_gravity").AsDouble();
        // Накат: dv/dt = -c - k v, c = сопротивление качению (RollingResistance * g), k = линейное демпфирование тела по умолчанию в проекте; v(t) = (v0 + c/k) e^(-kt) - c/k.
        double damp = ProjectSettings.GetSetting("physics/3d/default_linear_damp").AsDouble();
        double rollingDecel = WheelPhysics.RollingResistance * g;
        double predicted = damp > 1e-9 ? (coast.V0 + rollingDecel / damp) * Math.Exp(-3 * damp) - rollingDecel / damp : coast.V0 - 3 * rollingDecel;
        Check(coast.Time < 0 && Math.Abs(coast.V1 - predicted) < 0.15, $"without a brake the car coasts: after 3 s the speed matches rolling resistance + body damping ({predicted:0.00} m/s predicted)", numbers);
        Check(light.Time > 0 && medium.Time > 0 && variableFull.Time > 0 && locked.Time > 0, "'Variable brake' and the full 'Brake' both stop the car", numbers);
        Check(light.Time > medium.Time && medium.Time > variableFull.Time * 0.95, "a stronger 'Variable brake' stops faster (0.3 slower than 0.6, 0.6 slower than 1.0)", numbers);
        Check(locked.Time < light.Time && locked.Time < medium.Time, "the full 'Brake' stops faster than a partial 'Variable brake' (0.3 and 0.6)", numbers);
        double decelLocked = (locked.V0 - 0.05) / locked.Time;
        Check(decelLocked > 0.6 * WheelPhysics.TireFriction * g * WheelPhysics.SlideFactor && decelLocked < 1.1 * WheelPhysics.TireFriction * g * WheelPhysics.SlideFactor,
            "...its deceleration is about the locked-wheel friction limit mu * slideFactor * g (speed / stop time)", $"{decelLocked:0.00} m/s2 vs {WheelPhysics.TireFriction * g * WheelPhysics.SlideFactor:0.00}; " + numbers);
        Check(light.After < 0.005 && medium.After < 0.005 && variableFull.After < 0.005 && locked.After < 0.005, "after the stop the braked car does not creep (< 5 mm in 2 s)", numbers);
        Check(Math.Max(Math.Max(light.Drift, medium.Drift), Math.Max(variableFull.Drift, locked.Drift)) < 0.05, "...and braking keeps the straight line (< 5 cm sideways)", numbers);
    }

    // ------------------------------------------------------------------ д) склон

    private async Task RunWheelSlopeTests(BuildEditor editor, Node3D world, StaticBody3D flatFloor, Func<double, int> sec)
    {
        const double slopeDegrees = 10;
        var tilt = new Basis(Vector3.Right, -Mathf.DegToRad(slopeDegrees)); // ось +Z поднимается: машина носом в горку
        double g = ProjectSettings.GetSetting("physics/3d/default_gravity").AsDouble();
        double downhill = g * Math.Sin(Mathf.DegToRad(slopeDegrees));

        flatFloor.Position = new Vector3(0, -50, 0); // плоский пол мешал бы скату назад
        var slope = MakeFloor(world, tilt);

        async Task<DriveCar> Place()
        {
            var car = await SpawnDriveCar(editor, world, new DriveOptions());
            car.Body.GlobalTransform = new Transform3D(tilt, tilt * car.Body.GlobalPosition); // вся постройка повёрнута вокруг начала поверхности
            car.Body.LinearVelocity = Vector3.Zero;
            car.Body.AngularVelocity = Vector3.Zero;
            return car;
        }

        double AlongUphill(DriveCar car, Vector3 from) => (car.Body.GlobalPosition - from).Dot(car.Basis.Z);

        // ---- с тормозом стоит
        foreach (var (name, apply) in new (string, Action<DriveCar>)[] { ("full Brake", c => c.FullBrake = true), ("Variable brake 1.0", c => c.VariableBrake = 1.0) })
        {
            var held = await Place();
            apply(held);
            await Step(editor, sec(3)); // осесть на склоне
            var from = held.Body.GlobalPosition;
            double maxSpeed = 0;
            for (int i = 0; i < sec(3); i++) { await Step(editor, 1); maxSpeed = Math.Max(maxSpeed, held.Body.LinearVelocity.Length()); }
            double moved = AlongUphill(held, from);
            string numbers = $"{name}: moved {moved * 1000:0.0} mm along the slope in 3 s, max speed {maxSpeed * 1000:0.0} mm/s";
            GD.Print("  slope hold: " + numbers);
            Check(Math.Abs(moved) < 0.01 && maxSpeed < 0.01, $"{slopeDegrees} degree slope, no power, {name}: the car stands (< 1 cm in 3 s, speed < 1 cm/s)", numbers);
            held.Body.QueueFree();
            await Frames(editor, 2);
        }

        // ---- без тормоза скатывается
        var roll = await Place();
        roll.FullBrake = true;
        await Step(editor, sec(3)); // стоит на тормозе
        roll.FullBrake = false; // отпустили
        double v0 = roll.Forward;
        var rollStart = roll.Body.GlobalPosition;
        await Step(editor, sec(1));
        double v1 = roll.Forward;
        double d1 = AlongUphill(roll, rollStart);
        await Step(editor, sec(1));
        double v2 = roll.Forward;
        double d2 = AlongUphill(roll, rollStart);
        string rolling = $"released at {v0 * 1000:0.0} mm/s; after 1 s: v {v1:0.00} m/s, {d1:0.00} m; after 2 s: v {v2:0.00} m/s, {d2:0.00} m (g*sin(slope) = {downhill:0.00} m/s2)";
        GD.Print("  slope roll: " + rolling);
        Check(Math.Abs(v0) < 0.01 && d2 < -1.0 && v2 < v1 && v1 < -0.5, "the same slope with no brake: released from rest, the car rolls back downhill and speeds up", rolling);
        Check(-(v1 - v0) > 0.6 * downhill && -(v1 - v0) < 1.05 * downhill, "...with an acceleration of about g*sin(slope) minus the rolling resistance (speed gained in 1 s within 60..105% of g*sin(slope) * 1 s)", rolling);
        roll.Body.QueueFree();
        await Frames(editor, 2);

        // ---- с мощностью заезжает
        var climb = await Place();
        await Step(editor, sec(1));
        climb.Throttle = 1.0;
        var climbStart = climb.Body.GlobalPosition;
        await Step(editor, sec(3));
        double climbSpeed = climb.Forward;
        double climbed = AlongUphill(climb, climbStart);
        string climbing = $"speed after 3 s {climbSpeed:0.00} m/s, climbed {climbed:0.00} m along the slope";
        GD.Print("  slope climb: " + climbing);
        Check(climbSpeed > 3.5 && climbSpeed < 4.9 && climbed > 5, "full throttle drives the car UP the slope (rear-wheel drive, near the wheel speed 4.7 m/s)", climbing);
        climb.Body.QueueFree();

        slope.QueueFree();
        flatFloor.Position = Vector3.Zero;
        await Frames(editor, 2);
    }

    // ------------------------------------------------------------------ е) ступенька

    /// <summary>
    /// Ступенька - прямоугольный брус поперёк пути, высота = доля настоящего радиуса шины (0.375 м у колеса по умолчанию, берётся из состояния колеса). Машина заднеприводная (газ 0.6, 2.8 м/с),
    /// шасси поднято на два слоя, чтобы не цеплять брус. Что МОДЕЛЬ умеет, а не что «хочется»: колесо тянет трение (μ = 1.1), поэтому до ≈0.5 R машина въезжает (с разгона), а 0.6 R и выше
    /// - нет; ожидание «ниже радиуса - преодолевается» для заднеприводной машины с такой шиной не выполняется и не может: см. WORKLOG/05. Проверяются только числа с запасом по обе стороны порога,
    /// монотонность («выше ступенька - не легче») и отсутствие «взлётов»; значения у самого порога только печатаются.
    /// </summary>
    private async Task RunWheelStepTests(BuildEditor editor, Node3D world, Func<double, int> sec)
    {
        const double faceZ = 6.0, depth = 2.0;
        var probe = await SpawnDriveCar(editor, world, new DriveOptions { PlateLevel = 2 });
        double radius = probe.Wheels[0].State.TireRadius;
        probe.Body.QueueFree();
        await Frames(editor, 2);

        var fractions = new[] { 0.3, 0.5, 0.6, 0.7, 0.8, 0.9, 1.4 };
        var crossedBy = new Dictionary<double, bool>();
        foreach (double fraction in fractions)
        {
            double height = fraction * radius;
            string name = $"{fraction:0.0} R";
            var car = await SpawnDriveCar(editor, world, new DriveOptions { PlateLevel = 2 });
            var obstacle = new StaticBody3D { Name = "WheelStep" };
            obstacle.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(6, (float)height, (float)depth) }, Position = new Vector3(1, (float)(height / 2), (float)(faceZ + depth / 2)) });
            world.AddChild(obstacle);
            await Step(editor, sec(2));

            car.Throttle = 0.6;
            double maxFrontZ = 0, maxHubY = 0, maxTiltDeg = 0;
            int total = sec(8);
            for (int i = 0; i < total; i++)
            {
                await Step(editor, 1);
                maxFrontZ = Math.Max(maxFrontZ, car.FrontHub.Z);
                maxHubY = Math.Max(maxHubY, Math.Max(car.FrontHub.Y, car.RearHub.Y));
                maxTiltDeg = Math.Max(maxTiltDeg, Mathf.RadToDeg(car.Basis.Y.AngleTo(Vector3.Up)));
            }

            double rearZ = car.RearHub.Z;
            double speed = car.Forward;
            bool crossed = rearZ > faceZ + depth + 0.2 && speed > 1.0;
            crossedBy[fraction] = crossed;
            string numbers = $"step {name} = {height * 1000:0} mm (R = {radius * 1000:0} mm): {(crossed ? "CLIMBED" : "stopped")}; front hub reached z {maxFrontZ:0.00} (face at {faceZ}), rear hub z {rearZ:0.00}, speed {speed:0.00} m/s, hub height max {maxHubY * 1000:0} mm, tilt max {maxTiltDeg:0.0} deg";
            GD.Print("  step: " + numbers);

            if (fraction <= 0.3)
            {
                Check(crossed, $"a step of {name} ({height * 1000:0} mm) is climbed: the whole car ends up on the far side, still moving", numbers);
            }

            if (fraction >= 0.9)
            {
                Check(!crossed && maxFrontZ < faceZ + 0.1 && Math.Abs(speed) < 0.3 && maxHubY < radius + 0.1, $"a step of {name} ({height * 1000:0} mm, about the tire radius or higher) stops the car: the front wheels stay before it and do not climb it", numbers);
            }

            if (crossed)
            {
                Check(maxTiltDeg < 25 && maxHubY < 2.5 * radius, $"...step {name} is climbed without a launch (tilt below 25 degrees, the hub never above 2.5 radii)", numbers);
            }

            obstacle.QueueFree();
            car.Body.QueueFree();
            await Frames(editor, 2);
        }

        bool monotone = true;
        for (int i = 1; i < fractions.Length; i++) monotone &= !(crossedBy[fractions[i]] && !crossedBy[fractions[i - 1]]);
        Check(monotone, "a higher step is never easier: if some height is climbed, every lower one is too (no erratic windows)", string.Join(", ", fractions.Select(f => $"{f:0.0}R:{(crossedBy[f] ? "climbed" : "stopped")}")));
    }
}
