using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Runtime;

/// <summary>Что рантайм сообщает потребителю электричества каждый тик (см. <see cref="IBlockBehavior.SetPower"/>).</summary>
/// <param name="Energized">В сети питания этого входа есть источник с зарядом (или включено отладочное питание F2) — «питание есть».</param>
/// <param name="Ratio">Какую долю запрошенной мощности (<see cref="IBlockBehavior.GetPowerDemand"/>) сеть реально отдала: 1 — полностью, 0 — ничего
/// (источники пусты или не подключены). Потребитель без запроса получает 1 при <paramref name="Energized"/> и 0 иначе.</param>
public readonly record struct PowerReport(bool Energized, double Ratio)
{
    public static readonly PowerReport None = new(false, 0);
}

/// <summary>
/// Поведение функционального блока в рантайме — общий интерфейс для всех <see cref="FunctionalBlockComponent.Behavior"/>
/// ("Button", "ElectricMotor", "Battery", "PilotSeat"). Реализация БЕЗ собственного состояния: один экземпляр поведения обслуживает все блоки
/// своего вида, а изменяемые данные каждого блока лежат в <see cref="BlockState"/>, который поведение создаёт (<see cref="CreateState"/>) и
/// получает обратно в каждом вызове. Константы типа блока (режим кнопки по умолчанию, высота глаз у сиденья) поведение читает из определения
/// (<see cref="FunctionalBlockComponent.GetParam"/>), НАСТРАИВАЕМЫЕ игроком значения — из <see cref="ParameterSet"/>. Реализации находятся по
/// ключу через <see cref="BlockBehaviorRegistry"/>, состояниями и проводами управляет <see cref="FunctionalBlockRuntime"/>.
/// <para/>
/// <b>Тик рантайма</b> (порядок вызовов за один шаг): <see cref="SetInput"/> для каждой входной Boolean/Number ноды (значение с провода, иначе 0) →
/// <see cref="GetPowerDemand"/>/<see cref="GetStoredEnergy"/> для решателя сети электричества → <see cref="SetPower"/>/<see cref="DrawEnergy"/>/
/// <see cref="ReceiveEnergy"/> с результатом → <see cref="Tick"/>. Значения выходных нод читаются через <see cref="TryReadNode"/> (провод передаёт
/// значение с задержкой в один тик — так петли в схеме безопасны). Все методы кроме <see cref="CreateState"/> необязательны.
/// </summary>
public interface IBlockBehavior
{
    /// <summary>Создаёт начальное состояние нового экземпляра блока по его определению и настройкам этого экземпляра.</summary>
    BlockState CreateState(FunctionalBlockComponent definition, ParameterSet parameters);

    /// <summary>То же с настройками по умолчанию (схемы Parameters нет) — для тестов и блоков без параметров.</summary>
    BlockState CreateState(FunctionalBlockComponent definition) => CreateState(definition, ParameterSet.Empty);

    /// <summary>Действие над блоком (<see cref="BlockInteraction"/>).</summary>
    void Interact(BlockState state, BlockInteraction interaction) { }

    /// <summary>Рантайм сообщает, запитан ли блок целиком (одна общая отметка; точнее — <see cref="SetPower"/> по каждому входу электричества).
    /// Оставлено для поведений, которым достаточно «есть питание или нет» (кнопка: только подсветка).</summary>
    void SetPowered(BlockState state, bool powered) { }

    /// <summary>Значение входной ноды <paramref name="nodeId"/> (Boolean/Number) на этом тике: с провода, а если провода нет — 0.</summary>
    void SetInput(BlockState state, string nodeId, NodeValue value) { }

    /// <summary>Сколько мощности (единиц заряда в секунду) блок хочет получить на ВХОД электричества <paramref name="nodeId"/> прямо сейчас; 0 — не потребляет.</summary>
    double GetPowerDemand(BlockState state, string nodeId) => 0;

    /// <summary>Решатель сообщает итог по входу электричества <paramref name="nodeId"/> (см. <see cref="PowerReport"/>).</summary>
    void SetPower(BlockState state, string nodeId, PowerReport report) { }

    /// <summary>Сколько энергии (единиц заряда) блок может отдать в сеть через ВЫХОД электричества <paramref name="nodeId"/>; 0 — не источник.</summary>
    double GetStoredEnergy(BlockState state, string nodeId) => 0;

