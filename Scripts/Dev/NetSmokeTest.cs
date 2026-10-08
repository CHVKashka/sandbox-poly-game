using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.Dev;

/// <summary>
/// Дымовой тест репликации ПО НАСТОЯЩЕЙ СЕТИ — два процесса на одной машине (самотест гоняет один процесс и RPC не проверяет): <c>godot --headless --path . -- --nettest=host</c> и
/// <c>godot --headless --path . -- --nettest=client</c> (порт — <c>--port=N</c>, по умолчанию 37998). Хост поднимает сервер, ждёт клиента, создаёт тело; клиент должен получить копию, сесть
/// на сиденье, газовать, нажать кнопку и встать — всё через настоящие RPC; хост проверяет, что это дошло до его рантайма, затем убирает тело, и клиент проверяет, что оно исчезло.
/// Код выхода 0 — всё прошло. Запускается вручную (см. Docs/04-testing.md), в общий прогон не входит — нужны два процесса.
/// </summary>
public static class NetSmokeTest
{
    private static int _failures;

    private static void Expect(string role, bool ok, string name, string detail = "")
    {
        if (!ok) _failures++;
        GD.Print($"[nettest {role}] {(ok ? "PASS" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : "  -> " + detail)}");
    }

    private static async Task<bool> WaitFor(Node node, Func<bool> condition, double seconds)
    {
        ulong deadline = Time.GetTicksMsec() + (ulong)(seconds * 1000);
        while (Time.GetTicksMsec() < deadline)
        {
            if (condition()) return true;
            await SelfTest.Frames(node, 1);
        }

        return condition();
    }

