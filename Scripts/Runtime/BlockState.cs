namespace SandboxPolyGame.Runtime;

/// <summary>
/// Базовый класс изменяемого состояния ОДНОГО экземпляра функционального блока в рантайме (нажата ли кнопка, заряд
/// батареи, обороты мотора...). Состояние хранится СНАРУЖИ данных постройки — в <see cref="FunctionalBlockRuntime"/>,
/// по <see cref="Core.BlockInstance.InstanceId"/>, — а не полем в <see cref="Core.BlockInstance"/>: постройка
/// (<see cref="Core.Construction"/>) остаётся чистым описанием "что где стоит" (сохраняется, реплицируется правками,
/// откатывается Undo), а динамическое состояние живёт только у запущенной постройки. Конкретное состояние создаёт и
/// понимает своё поведение (<see cref="IBlockBehavior.CreateState"/>); остальной код видит только эту базу.
/// </summary>
public abstract class BlockState
{
}
