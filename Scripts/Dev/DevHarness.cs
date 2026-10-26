using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Editor;

namespace SandboxPolyGame.Dev;

/// <summary>
/// Инструменты разработчика, включаются аргументами после <c>--</c> в командной строке (для игроков ничего не меняют):
/// <code>
/// godot --path . -- --selftest                       самотесты (можно с --headless), код выхода 0 = успех
/// godot --headless --path . -- --nettest=host|client [--port=N]   дымовой тест сетевой репликации, два процесса (см. NetSmokeTest)
/// godot --path . --resolution 1600x900 -- --demo=house|stress|shapes --screenshot=out.png [--cam=px,py,pz,tx,ty,tz]
///                                        [--wireframe=0|1] [--borders=0|1] [--tool=paint|delete] [--resize] [--slot=N]
///                                        [--size=x,y,z] [--mirror=x,y,z] [--picker] [--hover=x,y] [--frames=N]
/// </code>
/// </summary>
public static class DevHarness
{
    public static void Start(BuildEditor editor)
    {
        var args = ParseArgs(OS.GetCmdlineUserArgs());
        if (args.Count == 0) return;

        _ = RunAsync(editor, args);
    }

    private static Dictionary<string, string> ParseArgs(string[] raw)
    {
        var result = new Dictionary<string, string>();
        foreach (var arg in raw)
        {
            if (!arg.StartsWith("--")) continue;
            int eq = arg.IndexOf('=');
            if (eq < 0) result[arg[2..]] = "";
            else result[arg[2..eq]] = arg[(eq + 1)..];
        }

        return result;
    }

    private static async Task RunAsync(BuildEditor editor, Dictionary<string, string> args)
    {
        try
        {
            if (args.ContainsKey("selftest"))
            {
                await SelfTest.RunAsync(editor);
                return;
            }

            if (args.TryGetValue("nettest", out var role))
            {
                await NetSmokeTest.RunAsync(editor, role, args.TryGetValue("port", out var portText) ? int.Parse(portText, CultureInfo.InvariantCulture) : 37998);
                return;
            }

            if (args.TryGetValue("demo", out var demo))
            {
                ulong started = Time.GetTicksMsec();
                if (demo == "shapes") DemoBuilds.Shapes(editor.World.Construction);
                else DemoBuilds.Build(demo, editor.World.Grid);
                editor.World.RebuildDirty();
                GD.Print($"[dev] demo '{demo}': {editor.World.Grid.BlockCount} blocks built+meshed in {Time.GetTicksMsec() - started} ms, " +
                         $"quads {editor.World.Quads} (faces before merge {editor.World.FacesBeforeMerge}), wire segments {editor.World.LineSegments}");
            }

            if (args.TryGetValue("cam", out var cam))
            {
                var v = ParseFloats(cam);
                editor.EditorCamera.LookAtPoint(new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]));
            }

            if (args.TryGetValue("wireframe", out var wireframe)) editor.State.Wireframe = wireframe != "0";
            if (args.TryGetValue("borders", out var borders)) editor.State.Borders = borders != "0";
            if (args.TryGetValue("tool", out var tool))
            {
                editor.State.Tool = tool switch { "delete" => ToolMode.Delete, _ => ToolMode.Paint };
            }
            if (args.ContainsKey("resize")) editor.State.ResizePanelOpen = true;
            if (args.TryGetValue("slot", out var slot)) editor.State.SelectedSlot = int.Parse(slot, CultureInfo.InvariantCulture);
            if (args.TryGetValue("size", out var size))
            {
                var v = ParseFloats(size);
                for (int axis = 0; axis < 3; axis++) editor.State.SetPendingSizeAxis(axis, (int)v[axis]);
            }

            if (args.TryGetValue("mirror", out var mirror))
            {
                var v = ParseFloats(mirror);
                if (v[0] != 0) editor.State.ToggleMirrorX();
                if (v[1] != 0) editor.State.ToggleMirrorY();
                if (v[2] != 0) editor.State.ToggleMirrorZ();
            }

            if (args.ContainsKey("picker")) editor.Ui.TogglePicker();

            if (args.TryGetValue("hover", out var hover))
            {
                var v = ParseFloats(hover);
                var p = new Vector2(v[0], v[1]);
                Input.ParseInputEvent(new InputEventMouseMotion { Position = p, GlobalPosition = p });
            }

            if (args.TryGetValue("screenshot", out var path))
            {
                int frames = args.TryGetValue("frames", out var f) ? int.Parse(f, CultureInfo.InvariantCulture) : 10;
                await SelfTest.Frames(editor, frames);
                await editor.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

                var image = editor.GetViewport().GetTexture().GetImage();
                var error = image.SavePng(path);
                GD.Print($"[dev] screenshot {image.GetWidth()}x{image.GetHeight()} -> {path} ({error})");
                editor.GetTree().Quit(error == Error.Ok ? 0 : 1);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[dev] harness failed: {ex}");
            editor.GetTree().Quit(2);
        }
    }

    private static float[] ParseFloats(string csv)
    {
        var parts = csv.Split(',');
        var result = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++) result[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
        return result;
    }
}
