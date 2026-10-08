using System;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Editor.Ui;

/// <summary>
/// Весь интерфейс редактора: хотбар, список блоков (Tab), тулбар справа (инструменты + сохранение/загрузка
/// постройки), инфо-панель слева сверху.
/// </summary>
public sealed class EditorUi
{
    private readonly HotbarUi _hotbar;
    private readonly ToolbarUi _toolbar;
    private readonly BlockPickerUi _picker;
    private readonly SaveDialogUi _saveDialogUi;
    private readonly JoinRequestPopupUi _joinPopup;
    private readonly ParametersPanelUi _parametersPanel;
    private readonly Label _info;
    private readonly Label _status;
    private readonly FileDialog _loadDialog;

    // Последнее имя/описание, под которым СОХРАНЯЛАСЬ эта постройка в текущей сессии - повторное сохранение под
    // тем же именем перезаписывает тот же файл (не плодит "Boat (2)", "Boat (3)" на каждый клик Save); смена имени
    // в диалоге создаёт новый файл, как и ожидается от "Save" с явно другим названием.
    private string? _savedPath;
    private string _lastName = "";
    private string _lastDescription = "";

    /// <summary>Ответ на попап Join (см. <see cref="JoinRequestPopupUi"/>) - requesterId, accepted.</summary>
    public event Action<long, bool>? JoinResponseRequested;

    public EditorUi(BuildEditor editor, EditorState state)
    {
        var layer = new CanvasLayer { Layer = 10, Name = "Ui" };
        editor.AddChild(layer);

        var root = UiStyle.Transparent(new Control { Name = "UiRoot" });
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(root);

        var infoMargin = UiStyle.Transparent(new MarginContainer());
        infoMargin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_top" }) infoMargin.AddThemeConstantOverride(side, 12);
        var infoColumn = UiStyle.Transparent(new VBoxContainer());
        infoMargin.AddChild(infoColumn);
        _info = UiStyle.MakeLabel("", 13);
        _info.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
        _info.AddThemeConstantOverride("shadow_offset_x", 1);
        _info.AddThemeConstantOverride("shadow_offset_y", 1);
        infoColumn.AddChild(_info);
        _status = UiStyle.MakeLabel("", 13, UiStyle.Accent);
        infoColumn.AddChild(_status);
        root.AddChild(infoMargin);

        // Порядок важен: список блоков/диалог сохранения поверх сцены, но под хотбаром/тулбаром — слот можно
        // выбрать, не закрывая список (сам диалог, пока открыт, блокирует мир целиком - см. IsModalOpen).
        _picker = new BlockPickerUi(root, state);
        _saveDialogUi = new SaveDialogUi(root);
        _saveDialogUi.Confirmed += (name, description) => SaveAs(editor, name, description);
        _joinPopup = new JoinRequestPopupUi(root);
        _joinPopup.Responded += (requesterId, accepted) => JoinResponseRequested?.Invoke(requesterId, accepted);
        _hotbar = new HotbarUi(root, state);
        _toolbar = new ToolbarUi(root, state);
        _parametersPanel = new ParametersPanelUi(root);

        _loadDialog = new FileDialog
        {
            Title = "Load construction",
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            CurrentDir = ConstructionStorage.Directory,
            Filters = new[] { "*.json ; JSON build files" },
        };
        _loadDialog.FileSelected += path => LoadFrom(editor, path);
        root.AddChild(_loadDialog);

        _toolbar.SaveRequested += () => _saveDialogUi.Open(_lastName, _lastDescription);
        _toolbar.LoadRequested += () => _loadDialog.PopupCentered(new Vector2I(720, 480));
        _toolbar.ExitRequested += () => GoToWorld(editor);
        _toolbar.SpawnRequested += () => Spawn(editor);

        state.Changed += () =>
        {
            _hotbar.Refresh();
            _toolbar.Refresh();
            _picker.Refresh();
        };

        // Вход через верстак с уже выбранной постройкой (см. World.Ui.WorkbenchMenuUi/EditorHandoff) - подгружаем
        // её тем же путём, что и обычная кнопка Load, чтобы дальнейший Save сразу перезаписывал её же, а не плодил
        // новый файл. Once-off: сбрасывается сразу после использования (см. EditorHandoff class doc).
        if (EditorHandoff.PendingConstructionPath is { } pendingPath)
        {
            EditorHandoff.PendingConstructionPath = null;
            LoadFrom(editor, pendingPath);
        }
    }

    private const string WorldScenePath = "res://Scenes/World.tscn";

    /// <summary>
    /// Spawn: считает центр масс/коллизию (см. <see cref="World.VehicleSpawner"/>) и материализует постройку у
    /// верстака как физическое тело, возвращая в мир — не спавнит на месте в самом редакторе (там для этого нет ни
    /// физического мира, ни гравитации). Передаётся через <see cref="EditorHandoff.PendingSpawnJson"/>, читается и
    /// сбрасывается <c>World.GameWorld</c> при входе. Плавучесть/аэродинамика сюда не входят — см.
    /// Docs/05-world-and-vehicle-systems.md, «Спавн постройки».
    /// </summary>
    private void Spawn(BuildEditor editor)
    {
        EditorHandoff.PendingSpawnJson = ConstructionIO.Serialize(editor.World.Construction);
        GoToWorld(editor);
    }

