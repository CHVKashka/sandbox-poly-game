using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Editor.Ui;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.Dev;

public sealed partial class SelfTest
{
    private const string AllKindsParameters =
        "{ \"parameters\": [" +
        "{ \"id\": \"gain\", \"label\": \"Gain\", \"type\": \"Float\", \"min\": 0.5, \"max\": 4, \"step\": 0.25, \"default\": 2 }," +
        "{ \"id\": \"count\", \"label\": \"Count\", \"type\": \"Int\", \"min\": 1, \"max\": 9, \"default\": 3 }," +
        "{ \"id\": \"offset\", \"label\": \"Offset\", \"type\": \"Float\", \"default\": 0.75 }," +
        "{ \"id\": \"atLeast\", \"label\": \"At least\", \"type\": \"Float\", \"min\": 0, \"default\": 10 }," +
        "{ \"id\": \"loops\", \"label\": \"Loops\", \"type\": \"Int\", \"default\": 4 }," +
        "{ \"id\": \"flag\", \"label\": \"Flag\", \"type\": \"Bool\" }," +
        "{ \"id\": \"face\", \"label\": \"Face\", \"type\": \"Enum\", \"options\": [\"NegX\", \"PosX\", \"PosY\"], \"default\": \"PosX\" }," +
        "{ \"id\": \"tint\", \"label\": \"Tint\", \"type\": \"Color\", \"default\": \"#33ff55\" } ] }";

    private static ParametersComponent ParseSchema(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var component = new ParametersComponent();
        component.LoadFromJson(doc.RootElement);
        return component;
    }

    // ================================================================== правило выбора виджета и чтение параметров поведениями (чистая логика)

