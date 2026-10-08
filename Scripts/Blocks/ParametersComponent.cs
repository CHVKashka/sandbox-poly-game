using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace SandboxPolyGame.Blocks;

/// <summary>Тип настраиваемого параметра блока (<see cref="ParameterDefinition.Type"/>).</summary>
public enum ParameterType
{
    /// <summary>Дробное число (<see cref="ParameterDefinition.Min"/>/<see cref="ParameterDefinition.Max"/> — границы).</summary>
    Float,

    /// <summary>Целое число.</summary>
    Int,

    /// <summary>Флаг вкл/выкл.</summary>
    Bool,

    /// <summary>Один вариант из <see cref="ParameterDefinition.Options"/>.</summary>
    Enum,
}

/// <summary>
/// Описание одного настраиваемого параметра блока: что он значит (<see cref="Label"/>, <see cref="Group"/>), какого он типа и в каких
/// границах, значение по умолчанию. Само ЗНАЧЕНИЕ у каждого поставленного блока своё (<see cref="Core.BlockInstance.Parameters"/> — только
/// отличия от умолчания); здесь — схема, по которой инструмент «Parameters» рисует панель, а поведения читают значения.
/// Значения хранятся как текст в канонической форме (<see cref="Normalize"/>), чтобы одинаково сериализоваться в постройку и
/// передаваться по сети строкой.
/// </summary>
public sealed class ParameterDefinition
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required ParameterType Type { get; init; }

    /// <summary>Значение по умолчанию в канонической форме (всегда допустимое).</summary>
    public required string Default { get; init; }

    /// <summary>Подпись блока параметров в панели (параметры с одинаковой группой идут подряд под одним заголовком); пусто — без заголовка.</summary>
    public string Group { get; init; } = "";

    /// <summary>Подсказка под полем в панели (необязательна).</summary>
    public string Hint { get; init; } = "";

    public double Min { get; init; } = double.NegativeInfinity;
    public double Max { get; init; } = double.PositiveInfinity;

    /// <summary>Шаг полей-стрелок в панели (для чисел); 0 — без шага.</summary>
    public double Step { get; init; }

    /// <summary>Допустимые варианты (только <see cref="ParameterType.Enum"/>).</summary>
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Приводит введённый текст к допустимому значению параметра в канонической форме: числа — по границам и до 6 знаков, целые — округлённые,
    /// флаг — <c>true</c>/<c>false</c> (принимает 1/0/yes/no/on/off), перечисление — вариант из списка (без учёта регистра). false — текст
    /// разобрать нельзя (для числа — не число, для перечисления — нет такого варианта): значение не меняется.
    /// </summary>
    public bool TryNormalize(string text, out string normalized)
    {
        normalized = Default;
        text = text.Trim();
        switch (Type)
        {
            case ParameterType.Float:
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) return false;
                normalized = FormatNumber(Math.Clamp(value, Min, Max));
                return true;
            }

            case ParameterType.Int:
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) return false;
                normalized = ((long)Math.Round(Math.Clamp(value, Min, Max))).ToString(CultureInfo.InvariantCulture);
                return true;
            }

            case ParameterType.Bool:
            {
                switch (text.ToLowerInvariant())
                {
                    case "true": case "1": case "yes": case "on": normalized = "true"; return true;
                    case "false": case "0": case "no": case "off": normalized = "false"; return true;
                    default: return false;
                }
            }

            case ParameterType.Enum:
            {
                string? match = Options.FirstOrDefault(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase));
                if (match == null) return false;
                normalized = match;
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>Число в канонической текстовой форме: инвариантная культура, до 6 знаков после запятой, без хвостовых нулей.</summary>
    public static string FormatNumber(double value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>
/// Компонент «Parameters» — по аналогии с <see cref="BaseComponent"/>: ДЕКЛАРАТИВНО перечисляет, какие параметры у блока можно настраивать в
/// инструменте «Parameters» редактора построек (сиденье — режим и чувствительность осей, кнопка — режим, мотор — мощность/обороты, позже рычаги и
/// колёса). Нет компонента (или он пуст) — блок ничего не настраивается: инструмент его не подсвечивает и не открывает. Так не нужно в коде держать
/// список «какой блок какие параметры имеет»: панель строится по схеме, поведение читает значение через <see cref="Runtime.ParameterSet"/>.
/// <para/>
/// Не путать с <see cref="FunctionalBlockComponent.BehaviorParams"/> (<c>"params"</c> внутри FunctionalBlock): те — константы ТИПА блока
/// (имя узла-крышки, цвет свечения), одинаковые у всех экземпляров, их игрок не меняет; здесь — то, что игрок настраивает у КАЖДОГО экземпляра.
/// </summary>
/// <remarks>
/// JSON: <c>{ "parameters": [ { "id": "mode", "label": "Mode", "type": "Enum", "options": ["momentary","toggle"], "default": "momentary",
/// "group": "Button", "hint": "..." }, { "id": "sensitivity", "label": "Sensitivity", "type": "Float", "min": 0.1, "max": 20, "step": 0.1,
/// "default": 2 } ] }</c>. <c>type</c> — Float/Int/Bool/Enum; для Enum <c>options</c> обязательны, <c>default</c> — один из них.
/// Ошибка схемы (повтор id, неизвестный тип, Enum без вариантов, недопустимое умолчание, min &gt; max) — исключение при загрузке блока.
/// </remarks>
public sealed class ParametersComponent : BlockComponent
{
    public const string ComponentType = "Parameters";

    private readonly Dictionary<string, ParameterDefinition> _byId = new();

    public IReadOnlyList<ParameterDefinition> Parameters { get; private set; } = Array.Empty<ParameterDefinition>();

    public bool TryGet(string id, out ParameterDefinition definition) => _byId.TryGetValue(id, out definition!);

    public override void LoadFromJson(JsonElement json)
    {
        _byId.Clear();
        var list = new List<ParameterDefinition>();
        if (json.TryGetProperty("parameters", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var definition = ReadDefinition(item);
                if (!_byId.TryAdd(definition.Id, definition)) throw new InvalidOperationException($"parameter id '{definition.Id}' is declared twice");
                list.Add(definition);
            }
        }

        // Общие наборы настроек ("include": ["ControlAxes"]): одни и те же параметры у ВСЕХ блоков с управлением (сиденья и будущие органы управления) - объявлены один раз в коде.
        if (json.TryGetProperty("include", out var includes) && includes.ValueKind == JsonValueKind.Array)
        {
            foreach (var include in includes.EnumerateArray())
            {
                string presetName = include.GetString() ?? "";
                foreach (var definition in ParameterPresets.Get(presetName))
                {
                    if (!_byId.TryAdd(definition.Id, definition)) throw new InvalidOperationException($"parameter id '{definition.Id}' (from the '{presetName}' preset) is declared twice");
                    list.Add(definition);
                }
            }
        }

        Parameters = list;
    }

    internal static ParameterDefinition ReadDefinitionPublic(JsonElement json) => ReadDefinition(json);

    private static ParameterDefinition ReadDefinition(JsonElement json)
    {
        string id = json.TryGetProperty("id", out var idJson) ? idJson.GetString() ?? "" : "";
        if (id.Length == 0) throw new InvalidOperationException("a parameter is missing its \"id\"");

        string typeName = json.TryGetProperty("type", out var typeJson) ? typeJson.GetString() ?? "" : "";
        if (!Enum.TryParse<ParameterType>(typeName, ignoreCase: true, out var type))
        {
            throw new InvalidOperationException($"parameter '{id}': unknown type '{typeName}' (expected Float/Int/Bool/Enum)");
        }

        var options = json.TryGetProperty("options", out var optionsJson) && optionsJson.ValueKind == JsonValueKind.Array
            ? optionsJson.EnumerateArray().Select(o => o.GetString() ?? "").Where(o => o.Length > 0).ToArray()
            : Array.Empty<string>();
        if (type == ParameterType.Enum && options.Length == 0) throw new InvalidOperationException($"parameter '{id}': an Enum needs \"options\"");

        double min = json.TryGetProperty("min", out var minJson) ? minJson.GetDouble() : double.NegativeInfinity;
        double max = json.TryGetProperty("max", out var maxJson) ? maxJson.GetDouble() : double.PositiveInfinity;
        if (min > max) throw new InvalidOperationException($"parameter '{id}': min {min} is greater than max {max}");

        string label = json.TryGetProperty("label", out var labelJson) ? labelJson.GetString() ?? id : id;

        // Умолчание нормализуется той же логикой, что и вводимое значение (границы, формат), поэтому оно заведомо допустимо.
        var draft = new ParameterDefinition
        {
            Id = id, Label = label, Type = type, Default = type switch { ParameterType.Enum => options[0], ParameterType.Bool => "false", _ => "0" },
            Min = min, Max = max, Options = options,
        };
        string defaultText = json.TryGetProperty("default", out var defaultJson)
            ? defaultJson.ValueKind == JsonValueKind.String ? defaultJson.GetString() ?? "" : defaultJson.GetRawText()
            : draft.Default;
        if (!draft.TryNormalize(defaultText, out string normalizedDefault)) throw new InvalidOperationException($"parameter '{id}': default '{defaultText}' is not a valid {type}");

        return new ParameterDefinition
        {
            Id = id,
            Label = label,
            Type = type,
            Default = normalizedDefault,
            Group = json.TryGetProperty("group", out var groupJson) ? groupJson.GetString() ?? "" : "",
            Hint = json.TryGetProperty("hint", out var hintJson) ? hintJson.GetString() ?? "" : "",
            Min = min,
            Max = max,
            Step = json.TryGetProperty("step", out var stepJson) ? stepJson.GetDouble() : 0,
            Options = options,
        };
    }
}