    /// <summary>
    /// <see cref="SceneTree.ChangeSceneToFile"/> напрямую может упасть с engine-ошибкой "Parent node is busy
    /// adding/removing children", если дерево сцены в этот момент занято (например, вызов идёт из глубины
    /// обработки ввода/сигнала кнопки) — откладываем через <see cref="Callable"/> на конец кадра, как и
    /// <c>World.GameWorld</c> при переходе в обратную сторону.
    /// </summary>
    private static void GoToWorld(BuildEditor editor)
    {
        editor.LeaveNetworkSessionIfAdmin();
        Callable.From(() => editor.GetTree().ChangeSceneToFile(WorldScenePath)).CallDeferred();
    }

    public bool PickerOpen => _picker.IsOpen;

    /// <summary>Панель параметров блока (инструмент «Parameters»), слева на экране.</summary>
    public ParametersPanelUi ParametersPanel => _parametersPanel;

    /// <summary>Вкладка слоя логики на тулбаре (самотесты кликают по ней).</summary>
    public Button WireLayerTab(WireLayer layer) => _toolbar.WireLayerTab(layer);

    public bool SaveDialogOpen => _saveDialogUi.IsOpen;

    /// <summary>Показать попап "игрок N просится присоединиться" — см. <see cref="JoinRequestPopupUi"/>.</summary>
    public void ShowJoinRequestPopup(long requesterId) => _joinPopup.Show(requesterId);

    /// <summary>Заявитель передумал (кнопка Cancel на его плашке ожидания, см. <c>World.Ui.JoinWaitingUi</c>) -
    /// убрать попап, только если он ВСЁ ЕЩЁ показывает именно эту заявку.</summary>
    public void HideJoinRequestPopupIfFrom(long requesterId)
    {
        if (_joinPopup.IsShowingRequestFrom(requesterId)) _joinPopup.Hide();
    }

    /// <summary>Открыто ли какое-либо модальное окно (список блоков, диалог сохранения, попап Join) — пока да, мир
    /// не должен реагировать ни на клики/наведение, ни на движение камеры (см. <c>BuildEditor</c>: без этого,
    /// например, ввод буквы "w" в имя постройки заодно двигал бы камеру).</summary>
    public bool IsModalOpen => _picker.IsOpen || _saveDialogUi.IsOpen || _joinPopup.IsOpen;

    public void TogglePicker() => _picker.Toggle();

    public void ClosePicker() => _picker.Close();

    public void CloseSaveDialog() => _saveDialogUi.Close();

    public Vector2 GetHotbarSlotCenter(int index) => _hotbar.GetSlotCenter(index);

    public void SetInfo(string text) => _info.Text = text;

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>Текущий текст строки статуса (для самотестов).</summary>
    public string Status => _status.Text;

    /// <summary>true, если точка экрана лежит над элементом интерфейса (клик по ней не должен попадать в мир).</summary>
    public bool IsPointOverUi(Vector2 point) =>
        IsModalOpen || _hotbar.Panel.GetGlobalRect().HasPoint(point) || _toolbar.Panel.GetGlobalRect().HasPoint(point)
        || (_parametersPanel.IsOpen && _parametersPanel.GlobalRect.HasPoint(point));

    /// <summary>
    /// Сохраняет постройку под именем/описанием, введёнными в <see cref="SaveDialogUi"/> (см. class doc) —
    /// автоматический путь на диске (<see cref="ConstructionStorage"/>), не выбор пути игроком. Повторное
    /// сохранение ПОД ТЕМ ЖЕ именем в течение сессии перезаписывает тот же файл; смена имени создаёт новый файл
    /// (см. <see cref="ConstructionStorage.ResolveNewPath"/>) — обычная семантика "Save", не "Save As" на каждый клик.
    /// Превью (PNG) рендерится отдельно и асинхронно (<see cref="ConstructionPreviewRenderer"/>) — best-effort,
    /// не блокирует и не может провалить само сохранение.
    /// </summary>
    private void SaveAs(BuildEditor editor, string name, string description)
    {
        string path = _savedPath != null && ConstructionStorage.SanitizeFileName(name) == ConstructionStorage.SanitizeFileName(_lastName)
            ? _savedPath
            : ConstructionStorage.ResolveNewPath(name);

        var error = ConstructionStorage.Save(editor.World.Construction, path, name, description);
        if (error == Error.Ok)
        {
            _savedPath = path;
            _lastName = name;
            _lastDescription = description;
            _ = ConstructionPreviewRenderer.RenderAsync(editor, editor.World.Construction, ConstructionStorage.PreviewPathFor(path));
        }

        SetStatus(error == Error.Ok ? $"Saved '{name}' to {path}" : $"Save failed: {error}");
    }

    private void LoadFrom(BuildEditor editor, string path)
    {
        var (error, loaded, skipped) = ConstructionIO.LoadFromFile(editor.World.Construction, path, BlockCatalog.Instance);
        if (error == Error.Ok)
        {
            editor.World.RebuildDirty();
            // Загруженный файл становится "текущим" для последующего Save (перезаписывает его же, если имя не менялось).
            var meta = ConstructionIO.ReadMetadata(FileAccess.GetFileAsString(path));
            _savedPath = path;
            _lastName = meta.Name ?? path.GetFile().GetBaseName();
            _lastDescription = meta.Description ?? "";
        }

        SetStatus(error == Error.Ok
            ? $"Loaded {loaded} blocks ({skipped} skipped) from {path}"
            : $"Load failed: {error}");
    }
}
