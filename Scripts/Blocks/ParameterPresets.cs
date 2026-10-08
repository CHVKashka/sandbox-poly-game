using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SandboxPolyGame.Blocks;

/// <summary>
/// Общие наборы настраиваемых параметров, которые блок подключает одной строкой — <c>{ "include": ["ControlAxes"] }</c> в компоненте <see cref="ParametersComponent"/>.
/// Набор объявлен ОДИН раз здесь, поэтому у всех блоков, которые его подключают, настройки одинаковые (имена, границы, умолчания, подписи), и новый блок с управлением
/// (второе сиденье, пульт, штурвал) не копирует восемь параметров в свой XML.
/// <para/>
/// <b>ControlAxes</b> — оси управления игрока: для каждой из четырёх осей (<see cref="ControlAxisIds"/>) режим <c>{ось}_mode</c> (<c>reset</c> — после отпускания клавиши ось
/// плавно возвращается в 0; <c>sticky</c> — остаётся, как «ручка газа») и чувствительность <c>{ось}_sensitivity</c> (полных диапазонов в секунду, умолчание
/// <see cref="DefaultAxisSensitivity"/>). Читает их <see cref="Runtime.PilotSeatBehavior"/>.
/// </summary>
public static class ParameterPresets
{
    public const string ControlAxes = "ControlAxes";
    public const double DefaultAxisSensitivity = 3;

    /// <summary>Оси управления: id (префикс параметров) и подпись группы в панели. Порядок — как у <see cref="Runtime.PilotSeatBehavior.AxisNames"/>.</summary>
    public static readonly (string Id, string Title)[] ControlAxisIds =
    {
        ("ad", "A / D axis"),
        ("ws", "W / S axis"),
        ("lr", "Left / Right axis"),
        ("ud", "Up / Down axis"),
    };

    /// <summary>Параметры набора <paramref name="name"/>; неизвестное имя — исключение при загрузке блока.</summary>
    public static IReadOnlyList<ParameterDefinition> Get(string name)
    {
        if (name != ControlAxes) throw new InvalidOperationException($"unknown parameter preset '{name}' (known: {ControlAxes})");

        var list = new List<ParameterDefinition>();
        foreach (var (id, title) in ControlAxisIds)
        {
            list.Add(Read($"{{ \"id\": \"{id}_mode\", \"label\": \"Mode\", \"type\": \"Enum\", \"options\": [\"reset\", \"sticky\"], \"default\": \"reset\", \"group\": \"{title}\", \"hint\": \"reset: returns to 0 when released; sticky: stays where you left it\" }}"));
            list.Add(Read($"{{ \"id\": \"{id}_sensitivity\", \"label\": \"Sensitivity\", \"type\": \"Float\", \"min\": 0.1, \"max\": 20, \"step\": 0.1, \"default\": {DefaultAxisSensitivity.ToString(CultureInfo.InvariantCulture)}, \"group\": \"{title}\", \"hint\": \"full ranges per second\" }}"));
        }

        return list;
    }

    private static ParameterDefinition Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParametersComponent.ReadDefinitionPublic(document.RootElement);
    }
}
