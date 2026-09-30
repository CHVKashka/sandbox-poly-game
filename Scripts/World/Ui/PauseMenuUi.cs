using System;
using Godot;
using SandboxPolyGame.Editor.Ui;

namespace SandboxPolyGame.World.Ui;

/// <summary>
/// Меню паузы (`Esc` в открытом мире, см. <see cref="GameWorld"/>) — тот же паттерн полноэкранного модала, что и
/// <see cref="WorkbenchMenuUi"/>/<see cref="JoinRequestPopupUi"/>. Три кнопки: Resume (закрыть меню), Exit to menu
/// (разорвать сетевое соединение, если оно есть, и вернуться к <see cref="NetworkLobbyUi"/>), Exit to desktop
/// (закрыть игру целиком). Только для мира — в редакторе построек `Esc` уже занят (закрывает список блоков/диалог
/// сохранения), это меню туда не добавлено.
/// </summary>
public sealed class PauseMenuUi
{
    private readonly ColorRect _overlay;

    public event Action? ResumeRequested;
    public event Action? ExitToMenuRequested;
    public event Action? ExitToDesktopRequested;

    public PauseMenuUi(Control layerRoot)
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

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(280, 0) };
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(16f));
        center.AddChild(panel);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        panel.AddChild(column);

        column.AddChild(UiStyle.MakeLabel("Paused", 20));

        var resume = UiStyle.MakeButton("Resume", new Vector2(0, 36));
        resume.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        resume.Pressed += () => ResumeRequested?.Invoke();
        column.AddChild(resume);

        var exitToMenu = UiStyle.MakeButton("Exit to menu", new Vector2(0, 36));
        exitToMenu.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        exitToMenu.Pressed += () => ExitToMenuRequested?.Invoke();
        column.AddChild(exitToMenu);

        var exitToDesktop = UiStyle.MakeButton("Exit to desktop", new Vector2(0, 36));
        exitToDesktop.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        exitToDesktop.Pressed += () => ExitToDesktopRequested?.Invoke();
        column.AddChild(exitToDesktop);

        layerRoot.AddChild(_overlay);
    }

    public bool IsOpen => _overlay.Visible;

    public void Open() => _overlay.Visible = true;

    public void Close() => _overlay.Visible = false;
}
