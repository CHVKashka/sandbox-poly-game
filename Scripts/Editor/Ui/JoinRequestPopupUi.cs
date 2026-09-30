using System;
using Godot;

namespace SandboxPolyGame.Editor.Ui;

/// <summary>
/// Попап "игрок N просится присоединиться" — показывается АДМИНУ сессии верстака (см.
/// Docs/05-world-and-vehicle-systems.md, «Мультиплеер», вариант Б), когда кто-то нажал Join to workbench. Момент,
/// когда придёт заявка, заранее не известен — админ к этому моменту может быть ещё в мире
/// (<c>World.GameWorld</c>) или уже в редакторе (<c>Editor.BuildEditor</c>), поэтому оба экрана держат по своему
/// инстансу этого класса, подписанному на <c>Core.NetHub.JoinRequestIncoming</c>.
/// </summary>
public sealed class JoinRequestPopupUi
{
    private readonly ColorRect _overlay;
    private readonly Label _message;

    /// <summary>requesterId, accepted.</summary>
    public event Action<long, bool>? Responded;

    private long _requesterId;

    public JoinRequestPopupUi(Control layerRoot)
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

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(380, 0) };
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(16f));
        center.AddChild(panel);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);
        panel.AddChild(column);

        _message = UiStyle.MakeLabel("", 16);
        _message.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_message);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        column.AddChild(row);

        var accept = UiStyle.MakeButton("Accept", new Vector2(0, 36));
        accept.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        accept.Pressed += () => Respond(true);
        row.AddChild(accept);

        var decline = UiStyle.MakeButton("Decline", new Vector2(0, 36));
        decline.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        decline.Pressed += () => Respond(false);
        row.AddChild(decline);

        layerRoot.AddChild(_overlay);
    }

    public bool IsOpen => _overlay.Visible;

    public void Show(long requesterId)
    {
        _requesterId = requesterId;
        _message.Text = $"Player {requesterId} wants to join this workbench.";
        _overlay.Visible = true;
    }

    /// <summary>Заявитель отменил (см. <c>Core.NetHub.JoinCancelled</c>) — убрать попап, только если он сейчас
    /// показывает ИМЕННО его заявку (если админ уже разобрался и попап закрыт, или сейчас видна заявка от кого-то
    /// другого, отмена этого конкретного заявителя не должна закрывать чужой/уже неактуальный попап).</summary>
    public bool IsShowingRequestFrom(long requesterId) => IsOpen && _requesterId == requesterId;

    public void Hide() => _overlay.Visible = false;

    private void Respond(bool accepted)
    {
        _overlay.Visible = false;
        Responded?.Invoke(_requesterId, accepted);
    }
}
