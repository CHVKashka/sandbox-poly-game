using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SandboxPolyGame.Runtime;

/// <summary>
/// Математическое выражение с одной переменной <c>x</c> (блок <c>Function</c>, см. <see cref="FunctionBehavior"/>): разбирается один раз в дерево замыканий, дальше считается
/// быстро и без выделений. Поддержано: числа (<c>3</c>, <c>.5</c>, <c>1.5e-3</c>), переменная <c>x</c>, константы (<see cref="Constants"/>), скобки, операторы
/// <c>+ - * / % ^</c> (приоритет: <c>^</c> выше унарного минуса и правоассоциативна, затем <c>* / %</c>, затем <c>+ -</c>; <c>%</c> — fmod, знак как у делимого) и функции
/// стандартной библиотеки (<see cref="Functions"/>: Lua-подобный <c>math</c> плюс <c>sgn</c>, <c>len</c>, <c>lerp</c>, <c>clamp</c> и др.). Неявного умножения нет
/// (<c>2x</c> — ошибка, пишется <c>2*x</c>). Не число в результате (деление на ноль, корень из отрицательного) превращается в 0 — блок не должен отдавать NaN в провода.
/// </summary>
public sealed class MathExpression
{
    /// <summary>Описание функции для разбора и справки: имя, допустимое число аргументов (<see cref="MinArgs"/>..<see cref="MaxArgs"/>), вычисление.</summary>
    public sealed record FunctionInfo(string Name, int MinArgs, int MaxArgs, Func<double[], double> Evaluate);

    /// <summary>Константы выражений: имя → значение.</summary>
    public static readonly IReadOnlyDictionary<string, double> Constants = new Dictionary<string, double>
    {
        ["pi"] = Math.PI,
        ["pi2"] = Math.PI * 2,
        ["tau"] = Math.PI * 2,
        ["e"] = Math.E,
        ["phi"] = (1 + Math.Sqrt(5)) / 2,
        ["sqrt2"] = Math.Sqrt(2),
        ["ln2"] = Math.Log(2),
        ["ln10"] = Math.Log(10),
        ["huge"] = double.MaxValue,
    };

    private static FunctionInfo F1(string name, Func<double, double> f) => new(name, 1, 1, a => f(a[0]));
    private static FunctionInfo F2(string name, Func<double, double, double> f) => new(name, 2, 2, a => f(a[0], a[1]));

    /// <summary>Функции выражений (вся стандартная библиотека math + дополнения). Углы тригонометрии — в радианах.</summary>
    public static readonly IReadOnlyDictionary<string, FunctionInfo> Functions = new[]
    {
        // тригонометрия
        F1("sin", Math.Sin), F1("cos", Math.Cos), F1("tan", Math.Tan),
        F1("asin", Math.Asin), F1("acos", Math.Acos), F1("atan", Math.Atan), F2("atan2", Math.Atan2),
        F1("sinh", Math.Sinh), F1("cosh", Math.Cosh), F1("tanh", Math.Tanh),
        F1("deg", r => r * (180 / Math.PI)), F1("rad", d => d * (Math.PI / 180)),

        // степени, корни, логарифмы
        F1("sqrt", Math.Sqrt), F1("cbrt", Math.Cbrt), F1("exp", Math.Exp), F2("pow", Math.Pow),
        new FunctionInfo("log", 1, 2, a => a.Length == 1 ? Math.Log(a[0]) : Math.Log(a[0], a[1])), // log(x) - натуральный, log(x, основание)
        F1("log10", Math.Log10), F1("log2", Math.Log2),

        // addon
        new FunctionInfo("max", 2, 16, a => a.Max()), new FunctionInfo("min", 2, 16, a => a.Min()),
        F1("ceil", Math.Ceiling), F1("floor", Math.Floor), F1("trunc", Math.Truncate), F1("frac", v => v - Math.Truncate(v)),
        new FunctionInfo("round", 1, 2, a => a.Length == 1 ? Math.Round(a[0], MidpointRounding.AwayFromZero) : a[1] == 0 ? 0 : Math.Round(a[0] / a[1], MidpointRounding.AwayFromZero) * a[1]),
        F1("abs", Math.Abs), F1("sgn", v => v < 0 ? -1 : 1), // как в справке: 1 при x = 0
        F2("fmod", (a, b) => a % b),
        F2("len", (a, b) => Math.Sqrt(a * a + b * b)), F2("len2", (a, b) => a * a + b * b),
        new FunctionInfo("lerp", 3, 3, a => a[0] + (a[1] - a[0]) * a[2]),
        new FunctionInfo("clamp", 3, 3, a => Math.Min(Math.Max(a[0], a[1]), a[2])),
    }.ToDictionary(f => f.Name);

    private readonly Func<double, double> _compiled;

    private MathExpression(string text, Func<double, double> compiled)
    {
        Text = text;
        _compiled = compiled;
    }

