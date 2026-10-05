using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor.Ui;
using SandboxPolyGame.World.Ui;

namespace SandboxPolyGame.World;

/// <summary>
/// Корневой узел открытого мира (вне редактора построек) — по аналогии с <see cref="Editor.BuildEditor"/> всё
/// создаётся кодом в <see cref="_Ready"/>: окружение, террейн, верстаки+зоны спавна, игрок(и). Сейчас это прототип
/// (см. Docs/05-world-and-vehicle-systems.md, «Текущее состояние») — одна плоская плитка-заглушка, три пары
/// верстак+зона спавна разного размера; настоящий террейн и вода сюда ещё не подключены.
/// <para/>
/// <b>Мультиплеер</b> (см. Docs/05, «Мультиплеер», <see cref="NetHub"/>): при первом запуске процесса (не при
/// каждом возврате из редактора) показывает <see cref="NetworkLobbyUi"/> — Solo/Host/Join. Если игра сетевая, по
/// одному <see cref="Player"/> на каждого подключённого — контейнер+спаунер живут на <see cref="NetHub"/>
/// (автозагрузка, не здесь — см. <see cref="NetHub.EnsurePlayerReplication"/> class doc про то, почему: раньше жили
/// тут же, и когда СЕРВЕР входил в свой редактор, его же пересборка сцены попутно уничтожала игроков ВСЕХ
/// подключённых), <see cref="SetupNetworkedPlayers"/> лишь находит/подключается к своему; иначе (одиночная игра,
/// включая самотесты — см. ниже) ровно тот же код, что и раньше, без единого сетевого вызова
/// (<see cref="SetupSoloPlayer"/>).
/// <para/>
/// <b>Дев-харнесс</b>: если в командной строке есть аргументы после <c>--</c> (<c>--selftest</c>,
/// <c>--demo=...</c> и т.п.), весь <see cref="Dev.DevHarness"/> по-прежнему полностью завязан на
/// <see cref="Editor.BuildEditor"/> как корень сцены — вместо обычного мира сразу передаём управление редактору
/// (<see cref="GetTree"/>().<see cref="SceneTree.ChangeSceneToFile"/>), не трогая ни строчки в самом харнессе.
/// Обычный запуск без аргументов (<c>run.bat</c>) видит пустой список и грузит мир как обычно. Проверка
/// <c>GetTree().CurrentScene == this</c> обязательна (не просто "есть дев-аргументы") — иначе тестовый
/// <see cref="GameWorld"/>, который самотесты инстанцируют как обычного ребёнка ради проверки (не для того, чтобы
/// ещё раз перехватить управление у дев-харнесса — командная строка процесса одна на всех узлов), сам себя
/// зациклил бы (см. WORKLOG); та же проверка используется, чтобы НЕ показывать лобби такому тестовому инстансу —
/// он всегда получает одиночную игру, как и раньше (283 самотеста, существовавшие до мультиплеера, ничего не знают
/// о сети и не должны были заметить её появление).
/// </summary>
public partial class GameWorld : Node3D
{
	public const float TileSize = 60f;

	/// <summary>Максимальная дистанция наведения (взгляд по центру экрана) для открытия верстака (`E`) и возврата
	/// постройки (`R`) — см. <see cref="RaycastFromCamera"/>.</summary>
	private const float InteractDistance = 5f;

	private Player _player = null!;
	private TerrainTile _terrain = null!;
	private readonly List<Workbench> _workbenches = new();
	private readonly List<VehicleBody> _vehicles = new();
	private WorkbenchMenuUi _workbenchMenu = null!;
	private JoinRequestPopupUi _joinPopup = null!;
	private JoinWaitingUi _joinWaitingUi = null!;
	private PauseMenuUi _pauseMenu = null!;
	private Label _prompt = null!;
	private Label _debugLabel = null!;

	// Какой верстак открыл текущее меню - нужен при Create/Open/Join, чтобы знать, в чью зону спавна вернётся
	// постройка (см. EditorHandoff.PendingSpawnWorkbenchName), и на какой верстак ссылается ответ сервера, который
	// придёт позже (см. RequestEnterWorkbench/RequestJoin - оба асинхронные в сетевой игре).
	private Workbench? _activeWorkbench;
	private string _pendingSessionWorkbenchName = "";

	// Ключ сессии, выбранной (авто- или из выпадающего списка) при нажатии Join to workbench - см.
	// RequestJoinActiveWorkbench/OnJoinAcceptedForMe/OnJoinRejectedForMe. В отличие от _pendingSessionWorkbenchName
	// (имя физического верстака, нужно для Create/Open) этот ключ уже однозначно указывает на сессию - сверять
	// ответ сервера с ним можно напрямую, без второй координаты.
	private string _pendingJoinSessionKey = "";

	// Верстак, для которого пришла входящая заявка на Join (см. OnJoinRequestIncoming) - JoinRequestPopupUi не несёт
	// это имя сама, см. её class doc.
	private string _incomingJoinWorkbenchName = "";

