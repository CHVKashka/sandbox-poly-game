using System.Collections.Generic;

namespace SandboxPolyGame.Core;

/// <summary>
/// Серверная (авторитативная) сессия совместного редактирования одного верстака — см. <see cref="NetHub"/> и
/// Docs/05-world-and-vehicle-systems.md, «Мультиплеер», вариант Б. Существует только на сервере (listen-server —
/// это тот же процесс, что и хост-игрок); <see cref="Construction"/> тут — не привязанная ни к какой сцене копия
/// (тот же класс, что использует <c>Editor.VoxelWorld</c>, но без рендера), собранная ради вычислений/валидации,
/// а не показа на экране.
/// <para/>
/// На ОДНОМ физическом верстаке (<see cref="WorkbenchName"/>) одновременно может идти НЕСКОЛЬКО таких сессий —
/// каждый вызов Create vehicle/Open начинает свою, независимую от чужих (см. <see cref="NetHub"/>, "ServerOpenSession"
/// — раньше тут был лимит "одна сессия на верстак", убран по запросу пользователя). Поэтому объект сессии хранится
/// в словаре <c>NetHub._sessions</c> не по <see cref="WorkbenchName"/>, а по отдельному уникальному ключу
/// (строится из имени верстака + <see cref="Ordinal"/>, см. <c>NetHub.ServerOpenSession</c>) — <see cref="Ordinal"/>
/// нужен ровно затем, чтобы <c>NetHub.ServerJoin</c> мог выбрать САМУЮ НОВУЮ сессию данного верстака, когда их
/// несколько одновременно (простой детерминированный выбор, не полноценный выбор "к кому именно присоединиться" —
/// см. ROADMAP).
/// </summary>
public sealed class WorkbenchSession
{
    /// <summary>Имя (<c>Node.Name</c>) физического верстака, через который создана эта сессия — НЕ уникальный ключ
    /// сессии (см. class doc): к одному и тому же верстаку может относиться сразу несколько активных сессий.</summary>
    public string WorkbenchName { get; }

    /// <summary>Порядковый номер этой сессии среди ВСЕХ когда-либо открытых за время жизни процесса (не только на
    /// этом верстаке) — монотонно растёт, используется только для выбора "самой новой" при Join (см. class doc).</summary>
    public int Ordinal { get; }

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

    public WorkbenchSession(string workbenchName, int ordinal, long adminPeerId, Construction construction)
    {
        WorkbenchName = workbenchName;
        Ordinal = ordinal;
        AdminPeerId = adminPeerId;
        Construction = construction;
    }
}
