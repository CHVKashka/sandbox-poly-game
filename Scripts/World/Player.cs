using Godot;

namespace SandboxPolyGame.World;

/// <summary>
/// Игрок в открытом мире (вне редактора построек) — вид от первого лица. WASD — ходьба относительно поворота
/// корпуса, мышь — поворот (yaw крутит сам <see cref="CharacterBody3D"/>, pitch — только "голову" с камерой,
/// та же схема, что у <see cref="Editor.FlyCamera"/>), Shift — бег, пробел — прыжок. Клавиши читаются по
/// физическому расположению (<see cref="Input.IsPhysicalKeyPressed"/>), как и везде в проекте — работают в любой
/// раскладке. Пока без анимаций/модели персонажа (капсула-коллайдер, камера от первого лица) — это отдельная,
/// не первоочередная задача (см. Docs/05-world-and-vehicle-systems.md).
/// </summary>
public partial class Player : CharacterBody3D
{
    public const double WalkSpeed = 4.5;
    public const double SprintMultiplier = 1.8;
    public const double JumpVelocity = 4.5;
    public const double LookSensitivity = 0.0025;
    private const double MaxPitch = 1.5; // чуть меньше 90°, камеру не переворачивает

    private readonly double _gravity = (double)ProjectSettings.GetSetting("physics/3d/default_gravity");

    private double _yaw;
    private double _pitch;
    private Node3D _head = null!;
    private Camera3D _camera = null!;

    /// <summary>Камера от первого лица — <see cref="Camera3D.Current"/> выставляется в <see cref="_EnterTree"/>.</summary>
    public Camera3D Camera => _camera;

    /// <summary>
    /// Мультиплеер (см. Docs/05-world-and-vehicle-systems.md, «Мультиплеер»): весь узел строится в
    /// <see cref="_EnterTree"/>, не в <see cref="_Ready"/> — движковое требование для
    /// <see cref="MultiplayerSynchronizer"/>, найденное по двум реальным ошибкам у джойнящегося клиента (проверено
    /// чтением исходников движка, <c>scene_replication_interface.cpp</c>, <c>on_replication_start</c>): (1)
    /// "Condition '!node || !sync->get_replication_config_ptr()' is true" — движок применяет реплицированное
    /// начальное состояние узла в момент, когда сам <see cref="MultiplayerSynchronizer"/> входит в дерево, поэтому
    /// его <see cref="MultiplayerSynchronizer.ReplicationConfig"/> должен быть назначен ДО <c>AddChild</c>, а не
    /// после (было наоборот); (2) "...unable to process the pending spawn since it has no network ID... Make sure to
    /// only change the authority of multiplayer synchronizers during the '_enter_tree' callback" — по той же причине
    /// авторитет синхронизатора тоже должен быть выставлен ДО <c>AddChild</c>/<c>_EnterTree</c>, не внутри
    /// <see cref="_Ready"/> (это окно уже закрыто к тому моменту).
    /// </summary>
    public override void _EnterTree()
    {
        _head = new Node3D { Name = "Head", Position = new Vector3(0, 1.6, 0) };
        AddChild(_head);
        _camera = new Camera3D { Name = "Camera", Fov = 75f, Near = 0.05f, Far = 500f };
        _head.AddChild(_camera);

        AddChild(new CollisionShape3D
        {
            Name = "Collision",
            Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.8f },
            Position = new Vector3(0, 0.9, 0),
        });

        // Болванка тела - видна только ЧУЖИМ игрокам (мультиплеер): от первого лица своё тело по-прежнему не
        // показываем, как и раньше (см. class doc), но без хоть какого-то меша другие игроки были бы невидимой
        // летающей камерой - см. IsMultiplayerAuthority ниже, где решается, чьё это тело.
        var body = new MeshInstance3D
        {
            Name = "BodyMesh",
            Mesh = new CapsuleMesh { Radius = 0.35f, Height = 1.8f },
            Position = new Vector3(0, 0.9, 0),
        };
        AddChild(body);