    /// <summary>Исходный текст выражения.</summary>
    public string Text { get; }

    /// <summary>Значение при <c>x</c>; не число и бесконечность превращаются в 0.</summary>
    public double Evaluate(double x)
    {
        double value = _compiled(x);
        return double.IsFinite(value) ? value : 0;
    }

    /// <summary>Разбирает выражение; false и понятное сообщение об ошибке (с позицией), если оно неверно. Пустой текст — ошибка.</summary>
    public static bool TryParse(string text, out MathExpression expression, out string error)
    {
        expression = Identity;
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty expression";
            return false;
        }

        try
        {
            var parser = new Parser(text);
            var compiled = parser.ParseAll();
            expression = new MathExpression(text.Trim(), compiled);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Выражение <c>x</c> (запасное, когда текст неверен).</summary>
    public static readonly MathExpression Identity = new("x", x => x);

    /// <summary>Константа 0 — результат блока при неверном тексте выражения.</summary>
    public static readonly MathExpression Zero = new("0", _ => 0);

    /// <summary>Только проверка: null — выражение верно, иначе сообщение об ошибке.</summary>
    public static string? Validate(string text) => TryParse(text, out _, out string error) ? null : error;

    // ------------------------------------------------------------------ разбор

    private sealed class Parser
    {
        private readonly string _text;
        private int _position;

        public Parser(string text) => _text = text;

        public Func<double, double> ParseAll()
        {
            var result = ParseSum();
            SkipSpaces();
            if (_position < _text.Length) throw Error($"unexpected '{_text[_position]}'");
            return result;
        }

        private FormatException Error(string message) => new($"{message} at position {_position + 1}");

        private void SkipSpaces()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
        }

        private bool Take(char c)
        {
            SkipSpaces();
            if (_position < _text.Length && _text[_position] == c)
            {
                _position++;
                return true;
            }

            return false;
        }

        private char Peek()
        {
            SkipSpaces();
            return _position < _text.Length ? _text[_position] : '\0';
        }

        // sum := product (('+' | '-') product)*
        private Func<double, double> ParseSum()
        {
            var left = ParseProduct();
            while (true)
            {
                char c = Peek();
                if (c != '+' && c != '-') return left;
                _position++;
                var l = left;
                var r = ParseProduct();
                left = c == '+' ? x => l(x) + r(x) : x => l(x) - r(x);
            }
        }

        // product := unary (('*' | '/' | '%') unary)*
        private Func<double, double> ParseProduct()
        {
            var left = ParseUnary();
            while (true)
            {
                char c = Peek();
                if (c != '*' && c != '/' && c != '%') return left;
                _position++;
                var l = left;
                var r = ParseUnary();
                left = c switch
                {
                    '*' => x => l(x) * r(x),
                    '/' => x => l(x) / r(x),
                    _ => x => l(x) % r(x),
                };
            }
        }

        // unary := ('-' | '+') unary | power
        private Func<double, double> ParseUnary()
        {
            if (Take('-'))
            {
                var inner = ParseUnary();
                return x => -inner(x);
            }

            if (Take('+')) return ParseUnary();
            return ParsePower();
        }

        // power := primary ('^' unary)?   - правоассоциативна, показатель может быть с унарным минусом: 2^-3, 2^3^2 = 2^(3^2)
        private Func<double, double> ParsePower()
        {
            var baseValue = ParsePrimary();
            if (!Take('^')) return baseValue;
            var exponent = ParseUnary();
            return x => Math.Pow(baseValue(x), exponent(x));
        }

        private Func<double, double> ParsePrimary()
        {
            char c = Peek();
            if (c == '\0') throw Error("unexpected end of the expression");

            if (c == '(')
            {
                _position++;
                var inner = ParseSum();
                if (!Take(')')) throw Error("missing ')'");
                return inner;
            }

            if (char.IsDigit(c) || c == '.') return ParseNumber();
            if (char.IsLetter(c) || c == '_') return ParseName();
            throw Error($"unexpected '{c}'");
        }

        private Func<double, double> ParseNumber()
        {
            int start = _position;
            while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.')) _position++;
            if (_position < _text.Length && (_text[_position] == 'e' || _text[_position] == 'E'))
            {
                int save = _position;
                _position++;
                if (_position < _text.Length && (_text[_position] == '+' || _text[_position] == '-')) _position++;
                int digits = _position;
                while (_position < _text.Length && char.IsDigit(_text[_position])) _position++;
                if (digits == _position) _position = save; // «2e» без цифр - это число 2 и имя e (потом ошибка про отсутствие оператора)
            }

            string token = _text[start.._position];
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) throw Error($"bad number '{token}'");
            return _ => value;
        }