    private void RunParameterWidgetRuleTests()
    {
        GD.Print("-- parameter widgets: slider only with both limits, number box otherwise, dropdown for flags/enums, colour; behaviors read instance parameters first");

        var schema = ParseSchema(AllKindsParameters);
        ParameterWidget WidgetOf(string id) { schema.TryGet(id, out var definition); return definition.Widget; }

        Check(WidgetOf("gain") == ParameterWidget.Slider && WidgetOf("count") == ParameterWidget.Slider, "a Float/Int with BOTH limits is a slider");
        Check(WidgetOf("offset") == ParameterWidget.NumberBox && WidgetOf("loops") == ParameterWidget.NumberBox, "a number with no limits is a number box");
        Check(WidgetOf("atLeast") == ParameterWidget.NumberBox, "...and so is a number with only ONE limit (the limits are not both explicit)");
        Check(WidgetOf("flag") == ParameterWidget.Dropdown && WidgetOf("face") == ParameterWidget.Dropdown, "a flag and an enum (a few defined options) are dropdowns");
        Check(WidgetOf("tint") == ParameterWidget.ColorPicker, "a colour is a colour picker");
        var degenerate = ParseSchema("{ \"parameters\": [ { \"id\": \"fixed\", \"type\": \"Float\", \"min\": 3, \"max\": 3 } ] }");
        degenerate.TryGet("fixed", out var fixedDefinition);
        Check(fixedDefinition.Widget == ParameterWidget.NumberBox, "a degenerate range (min == max) is not a slider");

        schema.TryGet("tint", out var tint);
        Check(tint.Default == "#33ff55" && tint.TryNormalize("#FF8800", out var upper) && upper == "#ff8800" && tint.TryNormalize("ff8800", out var bare) && bare == "#ff8800",
            "a Color normalizes to lower-case #rrggbb", "");
        Check(!tint.TryNormalize("banana", out _) && !tint.TryNormalize("", out _), "...and rejects text that is not a colour (the value stays as it was)");
        Check(ParseSchema("{ \"parameters\": [ { \"id\": \"c\", \"type\": \"Color\" } ] }").Parameters[0].Default == "#ffffff", "a Color without a default is white");
        bool badDefault = false;
        try { ParseSchema("{ \"parameters\": [ { \"id\": \"c\", \"type\": \"Color\", \"default\": \"nope\" } ] }"); }
        catch (InvalidOperationException) { badDefault = true; }
        Check(badDefault, "a Color default that is not a colour is a data error");

        // ---- поведения читают параметр экземпляра раньше константы типа блока
        var buttonDefinition = MakeFunctional("{ \"params\": { \"glowColor\": \"#0000ff\" } }");
        var buttonSchema = ParseSchema("{ \"parameters\": [ { \"id\": \"glowColor\", \"type\": \"Color\", \"default\": \"#33ff55\" } ] }");
        var red = new Dictionary<string, string> { ["glowColor"] = "#ff0000" };
        Check(ButtonSettings.From(buttonDefinition, new ParameterSet(buttonSchema, red)).GlowColor.IsEqualApprox(Colors.Red), "button: glowColor is an instance parameter (wins over the block constant)");
        Check(ButtonSettings.From(buttonDefinition).GlowColor.IsEqualApprox(Colors.Blue), "...a block without the parameter in its schema still uses the \"params\" constant");
        BlockBehaviorRegistry.TryGet(ButtonBehavior.Key, out var buttonBehavior);
        var buttonState = (ButtonState)buttonBehavior.CreateState(buttonDefinition, new ParameterSet(buttonSchema, red));
        Check(buttonState.GlowColor.IsEqualApprox(Colors.Red), "...and the button state carries it (the visual follows it)");

        var wheelSchema = ParseSchema("{ \"parameters\": [ { \"id\": \"maxSteerDegrees\", \"type\": \"Float\", \"min\": 5, \"max\": 90, \"default\": 60 }, { \"id\": \"steerRateDegrees\", \"type\": \"Float\", \"min\": 30, \"max\": 720, \"default\": 180 } ] }");
        var wheelDefinition = MakeFunctional("{ \"params\": { \"maxSteerDegrees\": 45, \"steerRateDegrees\": 90 } }");
        var tuned = WheelSettings.From(wheelDefinition, new ParameterSet(wheelSchema, new Dictionary<string, string> { ["maxSteerDegrees"] = "30", ["steerRateDegrees"] = "360" }));
        Check(Math.Abs(tuned.MaxSteerRadians - 30 * Math.PI / 180) < 1e-9 && Math.Abs(tuned.SteerRateRadians - 360 * Math.PI / 180) < 1e-9, "wheel: max steer angle and steer speed are instance parameters");
        var constant = WheelSettings.From(wheelDefinition);
        Check(Math.Abs(constant.MaxSteerRadians - 45 * Math.PI / 180) < 1e-9 && Math.Abs(constant.SteerRateRadians - 90 * Math.PI / 180) < 1e-9, "...without them in the schema the block's \"params\" apply");
        Check(Math.Abs(WheelSettings.From(MakeFunctional("{}")).MaxSteerRadians - 60 * Math.PI / 180) < 1e-9, "...and without either: 60 degrees");

        var seatDefinition = MakeFunctional("{ \"params\": { \"eyeHeight\": 0.5, \"forward\": \"NegX\" } }");
        var seatSchema = ParseSchema("{ \"parameters\": [ { \"id\": \"eyeHeight\", \"type\": \"Float\", \"default\": 0.75 }, { \"id\": \"forward\", \"type\": \"Enum\", \"options\": [\"NegX\", \"PosX\", \"PosZ\"], \"default\": \"PosX\" } ] }");
        var (height, forward) = PilotSeatBehavior.ReadSeatSettings(seatDefinition, new ParameterSet(seatSchema, new Dictionary<string, string> { ["eyeHeight"] = "1.25", ["forward"] = "PosZ" }));
        Check(Math.Abs(height - 1.25) < 1e-9 && forward == BlockFace.PosZ, "seat: eye height and forward face are instance parameters", $"{height} {forward}");
        var (constHeight, constForward) = PilotSeatBehavior.ReadSeatSettings(seatDefinition);
        Check(Math.Abs(constHeight - 0.5) < 1e-9 && constForward == BlockFace.NegX, "...without the schema the block's \"params\" apply", $"{constHeight} {constForward}");

        var batterySchema = ParseSchema("{ \"parameters\": [ { \"id\": \"capacity\", \"type\": \"Float\", \"min\": 0, \"default\": 1000 } ] }");
        BlockBehaviorRegistry.TryGet(BatteryBehavior.Key, out var batteryBehavior);
        var battery = (BatteryState)batteryBehavior.CreateState(MakeFunctional("{ \"behavior\": \"Battery\", \"capacity\": 100 }"), new ParameterSet(batterySchema, new Dictionary<string, string> { ["capacity"] = "250" }));
        var batteryDefault = (BatteryState)batteryBehavior.CreateState(MakeFunctional("{ \"behavior\": \"Battery\", \"capacity\": 100 }"));
        Check(Math.Abs(battery.Capacity - 250) < 1e-9 && Math.Abs(batteryDefault.Capacity - 100) < 1e-9, "battery: capacity is an instance parameter; without the schema entry the block's capacity applies");
    }

