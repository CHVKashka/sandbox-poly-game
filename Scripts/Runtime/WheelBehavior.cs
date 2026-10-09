using System;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Runtime;

/// <summary>
/// Настройки колеса, общие для поведения (<see cref="WheelBehavior"/>), физики (<c>World.WheelPhysics</c>) и визуала (<c>Editor.WheelVisual</c>), чтобы все читали их одинаково.
/// Два источника:
/// <list type="bullet">
/// <item><b>Настраиваемые игроком</b> (инструмент «Parameters», <see cref="ParameterSet"/>): <c>tireRadius</c> и <c>rimRadius</c> — внешний радиус шины и радиус диска В КЛЕТКАХ
/// (границы у каждого типа колеса свои, это границы параметра в его схеме: у колеса 3×3 — 1.5…2.4 и 1.0…1.6), <c>reverse</c> — обратить направление вращения (колесо с другой стороны оси).
/// Диск не может подойти к шине ближе <see cref="MinRimGapCells"/> клетки (шина всегда остаётся видимой и имеет «толщину»): <c>rimRadius</c> зажимается до
/// <c>tireRadius − 0.25</c>.</item>
/// <item><b>Константы типа блока</b> (<c>"params"</c> в FunctionalBlock): имена узлов модели <c>steerNode</c> / <c>spinNode</c> / <c>rimNode</c> / <c>tireNode</c> (умолчание Steer / Spin /
/// Rim / Tire), id входных нод <c>steerSignal</c> / <c>brakeSignal</c> / <c>fullBrakeSignal</c> (умолчание steer / brake / full_brake), <c>maxSteerDegrees</c> (60),
/// <c>steerRateDegrees</c> — скорость поворота колеса, °/с (180), радиусы, при которых модель нарисована без растяжения: <c>modelTireRadius</c> (1.5) и <c>modelRimRadius</c> (1.0).</item>
/// </list>
/// </summary>
public sealed record WheelSettings(
    double TireRadiusCells, double RimRadiusCells, bool Reverse, double MaxSteerRadians, double SteerRateRadians,
    string SteerSignal, string BrakeSignal, string FullBrakeSignal,
    string SteerNode, string SpinNode, string RimNode, string TireNode,
    double ModelTireRadiusCells, double ModelRimRadiusCells)
{
    public const double DefaultTireRadiusCells = 1.5;
    public const double DefaultRimRadiusCells = 1.0;

    /// <summary>Минимальный зазор между краем диска и шиной, клеток: <c>rimRadius ≤ tireRadius − MinRimGapCells</c>.</summary>
    public const double MinRimGapCells = 0.25;

    public const double DefaultMaxSteerDegrees = 60;
    public const double DefaultSteerRateDegrees = 180;

    public static WheelSettings From(FunctionalBlockComponent definition) => From(definition, ParameterSet.Empty);

    public static WheelSettings From(FunctionalBlockComponent definition, ParameterSet parameters)
    {
        double tire = Math.Max(parameters.GetFloat("tireRadius", DefaultTireRadiusCells), 0.3);
        double rim = Math.Clamp(parameters.GetFloat("rimRadius", DefaultRimRadiusCells), 0.05, tire - MinRimGapCells);

        return new WheelSettings(
            tire, rim, parameters.GetBool("reverse"),
            Math.Max(0, ReadNumber(definition, "maxSteerDegrees", DefaultMaxSteerDegrees)) * Math.PI / 180,
            Math.Max(1, ReadNumber(definition, "steerRateDegrees", DefaultSteerRateDegrees)) * Math.PI / 180,
            Name(definition, "steerSignal", "steer"), Name(definition, "brakeSignal", "brake"), Name(definition, "fullBrakeSignal", "full_brake"),
            Name(definition, "steerNode", "Steer"), Name(definition, "spinNode", "Spin"), Name(definition, "rimNode", "Rim"), Name(definition, "tireNode", "Tire"),
            Math.Max(0.1, ReadNumber(definition, "modelTireRadius", DefaultTireRadiusCells)), Math.Max(0.05, ReadNumber(definition, "modelRimRadius", DefaultRimRadiusCells)));
    }

    private static string Name(FunctionalBlockComponent definition, string key, string fallback)
    {
        string value = definition.GetParam(key, fallback).Trim();
        return value.Length == 0 ? fallback : value;
    }

    private static double ReadNumber(FunctionalBlockComponent definition, string key, double fallback) =>
        double.TryParse(definition.GetParam(key, ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : fallback;
}

/// <summary>
/// Состояние одного колеса (<see cref="WheelBehavior"/>). Рантайм только ВЕДЁТ его: руль плавно догоняет сигнал, обороты берутся из сети вращения, угол вращения копится;
/// лучи, пружина и силы трения считает физика тела (<c>World.WheelPhysics</c>) и возвращает сюда единственное: обороты свободно катящегося колеса.
/// <para/>
/// <b>Направления.</b> Ось колеса — локальная X блока, вертикаль руля — локальная Y. Положительный <see cref="Omega"/> — колесо катится в сторону «вперёд колеса» =
/// <c>ось × нормаль</c> (для неповёрнутого блока на ровной земле это +Z блока): поворот блока меняет сторону, а <c>reverse</c> обращает знак оборотов сети. Положительный
/// <see cref="SteerAngle"/> — поворот вокруг локальной Y блока на положительный угол (нос колеса уходит к +X для колеса, катящегося по +Z).
/// </summary>
public sealed class WheelState : BlockState
{
    /// <summary>Мелкие обороты сети (рад/с) ниже этого значения считаются «сеть стоит»: колесо не ведомое и катится свободно.</summary>
    public const double DrivenEpsilon = 1e-4;

    // ---- константы экземпляра (создаются вместе с состоянием; смена параметров пересоздаёт состояние)
    public required WheelSettings Settings { get; init; }

    /// <summary>Внешний радиус шины, м.</summary>
    public double TireRadius => Settings.TireRadiusCells * BuildSpace.CellSize;

    /// <summary>Радиус диска, м (на нём колесо стоит, когда шина пробита).</summary>
    public double RimRadius => Settings.RimRadiusCells * BuildSpace.CellSize;

    /// <summary>Радиус, которым колесо касается земли сейчас: шина или (пробита) диск.</summary>
    public double ContactRadius => TireBroken ? RimRadius : TireRadius;

    // ---- входы с проводов
    /// <summary>Сигнал руля, -1..1 (знак — сторона).</summary>
    public double SteerInput { get; set; }

    /// <summary>Сила торможения, 0..1.</summary>
    public double BrakeInput { get; set; }

    /// <summary>Полный тормоз: колесо заблокировано (обороты 0), трение скольжения вместо качения.</summary>
    public bool FullBrake { get; set; }

    // ---- сеть вращения (выставляет рантайм в конце тика)
    public bool InNetwork { get; set; }

    /// <summary>Угловая скорость сети на этом колесе, рад/с, с учётом <c>reverse</c>.</summary>
    public double NetworkOmega { get; set; }

    /// <summary>Колесо тянет сеть вращения: ось цела, оно соединено с сетью, и сеть вращается. Иначе колесо катится свободно.</summary>
    public bool Driven => InNetwork && !AxleBroken && Math.Abs(NetworkOmega) > DrivenEpsilon;

    // ---- то, что меняется и реплицируется (CaptureNet)
    /// <summary>Угловая скорость колеса, рад/с.</summary>
    public double Omega { get; set; }

    /// <summary>Накопленный угол вращения шины вокруг оси, рад (-π..π].</summary>
    public double SpinAngle { get; set; }

    /// <summary>Текущий угол руля, рад (плавно догоняет <see cref="SteerInput"/> × максимальный угол).</summary>
    public double SteerAngle { get; set; }

    /// <summary>Шина пробита: меш шины скрыт, колесо стоит на диске (радиус меньше, трение ниже).</summary>
    public bool TireBroken { get; set; }

    /// <summary>Ось сломана: колесо выпало из сети вращения, но продолжает крутиться по инерции, подстраиваясь под скорость земли.</summary>
    public bool AxleBroken { get; set; }

    public override double[] CaptureNet() => new[] { Omega, SpinAngle, SteerAngle, TireBroken ? 1.0 : 0.0, AxleBroken ? 1.0 : 0.0 };

    public override void ApplyNet(double[] values)
    {
        Omega = At(values, 0);
        SpinAngle = At(values, 1);
        SteerAngle = At(values, 2);
        TireBroken = At(values, 3) != 0;
        AxleBroken = At(values, 4) != 0;
    }
}

/// <summary>
/// Поведение <c>"Wheel"</c> (колесо, см. Docs/05, «Колесо»): блок-ось с портом вала (Torque In) и тремя нодами: руль (Number, -1..1 → ±<c>maxSteerDegrees</c>), тормоз
/// (Number, 0..1) и полный тормоз (Boolean). Коллизии у колеса нет (только у оси, её задаёт блок), физика — лучи и силы в <c>World.VehicleBody</c>.
/// <para/>
/// <b>Шаг.</b> Руль догоняет цель со скоростью <c>steerRateDegrees</c>. Обороты: полный тормоз — 0; ведомое колесо (<see cref="WheelState.Driven"/>) — обороты сети ×
/// (1 − тормоз); иначе не трогаются (свободное колесо подстраивает под скорость земли физика). Угол вращения копит <c>ω·dt</c>.
/// Входные ноды находятся по ИМЕНАМ (<see cref="WheelSettings"/>): два Number-входа различить по типу нельзя.
/// </summary>
public sealed class WheelBehavior : BlockBehavior<WheelState>
{
    public const string Key = "Wheel";

    protected override WheelState CreateState(FunctionalBlockComponent definition, ParameterSet parameters) => new() { Settings = WheelSettings.From(definition, parameters) };

    protected override void SetInput(WheelState state, string nodeId, NodeValue value)
    {
        if (nodeId == state.Settings.SteerSignal) state.SteerInput = Math.Clamp(value.Number, -1.0, 1.0);
        else if (nodeId == state.Settings.BrakeSignal) state.BrakeInput = Math.Clamp(value.Number, 0.0, 1.0);
        else if (nodeId == state.Settings.FullBrakeSignal) state.FullBrake = value.IsOn;
    }

    protected override void SetTorqueNetwork(WheelState state, bool connected, double rpm)
    {
        state.InNetwork = connected;
        state.NetworkOmega = connected ? rpm * (2 * Math.PI / 60) * (state.Settings.Reverse ? -1 : 1) : 0;
    }

    protected override void Tick(WheelState state, double delta)
    {
        double steerTarget = state.SteerInput * state.Settings.MaxSteerRadians;
        double steerStep = state.Settings.SteerRateRadians * delta;
        state.SteerAngle += Math.Clamp(steerTarget - state.SteerAngle, -steerStep, steerStep);

        if (state.FullBrake) state.Omega = 0;
        else if (state.Driven) state.Omega = state.NetworkOmega * (1 - state.BrakeInput);

        state.SpinAngle = Wrap(state.SpinAngle + state.Omega * delta);
    }

    /// <summary>Угол в диапазон (-π, π].</summary>
    public static double Wrap(double angle) => angle - 2 * Math.PI * Math.Floor((angle + Math.PI) / (2 * Math.PI));
}
