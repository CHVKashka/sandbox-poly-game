using System;
using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.World;

/// <summary>
/// Взаимодействие игрока с блоками заспавненной постройки (часть <see cref="GameWorld"/>): <b>E</b> по прицелу — сесть на пилотское сиденье
/// (<see cref="PilotSeatBehavior"/>) или нажать кнопку (<see cref="ButtonBehavior"/>, держится, пока держишь E); <b>Shift</b> — встать. Пока игрок сидит, мир
/// каждый кадр читает его ввод и отдаёт сиденью (<see cref="ReadSeatInput"/> → <see cref="PilotSeatState.SetInput"/>): клавиши 1–6, ЛКМ/ПКМ, оси WASD и стрелок;
/// сиденье превращает их в сигналы на своих нодах. Камера стоит в точке глаз сиденья (<see cref="VehicleBody.GetSeatPose"/>), мышь по-прежнему вращает взгляд.
/// Одиночная игра: состояние сиденья не реплицируется по сети (как и весь рантайм функциональных блоков).
/// </summary>
public partial class GameWorld
{
    private VehicleBody? _seatVehicle;
    private int _seatInstanceId;
    private VehicleBody? _pressedButtonVehicle;
    private int _pressedButtonInstance;

    /// <summary>Сидит ли игрок на каком-нибудь сиденье.</summary>
    public bool IsSeated => _seatVehicle != null;

    public int SeatedInstanceId => _seatInstanceId;
    public VehicleBody? SeatedVehicle => _seatVehicle;

    /// <summary>Игрок (для самотестов).</summary>
    public Player PlayerForTesting => _player;

