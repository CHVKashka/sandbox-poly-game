using System.Text.Json;

namespace SandboxPolyGame.Blocks;

/// <summary>
/// Базовые физические параметры блока: масса, прочность, сопротивление урону. Есть у любого блока
/// (хотя бы с значениями по умолчанию, если XML-описание блока его не задаёт явно).
/// Сейчас параметры только хранятся — расчёт массы/прочности постройки будет позже (см. ROADMAP).
/// </summary>
/// <remarks>JSON-параметры: <c>{ "mass": 10, "durability": 100, "damageResistance": 0.1 }</c>.</remarks>
public sealed class BaseComponent : BlockComponent
{
    public const string ComponentType = "BaseComponent";

    /// <summary>Масса блока (условные единицы, кг за клетку 1x1x1).</summary>
    public float Mass { get; private set; } = 1f;

    /// <summary>Прочность блока (условные единицы урона до разрушения).</summary>
    public float Durability { get; private set; } = 100f;

    /// <summary>Доля урона, которую блок поглощает (0 — не поглощает, 1 — полностью).</summary>
    public float DamageResistance { get; private set; }

    public override void LoadFromJson(JsonElement json)
    {
        if (json.TryGetProperty("mass", out var mass)) Mass = mass.GetSingle();
        if (json.TryGetProperty("durability", out var durability)) Durability = durability.GetSingle();
        if (json.TryGetProperty("damageResistance", out var resistance)) DamageResistance = resistance.GetSingle();
    }
}
