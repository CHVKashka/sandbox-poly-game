using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Editor.Ui;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.Dev;

public sealed partial class SelfTest
{
    private const string FunctionJson =
        "{ \"footprint\": [2,1,1], \"behavior\": \"Function\", \"nodes\": [ { \"id\": \"in\", \"type\": \"Number\", \"direction\": \"In\", \"position\": [0,0,0] }, " +
        "{ \"id\": \"out\", \"type\": \"Number\", \"direction\": \"Out\", \"position\": [1,0,0] } ] }";

    private const string FunctionParameters = "{ \"parameters\": [ { \"id\": \"expression\", \"label\": \"Function\", \"type\": \"Expression\", \"default\": \"x\" } ] }";

    // ================================================================== язык выражений

    private void RunMathExpressionTests()
    {
        GD.Print("-- Function block: expression language (operators, precedence, constants, the whole math library, errors, reference)");

        double Eval(string text, double x = 0)
        {
            if (!MathExpression.TryParse(text, out var expression, out string error)) throw new InvalidOperationException($"'{text}': {error}");
            return expression.Evaluate(x);
        }

        bool Near(double a, double b) => Math.Abs(a - b) < 1e-9 * Math.Max(1, Math.Abs(b));
        bool Ok(string text, double expected, double x = 0)
        {
            try { return Near(Eval(text, x), expected); }
            catch (InvalidOperationException) { return false; }
        }

        // ---- операторы и приоритеты
        Check(Ok("1+2", 3) && Ok("5-8", -3) && Ok("3*4", 12) && Ok("7/2", 3.5) && Ok("10%3", 1) && Ok("2^10", 1024), "operators: + - * / % ^");
        Check(Ok("1+2*3", 7) && Ok("(1+2)*3", 9) && Ok("2*3^2", 18) && Ok("10-2-3", 5) && Ok("100/10/5", 2), "precedence: ^ over * / over + -, left-associative");
        Check(Ok("2^3^2", 512) && Ok("-x^2", -9, 3) && Ok("2^-1", 0.5) && Ok("-(-x)", 4, 4) && Ok("+x", 5, 5) && Ok("--x", 2, 2), "^ is right-associative and binds tighter than unary minus; unary +/- may repeat");
        Check(Ok("-7%3", -1) && Ok("7%-3", 1) && Ok("5.5%2", 1.5), "% is fmod: the sign follows the dividend");
        Check(Ok(".5+1.5e1", 15.5) && Ok("1e3", 1000) && Ok("2.5E-1", 0.25) && Ok("  x * 3.6  ", 7.2, 2) && Ok("(((x)))", 9, 9), "numbers (.5, exponents), spaces, nested parentheses");
        Check(Ok("x", 42, 42) && Ok("x*x+1", 17, 4), "the variable is x");

        // ---- константы
        Check(Ok("pi", Math.PI) && Ok("pi2", Math.PI * 2) && Ok("tau", Math.PI * 2) && Ok("e", Math.E) && Ok("phi", (1 + Math.Sqrt(5)) / 2)
              && Ok("sqrt2", Math.Sqrt(2)) && Ok("ln2", Math.Log(2)) && Ok("ln10", Math.Log(10)) && Ok("huge", double.MaxValue), "constants: pi pi2 tau e phi sqrt2 ln2 ln10 huge");

        // ---- вся библиотека: каждая функция против эталона
        var cases = new (string Text, double Expected)[]
        {
            ("sin(1)", Math.Sin(1)), ("cos(1)", Math.Cos(1)), ("tan(1)", Math.Tan(1)), ("asin(0.5)", Math.Asin(0.5)), ("acos(0.5)", Math.Acos(0.5)), ("atan(2)", Math.Atan(2)),
            ("atan2(1,2)", Math.Atan2(1, 2)), ("sinh(1)", Math.Sinh(1)), ("cosh(1)", Math.Cosh(1)), ("tanh(1)", Math.Tanh(1)), ("deg(pi)", 180), ("rad(180)", Math.PI),
            ("sqrt(16)", 4), ("cbrt(27)", 3), ("exp(1)", Math.E), ("pow(2,10)", 1024), ("log(e)", 1), ("log(8,2)", 3), ("log10(1000)", 3), ("log2(8)", 3),
            ("max(1,5)", 5), ("max(1,9,5)", 9), ("min(4,2)", 2), ("min(4,2,-1)", -1), ("ceil(1.2)", 2), ("floor(-1.2)", -2), ("trunc(-1.7)", -1), ("frac(2.25)", 0.25),
            ("round(2.5)", 3), ("round(-2.5)", -3), ("round(7,5)", 5), ("round(8,5)", 10), ("round(1.234,0.01)", 1.23), ("abs(-3)", 3), ("sgn(-2)", -1), ("sgn(2)", 1), ("sgn(0)", 1),
            ("fmod(7,3)", 1), ("len(3,4)", 5), ("len2(3,4)", 25), ("lerp(10,20,0.25)", 12.5), ("clamp(5,0,1)", 1), ("clamp(-5,0,1)", 0), ("clamp(0.5,0,1)", 0.5),
            ("max(min(x,10),-10)", 10), ("sin(x*pi2)", 0),
        };
        var wrong = cases.Where(c => !Ok(c.Text, c.Expected, c.Text.Contains('x') ? 25 : 0)).Select(c => c.Text).ToList();
        Check(wrong.Count == 0, $"{cases.Length} library calls give the reference values", string.Join(", ", wrong));
        Check(Functions_AllCovered(cases.Select(c => c.Text)), "...and every function of the library is among them", "");

        // ---- конверсии из справки
        Check(Ok("x * 3.6", 36, 10) && Ok("x * 2.236936", 22.36936, 10) && Ok("x * 1.943844", 19.43844, 10) && Ok("x * pi2", Math.PI * 2, 1)
              && Ok("x * (pi/180)", Math.PI, 180) && Ok("x * (180/pi)", 180, Math.PI), "the conversions of the reference (m/s to km/h, mph, knots; turns, degrees, radians)");

        // ---- ошибки: понятное сообщение с позицией, блок при ошибке отдаёт 0
        foreach (var (text, fragment) in new[] { ("", "empty"), ("   ", "empty"), ("2x", "unexpected 'x'"), ("1+", "end"), ("(1", "missing ')'"), ("foo(1)", "unknown function 'foo'"),
                     ("sin()", "takes 1"), ("sin(1,2)", "takes 1"), ("lerp(1,2)", "takes 3"), ("y", "unknown name 'y'"), (")", "unexpected ')'"), ("1 2", "unexpected '2'"), ("3 $ 4", "unexpected '$'") })
        {
            bool bad = !MathExpression.TryParse(text, out _, out string error);
            Check(bad && error.Contains(fragment), $"the text '{text}' is rejected with a message containing \"{fragment}\"", error);
        }

        Check(MathExpression.Validate("sin(x)") == null && MathExpression.Validate("sin(") != null && MathExpression.Validate("sin(")!.Contains("position"), "Validate: null for a good text, a message with the position for a bad one");
        Check(Ok("1/0", 0) && Ok("sqrt(-1)", 0) && Ok("log(0)", 0) && Ok("0/0", 0) && Ok("huge*huge", 0) && Ok("asin(2)", 0), "not-a-number and infinity become 0 (a block never puts NaN on a wire)");

        // ---- справка покрывает язык целиком
        var snippets = MathExpressionReference.Groups.SelectMany(g => g.Entries.Select(e => e.Snippet)).ToList();
        Check(MathExpressionReference.Groups.Select(g => g.Title).SequenceEqual(new[] { "Operators", "Constants", "Trigonometry (radians)", "Addon", "Conversions" }),
            "the reference has the five lists: Operators, Constants, Trigonometry (radians), Addon, Conversions");
        Check("+-*/%^".All(op => MathExpressionReference.Groups[0].Entries.Any(e => e.Snippet == op.ToString())), "Operators lists + - * / % ^");
        Check(MathExpression.Constants.Keys.All(name => MathExpressionReference.Groups[1].Entries.Any(e => e.Snippet == name)), "Constants lists every constant of the language", string.Join(",", MathExpression.Constants.Keys));
        var missing = MathExpression.Functions.Keys.Where(name => !snippets.Any(s => s.StartsWith(name + "("))).ToList();
        Check(missing.Count == 0, "the reference lists every function of the language", string.Join(",", missing));
        Check(MathExpressionReference.Groups[4].Entries.All(e => MathExpression.Validate(e.Snippet) == null), "every conversion of the reference is a valid expression");
        Check(MathExpressionReference.Groups.SelectMany(g => g.Entries).All(e => e.Description.Length > 0), "every entry has a description");
    }