	private bool _debugCollisionView;
	private bool _debugPowered;
	private bool _debugButtonsPressed;

	// Пока true, _Process/_Input не должны трогать остальные поля - при передаче управления дев-харнессу (см. ниже)
	// ни террейн/верстаки/игрок, ни UI не строятся вообще, а смена сцены (см. GoToBuildEditor) завершается не раньше
	// конца кадра - этот узел успевает получить ещё как минимум один _Process/_Input с пустыми полями.
	private bool _handingOff;

	public Player Player => _player;
	public TerrainTile Terrain => _terrain;
	public IReadOnlyList<Workbench> Workbenches => _workbenches;
	public IReadOnlyList<VehicleBody> Vehicles => _vehicles;
	public WorkbenchMenuUi WorkbenchMenu => _workbenchMenu;

	/// <summary>Для самотестов (см. <c>Dev.SelfTest</c>) — проверить, что эта плашка (в отличие от остальных
	/// модальных окон) не замораживает движение персонажа, см. <see cref="MovementBlockingModalOpen"/>.</summary>
	public JoinWaitingUi JoinWaitingUi => _joinWaitingUi;

	/// <summary>Открыто ЛЮБОЕ модальное окно поверх мира — E/R и подсказка внизу экрана должны молчать, пока это
	/// так (единая точка, вместо перечисления всех окон в каждом месте по отдельности). Движение (WASD) НЕ входит
	/// сюда — см. <see cref="_Process"/>: плашка ожидания Join (<see cref="_joinWaitingUi"/>) сознательно не должна
	/// замораживать игрока (баг, найденный пользователем — подключающийся игрок не мог ходить, пока ждал ответа
	/// админа), только показ курсора (нужен, чтобы было чем нажать Cancel — см. <see cref="RequestJoinActiveWorkbench"/>).</summary>
	private bool AnyModalOpen => _workbenchMenu.IsOpen || _joinPopup.IsOpen || _joinWaitingUi.IsOpen || _pauseMenu.IsOpen;

	/// <summary>Подмножество <see cref="AnyModalOpen"/>, которое ДОЛЖНО останавливать движение персонажа — всё,
	/// кроме плашки ожидания Join (см. её doc выше).</summary>
	private bool MovementBlockingModalOpen => _workbenchMenu.IsOpen || _joinPopup.IsOpen || _pauseMenu.IsOpen;

	public override void _Ready()
	{
		if (GetTree().CurrentScene == this && OS.GetCmdlineUserArgs().Length > 0)
		{
			_handingOff = true;
			// --blockeditor - отдельный инструмент (Dev.BlockPrefabEditor), не часть обычного BuildEditor (другая
			// камера/UI/цель), поэтому у него своя сцена вместо общего для всех остальных дев-аргументов перехода
			// на BuildEditor.tscn (см. class doc).
			bool blockEditor = Array.Exists(OS.GetCmdlineUserArgs(), a => a == "--blockeditor" || a.StartsWith("--blockeditor="));
			if (blockEditor) Callable.From(() => GetTree().ChangeSceneToFile("res://Scenes/BlockPrefabEditor.tscn")).CallDeferred();
			else GoToBuildEditor();
			return;
		}

		EnvironmentBuilder.BuildFlatSkyAndSun(this, Color.FromHtml("#6682FF"));

		_terrain = new TerrainTile { Name = "Terrain", Size = TileSize };
		AddChild(_terrain);

		// Большая/средняя/маленькая (2x/4x меньше большой по стороне) - см. задачу. Верстак каждой зоны стоит
		// СНАРУЖИ неё (за краем + небольшой отступ), чтобы заспавненная постройка, заполнившая всю зону, не могла
		// наложиться на сам верстак.
		CreateWorkbenchZone("WorkbenchLarge", 16f, new Vector3(-20f, 0f, 0f), new Vector3(1f, 0f, 0f));
		CreateWorkbenchZone("WorkbenchMedium", 8f, new Vector3(0f, 0f, -20f), new Vector3(0f, 0f, 1f));
		CreateWorkbenchZone("WorkbenchSmall", 4f, new Vector3(18f, 0f, 14f), new Vector3(-1f, 0f, -1f));

		BuildUi();

		// Лобби (Solo/Host/Join) - только настоящий загруженный мир (не тестовый инстанс самотестов, см. class doc)
		// и только один раз за запуск процесса (NetHub.LobbyResolved живёт на автозагрузке, переживает переходы
		// сцен - см. NetHub).
		if (GetTree().CurrentScene == this && !NetHub.Instance.LobbyResolved)
		{
			var lobby = new NetworkLobbyUi(_uiRoot);
			lobby.Resolved += ContinueBoot;
		}
		else
		{
			ContinueBoot();
		}
	}

	private Control _uiRoot = null!;

