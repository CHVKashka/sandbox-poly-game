using System;
using System.Collections.Generic;
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

    private CollisionShape3D _collisionShape = null!;
    private Func<Vector3>? _seatEye;

    /// <summary>Игрок сидит (<see cref="Sit"/>): ходьба и физика выключены, камера стоит в точке глаз сиденья, мышь по-прежнему вращает взгляд.</summary>
    public bool Seated => _seatEye != null;

    /// <summary>
    /// Посадить игрока. <paramref name="eyeWorld"/> вызывается каждый физический тик и возвращает ТЕКУЩУЮ мировую точку глаз (сиденье едет вместе с
    /// телом постройки); <paramref name="yaw"/> — куда повернуть взгляд при посадке (дальше игрок крутит камеру сам). Коллизия капсулы выключается —
    /// иначе она толкала бы тело постройки, внутри которого сидит.
    /// </summary>
    public void Sit(Func<Vector3> eyeWorld, double yaw)
    {
        _standSettle = 0;
        ClearPlatform();
        _seatEye = eyeWorld;
        _collisionShape.SetDeferred(CollisionShape3D.PropertyName.Disabled, true);
        Velocity = Vector3.Zero;
        _yaw = yaw;
        _pitch = 0;
        Rotation = new Vector3(0, _yaw, 0);
        _head.Rotation = Vector3.Zero;
        GlobalPosition = eyeWorld() - new Vector3(0, (float)_head.Position.Y, 0);
    }

    /// <summary>
    /// Встать: игрок появляется в <paramref name="worldPosition"/> (точка ног), коллизия и физика включаются, дальше он падает/ходит. Если задан
    /// <paramref name="platform"/> (тело постройки, с которого встали), игрок сразу «примагничен» к нему (см. <see cref="Platform"/>).
    /// Точка должна быть свободна от других коллайдеров (<see cref="FindFreePosition"/>): капсула, появившаяся внутри тела, выталкивается из него с огромным
    /// импульсом, и динамическая постройка улетает.
    /// </summary>
    public void Stand(Vector3 worldPosition, RigidBody3D? platform = null)
    {
        _seatEye = null;
        // Коллизия остаётся выключенной ещё несколько физических тиков (StandSettleTicks): физический сервер должен сначала принять новое положение тела. Включить капсулу
        // сразу - значит дать ей «перелететь» из старой точки (внутри постройки) в новую за один шаг, и она утащит/подбросит тело постройки с огромной скоростью.
        _collisionShape.Disabled = true;
        _standSettle = StandSettleTicks;
        GlobalPosition = worldPosition;
        Velocity = Vector3.Zero;
        _inheritedVelocity = Vector3.Zero;
        _airTime = 0;
        if (platform != null) AttachToPlatform(platform);
        else ClearPlatform();
    }

    /// <summary>Радиус/высота проверочной капсулы (чуть больше настоящей) — зазор, чтобы игрок не появлялся вплотную к поверхности.</summary>
    /// <summary>Сколько физических тиков после <see cref="Stand"/> капсула остаётся выключенной (см. там же).</summary>
    public const int StandSettleTicks = 2;

    private int _standSettle;

    private const float ProbeRadius = 0.37f;
    private const float ProbeHeight = 1.84f;

    /// <summary>
    /// Ближайшая к <paramref name="preferred"/> (точка ног) позиция, где капсула игрока не пересекается ни с чем: перебор по высоте от 0 до <paramref name="maxLift"/> м
    /// шагом 0.1 м, на каждой высоте — по очереди сдвиги <paramref name="sideways"/> (мировые векторы, первым идёт самый желанный; пусто — только точка без сдвига).
    /// Ничего не нашлось — точка на максимальной высоте.
    /// </summary>
    public Vector3 FindFreePosition(Vector3 preferred, IReadOnlyList<Vector3>? sideways = null, double maxLift = 3.0)
    {
        var space = GetWorld3D().DirectSpaceState;
        var exclude = new Godot.Collections.Array<Rid> { GetRid() };
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new CapsuleShape3D { Radius = ProbeRadius, Height = ProbeHeight },
            CollisionMask = CollisionMask,
            Exclude = exclude,
        };

        var offsets = sideways is { Count: > 0 } ? sideways : new[] { Vector3.Zero };
        for (double lift = 0; lift <= maxLift + 1e-9; lift += 0.1)
        {
            foreach (var side in offsets)
            {
                var feet = preferred + side + new Vector3(0, (float)lift, 0);
                query.Transform = new Transform3D(Basis.Identity, feet + new Vector3(0, ProbeHeight * 0.5f, 0));
                if (space.IntersectShape(query, 1).Count == 0) return feet;
            }
        }

        return preferred + new Vector3(0, (float)maxLift, 0);
    }

    // ------------------------------------------------------------------ платформа: игрок едет вместе с телом, на котором стоит

    /// <summary>Максимальный наклон поверхности под ногами (угол её нормали к вертикали), при котором игрок остаётся «приклеенным» к телу; круче — отклеивается.</summary>
    public const double PlatformMaxTiltDegrees = 30;

    /// <summary>Сколько секунд без опоры под ногами игрок ещё считается едущим на платформе (прыжок/падение на неё) — потом отклеивается и сохраняет её скорость.</summary>
    public const double PlatformAirGrace = 0.2;

    private RigidBody3D? _platform;
    private Transform3D _platformLast;
    private double _airTime;
    private Vector3 _inheritedVelocity;

    /// <summary>Тело, к которому игрок сейчас «приклеен» (стоит на нём): его смещение и поворот вокруг вертикали передаются игроку и камере. null — игрок сам по себе.</summary>
    public RigidBody3D? Platform => _platform != null && IsInstanceValid(_platform) ? _platform : null;

    /// <summary>Приклеить игрока к телу <paramref name="platform"/> (дальше он едет вместе с ним, пока стоит на достаточно ровной поверхности).</summary>
    public void AttachToPlatform(RigidBody3D platform)
    {
        _platform = platform;
        _platformLast = platform.GlobalTransform;
        _airTime = 0;
    }

    /// <summary>Отклеить от платформы без наследования скорости.</summary>
    public void ClearPlatform()
    {
        _platform = null;
        _airTime = 0;
    }

    /// <summary>Скорость точки <paramref name="worldPoint"/> жёсткого тела (линейная + от вращения).</summary>
    private static Vector3 VelocityAt(RigidBody3D body, Vector3 worldPoint)
    {
        var state = PhysicsServer3D.BodyGetDirectState(body.GetRid());
        if (state == null) return body.LinearVelocity;
        return state.GetVelocityAtLocalPosition(worldPoint - body.GlobalPosition); // смещение от начала тела в мировых осях
    }

    /// <summary>Отклеивает от платформы: игрок сохраняет горизонтальную скорость платформы, пока не приземлится.</summary>
    private void DetachFromPlatform()
    {
        if (Platform is { } body)
        {
            var velocity = VelocityAt(body, GlobalPosition);
            _inheritedVelocity = new Vector3(velocity.X, 0, velocity.Z);
        }

        ClearPlatform();
    }

    /// <summary>
    /// Переносит игрока вслед за платформой: положение — тем же преобразованием, каким сдвинулось тело с прошлого тика (поворот тела двигает и стоящего на нём), поворот
    /// взгляда — на изменение курса (yaw) тела; наклон тела камеру/капсулу не кренит (игрок всегда вертикален).
    /// </summary>
    private void CarryWithPlatform()
    {
        if (Platform is not { } body)
        {
            _platform = null;
            return;
        }

        var now = body.GlobalTransform;
        var delta = now * _platformLast.AffineInverse();
        _platformLast = now;

        GlobalPosition = delta * GlobalPosition;
        var forward = delta.Basis * Vector3.Forward;
        if (new Vector2(forward.X, forward.Z).LengthSquared() > 1e-8)
        {
            _yaw += Math.Atan2(-forward.X, -forward.Z);
            Rotation = new Vector3(0, _yaw, 0);
        }
    }

    /// <summary>После MoveAndSlide: определяет, на чём игрок стоит, и клеит/отклеивает его.</summary>
    private void UpdatePlatformContact(double delta)
    {
        RigidBody3D? floorBody = null;
        double tiltLimit = Math.Cos(Mathf.DegToRad(PlatformMaxTiltDegrees));
        if (IsOnFloor())
        {
            for (int i = 0; i < GetSlideCollisionCount(); i++)
            {
                var collision = GetSlideCollision(i);
                if (collision.GetCollider() is RigidBody3D body && collision.GetNormal().Y >= tiltLimit)
                {
                    floorBody = body;
                    break;
                }
            }

            _airTime = 0;
            _inheritedVelocity = Vector3.Zero;
        }
        else
        {
            _airTime += delta;
        }

        if (floorBody != null)
        {
            if (Platform != floorBody) AttachToPlatform(floorBody);
        }
        else if (Platform != null && (IsOnFloor() || _airTime > PlatformAirGrace))
        {
            // Стоит на поверхности тела, но она наклонилась круче допустимого (или встал на другую опору) / слишком долго в воздухе - отклеился.
            DetachFromPlatform();
        }
    }

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
        // Движение платформы переносим сами (CarryWithPlatform: ещё и поворот), встроенная скорость пола сложилась бы с нашей и удвоила смещение.
        PlatformFloorLayers = 0;
        PlatformWallLayers = 0;
        PlatformOnLeave = PlatformOnLeaveEnum.DoNothing;

        _head = new Node3D { Name = "Head", Position = new Vector3(0, 1.6, 0) };
        AddChild(_head);
        _camera = new Camera3D { Name = "Camera", Fov = 75f, Near = 0.05f, Far = 500f };
        _head.AddChild(_camera);

        _collisionShape = new CollisionShape3D
        {
            Name = "Collision",
            Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.8f },
            Position = new Vector3(0, 0.9, 0),
        };
        AddChild(_collisionShape);

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
        ClearPlatform();
        _inheritedVelocity = Vector3.Zero;
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

        // Сидим: глаза едут вместе с сиденьем (оно на движущемся теле постройки), никакой ходьбы/гравитации.
        if (_seatEye != null)
        {
            GlobalPosition = _seatEye() - new Vector3(0, (float)_head.Position.Y, 0);
            Velocity = Vector3.Zero;
            return;
        }

        if (_standSettle > 0)
        {
            CarryWithPlatform();
            Velocity = Vector3.Zero;
            if (--_standSettle == 0) _collisionShape.Disabled = false;
            return;
        }

        // Едем вместе с телом, на котором стоим (до расчёта движения: ходьба идёт уже относительно платформы).
        CarryWithPlatform();

        var velocity = Velocity;

        // Гравитация действует ВСЕГДА (в том числе на полу): иначе при стоянии на месте нет контакта с полом в MoveAndSlide и нечем определить, что под ногами.
        velocity.Y -= _gravity * delta;
        if (IsOnFloor() && MovementEnabled && Input.IsPhysicalKeyPressed(Key.Space)) velocity.Y = JumpVelocity;

        if (MovementEnabled)
        {
            double speed = WalkSpeed * (Input.IsKeyPressed(Key.Shift) ? SprintMultiplier : 1.0);
            var horizontal = ComputeWalkVelocity(ReadMoveInput(), _yaw, speed);
            velocity.X = horizontal.X + _inheritedVelocity.X;
            velocity.Z = horizontal.Z + _inheritedVelocity.Z;
        }
        else
        {
            velocity.X = _inheritedVelocity.X;
            velocity.Z = _inheritedVelocity.Z;
        }

        Velocity = velocity;
        MoveAndSlide();
        UpdatePlatformContact(delta);
    }
}