    private static bool Functions_AllCovered(IEnumerable<string> texts)
    {
        var list = texts.ToList();
        return MathExpression.Functions.Keys.All(name => list.Any(t => t.StartsWith(name + "(") || t.Contains(name + "(")));
    }

    // ================================================================== блок Function: поведение, провода, панель

    private async Task RunFunctionBlockTests(BuildEditor editor)
    {
        GD.Print("-- Function block: behavior, wire through the runtime, the expression field and the nested reference lists in the Parameters panel");

        using var scope = UseCatalogWith(
            new FixtureBlock("zz_seat", SeatJson(), SeatParameters()),
            new FixtureBlock("zz_function", FunctionJson, FunctionParameters));
        var catalog = BlockCatalog.Instance;
        var definition = catalog.Get("zz_function");
        var fn = FunctionOf(definition);
        BlockBehaviorRegistry.TryGet(FunctionBehavior.Key, out var behavior);

        // ---- параметр Expression
        var schema = Construction.ParametersOf(definition)!;
        schema.TryGet("expression", out var expressionParameter);
        Check(expressionParameter.Type == ParameterType.Expression && expressionParameter.Widget == ParameterWidget.ExpressionBox && expressionParameter.Default == "x", "an Expression parameter: the expression box widget, default x");
        Check(expressionParameter.TryNormalize("  sin(x) * 2 ", out var normalized) && normalized == "sin(x) * 2", "its text is trimmed");
        Check(expressionParameter.TryNormalize("2x +", out var half) && half == "2x +", "a half-typed (invalid) expression is accepted as text - the panel shows the error, the block outputs 0");
        Check(!expressionParameter.TryNormalize("   ", out _) && !expressionParameter.TryNormalize(new string('x', ParameterDefinition.MaxExpressionLength + 1), out _), "an empty or over-long text is rejected (the value stays)");

        // ---- поведение
        FunctionState Make(string expression)
        {
            var overrides = new Dictionary<string, string> { ["expression"] = expression };
            return (FunctionState)behavior.CreateState(fn, new ParameterSet(schema, overrides));
        }

        var plain = (FunctionState)behavior.CreateState(fn);
        Check(plain.InputNodeId == "in" && plain.OutputNodeId == "out", "the nodes are found by type and direction (first Number In, first Number Out)", $"{plain.InputNodeId}/{plain.OutputNodeId}");
        behavior.SetInput(plain, "in", new NodeValue(3));
        behavior.Tick(plain, 0.016);
        Check(behavior.TryReadNode(plain, "out", out var passed) && Math.Abs(passed.Number - 3) < 1e-9, "the default expression x passes the input through");

        var scaled = Make("x * 3.6 + 1");
        behavior.SetInput(scaled, "in", new NodeValue(10));
        behavior.Tick(scaled, 0.016);
        Check(behavior.TryReadNode(scaled, "out", out var result) && Math.Abs(result.Number - 37) < 1e-9 && scaled.Error == null, "x * 3.6 + 1 at x = 10 gives 37");
        Check(!behavior.TryReadNode(scaled, "in", out _), "the input node cannot be read as an output");

        var broken = Make("2x +");
        behavior.SetInput(broken, "in", new NodeValue(10));
        behavior.Tick(broken, 0.016);
        Check(broken.Error != null && behavior.TryReadNode(broken, "out", out var zero) && zero.Number == 0, "a broken expression outputs 0 and keeps the reason", broken.Error ?? "");
        Check(Math.Abs(((FunctionState)behavior.CreateState(MakeFunctional("{ \"behavior\": \"Function\", \"params\": { \"expression\": \"x * 2\" }, \"nodes\": [ { \"id\": \"i\", \"type\": \"Number\", \"direction\": \"In\" }, { \"id\": \"o\", \"type\": \"Number\", \"direction\": \"Out\", \"position\": [1,0,0] } ] }"))).Expression.Evaluate(4) - 8) < 1e-9,
            "a block without the schema entry takes the expression from its \"params\"");

        var captured = scaled.CaptureNet();
        var mirror = Make("x");
        mirror.ApplyNet(captured);
        Check(Math.Abs(mirror.Input - 10) < 1e-9 && Math.Abs(mirror.Output - 37) < 1e-9, "input and output are replicated (CaptureNet/ApplyNet)");

        // ---- через рантайм: ось сиденья -> Function -> значение на выходе
        var construction = new Construction(new VoxelGrid());
        var seat = PlaceFixture(construction, "zz_seat", new Vector3I(0, 5, 0));
        var function = PlaceFixture(construction, "zz_function", new Vector3I(0, 0, 0));
        construction.TrySetParameter(function, "expression", "clamp(x * 4, 0, 1) + 10");
        Check(construction.TryConnect(seat.InstanceId, "ws_out", function.InstanceId, "in", out _), "a seat axis connects to the Function input (Number -> Number)");
        using var runtime = new FunctionalBlockRuntime(construction, catalog);
        runtime.GetState<PilotSeatState>(seat.InstanceId)!.Modes[1] = SeatAxisMode.Sticky; // ось держит значение (в режиме reset она бы утекала к нулю каждый тик)
        runtime.GetState<PilotSeatState>(seat.InstanceId)!.AxisValue[1] = 0.1;
        runtime.Tick(0.016);
        runtime.Tick(0.016);
        Check(runtime.TryReadNode(function.InstanceId, "out", out var wired) && Math.Abs(wired.Number - 10.4) < 1e-9, "through the runtime: seat axis 0.1 -> clamp(x*4,0,1)+10 = 10.4", $"{wired.Number}");
        runtime.GetState<PilotSeatState>(seat.InstanceId)!.AxisValue[1] = 0.9;
        runtime.Tick(0.016);
        runtime.Tick(0.016);
        Check(runtime.TryReadNode(function.InstanceId, "out", out var clamped) && Math.Abs(clamped.Number - 11) < 1e-9, "...and 0.9 is clamped: 11", $"{clamped.Number}");
        construction.TrySetParameter(function, "expression", "x * 100");
        runtime.Tick(0.016);
        runtime.Tick(0.016);
        Check(runtime.TryReadNode(function.InstanceId, "out", out var changed) && Math.Abs(changed.Number - 90) < 1e-9, "changing the expression parameter takes effect (the state is rebuilt)", $"{changed.Number}");

        // ---- панель
        var layer = new Control();
        editor.AddChild(layer);
        var panel = new ParametersPanelUi(layer);
        var edits = new List<(string Id, string Text)>();
        panel.ParameterEdited += (id, text) => edits.Add((id, text));
        var fresh = construction.GetInstance(function.InstanceId)!;
        panel.Open(fresh, definition, schema);
        await Frames(editor, 1);

        var field = panel.ControlOf("expression") as LineEdit;
        Check(field != null && panel.DisplayedValue("expression") == "x * 100", "the panel shows the expression in a text field", field == null ? "no field" : field.Text);
        Check(panel.ReferenceTitles.SequenceEqual(MathExpressionReference.Groups.Select(g => g.Title)), "below the field: the five nested lists (Operators, Constants, Trigonometry, Addon, Conversions)", string.Join(",", panel.ReferenceTitles));
        Check(panel.ReferenceTitles.All(t => !panel.ReferenceExpanded(t)), "...collapsed by default");
        Check(MathExpressionReference.Groups.All(g => panel.ReferenceEntryCount(g.Title) == g.Entries.Count), "...each holds all its entries (a snippet and a description per row)");
        panel.ToggleReferenceForTesting("Addon");
        Check(panel.ReferenceExpanded("Addon") && !panel.ReferenceExpanded("Operators"), "clicking a list header expands just that list");
        panel.ToggleReferenceForTesting("Addon");
        Check(!panel.ReferenceExpanded("Addon"), "...and clicking again collapses it");

        field!.Text = "2x +";
        field.EmitSignal(LineEdit.SignalName.TextChanged, "2x +");
        Check(panel.ExpressionError("expression").Contains("Error") && panel.ExpressionError("expression").Contains("unexpected"), "typing a broken expression shows the error under the field", panel.ExpressionError("expression"));
        field.Text = "x * 2";
        field.EmitSignal(LineEdit.SignalName.TextChanged, "x * 2");
        Check(panel.ExpressionError("expression") == "", "...and a good one clears it");
        field.EmitSignal(LineEdit.SignalName.TextSubmitted, "x * 2");
        Check(edits.Count == 1 && edits[0] == ("expression", "x * 2"), "Enter sends the expression text as an edit", string.Join(";", edits));

        var colored = ParametersPanelUi.Colorize("sin(x)");
        string functionHex = new Color(1f, 0.31f, 0.64f).ToHtml(false), argumentHex = new Color(0.31f, 0.85f, 0.91f).ToHtml(false), operatorHex = new Color(0.61f, 0.91f, 0.31f).ToHtml(false);
        Check(colored.Contains($"[color=#{functionHex}]sin[/color]") && colored.Contains($"[color=#{argumentHex}]x[/color]") && functionHex != argumentHex, "the reference colours function names and variables apart (like the Stormworks reference)", colored);
        Check(ParametersPanelUi.Colorize("x * 3.6").Contains($"[color=#{operatorHex}]*[/color]") && ParametersPanelUi.Colorize("x * 3.6").Contains($"[color=#{argumentHex}]3.6[/color]"), "...operators green, numbers like variables");

        layer.QueueFree();
        await Frames(editor, 1);
    }

    private static FunctionalBlockComponent? FunctionOf(BlockDefinition definition) => definition.GetComponent<FunctionalBlockComponent>();
}
