using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

namespace SandboxPolyGame.Core;

/// <summary>
/// Именованные сохранения построек — обёртка над <see cref="ConstructionIO"/> с метаданными (имя, описание, даты
/// создания/изменения) и авто-управляемым каталогом файлов: путь на диске больше не выбирается вручную через
/// <c>FileDialog</c> (см. <c>Editor.Ui.SaveDialogUi</c>/Docs/05-world-and-vehicle-systems.md, «Сохранение построек»,
/// где и обоснование — постройки должны быть видны в окне выбора на верстаке по имени/дате/превью, а не по
/// произвольному пути, который игрок мог сохранить куда угодно).
/// </summary>
public static class ConstructionStorage
{
    // Настоящая папка "Документы" ОС, не user:// (godot-песочница) — так игроку легко найти файлы сам и поделиться
    // с другом, как и попросили.
    private const string SubFolder = "SandboxPolyGame/Constructions";

    /// <summary>Одна запись каталога построек — без загруженных блоков (см. <see cref="List"/>).</summary>
    public readonly record struct SavedConstruction(
        string Name, string? Description, string Path, string? PreviewPath, string CreatedUtc, string ModifiedUtc);

    /// <summary>Каталог сохранений — создаётся при первом обращении, если его ещё нет.</summary>
    public static string Directory
    {
        get
        {
            string documents = OS.GetSystemDir(OS.SystemDir.Documents);
            string dir = documents.Length > 0
                ? $"{documents}/{SubFolder}"
                : ProjectSettings.GlobalizePath($"user://{SubFolder}"); // на случай, если ОС не отдаёт "Документы" (например, часть Linux-окружений)
            if (!DirAccess.DirExistsAbsolute(dir)) DirAccess.MakeDirRecursiveAbsolute(dir);
            return dir;
        }
    }

    /// <summary>Превращает произвольное имя постройки в безопасное имя файла — буквы/цифры/пробел/дефис/подчёркивание,
    /// остальное вырезается; пусто после очистки — "construction".</summary>
    public static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_') sb.Append(c);
        }

        string result = sb.ToString().Trim();
        return result.Length == 0 ? "construction" : result;
    }

    /// <summary>Путь для НОВОГО сохранения с этим именем — если файл с таким именем уже существует (другая
    /// постройка с тем же названием), к имени добавляется " (2)", " (3)" и т.д., чтобы не перезаписать чужое.</summary>
    public static string ResolveNewPath(string name)
    {
        string baseName = SanitizeFileName(name);
        string dir = Directory;
        string candidate = $"{dir}/{baseName}.json";
        for (int suffix = 2; FileAccess.FileExists(candidate); suffix++)
        {
            candidate = $"{dir}/{baseName} ({suffix}).json";
        }

        return candidate;
    }

    public static string PreviewPathFor(string savePath) =>
        savePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? savePath[..^5] + ".png" : savePath + ".png";

    /// <summary>
    /// Сохраняет постройку по указанному пути (новому — см. <see cref="ResolveNewPath"/> — или уже существующему,
    /// для повторного сохранения той же постройки в течение сессии) с метаданными. <c>createdUtc</c> берётся из
    /// уже существующего файла по этому пути, если он есть, — пересохранение не должно менять дату создания,
    /// только дату изменения; при первом сохранении (файла ещё не было) <c>createdUtc</c> и <c>modifiedUtc</c> —
    /// один и тот же момент времени (один вызов <see cref="DateTime.UtcNow"/> на весь метод, не два раздельных).
    /// </summary>
    public static Error Save(Construction construction, string path, string name, string? description)
    {
        string now = DateTime.UtcNow.ToString("O");
        string createdUtc = now;
        if (FileAccess.FileExists(path))
        {
            using var existing = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (existing != null)
            {
                var meta = ConstructionIO.ReadMetadata(existing.GetAsText());
                if (!string.IsNullOrEmpty(meta.CreatedUtc)) createdUtc = meta.CreatedUtc!;
            }
        }

        string json = ConstructionIO.Serialize(construction, name, description, createdUtc, now);

        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (file == null) return FileAccess.GetOpenError();

        file.StoreString(json);
        return Error.Ok;
    }

    /// <summary>
    /// Список всех сохранённых построек в каталоге, отсортированный по дате изменения (сначала новые) — только
    /// метаданные (см. <see cref="ConstructionIO.ReadMetadata"/>), без разбора/размещения блоков: дёшево для окна
    /// выбора построек на верстаке, даже если сохранений много.
    /// </summary>
    public static List<SavedConstruction> List()
    {
        var result = new List<SavedConstruction>();
        string dir = Directory;

        using var access = DirAccess.Open(dir);
        if (access == null) return result;

        access.ListDirBegin();
        for (var fileName = access.GetNext(); fileName != ""; fileName = access.GetNext())
        {
            if (access.CurrentIsDir() || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            string path = $"{dir}/{fileName}";
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) continue;

            var meta = ConstructionIO.ReadMetadata(file.GetAsText());
            string name = string.IsNullOrEmpty(meta.Name) ? fileName[..^5] : meta.Name!;
            string previewPath = PreviewPathFor(path);
            result.Add(new SavedConstruction(
                name, meta.Description, path,
                FileAccess.FileExists(previewPath) ? previewPath : null,
                meta.CreatedUtc ?? "", meta.ModifiedUtc ?? ""));
        }

        access.ListDirEnd();
        return result.OrderByDescending(c => c.ModifiedUtc).ToList();
    }
}
