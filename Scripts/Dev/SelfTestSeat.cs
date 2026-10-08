using System;
using System.Collections.Generic;
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
    // ================================================================== мир: посадка на сиденье, нажатие кнопки, ввод пилота

    private async Task RunSeatInteractionTests(BuildEditor editor)
    {
        GD.Print("-- open world: sit on the pilot seat (E/Shift), the seat reads the pilot input, a button is pressed by holding E");

        // Чистая функция чтения ввода пилота.
        var held = new HashSet<Key> { Key.D, Key.W, Key.Key3, Key.Key6, Key.Left, Key.Down };
        var mouse = new HashSet<MouseButton> { MouseButton.Right };
        var input = GameWorld.ReadSeatInput(held.Contains, mouse.Contains);
        Check(input.Hotkeys.SequenceEqual(new[] { false, false, true, false, false, true }) && !input.Trigger1 && input.Trigger2,
            "pilot input: keys 1-6 are the hotkeys, LMB is trigger 1, RMB is trigger 2");
        Check(input.Axes.SequenceEqual(new[] { 1.0, 1.0, -1.0, -1.0 }), "pilot input axes: D=+1 (A/D), W=+1 (W/S), Left=-1 (left/right), Down=-1 (up/down)", string.Join(",", input.Axes));
        var opposed = GameWorld.ReadSeatInput(new HashSet<Key> { Key.A, Key.D }.Contains, _ => false);
        Check(opposed.Axes[0] == 0, "A and D together cancel out");

        using var scope = UseLogicFixtures();

        var world = new GameWorld { Name = "SelfTestSeatWorld" };
        editor.AddChild(world);
        await Frames(editor, 2);

        var source = new Construction(new VoxelGrid());
        PlaceFixture(source, "zz_seat", new Vector3I(0, 0, 0));
        PlaceFixture(source, "zz_button", new Vector3I(10, 0, 0));
        var vehicle = VehicleSpawner.Spawn(world, ConstructionIO.Serialize(source), new Vector3(0, 5, 0));
        vehicle.Freeze = true; // не падать на землю - тест целится лучом
        await Frames(editor, 4);

        var visual = vehicle.GetChildren().OfType<VoxelWorld>().First();
        var spawnedSeat = visual.Construction.Instances.First(i => i.BlockSlug == "zz_seat");
        var spawnedButton = visual.Construction.Instances.First(i => i.BlockSlug == "zz_button");
        var seatState = vehicle.Runtime!.GetState<PilotSeatState>(spawnedSeat.InstanceId)!;

        // Какой блок под лучом.
        var seatTop = vehicle.ToGlobal(new Vector3(0.5f, 0.75f, 0.1f));
        Check(vehicle.TryGetInstanceAt(seatTop, Vector3.Up)?.InstanceId == spawnedSeat.InstanceId, "a ray hit on the seat top face resolves to the seat block");
        Check(vehicle.TryGetInstanceAt(vehicle.ToGlobal(new Vector3(5f, 0.1f, 0.1f)), Vector3.Up) == null, "a hit over empty space resolves to no block");

        var pose = vehicle.GetSeatPose(spawnedSeat.InstanceId);
        Check(pose.HasValue, "the seat has an eye pose");
        var (eye, forward) = pose!.Value;
        Check(Math.Abs(forward.Length() - 1) < 1e-6 && Math.Abs(forward.Y) < 1e-6, "the seat forward is a horizontal unit vector in the world", $"{forward}");
        Check(vehicle.GetSeatPose(spawnedButton.InstanceId) == null, "a button has no seat pose");

        // ---- прицел -> E -> сесть (настоящий ввод)
        var player = world.PlayerForTesting;
        var seatCenter = vehicle.ToGlobal(new Vector3(0.5f, 0.375f, 0.125f));
        player.SetPose(seatCenter + new Vector3(0, -1.6f, 1.5f), 0); // камера в 1.5 м перед +Z гранью сиденья, смотрит в -Z
        await Frames(editor, 4);
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.E, Pressed = true });
        await Frames(editor, 2);
        Check(world.IsSeated && world.SeatedInstanceId == spawnedSeat.InstanceId, "E with the crosshair on the pilot seat sits the player down");
        Check(seatState.Occupied && vehicle.Runtime.TryReadNode(spawnedSeat.InstanceId, PilotSeatBehavior.OccupiedNodeId, out var occupied) && occupied.IsOn,
            "...the seat reports occuped = 1");
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.E, Pressed = false });
        await Frames(editor, 3);

        Check(player.Seated, "the player is in the seated state");
        var cameraPosition = player.Camera.GlobalPosition;
        Check(cameraPosition.DistanceTo(eye) < 0.05, "the camera stands at the seat eye point", $"camera={cameraPosition} eye={eye}");
        var collision = player.GetNode<CollisionShape3D>("Collision");
        Check(collision.Disabled, "the player collision is off while seated (the capsule would shove the vehicle)");
        Check(!player.MovementEnabled, "walking is disabled while seated");

        // Сиденье едет вместе с телом постройки - камера следует.
        vehicle.GlobalPosition += new Vector3(2, 0, 0);
        await Frames(editor, 3);
        Check(player.Camera.GlobalPosition.DistanceTo(vehicle.GetSeatPose(spawnedSeat.InstanceId)!.Value.Eye) < 0.05 && Math.Abs(player.Camera.GlobalPosition.X - cameraPosition.X - 2) < 0.05,
            "when the vehicle moves, the seated camera moves with it", $"{player.Camera.GlobalPosition}");

        Check(!world.TrySit(vehicle, spawnedSeat.InstanceId), "sitting again while already seated is refused");

        // ---- Shift -> встать
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Shift, Pressed = true });
        await Frames(editor, 3);
        await PhysicsFrames(editor, Player.StandSettleTicks + 3); // капсула включается через несколько физических тиков после вставания
        Check(!world.IsSeated && !player.Seated && !seatState.Occupied, "Shift stands the player up: the seat is free again");
        Check(!collision.Disabled && player.MovementEnabled, "...the collision and walking are back");
        Check(vehicle.Runtime.TryReadNode(spawnedSeat.InstanceId, PilotSeatBehavior.OccupiedNodeId, out var freed) && !freed.IsOn, "...and occuped is 0");
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Shift, Pressed = false });

        // ---- кнопка: E удерживается - нажата; отпустили - отпущена
        var buttonState = vehicle.Runtime.GetState<ButtonState>(spawnedButton.InstanceId)!;
        var buttonCenter = vehicle.ToGlobal(BuildSpace.CellCenter(new Vector3I(10, 0, 0)));
        player.SetPose(buttonCenter + new Vector3(0, -1.6f, 1.0f), 0);
        await Frames(editor, 4);
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.E, Pressed = true });
        await Frames(editor, 2);
        Check(buttonState.Pressed && !world.IsSeated, "holding E with the crosshair on a button presses it");
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.E, Pressed = false });
        await Frames(editor, 2);
        Check(!buttonState.Pressed, "releasing E releases the button");

        // Прицел в пустоту: E ничего не делает с постройкой.
        player.SetPose(buttonCenter + new Vector3(0, 20, 0), 0);
        await Frames(editor, 3);
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.E, Pressed = true });
        await Frames(editor, 2);
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.E, Pressed = false });
        Check(!world.IsSeated && !buttonState.Pressed, "E aimed at nothing neither sits nor presses anything");

        // Возврат постройки к верстаку, пока сидишь: игрок встаёт, камера не остаётся приклеенной к удалённому телу.
        Check(world.TrySit(vehicle, spawnedSeat.InstanceId), "(setup) sit again through the API");
        vehicle.QueueFree();
        await Frames(editor, 3);
        Check(!world.IsSeated && !player.Seated, "if the vehicle disappears under a seated player, the player is stood up (no stuck camera)");

        await RunStandUpOnDynamicVehicleTests(editor, world);

        world.QueueFree();
        await Frames(editor, 1);
        editor.EditorCamera.Current = true;
    }

    /// <summary>Регрессия «встал с сиденья — постройка улетела» + езда игрока на движущейся постройке (приклеивание, поворот, отклеивание при наклоне).</summary>
    private async Task RunStandUpOnDynamicVehicleTests(BuildEditor editor, GameWorld world)
    {
        GD.Print("-- open world: standing up does not fling a dynamic vehicle; the player rides a moving vehicle and unglues when it tilts");

        var floor = new StaticBody3D { Name = "StandTestFloor", Position = new Vector3(400, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(80, 1, 80) } });
        world.AddChild(floor);
        var player = world.PlayerForTesting;

        // ---- 1. одно настоящее сиденье на динамическом теле
        var seatOnly = new Construction(new VoxelGrid());
        PlaceFixture(seatOnly, "pilot_seat", new Vector3I(0, 0, 0));
        var vehicle = VehicleSpawner.Spawn(world, ConstructionIO.Serialize(seatOnly), new Vector3(400, 0.6f, 0));
        double landingMax = 0;
        for (int i = 0; i < 90; i++)
        {
            await PhysicsFrames(editor, 1);
            landingMax = Math.Max(landingMax, vehicle.LinearVelocity.Length());
        }

        Check(landingMax < 6, "(control) a vehicle with the real seat lands on the floor calmly without any player interaction", $"max speed {landingMax}");

        var seat = vehicle.Construction!.Instances.First(i => i.BlockSlug == "pilot_seat");
        Check(world.TrySit(vehicle, seat.InstanceId), "(setup) sit on the real pilot seat of a dynamic vehicle");
        await PhysicsFrames(editor, 10);

        var restAt = vehicle.GlobalPosition;
        world.Stand();
        double maxSpeed = 0, maxSpin = 0;
        for (int i = 0; i < 120; i++)
        {
            await PhysicsFrames(editor, 1);
            maxSpeed = Math.Max(maxSpeed, vehicle.LinearVelocity.Length());
            maxSpin = Math.Max(maxSpin, vehicle.AngularVelocity.Length());
        }

        Check(maxSpeed < 3 && maxSpin < 3, "standing up from the seat does not fling the vehicle (linear and angular speed stay small)", $"maxSpeed={maxSpeed} maxSpin={maxSpin}");
        Check(vehicle.GlobalPosition.DistanceTo(restAt) < 1, "...the vehicle stays where it was", $"{restAt} -> {vehicle.GlobalPosition}");
        Check(player.GlobalPosition.DistanceTo(vehicle.GlobalPosition) < 4 && !player.Seated, "...and the player stands next to it", $"player={player.GlobalPosition}");
        Check(!player.GetNode<CollisionShape3D>("Collision").Disabled, "...with the capsule collision back on once the physics server has taken the new position");
        vehicle.QueueFree();
        await Frames(editor, 2);

        // ---- 2. плоская палуба с сиденьем сверху: встали -> оказались на палубе, примагничены
        var deckSource = new Construction(new VoxelGrid());
        deckSource.PlaceBlock(new Vector3I(0, 0, 0), new Vector3I(40, 2, 40), BlockCatalog.Instance.Get("block"), Colors.White);
        PlaceFixture(deckSource, "pilot_seat", new Vector3I(18, 2, 18));
        var deck = VehicleSpawner.Spawn(world, ConstructionIO.Serialize(deckSource), new Vector3(400, 0.05f, 0));
        await PhysicsFrames(editor, 60);
        var deckSeat = deck.Construction!.Instances.First(i => i.BlockSlug == "pilot_seat");
        var deckRest = deck.GlobalPosition;
        Check(world.TrySit(deck, deckSeat.InstanceId), "(setup) sit on the seat on top of the deck");
        await PhysicsFrames(editor, 10);

        world.Stand();
        double deckMaxSpeed = 0;
        for (int i = 0; i < 120; i++)
        {
            await PhysicsFrames(editor, 1);
            deckMaxSpeed = Math.Max(deckMaxSpeed, deck.LinearVelocity.Length());
        }

        Check(deckMaxSpeed < 3 && deck.GlobalPosition.DistanceTo(deckRest) < 1, "standing up on top of a deck does not fling it either", $"maxSpeed={deckMaxSpeed} {deck.GlobalPosition}");
        Check(player.IsOnFloor() && player.Platform == deck, "the player who stood up is glued to the vehicle (standing on it)", $"onFloor={player.IsOnFloor()} platform={player.Platform}");

        // ---- 3. палуба едет и поворачивается — игрок и его камера вместе с ней
        deck.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
        deck.Freeze = true;
        await PhysicsFrames(editor, 3);
        var local = deck.ToLocal(player.GlobalPosition);
        double yawBefore = player.Yaw;
        var cameraLocal = deck.ToLocal(player.Camera.GlobalPosition);

        for (int i = 0; i < 60; i++)
        {
            deck.GlobalPosition += new Vector3(0.05f, 0, 0.02f);
            await PhysicsFrames(editor, 1);
        }

        var carried = deck.ToGlobal(local);
        Check(player.Platform == deck && player.GlobalPosition.DistanceTo(carried) < 0.15, "when the vehicle moves, the player moves with it (stays at the same spot on the deck)", $"player={player.GlobalPosition} expected={carried}");
        Check(deck.ToLocal(player.Camera.GlobalPosition).DistanceTo(cameraLocal) < 0.15, "...and the camera is synchronised with the body (same place relative to the vehicle)");

        var center = deck.GlobalPosition;
        for (int i = 0; i < 60; i++)
        {
            deck.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2 / 60 * (i + 1)), center) ;
            await PhysicsFrames(editor, 1);
        }

        carried = deck.ToGlobal(local);
        double yawTurned = Mathf.PosMod(player.Yaw - yawBefore + Math.PI, Math.PI * 2) - Math.PI;
        Check(player.Platform == deck && player.GlobalPosition.DistanceTo(carried) < 0.2, "when the vehicle turns, the player is carried round with it", $"player={player.GlobalPosition} expected={carried}");
        Check(Math.Abs(yawTurned - Math.PI / 2) < 0.1, "...and the view turns by the same angle (the player keeps facing the same way on the vehicle)", $"yaw turned {yawTurned}");

        // ---- 4. небольшой крен — остаётся приклеенным; крутой — отклеивается
        // Наклоняем вокруг точки под ногами игрока: поверхность под ним остаётся на месте и только кренится (а не уходит в пол мира, как при вращении вокруг угла).
        var yawed = deck.GlobalTransform.Basis;
        var pivotLocal = deck.ToLocal(player.GlobalPosition);
        var pivotWorld = player.GlobalPosition;
        async Task TiltTo(int degrees)
        {
            for (int i = 1; i <= degrees; i++)
            {
                var basis = new Basis(Vector3.Forward, Mathf.DegToRad(i)) * yawed;
                deck.GlobalTransform = new Transform3D(basis, pivotWorld - basis * pivotLocal);
                await PhysicsFrames(editor, 1);
            }

            await PhysicsFrames(editor, 10);
        }

        await TiltTo(20);
        Check(player.Platform == deck && player.IsOnFloor(), "a slight tilt (20 degrees) keeps the player on the vehicle", $"platform={player.Platform} onFloor={player.IsOnFloor()}");

        await TiltTo(40);
        Check(player.Platform == null, "when the deck tilts past the limit the player no longer stands on a flat surface and comes unglued", $"platform={player.Platform}");

        deck.QueueFree();
        floor.QueueFree();
        await Frames(editor, 2);
    }
}
