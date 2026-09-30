using System;
using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.World;

namespace SandboxPolyGame.Core;

/// <summary>
/// Единая точка сетевого мультиплеера (см. Docs/05-world-and-vehicle-systems.md, «Мультиплеер») — топология
/// клиент-сервер, listen server (хост тоже играет), транспорт по умолчанию <see cref="ENetMultiplayerPeer"/>.
/// <para/>
/// Автозагрузка (<c>project.godot</c>, <c>[autoload]</c>), а не обычный узел сцены — принципиально: RPC требуют
/// одинакового <see cref="NodePath"/> узла у всех участников, а сессии верстака (см. <see cref="WorkbenchSession"/>)
/// должны пережить переход мир → редактор → мир (<see cref="SceneTree.ChangeSceneToFile"/> заменяет ИМЕННО текущую
/// сцену, автозагрузки — её братья по дереву, не трогаются). Как и <see cref="EditorHandoff"/>, но тот обходится
/// простыми статическими полями, потому что ему не нужен RPC — этому нужен, поэтому обычный `static` тут не подошёл
/// бы (RPC-методы обязаны быть на живом узле дерева).
/// <para/>
/// <b>Кто с кем говорит</b>: каждый публичный <c>RequestX</c>/<c>RespondToX</c> — то, что вызывает игровой код
/// (World/Editor UI) на СВОЁМ локальном инстансе, независимо от того, сервер он или клиент, — сам решает, обработать
/// ли запрос на месте (<see cref="IsServer"/>) или переслать на сервер (<c>RpcId(1, ...)</c>). Ответы сервера
/// конкретному игроку (SessionReady/JoinAccepted/... ) идут через приватные `SendX`-хелперы той же формы: себе —
/// напрямую (вызов события), другим — тем же <c>RpcId</c>. Такая единая форма ("это я? вызови напрямую — иначе по
/// сети") не полагается на недокументированные тонкости поведения Godot при `RpcId(self, ...)`.
/// <para/>
/// <b>Не сделано в этом проходе</b> (см. открытые вопросы в 05): пары логин/имя игрока (участники видны только по
/// numeric peer id), выделенный сервер без локального игрока (архитектурно не мешает, но не проверялся).
/// </summary>
public partial class NetHub : Node
{
    public const int DefaultPort = 7777;

    public static NetHub Instance { get; private set; } = null!;

    private readonly Dictionary<string, WorkbenchSession> _sessions = new();
    private readonly HashSet<string> _activeSessionNames = new();

    /// <summary>Контейнер заспавненных <see cref="Player"/> — живёт ЗДЕСЬ (автозагрузка), не в
    /// <c>World.GameWorld</c>, см. <see cref="EnsurePlayerReplication"/> class doc.</summary>
    public Node3D? PlayersRoot { get; private set; }

    private MultiplayerSpawner? _playerSpawner;

    // ВАЖНО: не "Multiplayer.MultiplayerPeer != null" - у SceneTree есть какой-то ненулевой peer по умолчанию даже
    // без единого вызова Host/Join (проверено самотестом: IsNetworked оказывался true в целиком одиночной игре,
    // из-за чего GameWorld уходил в сетевую ветку SetupNetworkedPlayers и НИКОГДА не получал своего Player -
    // MultiplayerSpawner ждёт вызова сервера, которого в одиночной игре никто не делает). Держим свой явный флаг,
    // выставляемый только этим классом (Host/Join/Disconnect), а не выведенный из состояния движка.
    private bool _isNetworked;

    public bool IsNetworked => _isNetworked;
    public bool IsServer => IsNetworked && Multiplayer.IsServer();
    public long LocalPeerId => Multiplayer.GetUniqueId();

    /// <summary>Игрок уже выбрал Solo/Host/Join на <see cref="World.Ui.NetworkLobbyUi"/> в этом запуске процесса —
    /// живёт на автозагрузке (не в <c>World.GameWorld</c>), поэтому переживает переход мир → редактор → мир и не
    /// спрашивает заново при каждом возврате в мир.</summary>
    public bool LobbyResolved { get; set; }

    public event Action<long>? PeerConnected;
    public event Action<long>? PeerDisconnected;
    public event Action? ConnectedToServer;
    public event Action? ConnectionFailed;
    public event Action? ServerDisconnected;

