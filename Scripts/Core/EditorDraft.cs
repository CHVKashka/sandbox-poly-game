using System;
using System.Linq;

namespace SandboxPolyGame.Core;

/// <summary>
/// «Черновик» редактора — постройка, которую игрок редактировал в последний раз: сериализованный JSON (со ВСЕМИ несохранёнными
/// правками) плюс связь с файлом сохранения (куда писать при Save → «перезаписать») и имя/описание. Живёт в
/// <see cref="EditorHandoff.Draft"/> (статическое поле переживает смену сцены), поэтому:
/// <list type="bullet">
/// <item>выход из редактора (Exit/Spawn) запоминает черновик, и верстак предлагает «Continue editing»;</item>
/// <item><c>R</c> по заспавненной постройке кладёт в черновик то, что было заспавнено (<see cref="FromSpawnJson"/>), и открывает редактор.</item>
/// </list>
/// Связь с файлом при возврате по <c>R</c> восстанавливается по <c>createdUtc</c> из метаданных JSON (<see cref="Serialize"/> кладёт их при спавне):
/// по сети путь на диске другого игрока ничего не значит, а время создания файла — уникальный и переносимый признак. Файла нет (удалён,
/// чужой компьютер) — постройка просто считается несохранённой.
/// </summary>
public sealed record EditorDraft(string Json, string? SavedPath, string Name, string Description)
{
    /// <summary>Сериализует постройку вместе с метаданными (<paramref name="name"/>, <paramref name="description"/>, <c>createdUtc</c> файла
    /// <paramref name="savedPath"/>, если он существует) — так спавн-JSON «помнит», чья это постройка (см. <see cref="FromSpawnJson"/>).</summary>
    public static string Serialize(Construction construction, string? savedPath, string name, string description)
    {
        string? created = null;
        if (savedPath != null && Godot.FileAccess.FileExists(savedPath))
        {
            using var file = Godot.FileAccess.Open(savedPath, Godot.FileAccess.ModeFlags.Read);
            if (file != null) created = ConstructionIO.ReadMetadata(file.GetAsText()).CreatedUtc;
        }

        return ConstructionIO.Serialize(construction, name, description, created, DateTime.UtcNow.ToString("O"));
    }

    /// <summary>Черновик из JSON заспавненной постройки: имя/описание — из метаданных, файл сохранения — тот, чьё время создания совпало
    /// (null, если постройка никогда не сохранялась или файл пропал).</summary>
    public static EditorDraft FromSpawnJson(string json)
    {
        var meta = ConstructionIO.ReadMetadata(json);
        string? path = null;
        if (!string.IsNullOrEmpty(meta.CreatedUtc))
        {
            var found = ConstructionStorage.List().FirstOrDefault(c => c.CreatedUtc == meta.CreatedUtc);
            path = found.Path; // default(record struct) -> Path == null
        }

        return new EditorDraft(json, path, meta.Name ?? "", meta.Description ?? "");
    }
}
