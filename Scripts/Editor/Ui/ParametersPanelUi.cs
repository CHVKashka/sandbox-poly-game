using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Editor.Ui;

/// <summary>
/// Панель параметров выбранного блока (инструмент «Parameters», <see cref="ToolMode.Parameters"/>) — слева на экране. Строится ПО СХЕМЕ блока
/// (<see cref="ParametersComponent"/>), а не по коду конкретного блока: числа — поле ввода (применяется по Enter/потере фокуса), флаг — галочка,
/// перечисление — выпадающий список; параметры с одной <see cref="ParameterDefinition.Group"/> идут подряд под заголовком, у параметра может быть подсказка.
/// Панель ничего не меняет сама — отдаёт <see cref="ParameterEdited"/> (id, введённый текст), а редактор применяет правку тем же путём, что и остальные
/// (<see cref="NetEditKind.SetParameter"/> → Undo/сеть) и затем зовёт <see cref="Refresh"/>, чтобы показать нормализованное значение (например, зажатое в границы).
/// </summary>
public sealed class ParametersPanelUi
{
    private sealed record Row(ParameterDefinition Definition, Control Control);

    private readonly PanelContainer _panel;
    private readonly VBoxContainer _content;
    private readonly Label _title;
    private readonly List<Row> _rows = new();
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
        _panel = new PanelContainer { Name = "ParametersPanel", Visible = false, CustomMinimumSize = new Vector2(340, 0) };
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

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(316, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
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
        switch (parameter.Type)
        {
            case ParameterType.Bool:
            {
                var check = new CheckBox { FocusMode = Control.FocusModeEnum.None };
                check.Toggled += on => Emit(parameter, on ? "true" : "false");
                control = check;
                break;
            }

            case ParameterType.Enum:
            {
                var dropdown = new OptionButton { CustomMinimumSize = new Vector2(150, 28), FocusMode = Control.FocusModeEnum.None };
                foreach (string option in parameter.Options) dropdown.AddItem(option);
                dropdown.ItemSelected += index => Emit(parameter, parameter.Options[(int)index]);
                control = dropdown;
                break;
            }

            default:
            {
                var field = new LineEdit { CustomMinimumSize = new Vector2(150, 28), TooltipText = parameter.Hint };
                field.TextSubmitted += text => Emit(parameter, text);
                field.FocusExited += () => Emit(parameter, field.Text);
                control = field;
                break;
            }
        }

        row.AddChild(control);
        _content.AddChild(row);
        _rows.Add(new Row(parameter, control));

        if (parameter.Hint.Length > 0)
        {
            var hint = UiStyle.MakeLabel(parameter.Hint, 11, UiStyle.TextDim);
            hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            hint.CustomMinimumSize = new Vector2(300, 0);
            _content.AddChild(hint);
        }
    }

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
                case CheckBox check:
                    check.SetPressedNoSignal(value == "true");
                    break;

                case OptionButton dropdown:
                    dropdown.Selected = Math.Max(0, Array.IndexOf(definition.Options.ToArray(), value));
                    break;

                case LineEdit field when !field.HasFocus():
                    field.Text = value;
                    break;
            }
        }

        _refreshing = false;
    }

    /// <summary>Для самотестов: вводит значение в поле параметра так, как это сделал бы игрок (вызывает то же событие).</summary>
    public void EditForTesting(string parameterId, string text)
    {
        var row = _rows.First(r => r.Definition.Id == parameterId);
        Emit(row.Definition, text);
    }

    /// <summary>Для самотестов: текст, который сейчас показан в поле/списке параметра.</summary>
    public string DisplayedValue(string parameterId)
    {
        var row = _rows.First(r => r.Definition.Id == parameterId);
        return row.Control switch
        {
            CheckBox check => check.ButtonPressed ? "true" : "false",
            OptionButton dropdown => dropdown.GetItemText(dropdown.Selected),
            LineEdit field => field.Text,
            _ => "",
        };
    }

    /// <summary>Для самотестов: ставит фокус в первое текстовое поле панели (как будто игрок кликнул в него); false — текстовых полей нет.</summary>
    public bool FocusFirstTextFieldForTesting()
    {
        var field = _rows.Select(r => r.Control).OfType<LineEdit>().FirstOrDefault();
        field?.GrabFocus();
        return field != null;
    }

    /// <summary>Для самотестов: тип виджета у параметра.</summary>
    public string ControlKind(string parameterId) => _rows.First(r => r.Definition.Id == parameterId).Control.GetType().Name;
}
