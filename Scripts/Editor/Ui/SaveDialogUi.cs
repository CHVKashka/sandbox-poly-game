using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Editor.Ui;

/// <summary>
/// Окно кнопки Save на тулбаре — список ВСЕХ сохранённых построек (тот же список, что в меню верстака при входе в редактор, только без Create/Join/Spawn)
/// и поля имени/описания справа. Первой строкой списка всегда стоит <b>New vehicle</b>:
/// <list type="bullet">
/// <item><b>New vehicle</b> + имя → <b>Save</b> — постройка записывается НОВЫМ файлом (имя занято — к файлу добавится « (2)», чужое сохранение не затирается);</item>
/// <item>любая сохранённая постройка в списке → <b>Overwrite</b> — её файл перезаписывается текущей постройкой (дата создания сохраняется, имя/описание можно поправить).</item>
/// </list>
/// При открытии заранее выбрана постройка, с которой игрок сейчас работает (загруженная/уже сохранённая), — тогда обычное повторное сохранение — один клик по
/// Overwrite; несохранённая постройка открывает окно на New vehicle. Тот же паттерн модального окна, что у <see cref="BlockPickerUi"/> (затемнение на весь
/// экран + центрированная панель). Само окно ничего не пишет на диск — оно отдаёт выбор через <see cref="Confirmed"/>, пишет <see cref="EditorUi"/>.
/// </summary>
internal sealed class SaveDialogUi
{
    private readonly ColorRect _overlay;
    private readonly VBoxContainer _listColumn;
    private readonly Button _newButton;
    private readonly List<(ConstructionStorage.SavedConstruction Entry, Button Button)> _entries = new();
    private readonly Label _title;
    private readonly Label _subtitle;
    private readonly TextureRect _previewImage;
    private readonly LineEdit _nameField;
    private readonly TextEdit _descriptionField;
    private readonly Label _hint;
    private readonly Button _actionButton;

    private ConstructionStorage.SavedConstruction? _selected; // null - выбрана строка "New vehicle"
    private string _newName = "";
    private string _newDescription = "";

    /// <summary>Выбор игрока: путь файла для перезаписи (null — сохранить НОВЫМ файлом), имя и описание ("" — не null, если оставлено пустым).</summary>
    public event Action<string?, string, string>? Confirmed;

    public SaveDialogUi(Control layerRoot)
    {
        _overlay = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.6f),
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var center = UiStyle.Transparent(new CenterContainer());
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _overlay.AddChild(center);

        var window = new PanelContainer { CustomMinimumSize = new Vector2(820, 540) };
        window.AddThemeStyleboxOverride("panel", UiStyle.Panel(16f));
        center.AddChild(window);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        window.AddChild(row);

        // ------------------------------------------------------------ слева: New vehicle + все сохранённые постройки
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        left.AddThemeConstantOverride("separation", 8);
        row.AddChild(left);

        left.AddChild(UiStyle.MakeLabel("Save construction", 22));
        left.AddChild(UiStyle.MakeLabel("Pick a saved construction to overwrite, or save as a new one", 12, UiStyle.TextDim));

        var listScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        left.AddChild(listScroll);
        _listColumn = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _listColumn.AddThemeConstantOverride("separation", 4);
        listScroll.AddChild(_listColumn);

        _newButton = UiStyle.MakeButton("New vehicle", new Vector2(0, 44), toggle: true);
        _newButton.Alignment = HorizontalAlignment.Left;
        _newButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _newButton.Pressed += () => Select(null);
        _listColumn.AddChild(_newButton);

        // ------------------------------------------------------------ справа: превью выбранной + имя/описание + кнопки
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
        right.AddThemeConstantOverride("separation", 8);
        row.AddChild(right);

        _title = UiStyle.MakeLabel("", 18);
        right.AddChild(_title);
        _subtitle = UiStyle.MakeLabel("", 12, UiStyle.TextDim);
        right.AddChild(_subtitle);

        var previewFrame = new PanelContainer { CustomMinimumSize = new Vector2(0, 170) };
        previewFrame.AddThemeStyleboxOverride("panel", UiStyle.Box(new Color(0, 0, 0, 0.3f), UiStyle.PanelBorder, 1, 6, 0f));
        right.AddChild(previewFrame);
        _previewImage = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        previewFrame.AddChild(_previewImage);

        right.AddChild(UiStyle.MakeLabel("Vehicle name", 13, UiStyle.TextDim));
        _nameField = new LineEdit { PlaceholderText = "My Boat", CustomMinimumSize = new Vector2(0, 32) };
        _nameField.TextSubmitted += _ => TryConfirm();
        right.AddChild(_nameField);

        right.AddChild(UiStyle.MakeLabel("Description (optional)", 13, UiStyle.TextDim));
        _descriptionField = new TextEdit { CustomMinimumSize = new Vector2(0, 80), WrapMode = TextEdit.LineWrappingMode.Boundary };
        right.AddChild(_descriptionField);