	/// <summary>Всё, что раньше шло прямо в <see cref="_Ready"/> сразу после мира/UI — вынесено отдельно, потому что
	/// теперь может случиться не сразу, а по завершении <see cref="NetworkLobbyUi"/> (см. <see cref="_Ready"/>).</summary>
	private void ContinueBoot()
	{
		SubscribeNetworking();

		if (NetHub.Instance.IsNetworked) SetupNetworkedPlayers();
		else SetupSoloPlayer();

		// Spawn в редакторе (см. Editor.Ui.EditorUi.Spawn) - постройка появляется в зоне ИМЕННО ТОГО верстака,
		// через который вошли (EditorHandoff.PendingSpawnWorkbenchName), не обязательно первого. Once-off: оба поля
		// сбрасываются сразу после использования, см. EditorHandoff class doc.
		if (EditorHandoff.PendingSpawnJson is { } spawnJson)
		{
			string? workbenchName = EditorHandoff.PendingSpawnWorkbenchName;
			EditorHandoff.PendingSpawnJson = null;
			EditorHandoff.PendingSpawnWorkbenchName = null;

			var sourceWorkbench = _workbenches.Find(w => w.Name == (workbenchName ?? "")) ?? _workbenches[0];
			var vehicle = VehicleSpawner.Spawn(this, spawnJson,
				sourceWorkbench.SpawnArea.GlobalPosition + new Vector3(0f, 0.1f, 0f), sourceWorkbench);
			_vehicles.Add(vehicle);
			ApplyDebugFlags(vehicle);
		}
	}

	private void SetupSoloPlayer()
	{
		_player = new Player { Name = "Player" };
		AddChild(_player);
		ApplyPendingPose();
	}

	// ------------------------------------------------------------------------------------------------ мультиплеер: игроки

	/// <summary>
	/// Контейнер+спаунер игроков живут на <see cref="NetHub"/> (переживает СОБСТВЕННЫЙ переход этого пира
	/// мир→редактор→мир — см. её <see cref="NetHub.EnsurePlayerReplication"/> class doc про баг, который это
	/// чинит), не здесь — <see cref="GameWorld"/> лишь подключается к уже существующему или инициирует его создание.
	/// Первый раз за процесс (<c>alreadyExisted == false</c>) — свой игрок либо уже пришёл синхронно (сервер, см.
	/// <see cref="NetHub.PlayerSpawned"/>), либо придёт по сети чуть позже (клиент, см. <see cref="OnPlayerSpawned"/>) —
	/// поэтому <see cref="_player"/> в сетевой игре доступен не обязательно с первого кадра (см. null-проверки в
	/// <see cref="_Process"/>/<see cref="RaycastFromCamera"/>). Повторно за процесс (этот же пир вернулся в мир из
	/// редактора) — свой узел уже существует и просто переиспользуется, никакого нового спавна/сигнала не будет.
	/// </summary>
	private void SetupNetworkedPlayers()
	{
		NetHub.Instance.PlayerSpawned += OnPlayerSpawned;
		bool alreadyExisted = NetHub.Instance.EnsurePlayerReplication();

		// Раз игроки живут на NetHub (сиблинг текущей сцены), их снова нужно ПОКАЗАТЬ - на время в редакторе
		// (см. EnterEditor) они были спрятаны, иначе чужие персонажи маячили бы посреди сцены редактора (см.
		// WORKLOG про баг "в редакторе отображается персонаж хоста").
		NetHub.Instance.PlayersRoot!.Visible = true;

		if (!alreadyExisted) return; // свежий контейнер - HandleSpawnedPlayer придёт через PlayerSpawned (см. выше)

		var existing = NetHub.Instance.PlayersRoot?.GetNodeOrNull<Player>(NetHub.Instance.LocalPeerId.ToString());
		if (existing != null) HandleSpawnedPlayer(existing);
	}

	/// <summary>Срабатывает и для своего локального спавна (сервер уведомляет себя сам, см.
	/// <see cref="NetHub.PlayerSpawned"/> class doc), и по сети (клиент, чей игрок пришёл по репликации).</summary>
	private void OnPlayerSpawned(Player player) => HandleSpawnedPlayer(player);

