using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor.Ui;

namespace SandboxPolyGame.World.Ui;

/// <summary>
/// Меню верстака (см. Docs/05-world-and-vehicle-systems.md, «Вход в редактор через верстак»): открывается при
/// взаимодействии с <see cref="Workbench"/>, до того, как игрок реально попадёт в редактор.
/// <list type="bullet">
/// <item><b>Create vehicle</b> — сразу переход в редактор с пустой (только корневой блок) постройкой.</item>
/// <item><b>Join to workbench</b> — присоединиться к уже идущей сессии редактора на этом верстаке (совместное
/// редактирование, см. Docs); пока всегда недоступна — сети ещё нет, присоединяться не к чему.</item>
/// <item>Список сохранённых построек (<see cref="ConstructionStorage.List"/>) — клик по строке показывает её
/// превью+описание справа, кнопка <b>Open</b> под описанием переходит в редактор с этой постройкой загруженной.</item>
/// </list>
/// Тот же паттерн модального окна, что и <c>Editor.Ui.BlockPickerUi</c>/<c>SaveDialogUi</c> (затемнение на весь
/// экран + центрированная панель) — переиспользует <see cref="UiStyle"/> (internal, но тот же assembly).
/// </summary>
public sealed class WorkbenchMenuUi
{
    private readonly ColorRect _overlay;
    private readonly VBoxContainer _listColumn;
    private readonly TextureRect _previewImage;
    private readonly Label _previewName;
    private readonly Label _previewDescription;
    private readonly Button _openButton;
    private readonly Button _spawnButton;
    private readonly Button _createButton;
    private readonly Button _joinButton;
    private readonly Label _emptyHint;

    private ConstructionStorage.SavedConstruction? _selected;
    private string _workbenchName = "";

    /// <summary>Нажата Create vehicle — открыть редактор пустым.</summary>
    public event Action? CreateVehicleRequested;

    /// <summary>Нажата Open для выбранной постройки — открыть редактор с ней (путь к файлу).</summary>
    public event Action<string>? OpenConstructionRequested;

    /// <summary>Нажата Spawn для выбранной постройки — заспавнить её в мире, минуя редактор (путь к файлу).</summary>
    public event Action<string>? SpawnConstructionRequested;

    /// <summary>Нажата Join to workbench — попроситься в уже идущую сессию на этом верстаке (см. <see cref="Open"/>).</summary>
    public event Action? JoinRequested;

    public WorkbenchMenuUi(Control layerRoot)
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

        var window = new PanelContainer { CustomMinimumSize = new Vector2(820, 520) };
        window.AddThemeStyleboxOverride("panel", UiStyle.Panel(16f));
        center.AddChild(window);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        window.AddChild(row);

        // ------------------------------------------------------------ левая колонка: Create/Join + список построек
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        left.AddThemeConstantOverride("separation", 8);
        row.AddChild(left);

        left.AddChild(UiStyle.MakeLabel("Workbench", 22));

        _createButton = UiStyle.MakeButton("Create vehicle", new Vector2(0, 36));
        _createButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _createButton.Pressed += () => CreateVehicleRequested?.Invoke();
        left.AddChild(_createButton);

        _joinButton = UiStyle.MakeButton("Join to workbench", new Vector2(0, 36));
        _joinButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _joinButton.Pressed += () => JoinRequested?.Invoke();
        left.AddChild(_joinButton);

        left.AddChild(new HSeparator());
        left.AddChild(UiStyle.MakeLabel("Saved constructions", 13, UiStyle.TextDim));

        var listScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        left.AddChild(listScroll);
        _listColumn = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _listColumn.AddThemeConstantOverride("separation", 4);
        listScroll.AddChild(_listColumn);

        _emptyHint = UiStyle.MakeLabel("No saved constructions yet.", 12, UiStyle.TextDim);
        _emptyHint.Visible = false;

        // ------------------------------------------------------------ правая колонка: превью + описание + Open
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
        right.AddThemeConstantOverride("separation", 8);
        row.AddChild(right);

        _previewName = UiStyle.MakeLabel("", 18);
        right.AddChild(_previewName);

        var previewFrame = new PanelContainer { CustomMinimumSize = new Vector2(0, 260) };
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

        var descriptionScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        right.AddChild(descriptionScroll);
        _previewDescription = UiStyle.MakeLabel("", 13, UiStyle.TextDim);
        _previewDescription.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _previewDescription.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        descriptionScroll.AddChild(_previewDescription);

        var actionsRow = new HBoxContainer();
        actionsRow.AddThemeConstantOverride("separation", 8);
        right.AddChild(actionsRow);

        _openButton = UiStyle.MakeButton("Open", new Vector2(0, 36));
        _openButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _openButton.Disabled = true;
        _openButton.Pressed += () =>
        {
            if (_selected is { } selection) OpenConstructionRequested?.Invoke(selection.Path);
        };
        actionsRow.AddChild(_openButton);

