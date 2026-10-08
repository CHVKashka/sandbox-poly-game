using System;
using System.Linq;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Runtime;

/// <summary>Состояние одного аккумулятора (<see cref="BatteryBehavior"/>).</summary>
public sealed class BatteryState : BlockState
{
    /// <summary>Ёмкость (<see cref="FunctionalBlockComponent.Capacity"/>), единиц заряда.</summary>
    public double Capacity { get; init; }

    /// <summary>Текущий заряд, 0..<see cref="Capacity"/>.</summary>
    public double Charge { get; set; }

    /// <summary>Максимальная скорость зарядки от внешнего источника, единиц в секунду (параметр <c>chargeRate</c>).</summary>
    public double ChargeRate { get; init; }

    /// <summary>true — нода уровня отдаёт долю 0..1, false — абсолютный заряд в единицах (параметр <c>levelOutput</c>).</summary>
    public bool LevelAsFraction { get; init; }

    /// <summary>Id выходной Number-ноды уровня заряда; null — в блоке такой ноды нет.</summary>
    public string? LevelNodeId { get; init; }

    /// <summary>Id входа электричества (зарядка); null — аккумулятор не заряжается извне.</summary>
    public string? ChargeNodeId { get; init; }

    public double Fraction => Capacity > 0 ? Charge / Capacity : 0;

    public override double[] CaptureNet() => new[] { Charge };
    public override void ApplyNet(double[] values) => Charge = At(values, 0);
}

/// <summary>
/// Поведение <c>"Battery"</c> (<c>blocks/battery_small.xml</c>): хранит заряд, отдаёт его в сеть электричества через свои ВЫХОДЫ электричества и
/// выводит уровень на выходную Number-ноду. Заряд убывает, когда подключённые потребители (мотор) берут мощность (<see cref="DrawEnergy"/>), и растёт,
/// когда на ВХОД электричества приходит энергия от другого источника (<see cref="ReceiveEnergy"/>, не быстрее <c>chargeRate</c>). Ёмкость —
/// <see cref="FunctionalBlockComponent.Capacity"/> блока. Параметры («Parameters»): <c>initialCharge</c> (доля ёмкости при спавне, по умолчанию 1),
/// <c>chargeRate</c>, <c>levelOutput</c> (<c>absolute</c> — заряд в единицах, <c>fraction</c> — доля 0..1). Ноды — по типу и направлению.
/// </summary>
public sealed class BatteryBehavior : BlockBehavior<BatteryState>
{
    public const string Key = "Battery";
    public const double DefaultChargeRate = 20;

    protected override BatteryState CreateState(FunctionalBlockComponent definition, ParameterSet parameters)
    {
        double capacity = Math.Max(0, definition.Capacity);
        return new BatteryState
        {
            Capacity = capacity,
            Charge = capacity * Math.Clamp(parameters.GetFloat("initialCharge", 1), 0, 1),
            ChargeRate = Math.Max(0, parameters.GetFloat("chargeRate", DefaultChargeRate)),
            LevelAsFraction = parameters.GetString("levelOutput", "absolute") == "fraction",
            LevelNodeId = definition.Nodes.FirstOrDefault(n => n.Direction == PortDirection.Out && n.Type == NodeType.Number)?.Id,
            ChargeNodeId = definition.Nodes.FirstOrDefault(n => n.Direction == PortDirection.In && n.Type == NodeType.Electricity)?.Id,
        };
    }

    // Все ВЫХОДЫ электричества блока отдают один и тот же общий заряд; решатель вызывает метод для каждой подключённой выходной ноды отдельно,
    // поэтому одна батарея на несколько проводов не «умножает» энергию — см. FunctionalBlockRuntime (источник учитывается один раз на сеть).
    protected override double GetStoredEnergy(BatteryState state, string nodeId) => state.Charge;

    protected override void DrawEnergy(BatteryState state, string nodeId, double energy) =>
        state.Charge = Math.Max(0, state.Charge - energy);

    protected override double GetPowerDemand(BatteryState state, string nodeId) =>
        nodeId == state.ChargeNodeId && state.Charge < state.Capacity ? state.ChargeRate : 0;

    protected override void ReceiveEnergy(BatteryState state, string nodeId, double energy)
    {
        if (nodeId == state.ChargeNodeId) state.Charge = Math.Min(state.Capacity, state.Charge + energy);
    }

    protected override bool TryReadNode(BatteryState state, string nodeId, out NodeValue value)
    {
        if (state.LevelNodeId != null && nodeId == state.LevelNodeId)
        {
            value = new NodeValue(state.LevelAsFraction ? state.Fraction : state.Charge);
            return true;
        }

        value = NodeValue.Off;
        return false;
    }
}