    /// <summary>На этом верстаке появилась/пропала активная сессия — драйвит кнопку Join в
    /// <c>World.Ui.WorkbenchMenuUi</c> у ВСЕХ игроков, не только участников.</summary>
    public event Action<string, bool>? SessionActiveChanged;

    /// <summary>Ответ на МОЙ <see cref="RequestOpenSession"/> — пора входить в редактор с этой постройкой.</summary>
    public event Action<string, string>? SessionReadyForMe;

    /// <summary>Я админ этого верстака, кто-то (peer id) просится присоединиться — показать попап принять/отклонить.</summary>
    public event Action<string, long>? JoinRequestIncoming;

    /// <summary>Админ принял мою заявку — пора входить в редактор с этой постройкой (как и SessionReadyForMe).</summary>
    public event Action<string, string>? JoinAcceptedForMe;

    public event Action<string, string>? JoinRejectedForMe;

    /// <summary>Заявитель отменил свою заявку (кнопка Cancel) — я админ и должен убрать попап, если он ещё
    /// показывает ИМЕННО эту заявку (см. <see cref="RequestCancelJoin"/>).</summary>
    public event Action<string, long>? JoinCancelled;

    /// <summary>Админ завершил сессию (вышел/заспавнил) — участников без предупреждения вышвыривает обратно в мир.</summary>
    public event Action<string>? SessionClosedForMe;

    /// <summary>Правка принята сервером и разослана всем участникам сессии (включая меня, если я один из них) —
    /// см. <see cref="NetEditOps.Apply"/>, тот же метод применяет её и здесь, и на сервере. Последние два параметра —
    /// <c>extraInt</c>/<c>extraBool</c>, см. <see cref="NetEditKind"/> doc на каждом значении.</summary>
    public event Action<string, NetEditKind, Vector3I, Vector3I, string, Color, Vector3I, Vector3I, int, bool>? EditApplied;

    /// <summary>Undo/Redo дали результат (приняты) — полный снэпшот постройки сессии ПОСЛЕ отката/повтора (не
    /// поштучная правка, как <see cref="EditApplied"/> — Undo/Redo может затронуть произвольное число блоков разом,
    /// см. <see cref="UndoHistory.Restore"/>), применяется тем же методом у каждого участника.</summary>
    public event Action<string, UndoHistory.Snapshot>? SessionSynced;

    /// <summary>Мой Undo/Redo отклонён — см. <see cref="ServerUndo"/>/<see cref="ServerRedo"/> про причины (ничего
    /// отменять, или отменяемое — не моё последнее действие, см. Docs/05, «Мультиплеер», про правило "только самое
    /// верхнее по времени действие, и только его автором").</summary>
    public event Action<string, string>? UndoRedoRejected;

    /// <summary>Заспавнен игрок — свой или чужой, <c>World.GameWorld</c> сам решает по <see cref="Node.IsMultiplayerAuthority"/>.
    /// Стреляет и для локального спавна (сервер сам себя уведомляет, см. <see cref="EnsurePlayerReplication"/> class
    /// doc — движковый <see cref="MultiplayerSpawner.Spawned"/> для инициатора не срабатывает вообще), и по
    /// настоящему сигналу (клиент, чей игрок пришёл по сети).</summary>
    public event Action<Player>? PlayerSpawned;

    public override void _EnterTree()
    {
        Instance = this;
        Multiplayer.PeerConnected += id => PeerConnected?.Invoke(id);
        Multiplayer.PeerDisconnected += id => PeerDisconnected?.Invoke(id);
        Multiplayer.ConnectedToServer += () => ConnectedToServer?.Invoke();
        Multiplayer.ConnectionFailed += () => ConnectionFailed?.Invoke();
        Multiplayer.ServerDisconnected += () => ServerDisconnected?.Invoke();
    }

    // ------------------------------------------------------------------------------------------------ подключение

    public Error Host(int port = DefaultPort)
    {
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateServer(port);
        if (err != Error.Ok) return err;
        Multiplayer.MultiplayerPeer = peer;
        _isNetworked = true;
        return Error.Ok;
    }

    public Error Join(string address, int port = DefaultPort)
    {
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateClient(address, port);
        if (err != Error.Ok) return err;
        Multiplayer.MultiplayerPeer = peer;
        _isNetworked = true;
        return Error.Ok;
    }