        // Мультиплеер: каждый подключённый игрок реплицирует свою позу через MultiplayerSynchronizer (см.
        // World.NetHub - там же спавнятся сами узлы) - настраивается на ВСЕХ инстансах (включая чужие копии,
        // которым синхронизатор как раз и ставит позицию/поворот). Авторитет синхронизатора выставляется ЕМУ
        // САМОМУ явно, ДО AddChild - см. class doc про то, почему не рекурсивным SetMultiplayerAuthority постфактум
        // и не в _Ready (обе версии ловили реальные баги, см. WORKLOG 2026-09-29 (7)/(9)).
        var sync = new MultiplayerSynchronizer { Name = "Sync" };
        var replication = new SceneReplicationConfig();
        replication.AddProperty(new NodePath(".:position"));
        replication.AddProperty(new NodePath(".:rotation"));
        sync.ReplicationConfig = replication;
        sync.SetMultiplayerAuthority(GetMultiplayerAuthority()); // this (Player) уже получил его в SpawnPlayerFunc/NetHub, ДО входа в дерево
        AddChild(sync);

        // Камера/мышь/ввод/собственное тело на экране принадлежат РОВНО ОДНОМУ инстансу — тому, чей это узел
        // (IsMultiplayerAuthority). В одиночной игре узел без явного SetMultiplayerAuthority по умолчанию имеет
        // авторитет 1, и локальный peer id тоже 1 - проверка всегда проходит, поведение не меняется.
        body.Visible = !IsMultiplayerAuthority();
        if (!IsMultiplayerAuthority()) return;

        _camera.Current = true;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// <summary>
    /// Мультиплеер: узел теперь живёт на <c>Core.NetHub</c> (автозагрузка, переживает переход мир→редактор→мир у
    /// СВОЕГО ЖЕ пира — см. Docs/05, «Мультиплеер», фикс 2026-09-29 (9)), поэтому больше не пересоздаётся каждый
    /// раз при возврате в мир. Пока пир СЕЙЧАС в редакторе (не в <c>World.GameWorld</c> локально), его собственный
    /// узел должен молчать — иначе WASD/мышь, предназначенные редактору, заодно двигали бы и поворачивали бы
    /// персонажа где-то в мире фоном. <see cref="World.GameWorld"/> вызывает это ДО перехода в редактор (false) и
    /// сразу же по возвращении, найдя уже существующий узел (true) — см. её же исходники.
    /// </summary>
    public void SetActive(bool active)
    {
        SetProcess(active);
        SetPhysicsProcess(active);
        SetProcessInput(active);
        SetProcessUnhandledInput(active);
    }

    // Esc теперь целиком в ведении GameWorld (меню паузы - Resume/Exit to menu/Exit to desktop, см.
    // World.Ui.PauseMenuUi) - раньше Player сам переключал захват мыши по Esc, но это конфликтовало бы с тем, что
    // GameWorld тоже должна выставлять MouseMode при открытии меню.
    public override void _Input(InputEvent e)
    {
        if (!IsMultiplayerAuthority()) return;

        if (e is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured) Look(motion.Relative);
    }

    private void Look(Vector2 relative)
    {
        _yaw -= relative.X * LookSensitivity;
        _pitch = Mathf.Clamp(_pitch - relative.Y * LookSensitivity, -MaxPitch, MaxPitch);
        Rotation = new Vector3(0, _yaw, 0);
        _head.Rotation = new Vector3(_pitch, 0, 0);
    }

    /// <summary>Считывает WASD как плоский вектор ввода (X — вправо/влево, Y — назад/вперёд, ещё не нормализован
    /// и не привязан к повороту) — вынесено отдельно от <see cref="ComputeWalkVelocity"/> только ради читаемости.</summary>
    private static Vector2 ReadMoveInput()
    {
        double x = 0, y = 0;
        if (Input.IsPhysicalKeyPressed(Key.D)) x += 1;
        if (Input.IsPhysicalKeyPressed(Key.A)) x -= 1;
        if (Input.IsPhysicalKeyPressed(Key.S)) y += 1;
        if (Input.IsPhysicalKeyPressed(Key.W)) y -= 1;
        return new Vector2(x, y);
    }

