namespace SandboxPolyGame.Runtime;

/// <summary>Действие игрока (или другого источника) над функциональным блоком — см. <see cref="IBlockBehavior.Interact"/>.
/// Ввод и наведение (кто и когда нажимает) — отдельный слой, здесь только сама семантика.</summary>
public enum BlockInteraction
{
    /// <summary>Начали взаимодействие (нажали кнопку).</summary>
    Press,

    /// <summary>Закончили взаимодействие (отпустили кнопку).</summary>
    Release,
}
