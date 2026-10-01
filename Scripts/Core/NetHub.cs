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
/// <b>Один верстак — несколько одновременных сессий</b> (см. <see cref="WorkbenchSession"/> class doc, изменено по
/// запросу пользователя): раньше на одном физическом верстаке одновременно мог идти только ОДИН Create/Open/Join —
/// остальным Create vehicle был попросту недоступен, пока кто-то другой уже редактировал. Теперь каждый вызов
/// <see cref="RequestOpenSession"/> начинает СВОЮ, независимую сессию с уникальным ключом (не просто имя верстака —
/// см. <see cref="ServerOpenSession"/>), и таких сессий на одном верстаке может быть сколько угодно одновременно.
/// Везде ниже, где раньше параметр назывался <c>workbenchName</c> и служил ключом словаря <see cref="_sessions"/>
/// (правки/Undo/Redo/закрытие/ответ на Join — методы, работающие с УЖЕ ОТКРЫТОЙ сессией), он переименован в
/// <c>sessionKey</c>, чтобы не путать с именем ФИЗИЧЕСКОГО верстака (используется только там, где запрос ещё не
/// привязан ни к какой конкретной сессии — <see cref="RequestOpenSession"/>/<see cref="RequestJoin"/>).
/// <para/>
/// <b>Список сессий верстака — по запросу, не по broadcast</b> (см. <see cref="RequestWorkbenchSessions"/>, изменено
/// по фидбеку пользователя): раньше "на этом верстаке есть активная сессия" рассылалось ОДИН раз в момент
/// открытия/закрытия сессии всем ТОГДА ПОДКЛЮЧЁННЫМ игрокам — игрок, подключившийся ПОЗЖЕ (уже после рассылки),
/// никогда не узнавал о уже идущей сессии, и кнопка Join оставалась выключенной навсегда, хотя Join'иться было к
/// кому. Вместо broadcast+кэш — простой запрос-ответ: <c>World.Ui.WorkbenchMenuUi</c> спрашивает сервер заново
/// КАЖДЫЙ раз при открытии меню верстака, поэтому не бывает устаревшим ни при каком порядке подключения. Тот же
/// запрос даёт список ВСЕХ сессий на верстаке (ключ + id админа каждой) — если их несколько, UI показывает
/// выпадающий список, и <see cref="RequestJoin"/> явно адресует ВЫБРАННУЮ (не "самую новую", как раньше).
/// <para/>
/// <b>Не сделано в этом проходе</b> (см. открытые вопросы в 05): пары логин/имя игрока (участники видны только по
/// numeric peer id — в выпадающем списке Join тоже только "Admin #id"), выделенный сервер без локального игрока
/// (архитектурно не мешает, но не проверялся).
/// </summary>
public partial class NetHub : Node
{
    public const int DefaultPort = 7777;

    public static NetHub Instance { get; private set; } = null!;

    /// <summary>Ключ — не имя верстака, а уникальный ключ сессии (см. class doc и <see cref="ServerOpenSession"/>).</summary>
    private readonly Dictionary<string, WorkbenchSession> _sessions = new();

    /// <summary>Монотонно растущий счётчик — единственный источник уникальности ключа сессии (см.
    /// <see cref="ServerOpenSession"/>).</summary>
    private int _sessionCounter;

    /// <summary>
    /// Сервер-only: кто (peer id) сейчас ждёт ответа на свой Join, и у кого (админа) висит соответствующий попап —
    /// нужно, чтобы при внезапном отключении заявителя (вышел в меню/закрыл игру/потерял соединение) попап у админа
    /// не остался висеть на отключившегося игрока навсегда (см. <see cref="HandlePeerDisconnectedForPendingJoins"/>,
    /// Docs/05-world-and-vehicle-systems.md, «Мультиплеер»). Явная отмена кнопкой Cancel
    /// (<see cref="RequestCancelJoin"/>) тоже снимает запись отсюда — это НЕ дублирующий механизм, а тот же самый
    /// случай "заявка больше не актуальна", только обнаруженный по дисконнекту, а не по явному клику.
    /// </summary>
    private readonly Dictionary<long, (long AdminId, string SessionKey)> _pendingJoinRequests = new();

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

