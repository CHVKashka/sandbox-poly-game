using System.Linq;
using Godot;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Runtime;

/// <summary>Режим кнопки (параметр <c>"mode"</c> блока, значения <c>"momentary"</c>/<c>"toggle"</c>).</summary>
public enum ButtonMode
{
    /// <summary>Кнопка "активна", пока её удерживают; отпустили — неактивна.</summary>
    Momentary,

    /// <summary>Каждое нажатие переключает <see cref="ButtonState.Toggled"/>; отпускание ничего не меняет.</summary>
    Toggle,
}

/// <summary>
/// Параметры кнопки из <c>"params"</c> её блока (см. <see cref="FunctionalBlockComponent.BehaviorParams"/>) с разумными
/// умолчаниями — общие для поведения (<see cref="ButtonBehavior"/>) и визуального слоя (<see cref="Editor.ButtonVisual"/>),
/// чтобы оба читали их одинаково: <c>"mode"</c> (momentary по умолчанию; неизвестное значение — предупреждение в лог и
/// momentary), <c>"glowNode"</c> (имя узла-меша крышки в модели, по умолчанию "Cap"), <c>"glowColor"</c> (цвет свечения,
/// по умолчанию зелёный #33ff55; нечитаемый цвет — предупреждение и умолчание).
/// </summary>
public sealed record ButtonSettings(ButtonMode Mode, string GlowNode, Color GlowColor)
{
    public const string DefaultGlowNode = "Cap";
    public static readonly Color DefaultGlowColor = Color.FromHtml("#33ff55");

    public static ButtonSettings From(FunctionalBlockComponent definition) => From(definition, ParameterSet.Empty);

    /// <summary>Режим берётся из настраиваемого параметра <c>"mode"</c> экземпляра (инструмент «Parameters»), если он объявлен в схеме блока; иначе — из
    /// <c>"params"</c> типа блока. Узел-крышка и цвет свечения — константы типа блока.</summary>
    public static ButtonSettings From(FunctionalBlockComponent definition, ParameterSet parameters)
    {
        string glowNode = definition.GetParam("glowNode", DefaultGlowNode).Trim();
        if (glowNode.Length == 0) glowNode = DefaultGlowNode;

        var glowColor = DefaultGlowColor;
        string colorText = definition.GetParam("glowColor", "").Trim();
        if (colorText.Length > 0)
        {
            if (Color.HtmlIsValid(colorText)) glowColor = Color.FromHtml(colorText);
            else GD.PushWarning($"[button] unreadable glowColor '{colorText}' - using the default");
        }

        string modeText = parameters.Has("mode") ? parameters.GetString("mode") : definition.GetParam("mode", "momentary");
        return new ButtonSettings(ParseMode(modeText), glowNode, glowColor);
    }

    private static ButtonMode ParseMode(string text)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "momentary": return ButtonMode.Momentary;
            case "toggle": return ButtonMode.Toggle;
            default:
                GD.PushWarning($"[button] unknown mode '{text}' (expected momentary/toggle) - using momentary");
                return ButtonMode.Momentary;
        }
    }
}

/// <summary>Состояние одной кнопки (хранится в <see cref="FunctionalBlockRuntime"/>, не в <see cref="Core.BlockInstance"/>).
/// <see cref="Mode"/> фиксируется при создании (из параметров блока), остальное меняется.</summary>
public sealed class ButtonState : BlockState
{
    public ButtonMode Mode { get; init; }

    /// <summary>Id выходной ноды, значение которой отдаёт кнопка; null — в блоке не объявлена
    /// выходная Boolean-нода (тогда кнопка работает, но ничего наружу не отдаёт).</summary>
    public string? OutputNodeId { get; init; }

    /// <summary>Кнопка сейчас физически удержана (между <see cref="BlockInteraction.Press"/> и
    /// <see cref="BlockInteraction.Release"/>) — повторный Press без Release игнорируется, чтобы toggle не мигал.</summary>
    public bool Pressed { get; set; }

    /// <summary>Состояние-защёлка режима <see cref="ButtonMode.Toggle"/>: каждое нажатие инвертирует. В momentary не используется.</summary>
    public bool Toggled { get; set; }

    /// <summary>Запитан ли блок — выставляет рантайм каждый тик (<see cref="IBlockBehavior.SetPowered"/>); влияет только на
    /// подсветку, не на сигнал кнопки.</summary>
    public bool Powered { get; set; }

    /// <summary>"Кнопка включена" — то, что показывает и отдаёт кнопка: в momentary это <see cref="Pressed"/>, в toggle —
    /// <see cref="Toggled"/>. Именно по нему идёт и выходной сигнал, и целевое положение/подсветка визуала.</summary>
    public bool Active => Mode == ButtonMode.Toggle ? Toggled : Pressed;

    public override double[] CaptureNet() => new[] { Pressed ? 1.0 : 0.0, Toggled ? 1.0 : 0.0, Powered ? 1.0 : 0.0 };

    public override void ApplyNet(double[] values)
    {
        Pressed = At(values, 0) != 0;
        Toggled = At(values, 1) != 0;
        Powered = At(values, 2) != 0;
    }
}

/// <summary>
/// Поведение <c>"Button"</c> (см. <c>blocks/button.xml</c>): режим — <see cref="ButtonSettings.Mode"/>, выход — первый
/// выходная <see cref="NodeType.Boolean"/> нода из <see cref="FunctionalBlockComponent.Nodes"/>, значение — <see cref="ButtonState.Active"/>
/// (состояние кнопки; питание на сигнал не влияет — оно управляет только подсветкой).
/// </summary>
public sealed class ButtonBehavior : BlockBehavior<ButtonState>
{
    public const string Key = "Button";

    protected override ButtonState CreateState(FunctionalBlockComponent definition, ParameterSet parameters)
    {
        var output = definition.Nodes.FirstOrDefault(p => p.Direction == PortDirection.Out && p.Type == NodeType.Boolean);
        if (output == null) GD.PushWarning("[button] block declares no Out/Boolean node - the button will output nothing");

        return new ButtonState { Mode = ButtonSettings.From(definition, parameters).Mode, OutputNodeId = output?.Id };
    }

    protected override void Interact(ButtonState state, BlockInteraction interaction)
    {
        if (interaction == BlockInteraction.Press)
        {
            if (state.Pressed) return;
            state.Pressed = true;
            if (state.Mode == ButtonMode.Toggle) state.Toggled = !state.Toggled;
        }
        else
        {
            state.Pressed = false;
        }
    }

    protected override void SetPowered(ButtonState state, bool powered) => state.Powered = powered;

    protected override bool TryReadNode(ButtonState state, string nodeId, out NodeValue value)
    {
        if (state.OutputNodeId != null && nodeId == state.OutputNodeId)
        {
            value = NodeValue.FromBool(state.Active);
            return true;
        }

        value = NodeValue.Off;
        return false;
    }
}