    /// <summary>Луч из центра экрана с подробностями попадания (позиция, нормаль, коллайдер); пустой словарь — ничего не попало.</summary>
    private Godot.Collections.Dictionary RaycastDetailed(float maxDistance)
    {
        var camera = _player.Camera;
        var from = camera.GlobalPosition;
        var to = from - camera.GlobalTransform.Basis.Z * maxDistance;
        return GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));
    }

    /// <summary>Блок заспавненной постройки под прицелом и его определение; null — прицел не на блоке постройки.</summary>
    private (VehicleBody Vehicle, Core.BlockInstance Instance, FunctionalBlockComponent Block)? BlockUnderCrosshair()
    {
        var hit = RaycastDetailed(InteractDistance);
        if (hit.Count == 0 || hit["collider"].As<Node3D>() is not VehicleBody vehicle) return null;

        var instance = vehicle.TryGetInstanceAt(hit["position"].AsVector3(), hit["normal"].AsVector3());
        if (instance == null || !BlockCatalog.Instance.TryGetBySlug(instance.BlockSlug, out var definition)) return null;
        var block = definition.GetComponent<FunctionalBlockComponent>();
        return block == null ? null : (vehicle, instance, block);
    }

    /// <summary>Обрабатывает ввод, относящийся к блокам: отпускание E у кнопки, Shift (встать), E по сиденью/кнопке. true — событие поглощено.</summary>
    private bool HandleBlockInteractionInput(InputEvent e)
    {
        if (e is InputEventKey { PhysicalKeycode: Key.E, Pressed: false })
        {
            ReleasePressedButton();
            return false; // отпускание E могут слушать и другие (на всякий случай не поглощаем)
        }

        if (e is not InputEventKey { Pressed: true, Echo: false } key) return false;

        if (key.PhysicalKeycode == Key.Shift && IsSeated)
        {
            Stand();
            return true;
        }

        if (key.PhysicalKeycode == Key.E && !AnyModalOpen && !IsSeated) return TryUseBlockUnderCrosshair();
        return false;
    }

    /// <summary>E по прицелу: сиденье — сесть, кнопка — нажать (до отпускания E). false — прицел не на таком блоке (E пойдёт дальше, например к верстаку).</summary>
    private bool TryUseBlockUnderCrosshair()
    {
        if (BlockUnderCrosshair() is not { } target) return false;

        switch (target.Block.Behavior)
        {
            case PilotSeatBehavior.Key:
                return TrySit(target.Vehicle, target.Instance.InstanceId);

            case ButtonBehavior.Key:
                if (target.Vehicle.Runtime == null) return false;
                ReleasePressedButton();
                _pressedButtonVehicle = target.Vehicle;
                _pressedButtonInstance = target.Instance.InstanceId;
                SendButton(target.Vehicle, _pressedButtonInstance, pressed: true);
                return true;

            default:
                return false;
        }
    }

    private void ReleasePressedButton()
    {
        if (_pressedButtonVehicle != null && IsInstanceValid(_pressedButtonVehicle))
        {
            SendButton(_pressedButtonVehicle, _pressedButtonInstance, pressed: false);
        }

        _pressedButtonVehicle = null;
    }

    private void SendButton(VehicleBody vehicle, int instanceId, bool pressed)
    {
        if (NetHub.Instance.IsNetworked && vehicle.NetId != 0) NetHub.Instance.RequestButton(vehicle.NetId, instanceId, pressed);
        else vehicle.Runtime?.Interact(instanceId, pressed ? BlockInteraction.Press : BlockInteraction.Release);
    }

    // Посадка в сетевой игре: у клиента ответ сервера приходит позже (SitResultForMe) - до него сидим «в ожидании», второй запрос не шлём.
    private (VehicleBody Vehicle, int InstanceId)? _pendingSit;

    /// <summary>
    /// Сесть на сиденье <paramref name="instanceId"/> тела <paramref name="vehicle"/>. false — уже сидим, у постройки нет рантайма, это не сиденье или оно занято. В сетевой игре
    /// сиденье занимает СЕРВЕР (одно сиденье — один игрок): у хоста ответ сразу, у клиента true означает «запрос отправлен», сам игрок садится по ответу (<see cref="OnSitResult"/>).
    /// </summary>
    public bool TrySit(VehicleBody vehicle, int instanceId)
    {
        if (IsSeated || _pendingSit != null || vehicle.Runtime == null) return false;
        var state = vehicle.Runtime.GetState<PilotSeatState>(instanceId);
        if (state == null || state.Occupied || vehicle.GetSeatPose(instanceId) is not { } pose) return false;

        if (NetHub.Instance.IsNetworked && vehicle.NetId != 0)
        {
            if (NetHub.Instance.IsServer)
            {
                if (!NetHub.Instance.ServerSit(NetHub.Instance.LocalPeerId, vehicle.NetId, instanceId)) return false;
                BeginSit(vehicle, instanceId, pose);
                return true;
            }

            _pendingSit = (vehicle, instanceId);
            NetHub.Instance.RequestSit(vehicle.NetId, instanceId);
            return true;
        }

        state.SetOccupied(true);
        BeginSit(vehicle, instanceId, pose);
        return true;
    }

    /// <summary>Сервер ответил на мой запрос посадки: разрешил — сажаем игрока, отказал — ничего не происходит.</summary>
    private void OnSitResult(int netId, int instanceId, bool ok)
    {
        if (_pendingSit is not { } pending || pending.Vehicle.NetId != netId || pending.InstanceId != instanceId) return;
        _pendingSit = null;
        if (!ok) return;

        if (IsSeated || !IsInstanceValid(pending.Vehicle) || pending.Vehicle.GetSeatPose(instanceId) is not { } pose)
        {
            NetHub.Instance.RequestStand(netId, instanceId); // сиденье уже занято за меня, а сесть не получилось - освободить
            return;
        }

        BeginSit(pending.Vehicle, instanceId, pose);
    }

    private void BeginSit(VehicleBody vehicle, int instanceId, (Vector3 Eye, Vector3 Forward) pose)
    {
        _seatVehicle = vehicle;
        _seatInstanceId = instanceId;
        _lastSentSeatInput = null;
        // Глаза читаются каждый физический тик; если тело постройки уже освобождено (вернули к верстаку), остаётся последняя известная точка, а не обращение
        // к мёртвому узлу - сам игрок встанет в ближайшем UpdateSeat.
        var lastEye = pose.Eye;
        _player.Sit(() =>
        {
            if (IsInstanceValid(vehicle) && vehicle.GetSeatPose(instanceId) is { } current) lastEye = current.Eye;
            return lastEye;
        }, Math.Atan2(-pose.Forward.X, -pose.Forward.Z));
    }

    /// <summary>Встать с сиденья: сиденье освобождается (ввод обнуляется), игрок оказывается над и чуть впереди сиденья и падает на землю/постройку.</summary>
    public void Stand()
    {
        if (!IsSeated) return;

        var vehicle = _seatVehicle!;
        var preferred = _player.GlobalPosition;
        var sideways = new List<Vector3> { Vector3.Zero };
        if (IsInstanceValid(vehicle))
        {
            if (NetHub.Instance.IsNetworked && vehicle.NetId != 0) NetHub.Instance.RequestStand(vehicle.NetId, _seatInstanceId);
            else vehicle.Runtime?.GetState<PilotSeatState>(_seatInstanceId)?.SetOccupied(false);
            if (vehicle.GetSeatPose(_seatInstanceId) is { } pose)
            {
                // Ноги там, где у сидящего сидение (глаза - на высоте eyeHeight над клеткой): ищем ближайшее СВОБОДНОЕ место - над сиденьем или перед ним.
                preferred = pose.Eye - new Vector3(0, 0.9f, 0);
                sideways = new List<Vector3> { pose.Forward * 0.5f, pose.Forward, Vector3.Zero, -pose.Forward * 0.5f };
            }
        }

        _seatVehicle = null;

        // Капсула, появившаяся внутри тела постройки, выталкивается из него огромным импульсом - постройка улетает. Поэтому встаём только в свободное место
        // и сразу «приклеиваемся» к телу, с которого встали: дальше игрок едет вместе с ним (см. Player.Platform).
        var standAt = _player.FindFreePosition(preferred, sideways);
        _player.Stand(standAt, IsInstanceValid(vehicle) ? vehicle : null);
    }

    private SeatInput? _lastSentSeatInput;
    private double _seatInputAge;

    /// <summary>Каждый кадр: если игрок сидит — читает его ввод и отдаёт сиденью; если тело постройки исчезло (вернули к верстаку) — встаёт.</summary>
    private void UpdateSeat(double delta = 0)
    {
        if (!IsSeated) return;

        var vehicle = _seatVehicle!;
        var state = IsInstanceValid(vehicle) ? vehicle.Runtime?.GetState<PilotSeatState>(_seatInstanceId) : null;
        if (state == null)
        {
            Stand();
            return;
        }

        bool live = !AnyModalOpen && Input.MouseMode == Input.MouseModeEnum.Captured;
        var input = live ? ReadSeatInput(Input.IsPhysicalKeyPressed, Input.IsMouseButtonPressed) : SeatInput.None;

        if (!NetHub.Instance.IsNetworked || vehicle.NetId == 0)
        {
            state.SetInput(input);
            return;
        }

        // Сетевая игра: ввод уходит серверу (хосту — сразу). Клиент шлёт только изменения и раз в 0.2 с как «я здесь» (потерянный пакет не залипит клавишу надолго).
        _seatInputAge += delta;
        if (_lastSentSeatInput is { } last && SameSeatInput(last, input) && _seatInputAge < 0.2) return;
        _lastSentSeatInput = input;
        _seatInputAge = 0;
        NetHub.Instance.SendSeatInput(vehicle.NetId, _seatInstanceId, input);
    }

    private static bool SameSeatInput(SeatInput a, SeatInput b) =>
        a.Trigger1 == b.Trigger1 && a.Trigger2 == b.Trigger2 && a.Hotkeys.AsSpan().SequenceEqual(b.Hotkeys) && a.Axes.AsSpan().SequenceEqual(b.Axes);

    /// <summary>
    /// Ввод пилота за кадр: клавиши 1–6, ЛКМ/ПКМ, оси — A/D (D = +1), W/S (W = +1), стрелки влево/вправо (вправо = +1), стрелки вверх/вниз (вверх = +1).
    /// Чистая функция от «нажата ли клавиша/кнопка» — тестируется без реального ввода.
    /// </summary>
    public static SeatInput ReadSeatInput(Func<Key, bool> keyDown, Func<MouseButton, bool> mouseDown)
    {
        var hotkeys = new bool[PilotSeatBehavior.HotkeyCount];
        for (int i = 0; i < hotkeys.Length; i++) hotkeys[i] = keyDown(Key.Key1 + i);

        double Axis(Key positive, Key negative) => (keyDown(positive) ? 1.0 : 0.0) - (keyDown(negative) ? 1.0 : 0.0);
        return new SeatInput(
            hotkeys,
            mouseDown(MouseButton.Left),
            mouseDown(MouseButton.Right),
            new[] { Axis(Key.D, Key.A), Axis(Key.W, Key.S), Axis(Key.Right, Key.Left), Axis(Key.Up, Key.Down) });
    }

    /// <summary>Подсказка внизу экрана для блока под прицелом (сиденье/кнопка) и для сидящего игрока; пусто — нечего подсказывать.</summary>
    private string BlockPrompt()
    {
        if (IsSeated) return "Seated: 1-6 hotkeys, WASD/arrows axes, LMB/RMB triggers, Shift - stand up";
        if (BlockUnderCrosshair() is not { } target) return "";

        return target.Block.Behavior switch
        {
            PilotSeatBehavior.Key => "Press E to sit",
            ButtonBehavior.Key => "Hold E to press the button",
            _ => "",
        };
    }
}