	/// <summary>Общая точка "это мой локальный игрок?" — чужой (не мой) просто no-op, узел уже полностью настроил
	/// себя сам в своём <see cref="Player._Ready"/> (видимость тела и т.п.), больше от <see cref="GameWorld"/>
	/// ничего не требуется. <see cref="Player.SetActive"/>(true) — на случай, если это переиспользованный узел,
	/// который был приостановлен перед уходом в редактор (см. <see cref="EnterEditor"/>) — для свежего спавна
	/// не более чем no-op (узел и так активен по умолчанию). Камера переактивируется явно — если это возврат из
	/// редактора, узел не пересоздавался (значит и <see cref="Player._EnterTree"/>, где камера обычно становится
	/// текущей, не запускался заново), а редакторская `FlyCamera` за это время успела стать текущей и уже уехала
	/// вместе со своей сценой — без этого вернувшийся в мир игрок увидел бы пустой вьюпорт без активной камеры.
	/// Мышь по той же причине захватывается явно, не только в <see cref="Player._EnterTree"/> — баг, найденный
	/// пользователем: и у хоста, и у клиента вращение камеры переставало работать после выхода с верстака, потому
	/// что в редакторе мышь обычно видима (СКМ только на время поворота — см. <see cref="Editor.FlyCamera"/>), а
	/// <see cref="Player._Input"/> крутит камеру, только пока <see cref="Input.MouseMode"/> == Captured; для
	/// переиспользованного узла (мультиплеер) ничего не возвращало его в Captured само — "чинило" это только
	/// открытие/закрытие меню паузы (Esc), которое попутно и выставляет Captured.</summary>
	private void HandleSpawnedPlayer(Player player)
	{
		if (!player.IsMultiplayerAuthority()) return;

		_player = player;
		player.SetActive(true);
		player.Camera.Current = true;
		Input.MouseMode = Input.MouseModeEnum.Captured;
		ApplyPendingPose();
	}

	private void ApplyPendingPose()
	{
		if (EditorHandoff.PendingPlayerPosition is { } savedPosition)
		{
			// Возврат из редактора (Exit или Spawn) - игрок остаётся там же, откуда вошёл, а не на точке спавна
			// мира по умолчанию; неуязвимость - см. Player.Invulnerable class doc.
			_player.SetPose(savedPosition, EditorHandoff.PendingPlayerYaw);
			_player.Invulnerable = true;
			EditorHandoff.PendingPlayerPosition = null;
		}
		else
		{
			_player.Position = new Vector3(0f, 1f, 0f);
		}
	}

	private void CreateWorkbenchZone(string name, float areaSize, Vector3 areaCenter, Vector3 workbenchDirection)
	{
		var area = new SpawnArea { Name = name + "Area", Size = areaSize, Position = areaCenter };
		AddChild(area);

		const float margin = 2f;
		var workbenchPosition = areaCenter + workbenchDirection.Normalized() * (areaSize / 2f + margin);
		var workbench = new Workbench { Name = name, Position = workbenchPosition, SpawnArea = area };
		AddChild(workbench);
		_workbenches.Add(workbench);
	}

	private void BuildUi()
	{
		var layer = new CanvasLayer { Layer = 10, Name = "Ui" };
		AddChild(layer);

		_uiRoot = new Control { Name = "UiRoot", MouseFilter = Control.MouseFilterEnum.Ignore };
		_uiRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(_uiRoot);

		_prompt = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
		_prompt.AddThemeFontSizeOverride("font_size", 18);
		_prompt.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
		_prompt.Position = new Vector2(-160, -80);
		_prompt.Size = new Vector2(320, 24);
		_uiRoot.AddChild(_prompt);

		// Зачаток дебаг-меню (см. задачу) - пока одна строка-индикатор, включается/выключается вместе с F1.
		_debugLabel = new Label { Text = "", MouseFilter = Control.MouseFilterEnum.Ignore };
		_debugLabel.AddThemeFontSizeOverride("font_size", 14);
		_debugLabel.AddThemeColorOverride("font_color", new Color(1f, 0.8f, 0.2f));
		_debugLabel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
		_debugLabel.Position = new Vector2(-260, 12);
		_debugLabel.Size = new Vector2(248, 72);
		_debugLabel.HorizontalAlignment = HorizontalAlignment.Right;
		_uiRoot.AddChild(_debugLabel);

		_workbenchMenu = new WorkbenchMenuUi(_uiRoot);
		_workbenchMenu.CreateVehicleRequested += () => RequestEnterWorkbench(null);
		_workbenchMenu.OpenConstructionRequested += path => RequestEnterWorkbench(path);
		_workbenchMenu.SpawnConstructionRequested += SpawnFromWorkbenchMenu;
		_workbenchMenu.JoinRequested += RequestJoinActiveWorkbench;

		_joinPopup = new JoinRequestPopupUi(_uiRoot);
		_joinPopup.Responded += (requesterId, accepted) =>
		{
			NetHub.Instance.RespondToJoin(_incomingJoinWorkbenchName, requesterId, accepted);
			Input.MouseMode = Input.MouseModeEnum.Captured;
		};

		_joinWaitingUi = new JoinWaitingUi(_uiRoot);
		_joinWaitingUi.CancelRequested += () =>
		{
			NetHub.Instance.RequestCancelJoin();
			_joinWaitingUi.Hide();
			Input.MouseMode = Input.MouseModeEnum.Captured;
		};

		_pauseMenu = new PauseMenuUi(_uiRoot);
		_pauseMenu.ResumeRequested += () =>
		{
			_pauseMenu.Close();
			Input.MouseMode = Input.MouseModeEnum.Captured;
		};
		_pauseMenu.ExitToMenuRequested += ExitToMainMenu;
		_pauseMenu.ExitToDesktopRequested += () => GetTree().Quit();
	}

