using System.Linq;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Runtime;

/// <summary>Состояние логического блока <c>Function</c> (<see cref="FunctionBehavior"/>).</summary>
public sealed class FunctionState : BlockState
{
    /// <summary>Id входной Number-ноды; null — в блоке такой ноды нет (блок отдаёт значение выражения при x = 0).</summary>
    public string? InputNodeId { get; init; }

    /// <summary>Id выходной Number-ноды; null — выходной ноды нет, блок ничего наружу не отдаёт.</summary>
    public string? OutputNodeId { get; init; }

    /// <summary>Разобранное выражение (параметр <c>expression</c>); при ошибке в тексте — константа 0, причина в <see cref="Error"/>.</summary>
    public required MathExpression Expression { get; init; }

    /// <summary>Сообщение об ошибке разбора текста выражения; null — выражение верно.</summary>
    public string? Error { get; init; }

    /// <summary>Значение на входе (<c>x</c>).</summary>
    public double Input { get; set; }

    /// <summary>Значение на выходе — результат выражения на прошлом шаге.</summary>
    public double Output { get; set; }

    public override double[] CaptureNet() => new[] { Input, Output };

    public override void ApplyNet(double[] values)
    {
        Input = At(values, 0);
        Output = At(values, 1);
    }
}

/// <summary>
/// Поведение <c>"Function"</c> (<c>blocks/function.xml</c>, заготовка логического блока): один Number-вход и один Number-выход, между ними — математическая функция, которую игрок
/// записывает в параметре <c>expression</c> (инструмент «Parameters», поле ввода; под ним справочник операторов, констант, тригонометрии, addon и конверсий). Единственная
/// переменная — <c>x</c> (значение входа); язык — <see cref="MathExpression"/>. Выход считается каждый тик; ошибка в тексте даёт на выходе 0 (причина показана в панели).
/// Ноды находятся по ТИПУ и направлению (первая входная и первая выходная Number), имена в XML можно менять. Нет провода на входе — <c>x = 0</c>.
/// </summary>
public sealed class FunctionBehavior : BlockBehavior<FunctionState>
{
    public const string Key = "Function";
    public const string DefaultExpression = "x";

    protected override FunctionState CreateState(FunctionalBlockComponent definition, ParameterSet parameters)
    {
        string text = parameters.Has("expression") ? parameters.GetString("expression", DefaultExpression) : definition.GetParam("expression", DefaultExpression);
        bool ok = MathExpression.TryParse(text, out var expression, out string error);
        return new FunctionState
        {
            InputNodeId = definition.Nodes.FirstOrDefault(n => n.Direction == PortDirection.In && n.Type == NodeType.Number)?.Id,
            OutputNodeId = definition.Nodes.FirstOrDefault(n => n.Direction == PortDirection.Out && n.Type == NodeType.Number)?.Id,
            Expression = ok ? expression : MathExpression.Zero,
            Error = ok ? null : error,
        };
    }

    protected override void SetInput(FunctionState state, string nodeId, NodeValue value)
    {
        if (nodeId == state.InputNodeId) state.Input = value.Number;
    }

    protected override void Tick(FunctionState state, double delta) => state.Output = state.Expression.Evaluate(state.Input);

    protected override bool TryReadNode(FunctionState state, string nodeId, out NodeValue value)
    {
        if (state.OutputNodeId != null && nodeId == state.OutputNodeId)
        {
            value = new NodeValue(state.Output);
            return true;
        }

        value = NodeValue.Off;
        return false;
    }
}
