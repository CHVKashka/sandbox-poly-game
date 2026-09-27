using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Editor;

/// <summary>
/// История отмены/повтора (Ctrl+Z/Ctrl+Y) для <see cref="Construction"/>. Снэпшот-based: каждый шаг — сериализованное
/// (см. <see cref="ConstructionIO"/>) состояние ВСЕЙ постройки целиком, а не набор обратных операций по типам
/// действий (Place/Remove/Paint/TrySetSize) — заметно проще и надёжнее (общий путь для любого действия, включая
/// будущие), а для построек ожидаемого в редакторе размера (сотни-тысячи блоков) сериализация в JSON достаточно
/// быстрая, чтобы не быть узким местом на каждый шаг истории.
///
/// <see cref="ConstructionIO.Serialize"/> хранит только "представительный" цвет каждого <c>BlockInstance</c> (одно
/// поле <c>Color</c>) — точечная покраска ГРАНЕЙ (<see cref="VoxelGrid.TryPaintFace"/>) в него не попадает, а
/// перестройка постройки через <see cref="ConstructionIO.Deserialize"/> заново красит все 6 граней каждой клетки
/// в этот представительный цвет. Поэтому снэпшот здесь — не просто JSON, а <see cref="Snapshot"/> (JSON + отдельный
/// дамп per-face цветов всех клеток, принадлежащих экземплярам Construction) — иначе Undo/Redo стирали бы точечную
/// покраску граней. Клетки, залитые в обход Construction (см. <c>Dev.DemoBuilds</c>), этим не покрыты — как и
/// раньше, история вообще не знает про них.
/// </summary>
public sealed class UndoHistory
{
    private const int MaxDepth = 100;

    /// <summary>Состояние постройки на момент снятия снэпшота: <see cref="Json"/> — экземпляры (см.
    /// <see cref="ConstructionIO.Serialize"/>), <see cref="Faces"/> — per-face цвета их клеток (см.
    /// <see cref="UndoHistory"/> class doc).</summary>
    public readonly record struct Snapshot(string Json, string Faces);

    private readonly List<Snapshot> _undoStack = new();
    private readonly List<Snapshot> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>Снимает снэпшот текущего состояния постройки (JSON + per-face цвета) — вызывается ДО действия;
    /// результат передаётся в <see cref="RecordIfChanged"/> ПОСЛЕ него.</summary>
    public Snapshot Capture(Construction construction) => new(ConstructionIO.Serialize(construction), SerializeFaceColors(construction));

    /// <summary>
    /// Если состояние правда изменилось с момента <paramref name="before"/> (иначе это была бы пустая запись в
    /// истории — например, ЛКМ в занятую клетку, или покраска грани в уже стоящий там цвет), кладёт снэпшот "до" в
    /// стек отмены и стирает стек повтора (как в любом редакторе: новое действие после отмены обрывает старую
    /// "будущую" ветку истории).
    /// </summary>
    public void RecordIfChanged(Snapshot before, Construction construction)
    {
        var current = Capture(construction);
        if (before.Json == current.Json && before.Faces == current.Faces) return;

        _undoStack.Add(before);
        if (_undoStack.Count > MaxDepth) _undoStack.RemoveAt(0);
        _redoStack.Clear();
    }

    public bool Undo(Construction construction, BlockCatalog catalog)
    {
        if (!CanUndo) return false;

        var current = Capture(construction);
        var previous = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _redoStack.Add(current);

        Restore(construction, catalog, previous);
        return true;
    }

    public bool Redo(Construction construction, BlockCatalog catalog)
    {
        if (!CanRedo) return false;

        var current = Capture(construction);
        var next = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _undoStack.Add(current);

        Restore(construction, catalog, next);
        return true;
    }

    private static void Restore(Construction construction, BlockCatalog catalog, Snapshot snapshot)
    {
        ConstructionIO.Deserialize(construction, snapshot.Json, catalog);
        RestoreFaceColors(construction, snapshot.Faces);
    }

    /// <summary>Дамп per-face цветов (см. class doc) — по строке на клетку, принадлежащую экземпляру Construction:
    /// <c>x,y,z:c0,c1,c2,c3,c4,c5;</c> (6 цветов в порядке <c>axis*2+(positive?1:0)</c>, см. <see cref="VoxelChunk"/>).</summary>
    private static string SerializeFaceColors(Construction construction)
    {
        var grid = construction.Grid;
        var sb = new StringBuilder();
        foreach (var instance in construction.Instances)
        foreach (var cell in Construction.CellsOf(instance))
        {
            sb.Append(cell.X).Append(',').Append(cell.Y).Append(',').Append(cell.Z).Append(':');
            for (int f = 0; f < 6; f++)
            {
                sb.Append(grid.GetFaceColor(cell, f / 2, f % 2 == 1));
                sb.Append(f < 5 ? ',' : ';');
            }
        }

        return sb.ToString();
    }

    private static void RestoreFaceColors(Construction construction, string dump)
    {
        if (dump.Length == 0) return;

        var grid = construction.Grid;
        foreach (var entry in dump.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = entry.IndexOf(':');
            var coords = entry[..colon].Split(',');
            var colors = entry[(colon + 1)..].Split(',');
            var cell = new Vector3I(int.Parse(coords[0]), int.Parse(coords[1]), int.Parse(coords[2]));

            for (int f = 0; f < 6; f++)
            {
                grid.TryPaintFace(cell, f / 2, f % 2 == 1, uint.Parse(colors[f]));
            }
        }
    }
}
