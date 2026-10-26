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
    /// <summary>
    /// То, что надо передать по сети, чтобы у наблюдателя (<see cref="FunctionalBlockRuntime.IsMirror"/>) состояние выглядело так же, как у сервера: числа в фиксированном порядке
    /// (булевы — 0/1). Только ИЗМЕНЯЮЩЕЕСЯ — константы типа блока и настройки экземпляра у наблюдателя те же (постройка и параметры реплицируются отдельно). Пусто — нечего передавать.
    /// </summary>
    public virtual double[] CaptureNet() => System.Array.Empty<double>();

    /// <summary>Применяет то, что вернул <see cref="CaptureNet"/> на сервере (длина может не совпасть при рассинхроне версий — лишнее/недостающее игнорируется).</summary>
    public virtual void ApplyNet(double[] values) { }

    protected static double At(double[] values, int index) => index < values.Length ? values[index] : 0;
}