    public void Disconnect()
    {
        // Close() ЯВНО, не просто обнулить ссылку - иначе нижележащий ENet-сокет (UDP-порт сервера) мог не
        // освободиться сразу (баг, найденный пользователем: "Couldn't create an ENet host" при повторном Host()
        // после Exit to menu - предыдущий peer всё ещё держал тот же порт).
        Multiplayer.MultiplayerPeer?.Close();
        Multiplayer.MultiplayerPeer = null;
        _isNetworked = false;
        _sessions.Clear();
        _activeSessionNames.Clear();

        PeerConnected -= OnPeerConnectedSpawn;
        PeerDisconnected -= OnPeerDisconnectedDespawn;
        PlayersRoot?.QueueFree();
        PlayersRoot = null;
        _playerSpawner?.QueueFree();
        _playerSpawner = null;
    }

    // ------------------------------------------------------------------------------------------------ игроки

    /// <summary>
    /// Контейнер+спаунер игроков живут ЗДЕСЬ (автозагрузка), не в <c>World.GameWorld</c> — та же причина, что и у
    /// сессий верстака: обычный сценовый узел не пережил бы собственный переход ЭТОГО пира мир→редактор→мир
    /// (<see cref="SceneTree.ChangeSceneToFile"/> меняет только текущую сцену). Раньше жили в <c>GameWorld</c> —
    /// когда СЕРВЕР входил в свой редактор, его собственная пересборка сцены попутно уничтожала ВСЕХ реплицированных
    /// игроков у ВСЕХ клиентов разом (сервер авторитативен над существованием этих узлов, см.
    /// <see cref="MultiplayerSpawner"/>) — баг, пойманный пользователем (2026-09-29 (9)): у остальных игроков экран
    /// становился серым (потеря активной камеры), в консоль летел <c>ObjectDisposedException</c>.
    /// <para/>
    /// Идемпотентно — повторный вызов (например, этот же пир вернулся в мир из редактора) ничего не пересоздаёт.
    /// <b>Возвращает</b> <c>true</c>, если контейнер уже существовал (переиспользован) — вызывающий
    /// (<c>GameWorld.SetupNetworkedPlayers</c>) в этом случае должен сам найти СВОЙ уже существующий узел по имени
    /// (peer id), не дожидаясь <see cref="PlayerSpawned"/> — он для уже существующего узла повторно не придёт.
    /// <c>false</c> — контейнер создан только что этим вызовом (и, если это сервер, уже успел заспавнить всех
    /// текущих участников — <see cref="PlayerSpawned"/> для них уже сработал синхронно, ДО возврата из этого метода).
    /// </summary>
    public bool EnsurePlayerReplication()
    {
        if (PlayersRoot != null) return true;

        PlayersRoot = new Node3D { Name = "NetPlayers" };
        AddChild(PlayersRoot);

        _playerSpawner = new MultiplayerSpawner { Name = "PlayerSpawner" };
        AddChild(_playerSpawner);
        _playerSpawner.SpawnPath = PlayersRoot.GetPath();
        _playerSpawner.SpawnFunction = Callable.From<Variant, Node>(SpawnPlayerFunc);
        _playerSpawner.Spawned += node => { if (node is Player player) PlayerSpawned?.Invoke(player); };

        if (IsServer)
        {
            SpawnPlayerForPeer(LocalPeerId);
            foreach (long peerId in Multiplayer.GetPeers()) SpawnPlayerForPeer(peerId);
            PeerConnected += OnPeerConnectedSpawn;
            PeerDisconnected += OnPeerDisconnectedDespawn;
        }

        return false;
    }

    /// <summary><see cref="MultiplayerSpawner.Spawned"/> НЕ стреляет для peer'а, который сам вызвал
    /// <see cref="MultiplayerSpawner.Spawn"/> — эмиттится только на принимающей стороне разбора ВХОДЯЩЕГО сетевого
    /// пакета спавна (проверено чтением исходников движка, см. WORKLOG 2026-09-29 (8)). Раз спавнит всегда сервер
    /// (в т.ч. себя самого), сервер уведомляет себя сам по возвращаемому значению, а не через сигнал.</summary>
    private void SpawnPlayerForPeer(long peerId)
    {
        if (_playerSpawner!.Spawn(peerId) is Player player) PlayerSpawned?.Invoke(player);
    }

    private void OnPeerConnectedSpawn(long peerId) => SpawnPlayerForPeer(peerId);

    private void OnPeerDisconnectedDespawn(long peerId) => PlayersRoot?.GetNodeOrNull(peerId.ToString())?.QueueFree();

