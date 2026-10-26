using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.Editor.Ui;

/// <summary>
/// Панель параметров выбранного блока (инструмент «Parameters», <see cref="ToolMode.Parameters"/>) — слева на экране. Строится ПО СХЕМЕ блока
/// (<see cref="ParametersComponent"/>), а не по коду конкретного блока; виджет выбирает ОДНО правило (<see cref="ParameterDefinition.Widget"/>): число с явно заданными
/// обоими пределами — <b>ползунок</b> (<see cref="HSlider"/> с числом справа; значение уходит в блок, когда ползунок отпустили, а не на каждый пиксель - иначе каждый
/// сдвиг был бы правкой для Undo и сети), число без пределов (или с одним) — <b>числовое поле</b> (<see cref="SpinBox"/>: Enter/стрелки/потеря фокуса), флаг и перечисление
/// (несколько определённых вариантов) — <b>выпадающий список</b> (<see cref="OptionButton"/>; у флага варианты false/true), цвет — <see cref="ColorPickerButton"/> (значение уходит
/// при закрытии окна выбора); параметры с одной <see cref="ParameterDefinition.Group"/> идут подряд под заголовком, у параметра может быть подсказка.
/// Панель ничего не меняет сама — отдаёт <see cref="ParameterEdited"/> (id, введённый текст), а редактор применяет правку тем же путём, что и остальные
/// (<see cref="NetEditKind.SetParameter"/> → Undo/сеть) и затем зовёт <see cref="Refresh"/>, чтобы показать нормализованное значение (например, зажатое в границы).
/// </summary>
public sealed class ParametersPanelUi
{
    private sealed record Row(ParameterDefinition Definition, Control Control);

    private readonly PanelContainer _panel;
    private readonly VBoxContainer _content;
    private readonly ScrollContainer _scroll;
    private readonly Label _title;
    private readonly List<Row> _rows = new();
    private readonly Dictionary<string, Label> _expressionErrors = new();
    private readonly List<(string Title, Control Body, Button Header)> _referenceSections = new();
    private bool _refreshing;

    /// <summary>Игрок ввёл значение параметра: идентификатор параметра и введённый текст (ещё не нормализованный).</summary>
    public event Action<string, string>? ParameterEdited;

    /// <summary>Игрок закрыл панель кнопкой ×.</summary>
    public event Action? CloseRequested;

    /// <summary>Экземпляр, чьи параметры сейчас показаны; 0 — панель закрыта.</summary>
    public int InstanceId { get; private set; }

    public bool IsOpen => _panel.Visible;

    /// <summary>Прямоугольник панели на экране — клики по нему не должны попадать в мир (см. <see cref="EditorUi.IsPointOverUi"/>).</summary>
    public Rect2 GlobalRect => _panel.GetGlobalRect();

    public ParametersPanelUi(Control layerRoot)
    {
        _panel = new PanelContainer { Name = "ParametersPanel", Visible = false, CustomMinimumSize = new Vector2(380, 0) };
        _panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(12f));
        _panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _panel.Position = new Vector2(12, 78);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(column);

        var header = new HBoxContainer();
        _title = UiStyle.MakeLabel("", 18);
        _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(_title);
        var close = UiStyle.MakeButton("×", new Vector2(30, 28));
        close.TooltipText = "Close (Esc)";
        close.Pressed += () => CloseRequested?.Invoke();
        header.AddChild(close);
        column.AddChild(header);
        column.AddChild(new HSeparator());

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(356, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _scroll = scroll;
        column.AddChild(scroll);
        _content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _content.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_content);