    /// <summary>Ответ на МОЙ <see cref="RequestWorkbenchSessions"/> — все сессии, сейчас открытые на этом верстаке:
    /// имя верстака (эхом — для сверки, что ответ ещё относится к открытому меню), параллельные массивы ключей
    /// сессий и id их админов (одинаковой длины; пусто — ни одной). Запрашивается заново при КАЖДОМ открытии меню
    /// верстака (см. <c>World.Ui.WorkbenchMenuUi</c>/class doc) — драйвит и доступность кнопки Join, и выпадающий
    /// список, если сессий несколько.</summary>
    public event Action<string, string[], long[]>? WorkbenchSessionsForMe;

    /// <summary>Ответ на МОЙ <see cref="RequestOpenSession"/> — пора входить в редактор с этой постройкой.
    /// Параметры: уникальный ключ сессии (пойдёт во все дальнейшие <c>RequestEdit</c>/<c>RequestUndo</c>/...),
    /// имя физического верстака (нужно только чтобы вернуть заспавненную постройку в его зону — не путать с ключом
    /// сессии, см. class doc), сериализованная постройка.</summary>
    public event Action<string, string, string>? SessionReadyForMe;

    /// <summary>Я админ этого верстака, кто-то (peer id) просится присоединиться к МОЕЙ сессии (ключ) — показать
    /// попап принять/отклонить.</summary>
    public event Action<string, long>? JoinRequestIncoming;

    /// <summary>Админ принял мою заявку — пора входить в редактор с этой постройкой. Ключ сессии тут — ровно тот,
    /// что я сам указал в <see cref="RequestJoin"/> (выбран из <see cref="WorkbenchSessionsForMe"/>), поэтому, в
    /// отличие от <see cref="SessionReadyForMe"/>, эхо имени верстака не нужно — я уже знаю его сам
    /// (<c>GameWorld._activeWorkbench</c>, тот же верстак, через который открыл меню).</summary>
    public event Action<string, string>? JoinAcceptedForMe;

    public event Action<string, string>? JoinRejectedForMe;

    /// <summary>Заявитель отменил свою заявку (кнопка Cancel, либо сам отключился от сети — см.
    /// <see cref="HandlePeerDisconnectedForPendingJoins"/>) — я админ и должен убрать попап, если он ещё показывает
    /// ИМЕННО эту заявку (см. <see cref="RequestCancelJoin"/>).</summary>
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
        Multiplayer.PeerDisconnected += HandlePeerDisconnectedForPendingJoins;
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
        _pendingJoinRequests.Clear();

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

    /// <summary>Сервер-only: заявитель (любой peer) отключился от сети, пока ждал ответа на свой Join — убираем
    /// попап у админа (тот же путь, что и явная Cancel, см. <see cref="ServerCancelJoin"/>) и чистим запись, чтобы
    /// она не осталась висеть навсегда. Покрывает ЛЮБУЮ причину отключения (Exit to menu → Solo, закрытие игры,
    /// обрыв связи) — все они одинаково бьют по ENet-соединению, значит одинаково стреляют этим событием.</summary>
    private void HandlePeerDisconnectedForPendingJoins(long peerId)
    {
        if (!IsServer) return;
        if (!_pendingJoinRequests.Remove(peerId, out var pending)) return;

        if (pending.AdminId == LocalPeerId) JoinCancelled?.Invoke(pending.SessionKey, peerId);
        else RpcId(pending.AdminId, nameof(RpcJoinCancelled), pending.SessionKey, peerId);
    }

    // ------------------------------------------------------------------------------------------------ сессии — вход