        // Spawn прямо из меню верстака — заспавнить постройку в мире, не заходя в редактор (см. задачу).
        _spawnButton = UiStyle.MakeButton("Spawn", new Vector2(0, 36));
        _spawnButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _spawnButton.Disabled = true;
        _spawnButton.Pressed += () =>
        {
            if (_selected is { } selection) SpawnConstructionRequested?.Invoke(selection.Path);
        };
        actionsRow.AddChild(_spawnButton);

        layerRoot.AddChild(_overlay);
    }

    public bool IsOpen => _overlay.Visible;

    /// <summary><paramref name="workbenchName"/> — нужен, чтобы понять, есть ли уже сетевая сессия ИМЕННО на этом
    /// верстаке (<see cref="NetHub.IsSessionActive"/>) и соответственно включить/выключить Join/Create/Open — см.
    /// <see cref="UpdateNetworkState"/>.</summary>
    public void Open(string workbenchName)
    {
        _workbenchName = workbenchName;
        Refresh();
        UpdateNetworkState();
        _overlay.Visible = true;
    }

    public void Close() => _overlay.Visible = false;

    /// <summary>Перечитывает список сохранённых построек с диска — вызывается при каждом открытии меню, чтобы
    /// постройка, сохранённая только что в редакторе, сразу появилась в списке.</summary>
    private void Refresh()
    {
        foreach (var child in _listColumn.GetChildren()) child.QueueFree();
        _selected = null;
        ShowSelection(null);

        var saved = ConstructionStorage.List();
        if (saved.Count == 0)
        {
            _listColumn.AddChild(_emptyHint);
            _emptyHint.Visible = true;
            return;
        }

        foreach (var entry in saved)
        {
            var button = UiStyle.MakeButton("", new Vector2(0, 44), toggle: true);
            button.Alignment = HorizontalAlignment.Left;
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            button.Text = $"{entry.Name}\n{FormatDates(entry)}";
            button.Pressed += () => Select(entry, button);
            _listColumn.AddChild(button);
        }
    }

    /// <summary>
    /// Мультиплеер (см. Docs/05-world-and-vehicle-systems.md, «Мультиплеер», вариант Б): на верстаке одновременно
    /// может идти только ОДНА сессия редактирования — если она уже есть, Create/Open/Spawn (все они начали бы
    /// СВОЮ, отдельную от чужой постройку) выключены, доступен только Join. Не подключены к сети вообще — Join,
    /// наоборот, всегда выключена (присоединяться некуда, как и было раньше).
    /// </summary>
    private void UpdateNetworkState()
    {
        bool networked = NetHub.Instance.IsNetworked;
        bool sessionActive = networked && NetHub.Instance.IsSessionActive(_workbenchName);

        _joinButton.Disabled = !sessionActive;
        _joinButton.TooltipText = !networked
            ? "Not connected to a multiplayer game."
            : sessionActive
                ? "Join the construction currently being edited on this workbench."
                : "No one is editing on this workbench right now.";

        _createButton.Disabled = sessionActive;
        _createButton.TooltipText = sessionActive ? "Someone is already editing on this workbench - join them instead." : "";
        foreach (var child in _listColumn.GetChildren())
        {
            if (child is Button button) button.Disabled = sessionActive;
        }

        if (sessionActive)
        {
            _openButton.Disabled = true;
            _spawnButton.Disabled = true;
        }
    }

    private void Select(ConstructionStorage.SavedConstruction entry, Button pressedButton)
    {
        foreach (var child in _listColumn.GetChildren())
        {
            if (child is Button b) b.ButtonPressed = b == pressedButton;
        }

        _selected = entry;
        ShowSelection(entry);
    }

    private void ShowSelection(ConstructionStorage.SavedConstruction? entry)
    {
        _openButton.Disabled = entry == null;
        _spawnButton.Disabled = entry == null;
        _previewImage.Texture = null;

        if (entry is not { } selection)
        {
            _previewName.Text = "";
            _previewDescription.Text = "Select a construction on the left to see its preview and description.";
            return;
        }

        _previewName.Text = selection.Name;
        _previewDescription.Text = string.IsNullOrEmpty(selection.Description) ? "(no description)" : selection.Description!;

        if (selection.PreviewPath != null)
        {
            var image = new Image();
            if (image.Load(selection.PreviewPath) == Error.Ok) _previewImage.Texture = ImageTexture.CreateFromImage(image);
        }
    }

    private static string FormatDates(ConstructionStorage.SavedConstruction entry) =>
        $"created {TryFormatUtc(entry.CreatedUtc)}   modified {TryFormatUtc(entry.ModifiedUtc)}";

    private static string TryFormatUtc(string iso)
    {
        return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "unknown";
    }
}