        layerRoot.AddChild(_panel);
    }

    /// <summary>Идентификаторы показанных параметров в порядке схемы (для самотестов).</summary>
    public IReadOnlyList<string> ParameterIds => _rows.Select(r => r.Definition.Id).ToList();

    /// <summary>Строка с заголовком панели (название блока) — для самотестов.</summary>
    public string Title => _title.Text;

    /// <summary>Открывает панель для блока: перестраивает поля по схеме и подставляет текущие значения экземпляра.</summary>
    public void Open(BlockInstance instance, BlockDefinition definition, ParametersComponent schema)
    {
        foreach (var child in _content.GetChildren()) child.QueueFree();
        _rows.Clear();
        _expressionErrors.Clear();
        _referenceSections.Clear();
        InstanceId = instance.InstanceId;
        _title.Text = definition.Name;

        string? lastGroup = null;
        foreach (var parameter in schema.Parameters)
        {
            if (parameter.Group.Length > 0 && parameter.Group != lastGroup)
            {
                var group = UiStyle.MakeLabel(parameter.Group, 13, UiStyle.Accent);
                group.CustomMinimumSize = new Vector2(0, 22);
                _content.AddChild(group);
            }

            lastGroup = parameter.Group;
            AddRow(parameter);
        }

        _panel.Visible = true;
        Refresh(instance);
        FitListHeight();
        Callable.From(FitListHeight).CallDeferred(); // размеры строк окончательны только после раскладки - подгоняем ещё раз в конце кадра
    }

    /// <summary>
    /// Высота области списка = высота содержимого, но не больше свободного места в окне (дальше — прокрутка). <see cref="ScrollContainer"/> сам вертикальный минимум не наследует
    /// от содержимого (только по оси без прокрутки), поэтому без явной высоты список схлопывался в нуль и в панели не было видно ни одного параметра. Вызывается при открытии и при
    /// раскрытии/сворачивании справочника выражения (меняет высоту содержимого).
    /// </summary>
    private void FitListHeight()
    {
        if (!_panel.Visible) return;

        double content = _content.GetCombinedMinimumSize().Y;
        double room = _panel.GetViewportRect().Size.Y - _panel.Position.Y - 76.0; // заголовок панели, разделитель и отступы
        double height = Mathf.Clamp(content + 6.0, 48.0, Mathf.Max(room, 120.0));
        _scroll.CustomMinimumSize = new Vector2(356, height);
        _panel.ResetSize();
    }

    public void Close()
    {
        _panel.Visible = false;
        InstanceId = 0;
    }

    private void AddRow(ParameterDefinition parameter)
    {
        var row = new HBoxContainer();
        var label = UiStyle.MakeLabel(parameter.Label, 13);
        label.CustomMinimumSize = new Vector2(112, 0);
        label.TooltipText = parameter.Hint;
        row.AddChild(label);

        Control control;
        bool alreadyInRow = false;
        switch (parameter.Widget)
        {
            case ParameterWidget.Slider:
            {
                bool integer = parameter.Type == ParameterType.Int;
                var slider = new HSlider
                {
                    MinValue = parameter.Min,
                    MaxValue = parameter.Max,
                    Step = integer ? 1 : parameter.Step > 0 ? parameter.Step : (parameter.Max - parameter.Min) / 200,
                    CustomMinimumSize = new Vector2(150, 28),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    FocusMode = Control.FocusModeEnum.None,
                    Scrollable = false, // колесо мыши над ползунком листает панель, а не меняет значение
                    TooltipText = parameter.Hint,
                };
                var readout = UiStyle.MakeLabel("", 13);
                readout.CustomMinimumSize = new Vector2(56, 0);
                readout.HorizontalAlignment = HorizontalAlignment.Right;
                slider.ValueChanged += value => readout.Text = ParameterDefinition.FormatNumber(value);
                // Значение уходит в блок, когда ползунок отпустили (перетаскивание или клик по полосе).
                slider.DragEnded += changed =>
                {
                    if (changed) Emit(parameter, ParameterDefinition.FormatNumber(slider.Value));
                };
                row.AddChild(slider);
                row.AddChild(readout);
                control = slider;
                alreadyInRow = true; // ползунок и число справа уже в строке (второй AddChild движок отвергал с ошибкой в логе)
                break;
            }

            case ParameterWidget.Dropdown:
            {
                var dropdown = new OptionButton { CustomMinimumSize = new Vector2(150, 28), FocusMode = Control.FocusModeEnum.None };
                foreach (string option in DropdownOptions(parameter)) dropdown.AddItem(option);
                dropdown.ItemSelected += index => Emit(parameter, DropdownOptions(parameter)[(int)index]);
                control = dropdown;
                break;
            }

            case ParameterWidget.ColorPicker:
            {
                var picker = new ColorPickerButton { CustomMinimumSize = new Vector2(150, 28), EditAlpha = false, FocusMode = Control.FocusModeEnum.None };
                // Окно выбора шлёт ColorChanged на каждое движение - в блок значение уходит, когда окно закрыли.
                picker.PopupClosed += () => Emit(parameter, "#" + picker.Color.ToHtml(false));
                control = picker;
                break;
            }

            case ParameterWidget.ExpressionBox:
            {
                var field = new LineEdit { CustomMinimumSize = new Vector2(230, 28), TooltipText = parameter.Hint, PlaceholderText = "x" };
                var error = UiStyle.MakeLabel("", 11, new Color(1f, 0.45f, 0.4f));
                error.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                error.CustomMinimumSize = new Vector2(340, 0);
                _expressionErrors[parameter.Id] = error;
                field.TextChanged += text => ShowExpressionError(error, text);
                field.TextSubmitted += text => Emit(parameter, text);
                field.FocusExited += () => Emit(parameter, field.Text);
                control = field;
                break;
            }

            default:
            {
                var box = new SpinBox
                {
                    MinValue = double.IsFinite(parameter.Min) ? parameter.Min : -1e12,
                    MaxValue = double.IsFinite(parameter.Max) ? parameter.Max : 1e12,
                    Step = parameter.Type == ParameterType.Int ? 1 : parameter.Step > 0 ? parameter.Step : 0.0001,
                    AllowGreater = !double.IsFinite(parameter.Max),
                    AllowLesser = !double.IsFinite(parameter.Min),
                    CustomMinimumSize = new Vector2(150, 28),
                    TooltipText = parameter.Hint,
                };
                box.ValueChanged += value => Emit(parameter, ParameterDefinition.FormatNumber(value));
                control = box;
                break;
            }
        }

        if (!alreadyInRow) row.AddChild(control);
        _content.AddChild(row);
        _rows.Add(new Row(parameter, control));

        if (parameter.Widget == ParameterWidget.ExpressionBox)
        {
            _content.AddChild(_expressionErrors[parameter.Id]);
            AddExpressionReference();
        }

        if (parameter.Hint.Length > 0)
        {
            var hint = UiStyle.MakeLabel(parameter.Hint, 11, UiStyle.TextDim);
            hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            hint.CustomMinimumSize = new Vector2(340, 0);
            _content.AddChild(hint);
        }
    }

    private static void ShowExpressionError(Label label, string text)
    {
        string? error = MathExpression.Validate(text);
        label.Text = error == null ? "" : "Error: " + error + " (the block outputs 0)";
    }

    private static readonly Color FunctionColor = new(1f, 0.31f, 0.64f);
    private static readonly Color ArgumentColor = new(0.31f, 0.85f, 0.91f);
    private static readonly Color OperatorColor = new(0.61f, 0.91f, 0.31f);

    /// <summary>Запись справки с подсветкой, как в справочнике Stormworks: имена функций розовым, переменные/числа/константы голубым, операторы зелёным (BBCode).</summary>
    public static string Colorize(string snippet)
    {
        var builder = new System.Text.StringBuilder();
        int i = 0;
        while (i < snippet.Length)
        {
            char c = snippet[i];
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < snippet.Length && (char.IsLetterOrDigit(snippet[i]) || snippet[i] == '_')) i++;
                string name = snippet[start..i];
                int next = i;
                while (next < snippet.Length && snippet[next] == ' ') next++;
                bool isFunction = next < snippet.Length && snippet[next] == '(';
                builder.Append($"[color=#{(isFunction ? FunctionColor : ArgumentColor).ToHtml(false)}]{name}[/color]");
            }
            else if (char.IsDigit(c) || c == '.')
            {
                int start = i;
                while (i < snippet.Length && (char.IsDigit(snippet[i]) || snippet[i] == '.')) i++;
                builder.Append($"[color=#{ArgumentColor.ToHtml(false)}]{snippet[start..i]}[/color]");
            }
            else if ("+-*/%^".IndexOf(c) >= 0)
            {
                builder.Append($"[color=#{OperatorColor.ToHtml(false)}]{c}[/color]");
                i++;
            }
            else
            {
                builder.Append(c);
                i++;
            }
        }

        return builder.ToString();
    }

    /// <summary>Вложенные списки справки под полем выражения: заголовок-кнопка раскрывает/сворачивает таблицу «запись — что делает» (по умолчанию свёрнуты).</summary>
    private void AddExpressionReference()
    {
        foreach (var group in MathExpressionReference.Groups)
        {
            var header = new Button { Text = group.Title + "  \u25B6", Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(340, 30), FocusMode = Control.FocusModeEnum.None };
            var grid = new GridContainer { Columns = 2, Visible = false };
            grid.AddThemeConstantOverride("h_separation", 14);
            grid.AddThemeConstantOverride("v_separation", 6);
            foreach (var entry in group.Entries)
            {
                var snippet = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, CustomMinimumSize = new Vector2(150, 22), Text = Colorize(entry.Snippet) };
                var description = UiStyle.MakeLabel(entry.Description, 13);
                description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                description.CustomMinimumSize = new Vector2(180, 0);
                grid.AddChild(snippet);
                grid.AddChild(description);
            }

            header.Pressed += () => SetReferenceExpanded(group.Title, !grid.Visible);
            _content.AddChild(header);
            _content.AddChild(grid);
            _referenceSections.Add((group.Title, grid, header));
        }
    }

    private void SetReferenceExpanded(string title, bool expanded)
    {
        foreach (var (sectionTitle, body, header) in _referenceSections)
        {
            if (sectionTitle != title) continue;
            body.Visible = expanded;
            header.Text = title + (expanded ? "  \u25BC" : "  \u25B6");
        }

        Callable.From(FitListHeight).CallDeferred(); // содержимое стало выше/ниже
    }

    /// <summary>Заголовки вложенных списков справки в порядке показа (для самотестов).</summary>
    public IReadOnlyList<string> ReferenceTitles => _referenceSections.Select(r => r.Title).ToList();

    /// <summary>Развёрнут ли список справки (для самотестов).</summary>
    public bool ReferenceExpanded(string title) => _referenceSections.First(r => r.Title == title).Body.Visible;

    /// <summary>Число записей в списке справки (для самотестов).</summary>
    public int ReferenceEntryCount(string title) => _referenceSections.First(r => r.Title == title).Body.GetChildCount() / 2;

    /// <summary>Нажимает заголовок списка справки, как игрок (для самотестов).</summary>
    public void ToggleReferenceForTesting(string title) => _referenceSections.First(r => r.Title == title).Header.EmitSignal(BaseButton.SignalName.Pressed);

    /// <summary>Текст ошибки выражения под полем (пусто — выражение верно; для самотестов).</summary>
    public string ExpressionError(string parameterId) => _expressionErrors[parameterId].Text;

    /// <summary>Варианты выпадающего списка: у перечисления - его варианты, у флага - false/true.</summary>
    private static IReadOnlyList<string> DropdownOptions(ParameterDefinition parameter) =>
        parameter.Type == ParameterType.Bool ? BoolOptions : parameter.Options;

    private static readonly string[] BoolOptions = { "false", "true" };

    private void Emit(ParameterDefinition parameter, string text)
    {
        if (_refreshing || InstanceId == 0) return;
        ParameterEdited?.Invoke(parameter.Id, text);
    }

    /// <summary>Показывает текущие значения экземпляра (изменённое или умолчание); вызывается после любой правки и после Undo/Redo.</summary>
    public void Refresh(BlockInstance instance)
    {
        _refreshing = true;
        foreach (var (definition, control) in _rows)
        {
            string value = instance.Parameters != null && instance.Parameters.TryGetValue(definition.Id, out var stored) ? stored : definition.Default;
            switch (control)
            {
                case HSlider slider:
                    slider.SetValueNoSignal(ParseNumber(value));
                    if (slider.GetParent() is { } sliderRow && sliderRow.GetChildCount() > 2 && sliderRow.GetChild(2) is Label readout) readout.Text = ParameterDefinition.FormatNumber(slider.Value);
                    break;

                case OptionButton dropdown:
                    dropdown.Selected = Math.Max(0, DropdownOptions(definition).ToList().IndexOf(value));
                    break;

                case ColorPickerButton picker:
                    if (Color.HtmlIsValid(value)) picker.Color = Color.FromHtml(value);
                    break;

                case SpinBox box when !box.GetLineEdit().HasFocus():
                    box.SetValueNoSignal(ParseNumber(value));
                    break;

                case LineEdit field when !field.HasFocus():
                    field.Text = value;
                    if (_expressionErrors.TryGetValue(definition.Id, out var errorLabel)) ShowExpressionError(errorLabel, value);
                    break;
            }
        }

        _refreshing = false;
    }

    private static double ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;

    /// <summary>Для самотестов: вводит значение в поле параметра так, как это сделал бы игрок (вызывает то же событие).</summary>
    public void EditForTesting(string parameterId, string text)
    {
        var row = _rows.First(r => r.Definition.Id == parameterId);
        Emit(row.Definition, text);
    }

    /// <summary>Для самотестов: текст, который сейчас показан в виджете параметра (число - в канонической форме, цвет - #rrggbb).</summary>
    public string DisplayedValue(string parameterId)
    {
        var row = _rows.First(r => r.Definition.Id == parameterId);
        return row.Control switch
        {
            HSlider slider => ParameterDefinition.FormatNumber(slider.Value),
            SpinBox box => ParameterDefinition.FormatNumber(box.Value),
            OptionButton dropdown => dropdown.GetItemText(dropdown.Selected),
            ColorPickerButton picker => "#" + picker.Color.ToHtml(false),
            LineEdit field => field.Text,
            _ => "",
        };
    }

    /// <summary>Для самотестов: ставит фокус в первое числовое поле панели (как будто игрок кликнул в него); false — числовых полей нет.</summary>
    public bool FocusFirstTextFieldForTesting()
    {
        var box = _rows.Select(r => r.Control).OfType<SpinBox>().FirstOrDefault();
        box?.GetLineEdit().GrabFocus();
        return box != null;
    }

    /// <summary>Для самотестов: сам виджет параметра.</summary>
    public Control ControlOf(string parameterId) => _rows.First(r => r.Definition.Id == parameterId).Control;

    /// <summary>Для самотестов: прямоугольник строки параметра (подпись + виджет) на экране - если панель схлопнула область списка, высота/положение это покажут.</summary>
    public Rect2 RowGlobalRect(string parameterId) => _rows.First(r => r.Definition.Id == parameterId).Control.GetParent<Control>().GetGlobalRect();

    /// <summary>Для самотестов: видна ли строка параметра ЦЕЛИКОМ внутри области прокрутки панели (не обрезана нулевой высотой).</summary>
    public bool RowFullyVisible(string parameterId)
    {
        var row = RowGlobalRect(parameterId);
        var area = _scroll.GetGlobalRect();
        return _panel.Visible && row.Size.Y > 8 && area.Size.Y > 8 && row.Position.Y >= area.Position.Y - 0.5f && row.End.Y <= area.End.Y + 0.5f;
    }

    /// <summary>Для самотестов: область прокрутки со списком параметров.</summary>
    public Rect2 ListGlobalRect => _scroll.GetGlobalRect();

    /// <summary>Для самотестов: тип виджета у параметра.</summary>
    public string ControlKind(string parameterId) => _rows.First(r => r.Definition.Id == parameterId).Control.GetType().Name;
}