    // ================================================================== панель «Parameters»: настоящие виджеты

    private async Task RunParameterPanelWidgetTests(BuildEditor editor)
    {
        GD.Print("-- Parameters panel: slider / number box / dropdown / colour picker built from the schema, values go out on release");

        using var scope = UseCatalogWith(new FixtureBlock("zz_widgets", "{}", AllKindsParameters));
        var construction = new Construction(new VoxelGrid());
        var instance = PlaceFixture(construction, "zz_widgets", Vector3I.Zero);
        var definition = BlockCatalog.Instance.Get("zz_widgets");

        var layer = new Control();
        editor.AddChild(layer);
        var panel = new ParametersPanelUi(layer);
        var edits = new List<(string Id, string Text)>();
        panel.ParameterEdited += (id, text) => edits.Add((id, text));
        panel.Open(instance, definition, Construction.ParametersOf(definition)!);
        await Frames(editor, 1);

        Check(panel.ControlOf("gain") is HSlider gain && Math.Abs(gain.MinValue - 0.5) < 1e-9 && Math.Abs(gain.MaxValue - 4) < 1e-9 && Math.Abs(gain.Step - 0.25) < 1e-9 && Math.Abs(gain.Value - 2) < 1e-9,
            "a Float with both limits: an HSlider over exactly min..max with the schema's step, at the default value");
        Check(panel.ControlOf("count") is HSlider count && Math.Abs(count.Step - 1) < 1e-9 && Math.Abs(count.Value - 3) < 1e-9, "an Int with both limits: a slider with step 1");
        Check(panel.ControlOf("gain") is HSlider { Scrollable: false }, "...the mouse wheel over a slider scrolls the panel, it does not change the value");
        Check(panel.ControlOf("offset") is SpinBox offsetBox && Math.Abs(offsetBox.Value - 0.75) < 1e-9 && offsetBox.AllowGreater && offsetBox.AllowLesser, "a Float without limits: a number box that accepts any value");
        Check(panel.ControlOf("atLeast") is SpinBox atLeast && atLeast.AllowGreater && !atLeast.AllowLesser && Math.Abs(atLeast.MinValue) < 1e-9, "a Float with only a minimum: a number box that stops at the minimum but has no maximum");
        Check(panel.ControlOf("loops") is SpinBox { Step: 1 }, "an Int without limits: a number box with step 1");
        Check(panel.ControlOf("flag") is OptionButton flagBox && flagBox.ItemCount == 2 && flagBox.GetItemText(0) == "false" && flagBox.GetItemText(1) == "true", "a flag: a dropdown with false / true");
        Check(panel.ControlOf("face") is OptionButton faceBox && faceBox.ItemCount == 3 && panel.DisplayedValue("face") == "PosX", "an enum: a dropdown with its options, at the default");
        Check(panel.ControlOf("tint") is ColorPickerButton && panel.DisplayedValue("tint") == "#33ff55", "a colour: a colour picker button showing the default");
        Check(panel.ParameterIds.Count == 8, "every schema entry has exactly one row", string.Join(",", panel.ParameterIds));

        // Раскладка: ScrollContainer сам не занимает высоту содержимого - без явной высоты список параметров схлопывался в нуль, и в меню НЕ БЫЛО видно ни одного параметра (баг пользователя).
        await Frames(editor, 2);
        var listRect = panel.ListGlobalRect;
        Check(listRect.Size.Y > 100, "the parameter list has real height on screen (a collapsed list shows nothing)", $"{listRect}");
        bool allVisible = panel.ParameterIds.All(panel.RowFullyVisible);
        Check(panel.RowFullyVisible("gain") && panel.RowFullyVisible(panel.ParameterIds[0]), "the first parameter row is fully visible inside the list", $"row={panel.RowGlobalRect("gain")} list={listRect}");
        Check(allVisible || listRect.End.Y >= editor.GetViewport().GetVisibleRect().Size.Y - 120, "...and so is every row, unless the list was capped by the window height (then it scrolls)", $"list={listRect}");
        Check(listRect.End.Y <= editor.GetViewport().GetVisibleRect().Size.Y + 0.5f, "the panel stays inside the window", $"{listRect}");

        // Refresh показывает сохранённые значения и не порождает правок.
        construction.TrySetParameter(instance, "gain", "3.5");
        construction.TrySetParameter(instance, "loops", "12");
        construction.TrySetParameter(instance, "flag", "true");
        construction.TrySetParameter(instance, "tint", "#FF0000");
        panel.Refresh(instance);
        Check(panel.DisplayedValue("gain") == "3.5" && panel.DisplayedValue("loops") == "12" && panel.DisplayedValue("flag") == "true" && panel.DisplayedValue("tint") == "#ff0000",
            "Refresh shows the instance's values in every kind of widget", $"{panel.DisplayedValue("gain")} {panel.DisplayedValue("loops")} {panel.DisplayedValue("flag")} {panel.DisplayedValue("tint")}");
        Check(edits.Count == 0, "...and showing values raises no edit event (nothing is written back by redrawing)", $"{edits.Count}");

        // Настоящие сигналы виджетов -> правка с каноническим текстом.
        var slider = (HSlider)panel.ControlOf("gain");
        slider.Value = 1.25;
        Check(edits.Count == 0, "dragging a slider changes nothing in the block until it is released");
        slider.EmitSignal(Godot.Range.SignalName.ValueChanged, 1.25);
        slider.EmitSignal(Slider.SignalName.DragEnded, true);
        Check(edits.Count == 1 && edits[0] == ("gain", "1.25"), "releasing the slider sends one edit with the value", string.Join(";", edits));
        slider.EmitSignal(Slider.SignalName.DragEnded, false);
        Check(edits.Count == 1, "a click on the slider that changed nothing sends nothing");

        var box = (SpinBox)panel.ControlOf("offset");
        box.Value = 7.5;
        Check(edits.Count == 2 && edits[1] == ("offset", "7.5"), "committing a number box sends the value", string.Join(";", edits));

        var flag = (OptionButton)panel.ControlOf("flag");
        flag.Select(0);
        flag.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        Check(edits.Count == 3 && edits[2] == ("flag", "false"), "choosing 'false' in the flag dropdown sends false", string.Join(";", edits));
        var face = (OptionButton)panel.ControlOf("face");
        face.Select(2);
        face.EmitSignal(OptionButton.SignalName.ItemSelected, 2);
        Check(edits.Count == 4 && edits[3] == ("face", "PosY"), "choosing an option in the enum dropdown sends its name", string.Join(";", edits));

        var picker = (ColorPickerButton)panel.ControlOf("tint");
        picker.Color = new Color(0, 0, 1);
        Check(edits.Count == 4, "moving inside the colour popup sends nothing yet");
        picker.EmitSignal(ColorPickerButton.SignalName.PopupClosed);
        Check(edits.Count == 5 && edits[4] == ("tint", "#0000ff"), "closing the colour popup sends the colour", string.Join(";", edits));

        layer.QueueFree();
        await Frames(editor, 1);
    }
}
