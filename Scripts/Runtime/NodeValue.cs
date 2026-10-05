namespace SandboxPolyGame.Runtime;

/// <summary>
/// Значение на логической ноде в рантайме — одно число на все <see cref="Blocks.NodeType"/> (Stormworks-подход: булев
/// сигнал — это просто 0/1): <see cref="Blocks.NodeType.Boolean"/> = 0 (выкл) или 1 (вкл), <see cref="Blocks.NodeType.Number"/> —
/// любое, <see cref="Blocks.NodeType.Electricity"/> — величина питания. Один тип вместо нескольких позволяет будущим
/// логическим проводам обращаться с любой нодой одинаково.
/// </summary>
public readonly record struct NodeValue(double Number)
{
    public static readonly NodeValue Off = new(0);
    public static readonly NodeValue On = new(1);

    /// <summary>Булево прочтение: любое ненулевое значение — "вкл".</summary>
    public bool IsOn => Number != 0;

    public static NodeValue FromBool(bool on) => on ? On : Off;
}
