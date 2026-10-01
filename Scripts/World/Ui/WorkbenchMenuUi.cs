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
/// <item><b>Create vehicle</b> — сразу переход в редактор с пустой (только корневой блок) постройкой; ВСЕГДА
/// доступна, даже если кто-то ещё уже редактирует через этот же верстак — каждый клик начинает СВОЮ, независимую
/// сессию (см. <see cref="UpdateNetworkState"/>/<c>NetHub</c> class doc).</item>
/// <item><b>Join to workbench</b> — присоединиться к уже идущей сессии редактора на этом верстаке (совместное
/// редактирование, см. Docs); доступна, только если сеть есть и на этом верстаке сейчас идёт хотя бы одна сессия.
/// Если их несколько — рядом появляется выпадающий список (<see cref="_joinPicker"/>, "Admin #id" на пункт), Join
/// адресует выбранную; если сессия всего одна, список скрыт, Join адресует её напрямую (см. <see cref="SetJoinableSessions"/>).</item>
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
    private readonly OptionButton _joinPicker;
    private readonly Label _emptyHint;

    private ConstructionStorage.SavedConstruction? _selected;
    private string _workbenchName = "";

    /// <summary>Ключи сессий, которые сейчас можно присоединить на этом верстаке — индексы совпадают с пунктами
    /// <see cref="_joinPicker"/> (см. <see cref="SetJoinableSessions"/>). Пусто, пока ответ сервера ещё не пришёл
    /// или сети нет вообще — тогда Join выключена.</summary>
    private string[] _joinSessionKeys = Array.Empty<string>();

    /// <summary>Нажата Create vehicle — открыть редактор пустым.</summary>
    public event Action? CreateVehicleRequested;

    /// <summary>Нажата Open для выбранной постройки — открыть редактор с ней (путь к файлу).</summary>
    public event Action<string>? OpenConstructionRequested;

    /// <summary>Нажата Spawn для выбранной постройки — заспавнить её в мире, минуя редактор (путь к файлу).</summary>
    public event Action<string>? SpawnConstructionRequested;

    /// <summary>Нажата Join to workbench — попроситься в уже идущую сессию на этом верстаке (см. <see cref="Open"/>).
    /// Параметр — ключ ВЫБРАННОЙ сессии (одна — выбрана автоматически; несколько — та, что отмечена в
    /// <see cref="_joinPicker"/>), см. <see cref="SetJoinableSessions"/>.</summary>
    public event Action<string>? JoinRequested;

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

        // Выпадающий список — виден, только если на верстаке одновременно несколько сессий (см. SetJoinableSessions).
        // Создан РАНЬШЕ кнопки Join, которая ссылается на него из своего обработчика ниже.
        _joinPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = false, FocusMode = Control.FocusModeEnum.None };

        _joinButton = UiStyle.MakeButton("Join to workbench", new Vector2(0, 36));
        _joinButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _joinButton.Pressed += () =>
        {
            if (_joinSessionKeys.Length == 0) return;

            // Одна сессия - выбор не нужен; несколько - берём отмеченный пункт списка (по умолчанию первый).
            int index = _joinSessionKeys.Length > 1 ? _joinPicker.Selected : 0;
            if (index < 0 || index >= _joinSessionKeys.Length) index = 0;
            JoinRequested?.Invoke(_joinSessionKeys[index]);
        };
        left.AddChild(_joinButton);
        left.AddChild(_joinPicker);

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

    /// <summary>Для самотестов (см. <c>Dev.SelfTest</c>) — проверить, что Create остаётся доступной независимо от
    /// чужих сессий на этом верстаке, и что Join включается/выключается по ответу сервера (см.
    /// <see cref="SetJoinableSessions"/>).</summary>
    public bool IsCreateButtonDisabled => _createButton.Disabled;

    public bool IsJoinButtonDisabled => _joinButton.Disabled;

    /// <summary>Для самотестов — сколько сессий сейчас предлагает выпадающий список Join (0 = кнопка выключена,
    /// 1 = список скрыт/Join адресует её напрямую, 2+ = список виден).</summary>
    public int JoinableSessionCount => _joinSessionKeys.Length;

    /// <summary><paramref name="workbenchName"/> — какой верстак открыли (для <see cref="GameWorld"/>: по нему
    /// запрашивается список сессий, см. <see cref="SetJoinableSessions"/>/class doc). Join выключена, пока ответ
    /// сервера ещё не пришёл (или сразу и навсегда, если сеть не подключена вообще) — см. <see cref="UpdateOfflineState"/>.</summary>
    public void Open(string workbenchName)
    {
        _workbenchName = workbenchName;
        Refresh();
        UpdateOfflineState();
        _overlay.Visible = true;
    }

    public string WorkbenchName => _workbenchName;

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
    /// Мультиплеер (см. Docs/05-world-and-vehicle-systems.md, «Мультиплеер», вариант Б; <see cref="NetHub"/> class
    /// doc): раньше на одном верстаке одновременно мог идти только ОДИН Create/Open/Join — остальным Create vehicle
    /// был попросту недоступен, пока кто-то другой уже редактировал через тот же физический верстак (баг, найденный
    /// пользователем). Убрано: Create/Open/Spawn/список сохранённых построек ВСЕГДА доступны — каждый клик начинает
    /// СВОЮ, независимую сессию, и сколько угодно игроков может одновременно зайти в свой собственный редактор через
    /// один и тот же верстак. Единственное, что по-прежнему зависит от чужих сессий — кнопка <b>Join</b>.
    /// </summary>
    private void UpdateOfflineState()
    {
        _joinSessionKeys = Array.Empty<string>();
        _joinPicker.Clear();
        _joinPicker.Visible = false;

        bool networked = NetHub.Instance.IsNetworked;
        _joinButton.Disabled = true;
        // Сеть есть - GameWorld вот-вот спросит сервер (NetHub.RequestWorkbenchSessions) и позовёт
        // SetJoinableSessions с настоящим ответом; "Checking..." - на случай редкой заметной задержки RPC.
        _joinButton.TooltipText = !networked ? "Not connected to a multiplayer game." : "Checking for active sessions...";
    }

    /// <summary>Вызывается <see cref="GameWorld"/>, когда пришёл ответ сервера (<c>NetHub.WorkbenchSessionsForMe</c>)
    /// на запрос, отправленный при открытии этого меню (см. <see cref="Open"/>/<see cref="WorkbenchName"/>) — баг,
    /// найденный пользователем: раньше состояние "есть активная сессия" рассылалось ОДИН раз в момент её открытия,
    /// и игрок, подключившийся позже, никогда об этом не узнавал (Join оставалась выключенной навсегда). Теперь
    /// запрашивается заново при КАЖДОМ открытии меню — не бывает устаревшим. Несколько сессий — показываем
    /// выпадающий список ("Admin #id" на пункт, порядок — по времени создания); одна — список скрыт, Join адресует
    /// её напрямую; ни одной — Join выключена.</summary>
    public void SetJoinableSessions(string[] sessionKeys, long[] adminPeerIds)
    {
        _joinSessionKeys = sessionKeys;
        _joinPicker.Clear();
        for (int i = 0; i < sessionKeys.Length; i++) _joinPicker.AddItem($"Admin #{adminPeerIds[i]}");
        _joinPicker.Visible = sessionKeys.Length > 1;
        if (sessionKeys.Length > 1) _joinPicker.Selected = 0;

        _joinButton.Disabled = sessionKeys.Length == 0;
        _joinButton.TooltipText = sessionKeys.Length == 0
            ? "No one is editing on this workbench right now."
            : "Join a construction currently being edited on this workbench.";
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
