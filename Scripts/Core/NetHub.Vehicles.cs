using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SandboxPolyGame.Runtime;
using SandboxPolyGame.World;

namespace SandboxPolyGame.Core;

/// <summary>
/// Репликация заспавненных построек и их рантайма (партиал <see cref="NetHub"/>). <b>Сервер владеет всем</b>: физикой тел (<see cref="VehicleBody"/>) и рантаймом функциональных
/// блоков (заряд, сигналы, обороты, кнопки, сиденья); клиенты держат копии-наблюдатели (<see cref="VehicleBody.IsPuppet"/>), собранные из того же JSON, и получают от сервера
/// <list type="bullet">
/// <item>создание/удаление тела (надёжно), для подключившегося позже — все уже существующие тела;</item>
/// <item>позы тел (~30 Гц, без гарантий доставки, старые пакеты отбрасываются) — <see cref="VehicleReplication"/>;</item>
/// <item>снимки рантайма (10 Гц, надёжно) — <see cref="FunctionalBlockRuntime.CaptureNet"/>.</item>
/// </list>
/// Ввод идёт обратно запросами к серверу: посадка/вставание, клавиши пилота (<see cref="SeatInput"/>), нажатие кнопки. Сервер проверяет всё сам: сиденье может занять один игрок
/// (<see cref="ServerSit"/>), ввод принимается только от того, кто на нём сидит. Отключившийся игрок освобождает сиденье и отпускает кнопку.
/// <para/>
/// <b>Где живут тела:</b> на <see cref="VehiclesRoot"/> (дочерний узел автозагрузки), а не под <c>World.GameWorld</c> — иначе уход хоста в редактор (смена сцены) удалил бы тела у всех
/// (тот же урок, что у игроков, см. <see cref="EnsurePlayerReplication"/>). Пока пир в редакторе, корень скрыт (<see cref="SetVehiclesVisible"/>), у сервера есть свой невидимый
/// пол (иначе тела падали бы сквозь освобождённый террейн). Одиночная игра этот слой не использует — <c>GameWorld</c> спавнит тела сам.
/// </summary>
public partial class NetHub
{
    /// <summary>Корень сетевых тел построек; null — ещё ни одного не было.</summary>
    public Node3D? VehiclesRoot { get; private set; }

    private readonly Dictionary<int, VehicleBody> _vehicleBodies = new();
    private readonly Dictionary<int, string> _vehicleJson = new();
    private readonly Dictionary<long, (int NetId, int Instance)> _seatOfPeer = new();
    private readonly Dictionary<long, (int NetId, int Instance)> _buttonOfPeer = new();
    private StaticBody3D? _vehicleGround;
    private int _vehicleCounter;
    private int _poseTick;
    private double _runtimeTimer;
    private bool _vehiclesVisible = true;

    /// <summary>Период рассылки снимков рантайма, секунд.</summary>
    public const double RuntimeSyncInterval = 0.1;

    public IReadOnlyCollection<VehicleBody> Vehicles => _vehicleBodies.Values;

    public VehicleBody? GetVehicle(int netId) => _vehicleBodies.TryGetValue(netId, out var body) && IsInstanceValid(body) ? body : null;

    /// <summary>Тело появилось (на сервере — сразу при спавне, на клиенте — по сообщению сервера).</summary>
    public event Action<VehicleBody>? VehicleSpawned;

    /// <summary>Тело убрано (возврат к верстаку); параметр — его <see cref="VehicleBody.NetId"/>.</summary>
    public event Action<int>? VehicleRemoved;

    /// <summary>Ответ на МОЙ <see cref="RequestSit"/>: номер тела, id блока-сиденья, разрешил ли сервер.</summary>
    public event Action<int, int, bool>? SitResultForMe;

    // ------------------------------------------------------------------------------------------------ корень и жизненный цикл

