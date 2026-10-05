using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Runtime;

/// <summary>
/// Поведение функционального блока в рантайме — общий интерфейс для всех <see cref="FunctionalBlockComponent.Behavior"/>
/// ("Button", позже "ElectricMotor" и т.д.). Реализация БЕЗ собственного состояния: один экземпляр поведения
/// обслуживает все блоки своего вида, а изменяемые данные каждого блока лежат в <see cref="BlockState"/>, который
/// поведение создаёт (<see cref="CreateState"/>) и получает обратно в каждом вызове. Параметры блока (например, режим
/// кнопки) поведение читает из определения при создании состояния (<see cref="FunctionalBlockComponent.GetParam"/>).
/// Реализации находятся по ключу через <see cref="BlockBehaviorRegistry"/>, состояниями управляет <see cref="FunctionalBlockRuntime"/>.
/// <para/>
/// Методы кроме <see cref="CreateState"/> необязательны — по умолчанию ничего не делают, поведение переопределяет только то, чем
/// пользуется (кнопке не нужен <see cref="Tick"/>, мотору не нужен <see cref="Interact"/>).
/// </summary>
public interface IBlockBehavior
{
    /// <summary>Создаёт начальное состояние нового экземпляра блока по его определению.</summary>
    BlockState CreateState(FunctionalBlockComponent definition);

    /// <summary>Действие над блоком (<see cref="BlockInteraction"/>).</summary>
    void Interact(BlockState state, BlockInteraction interaction) { }

    /// <summary>Рантайм сообщает, запитан ли блок (каждый тик, перед <see cref="Tick"/>; см. <see cref="FunctionalBlockRuntime"/> —
    /// там же единственное место, откуда берётся питание). Поведения, которым питание не нужно, ничего не делают.</summary>
    void SetPowered(BlockState state, bool powered) { }

    /// <summary>Шаг симуляции длиной <paramref name="delta"/> секунд (вызывается <see cref="FunctionalBlockRuntime.Tick"/>).</summary>
    void Tick(BlockState state, double delta) { }

    /// <summary>Текущее значение выходной ноды <paramref name="nodeId"/>
    /// (<see cref="LogicNode.Id"/>); false — у этого блока нет такой выходной ноды.</summary>
    bool TryReadNode(BlockState state, string nodeId, out NodeValue value)
    {
        value = NodeValue.Off;
        return false;
    }
}

/// <summary>
/// Удобная база для поведения с конкретным типом состояния <typeparamref name="TState"/>: реализует
/// <see cref="IBlockBehavior"/>, приводя <see cref="BlockState"/> к <typeparamref name="TState"/> один раз здесь, чтобы
/// конкретные поведения работали с типизированным состоянием без собственных приведений.
/// </summary>
public abstract class BlockBehavior<TState> : IBlockBehavior where TState : BlockState
{
    protected abstract TState CreateState(FunctionalBlockComponent definition);
    protected virtual void Interact(TState state, BlockInteraction interaction) { }
    protected virtual void Tick(TState state, double delta) { }
    protected virtual void SetPowered(TState state, bool powered) { }

    protected virtual bool TryReadNode(TState state, string nodeId, out NodeValue value)
    {
        value = NodeValue.Off;
        return false;
    }

    BlockState IBlockBehavior.CreateState(FunctionalBlockComponent definition) => CreateState(definition);
    void IBlockBehavior.Interact(BlockState state, BlockInteraction interaction) => Interact((TState)state, interaction);
    void IBlockBehavior.Tick(BlockState state, double delta) => Tick((TState)state, delta);
    void IBlockBehavior.SetPowered(BlockState state, bool powered) => SetPowered((TState)state, powered);
    bool IBlockBehavior.TryReadNode(BlockState state, string nodeId, out NodeValue value) => TryReadNode((TState)state, nodeId, out value);
}