    /// <summary><see cref="MultiplayerSpawner.SpawnFunction"/> — вызывается на сервере для спавна (себя или
    /// клиента), и на каждом клиенте при разборе реплицированного пакета спавна — статический, без состояния,
    /// не завязан ни на какой конкретный (транзиентный) экземпляр <c>World.GameWorld</c>.</summary>
    private static Node SpawnPlayerFunc(Variant data)
    {
        long peerId = data.AsInt64();
        var player = new Player { Name = peerId.ToString() };
        player.SetMultiplayerAuthority((int)peerId);
        return player;
    }

    // ------------------------------------------------------------------------------------------------ сессии — вход

    public bool IsSessionActive(string workbenchName) => _activeSessionNames.Contains(workbenchName);

    /// <summary><paramref name="constructionPath"/> — null означает Create vehicle (пустая постройка с корневым
    /// блоком, как и в одиночной игре).</summary>
    public void RequestOpenSession(string workbenchName, string? constructionPath)
    {
        string path = constructionPath ?? "";
        if (IsServer) ServerOpenSession(LocalPeerId, workbenchName, path);
        else RpcId(1, nameof(RpcOpenSession), workbenchName, path);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcOpenSession(string workbenchName, string constructionPath) =>
        ServerOpenSession(Multiplayer.GetRemoteSenderId(), workbenchName, constructionPath);

    private void ServerOpenSession(long requesterId, string workbenchName, string constructionPath)
    {
        // Уже идёт сессия на этом верстаке - UI не должен был предложить Create/Open в этом случае (см.
        // World.Ui.WorkbenchMenuUi), но проверяем и здесь: два конкурирующих Create одновременно не должны создать
        // вторую сессию поверх первой.
        if (_sessions.ContainsKey(workbenchName)) return;

        var construction = new Construction(new VoxelGrid());
        if (!string.IsNullOrEmpty(constructionPath) && FileAccess.FileExists(constructionPath))
        {
            ConstructionIO.LoadFromFile(construction, constructionPath, BlockCatalog.Instance);
        }
        else if (BlockCatalog.Instance.TryGetBySlug("block", out var rootDefinition))
        {
            construction.Place(Vector3I.Zero, rootDefinition, rootDefinition.DefaultColor);
        }

        _sessions[workbenchName] = new WorkbenchSession(workbenchName, requesterId, construction);

        SendSessionReady(requesterId, workbenchName, construction);
        BroadcastSessionActive(workbenchName, true);
    }

    private void SendSessionReady(long targetId, string workbenchName, Construction construction)
    {
        string json = ConstructionIO.Serialize(construction);
        if (targetId == LocalPeerId) SessionReadyForMe?.Invoke(workbenchName, json);
        else RpcId(targetId, nameof(RpcSessionReady), workbenchName, json);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcSessionReady(string workbenchName, string constructionJson) =>
        SessionReadyForMe?.Invoke(workbenchName, constructionJson);

    private void BroadcastSessionActive(string workbenchName, bool active)
    {
        if (active) _activeSessionNames.Add(workbenchName); else _activeSessionNames.Remove(workbenchName);
        SessionActiveChanged?.Invoke(workbenchName, active);

        foreach (long peerId in Multiplayer.GetPeers())
        {
            RpcId(peerId, nameof(RpcSessionActiveChanged), workbenchName, active);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcSessionActiveChanged(string workbenchName, bool active)
    {
        if (active) _activeSessionNames.Add(workbenchName); else _activeSessionNames.Remove(workbenchName);
        SessionActiveChanged?.Invoke(workbenchName, active);
    }

    // ------------------------------------------------------------------------------------------------ Join

    public void RequestJoin(string workbenchName)
    {
        if (IsServer) ServerJoin(LocalPeerId, workbenchName);
        else RpcId(1, nameof(RpcJoin), workbenchName);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcJoin(string workbenchName) => ServerJoin(Multiplayer.GetRemoteSenderId(), workbenchName);

    private void ServerJoin(long requesterId, string workbenchName)
    {
        if (!_sessions.TryGetValue(workbenchName, out var session))
        {
            SendJoinRejected(requesterId, workbenchName, "No active session on this workbench.");
            return;
        }

        if (requesterId == session.AdminPeerId || session.Participants.Contains(requesterId))
        {
            // Уже участник (двойной клик/переподключение) - просто отдать текущее состояние ещё раз, не спрашивая
            // админа заново.
            SendJoinAccepted(requesterId, workbenchName, session);
            return;
        }

        SendIncomingJoinRequest(session.AdminPeerId, workbenchName, requesterId);
    }

    private void SendIncomingJoinRequest(long adminId, string workbenchName, long requesterId)
    {
        if (adminId == LocalPeerId) JoinRequestIncoming?.Invoke(workbenchName, requesterId);
        else RpcId(adminId, nameof(RpcIncomingJoinRequest), workbenchName, requesterId);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcIncomingJoinRequest(string workbenchName, long requesterId) =>
        JoinRequestIncoming?.Invoke(workbenchName, requesterId);

    public void RespondToJoin(string workbenchName, long requesterId, bool accepted)
    {
        if (IsServer) ServerRespondToJoin(LocalPeerId, workbenchName, requesterId, accepted);
        else RpcId(1, nameof(RpcRespondToJoin), workbenchName, requesterId, accepted);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRespondToJoin(string workbenchName, long requesterId, bool accepted) =>
        ServerRespondToJoin(Multiplayer.GetRemoteSenderId(), workbenchName, requesterId, accepted);

    private void ServerRespondToJoin(long responderId, string workbenchName, long requesterId, bool accepted)
    {
        // Только админ ЭТОЙ сессии может отвечать на заявки - подмена id отправителем тут не поможет, responderId
        // всегда настоящий (GetRemoteSenderId для удалённых вызовов, LocalPeerId для локального).
        if (!_sessions.TryGetValue(workbenchName, out var session) || session.AdminPeerId != responderId) return;

        if (accepted)
        {
            if (!session.Participants.Contains(requesterId)) session.Participants.Add(requesterId);
            SendJoinAccepted(requesterId, workbenchName, session);
        }
        else
        {
            SendJoinRejected(requesterId, workbenchName, "Admin declined the request.");
        }
    }

    private void SendJoinAccepted(long targetId, string workbenchName, WorkbenchSession session)
    {
        string json = ConstructionIO.Serialize(session.Construction);
        if (targetId == LocalPeerId) JoinAcceptedForMe?.Invoke(workbenchName, json);
        else RpcId(targetId, nameof(RpcJoinAccepted), workbenchName, json);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcJoinAccepted(string workbenchName, string constructionJson) =>
        JoinAcceptedForMe?.Invoke(workbenchName, constructionJson);

    private void SendJoinRejected(long targetId, string workbenchName, string reason)
    {
        if (targetId == LocalPeerId) JoinRejectedForMe?.Invoke(workbenchName, reason);
        else RpcId(targetId, nameof(RpcJoinRejected), workbenchName, reason);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcJoinRejected(string workbenchName, string reason) => JoinRejectedForMe?.Invoke(workbenchName, reason);

    /// <summary>Заявитель передумал ждать (кнопка Cancel на "плашке ожидания", см.
    /// <c>World.Ui.JoinWaitingUi</c>) — сервер просто пересылает админу, чтобы тот убрал у себя попап
    /// (<see cref="JoinCancelled"/>), если он всё ещё показывает именно эту заявку. Ничего не хранит на сервере
    /// (в <see cref="ServerJoin"/> заявка и так не запоминается — попап у админа появляется сразу, без
    /// промежуточного состояния) — просто оповещение "эта заявка больше не актуальна".</summary>
    public void RequestCancelJoin(string workbenchName)
    {
        if (IsServer) ServerCancelJoin(LocalPeerId, workbenchName);
        else RpcId(1, nameof(RpcRequestCancelJoin), workbenchName);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestCancelJoin(string workbenchName) => ServerCancelJoin(Multiplayer.GetRemoteSenderId(), workbenchName);

    private void ServerCancelJoin(long requesterId, string workbenchName)
    {
        if (!_sessions.TryGetValue(workbenchName, out var session)) return;

        if (session.AdminPeerId == LocalPeerId) JoinCancelled?.Invoke(workbenchName, requesterId);
        else RpcId(session.AdminPeerId, nameof(RpcJoinCancelled), workbenchName, requesterId);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcJoinCancelled(string workbenchName, long requesterId) => JoinCancelled?.Invoke(workbenchName, requesterId);

    // ------------------------------------------------------------------------------------------------ закрытие сессии

    /// <summary>Вызывается админом при Exit/Spawn из редактора — сессия ЦЕЛИКОМ закрывается для всех участников
    /// разом (см. class doc, «Не сделано» — раздельного ухода одного не-админ участника без закрытия сессии сейчас
    /// нет: он просто перестаёт слать правки, а сессия остаётся висеть до ухода админа).</summary>
    public void RequestCloseSession(string workbenchName)
    {
        if (IsServer) ServerCloseSession(LocalPeerId, workbenchName);
        else RpcId(1, nameof(RpcCloseSession), workbenchName);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcCloseSession(string workbenchName) => ServerCloseSession(Multiplayer.GetRemoteSenderId(), workbenchName);

    private void ServerCloseSession(long requesterId, string workbenchName)
    {
        if (!_sessions.TryGetValue(workbenchName, out var session) || session.AdminPeerId != requesterId) return;

        _sessions.Remove(workbenchName);
        foreach (long participantId in session.Participants)
        {
            if (participantId == LocalPeerId) SessionClosedForMe?.Invoke(workbenchName);
            else RpcId(participantId, nameof(RpcSessionClosed), workbenchName);
        }

        BroadcastSessionActive(workbenchName, false);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcSessionClosed(string workbenchName) => SessionClosedForMe?.Invoke(workbenchName);

    // ------------------------------------------------------------------------------------------------ правки

    public void RequestEdit(string workbenchName, NetEditKind kind, Vector3I cell, Vector3I size, string blockSlug,
        Color color, Vector3I rotation, Vector3I mirror, int extraInt = 0, bool extraBool = false)
    {
        if (IsServer) ServerApplyEdit(LocalPeerId, workbenchName, kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);
        else RpcId(1, nameof(RpcRequestEdit), workbenchName, (byte)kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestEdit(string workbenchName, byte kind, Vector3I cell, Vector3I size, string blockSlug,
        Color color, Vector3I rotation, Vector3I mirror, int extraInt, bool extraBool) =>
        ServerApplyEdit(Multiplayer.GetRemoteSenderId(), workbenchName, (NetEditKind)kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);

    private void ServerApplyEdit(long requesterId, string workbenchName, NetEditKind kind, Vector3I cell, Vector3I size,
        string blockSlug, Color color, Vector3I rotation, Vector3I mirror, int extraInt, bool extraBool)
    {
        if (!_sessions.TryGetValue(workbenchName, out var session)) return;
        if (requesterId != session.AdminPeerId && !session.Participants.Contains(requesterId)) return;

        // Снэпшот ДО - тот же UndoHistory, что и в одиночном редактировании, только общий на сессию и с автором
        // (requesterId) на каждой записи - см. ServerUndo/ServerRedo про то, зачем.
        var before = session.UndoHistory.Capture(session.Construction);

        // NetEditOps.Apply — тот же код, что применит правку у каждого участника ниже (BroadcastEditApplied) -
        // сервер и клиенты гарантированно трактуют её одинаково. false - отклонено (правило соседства, занятая
        // клетка, регион не найден и т.п.) - НЕ рассылается и НЕ пишется в историю: несогласованные клиенты просто
        // не увидят изменения у себя, что и подразумевалось запросом, который не должен был пройти.
        if (!NetEditOps.Apply(session.Construction, kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool)) return;

        session.UndoHistory.RecordIfChanged(before, session.Construction, requesterId);
        BroadcastEditApplied(session, workbenchName, kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);
    }

    private void BroadcastEditApplied(WorkbenchSession session, string workbenchName, NetEditKind kind, Vector3I cell,
        Vector3I size, string blockSlug, Color color, Vector3I rotation, Vector3I mirror, int extraInt, bool extraBool)
    {
        foreach (long participantId in AllSessionPeers(session))
        {
            if (participantId == LocalPeerId)
            {
                EditApplied?.Invoke(workbenchName, kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);
            }
            else
            {
                RpcId(participantId, nameof(RpcApplyEdit), workbenchName, (byte)kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);
            }
        }
    }

    private static IEnumerable<long> AllSessionPeers(WorkbenchSession session)
    {
        yield return session.AdminPeerId;
        foreach (var participantId in session.Participants)
        {
            if (participantId != session.AdminPeerId) yield return participantId;
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcApplyEdit(string workbenchName, byte kind, Vector3I cell, Vector3I size, string blockSlug,
        Color color, Vector3I rotation, Vector3I mirror, int extraInt, bool extraBool) =>
        EditApplied?.Invoke(workbenchName, (NetEditKind)kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);

    // ------------------------------------------------------------------------------------------------ Undo/Redo

    /// <summary>Откатить последнюю правку СЕССИИ — но только если она моя (см. Docs/05, «Мультиплеер»: простое
    /// правило "отменяемо только самое верхнее по времени действие, и только его автором", без анализа зависимостей
    /// между действиями чужих игроков). Если кто-то другой уже построил что-то после моего последнего действия —
    /// сервер отклонит (<see cref="UndoRedoRejected"/>), а не откатит чужую правку молча.</summary>
    public void RequestUndo(string workbenchName)
    {
        if (IsServer) ServerUndo(LocalPeerId, workbenchName);
        else RpcId(1, nameof(RpcRequestUndo), workbenchName);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestUndo(string workbenchName) => ServerUndo(Multiplayer.GetRemoteSenderId(), workbenchName);

    private void ServerUndo(long requesterId, string workbenchName)
    {
        if (!_sessions.TryGetValue(workbenchName, out var session)) return;
        if (requesterId != session.AdminPeerId && !session.Participants.Contains(requesterId)) return;

        if (!session.UndoHistory.CanUndo)
        {
            SendUndoRedoRejected(requesterId, workbenchName, "Nothing to undo.");
            return;
        }

        if (session.UndoHistory.PeekUndoAuthor != requesterId)
        {
            SendUndoRedoRejected(requesterId, workbenchName, "Someone else already built something since your last action - can't undo it.");
            return;
        }

        session.UndoHistory.Undo(session.Construction, BlockCatalog.Instance);
        BroadcastSessionSync(session, workbenchName);
    }

    public void RequestRedo(string workbenchName)
    {
        if (IsServer) ServerRedo(LocalPeerId, workbenchName);
        else RpcId(1, nameof(RpcRequestRedo), workbenchName);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestRedo(string workbenchName) => ServerRedo(Multiplayer.GetRemoteSenderId(), workbenchName);

    private void ServerRedo(long requesterId, string workbenchName)
    {
        if (!_sessions.TryGetValue(workbenchName, out var session)) return;
        if (requesterId != session.AdminPeerId && !session.Participants.Contains(requesterId)) return;

        if (!session.UndoHistory.CanRedo)
        {
            SendUndoRedoRejected(requesterId, workbenchName, "Nothing to redo.");
            return;
        }

        if (session.UndoHistory.PeekRedoAuthor != requesterId)
        {
            SendUndoRedoRejected(requesterId, workbenchName, "That undone action isn't yours to redo.");
            return;
        }

        session.UndoHistory.Redo(session.Construction, BlockCatalog.Instance);
        BroadcastSessionSync(session, workbenchName);
    }

    /// <summary>Полная ресинхронизация после Undo/Redo — не поштучная правка (см. <see cref="EditApplied"/>), а
    /// снэпшот целиком (<see cref="UndoHistory.Snapshot"/>, включая per-face/region покраску): Undo/Redo снэпшотный,
    /// не операционный (см. <see cref="UndoHistory"/> class doc), поэтому и рассылка результата — тем же снэпшотом,
    /// не набором отдельных правок.</summary>
    private void BroadcastSessionSync(WorkbenchSession session, string workbenchName)
    {
        var snapshot = session.UndoHistory.Capture(session.Construction);
        foreach (long participantId in AllSessionPeers(session))
        {
            if (participantId == LocalPeerId)
            {
                SessionSynced?.Invoke(workbenchName, snapshot);
            }
            else
            {
                RpcId(participantId, nameof(RpcSessionSynced), workbenchName, snapshot.Json, snapshot.Faces, snapshot.Regions);
            }
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcSessionSynced(string workbenchName, string json, string faces, string regions) =>
        SessionSynced?.Invoke(workbenchName, new UndoHistory.Snapshot(json, faces, regions));

    private void SendUndoRedoRejected(long targetId, string workbenchName, string reason)
    {
        if (targetId == LocalPeerId) UndoRedoRejected?.Invoke(workbenchName, reason);
        else RpcId(targetId, nameof(RpcUndoRedoRejected), workbenchName, reason);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcUndoRedoRejected(string workbenchName, string reason) => UndoRedoRejected?.Invoke(workbenchName, reason);
}
