using Godot;

namespace SwV2.Editor.Ui;

internal static class UiStyle
{
    public static readonly Color PanelBg = new(0.07f, 0.08f, 0.10f, 0.90f);
    public static readonly Color PanelBorder = new(1f, 1f, 1f, 0.14f);
    public static readonly Color Accent = new(1.0f, 0.75f, 0.20f);
    public static readonly Color TextDim = new(1f, 1f, 1f, 0.65f);

    public static StyleBoxFlat Box(Color bg, Color border, int borderWidth = 1, int radius = 6, float margin = 8f)
    {
        var style = new StyleBoxFlat { BgColor = bg, BorderColor = border };
        style.SetBorderWidthAll(borderWidth);
        style.SetCornerRadiusAll(radius);
        style.SetContentMarginAll(margin);
        return style;
    }

    public static StyleBoxFlat Panel(float margin = 10f) => Box(PanelBg, PanelBorder, 1, 8, margin);

    /// <summary>Контейнер-обёртка на весь экран, который сам не перехватывает мышь.</summary>
    public static T Transparent<T>(T control) where T : Control
    {
        control.MouseFilter = Control.MouseFilterEnum.Ignore;
        return control;
    }

    public static Label MakeLabel(string text, int size = 14, Color? color = null)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        if (color.HasValue) label.AddThemeColorOverride("font_color", color.Value);
        return label;
    }

    public static Button MakeButton(string text, Vector2 minSize, bool toggle = false)
    {
        return new Button
        {
            Text = text,
            ToggleMode = toggle,
            CustomMinimumSize = minSize,
            FocusMode = Control.FocusModeEnum.None, // иначе Tab/стрелки уходят в навигацию по UI
        };
    }
}
