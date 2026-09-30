using Godot;

namespace SandboxPolyGame.Core;

/// <summary>
/// Данные, которые нужно передать между сценами <c>World.GameWorld</c>/<c>Editor.BuildEditor</c> при переходе
/// через верстак. Обычная смена сцены (<c>SceneTree.ChangeSceneToFile</c>) не сохраняет узлы старой сцены — а
/// Godot-автозагрузка (autoload-синглтон в project.godot) тут не нужна: обычные статические поля C# переживают
/// смену сцены точно так же (процесс не перезапускается, сборка не перезагружается), пока никто их явно не
/// сбросит. Как только мир перестанет быть "плоской плиткой с одним верстаком" (сохранение позиции игрока,
/// несколько верстаков и т.п.) — это то место, где такое состояние естественно вырастет в нечто большее.
/// </summary>
public static class EditorHandoff
{
    /// <summary>Путь к файлу постройки, которую нужно загрузить при входе в <c>BuildEditor</c> — null означает
    /// "Create vehicle" (пустой редактор с корневым блоком, как и раньше). Сбрасывается в null самим редактором
    /// сразу после прочтения — переживать больше одного перехода в редактор ему не нужно.</summary>
    public static string? PendingConstructionPath;

    /// <summary>Сериализованная (<c>ConstructionIO.Serialize</c>) постройка, которую нужно материализовать как
    /// физическое тело при возврате в мир — задаётся кнопкой Spawn в редакторе, читается и сбрасывается
    /// <c>World.GameWorld</c> при следующем входе в мир.</summary>
    public static string? PendingSpawnJson;

    /// <summary>Имя (<c>Node.Name</c>) верстака, через который вошли в редактор — читается вместе с
    /// <see cref="PendingSpawnJson"/>, чтобы Spawn поставил постройку в зону именно ЭТОГО верстака (см.
    /// <c>World.Workbench.SpawnArea</c>), а не первого попавшегося. Задаётся при открытии меню верстака,
    /// не только при Spawn — не помешает, если вместо Spawn нажали Exit (то же поле сбросится не читая).</summary>
    public static string? PendingSpawnWorkbenchName;

    /// <summary>Мировая позиция/поворот игрока в момент входа в редактор — при возврате (Exit или Spawn) игрок
    /// должен оказаться там же, откуда вошёл, а не на дефолтной точке спавна мира (см. <c>World.GameWorld</c>).
    /// null — самый первый вход в мир (ещё не был в редакторе ни разу), тогда используется точка спавна по умолчанию.</summary>
    public static Vector3? PendingPlayerPosition;

    public static double PendingPlayerYaw;

    /// <summary>Не null — вход в редактор ведёт в СЕТЕВУЮ сессию совместного редактирования этого верстака (см.
    /// <c>Core.NetHub</c>/Docs/05-world-and-vehicle-systems.md, «Мультиплеер»), а не в обычное одиночное
    /// редактирование: <c>Editor.BuildEditor</c> шлёт правки через <c>NetHub.RequestEdit</c> вместо прямой мутации
    /// своей <see cref="Construction"/> и подписывается на <c>NetHub.EditApplied</c>. null — обычная одиночная игра,
    /// поведение не меняется.</summary>
    public static string? NetworkedWorkbenchName;

    /// <summary>Сериализованная постройка сетевой сессии на момент входа (от <c>NetHub.SessionReadyForMe</c> или
    /// <c>JoinAcceptedForMe</c>) — читается вместо <see cref="PendingConstructionPath"/>, когда
    /// <see cref="NetworkedWorkbenchName"/> задан.</summary>
    public static string? PendingNetworkedConstructionJson;

    /// <summary>Я создал(а) эту сетевую сессию (Create vehicle/Open), а не присоединился(лась) к чужой (Join) — только
    /// админ может завершить сессию при Exit/Spawn (<c>NetHub.RequestCloseSession</c>); участник просто тихо
    /// выходит, не трогая сессию остальных (см. <c>Editor.BuildEditor</c>).</summary>
    public static bool IsSessionAdmin;
}
