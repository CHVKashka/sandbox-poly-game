using System.Collections.Generic;

namespace SandboxPolyGame.Runtime;

/// <summary>
/// Реестр реализаций <see cref="IBlockBehavior"/> по ключу <see cref="Blocks.FunctionalBlockComponent.Behavior"/> —
/// тот же приём, что и <c>BlockCatalog.CreateComponent</c> для компонентов: новое поведение = новый класс + одна
/// строка здесь. Блок с ключом, которого тут нет (поведение ещё не написано), не получает состояния и рантаймом
/// не затрагивается.
/// </summary>
public static class BlockBehaviorRegistry
{
    private static readonly Dictionary<string, IBlockBehavior> Behaviors = new()
    {
        [ButtonBehavior.Key] = new ButtonBehavior(),
    };

    public static bool TryGet(string key, out IBlockBehavior behavior) => Behaviors.TryGetValue(key, out behavior!);
}