        _hint = UiStyle.MakeLabel("", 12, new Color(1f, 0.45f, 0.45f));
        _hint.Visible = false;
        right.AddChild(_hint);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 8);
        right.AddChild(actions);
        var cancel = UiStyle.MakeButton("Cancel", new Vector2(0, 36));
        cancel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        cancel.Pressed += Close;
        actions.AddChild(cancel);
        _actionButton = UiStyle.MakeButton("Save", new Vector2(0, 36));
        _actionButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _actionButton.Pressed += TryConfirm;
        actions.AddChild(_actionButton);

        layerRoot.AddChild(_overlay);
    }

    public bool IsOpen => _overlay.Visible;

    // ------------------------------------------------------------------ для самотестов

    /// <summary>Подписи кнопок списка сверху вниз: сначала «New vehicle», затем сохранённые постройки (имя и даты через перевод строки).</summary>
    public IReadOnlyList<string> ListTexts => _listColumn.GetChildren().OfType<Button>().Select(b => b.Text).ToList();

    public Button NewButton => _newButton;

    /// <summary>Все кнопки окна (список, Cancel, действие) - проверить, что кнопок верстака здесь нет.</summary>
    public IEnumerable<Button> AllButtons => _overlay.FindChildren("*", "Button", true, false).OfType<Button>();

    public Button ActionButton => _actionButton;

    public LineEdit NameField => _nameField;

    public TextEdit DescriptionField => _descriptionField;

    /// <summary>Файл выбранной строки (null — выбрана New vehicle).</summary>
    public string? SelectedPath => _selected?.Path;

    public Button? EntryButton(string path) => _entries.FirstOrDefault(e => e.Entry.Path == path).Button;

    /// <summary>Выбранная строка видна выделенной (нажатой) — ровно одна.</summary>
    public int PressedRowCount => _listColumn.GetChildren().OfType<Button>().Count(b => b.ButtonPressed);

    // ------------------------------------------------------------------

    /// <summary>
    /// Открывает окно: перечитывает сохранённые постройки с диска (только что сохранённые видны сразу) и выбирает ту, с которой игрок работает
    /// (<paramref name="currentPath"/>, если файл существует), иначе New vehicle — с именем/описанием <paramref name="prefillName"/>/<paramref name="prefillDescription"/>
    /// (то, под чем постройка уже называлась, но файла у неё нет).
    /// </summary>
    public void Open(string? currentPath, string prefillName, string prefillDescription)
    {
        _newName = currentPath == null ? prefillName : "";
        _newDescription = currentPath == null ? prefillDescription : "";

        foreach (var (_, button) in _entries)
        {
            _listColumn.RemoveChild(button);
            button.QueueFree();
        }

        _entries.Clear();
        foreach (var entry in ConstructionStorage.List())
        {
            var button = UiStyle.MakeButton("", new Vector2(0, 44), toggle: true);
            button.Alignment = HorizontalAlignment.Left;
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            button.Text = $"{entry.Name}\n{ConstructionStorage.DescribeDates(entry)}";
            var captured = entry;
            button.Pressed += () => Select(captured);
            _listColumn.AddChild(button);
            _entries.Add((entry, button));
        }

        ConstructionStorage.SavedConstruction? current = null;
        foreach (var (entry, _) in _entries)
        {
            if (entry.Path == currentPath) current = entry;
        }

        Select(current);

        _overlay.Visible = true;
        _nameField.GrabFocus();
    }

    public void Close() => _overlay.Visible = false;

    /// <summary>Выбор строки списка (<c>null</c> — New vehicle): подсветка, превью и поля справа, подпись кнопки действия.</summary>
    public void Select(ConstructionStorage.SavedConstruction? entry)
    {
        _selected = entry;
        _newButton.ButtonPressed = entry == null;
        foreach (var (e, button) in _entries) button.ButtonPressed = entry is { } chosen && e.Path == chosen.Path;

        _hint.Visible = false;
        if (entry is not { } selection)
        {
            _title.Text = "New vehicle";
            _subtitle.Text = "Saved as a new file - nothing existing is replaced.";
            _previewImage.Texture = null;
            _nameField.Text = _newName;
            _descriptionField.Text = _newDescription;
            _actionButton.Text = "Save";
            _actionButton.TooltipText = "Save the construction as a new file";
            return;
        }

        _title.Text = selection.Name;
        _subtitle.Text = ConstructionStorage.DescribeDates(selection);
        _previewImage.Texture = ConstructionStorage.LoadPreview(selection);
        _nameField.Text = selection.Name;
        _descriptionField.Text = selection.Description ?? "";
        _actionButton.Text = "Overwrite";
        _actionButton.TooltipText = $"Replace '{selection.Name}' with the current construction";
    }

    private void TryConfirm()
    {
        string name = _nameField.Text.Trim();
        if (name.Length == 0)
        {
            _hint.Text = "Enter a name for the construction.";
            _hint.Visible = true;
            return;
        }

        Confirmed?.Invoke(_selected?.Path, name, _descriptionField.Text);
        Close();
    }
}
