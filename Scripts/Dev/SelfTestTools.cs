using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.Dev;

public sealed partial class SelfTest
{
    private const string ToolModelPath = "res://meshes/selftest_tool_model.gltf";

    // ================================================================== инструменты редактора построек: «Nodes» и «Parameters»

    private async Task RunEditorToolTests(BuildEditor editor)
    {
        GD.Print("-- editor tools: Nodes (wiring by dragging between nodes) and Parameters (highlight, hover, side panel)");

        // Блоки с настоящей (самодельной) моделью - чтобы проверить подсветку моделей; обычные фикстуры рисуются кубом-плейсхолдером.
        WriteTestGltf(ToolModelPath, (Vector3.Zero, new Vector3(2, 2, 2)));
        FunctionalBlockGeometry.ClearSceneCache(ToolModelPath);
        string modelJson = $"{{ \"scene\": \"{ToolModelPath}\", \"behavior\": \"ElectricMotor\", \"nodes\": [ {{ \"id\": \"sig\", \"type\": \"Number\", \"direction\": \"In\" }}, {{ \"id\": \"pwr\", \"type\": \"Electricity\", \"direction\": \"In\" }} ] }}";
        string plainModelJson = $"{{ \"scene\": \"{ToolModelPath}\" }}";

        using var scope = UseCatalogWith(
            new FixtureBlock("zz_bat", BatteryJson, BatteryParameters),
            new FixtureBlock("zz_motor", MotorJson, MotorParameters),
            new FixtureBlock("zz_shaft", ShaftJson),
            new FixtureBlock("zz_button", ButtonJson, ButtonParameters),
            new FixtureBlock("zz_seat", SeatJson(), SeatParameters()),
            new FixtureBlock("zz_confmodel", modelJson, MotorParameters),
            new FixtureBlock("zz_plainmodel", plainModelJson));

        var state = editor.State;
        var construction = editor.World.Construction;
        construction.Clear();
        state.Tool = ToolMode.None;
        var camera = editor.EditorCamera;
        camera.LookAtPoint(new Vector3(1.6f, 1.7f, 3.2f), new Vector3(1.6f, 0.6f, 0f));

        var battery = PlaceFixture(construction, "zz_bat", new Vector3I(0, 0, 0));
        var motor = PlaceFixture(construction, "zz_motor", new Vector3I(4, 0, 0));
        var motor2 = PlaceFixture(construction, "zz_motor", new Vector3I(6, 0, 0));
        var button = PlaceFixture(construction, "zz_button", new Vector3I(8, 0, 0));
        var seat = PlaceFixture(construction, "zz_seat", new Vector3I(10, 0, 0));
        var shaft = PlaceFixture(construction, "zz_shaft", new Vector3I(14, 0, 0));
        var confModel = PlaceFixture(construction, "zz_confmodel", new Vector3I(16, 0, 0));
        var plainModel = PlaceFixture(construction, "zz_plainmodel", new Vector3I(18, 0, 0));
        editor.World.RebuildDirty();
        await Frames(editor, 3);

        // После Undo/Redo постройка восстанавливается НОВЫМИ экземплярами (id меняются) - ссылки на блоки всегда берутся по слагу и клетке.
        BlockInstance Fresh(BlockInstance instance) => construction.Instances.First(i => i.BlockSlug == instance.BlockSlug && i.Origin == instance.Origin);
        Vector2 ScreenOf(NodeRef node) => camera.UnprojectPosition(editor.WireOverlay.PositionOf(node)!.Value);
        NodeRef Ref(BlockInstance instance, string id) => new(Fresh(instance).InstanceId, id);
        int WiresBetween(BlockInstance from, string fromNode, BlockInstance to, string toNode) =>
            construction.Wires.Count(w => w.FromInstance == Fresh(from).InstanceId && w.FromNode == fromNode && w.ToInstance == Fresh(to).InstanceId && w.ToNode == toNode);

        // ---------------------------------------------------------------- Nodes
        Check(editor.World.Opacity == 1f && !editor.WireOverlay.Visible, "setup: no tool - the construction is opaque and the node layer is hidden");
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.N, Pressed = true });
        await Frames(editor, 2);
        Check(state.Tool == ToolMode.Wire, "N switches on the Nodes tool");
        Check(Math.Abs(editor.World.Opacity - BuildEditor.ToolGhostOpacity) < 1e-6 && editor.World.FunctionalBlocks.DefaultOpacity < 1f, "the construction becomes translucent under the Nodes tool (rubber/shape layers and models)");
        Check(editor.WireOverlay.Visible, "the node markers are shown");

        // Ctrl - настоящими событиями клавиатуры (редактор следит за ними сам); ЛКМ - вызовами инструмента с экранными координатами нод.
        bool ctrlDown = false;
        async Task SetCtrl(bool down)
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Ctrl, PhysicalKeycode = Key.Ctrl, Pressed = down });
            await Frames(editor, 2);
            ctrlDown = down;
        }

        void Press(NodeRef node) => editor.WireToolPress(ScreenOf(node), ctrlDown);
        void ReleaseOn(NodeRef node) => editor.WireToolRelease(ScreenOf(node));
        void Drag(NodeRef from, NodeRef to) { Press(from); ReleaseOn(to); }
        void ClickNode(NodeRef node) { Press(node); ReleaseOn(node); }
        void ClickEmpty() { editor.WireToolPress(new Vector2(-4000, -4000), ctrlDown); editor.WireToolRelease(new Vector2(-4000, -4000)); }

        // ---- слои: по умолчанию электричество, на вкладке «Logic» - Number и Boolean
        int NodesOf(Func<LogicNode, bool> match) => construction.Instances.Sum(i => BlockCatalog.Instance.Get(i.BlockSlug).GetComponent<FunctionalBlockComponent>()?.Nodes.Count(match) ?? 0);
        int electricityNodes = NodesOf(n => n.Type == NodeType.Electricity);
        int logicNodes = NodesOf(n => n.Type != NodeType.Electricity);
        Check(state.WireLayer == WireLayer.Electricity && editor.WireOverlay.Layer == WireLayer.Electricity, "the Electricity layer is shown by default");
        Check(editor.WireOverlay.MarkerCount == electricityNodes && electricityNodes >= 5 && logicNodes > 15, "only the electricity nodes have markers on the Electricity layer", $"{editor.WireOverlay.MarkerCount} vs {electricityNodes}");
        Check(editor.WireOverlay.PositionOf(Ref(seat, "ws_out")) == null && editor.WireOverlay.PositionOf(Ref(battery, "level")) == null, "Number/Boolean nodes are not drawn (and cannot be picked) on the Electricity layer");
        var yellow = WireOverlay.ColorOf(NodeType.Electricity);
        var green = WireOverlay.ColorOf(NodeType.Number);
        var red = WireOverlay.ColorOf(NodeType.Boolean);
        Check(yellow.R > 0.8f && yellow.G > 0.7f && yellow.B < 0.4f, "Electricity nodes are yellow");
        Check(green.G > 0.7f && green.R < 0.5f && green.B < 0.5f, "Number nodes are green", $"{green}");
        Check(red.R > 0.8f && red.G < 0.5f && red.B < 0.5f, "Boolean nodes are red", $"{red}");
        var electricityTab = editor.Ui.WireLayerTab(WireLayer.Electricity);
        var logicTab = editor.Ui.WireLayerTab(WireLayer.Logic);
        Check(electricityTab.IsVisibleInTree() && logicTab.IsVisibleInTree() && electricityTab.ButtonPressed && !logicTab.ButtonPressed, "the layer tabs are on the toolbar while the Nodes tool is on; Electricity is the active one");
        Check(editor.PickWireNode(ScreenOf(Ref(battery, "pwr_out"))) == Ref(battery, "pwr_out") && editor.PickWireNode(ScreenOf(Ref(motor, "pwr"))) == Ref(motor, "pwr"), "clicking a marker picks that node");
        Check(editor.PickWireNode(new Vector2(-4000, -4000)) == null, "clicking away from every marker picks nothing");

        // ---- электричество: перетаскивание создаёт провод, повторное между теми же нодами - убирает
        Press(Ref(battery, "pwr_out"));
        Check(editor.WireDragFrom == Ref(battery, "pwr_out") && editor.WireSelected == Ref(battery, "pwr_out"), "pressing LMB on a node selects it and starts a drag from it");
        ReleaseOn(Ref(motor, "pwr"));
        Check(editor.WireDragFrom == null && WiresBetween(battery, "pwr_out", motor, "pwr") == 1, "dropping on a compatible node creates the wire (output -> input)");
        Check(editor.WireOverlay.WireCount == 1 && editor.WireSelected == Ref(battery, "pwr_out"), "...it is drawn and the origin node stays selected");

        Drag(Ref(motor2, "pwr"), Ref(battery, "pwr_out")); // с входа на выход: тот же провод «от выхода ко входу»
        Check(WiresBetween(battery, "pwr_out", motor2, "pwr") == 1, "dragging from an INPUT onto an OUTPUT makes the same wire: the value still flows from the output to the input");

        int wiresBeforeToggle = construction.Wires.Count;
        Drag(Ref(battery, "pwr_out"), Ref(motor, "pwr")); // ровно те же две ноды тем же способом
        Check(construction.Wires.Count == wiresBeforeToggle - 1 && WiresBetween(battery, "pwr_out", motor, "pwr") == 0 && editor.Ui.Status.Contains("Disconnected"),
            "dragging between two connected nodes breaks the wire (the same gesture as connecting)", editor.Ui.Status);
        Check(editor.WireOverlay.WireCount == construction.Wires.Count, "...and it disappears from the overlay");
        Check(editor.Undo.Undo(construction, BlockCatalog.Instance) && WiresBetween(battery, "pwr_out", motor, "pwr") == 1, "ONE Ctrl+Z puts the removed wire back");

        Drag(Ref(battery, "pwr_out"), Ref(motor2, "pwr")); // провод был создан с входа на выход - убираем протягиванием с выхода на вход
        Check(WiresBetween(battery, "pwr_out", motor2, "pwr") == 0, "the wire made input->output is broken by output->input (direction does not matter for breaking either)");
        Drag(Ref(motor2, "pwr"), Ref(battery, "pwr_out"));
        Check(WiresBetween(battery, "pwr_out", motor2, "pwr") == 1, "(the same pair dragged again makes it once more)");

        int beforeInvalid = construction.Wires.Count;
        Drag(Ref(motor, "pwr"), Ref(motor2, "pwr"));
        Check(construction.Wires.Count == beforeInvalid && editor.Ui.Status.Contains("Cannot connect"), "two inputs cannot be wired: no wire, the status line says why", editor.Ui.Status);
        Drag(Ref(battery, "pwr_out"), Ref(battery, "pwr_in"));
        Check(construction.Wires.Count == beforeInvalid && editor.Ui.Status.Contains("itself"), "a wire from a block to itself is refused", editor.Ui.Status);
        editor.Ui.SetStatus("");

        // ---- вкладка «Logic»: другие ноды, провода другого слоя не рисуются, выбор нод чужого слоя сбрасывается
        editor.WireToolPress(ScreenOf(Ref(battery, "pwr_out")), false); // выбрана электрическая нода
        editor.WireToolRelease(new Vector2(-4000, -4000));
        Check(editor.WireSelected == Ref(battery, "pwr_out"), "(setup) an electricity node is selected");
        logicTab.ButtonPressed = true;
        await Frames(editor, 2);
        Check(state.WireLayer == WireLayer.Logic && editor.WireOverlay.Layer == WireLayer.Logic && logicTab.ButtonPressed && !electricityTab.ButtonPressed, "clicking the Logic tab switches the layer");
        Check(editor.WireOverlay.MarkerCount == logicNodes, "the Logic layer shows exactly the Number and Boolean nodes", $"{editor.WireOverlay.MarkerCount} vs {logicNodes}");
        Check(editor.WireOverlay.PositionOf(Ref(battery, "pwr_out")) == null && editor.WireOverlay.WireCount == 0, "the electricity nodes and wires are hidden on the Logic layer");
        Check(editor.WireSelected == null, "the selection of a node from the other layer is dropped");

        // ---- логика: Number, Boolean -> Number, отказы, замена источника у входа
        Drag(Ref(seat, "ws_out"), Ref(motor, "sig"));
        Check(WiresBetween(seat, "ws_out", motor, "sig") == 1, "a Number output wires to a Number input");
        Drag(Ref(button, "signal"), Ref(confModel, "sig"));
        Check(WiresBetween(button, "signal", confModel, "sig") == 1, "a Boolean output wires to a Number input");
        Drag(Ref(confModel, "sig"), Ref(button, "signal"));
        Check(WiresBetween(button, "signal", confModel, "sig") == 0, "...and dragging back from the input breaks it again");

        int beforeLogicInvalid = construction.Wires.Count;
        Drag(Ref(seat, "ws_out"), Ref(button, "signal"));
        Check(construction.Wires.Count == beforeLogicInvalid && editor.Ui.Status.Contains("two outputs"), "two outputs cannot be wired", editor.Ui.Status);
        Drag(Ref(seat, "ws_out"), Ref(seat, "hotkey_1"));
        Check(construction.Wires.Count == beforeLogicInvalid, "a seat is not wired to itself");

        Drag(Ref(button, "signal"), Ref(motor, "sig"));
        Check(WiresBetween(button, "signal", motor, "sig") == 1 && WiresBetween(seat, "ws_out", motor, "sig") == 0, "a Number/Boolean input takes one source: a new wire replaces the old one");
        editor.Undo.Undo(construction, BlockCatalog.Instance);
        Check(WiresBetween(seat, "ws_out", motor, "sig") == 1 && WiresBetween(button, "signal", motor, "sig") == 0, "(...one Ctrl+Z gives the old source back)");

        // ---- Ctrl: якорь, выбранный ДО нажатия Ctrl
        ClickEmpty();
        ClickNode(Ref(seat, "ws_out"));
        Check(editor.WireSelected == Ref(seat, "ws_out") && editor.WireAnchor == null, "a plain click selects the node (no anchor without Ctrl)");
        await SetCtrl(true);
        Check(editor.WireAnchor == Ref(seat, "ws_out") && editor.WireOverlay.SelectedIsAnchor && editor.WireOverlay.Selected == Ref(seat, "ws_out"), "Ctrl remembers the node selected BEFORE it was pressed as the anchor");
        ClickNode(Ref(motor2, "sig"));
        ClickNode(Ref(confModel, "sig"));
        Check(WiresBetween(seat, "ws_out", motor, "sig") == 1 && WiresBetween(seat, "ws_out", motor2, "sig") == 1 && WiresBetween(seat, "ws_out", confModel, "sig") == 1,
            "while Ctrl is held, clicking other nodes wires them all to the anchor (one node -> several)");
        Check(editor.WireAnchor == Ref(seat, "ws_out"), "...and the anchor stays the same");

        // Ctrl работает и для разрыва: повторный клик по уже подключённой ноде; перетаскивание ОТ чужой ноды всё равно идёт от якоря.
        ClickNode(Ref(motor2, "sig"));
        Check(WiresBetween(seat, "ws_out", motor2, "sig") == 0 && WiresBetween(seat, "ws_out", confModel, "sig") == 1, "Ctrl + click on an already connected node breaks that wire (the others stay)");
        Drag(Ref(motor, "sig"), Ref(confModel, "sig"));
        Check(WiresBetween(seat, "ws_out", confModel, "sig") == 0 && WiresBetween(seat, "ws_out", motor, "sig") == 1, "with Ctrl the wire always goes from the anchor, not from the node the drag started on");
        ClickNode(Ref(confModel, "sig"));
        Check(WiresBetween(seat, "ws_out", confModel, "sig") == 1, "(and it can be made again)");

        await SetCtrl(false);
        Check(editor.WireAnchor == null && editor.WireSelected == Ref(seat, "ws_out") && !editor.WireOverlay.SelectedIsAnchor, "releasing Ctrl forgets the anchor; the node stays selected");
        int wiresAfterCtrl = construction.Wires.Count;
        ClickNode(Ref(motor2, "sig"));
        Check(construction.Wires.Count == wiresAfterCtrl && editor.WireSelected == Ref(motor2, "sig"), "without Ctrl a click only selects another node");

        // ---- Ctrl, зажатый ДО выбора: первая нажатая нода становится якорем
        ClickEmpty();
        Check(editor.WireSelected == null, "a click on empty space clears the selection");
        await SetCtrl(true);
        Check(editor.WireAnchor == null, "(setup) Ctrl pressed with nothing selected: no anchor yet");
        ClickEmpty();
        Check(editor.WireAnchor == null, "Ctrl + click on empty space does not make an anchor");
        ClickNode(Ref(seat, "ad_out"));
        Check(editor.WireAnchor == Ref(seat, "ad_out") && editor.WireSelected == Ref(seat, "ad_out"), "Ctrl pressed first: the first node clicked becomes the anchor");
        ClickNode(Ref(motor2, "sig"));
        Check(WiresBetween(seat, "ad_out", motor2, "sig") == 1, "...and the next click connects it");
        await SetCtrl(false);

        // ---- Esc: сначала отменяет перетаскивание, затем снимает выбор
        int wiresBeforeEsc = construction.Wires.Count;
        Press(Ref(seat, "lf_out"));
        Check(editor.WireDragFrom == Ref(seat, "lf_out"), "(setup) a drag is in progress");
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Escape, Pressed = true });
        await Frames(editor, 2);
        Check(editor.WireDragFrom == null && editor.WireSelected == Ref(seat, "lf_out") && construction.Wires.Count == wiresBeforeEsc, "Esc during a drag cancels the drag and changes nothing; the node stays selected");
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Escape, Pressed = true });
        await Frames(editor, 2);
        Check(editor.WireSelected == null && state.Tool == ToolMode.Wire, "the next Esc clears the selection; the tool stays on");

        // ---- ПКМ убирает все провода ноды
        Check(construction.WiresOf(Fresh(seat).InstanceId, "ws_out").Count() == 2, "(setup) the seat axis has two wires");
        editor.WireToolRemove(ScreenOf(Ref(seat, "ws_out")));
        Check(!construction.WiresOf(Fresh(seat).InstanceId, "ws_out").Any(), "RMB on a node removes all of its wires");
        Check(editor.WireOverlay.WireCount == construction.Wires.Count(w => WireOverlay.LayerOf(construction.FindNode(w.FromInstance, w.FromNode)!.Type) == WireLayer.Logic),
            "the drawn wires of the layer follow the construction", $"{editor.WireOverlay.WireCount}");

        // ---- перетаскивание живой мышью: подсветка ноды под курсором, «резинка»
        Press(Ref(seat, "ws_out"));
        await Move(editor, ScreenOf(Ref(motor, "sig")));
        Check(editor.WireOverlay.Hovered == Ref(motor, "sig"), "the node under the cursor is highlighted while dragging");
        ReleaseOn(Ref(motor, "sig"));
        Check(WiresBetween(seat, "ws_out", motor, "sig") == 1, "(the wire made by dragging with real mouse movement)");

        // ---- назад на электричество: провода слоя снова видны
        electricityTab.ButtonPressed = true;
        await Frames(editor, 2);
        Check(state.WireLayer == WireLayer.Electricity && editor.WireOverlay.MarkerCount == electricityNodes && editor.WireOverlay.WireCount == 2, "back on the Electricity layer its wires are drawn again", $"{editor.WireOverlay.WireCount}");
        editor.WireToolRemove(ScreenOf(Ref(battery, "pwr_out")));
        Check(!construction.WiresOf(Fresh(battery).InstanceId, "pwr_out").Any(), "RMB works on the Electricity layer too");


        // выход из инструмента убирает слой, вкладки и выбор
        await SetCtrl(false);
        ClickNode(Ref(battery, "pwr_out")); // выбранная нода к моменту выхода из инструмента
        Check(editor.WireSelected != null, "(setup) a node is selected when the tool is switched off");
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.N, Pressed = true });
        await Frames(editor, 2);
        Check(state.Tool == ToolMode.None && editor.World.Opacity == 1f && !editor.WireOverlay.Visible, "N again switches the tool off: opaque construction, node layer hidden");
        Check(editor.WireSelected == null && editor.WireAnchor == null && !editor.Ui.WireLayerTab(WireLayer.Logic).IsVisibleInTree(), "...the selection is forgotten and the layer tabs are hidden");

        // ---------------------------------------------------------------- Parameters
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.P, Pressed = true });
        await Frames(editor, 2);
        Check(state.Tool == ToolMode.Parameters, "P switches on the Parameters tool");
        Check(Math.Abs(editor.World.Opacity - BuildEditor.ToolGhostOpacity) < 1e-6, "blocks become translucent");

        // Блоки без модели: подсвечиваются боксом; с моделью - красится сама модель.
        int configurableWithoutModel = new[] { battery, motor, motor2, button, seat }.Length;
        Check(editor.ParameterBoxCount == configurableWithoutModel, "every configurable block WITHOUT a model gets a purple box (the shaft does not)", $"{editor.ParameterBoxCount}");

        var confView = editor.World.FunctionalBlocks.GetView(Fresh(confModel).InstanceId);
        var plainView = editor.World.FunctionalBlocks.GetView(Fresh(plainModel).InstanceId);
        Check(confView != null && plainView != null, "(setup) both model blocks have a model node");
        var confMesh = FirstMesh(confView!);
        var plainMesh = FirstMesh(plainView!);
        Check(confMesh.MaterialOverride is StandardMaterial3D { AlbedoColor: var purple } && purple.B > purple.G && purple.R < 0.4f && purple.G < 0.2f && confMesh.Transparency == 0,
            "a configurable model block is painted dark purple and stays opaque", confMesh.MaterialOverride is StandardMaterial3D m ? $"{m.AlbedoColor}" : "no override");
        Check(plainMesh.MaterialOverride == null && Math.Abs(plainMesh.Transparency - (1f - BuildEditor.ToolGhostOpacity)) < 1e-5,
            "a model block with no settings is NOT painted, it is just translucent", $"{plainMesh.Transparency}");

        // Наведение: цвет меняется на голубой.
        var motorCenter = BuildSpace.CellCenter(motor.Origin);
        await Move(editor, camera.UnprojectPosition(motorCenter));
        Check(editor.ParametersHoverInstanceId == Fresh(motor).InstanceId, "the configurable block under the cursor is the hovered one", $"{editor.ParametersHoverInstanceId} vs {Fresh(motor).InstanceId}");
        var confCenter = BuildSpace.CellCenter(confModel.Origin);
        await Move(editor, camera.UnprojectPosition(confCenter));
        Check(editor.ParametersHoverInstanceId == Fresh(confModel).InstanceId && confMesh.MaterialOverride is StandardMaterial3D { AlbedoColor: var cyan } && cyan.B > 0.8f && cyan.G > 0.6f && cyan.R < 0.3f,
            "hovering a configurable model block turns it from dark purple to cyan", confMesh.MaterialOverride is StandardMaterial3D m2 ? $"{m2.AlbedoColor}" : "no override");
        await Move(editor, camera.UnprojectPosition(BuildSpace.CellCenter(shaft.Origin)));
        Check(editor.ParametersHoverInstanceId == 0 && confMesh.MaterialOverride is StandardMaterial3D { AlbedoColor: var backToPurple } && backToPurple.G < 0.2f,
            "moving off it (onto a block without settings) turns it back to purple and nothing is hovered");

        // Клик по блоку без настроек / по пустому месту - панели нет.
        editor.ParametersToolClickAt(camera.UnprojectPosition(BuildSpace.CellCenter(shaft.Origin)));
        Check(!editor.Ui.ParametersPanel.IsOpen && editor.ParametersSelectedInstanceId == 0, "clicking a block without settings opens nothing");

        // Клик по мотору: панель слева с его параметрами.
        editor.ParametersToolClickAt(camera.UnprojectPosition(motorCenter));
        var panel = editor.Ui.ParametersPanel;
        Check(panel.IsOpen && panel.InstanceId == Fresh(motor).InstanceId && editor.ParametersSelectedInstanceId == Fresh(motor).InstanceId, "clicking a configurable block opens its panel");
        Check(panel.ParameterIds.SequenceEqual(new[] { "maxPower", "maxRpm" }) && panel.Title == "zz_motor", "the panel is built from the block's schema, in schema order", string.Join(",", panel.ParameterIds));
        Check(panel.GlobalRect.Position.X < 100 && panel.GlobalRect.Size.X > 200, "the panel is on the LEFT side of the screen", $"{panel.GlobalRect}");
        Check(panel.DisplayedValue("maxPower") == "10" && panel.DisplayedValue("maxRpm") == "600", "it shows the default values", $"{panel.DisplayedValue("maxPower")} {panel.DisplayedValue("maxRpm")}");
        Check(editor.Ui.IsPointOverUi(panel.GlobalRect.GetCenter()), "clicks over the panel do not reach the world");

        // Правка: значение попадает в блок, Undo откатывает.
        panel.EditForTesting("maxRpm", "1200");
        Check(Fresh(motor).Parameters is { Count: 1 } && Fresh(motor).Parameters!["maxRpm"] == "1200" && panel.DisplayedValue("maxRpm") == "1200", "typing a value changes the block and the panel keeps showing it");
        panel.EditForTesting("maxRpm", "99999");
        Check(Fresh(motor).Parameters!["maxRpm"] == "10000" && panel.DisplayedValue("maxRpm") == "10000", "an out-of-range value is clamped and the CLAMPED value is shown");
        panel.EditForTesting("maxRpm", "turbo");
        Check(Fresh(motor).Parameters!["maxRpm"] == "10000", "text that is not a number changes nothing");
        editor.Undo.Undo(construction, BlockCatalog.Instance);
        // после Undo экземпляры пересоздаются - берём актуальные
        var motorAfterUndo = construction.Instances.First(i => i.BlockSlug == "zz_motor" && i.Origin == motor.Origin);
        Check(motorAfterUndo.Parameters is { Count: 1 } && motorAfterUndo.Parameters["maxRpm"] == "1200", "Ctrl+Z undoes the last parameter edit (10000 -> 1200)");
        editor.Undo.Undo(construction, BlockCatalog.Instance);
        motorAfterUndo = construction.Instances.First(i => i.BlockSlug == "zz_motor" && i.Origin == motor.Origin);
        Check(motorAfterUndo.Parameters == null, "...and the one before it (back to the default)");

        // Набор виджетов по типу параметра.
        editor.ParametersToolClickAt(camera.UnprojectPosition(BuildSpace.CellCenter(seat.Origin)));
        var seatNow = construction.Instances.First(i => i.BlockSlug == "zz_seat");
        Check(panel.IsOpen && panel.InstanceId == seatNow.InstanceId && panel.ParameterIds.Count == 8, "the seat panel lists its 8 axis parameters", $"{panel.ParameterIds.Count}");
        Check(panel.ControlKind("ad_mode") == nameof(OptionButton) && panel.ControlKind("ad_sensitivity") == nameof(LineEdit), "an Enum is a dropdown, a Float is a text field");
        panel.EditForTesting("ws_mode", "sticky");
        panel.EditForTesting("ws_sensitivity", "7.5");
        seatNow = construction.Instances.First(i => i.BlockSlug == "zz_seat");
        Check(seatNow.Parameters is { Count: 2 } && seatNow.Parameters["ws_mode"] == "sticky" && seatNow.Parameters["ws_sensitivity"] == "7.5" && panel.DisplayedValue("ws_mode") == "sticky",
            "axis mode and sensitivity are stored on THIS seat");

        // Печать в поле панели не включает горячие клавиши редактора.
        Check(panel.FocusFirstTextFieldForTesting(), "(setup) focus a text field of the panel");
        await Frames(editor, 1);
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.N, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.X, Pressed = true });
        await Frames(editor, 2);
        Check(state.Tool == ToolMode.Parameters, "typing letters into a panel field does not trigger the N/X editor hotkeys");
        editor.GetViewport().GuiReleaseFocus();

        // Esc закрывает панель, инструмент остаётся.
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Escape, Pressed = true });
        await Frames(editor, 2);
        Check(!panel.IsOpen && editor.ParametersSelectedInstanceId == 0 && state.Tool == ToolMode.Parameters, "Esc closes the panel; the tool stays on");

        // Клик по пустому месту закрывает панель.
        editor.ParametersToolClickAt(camera.UnprojectPosition(motorCenter));
        Check(panel.IsOpen, "(setup) the panel is open again");
        editor.ParametersToolClickAt(new Vector2(500, 60));
        Check(!panel.IsOpen, "a click on empty space closes the panel");

        // Смена инструмента закрывает панель и снимает подсветку.
        editor.ParametersToolClickAt(camera.UnprojectPosition(motorCenter));
        state.Tool = ToolMode.None;
        await Frames(editor, 2);
        var confMeshAfter = FirstMesh(editor.World.FunctionalBlocks.GetView(Fresh(confModel).InstanceId)!);
        Check(!panel.IsOpen && editor.ParameterBoxCount == 0 && editor.World.Opacity == 1f && confMeshAfter.MaterialOverride == null && confMeshAfter.Transparency == 0,
            "leaving the tool closes the panel and removes every highlight (opaque again)");

        // Провода и параметры сохраняются вместе с постройкой.
        string saved = ConstructionIO.Serialize(construction);
        var reloaded = new Construction(new VoxelGrid());
        ConstructionIO.Deserialize(reloaded, saved, BlockCatalog.Instance);
        Check(reloaded.Wires.Count == construction.Wires.Count && reloaded.Instances.Any(i => i.Parameters != null), "the wires and parameters made with the tools survive Save/Load of the construction");

        construction.Clear();
        state.Tool = ToolMode.None;
        if (FileAccess.FileExists(ToolModelPath)) DirAccess.RemoveAbsolute(ToolModelPath);
        FunctionalBlockGeometry.ClearSceneCache(ToolModelPath);
        await Frames(editor, 2);
    }

    private static MeshInstance3D FirstMesh(Node root)
    {
        if (root is MeshInstance3D mesh) return mesh;
        foreach (var child in root.GetChildren())
        {
            var found = FirstMeshOrNull(child);
            if (found != null) return found;
        }

        throw new InvalidOperationException("the model has no mesh");
    }

    private static MeshInstance3D? FirstMeshOrNull(Node root)
    {
        if (root is MeshInstance3D mesh) return mesh;
        foreach (var child in root.GetChildren())
        {
            var found = FirstMeshOrNull(child);
            if (found != null) return found;
        }

        return null;
    }
}
