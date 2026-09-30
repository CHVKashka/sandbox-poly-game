using System.Collections.Generic;

namespace SandboxPolyGame.Core;

/// <summary>
/// Серверная (авторитативная) сессия совместного редактирования на одном верстаке — см. <see cref="NetHub"/> и
/// Docs/05-world-and-vehicle-systems.md, «Мультиплеер», вариант Б. Существует только на сервере (listen-server —
/// это тот же процесс, что и хост-игрок); <see cref="Construction"/> тут — не привязанная ни к какой сцене копия
/// (тот же класс, что использует <c>Editor.VoxelWorld</c>, но без рендера), собранная ради вычислений/валидации,
/// а не показа на экране.
/// </summary>
public sealed class WorkbenchSession
{
    public string Name { get; }

    /// <summary>Кто создал сессию (Create vehicle/Open) — только админ может принимать Join-заявки и закрывать
    /// сессию (<see cref="NetHub.RequestCloseSession"/>).</summary>
    public long AdminPeerId { get; }

    /// <summary>Все, кто присоединился ПОСЛЕ админа (Join to workbench, принято админом) — сам админ сюда не входит,
    /// см. <see cref="AdminPeerId"/>.</summary>
    public List<long> Participants { get; } = new();

    public Construction Construction { get; }

    /// <summary>Общая на сессию история Undo/Redo (см. <see cref="UndoHistory"/> class doc про авторство записей и
    /// Docs/05-world-and-vehicle-systems.md, «Мультиплеер») — сервер снимает снэпшот до/после каждой применённой
    /// правки (<see cref="NetHub"/>), помечая его автором того, кто её запросил.</summary>
    public UndoHistory UndoHistory { get; } = new();

    public WorkbenchSession(string name, long adminPeerId, Construction construction)
    {
        Name = name;
        AdminPeerId = adminPeerId;
        Construction = construction;
    }
}
