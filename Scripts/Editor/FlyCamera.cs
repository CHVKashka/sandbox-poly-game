using Godot;

namespace SandboxPolyGame.Editor;

/// <summary>
/// Свободная камера редактора. WASD — движение вдоль взгляда/вбок, Q/E — вниз/вверх, Shift — ускорение.
/// Поворот вызывается извне (<see cref="Look"/>) — по зажатой средней кнопке мыши.
/// Клавиши читаются по физическому расположению, поэтому работают при любой раскладке (RU/EN).
/// </summary>
public partial class FlyCamera : Camera3D
{
    private const double MaxPitch = 1.5533; // ~89 градусов

    public double BaseSpeed { get; set; } = 4.0;
    public double FastMultiplier { get; set; } = 4.0;
    public double SlowMultiplier { get; set; } = 0.25;
    public double LookSensitivity { get; set; } = 0.0025;
    public double ZoomStep { get; set; } = 0.6;
    public bool MovementEnabled { get; set; } = true;

    private double _yaw;
    private double _pitch;

    public double Yaw => _yaw;
    public double Pitch => _pitch;

    public void SetPose(Vector3 position, double yaw, double pitch)
    {
        Position = position;
        _yaw = yaw;
        _pitch = Mathf.Clamp(pitch, -MaxPitch, MaxPitch);
        ApplyRotation();
    }

    public void LookAtPoint(Vector3 position, Vector3 target)
    {
        var f = (target - position).Normalized();
        SetPose(position, Mathf.Atan2(-f.X, -f.Z), Mathf.Asin(f.Y));
    }

    public void Look(Vector2 relative)
    {
        _yaw -= relative.X * LookSensitivity;
        _pitch = Mathf.Clamp(_pitch - relative.Y * LookSensitivity, -MaxPitch, MaxPitch);
        ApplyRotation();
    }

    private void ApplyRotation() => Rotation = new Vector3(_pitch, _yaw, 0);

    /// <summary>Двигает камеру вдоль направления взгляда (вперёд — положительный <paramref name="steps"/>) — колесо
    /// мыши в редакторе теперь зумит камеру этим методом, а не листает слоты хотбара (см. <c>BuildEditor</c>).
    /// Не "настоящий" зум (не меняет FOV/перспективу) — обычное перемещение свободной камеры, тот же приём, что и
    /// WASD, просто по колесу и с своим шагом на "щелчок".</summary>
    public void Zoom(double steps) => GlobalPosition -= GlobalTransform.Basis.Z * (float)(steps * ZoomStep);

    /// <summary>
    /// Баг, найденный пользователем: WASD печатался в сфокусированное текстовое поле И ОДНОВРЕМЕННО двигал камеру -
    /// <see cref="Input.IsPhysicalKeyPressed"/> ниже читает физическое состояние клавиши НАПРЯМУЮ, не глядя на фокус
    /// UI (в отличие от текста, который Godot доставляет ОТДЕЛЬНО сфокусированному <see cref="LineEdit"/>). Раньше
    /// от этого защищал только клик мышью (снимал фокус ДО печати, см. <c>Dev.BlockPrefabEditor._Input</c>/
    /// <c>Editor.BuildEditor</c> про Ctrl+Z/Y) - не помогало, если поле было сфокусировано и с него просто печатали,
    /// ни разу не кликнув заново в 3D-вид. Явная проверка фокуса здесь закрывает именно этот случай, в одном месте
    /// сразу для обоих инструментов, использующих эту камеру (<c>BuildEditor</c>/<c>Dev.BlockPrefabEditor</c>).
    /// </summary>
    private bool IsTypingIntoField() => GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;

    public override void _Process(double delta)
    {
        if (!MovementEnabled || IsTypingIntoField()) return;

        var basis = GlobalTransform.Basis;
        var direction = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) direction -= basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.S)) direction += basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.D)) direction += basis.X;
        if (Input.IsPhysicalKeyPressed(Key.A)) direction -= basis.X;
        if (Input.IsPhysicalKeyPressed(Key.E)) direction += Vector3.Up;
        if (Input.IsPhysicalKeyPressed(Key.Q)) direction -= Vector3.Up;
        if (direction == Vector3.Zero) return;

        double speed = BaseSpeed;
        if (Input.IsKeyPressed(Key.Shift)) speed *= FastMultiplier;
        else if (Input.IsKeyPressed(Key.Ctrl)) speed *= SlowMultiplier;

        GlobalPosition += direction.Normalized() * (speed * delta);
    }
}