    public static async Task RunAsync(BuildEditor editor, string role, int port)
    {
        _failures = 0;
        if (role.StartsWith("host")) await RunHost(editor, port, early: role == "host-early");
        else await RunClient(editor, port);

        GD.Print($"[nettest {role}] finished with {_failures} failure(s)");
        editor.GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private static async Task RunHost(BuildEditor editor, int port, bool early)
    {
        var hub = NetHub.Instance;
        Expect("host", hub.Host(port) == Error.Ok, "the server starts");
        var vehicle = early ? hub.ServerSpawnVehicle(SelfTest.BuildReplicatedVehicleJson(), new Vector3(10, 0.1f, 10), "WorkbenchLarge") : null; // early: тело ДО подключения клиента (путь «подключился позже»)
        bool clientCame = await WaitFor(editor, () => hub.Multiplayer.GetPeers().Length > 0, 40);
        Expect("host", clientCame, "a client connects");
        if (!clientCame) return;

        vehicle ??= hub.ServerSpawnVehicle(SelfTest.BuildReplicatedVehicleJson(), new Vector3(10, 0.1f, 10), "WorkbenchLarge");
        Expect("host", vehicle != null, "the server spawns a vehicle for everyone");
        if (vehicle == null) return;

        int seatId = vehicle.Construction!.Instances.First(i => i.BlockSlug == "pilot_seat").InstanceId;
        int buttonId = vehicle.Construction.Instances.First(i => i.BlockSlug == "button").InstanceId;
        int motorId = vehicle.Construction.Instances.First(i => i.BlockSlug == "electric_motor_small").InstanceId;
        var seat = vehicle.Runtime!.GetState<PilotSeatState>(seatId)!;
        var motor = vehicle.Runtime.GetState<ElectricMotorState>(motorId)!;
        var button = vehicle.Runtime.GetState<ButtonState>(buttonId)!;

        Expect("host", await WaitFor(editor, () => seat.Occupied, 30), "the client's sit request reaches the server and the seat becomes occupied");
        Expect("host", await WaitFor(editor, () => motor.Rpm > 100, 30), "the client's W input drives the server's motor", $"rpm={motor.Rpm}");
        Expect("host", await WaitFor(editor, () => button.Pressed, 30), "the client's button press reaches the server's button");
        Expect("host", await WaitFor(editor, () => !button.Pressed, 30), "...and its release too");
        Expect("host", await WaitFor(editor, () => !seat.Occupied, 30), "the client's stand-up frees the seat on the server");

        await WaitFor(editor, () => false, 1); // дать клиенту получить последний снимок рантайма до удаления тела
        Expect("host", hub.ServerRecallVehicle(vehicle.NetId), "the server recalls the vehicle");
        await WaitFor(editor, () => false, 2); // дать клиенту увидеть удаление и завершиться
    }

    private static async Task RunClient(BuildEditor editor, int port)
    {
        var hub = NetHub.Instance;
        Expect("client", hub.Join("127.0.0.1", port) == Error.Ok, "the client starts connecting");
        bool connected = await WaitFor(editor, () => hub.Multiplayer.MultiplayerPeer?.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected, 30);
        Expect("client", connected, "the client connects to the server");
        if (!connected) return;

        bool got = await WaitFor(editor, () => hub.Vehicles.Count == 1, 40);
        Expect("client", got, "a copy of the server's vehicle appears (spawn message arrived)");
        if (!got) return;

        var puppet = hub.Vehicles.First();
        Expect("client", puppet.IsPuppet && puppet.Runtime is { IsMirror: true } && puppet.NetId > 0, "...it is a puppet with a mirror runtime");
        var reference = new Construction(new VoxelGrid());
        ConstructionIO.Deserialize(reference, SelfTest.BuildReplicatedVehicleJson(), BlockCatalog.Instance);
        Expect("client", SelfTest.CanonicalOf(puppet.Construction!) == SelfTest.CanonicalOf(reference), "...with the same blocks, wires and parameters as the server's");

        int seatId = puppet.Construction!.Instances.First(i => i.BlockSlug == "pilot_seat").InstanceId;
        int buttonId = puppet.Construction.Instances.First(i => i.BlockSlug == "button").InstanceId;
        int motorId = puppet.Construction.Instances.First(i => i.BlockSlug == "electric_motor_small").InstanceId;
        var seat = puppet.Runtime!.GetState<PilotSeatState>(seatId)!;
        var motor = puppet.Runtime.GetState<ElectricMotorState>(motorId)!;
        var button = puppet.Runtime.GetState<ButtonState>(buttonId)!;

        Expect("client", await WaitFor(editor, () => puppet.GlobalPosition.Y > -0.5 && puppet.GlobalPosition.DistanceTo(new Vector3(7.5f, 0, 7.5f)) < 1, 10), "pose packets keep the copy where the server's body is", $"{puppet.GlobalPosition}");

        bool sitAnswered = false, sitOk = false;
        void OnSit(int netId, int instance, bool ok) { sitAnswered = true; sitOk = ok; }
        hub.SitResultForMe += OnSit;
        hub.RequestSit(puppet.NetId, seatId);
        Expect("client", await WaitFor(editor, () => sitAnswered, 20) && sitOk, "the server answers the sit request positively");
        hub.SitResultForMe -= OnSit;

        Expect("client", await WaitFor(editor, () => seat.Occupied, 20), "the snapshot tells the client its seat is occupied");

        // Газ: W держим, пока копия не покажет обороты (снимок рантайма пришёл).
        var forward = new SeatInput(new bool[6], false, false, new[] { 0.0, 1.0, 0.0, 0.0 });
        ulong until = Time.GetTicksMsec() + 30000;
        while (motor.Rpm <= 100 && Time.GetTicksMsec() < until)
        {
            hub.SendSeatInput(puppet.NetId, seatId, forward);
            await SelfTest.Frames(editor, 3);
        }

        Expect("client", motor.Rpm > 100, "the server's motor RPM arrives in the client's runtime snapshots", $"rpm={motor.Rpm}");

        hub.RequestButton(puppet.NetId, buttonId, true);
        Expect("client", await WaitFor(editor, () => button.Active, 20), "the pressed button is shown on the client (from the server's state)");
        hub.RequestButton(puppet.NetId, buttonId, false);
        Expect("client", await WaitFor(editor, () => !button.Active, 20), "...and released");

        hub.SendSeatInput(puppet.NetId, seatId, SeatInput.None);
        hub.RequestStand(puppet.NetId, seatId);
        Expect("client", await WaitFor(editor, () => !seat.Occupied, 20), "after standing up the seat is free on the client too");

        Expect("client", await WaitFor(editor, () => hub.Vehicles.Count == 0, 30), "the server's recall removes the copy");
    }
}