    /// <summary>Какие сессии сейчас открыты на этом верстаке — спрашивается заново при каждом открытии меню
    /// верстака (см. class doc/<see cref="WorkbenchSessionsForMe"/>), не кэшируется клиентом.</summary>
    public void RequestWorkbenchSessions(string workbenchName)
    {
        if (IsServer) ServerListSessions(LocalPeerId, workbenchName);
        else RpcId(1, nameof(RpcRequestWorkbenchSessions), workbenchName);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestWorkbenchSessions(string workbenchName) =>
        ServerListSessions(Multiplayer.GetRemoteSenderId(), workbenchName);

    private void ServerListSessions(long requesterId, string workbenchName)
    {
        var keys = new List<string>();
        var admins = new List<long>();

        // Сортировка по Ordinal (порядок создания) - детерминированный, стабильный порядок в выпадающем списке,
        // не зависящий от порядка перечисления словаря.
        var matches = new List<(int Ordinal, string Key, WorkbenchSession Session)>();
        foreach (var (key, session) in _sessions)
        {
            if (session.WorkbenchName == workbenchName) matches.Add((session.Ordinal, key, session));
        }

        matches.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        foreach (var (_, key, session) in matches)
        {
            keys.Add(key);
            admins.Add(session.AdminPeerId);
        }

        if (requesterId == LocalPeerId) WorkbenchSessionsForMe?.Invoke(workbenchName, keys.ToArray(), admins.ToArray());
        else RpcId(requesterId, nameof(RpcWorkbenchSessions), workbenchName, keys.ToArray(), admins.ToArray());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcWorkbenchSessions(string workbenchName, string[] sessionKeys, long[] adminPeerIds) =>
        WorkbenchSessionsForMe?.Invoke(workbenchName, sessionKeys, adminPeerIds);

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

    /// <summary>
    /// Раньше здесь была проверка "уже есть сессия на этом верстаке — отказать" (одна сессия на верстак
    /// одновременно, UI вообще не предлагал бы Create в этом случае). Убрано по запросу пользователя: Create vehicle
    /// должен работать независимо от того, кто ещё сейчас редактирует через тот же физический верстак — каждый
    /// вызов начинает СВОЮ, отдельную сессию с уникальным ключом (<paramref name="workbenchName"/> + счётчик), а не
    /// делит её с чужими и не блокируется их существованием. Сколько угодно игроков может одновременно зайти в свой
    /// собственный редактор через один и тот же верстак.
    /// </summary>
    private void ServerOpenSession(long requesterId, string workbenchName, string constructionPath)
    {
        string sessionKey = $"{workbenchName}#{++_sessionCounter}";

        var construction = new Construction(new VoxelGrid());
        if (!string.IsNullOrEmpty(constructionPath) && FileAccess.FileExists(constructionPath))
        {
            ConstructionIO.LoadFromFile(construction, constructionPath, BlockCatalog.Instance);
        }
        else if (BlockCatalog.Instance.TryGetBySlug("block", out var rootDefinition))
        {
            construction.Place(Vector3I.Zero, rootDefinition, rootDefinition.DefaultColor);
        }

        _sessions[sessionKey] = new WorkbenchSession(workbenchName, _sessionCounter, requesterId, construction);

        SendSessionReady(requesterId, sessionKey, workbenchName, construction);
    }

    private void SendSessionReady(long targetId, string sessionKey, string workbenchName, Construction construction)
    {
        string json = ConstructionIO.Serialize(construction);
        if (targetId == LocalPeerId) SessionReadyForMe?.Invoke(sessionKey, workbenchName, json);
        else RpcId(targetId, nameof(RpcSessionReady), sessionKey, workbenchName, json);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcSessionReady(string sessionKey, string workbenchName, string constructionJson) =>
        SessionReadyForMe?.Invoke(sessionKey, workbenchName, constructionJson);

    // ------------------------------------------------------------------------------------------------ Join

    /// <summary><paramref name="sessionKey"/> — конкретная сессия, выбранная игроком из <see cref="WorkbenchSessionsForMe"/>
    /// (если на верстаке их несколько — из выпадающего списка в UI; если одна — выбрана автоматически).</summary>
    public void RequestJoin(string sessionKey)
    {
        if (IsServer) ServerJoin(LocalPeerId, sessionKey);
        else RpcId(1, nameof(RpcJoin), sessionKey);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcJoin(string sessionKey) => ServerJoin(Multiplayer.GetRemoteSenderId(), sessionKey);

    private void ServerJoin(long requesterId, string sessionKey)
    {
        if (!_sessions.TryGetValue(sessionKey, out var session))
        {
            SendJoinRejected(requesterId, sessionKey, "This session is no longer available.");
            return;
        }

        if (requesterId == session.AdminPeerId || session.Participants.Contains(requesterId))
        {
            // Уже участник (двойной клик/переподключение) - просто отдать текущее состояние ещё раз, не спрашивая
            // админа заново.
            SendJoinAccepted(requesterId, sessionKey, session);
            return;
        }

        _pendingJoinRequests[requesterId] = (session.AdminPeerId, sessionKey);
        SendIncomingJoinRequest(session.AdminPeerId, sessionKey, requesterId);
    }

    private void SendIncomingJoinRequest(long adminId, string sessionKey, long requesterId)
    {
        if (adminId == LocalPeerId) JoinRequestIncoming?.Invoke(sessionKey, requesterId);
        else RpcId(adminId, nameof(RpcIncomingJoinRequest), sessionKey, requesterId);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcIncomingJoinRequest(string sessionKey, long requesterId) =>
        JoinRequestIncoming?.Invoke(sessionKey, requesterId);

    public void RespondToJoin(string sessionKey, long requesterId, bool accepted)
    {
        if (IsServer) ServerRespondToJoin(LocalPeerId, sessionKey, requesterId, accepted);
        else RpcId(1, nameof(RpcRespondToJoin), sessionKey, requesterId, accepted);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRespondToJoin(string sessionKey, long requesterId, bool accepted) =>
        ServerRespondToJoin(Multiplayer.GetRemoteSenderId(), sessionKey, requesterId, accepted);

    private void ServerRespondToJoin(long responderId, string sessionKey, long requesterId, bool accepted)
    {
        // Только админ ЭТОЙ сессии может отвечать на заявки - подмена id отправителем тут не поможет, responderId
        // всегда настоящий (GetRemoteSenderId для удалённых вызовов, LocalPeerId для локального).
        if (!_sessions.TryGetValue(sessionKey, out var session) || session.AdminPeerId != responderId) return;

        // Заявка разрешилась (тем или иным способом) - больше не "висит", дисконнект заявителя после этого момента
        // уже ничего не должен отменять (см. HandlePeerDisconnectedForPendingJoins).
        _pendingJoinRequests.Remove(requesterId);

        if (accepted)
        {
            if (!session.Participants.Contains(requesterId)) session.Participants.Add(requesterId);
            SendJoinAccepted(requesterId, sessionKey, session);
        }
        else
        {
            SendJoinRejected(requesterId, sessionKey, "Admin declined the request.");
        }
    }

    private void SendJoinAccepted(long targetId, string sessionKey, WorkbenchSession session)
    {
        string json = ConstructionIO.Serialize(session.Construction);
        if (targetId == LocalPeerId) JoinAcceptedForMe?.Invoke(sessionKey, json);
        else RpcId(targetId, nameof(RpcJoinAccepted), sessionKey, json);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcJoinAccepted(string sessionKey, string constructionJson) =>
        JoinAcceptedForMe?.Invoke(sessionKey, constructionJson);

    private void SendJoinRejected(long targetId, string sessionKey, string reason)
    {
        if (targetId == LocalPeerId) JoinRejectedForMe?.Invoke(sessionKey, reason);
        else RpcId(targetId, nameof(RpcJoinRejected), sessionKey, reason);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcJoinRejected(string sessionKey, string reason) => JoinRejectedForMe?.Invoke(sessionKey, reason);

    /// <summary>Заявитель передумал ждать (кнопка Cancel на "плашке ожидания", см.
    /// <c>World.Ui.JoinWaitingUi</c>) — сервер просто пересылает админу, чтобы тот убрал у себя попап
    /// (<see cref="JoinCancelled"/>), если он всё ещё показывает именно эту заявку. Тот же эффект, что и
    /// <see cref="HandlePeerDisconnectedForPendingJoins"/>, только по явному клику, а не по дисконнекту. Не требует
    /// параметра — у каждого peer'а может висеть не больше одной заявки разом (<see cref="_pendingJoinRequests"/>
    /// ключуется по requesterId).</summary>
    public void RequestCancelJoin()
    {
        if (IsServer) ServerCancelJoin(LocalPeerId);
        else RpcId(1, nameof(RpcRequestCancelJoin));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestCancelJoin() => ServerCancelJoin(Multiplayer.GetRemoteSenderId());

    private void ServerCancelJoin(long requesterId)
    {
        if (!_pendingJoinRequests.Remove(requesterId, out var pending)) return;

        if (pending.AdminId == LocalPeerId) JoinCancelled?.Invoke(pending.SessionKey, requesterId);
        else RpcId(pending.AdminId, nameof(RpcJoinCancelled), pending.SessionKey, requesterId);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcJoinCancelled(string sessionKey, long requesterId) => JoinCancelled?.Invoke(sessionKey, requesterId);

    // ------------------------------------------------------------------------------------------------ закрытие сессии

    /// <summary>Вызывается админом при Exit/Spawn из редактора — сессия ЦЕЛИКОМ закрывается для всех участников
    /// разом (см. class doc, «Не сделано» — раздельного ухода одного не-админ участника без закрытия сессии сейчас
    /// нет: он просто перестаёт слать правки, а сессия остаётся висеть до ухода админа).</summary>
    public void RequestCloseSession(string sessionKey)
    {
        if (IsServer) ServerCloseSession(LocalPeerId, sessionKey);
        else RpcId(1, nameof(RpcCloseSession), sessionKey);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcCloseSession(string sessionKey) => ServerCloseSession(Multiplayer.GetRemoteSenderId(), sessionKey);

    private void ServerCloseSession(long requesterId, string sessionKey)
    {
        if (!_sessions.TryGetValue(sessionKey, out var session) || session.AdminPeerId != requesterId) return;

        _sessions.Remove(sessionKey);
        foreach (long participantId in session.Participants)
        {
            if (participantId == LocalPeerId) SessionClosedForMe?.Invoke(sessionKey);
            else RpcId(participantId, nameof(RpcSessionClosed), sessionKey);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcSessionClosed(string sessionKey) => SessionClosedForMe?.Invoke(sessionKey);

    // ------------------------------------------------------------------------------------------------ правки

    public void RequestEdit(string sessionKey, NetEditKind kind, Vector3I cell, Vector3I size, string blockSlug,
        Color color, Vector3I rotation, Vector3I mirror, int extraInt = 0, bool extraBool = false)
    {
        if (IsServer) ServerApplyEdit(LocalPeerId, sessionKey, kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);
        else RpcId(1, nameof(RpcRequestEdit), sessionKey, (byte)kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestEdit(string sessionKey, byte kind, Vector3I cell, Vector3I size, string blockSlug,
        Color color, Vector3I rotation, Vector3I mirror, int extraInt, bool extraBool) =>
        ServerApplyEdit(Multiplayer.GetRemoteSenderId(), sessionKey, (NetEditKind)kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);

    private void ServerApplyEdit(long requesterId, string sessionKey, NetEditKind kind, Vector3I cell, Vector3I size,
        string blockSlug, Color color, Vector3I rotation, Vector3I mirror, int extraInt, bool extraBool)
    {
        if (!_sessions.TryGetValue(sessionKey, out var session)) return;
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
        BroadcastEditApplied(session, sessionKey, kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);
    }

    private void BroadcastEditApplied(WorkbenchSession session, string sessionKey, NetEditKind kind, Vector3I cell,
        Vector3I size, string blockSlug, Color color, Vector3I rotation, Vector3I mirror, int extraInt, bool extraBool)
    {
        foreach (long participantId in AllSessionPeers(session))
        {
            if (participantId == LocalPeerId)
            {
                EditApplied?.Invoke(sessionKey, kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);
            }
            else
            {
                RpcId(participantId, nameof(RpcApplyEdit), sessionKey, (byte)kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);
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
    private void RpcApplyEdit(string sessionKey, byte kind, Vector3I cell, Vector3I size, string blockSlug,
        Color color, Vector3I rotation, Vector3I mirror, int extraInt, bool extraBool) =>
        EditApplied?.Invoke(sessionKey, (NetEditKind)kind, cell, size, blockSlug, color, rotation, mirror, extraInt, extraBool);

    // ------------------------------------------------------------------------------------------------ Undo/Redo

    /// <summary>Откатить последнюю правку СЕССИИ — но только если она моя (см. Docs/05, «Мультиплеер»: простое
    /// правило "отменяемо только самое верхнее по времени действие, и только его автором", без анализа зависимостей
    /// между действиями чужих игроков). Если кто-то другой уже построил что-то после моего последнего действия —
    /// сервер отклонит (<see cref="UndoRedoRejected"/>), а не откатит чужую правку молча.</summary>
    public void RequestUndo(string sessionKey)
    {
        if (IsServer) ServerUndo(LocalPeerId, sessionKey);
        else RpcId(1, nameof(RpcRequestUndo), sessionKey);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestUndo(string sessionKey) => ServerUndo(Multiplayer.GetRemoteSenderId(), sessionKey);

    private void ServerUndo(long requesterId, string sessionKey)
    {
        if (!_sessions.TryGetValue(sessionKey, out var session)) return;
        if (requesterId != session.AdminPeerId && !session.Participants.Contains(requesterId)) return;

        if (!session.UndoHistory.CanUndo)
        {
            SendUndoRedoRejected(requesterId, sessionKey, "Nothing to undo.");
            return;
        }

        if (session.UndoHistory.PeekUndoAuthor != requesterId)
        {
            SendUndoRedoRejected(requesterId, sessionKey, "Someone else already built something since your last action - can't undo it.");
            return;
        }

        session.UndoHistory.Undo(session.Construction, BlockCatalog.Instance);
        BroadcastSessionSync(session, sessionKey);
    }

    public void RequestRedo(string sessionKey)
    {
        if (IsServer) ServerRedo(LocalPeerId, sessionKey);
        else RpcId(1, nameof(RpcRequestRedo), sessionKey);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestRedo(string sessionKey) => ServerRedo(Multiplayer.GetRemoteSenderId(), sessionKey);

    private void ServerRedo(long requesterId, string sessionKey)
    {
        if (!_sessions.TryGetValue(sessionKey, out var session)) return;
        if (requesterId != session.AdminPeerId && !session.Participants.Contains(requesterId)) return;

        if (!session.UndoHistory.CanRedo)
        {
            SendUndoRedoRejected(requesterId, sessionKey, "Nothing to redo.");
            return;
        }

        if (session.UndoHistory.PeekRedoAuthor != requesterId)
        {
            SendUndoRedoRejected(requesterId, sessionKey, "That undone action isn't yours to redo.");
            return;
        }

        session.UndoHistory.Redo(session.Construction, BlockCatalog.Instance);
        BroadcastSessionSync(session, sessionKey);
    }

    /// <summary>Полная ресинхронизация после Undo/Redo — не поштучная правка (см. <see cref="EditApplied"/>), а
    /// снэпшот целиком (<see cref="UndoHistory.Snapshot"/>, включая per-face/region покраску): Undo/Redo снэпшотный,
    /// не операционный (см. <see cref="UndoHistory"/> class doc), поэтому и рассылка результата — тем же снэпшотом,
    /// не набором отдельных правок.</summary>
    private void BroadcastSessionSync(WorkbenchSession session, string sessionKey)
    {
        var snapshot = session.UndoHistory.Capture(session.Construction);
        foreach (long participantId in AllSessionPeers(session))
        {
            if (participantId == LocalPeerId)
            {
                SessionSynced?.Invoke(sessionKey, snapshot);
            }
            else
            {
                RpcId(participantId, nameof(RpcSessionSynced), sessionKey, snapshot.Json, snapshot.Faces, snapshot.Regions);
            }
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcSessionSynced(string sessionKey, string json, string faces, string regions) =>
        SessionSynced?.Invoke(sessionKey, new UndoHistory.Snapshot(json, faces, regions));

    private void SendUndoRedoRejected(long targetId, string sessionKey, string reason)
    {
        if (targetId == LocalPeerId) UndoRedoRejected?.Invoke(sessionKey, reason);
        else RpcId(targetId, nameof(RpcUndoRedoRejected), sessionKey, reason);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcUndoRedoRejected(string sessionKey, string reason) => UndoRedoRejected?.Invoke(sessionKey, reason);
}
