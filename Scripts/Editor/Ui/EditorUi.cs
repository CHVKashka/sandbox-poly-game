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
    private readonly ResizeDialogUi _resizeDialog;
    private readonly Label _info;
    private readonly Label _status;
    private readonly FileDialog _saveDialog;
    private readonly FileDialog _loadDialog;

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

        // Порядок важен: список блоков/диалог resize поверх сцены, но под хотбаром/тулбаром — слот можно выбрать,
        // не закрывая список.
        _picker = new BlockPickerUi(root, state);
        _resizeDialog = new ResizeDialogUi(root);
        _hotbar = new HotbarUi(root, state);
        _toolbar = new ToolbarUi(root, state);

        string constructionsDir = DefaultConstructionsDir();
        _saveDialog = new FileDialog
        {
            Title = "Save construction",
            FileMode = FileDialog.FileModeEnum.SaveFile,
            Access = FileDialog.AccessEnum.Filesystem,
            CurrentDir = constructionsDir,
            CurrentFile = "construction.json",
            Filters = new[] { "*.json ; JSON build files" },
        };
        _saveDialog.FileSelected += path => SaveTo(editor, path);
        root.AddChild(_saveDialog);

        _loadDialog = new FileDialog
        {
            Title = "Load construction",
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            CurrentDir = constructionsDir,
            Filters = new[] { "*.json ; JSON build files" },
        };
        _loadDialog.FileSelected += path => LoadFrom(editor, path);
        root.AddChild(_loadDialog);

        _toolbar.SaveRequested += () => _saveDialog.PopupCentered(new Vector2I(720, 480));
        _toolbar.LoadRequested += () => _loadDialog.PopupCentered(new Vector2I(720, 480));

        state.Changed += () =>
        {
            _hotbar.Refresh();
            _toolbar.Refresh();
            _picker.Refresh();
        };
    }

    public bool PickerOpen => _picker.IsOpen;

    public bool ResizeDialogOpen => _resizeDialog.IsOpen;

    public void TogglePicker() => _picker.Toggle();

    public void ClosePicker() => _picker.Close();

    public void OpenResizeDialog(Construction construction, BlockInstance instance, BlockDefinition definition) =>
        _resizeDialog.Open(construction, instance, definition);

    public void CloseResizeDialog() => _resizeDialog.Close();

    public Vector2 GetHotbarSlotCenter(int index) => _hotbar.GetSlotCenter(index);

    public void SetInfo(string text) => _info.Text = text;

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>true, если точка экрана лежит над элементом интерфейса (клик по ней не должен попадать в мир).</summary>
    public bool IsPointOverUi(Vector2 point) =>
        _picker.IsOpen || _resizeDialog.IsOpen
        || _hotbar.Panel.GetGlobalRect().HasPoint(point) || _toolbar.Panel.GetGlobalRect().HasPoint(point);

    private static string DefaultConstructionsDir()
    {
        const string dir = "user://Constructions";
        if (!DirAccess.DirExistsAbsolute(dir)) DirAccess.MakeDirAbsolute(dir);
        return ProjectSettings.GlobalizePath(dir);
    }

    private void SaveTo(BuildEditor editor, string path)
    {
        var error = ConstructionIO.SaveToFile(editor.World.Construction, path);
        SetStatus(error == Error.Ok ? $"Saved to {path}" : $"Save failed: {error}");
    }

    private void LoadFrom(BuildEditor editor, string path)
    {
        var (error, loaded, skipped) = ConstructionIO.LoadFromFile(editor.World.Construction, path, BlockCatalog.Instance);
        if (error == Error.Ok) editor.World.RebuildDirty();
        SetStatus(error == Error.Ok
            ? $"Loaded {loaded} blocks ({skipped} skipped) from {path}"
            : $"Load failed: {error}");
    }
}
