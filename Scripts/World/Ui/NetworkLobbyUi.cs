using System;
using Godot;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor.Ui;

namespace SandboxPolyGame.World.Ui;

/// <summary>
/// Первый экран при запуске мира (см. <see cref="GameWorld"/>) — решить, играть ли одному или по сети, ДО того, как
/// собирается террейн/верстаки/игрок. Показывается ровно один раз за запуск процесса (см. <see cref="NetHub.LobbyResolved"/>) —
/// возврат из редактора уже не должен спрашивать заново, сеть (если она есть) продолжает жить между переходами
/// сцен (см. <see cref="NetHub"/> class doc, автозагрузка). Дев-харнесс (<c>--selftest</c> и т.п.) и самотесты,
/// инстанцирующие свой собственный <see cref="GameWorld"/> для структурной проверки, это окно вообще не видят — см.
/// <see cref="GameWorld._Ready"/>.
/// </summary>
public sealed class NetworkLobbyUi
{
    private readonly ColorRect _overlay;
    private readonly Label _status;
    private readonly LineEdit _addressField;
    private readonly LineEdit _portField;

    /// <summary>Выбор сделан (Solo/Host/успешный Join) — мир можно достраивать.</summary>
    public event Action? Resolved;

    public NetworkLobbyUi(Control layerRoot)
    {
        _overlay = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.75f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var center = UiStyle.Transparent(new CenterContainer());
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _overlay.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(420, 0) };
        panel.AddThemeStyleboxOverride("panel", UiStyle.Panel(16f));
        center.AddChild(panel);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        panel.AddChild(column);

        column.AddChild(UiStyle.MakeLabel("Sandbox Poly Game", 22));

        var solo = UiStyle.MakeButton("Play solo", new Vector2(0, 36));
        solo.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        solo.Pressed += PlaySolo;
        column.AddChild(solo);

        column.AddChild(new HSeparator());
        column.AddChild(UiStyle.MakeLabel("Host a game", 13, UiStyle.TextDim));
        var host = UiStyle.MakeButton($"Host on port {NetHub.DefaultPort}", new Vector2(0, 36));
        host.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        host.Pressed += Host;
        column.AddChild(host);

        column.AddChild(new HSeparator());
        column.AddChild(UiStyle.MakeLabel("Join a game", 13, UiStyle.TextDim));

        var joinRow = new HBoxContainer();
        joinRow.AddThemeConstantOverride("separation", 6);
        column.AddChild(joinRow);

        _addressField = new LineEdit { Text = "127.0.0.1", PlaceholderText = "address", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        joinRow.AddChild(_addressField);
        _portField = new LineEdit { Text = NetHub.DefaultPort.ToString(), PlaceholderText = "port", CustomMinimumSize = new Vector2(64, 0) };
        joinRow.AddChild(_portField);

        var join = UiStyle.MakeButton("Join", new Vector2(0, 36));
        join.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        join.Pressed += JoinGame;
        column.AddChild(join);

        _status = UiStyle.MakeLabel("", 12, UiStyle.TextDim);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        column.AddChild(_status);

        layerRoot.AddChild(_overlay);
    }

    private void PlaySolo()
    {
        NetHub.Instance.LobbyResolved = true;
        Close();
    }

    private void Host()
    {
        var err = NetHub.Instance.Host();
        if (err != Error.Ok)
        {
            _status.Text = $"Could not host: {err}";
            return;
        }

        NetHub.Instance.LobbyResolved = true;
        Close();
    }

    private void JoinGame()
    {
        string address = string.IsNullOrWhiteSpace(_addressField.Text) ? "127.0.0.1" : _addressField.Text.Trim();
        int port = int.TryParse(_portField.Text, out int parsed) ? parsed : NetHub.DefaultPort;

        var err = NetHub.Instance.Join(address, port);
        if (err != Error.Ok)
        {
            _status.Text = $"Could not join: {err}";
            return;
        }

        _status.Text = "Connecting...";

        // ConnectedToServer/ConnectionFailed приходят асинхронно (реальное сетевое рукопожатие) - мир достраивается
        // только по факту успеха; при неудаче остаёмся на этом экране, чтобы можно было попробовать ещё раз (другой
        // адрес/порт) без перезапуска игры.
        void OnConnected()
        {
            NetHub.Instance.ConnectedToServer -= OnConnected;
            NetHub.Instance.ConnectionFailed -= OnFailed;
            NetHub.Instance.LobbyResolved = true;
            Close();
        }

        void OnFailed()
        {
            NetHub.Instance.ConnectedToServer -= OnConnected;
            NetHub.Instance.ConnectionFailed -= OnFailed;
            NetHub.Instance.Disconnect();
            _status.Text = "Connection failed.";
        }

        NetHub.Instance.ConnectedToServer += OnConnected;
        NetHub.Instance.ConnectionFailed += OnFailed;
    }

    private void Close()
    {
        _overlay.Visible = false;
        _overlay.QueueFree();
        Resolved?.Invoke();
    }
}
