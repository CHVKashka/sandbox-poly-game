using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Runtime;
using SandboxPolyGame.World;

namespace SandboxPolyGame.Dev;

public sealed partial class SelfTest
{
    // Колесо-фикстура для физики: диск 3x3 шириной в клетку, ступица в корне, без коллизии (как у настоящего: у колеса коллизии нет, только у оси).
    private const string PhysicsWheelJson =
        "{ \"footprint\": [1,3,3], \"footprintMin\": [0,-1,-1], \"footprintManual\": true, \"behavior\": \"Wheel\", " +
        "\"ports\": [ { \"id\": \"RPS\", \"resource\": \"Torque\", \"direction\": \"In\", \"face\": \"PosX\", \"position\": [0,0] } ], " +
        "\"nodes\": [ { \"id\": \"Brake\", \"type\": \"Boolean\", \"direction\": \"In\", \"position\": [0,0,0] }, " +
        "{ \"id\": \"Variable brake\", \"type\": \"Number\", \"direction\": \"In\", \"position\": [0,0,1] }, " +
        "{ \"id\": \"Steering\", \"type\": \"Number\", \"direction\": \"In\", \"position\": [0,0,-1] } ] }";

    /// <summary>Плита 8x1x12 клеток на четырёх колёсах (по два слева и справа), без привода.</summary>
    private static string BuildWheeledPlateJson()
    {
        var construction = new Construction(new VoxelGrid());
        construction.PlaceBlock(new Vector3I(0, 0, 0), new Vector3I(8, 1, 12), BlockCatalog.Instance.Get("block"), Colors.White);
        PlaceFixture(construction, "zz_phys_wheel", new Vector3I(-1, 0, 1));
        PlaceFixture(construction, "zz_phys_wheel", new Vector3I(-1, 0, 10));
        PlaceFixture(construction, "zz_phys_wheel", new Vector3I(8, 0, 1));
        PlaceFixture(construction, "zz_phys_wheel", new Vector3I(8, 0, 10));
        return ConstructionIO.Serialize(construction);
    }

    /// <summary>
    /// Физика колёс на настоящем физическом теле: машина стоит на колёсах без проваливания и БЕЗ дрожания. Баг (найден пользователем на тестовой машине): стоящая машина
    /// подпрыгивала на доли сантиметра и от этого сдвигалась. Причина - точка приложения силы бралась из попадания «самого глубокого» луча веера; на ровной земле глубину
    /// даёт несколько лучей с одинаковой (до шума) глубиной, и выбор прыгал между ними через тик, а с ним - точка приложения силы на ±12 см вдоль колеса, то есть момент тангажа.
    /// Теперь точка - проекция центра колеса на плоскость контакта (не зависит от того, какой из равных лучей выбран).
    /// </summary>
    private async Task RunWheelRestTests(BuildEditor editor)
    {
        GD.Print("-- wheel physics: a car stands on its wheels without sinking and without jittering");

        using var scope = UseCatalogWith(("zz_phys_wheel", PhysicsWheelJson));
        var floor = new StaticBody3D { Name = "WheelRestFloor", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(80, 1, 80) } });
        editor.AddChild(floor);
        var parent = new Node3D();
        editor.AddChild(parent);

        var vehicle = VehicleSpawner.Spawn(parent, BuildWheeledPlateJson(), new Vector3(0, 0.02f, 0));
        await PhysicsFrames(editor, 2);
        Check(vehicle.Wheels is { WheelCount: 4 }, "(setup) the vehicle has four wheels in the wheel physics", $"{vehicle.Wheels?.WheelCount}");

        await PhysicsFrames(editor, 240); // осесть

        // Наблюдение за стоящей машиной.
        double minY = double.MaxValue, maxY = double.MinValue, minTilt = double.MaxValue, maxTilt = double.MinValue, maxSpeed = 0, maxSpin = 0;
        var startPosition = vehicle.GlobalPosition;
        int groundedFrames = 0, frames = 180;
        double minCompression = double.MaxValue, maxCompression = 0;
        for (int i = 0; i < frames; i++)
        {
            await PhysicsFrames(editor, 1);
            var position = vehicle.GlobalPosition;
            double tilt = vehicle.GlobalTransform.Basis.Y.AngleTo(Vector3.Up);
            minY = Math.Min(minY, position.Y); maxY = Math.Max(maxY, position.Y);
            minTilt = Math.Min(minTilt, tilt); maxTilt = Math.Max(maxTilt, tilt);
            maxSpeed = Math.Max(maxSpeed, vehicle.LinearVelocity.Length());
            maxSpin = Math.Max(maxSpin, vehicle.AngularVelocity.Length());
            if (vehicle.Wheels!.Contacts.Count == 4 && vehicle.Wheels.Contacts.All(c => c.Grounded)) groundedFrames++;
            foreach (var contact in vehicle.Wheels.Contacts)
            {
                minCompression = Math.Min(minCompression, contact.Compression);
                maxCompression = Math.Max(maxCompression, contact.Compression);
            }
        }

        var drift = vehicle.GlobalPosition - startPosition;
        string numbers = $"y range {(maxY - minY) * 1000:0.000} mm, tilt range {Mathf.RadToDeg(maxTilt - minTilt):0.0000} deg, drift {drift.Length() * 1000:0.000} mm, max speed {maxSpeed:0.0000} m/s, max spin {maxSpin:0.0000} rad/s, compression {minCompression * 1000:0.00}..{maxCompression * 1000:0.00} mm";
        GD.Print("  wheel rest: " + numbers);

        Check(groundedFrames == frames, "all four wheels touch the ground on every tick of the observation", $"{groundedFrames}/{frames}");
        Check(minY > 0.2 && minY < 0.5, "the car stands on its wheels (the chassis rides about a wheel radius above the floor, not sunk and not flying)", $"y={minY}");
        Check(minCompression > 0.01 && maxCompression < 0.05, "the tires are pressed about the 3 cm contact travel (the spring carries the weight)", $"{minCompression * 1000:0.0}..{maxCompression * 1000:0.0} mm");
        Check(maxY - minY < 0.0005, "a standing car does not bounce: the height changes by less than 0.5 mm", numbers);
        Check(Mathf.RadToDeg(maxTilt - minTilt) < 0.02, "...and does not rock: the tilt changes by less than 0.02 degrees", numbers);
        Check(drift.Length() < 0.005, "...and does not wander: less than 5 mm in three seconds", numbers);
        Check(maxSpeed < 0.01 && maxSpin < 0.02, "...it stays at rest (speed below 1 cm/s)", numbers);

        parent.QueueFree();
        floor.QueueFree();
        await Frames(editor, 2);
    }
}
