using System;
using Godot;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.Editor;

/// <summary>
/// Визуал ОДНОГО колеса на экземпляре его модели (см. <see cref="FunctionalBlockView"/>). Модель — дерево узлов <c>Wheel → Steer → Spin → { Rim, Tire }</c>, имена узлов — константы
/// блока в <c>"params"</c> (<see cref="WheelSettings"/>). Анимаций нет, всё считается кодом:
/// <list type="bullet">
/// <item><b>Вращение</b> — узел <c>Spin</c> поворачивается на накопленный угол вокруг СВОЕЙ локальной оси X (<c>базовое положение · поворот_X(угол)</c>). Угол копит сам визуал:
/// <c>+= ω·dt</c> каждый кадр (иначе на копии-наблюдателе с обновлениями 10 Гц колесо дёргалось бы) и мягко подтягивается к углу состояния (<see cref="WheelState.SpinAngle"/>),
/// чтобы не уплывать от сервера.</item>
/// <item><b>Руль</b> — узел <c>Steer</c> поворачивается вокруг своей локальной оси Y на угол руля; показываемый угол плавно догоняет <see cref="WheelState.SteerAngle"/>.</item>
/// <item><b>Размеры</b> — радиальный масштаб узлов <c>Rim</c> (диск) и <c>Tire</c> (шина) в системе узла Spin: по Y и Z на <c>radius / modelRadius</c>, ось X (ширина) не меняется.
/// Радиусы берутся из состояния (параметры экземпляра), радиусы нарисованной модели — <see cref="WheelSettings.ModelTireRadiusCells"/>/<see cref="WheelSettings.ModelRimRadiusCells"/>.
/// Внутренний край шины прячется под диском (диск не меньше <c>MinRimGapCells</c> от края шины).</item>
/// <item><b>Пробитая шина</b> — узел <c>Tire</c> скрыт.</item>
/// </list>
/// Нет какого-то из узлов — этот канал просто не работает, ошибок нет. Иконка в хотбаре и призрак установки колесо не анимируют.
/// Положение центра колеса и направления осей (ось X блока, вертикаль Y) модель должна задавать сама: Spin стоит центром в центре корневой клетки блока.
/// </summary>
public sealed class WheelVisual
{
    /// <summary>Как быстро показываемый угол руля догоняет угол состояния, 1/с (экспоненциально).</summary>
    public const double SteerFollow = 12.0;

    /// <summary>Как быстро показываемый угол вращения подтягивается к углу состояния, 1/с.</summary>
    public const double SpinCorrection = 6.0;

    private readonly Node3D? _steer;
    private readonly Node3D? _spin;
    private readonly Node3D? _rim;
    private readonly Node3D? _tire;
    private readonly Transform3D _steerBase;
    private readonly Transform3D _spinBase;
    private readonly Transform3D _rimBase;
    private readonly Transform3D _tireBase;
    private readonly WheelSettings _settings;

    public double SpinAngle { get; private set; }
    public double SteerAngle { get; private set; }

    public bool HasSteer => _steer != null;
    public bool HasSpin => _spin != null;
    public bool HasRim => _rim != null;
    public bool HasTire => _tire != null;

    public WheelVisual(Node modelRoot, WheelSettings settings)
    {
        _settings = settings;
        _steer = Find(modelRoot, settings.SteerNode);
        _spin = Find(modelRoot, settings.SpinNode);
        _rim = Find(modelRoot, settings.RimNode);
        _tire = Find(modelRoot, settings.TireNode);
        _steerBase = _steer?.Transform ?? Transform3D.Identity;
        _spinBase = _spin?.Transform ?? Transform3D.Identity;
        _rimBase = _rim?.Transform ?? Transform3D.Identity;
        _tireBase = _tire?.Transform ?? Transform3D.Identity;
    }

    private static Node3D? Find(Node root, string name) => root.FindChild(name, true, false) as Node3D;

    /// <summary>Один кадр: <paramref name="delta"/> — время кадра, <paramref name="state"/> — состояние колеса из рантайма (радиусы, обороты, руль, пробитая шина).</summary>
    public void Update(double delta, WheelState state)
    {
        SpinAngle = WheelBehavior.Wrap(SpinAngle + state.Omega * delta);
        double spinError = WheelBehavior.Wrap(state.SpinAngle - SpinAngle);
        SpinAngle = WheelBehavior.Wrap(SpinAngle + spinError * Math.Min(1.0, SpinCorrection * delta));
        SteerAngle += (state.SteerAngle - SteerAngle) * (1 - Math.Exp(-SteerFollow * delta));

        if (_steer != null) _steer.Transform = _steerBase * new Transform3D(new Basis(Vector3.Up, SteerAngle), Vector3.Zero);
        if (_spin != null) _spin.Transform = _spinBase * new Transform3D(new Basis(Vector3.Right, SpinAngle), Vector3.Zero);

        // Радиальный масштаб в системе узла Spin (его X - ось колеса): растягиваются Y и Z, ширина остаётся.
        if (_rim != null) _rim.Transform = Radial(state.Settings.RimRadiusCells / _settings.ModelRimRadiusCells) * _rimBase;
        if (_tire != null)
        {
            _tire.Transform = Radial(state.Settings.TireRadiusCells / _settings.ModelTireRadiusCells) * _tireBase;
            _tire.Visible = !state.TireBroken;
        }
    }

    private static Transform3D Radial(double factor) => new(Basis.FromScale(new Vector3(1, factor, factor)), Vector3.Zero);
}
