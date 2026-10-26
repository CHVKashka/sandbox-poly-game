using System;
using System.Linq;
using Godot;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Runtime;

/// <summary>Состояние одного электромотора (<see cref="ElectricMotorBehavior"/>).</summary>
public sealed class ElectricMotorState : BlockState
{
    /// <summary>Id входной Number-ноды управления (-1..1: знак — направление, модуль — «газ»); null — ноды в блоке нет, мотор не управляется.</summary>
    public string? SignalNodeId { get; init; }

    /// <summary>Id входа электричества; null — в блоке нет такой ноды, мотор не потребляет и не крутится.</summary>
    public string? ElectricityNodeId { get; init; }

    /// <summary>Мощность на полном газу, единиц заряда в секунду (параметр <c>maxPower</c>).</summary>
    public double MaxPower { get; init; }

    /// <summary>Обороты на полном газу при полном питании, об/мин (параметр <c>maxRpm</c>).</summary>
    public double MaxRpm { get; init; }

    /// <summary>Сигнал управления, зажатый в -1..1 (см. <see cref="SignalNodeId"/>).</summary>
    public double Signal { get; set; }

    /// <summary>Доля запрошенной мощности, которую сеть отдала в последнем тике (0, если питания нет).</summary>
    public double PowerRatio { get; set; }

    /// <summary>Текущие обороты (об/мин, со знаком) — плавно догоняют целевые.</summary>
    public double Rpm { get; set; }

    public override double[] CaptureNet() => new[] { Signal, PowerRatio, Rpm };

    public override void ApplyNet(double[] values)
    {
        Signal = At(values, 0);
        PowerRatio = At(values, 1);
        Rpm = At(values, 2);
    }
}

/// <summary>
/// Поведение <c>"ElectricMotor"</c> (<c>blocks/electric_motor_small.xml</c>): потребляет электричество, расходуя заряд аккумуляторов сети, и
/// крутит свой физический выход вала. Управляется числом на входной Number-ноде (-1..1). Мощность запроса = <c>maxPower</c> × |сигнал|; целевые обороты =
/// сигнал × <c>maxRpm</c> × доля реально полученной мощности (разряженные/не подключённые аккумуляторы — мотор останавливается). Обороты плавно
/// догоняют цель (разгон за ~0.7 с), а потом идут по валам (<see cref="TorqueNetwork"/>). Настраивается инструментом «Parameters»: <c>maxPower</c>, <c>maxRpm</c>.
/// Ноды находятся по ТИПУ (первая входная Number и первая входная Electricity), не по имени — имена в XML можно менять.
/// </summary>
public sealed class ElectricMotorBehavior : BlockBehavior<ElectricMotorState>
{
    public const string Key = "ElectricMotor";
    public const double DefaultMaxPower = 0.1;
    public const double DefaultMaxRpm = 3600;

    /// <summary>Разгон/торможение в долях максимальных оборотов в секунду (1.5 — до полных за ~0.67 с).</summary>
    public const double AccelerationFraction = 1.5;

    protected override ElectricMotorState CreateState(FunctionalBlockComponent definition, ParameterSet parameters) => new()
    {
        SignalNodeId = definition.Nodes.FirstOrDefault(n => n.Direction == PortDirection.In && n.Type == NodeType.Number)?.Id,
        ElectricityNodeId = definition.Nodes.FirstOrDefault(n => n.Direction == PortDirection.In && n.Type == NodeType.Electricity)?.Id,
        MaxPower = Math.Max(0, parameters.GetFloat("maxPower", DefaultMaxPower)),
        MaxRpm = Math.Max(0, parameters.GetFloat("maxRpm", DefaultMaxRpm)),
    };

    protected override void SetInput(ElectricMotorState state, string nodeId, NodeValue value)
    {
        if (nodeId == state.SignalNodeId) state.Signal = Math.Clamp(value.Number, -1.0, 1.0);
    }

    protected override double GetPowerDemand(ElectricMotorState state, string nodeId) =>
        nodeId == state.ElectricityNodeId ? state.MaxPower * Math.Abs(state.Signal) : 0;

    protected override void SetPower(ElectricMotorState state, string nodeId, PowerReport report)
    {
        if (nodeId == state.ElectricityNodeId) state.PowerRatio = report.Energized ? report.Ratio : 0;
    }

    protected override void Tick(ElectricMotorState state, double delta)
    {
        double target = state.Signal * state.MaxRpm * state.PowerRatio;
        double step = state.MaxRpm * AccelerationFraction * delta;
        state.Rpm += Math.Clamp(target - state.Rpm, -step, step);
    }

    protected override double GetTorqueRpm(ElectricMotorState state) => state.Rpm;
}