    private void EnsureVehicleRoot()
    {
        if (VehiclesRoot != null) return;

        VehiclesRoot = new Node3D { Name = "NetVehicles", Visible = _vehiclesVisible };
        AddChild(VehiclesRoot);

        if (!IsServer) return;

        // Пол сервера: той же формы и размера, что и террейн мира (GameWorld.TileSize), но не зависящий от сцены, которую хост может покинуть.
        _vehicleGround = new StaticBody3D { Name = "VehicleGround" };
        _vehicleGround.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(GameWorld.TileSize, 1f, GameWorld.TileSize) }, Position = new Vector3(0, -0.5f, 0) });
        VehiclesRoot.AddChild(_vehicleGround);

        PeerConnected += OnPeerConnectedVehicles;
        PeerDisconnected += OnPeerDisconnectedVehicles;
    }

    /// <summary>Показать/спрятать все тела (пока пир в редакторе, они не должны маячить посреди его сцены). Физика при этом не останавливается.</summary>
    public void SetVehiclesVisible(bool visible)
    {
        _vehiclesVisible = visible;
        if (VehiclesRoot != null) VehiclesRoot.Visible = visible;
    }

    private void ClearVehicles()
    {
        PeerConnected -= OnPeerConnectedVehicles;
        PeerDisconnected -= OnPeerDisconnectedVehicles;
        VehiclesRoot?.QueueFree();
        VehiclesRoot = null;
        _vehicleGround = null;
        _vehicleBodies.Clear();
        _vehicleJson.Clear();
        _seatOfPeer.Clear();
        _buttonOfPeer.Clear();
        _vehicleCounter = 0;
    }

    private void RegisterVehicle(VehicleBody body, string json)
    {
        _vehicleBodies[body.NetId] = body;
        _vehicleJson[body.NetId] = json;
    }

    private static Transform3D PoseTransform(double[] pose) =>
        new(new Basis(new Quaternion(pose[4], pose[5], pose[6], pose[7]).Normalized()), new Vector3(pose[1], pose[2], pose[3]));

    // ------------------------------------------------------------------------------------------------ спавн и возврат

    /// <summary>Запросить создание тела по постройке (любой пир → сервер). Сервер создаёт тело и рассылает его всем; результат приходит событием <see cref="VehicleSpawned"/>.</summary>
    public void RequestSpawnVehicle(string constructionJson, Vector3 position, string workbenchName)
    {
        if (IsServer) ServerSpawnVehicle(constructionJson, position, workbenchName);
        else RpcId(1, nameof(RpcRequestSpawnVehicle), constructionJson, position, workbenchName);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestSpawnVehicle(string constructionJson, Vector3 position, string workbenchName) => ServerSpawnVehicle(constructionJson, position, workbenchName);

    /// <summary>Сервер: создать тело и разослать всем подключённым. null — постройка не разобралась.</summary>
    public VehicleBody? ServerSpawnVehicle(string constructionJson, Vector3 position, string workbenchName)
    {
        if (!IsServer) return null;
        EnsureVehicleRoot();

        VehicleBody body;
        try
        {
            body = VehicleSpawner.Spawn(VehiclesRoot!, constructionJson, position);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[net] a vehicle could not be spawned: {e.Message}");
            return null;
        }

        body.NetId = ++_vehicleCounter;
        body.SourceWorkbenchName = workbenchName;
        RegisterVehicle(body, constructionJson);

        var pose = VehicleReplication.CapturePoses(new[] { body });
        foreach (long peerId in Multiplayer.GetPeers()) RpcId(peerId, nameof(RpcVehicleSpawned), body.NetId, constructionJson, workbenchName, pose);
        VehicleSpawned?.Invoke(body);
        return body;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcVehicleSpawned(int netId, string constructionJson, string workbenchName, double[] pose) => ClientCreatePuppet(netId, constructionJson, workbenchName, pose);

    /// <summary>Клиент: создать копию-наблюдателя серверного тела (по сообщению сервера; открыт для самотеста). Повторное сообщение про то же тело игнорируется.</summary>
    public VehicleBody? ClientCreatePuppet(int netId, string constructionJson, string workbenchName, double[] pose)
    {
        if (GetVehicle(netId) is { } existing) return existing;
        EnsureVehicleRoot();

        VehicleBody body;
        try
        {
            body = VehicleReplication.CreatePuppet(VehiclesRoot!, netId, constructionJson, workbenchName, PoseTransform(pose));
        }
        catch (Exception e)
        {
            GD.PushWarning($"[net] a vehicle copy could not be built: {e.Message}");
            return null;
        }

        RegisterVehicle(body, constructionJson);
        VehicleSpawned?.Invoke(body);
        return body;
    }

    /// <summary>Вернуть тело к верстаку (убрать): любой пир → сервер, тот убирает у всех.</summary>
    public void RequestRecallVehicle(int netId)
    {
        if (IsServer) ServerRecallVehicle(netId);
        else RpcId(1, nameof(RpcRequestRecall), netId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestRecall(int netId) => ServerRecallVehicle(netId);

    public bool ServerRecallVehicle(int netId)
    {
        if (!IsServer || GetVehicle(netId) == null) return false;

        RemoveVehicleLocal(netId);
        Rpc(nameof(RpcVehicleRemoved), netId);
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcVehicleRemoved(int netId) => RemoveVehicleLocal(netId);

    private void RemoveVehicleLocal(int netId)
    {
        foreach (var peer in _seatOfPeer.Where(p => p.Value.NetId == netId).Select(p => p.Key).ToList()) _seatOfPeer.Remove(peer);
        foreach (var peer in _buttonOfPeer.Where(p => p.Value.NetId == netId).Select(p => p.Key).ToList()) _buttonOfPeer.Remove(peer);

        if (_vehicleBodies.Remove(netId, out var body) && IsInstanceValid(body)) body.QueueFree();
        _vehicleJson.Remove(netId);
        VehicleRemoved?.Invoke(netId);
    }

    // ------------------------------------------------------------------------------------------------ рассылка поз и рантайма

    public override void _PhysicsProcess(double delta)
    {
        // Соединения может не быть (сервер ушёл, а мы ещё не вернулись в лобби): тогда спрашивать у движка, кто сервер, нельзя - он ругается каждый тик.
        if (_vehicleBodies.Count == 0 || !_isNetworked || Multiplayer.MultiplayerPeer is not { } peer || peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected) return;
        if (!IsServer || Multiplayer.GetPeers().Length == 0) return;

        if ((++_poseTick & 1) == 0) Rpc(nameof(RpcVehiclePoses), VehicleReplication.CapturePoses(_vehicleBodies.Values));

        _runtimeTimer += delta;
        if (_runtimeTimer < RuntimeSyncInterval) return;
        _runtimeTimer = 0;

        foreach (var body in _vehicleBodies.Values)
        {
            if (!IsInstanceValid(body) || body.Runtime == null) continue;
            var (ids, lengths, values) = body.Runtime.CaptureNet();
            Rpc(nameof(RpcVehicleRuntime), body.NetId, ids, lengths, values);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void RpcVehiclePoses(double[] poses) => VehicleReplication.ApplyPoses(poses, GetVehicle);

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcVehicleRuntime(int netId, int[] ids, int[] lengths, double[] values) => GetVehicle(netId)?.Runtime?.ApplyNet(ids, lengths, values);

    private void OnPeerConnectedVehicles(long peerId)
    {
        // Подключившийся позже получает все уже существующие тела (текущая поза; состояние рантайма придёт со ближайшим снимком).
        foreach (var body in _vehicleBodies.Values)
        {
            if (IsInstanceValid(body)) RpcId(peerId, nameof(RpcVehicleSpawned), body.NetId, _vehicleJson[body.NetId], body.SourceWorkbenchName, VehicleReplication.CapturePoses(new[] { body }));
        }
    }

    private void OnPeerDisconnectedVehicles(long peerId) => ServerReleasePeer(peerId);

    // ------------------------------------------------------------------------------------------------ сиденье

    /// <summary>Запросить посадку на сиденье <paramref name="instanceId"/> тела <paramref name="netId"/>; ответ — событие <see cref="SitResultForMe"/> (на сервере — сразу).</summary>
    public void RequestSit(int netId, int instanceId)
    {
        if (IsServer) SitResultForMe?.Invoke(netId, instanceId, ServerSit(LocalPeerId, netId, instanceId));
        else RpcId(1, nameof(RpcRequestSit), netId, instanceId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestSit(int netId, int instanceId)
    {
        long sender = Multiplayer.GetRemoteSenderId();
        RpcId(sender, nameof(RpcSitResult), netId, instanceId, ServerSit(sender, netId, instanceId));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcSitResult(int netId, int instanceId, bool ok) => SitResultForMe?.Invoke(netId, instanceId, ok);

    /// <summary>Сервер: посадить игрока <paramref name="peerId"/>. Отказ — нет тела/сиденья, оно занято или игрок уже сидит где-то.</summary>
    public bool ServerSit(long peerId, int netId, int instanceId)
    {
        if (GetVehicle(netId) is not { Runtime: { } runtime } vehicle) return false;
        if (_seatOfPeer.ContainsKey(peerId)) return false;

        var state = runtime.GetState<PilotSeatState>(instanceId);
        if (state == null || state.Occupied || vehicle.GetSeatPose(instanceId) == null) return false;

        state.SetOccupied(true);
        _seatOfPeer[peerId] = (netId, instanceId);
        return true;
    }

    public void RequestStand(int netId, int instanceId)
    {
        if (IsServer) ServerStand(LocalPeerId, netId, instanceId);
        else RpcId(1, nameof(RpcRequestStand), netId, instanceId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestStand(int netId, int instanceId) => ServerStand(Multiplayer.GetRemoteSenderId(), netId, instanceId);

    /// <summary>Сервер: игрок встаёт. Чужое сиденье освободить нельзя — только тот, кто на нём сидит.</summary>
    public bool ServerStand(long peerId, int netId, int instanceId)
    {
        if (!_seatOfPeer.TryGetValue(peerId, out var seat) || seat != (netId, instanceId)) return false;

        _seatOfPeer.Remove(peerId);
        if (GetVehicle(netId)?.Runtime?.GetState<PilotSeatState>(instanceId) is { } state) state.SetOccupied(false);
        return true;
    }

    /// <summary>Отправить ввод пилота кадра (сервер применяет к сиденью; от не сидящего — игнорируется).</summary>
    public void SendSeatInput(int netId, int instanceId, SeatInput input)
    {
        if (IsServer)
        {
            ServerSeatInput(LocalPeerId, netId, instanceId, input);
            return;
        }

        int mask = 0;
        for (int i = 0; i < input.Hotkeys.Length && i < PilotSeatBehavior.HotkeyCount; i++) if (input.Hotkeys[i]) mask |= 1 << i;
        if (input.Trigger1) mask |= 1 << 6;
        if (input.Trigger2) mask |= 1 << 7;
        RpcId(1, nameof(RpcSeatInput), netId, instanceId, mask, input.Axes);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcSeatInput(int netId, int instanceId, int mask, double[] axes)
    {
        var hotkeys = new bool[PilotSeatBehavior.HotkeyCount];
        for (int i = 0; i < hotkeys.Length; i++) hotkeys[i] = (mask & (1 << i)) != 0;
        ServerSeatInput(Multiplayer.GetRemoteSenderId(), netId, instanceId, new SeatInput(hotkeys, (mask & (1 << 6)) != 0, (mask & (1 << 7)) != 0, axes));
    }

    public bool ServerSeatInput(long peerId, int netId, int instanceId, SeatInput input)
    {
        if (!_seatOfPeer.TryGetValue(peerId, out var seat) || seat != (netId, instanceId)) return false;
        if (GetVehicle(netId)?.Runtime?.GetState<PilotSeatState>(instanceId) is not { } state) return false;

        state.SetInput(input);
        return true;
    }

    // ------------------------------------------------------------------------------------------------ кнопки

    /// <summary>Нажать (true) или отпустить (false) кнопку блока <paramref name="instanceId"/>; пока игрок держит кнопку, отключение отпустит её.</summary>
    public void RequestButton(int netId, int instanceId, bool pressed)
    {
        if (IsServer) ServerButton(LocalPeerId, netId, instanceId, pressed);
        else RpcId(1, nameof(RpcRequestButton), netId, instanceId, pressed);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestButton(int netId, int instanceId, bool pressed) => ServerButton(Multiplayer.GetRemoteSenderId(), netId, instanceId, pressed);

    public bool ServerButton(long peerId, int netId, int instanceId, bool pressed)
    {
        if (GetVehicle(netId)?.Runtime is not { } runtime) return false;

        if (pressed)
        {
            if (runtime.GetState<ButtonState>(instanceId) == null) return false;
            ServerReleaseButton(peerId); // у игрока одна рука
            runtime.Interact(instanceId, BlockInteraction.Press);
            _buttonOfPeer[peerId] = (netId, instanceId);
            return true;
        }

        if (!_buttonOfPeer.TryGetValue(peerId, out var held) || held != (netId, instanceId)) return false;
        ServerReleaseButton(peerId);
        return true;
    }

    private void ServerReleaseButton(long peerId)
    {
        if (!_buttonOfPeer.Remove(peerId, out var held)) return;
        GetVehicle(held.NetId)?.Runtime?.Interact(held.Instance, BlockInteraction.Release);
    }

    /// <summary>Пир ушёл (или тело убрано его владельцем): освободить его сиденье и отпустить его кнопку, чтобы они не «залипли».</summary>
    public void ServerReleasePeer(long peerId)
    {
        ServerReleaseButton(peerId);
        if (_seatOfPeer.TryGetValue(peerId, out var seat)) ServerStand(peerId, seat.NetId, seat.Instance);
    }

    /// <summary>Кто сейчас сидит на сиденье (для самотеста); 0 — никто.</summary>
    public long SeatedPeer(int netId, int instanceId) => _seatOfPeer.FirstOrDefault(p => p.Value == (netId, instanceId)).Key;
}