        private Func<double, double> ParseName()
        {
            int start = _position;
            while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_')) _position++;
            string name = _text[start.._position];

            if (Peek() == '(')
            {
                _position++;
                var arguments = new List<Func<double, double>>();
                if (!Take(')'))
                {
                    do arguments.Add(ParseSum());
                    while (Take(','));
                    if (!Take(')')) throw Error("missing ')'");
                }

                if (!Functions.TryGetValue(name, out var function)) throw Error($"unknown function '{name}'");
                if (arguments.Count < function.MinArgs || arguments.Count > function.MaxArgs)
                {
                    string expected = function.MinArgs == function.MaxArgs ? $"{function.MinArgs}" : $"{function.MinArgs}..{function.MaxArgs}";
                    throw Error($"{name} takes {expected} argument(s), got {arguments.Count}");
                }

                var compiled = arguments.ToArray();
                var buffer = new double[compiled.Length];
                return x =>
                {
                    for (int i = 0; i < compiled.Length; i++) buffer[i] = compiled[i](x);
                    return function.Evaluate(buffer);
                };
            }

            if (name == "x") return x => x;
            if (Constants.TryGetValue(name, out double constant)) return _ => constant;
            throw Error($"unknown name '{name}' (the only variable is x)");
        }
    }
}

/// <summary>
/// Справка по выражениям для панели «Parameters» (вложенные списки под полем ввода блока Function): группы <c>operators</c>, <c>constants</c>, <c>trigonometry</c>, <c>addon</c>,
/// <c>conversions</c> — запись «как писать» и что делает. Все функции и константы из <see cref="MathExpression"/> здесь перечислены (проверяется самотестом), поэтому справка
/// не может разойтись с тем, что реально разбирается.
/// </summary>
public static class MathExpressionReference
{
    public sealed record Entry(string Snippet, string Description);

    public sealed record Group(string Title, IReadOnlyList<Entry> Entries);

    public static readonly IReadOnlyList<Group> Groups = new List<Group>
    {
        new("Operators", new List<Entry>
        {
            new("+", "Addition"), new("-", "Subtraction"), new("*", "Multiply"), new("/", "Divide"), new("%", "Modulo (fmod)"), new("^", "Power"),
        }),
        new("Constants", new List<Entry>
        {
            new("pi", "3.14159... (half a turn)"), new("pi2", "2 * pi (a full turn)"), new("tau", "2 * pi (same as pi2)"), new("e", "2.71828... (Euler's number)"),
            new("phi", "1.61803... (golden ratio)"), new("sqrt2", "Square root of 2"), new("ln2", "Natural logarithm of 2"), new("ln10", "Natural logarithm of 10"),
            new("huge", "Largest number"),
        }),
        new("Trigonometry (radians)", new List<Entry>
        {
            new("sin(x)", "Sine"), new("cos(x)", "Cosine"), new("tan(x)", "Tangent"), new("asin(x)", "Arcsine"), new("acos(x)", "Arccosine"), new("atan(x)", "Arctangent"),
            new("atan2(y,x)", "Atan2"), new("sinh(x)", "Hyperbolic sine"), new("cosh(x)", "Hyperbolic cosine"), new("tanh(x)", "Hyperbolic tangent"),
            new("deg(x)", "Radians to degrees"), new("rad(x)", "Degrees to radians"),
        }),
        new("Addon", new List<Entry>
        {
            new("max(x,y)", "Max of x and y (2 or more arguments)"), new("min(x,y)", "Min of x and y (2 or more arguments)"), new("ceil(x)", "Ceil x"), new("floor(x)", "Floor x"),
            new("trunc(x)", "Integer part of x (towards zero)"), new("frac(x)", "Fractional part of x"), new("round(x,y)", "Round x to nearest y (y optional, default 1)"),
            new("abs(x)", "Absolute value of x"), new("sgn(x)", "Sign of x (1 if x=0)"), new("sqrt(x)", "Square root of x"), new("cbrt(x)", "Cube root of x"),
            new("pow(x,y)", "x to the power y"), new("exp(x)", "e to the power x"), new("log(x,b)", "Logarithm of x (b optional, default e)"), new("log10(x)", "Base-10 logarithm"),
            new("log2(x)", "Base-2 logarithm"), new("fmod(x,y)", "Remainder of x / y"), new("len(x,y)", "Length of vector(x,y)"), new("len2(x,y)", "len(x,y) (no sqrt)"),
            new("lerp(x,y,z)", "Linearly interpolate x to y by z"), new("clamp(x,y,z)", "Clamp x within y and z"),
        }),
        new("Conversions", new List<Entry>
        {
            new("x * 3.6", "m/s to kmph"), new("x * 2.236936", "m/s to mph"), new("x * 1.943844", "m/s to knots"), new("x * pi2", "Turns to radians"),
            new("x * (pi/180)", "Degrees to radians"), new("x * (180/pi)", "Radians to degrees"),
        }),
    };
}
