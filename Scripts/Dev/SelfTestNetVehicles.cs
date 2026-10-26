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
    /// <summary>
    /// Порядок блоков в списке у построек, загруженных разными путями, может отличаться (не часть постройки) — для сравнения берётся содержимое: блоки с поворотом, размером и
    /// параметрами и провода между ними.
    /// </summary>
    internal static string CanonicalOf(Construction c) => CanonicalConstruction(c);

    private static string CanonicalConstruction(Construction c)
    {
        string Key(int instanceId) { var i = c.GetInstance(instanceId)!; return $"{i.BlockSlug}@{i.Origin}"; }
        var blocks = c.Instances.Select(i => $"{Key(i.InstanceId)} rot={i.RotationSteps} size={i.Size} params=[{string.Join(",", (i.Parameters ?? new()).OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value))}]");
        var wires = c.Wires.Select(w => $"{Key(w.FromInstance)}.{w.FromNode}->{Key(w.ToInstance)}.{w.ToNode}");
        return string.Join("\n", blocks.OrderBy(b => b, StringComparer.Ordinal)) + "\n--\n" + string.Join("\n", wires.OrderBy(w => w, StringComparer.Ordinal));
    }

    /// <summary>Постройка для тестов репликации из НАСТОЯЩИХ блоков: палуба, сиденье (W/S → сигнал мотора), аккумулятор → мотор и кнопка, у мотора свой параметр.</summary>
    internal static string BuildReplicatedVehicleJson()
    {
        var source = new Construction(new VoxelGrid());
        source.PlaceBlock(new Vector3I(0, 0, 0), new Vector3I(20, 2, 20), BlockCatalog.Instance.Get("block"), Colors.White);
        var seat = PlaceFixture(source, "pilot_seat", new Vector3I(8, 2, 8));
        var button = PlaceFixture(source, "button", new Vector3I(2, 2, 2));
        var battery = PlaceFixture(source, "battery_small", new Vector3I(12, 2, 2));
        var motor = PlaceFixture(source, "electric_motor_small", new Vector3I(14, 2, 2));
        source.TryConnect(battery.InstanceId, "electricity_out", motor.InstanceId, "electricity", out _);
        source.TryConnect(battery.InstanceId, "electricity_out", button.InstanceId, "electricity", out _);
        source.TryConnect(seat.InstanceId, "ws_out", motor.InstanceId, "Signal", out _);
        source.TrySetParameter(motor, "maxRpm", "1800");
        return ConstructionIO.Serialize(source);
    }

    /// <summary>Сервер владеет телами и рантаймом, клиент держит копии-наблюдатели: создание, позы, снимки рантайма, правила сиденья/кнопок, возврат тела, посадка хоста через мир.</summary>
    private async Task RunNetworkedVehicleTests(BuildEditor host, GameWorld world)
    {
        GD.Print("-- networking: vehicles and their runtime are replicated (server owns, clients hold puppets)");

        var hub = NetHub.Instance;
        string json = BuildReplicatedVehicleJson();
        int spawnedEvents = 0, removedEvents = 0;
        void OnSpawned(VehicleBody body) => spawnedEvents++;
        void OnRemoved(int netId) => removedEvents++;
        hub.VehicleSpawned += OnSpawned;
        hub.VehicleRemoved += OnRemoved;

        // ---- сервер создаёт тело на общем корне (не под сценой мира)
        var vehicle = hub.ServerSpawnVehicle(json, new Vector3(10, 0.1f, 10), "WorkbenchLarge");
        Check(vehicle != null && hub.VehiclesRoot != null && vehicle.GetParent() == hub.VehiclesRoot, "the server spawns the body under the shared NetVehicles root, not under the world scene");
        if (vehicle == null)
        {
            hub.VehicleSpawned -= OnSpawned;
            hub.VehicleRemoved -= OnRemoved;
            return;
        }

        Check(vehicle.NetId > 0 && !vehicle.IsPuppet && vehicle.Runtime is { IsMirror: false } && hub.GetVehicle(vehicle.NetId) == vehicle && spawnedEvents == 1 && vehicle.SourceWorkbenchName == "WorkbenchLarge",
            "...it is a real simulated body (not a puppet) with a numbered id, a working runtime and a spawn event");
        Check(hub.Vehicles.Count == 1, "...registered once");

        await PhysicsFrames(host, 90);
        Check(vehicle.GlobalPosition.Y > -0.5 && vehicle.LinearVelocity.Length() < 1, "(setup) the body rests on the server's own ground (it does not fall through while the world scene is gone)", $"{vehicle.GlobalPosition} v={vehicle.LinearVelocity}");

        // ---- копия-наблюдатель строится из того же JSON: те же блоки, провода, параметры
        var holder = new Node3D { Name = "PuppetHolder" };
        host.AddChild(holder);
        var puppet = VehicleReplication.CreatePuppet(holder, 99, json, "WorkbenchLarge", vehicle.GlobalTransform);
        Check(puppet.IsPuppet && puppet.Freeze && puppet.Runtime is { IsMirror: true } && puppet.NetId == 99, "a client copy is a frozen puppet with a mirror runtime");
        Check(CanonicalConstruction(puppet.Construction!) == CanonicalConstruction(vehicle.Construction!), "...built from the same JSON: the same blocks, wires and parameters");
        int seatId = vehicle.Construction!.Instances.First(i => i.BlockSlug == "pilot_seat").InstanceId;
        int buttonId = vehicle.Construction.Instances.First(i => i.BlockSlug == "button").InstanceId;
        int motorId = vehicle.Construction.Instances.First(i => i.BlockSlug == "electric_motor_small").InstanceId;
        int batteryId = vehicle.Construction.Instances.First(i => i.BlockSlug == "battery_small").InstanceId;
        Check(puppet.Construction!.Instances.First(i => i.BlockSlug == "pilot_seat").InstanceId == seatId && puppet.Construction.Instances.First(i => i.BlockSlug == "electric_motor_small").InstanceId == motorId,
            "...and the instance ids match the server's (so runtime snapshots address the right blocks)");

        // ---- позы
        var poses = VehicleReplication.CapturePoses(hub.Vehicles);
        Check(poses.Length == VehicleReplication.PoseStride && (int)poses[0] == vehicle.NetId, "a pose packet carries id + position + rotation + velocity per body");
        var shifted = (double[])poses.Clone();
        shifted[0] = 99; // адресовано копии
        shifted[1] += 2;
        shifted[8] = 1; // скорость +X
        Check(VehicleReplication.ApplyPoses(shifted, id => id == 99 ? puppet : null) == 1 && VehicleReplication.ApplyPoses(poses, id => null) == 0, "a pose packet updates the copy it addresses and ignores unknown ids");
        double startX = puppet.GlobalPosition.X;
        await PhysicsFrames(host, 20);
        Check(puppet.GlobalPosition.X > startX + 1.5 && puppet.GlobalPosition.X < startX + 2.6, "the puppet catches up to the pose the server sent (smoothly, with the velocity extrapolated)", $"{puppet.GlobalPosition.X - startX}");
        var rotatedPose = (double[])poses.Clone();
        rotatedPose[0] = 99;
        var turn = new Quaternion(Vector3.Up, 1.0f);
        rotatedPose[1] = puppet.GlobalPosition.X; rotatedPose[2] = puppet.GlobalPosition.Y; rotatedPose[3] = puppet.GlobalPosition.Z;
        rotatedPose[4] = turn.X; rotatedPose[5] = turn.Y; rotatedPose[6] = turn.Z; rotatedPose[7] = turn.W;
        rotatedPose[8] = rotatedPose[9] = rotatedPose[10] = 0;
        VehicleReplication.ApplyPoses(rotatedPose, id => id == 99 ? puppet : null);
        await PhysicsFrames(host, 20);
        Check(puppet.GlobalTransform.Basis.GetRotationQuaternion().AngleTo(turn) < 0.05, "...and it turns to the sent orientation");

        // ---- правила сиденья на сервере
        const long peerA = 77, peerB = 78;
        Check(hub.ServerSit(peerA, vehicle.NetId, seatId) && vehicle.Runtime!.GetState<PilotSeatState>(seatId)!.Occupied && hub.SeatedPeer(vehicle.NetId, seatId) == peerA, "the server seats a player: the seat is occupied by that peer");
        Check(!hub.ServerSit(peerB, vehicle.NetId, seatId), "a second player cannot take an occupied seat");
        Check(!hub.ServerSit(peerA, vehicle.NetId, buttonId) && !hub.ServerSit(peerB, vehicle.NetId, buttonId) && !hub.ServerSit(peerB, 12345, seatId), "...nor sit on something that is not a seat, or on an unknown body");
        var driving = new SeatInput(new bool[6], false, false, new[] { 0.0, 1.0, 0.0, 0.0 });
        Check(!hub.ServerSeatInput(peerB, vehicle.NetId, seatId, driving) && vehicle.Runtime!.GetState<PilotSeatState>(seatId)!.AxisInput[1] == 0, "pilot input from someone who is not sitting there is ignored");
        Check(hub.ServerSeatInput(peerA, vehicle.NetId, seatId, driving), "input from the seated player is accepted");
        await PhysicsFrames(host, 90);

        var seatState = vehicle.Runtime!.GetState<PilotSeatState>(seatId)!;
        var motorState = vehicle.Runtime.GetState<ElectricMotorState>(motorId)!;
        var batteryState = vehicle.Runtime.GetState<BatteryState>(batteryId)!;
        Check(seatState.AxisValue[1] > 0.9 && motorState.Rpm > 100 && batteryState.Charge < batteryState.Capacity, "the SERVER's runtime runs the machine: W on the seat drives the motor and spends the battery", $"axis={seatState.AxisValue[1]} rpm={motorState.Rpm} charge={batteryState.Charge}");

        // ---- кнопка
        Check(hub.ServerButton(peerA, vehicle.NetId, buttonId, true) && vehicle.Runtime.GetState<ButtonState>(buttonId)!.Pressed, "a button press from a peer reaches the server's button");
        Check(!hub.ServerButton(peerB, vehicle.NetId, buttonId, false) && vehicle.Runtime.GetState<ButtonState>(buttonId)!.Pressed, "another peer cannot release a button it does not hold");

        // ---- снимок рантайма: наблюдатель показывает то же состояние и сам не считает
        var snapshot = vehicle.Runtime.CaptureNet();
        Check(snapshot.Ids.Length >= 4 && snapshot.Lengths.Sum() == snapshot.Values.Length, "a runtime snapshot lists every stateful block with its numbers");
        puppet.Runtime!.ApplyNet(snapshot.Ids, snapshot.Lengths, snapshot.Values);
        var puppetSeat = puppet.Runtime.GetState<PilotSeatState>(seatId)!;
        Check(puppetSeat.Occupied && Math.Abs(puppetSeat.AxisValue[1] - seatState.AxisValue[1]) < 1e-9
              && Math.Abs(puppet.Runtime.GetState<ElectricMotorState>(motorId)!.Rpm - motorState.Rpm) < 1e-9
              && Math.Abs(puppet.Runtime.GetState<BatteryState>(batteryId)!.Charge - batteryState.Charge) < 1e-9
              && puppet.Runtime.GetState<ButtonState>(buttonId)!.Active,
            "the copy shows the server's state after a snapshot: the seat is occupied, the axis, motor RPM, battery charge and the pressed button match");
        puppet.Runtime.Tick(5.0);
        Check(Math.Abs(puppetSeat.AxisValue[1] - seatState.AxisValue[1]) < 1e-9, "a mirror runtime never simulates on its own (Tick does nothing)");

        // ---- встали / ушли: сиденье свободно, кнопка отпущена, ввод сброшен
        Check(!hub.ServerStand(peerB, vehicle.NetId, seatId) && seatState.Occupied, "only the player sitting there can free the seat");
        hub.ServerReleasePeer(peerA); // игрок отключился
        Check(!seatState.Occupied && hub.SeatedPeer(vehicle.NetId, seatId) == 0 && !vehicle.Runtime.GetState<ButtonState>(buttonId)!.Pressed && seatState.AxisInput[1] == 0,
            "when a player disconnects, his seat is freed, his button is released and his input is cleared (nothing sticks)");
        Check(hub.ServerSit(peerB, vehicle.NetId, seatId) && hub.ServerStand(peerB, vehicle.NetId, seatId) && !seatState.Occupied, "the freed seat can be taken again, and a player can stand up on his own");

        // ---- хост садится/встаёт через мир: сиденье занимает сервер, ввод идёт той же дорогой
        var localPeer = hub.LocalPeerId;
        world.PlayerForTesting.SetPose(vehicle.GlobalPosition + new Vector3(0, 6, 0), 0);
        Check(world.TrySit(vehicle, seatId) && world.IsSeated && hub.SeatedPeer(vehicle.NetId, seatId) == localPeer && seatState.Occupied,
            "the host sits down through the world: the server registers him on the seat");
        Check(!world.TrySit(vehicle, seatId), "...and cannot sit twice");
        await Frames(host, 3);
        world.Stand();
        Check(!world.IsSeated && hub.SeatedPeer(vehicle.NetId, seatId) == 0 && !seatState.Occupied, "standing up frees the seat on the server");

        // ---- возврат тела к верстаку: исчезает у всех; повторно нечего возвращать
        int netId = vehicle.NetId;
        Check(hub.ServerSit(peerA, netId, seatId) && hub.ServerRecallVehicle(netId), "the server removes a body on request (even with someone sitting in it)");
        await Frames(host, 2);
        Check(hub.GetVehicle(netId) == null && hub.Vehicles.Count == 0 && removedEvents == 1 && hub.SeatedPeer(netId, seatId) == 0, "...it is gone from the registry, its seat bookkeeping is cleared, and a removal event fired");
        Check(!hub.ServerRecallVehicle(netId), "recalling it again does nothing");

        // ---- клиентский путь регистрации (то, что делает RPC у клиента): копия под общим корнем, затем позы по номеру
        var registered = hub.ClientCreatePuppet(5, json, "WorkbenchLarge", VehicleReplication.CapturePoses(new[] { puppet }));
        Check(registered != null && registered.IsPuppet && hub.GetVehicle(5) == registered && registered.GetParent() == hub.VehiclesRoot, "a client registers a copy under the shared root by the server's number");
        Check(hub.ClientCreatePuppet(5, json, "WorkbenchLarge", VehicleReplication.CapturePoses(new[] { puppet })) == registered && hub.Vehicles.Count == 1, "a repeated spawn message for the same body does not create a second copy");
        hub.SetVehiclesVisible(false);
        Check(hub.VehiclesRoot is { Visible: false }, "while this peer is in the editor the bodies are hidden (they keep existing)");
        hub.SetVehiclesVisible(true);
        Check(hub.ServerRecallVehicle(5), "(cleanup) the copy is removed");

        hub.VehicleSpawned -= OnSpawned;
        hub.VehicleRemoved -= OnRemoved;
        puppet.QueueFree();
        holder.QueueFree();
        await Frames(host, 2);
    }
}