	/// <summary>Разрывает сетевое соединение (если оно есть) и перезагружает мир — раз <see cref="NetHub.LobbyResolved"/>
	/// сбрасывается тоже, свежий <see cref="GameWorld"/> снова покажет <see cref="NetworkLobbyUi"/>. Тот же путь и
	/// для "Exit to menu" из <see cref="PauseMenuUi"/>, и для клиента, у которого внезапно пропал сервер (см.
	/// <see cref="OnServerDisconnected"/>) — оба случая означают одно и то же: "текущая сетевая сессия кончилась,
	/// начинаем заново с выбора Solo/Host/Join".</summary>
	private void ExitToMainMenu()
	{
		if (NetHub.Instance.IsNetworked) NetHub.Instance.Disconnect();
		NetHub.Instance.LobbyResolved = false;
		_handingOff = true;
		Callable.From(() => GetTree().ChangeSceneToFile("res://Scenes/World.tscn")).CallDeferred();
	}

	/// <summary>Я клиент, и сервер (хост) только что пропал — вместо зависшей/сломанной сессии сразу возвращаемся
	/// к лобби, тем же путём, что и ручной Exit to menu.</summary>
	private void OnServerDisconnected() => ExitToMainMenu();

	/// <summary>Create vehicle/Open — в одиночной игре сразу переходит в редактор, как и раньше; в сетевой сначала
	/// просит сервер открыть сессию (<see cref="NetHub.RequestOpenSession"/>) и ждёт
	/// <see cref="NetHub.SessionReadyForMe"/> (см. <see cref="OnSessionReadyForMe"/>) — переход в редактор случится
	/// уже оттуда.</summary>
	private void RequestEnterWorkbench(string? constructionPath)
	{
		if (_activeWorkbench == null) return;

		if (NetHub.Instance.IsNetworked)
		{
			_pendingSessionWorkbenchName = _activeWorkbench.Name;
			NetHub.Instance.RequestOpenSession(_activeWorkbench.Name, constructionPath);
			_workbenchMenu.Close();
			Input.MouseMode = Input.MouseModeEnum.Captured;
		}
		else
		{
			EditorHandoff.PendingConstructionPath = constructionPath;
			EditorHandoff.NetworkedWorkbenchName = null;
			EditorHandoff.IsSessionAdmin = false;
			EnterEditorFromWorkbench();
		}
	}

	/// <summary><paramref name="sessionKey"/> — какую именно сессию выбрал игрок в <see cref="WorkbenchMenuUi"/>
	/// (одна на верстаке — выбрана автоматически; несколько — из выпадающего списка, см.
	/// <see cref="WorkbenchMenuUi.SetJoinableSessions"/>).</summary>
	private void RequestJoinActiveWorkbench(string sessionKey)
	{
		if (_activeWorkbench == null) return;

		_pendingJoinSessionKey = sessionKey;
		NetHub.Instance.RequestJoin(sessionKey);
		_workbenchMenu.Close();
		_joinWaitingUi.Show();
		// Мышь остаётся видимой (не Captured) - иначе по кнопке Cancel на плашке нечем было бы кликнуть. Ответ
		// (JoinAcceptedForMe/JoinRejectedForMe) или сама отмена (JoinWaitingUi.CancelRequested) придёт асинхронно,
		// см. подписки ниже - оба прячут плашку и возвращают Captured.
	}

	// ------------------------------------------------------------------------------------------------ мультиплеер: NetHub

	private void SubscribeNetworking()
	{
		NetHub.Instance.WorkbenchSessionsForMe += OnWorkbenchSessionsForMe;
		NetHub.Instance.SessionReadyForMe += OnSessionReadyForMe;
		NetHub.Instance.JoinRequestIncoming += OnJoinRequestIncoming;
		NetHub.Instance.JoinAcceptedForMe += OnJoinAcceptedForMe;
		NetHub.Instance.JoinRejectedForMe += OnJoinRejectedForMe;
		NetHub.Instance.JoinCancelled += OnJoinCancelled;
		NetHub.Instance.ServerDisconnected += OnServerDisconnected;
	}

	private void UnsubscribeNetworking()
	{
		NetHub.Instance.WorkbenchSessionsForMe -= OnWorkbenchSessionsForMe;
		NetHub.Instance.SessionReadyForMe -= OnSessionReadyForMe;
		NetHub.Instance.JoinRequestIncoming -= OnJoinRequestIncoming;
		NetHub.Instance.JoinAcceptedForMe -= OnJoinAcceptedForMe;
		NetHub.Instance.JoinRejectedForMe -= OnJoinRejectedForMe;
		NetHub.Instance.JoinCancelled -= OnJoinCancelled;
		NetHub.Instance.ServerDisconnected -= OnServerDisconnected;
		NetHub.Instance.PlayerSpawned -= OnPlayerSpawned;
	}