    /// <summary>Источник отдал в сеть <paramref name="energy"/> единиц (решатель вызывает после расчёта сети).</summary>
    void DrawEnergy(BlockState state, string nodeId, double energy) { }

    /// <summary>Потребитель получил в этом тике <paramref name="energy"/> единиц энергии по входу электричества <paramref name="nodeId"/> (= запрошенная мощность × доля
    /// <see cref="PowerReport.Ratio"/> × длина тика). Аккумулятор кладёт её в заряд; остальным (мотору, кнопке) не нужна — они реагируют на <see cref="SetPower"/>.</summary>
    void ReceiveEnergy(BlockState state, string nodeId, double energy) { }

    /// <summary>Шаг симуляции длиной <paramref name="delta"/> секунд (вызывается <see cref="FunctionalBlockRuntime.Tick"/>).</summary>
    void Tick(BlockState state, double delta) { }

    /// <summary>Текущее значение выходной ноды <paramref name="nodeId"/> (<see cref="LogicNode.Id"/>); false — у этого блока нет такой выходной ноды.</summary>
    bool TryReadNode(BlockState state, string nodeId, out NodeValue value)
    {
        value = NodeValue.Off;
        return false;
    }

    /// <summary>Обороты (об/мин, знак — направление), которые блок ПОДАЁТ на свой физический выход вала; 0 — блок не источник вращения.</summary>
    double GetTorqueRpm(BlockState state) => 0;
}

/// <summary>
/// Удобная база для поведения с конкретным типом состояния <typeparamref name="TState"/>: реализует <see cref="IBlockBehavior"/>, приводя
/// <see cref="BlockState"/> к <typeparamref name="TState"/> один раз здесь, чтобы конкретные поведения работали с типизированным состоянием.
/// </summary>
public abstract class BlockBehavior<TState> : IBlockBehavior where TState : BlockState
{
    protected abstract TState CreateState(FunctionalBlockComponent definition, ParameterSet parameters);
    protected virtual void Interact(TState state, BlockInteraction interaction) { }
    protected virtual void Tick(TState state, double delta) { }
    protected virtual void SetPowered(TState state, bool powered) { }
    protected virtual void SetInput(TState state, string nodeId, NodeValue value) { }
    protected virtual double GetPowerDemand(TState state, string nodeId) => 0;
    protected virtual void SetPower(TState state, string nodeId, PowerReport report) { }
    protected virtual double GetStoredEnergy(TState state, string nodeId) => 0;
    protected virtual void DrawEnergy(TState state, string nodeId, double energy) { }
    protected virtual void ReceiveEnergy(TState state, string nodeId, double energy) { }
    protected virtual double GetTorqueRpm(TState state) => 0;

    protected virtual bool TryReadNode(TState state, string nodeId, out NodeValue value)
    {
        value = NodeValue.Off;
        return false;
    }

    BlockState IBlockBehavior.CreateState(FunctionalBlockComponent definition, ParameterSet parameters) => CreateState(definition, parameters);
    void IBlockBehavior.Interact(BlockState state, BlockInteraction interaction) => Interact((TState)state, interaction);
    void IBlockBehavior.Tick(BlockState state, double delta) => Tick((TState)state, delta);
    void IBlockBehavior.SetPowered(BlockState state, bool powered) => SetPowered((TState)state, powered);
    void IBlockBehavior.SetInput(BlockState state, string nodeId, NodeValue value) => SetInput((TState)state, nodeId, value);
    double IBlockBehavior.GetPowerDemand(BlockState state, string nodeId) => GetPowerDemand((TState)state, nodeId);
    void IBlockBehavior.SetPower(BlockState state, string nodeId, PowerReport report) => SetPower((TState)state, nodeId, report);
    double IBlockBehavior.GetStoredEnergy(BlockState state, string nodeId) => GetStoredEnergy((TState)state, nodeId);
    void IBlockBehavior.DrawEnergy(BlockState state, string nodeId, double energy) => DrawEnergy((TState)state, nodeId, energy);
    void IBlockBehavior.ReceiveEnergy(BlockState state, string nodeId, double energy) => ReceiveEnergy((TState)state, nodeId, energy);
    double IBlockBehavior.GetTorqueRpm(BlockState state) => GetTorqueRpm((TState)state);
    bool IBlockBehavior.TryReadNode(BlockState state, string nodeId, out NodeValue value) => TryReadNode((TState)state, nodeId, out value);
}
