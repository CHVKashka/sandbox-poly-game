using System;
using Godot;
using SandboxPolyGame.Editor.Ui;

namespace SandboxPolyGame.World.Ui;

/// <summary>
/// Плашка "ждём ответа админа" — показывается заявителю сразу после нажатия Join to workbench
/// (<see cref="GameWorld.RequestJoinActiveWorkbench"/>), пока не придёт <c>Core.NetHub.JoinAcceptedForMe</c>/
/// <c>JoinRejectedForMe</c>, либо пока сам заявитель не передумает (кнопка Cancel — шлёт
/// <c>Core.NetHub.RequestCancelJoin</c>, см. <see cref="CancelRequested"/>). Не полноэкранный модал, как
/// <see cref="JoinRequestPopupUi"/> у админа — маленькая плашка сверху экрана: WASD специально ОСТАЁТСЯ включённым
/// на время ожидания (баг, найденный пользователем — раньше ожидающий игрок вообще не мог ходить, см.
/// <see cref="GameWorld.MovementBlockingModalOpen"/>), мышь при этом всё равно видима (не Captured) — иначе по
/// кнопке Cancel было бы нечем кликнуть; E/R по-прежнему заблокированы (см. <see cref="GameWorld.AnyModalOpen"/>),
/// чтобы не открыть ещё один верстак поверх, пока заявка висит.
/// </summary>
public sealed class JoinWaitingUi
{
    private readonly PanelContainer _panel;

    public event Action? CancelRequested;

    public JoinWaitingUi(Control layerRoot)
    {
        _panel = new PanelContainer
        {
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(280, 0),
        };
        _panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(14f));
        _panel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _panel.Position = new Vector2(-140, 24);
        _panel.Size = new Vector2(280, 0);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(column);

        var message = UiStyle.MakeLabel("Waiting for the admin's response...", 14);
        message.HorizontalAlignment = HorizontalAlignment.Center;
        message.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(message);

        var cancel = UiStyle.MakeButton("Cancel", new Vector2(0, 32));
        cancel.Pressed += () => CancelRequested?.Invoke();
        column.AddChild(cancel);

        layerRoot.AddChild(_panel);
    }

    public bool IsOpen => _panel.Visible;

    public void Show() => _panel.Visible = true;

    public void Hide() => _panel.Visible = false;
}