	/// <summary>Ответ на запрос, отправленный при открытии меню верстака (см. <c>case Key.E</c> в <see cref="_Input"/>)
	/// — применяем, только если меню всё ещё открыто и всё ещё про ТОТ ЖЕ верстак (игрок мог успеть закрыть меню
	/// или открыть другое, пока ответ летел по сети).</summary>
	private void OnWorkbenchSessionsForMe(string workbenchName, string[] sessionKeys, long[] adminPeerIds)
	{
		if (!_workbenchMenu.IsOpen || _workbenchMenu.WorkbenchName != workbenchName) return;
		_workbenchMenu.SetJoinableSessions(sessionKeys, adminPeerIds);
	}

	/// <summary><paramref name="sessionKey"/> — уникальный ключ ИМЕННО МОЕЙ новой сессии (несколько игроков могут
	/// одновременно открыть свои на этом же верстаке, см. <see cref="NetHub"/> class doc) — идёт в
	/// <see cref="EditorHandoff.NetworkedWorkbenchName"/>, им редактор будет адресовать все дальнейшие правки/Undo.
	/// <paramref name="workbenchName"/> — имя физического верстака (то, что я и запрашивал), нужно только чтобы
	/// вернуть постройку в его зону спавна — см. <see cref="EditorHandoff.PendingSpawnWorkbenchName"/>.</summary>
	private void OnSessionReadyForMe(string sessionKey, string workbenchName, string constructionJson)
	{
		if (workbenchName != _pendingSessionWorkbenchName) return;

		EditorHandoff.PendingConstructionPath = null;
		EditorHandoff.NetworkedWorkbenchName = sessionKey;
		EditorHandoff.PendingNetworkedConstructionJson = constructionJson;
		EditorHandoff.IsSessionAdmin = true;
		EditorHandoff.PendingSpawnWorkbenchName = workbenchName;
		EditorHandoff.PendingPlayerPosition = _player.GlobalPosition;
		EditorHandoff.PendingPlayerYaw = _player.Yaw;
		EnterEditor();
	}

	/// <summary>Приняли мою заявку на <paramref name="sessionKey"/> (ровно та, что я выбрал в
	/// <see cref="RequestJoinActiveWorkbench"/> — сравниваем с ней напрямую, workbenchName сервер тут уже не
	/// присылает, он мне и не нужен: <see cref="_activeWorkbench"/> уже указывает на тот же физический верстак,
	/// через который я и открывал меню Join).</summary>
	private void OnJoinAcceptedForMe(string sessionKey, string constructionJson)
	{
		if (sessionKey != _pendingJoinSessionKey) return;

		_joinWaitingUi.Hide();
		EditorHandoff.PendingConstructionPath = null;
		EditorHandoff.NetworkedWorkbenchName = sessionKey;
		EditorHandoff.PendingNetworkedConstructionJson = constructionJson;
		EditorHandoff.IsSessionAdmin = false;
		EditorHandoff.PendingSpawnWorkbenchName = _activeWorkbench?.Name;
		EditorHandoff.PendingPlayerPosition = _player.GlobalPosition;
		EditorHandoff.PendingPlayerYaw = _player.Yaw;
		EnterEditor();
	}

	private void OnJoinRejectedForMe(string sessionKey, string reason)
	{
		if (sessionKey != _pendingJoinSessionKey) return;
		_joinWaitingUi.Hide();
		Input.MouseMode = Input.MouseModeEnum.Captured;
		_prompt.Text = $"Join request declined: {reason}";
	}