    /// <summary>
    /// Чистая логика без сцены (специально вынесена из <see cref="_PhysicsProcess"/>) — только математика:
    /// плоский ввод (см. <see cref="ReadMoveInput"/>) поворачивается на <paramref name="yaw"/> корпуса и
    /// масштабируется до <paramref name="speed"/>. Раздельность ввод/математика/применение к телу — не только ради
    /// самотеста, но и задел на будущее сетевое предсказание движения (см. Docs/05-world-and-vehicle-systems.md,
    /// «Мультиплеер»): предсказание клиента пересчитывает ровно эту функцию по сохранённой истории ввода, без
    /// зависимости от live-сцены.
    /// </summary>
    public static Vector3 ComputeWalkVelocity(Vector2 inputAxis, double yaw, double speed)
    {
        if (inputAxis.LengthSquared() < 1e-9) return Vector3.Zero;

        var direction = inputAxis.Normalized();
        var basis = new Basis(Vector3.Up, yaw);
        var local = new Vector3(direction.X, 0, direction.Y);
        return basis * local * speed;
    }

    /// <summary>Выключается, пока открыто модальное окно поверх мира (например, меню верстака — см.
    /// <see cref="GameWorld"/>) — WASD/прыжок не должны двигать персонажа, пока игрок печатает/кликает по UI, хотя
    /// гравитация по-прежнему действует (иначе игрок завис бы в воздухе, если меню открыто в падении/прыжке).</summary>
    public bool MovementEnabled { get; set; } = true;

    /// <summary>Выставляется <see cref="GameWorld"/> сразу после возврата из редактора (Exit или Spawn) — пока
    /// урона в игре вообще нет неоткуда взяться, это просто хранимый флаг на будущее (когда появится сама система
    /// урона, она должна будет его проверять — см. Docs/05-world-and-vehicle-systems.md). Когда и как именно
    /// неуязвимость снова выключается — пока не решено, см. тот же документ.</summary>
    public bool Invulnerable { get; set; }

    /// <summary>Текущий поворот корпуса (yaw) — нужен <see cref="GameWorld"/>, чтобы сохранить ориентацию игрока
    /// перед входом в редактор и восстановить её при возврате (см. <see cref="SetPose"/>).</summary>
    public double Yaw => _yaw;

    /// <summary>Жёстко ставит игрока в точку с заданным поворотом корпуса (pitch головы не трогается) — используется
    /// при возврате из редактора (сохранить место) и при возврате по `R` (телепорт к верстаку).</summary>
    public void SetPose(Vector3 position, double yaw)
    {
        GlobalPosition = position;
        _yaw = yaw;
        Rotation = new Vector3(0, _yaw, 0);
        Velocity = Vector3.Zero;
    }

    public override void _PhysicsProcess(double delta)
    {
        // Чужой игрок (мультиплеер) - позицию для него ставит MultiplayerSynchronizer (см. World.GameWorld), своя
        // локальная физика/ввод тут же её только сбивали бы (в т.ч. читая WASD с ЭТОЙ машины, будто это мой ввод).
        if (!IsMultiplayerAuthority()) return;

        var velocity = Velocity;

        if (!IsOnFloor()) velocity.Y -= _gravity * delta;
        else if (MovementEnabled && Input.IsPhysicalKeyPressed(Key.Space)) velocity.Y = JumpVelocity;

        if (MovementEnabled)
        {
            double speed = WalkSpeed * (Input.IsKeyPressed(Key.Shift) ? SprintMultiplier : 1.0);
            var horizontal = ComputeWalkVelocity(ReadMoveInput(), _yaw, speed);
            velocity.X = horizontal.X;
            velocity.Z = horizontal.Z;
        }
        else
        {
            velocity.X = 0;
            velocity.Z = 0;
        }

        Velocity = velocity;
        MoveAndSlide();
    }
}
