using System.Collections.Generic;
using System.Globalization;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Runtime;

/// <summary>
/// Эффективные значения настраиваемых параметров ОДНОГО экземпляра блока: схема и умолчания — из <see cref="ParametersComponent"/> блока, значения,
/// которые игрок поменял инструментом «Parameters», — из <see cref="Core.BlockInstance.Parameters"/>. Поведения читают параметры только отсюда
/// (<see cref="GetFloat"/>/<see cref="GetBool"/>/<see cref="GetString"/>), не зная ни схемы, ни того, как значения хранятся. Читающий всегда
/// получает допустимое значение: у параметра, которого в схеме нет (или у блока нет компонента), — запасное, переданное вызывающим.
/// </summary>
public sealed class ParameterSet
{
    private readonly ParametersComponent? _schema;
    private readonly IReadOnlyDictionary<string, string>? _overrides;

    /// <summary>Набор без схемы и без изменённых значений — все чтения вернут запасные значения (для блоков без компонента Parameters и тестов).</summary>
    public static readonly ParameterSet Empty = new(null, null);

    public ParameterSet(ParametersComponent? schema, IReadOnlyDictionary<string, string>? overrides)
    {
        _schema = schema;
        _overrides = overrides;
    }

    /// <summary>Объявлен ли параметр в схеме блока.</summary>
    public bool Has(string id) => _schema != null && _schema.TryGet(id, out _);

    /// <summary>Каноническое текстовое значение параметра (изменённое, иначе умолчание схемы); <paramref name="fallback"/> — если параметра нет в схеме.</summary>
    public string GetString(string id, string fallback = "")
    {
        if (_schema == null || !_schema.TryGet(id, out var definition)) return fallback;

        // Сохранённое значение прогоняется через ту же нормализацию, что и ввод: файл могли поправить руками или схема сменила границы.
        if (_overrides != null && _overrides.TryGetValue(id, out string? raw) && definition.TryNormalize(raw, out string normalized)) return normalized;
        return definition.Default;
    }

    public double GetFloat(string id, double fallback = 0) =>
        Has(id) && double.TryParse(GetString(id), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;

    public int GetInt(string id, int fallback = 0) => (int)System.Math.Round(GetFloat(id, fallback));

    public bool GetBool(string id, bool fallback = false) => Has(id) ? GetString(id) == "true" : fallback;
}