	/// <summary>Я админ сессии на этом верстаке (см. <see cref="EditorHandoff.IsSessionAdmin"/>) и ещё в мире (не
	/// успел перейти в редактор) - см. <see cref="JoinRequestPopupUi"/> class doc про то, почему это может случиться
	/// и здесь, и в <see cref="Editor.BuildEditor"/>.</summary>
	private void OnJoinRequestIncoming(string workbenchName, long requesterId)
	{
		_incomingJoinWorkbenchName = workbenchName;
		_joinPopup.Show(requesterId);
		// Мышь обычно захвачена (обзор от первого лица) - без этого курсор не виден и по Accept/Decline нельзя
		// кликнуть вообще (см. WORKLOG про баг "не видно курсора, навестись на кнопки невозможно").
		Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	/// <summary>Заявитель передумал (кнопка Cancel на его плашке ожидания) - убираем попап, только если он ВСЁ ЕЩЁ
	/// показывает именно эту заявку (см. <see cref="JoinRequestPopupUi.IsShowingRequestFrom"/>).</summary>
	private void OnJoinCancelled(string workbenchName, long requesterId)
	{
		if (!_joinPopup.IsShowingRequestFrom(requesterId)) return;
		_joinPopup.Hide();
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	/// <summary>Spawn прямо из меню верстака (кнопка рядом с Open) — не заходя в редактор: читает JSON постройки
	/// с диска и сразу материализует её в зоне спавна <see cref="_activeWorkbench"/> (тот верстак, через который
	/// открыли меню — см. <see cref="_activeWorkbench"/> doc). Доступна всегда, независимо от чужих сессий на этом
	/// верстаке (см. <c>WorkbenchMenuUi</c> class doc).</summary>
	private void SpawnFromWorkbenchMenu(string path)
	{
		using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		if (file == null || _activeWorkbench == null) return;

		string json = file.GetAsText();
		_workbenchMenu.Close();
		Input.MouseMode = Input.MouseModeEnum.Captured;

		var vehicle = VehicleSpawner.Spawn(this, json,
			_activeWorkbench.SpawnArea.GlobalPosition + new Vector3(0f, 0.1f, 0f), _activeWorkbench);
		_vehicles.Add(vehicle);
		ApplyDebugFlags(vehicle);
	}

	public override void _Input(InputEvent e)
	{
		// !IsInstanceValid - защита от случая, когда _player уже был свободен (например, мультиплеерная репликация
		// когда-то его удалила), а этот GameWorld ещё не успел это заметить - без неё обращение к освобождённому
		// узлу ниже кидало бы ObjectDisposedException каждый кадр (см. WORKLOG про баг с серым экраном у клиента).
		if (_handingOff || _player == null || !IsInstanceValid(_player)) return;
		if (e is not InputEventKey { Pressed: true, Echo: false } key) return;

		switch (key.PhysicalKeycode)
		{
			// Открыть верстак — по наведению (взгляд по центру экрана), не по близости, как было раньше.
			case Key.E when !AnyModalOpen && RaycastFromCamera(InteractDistance) is Workbench workbench:
				_activeWorkbench = workbench;
				_workbenchMenu.Open(workbench.Name);
				// Список сессий на этом верстаке запрашивается заново при КАЖДОМ открытии меню (не кэшируется) -
				// баг, найденный пользователем: игрок, подключившийся к сети ПОСЛЕ того, как сессию уже открыли,
				// раньше никогда не узнавал о ней (старый broadcast-механизм уведомлял только тех, кто уже был на
				// связи в момент открытия) - см. NetHub class doc/OnWorkbenchSessionsForMe.
				if (NetHub.Instance.IsNetworked) NetHub.Instance.RequestWorkbenchSessions(workbench.Name);
				Input.MouseMode = Input.MouseModeEnum.Visible;
				GetViewport().SetInputAsHandled();
				break;

			// Закрыть меню верстака без выбора — тот же Escape, что и остальные модальные окна проекта
			// (см. Editor.Ui.BlockPickerUi/SaveDialogUi).
			case Key.Escape when _workbenchMenu.IsOpen:
				_workbenchMenu.Close();
				Input.MouseMode = Input.MouseModeEnum.Captured;
				GetViewport().SetInputAsHandled();
				break;

			// Меню паузы (Resume/Exit to menu/Exit to desktop) - Escape закрывает его так же, как открывает (ниже),
			// когда ничего другого не открыто. Попап Join/плашка ожидания намеренно НЕ закрываются по Escape - у
			// них есть свои явные кнопки (Accept/Decline/Cancel), чтобы не отменить чужую заявку по случайности.
			case Key.Escape when _pauseMenu.IsOpen:
				_pauseMenu.Close();
				Input.MouseMode = Input.MouseModeEnum.Captured;
				GetViewport().SetInputAsHandled();
				break;

			case Key.Escape when !_joinPopup.IsOpen && !_joinWaitingUi.IsOpen:
				_pauseMenu.Open();
				Input.MouseMode = Input.MouseModeEnum.Visible;
				GetViewport().SetInputAsHandled();
				break;

			// Вернуть постройку (и себя) к верстаку, с которого её заспавнили — по наведению на саму постройку.
			case Key.R when !AnyModalOpen && RaycastFromCamera(InteractDistance) is VehicleBody vehicle:
				RecallVehicle(vehicle);
				GetViewport().SetInputAsHandled();
				break;

			// Дебаг-меню (см. задачу): solid <-> вид коллизии для всех заспавненных построек сразу.
			case Key.F1:
				_debugCollisionView = !_debugCollisionView;
				foreach (var v in _vehicles) v.SetDebugCollisionView(_debugCollisionView);
				GetViewport().SetInputAsHandled();
				break;

			// Дебаг: заглушка питания функциональных блоков (нод логики ещё нет) - запитать/обесточить все постройки.
			case Key.F2:
				_debugPowered = !_debugPowered;
				foreach (var v in _vehicles) v.SetDebugPowered(_debugPowered);
				GetViewport().SetInputAsHandled();
				break;

			// Дебаг: нажать/отпустить все кнопки всех построек (определения "в какую целится игрок" ещё нет).
			case Key.F3:
				_debugButtonsPressed = !_debugButtonsPressed;
				foreach (var v in _vehicles) v.SetDebugButtonsPressed(_debugButtonsPressed);
				GetViewport().SetInputAsHandled();
				break;
		}
	}

	public override void _Process(double delta)
	{
		if (_handingOff || _player == null || !IsInstanceValid(_player)) return;

		_player.MovementEnabled = !MovementBlockingModalOpen;
		_debugLabel.Text = string.Join("\n", new[]
		{
			_debugCollisionView ? "Debug: collision view (F1)" : "",
			_debugPowered ? "Debug: blocks powered (F2)" : "",
			_debugButtonsPressed ? "Debug: buttons pressed (F3)" : "",
		}.Where(line => line.Length > 0));

		if (AnyModalOpen)
		{
			_prompt.Text = "";
			return;
		}

		_prompt.Text = RaycastFromCamera(InteractDistance) switch
		{
			Workbench => "Press E to open the workbench",
			VehicleBody => "Press R to recall it to its workbench",
			_ => "",
		};
	}

	/// <summary>Луч из центра экрана (камера игрока) вперёд на <paramref name="maxDistance"/> метров — та же идея,
	/// что и наведение курсора в редакторе (<c>Editor.VoxelRaycaster</c>), только по физическим коллайдерам мира
	/// (<c>PhysicsDirectSpaceState3D</c>), а не по воксельной сетке построек — тут коллайдеры настоящие
	/// (<see cref="Workbench"/>/<see cref="VehicleBody"/>). null — луч ни во что не попал за это расстояние.</summary>
	private Node3D? RaycastFromCamera(float maxDistance)
	{
		var camera = _player.Camera;
		var from = camera.GlobalPosition;
		var to = from - camera.GlobalTransform.Basis.Z * maxDistance;
		var query = PhysicsRayQueryParameters3D.Create(from, to);
		var result = GetWorld3D().DirectSpaceState.IntersectRay(query);
		return result.Count == 0 ? null : result["collider"].As<Node3D>();
	}

	/// <summary>Свежезаспавненная постройка подхватывает уже включённые дебаг-флаги (F2/F3).</summary>
	private void ApplyDebugFlags(VehicleBody vehicle)
	{
		vehicle.SetDebugCollisionView(_debugCollisionView);
		vehicle.SetDebugPowered(_debugPowered);
		vehicle.SetDebugButtonsPressed(_debugButtonsPressed);
	}

	private void RecallVehicle(VehicleBody vehicle)
	{
		_vehicles.Remove(vehicle);
		var workbench = vehicle.SourceWorkbench;
		vehicle.QueueFree();

		// Место у верстака хоть сколько-то произвольное (фиксированный отступ, не разворот лицом к верстаку) -
		// самого верстака при возврате достаточно, точная поза не так важна.
		if (workbench != null) _player.SetPose(workbench.GlobalPosition + new Vector3(0f, 1f, 2f), _player.Yaw);
	}

	private void EnterEditorFromWorkbench()
	{
		EditorHandoff.PendingSpawnWorkbenchName = _activeWorkbench?.Name;
		EditorHandoff.PendingPlayerPosition = _player.GlobalPosition;
		EditorHandoff.PendingPlayerYaw = _player.Yaw;
		EnterEditor();
	}

	private void EnterEditor()
	{
		// Мультиплеер: игроки больше не уничтожаются при уходе в редактор (живут на NetHub, см.
		// SetupNetworkedPlayers class doc) - но пока я в редакторе, WASD/мышь предназначены ЕМУ, не моему
		// персонажу где-то в мире фоном (SetActive(false)), а ВСЕ игроки (свой и чужие) не должны маячить посреди
		// сцены редактора - NetHub.PlayersRoot сиблинг текущей сцены, значит виден и оттуда, если не спрятать (баг
		// из WORKLOG "в редакторе отображается персонаж хоста"). Оба возвращаются обратно в HandleSpawnedPlayer/
		// SetupNetworkedPlayers при возврате в мир.
		_player?.SetActive(false);
		if (NetHub.Instance.PlayersRoot != null) NetHub.Instance.PlayersRoot.Visible = false;
		_handingOff = true;
		GoToBuildEditor();
	}

	public override void _ExitTree() => UnsubscribeNetworking();

	/// <summary>
	/// <see cref="SceneTree.ChangeSceneToFile"/> напрямую (не через <see cref="Node.CallDeferred"/>) падает с
	/// engine-ошибкой "Parent node is busy adding/removing children", если вызвана, пока текущий узел ещё сам
	/// заканчивает входить в дерево (ровно наш случай — вызывается прямо из <see cref="_Ready"/> для дев-харнесса) —
	/// откладываем через <see cref="Callable"/> на конец кадра, когда дерево уже точно не занято.
	/// </summary>
	private void GoToBuildEditor() =>
		Callable.From(() => GetTree().ChangeSceneToFile("res://Scenes/BuildEditor.tscn")).CallDeferred();
}
