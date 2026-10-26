using System.Collections.Generic;
using System.Linq;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.Editor.Ui;

/// <summary>
/// Подсказка функционального блока: заголовок (название блока) и таблица «подпись — значение». Один и тот же виджет работает в двух местах: в РЕДАКТОРЕ построек при наведении курсора
/// (<see cref="Show"/>: параметры блока и их текущие значения — значение экземпляра, а если его не меняли — умолчание схемы; включается кнопкой/клавишей <c>T</c>) и в ИГРОВОМ МИРЕ при наведении
/// прицела на блок заспавненной постройки (<see cref="ShowLines"/>: только значения нод, см. <c>World.GameWorld</c>). Не перехватывает мышь, прижимается к границам окна, содержимое
/// пересобирается только при смене ключа (блок/значения), положение обновляется каждый вызов.
/// </summary>
public sealed class BlockTooltipUi
{
    private const float Offset = 18f;

    private readonly PanelContainer _panel;
    private readonly Label _title;
    private readonly Label _hint;
    private readonly VBoxContainer _body;
    private readonly List<(string Label, string Value)> _lines = new();
    private string _key = "";

    public BlockTooltipUi(Control layerRoot)
    {
        _panel = new PanelContainer { Name = "BlockTooltip", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 50 };
        _panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(10f));
        _panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);

        _body = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _body.AddThemeConstantOverride("separation", 3);
        _panel.AddChild(_body);
        _title = UiStyle.MakeLabel("", 15, UiStyle.Accent);
        _title.MouseFilter = Control.MouseFilterEnum.Ignore;
        _body.AddChild(_title);
        _hint = UiStyle.MakeLabel("", 11, UiStyle.TextDim);
        _hint.MouseFilter = Control.MouseFilterEnum.Ignore;

        layerRoot.AddChild(_panel);
    }

    public bool IsVisible => _panel.Visible;

    /// <summary>Название блока в заголовке подсказки.</summary>
    public string Title => _title.Text;

    /// <summary>Строки таблицы (подпись, показанное значение) в порядке показа — для самотестов.</summary>
    public IReadOnlyList<(string Label, string Value)> Lines => _lines;

    /// <summary>Строка-подсказка внизу (например, «RMB - edit properties»); пусто — нет.</summary>
    public string Hint => _hint.Text;

    public Rect2 GlobalRect => _panel.GetGlobalRect();

    /// <summary>Что сейчас показано: слаг + id экземпляра + значения (при их смене содержимое пересобирается).</summary>
    public static string BuildKey(BlockInstance instance) =>
        $"{instance.BlockSlug}#{instance.InstanceId}|" + (instance.Parameters == null ? "" : string.Join(";", instance.Parameters.OrderBy(p => p.Key, System.StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)));

    /// <summary>Редактор построек: подсказка с параметрами блока у курсора <paramref name="mouse"/> (в пределах окна размера <paramref name="viewport"/>).</summary>
    public void Show(BlockInstance instance, BlockDefinition definition, ParametersComponent schema, Vector2 mouse, Vector2 viewport, string hint = "")
    {
        var values = new ParameterSet(schema, instance.Parameters);
        var lines = schema.Parameters.Select(p => (p.Label, values.GetString(p.Id, p.Default))).ToList();
        ShowLines(BuildKey(instance), definition.Name, lines, mouse + new Vector2(Offset, Offset), mouse, viewport, hint);
    }

    /// <summary>
    /// Показывает таблицу <paramref name="lines"/> под заголовком <paramref name="title"/>; <paramref name="topLeft"/> — желаемый левый верхний угол (у границ окна сдвигается внутрь; если справа
    /// не помещается — становится слева от <paramref name="avoid"/>). <paramref name="key"/> меняется, когда содержимое надо пересобрать.
    /// </summary>
    public void ShowLines(string key, string title, IReadOnlyList<(string Label, string Value)> lines, Vector2 topLeft, Vector2 avoid, Vector2 viewport, string hint = "")
    {
        string fullKey = key + "|" + string.Join("|", lines.Select(l => l.Label + "=" + l.Value)) + "|" + hint;
        if (fullKey != _key)
        {
            Rebuild(title, lines, hint);
            _key = fullKey;
        }

        _panel.Visible = true;
        _panel.ResetSize(); // размер по содержимому (контейнер сам не сжимается после смены строк)
        var size = _panel.GetCombinedMinimumSize();
        var position = topLeft;
        if (position.X + size.X > viewport.X) position.X = avoid.X - Offset - size.X; // не лезет справа - слева от курсора
        if (position.Y + size.Y > viewport.Y) position.Y = viewport.Y - size.Y - 4;
        _panel.Position = new Vector2(Mathf.Max(position.X, 0), Mathf.Max(position.Y, 0));
    }

    public void Hide()
    {
        _panel.Visible = false;
        _key = "";
    }

    private void Rebuild(string title, IReadOnlyList<(string Label, string Value)> lines, string hint)
    {
        if (_hint.GetParent() != null) _body.RemoveChild(_hint); // подсказку не освобождаем - она переиспользуется
        foreach (var child in _body.GetChildren())
        {
            if (child == _title) continue;
            _body.RemoveChild(child); // сразу: иначе до конца кадра старые строки учитываются в размере подсказки
            child.QueueFree();
        }

        _lines.Clear();
        _title.Text = title;

        var grid = new GridContainer { Columns = 2, MouseFilter = Control.MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 2);
        _body.AddChild(grid);

        foreach (var (label, value) in lines)
        {
            _lines.Add((label, value));
            var name = UiStyle.MakeLabel(label, 13, UiStyle.TextDim);
            var shown = UiStyle.MakeLabel(value, 13);
            name.MouseFilter = Control.MouseFilterEnum.Ignore;
            shown.MouseFilter = Control.MouseFilterEnum.Ignore;
            grid.AddChild(name);
            grid.AddChild(shown);
        }

        _hint.Text = hint;
        if (hint.Length > 0) _body.AddChild(_hint);
    }
}
