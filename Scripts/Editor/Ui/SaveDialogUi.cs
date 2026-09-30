using System;
using Godot;

namespace SandboxPolyGame.Editor.Ui;

/// <summary>
/// Диалог сохранения постройки (кнопка Save на тулбаре) — поля "Vehicle name"/"Description" (опционально) и кнопки
/// Done/Cancel, вместо произвольного выбора пути через <c>FileDialog</c> (см.
/// Docs/05-world-and-vehicle-systems.md, «Сохранение построек» — путь на диске теперь выбирается автоматически,
/// <c>Core.ConstructionStorage</c>, чтобы постройки были видны в окне выбора на верстаке по имени/дате/превью, а
/// не по произвольному пути). Тот же паттерн модального окна, что и у <see cref="BlockPickerUi"/> (затемнение на
/// весь экран + центрированная панель), не нативный <c>Window</c>/<c>AcceptDialog</c>.
/// </summary>
internal sealed class SaveDialogUi
{
    private readonly ColorRect _overlay;
    private readonly LineEdit _nameField;
    private readonly TextEdit _descriptionField;
    private readonly Label _hint;

    /// <summary>Имя и описание, введённые в момент нажатия Done (описание — "", не null, если оставлено пустым).</summary>
    public event Action<string, string>? Confirmed;

    public SaveDialogUi(Control layerRoot)
    {
        _overlay = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.55f),
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var center = UiStyle.Transparent(new CenterContainer());
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _overlay.AddChild(center);

        var window = new PanelContainer { CustomMinimumSize = new Vector2(380, 0) };
        window.AddThemeStyleboxOverride("panel", UiStyle.Panel(16f));
        center.AddChild(window);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        column.CustomMinimumSize = new Vector2(340, 0);
        window.AddChild(column);

        column.AddChild(UiStyle.MakeLabel("Save construction", 20));

        column.AddChild(UiStyle.MakeLabel("Vehicle name", 13, UiStyle.TextDim));
        _nameField = new LineEdit { PlaceholderText = "My Boat", CustomMinimumSize = new Vector2(0, 32) };
        _nameField.TextSubmitted += _ => TryConfirm();
        column.AddChild(_nameField);

        column.AddChild(UiStyle.MakeLabel("Description (optional)", 13, UiStyle.TextDim));
        _descriptionField = new TextEdit { CustomMinimumSize = new Vector2(0, 90), WrapMode = TextEdit.LineWrappingMode.Boundary };
        column.AddChild(_descriptionField);

        _hint = UiStyle.MakeLabel("", 12, new Color(1f, 0.45f, 0.45f));
        _hint.Visible = false;
        column.AddChild(_hint);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var cancel = UiStyle.MakeButton("Cancel", new Vector2(110, 34));
        cancel.Pressed += Close;
        row.AddChild(cancel);
        var done = UiStyle.MakeButton("Done", new Vector2(110, 34));
        done.Pressed += TryConfirm;
        row.AddChild(done);
        column.AddChild(row);

        layerRoot.AddChild(_overlay);
    }

    public bool IsOpen => _overlay.Visible;

    /// <summary>Открывает диалог, предзаполняя поля значениями последнего сохранения этой постройки в текущей
    /// сессии (пусто — при самом первом сохранении).</summary>
    public void Open(string prefillName, string prefillDescription)
    {
        _nameField.Text = prefillName;
        _descriptionField.Text = prefillDescription;
        _hint.Visible = false;
        _overlay.Visible = true;
        _nameField.GrabFocus();
    }

    public void Close() => _overlay.Visible = false;

    private void TryConfirm()
    {
        string name = _nameField.Text.Trim();
        if (name.Length == 0)
        {
            _hint.Text = "Enter a name for the construction.";
            _hint.Visible = true;
            return;
        }

        Confirmed?.Invoke(name, _descriptionField.Text);
        Close();
    }
}
