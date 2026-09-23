using System.Collections.Generic;
using Godot;

namespace SandboxPolyGame.Editor.Ui;

/// <summary>
/// Пользовательские сохранённые цвета инструмента Paint. Хранятся отдельно от 12 базовых цветов палитры
/// (см. <see cref="ToolbarUi"/>) и переживают перезапуск игры — простой список HEX-строк в <c>ConfigFile</c>.
/// </summary>
public static class CustomPalette
{
    private const string SectionName = "palette";
    private const string KeyName = "colors";

    public static List<Color> Load(string path)
    {
        var result = new List<Color>();
        var cfg = new ConfigFile();
        if (cfg.Load(path) != Error.Ok) return result;

        if (cfg.GetValue(SectionName, KeyName, new Godot.Collections.Array()).AsGodotArray<string>() is { } hexes)
        {
            foreach (var hex in hexes)
            {
                if (string.IsNullOrEmpty(hex)) continue;
                try { result.Add(Color.FromHtml(hex)); }
                catch { /* повреждённая строка в файле — пропускаем, не валим загрузку */ }
            }
        }

        return result;
    }

    public static Error Save(string path, IEnumerable<Color> colors)
    {
        var cfg = new ConfigFile();
        var hexes = new Godot.Collections.Array();
        foreach (var color in colors) hexes.Add("#" + color.ToHtml(false));

        cfg.SetValue(SectionName, KeyName, hexes);
        return cfg.Save(path);
    }
}
