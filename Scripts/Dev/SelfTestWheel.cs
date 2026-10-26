using System;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.Dev;

public sealed partial class SelfTest
{
    /// <summary>
    /// Контракт блока-колеса без зависимости от реального <c>blocks/</c> (пользователь правит файлы): (1) ступица модели (начало координат Wheel/Steer/Spin) при заданных масштабе и
    /// якоре встаёт в центр корневой клетки (0,0,0), где её берёт физика (<c>WheelPhysics</c>), а ручной footprint содержит эту клетку; (2) id входных нод колеса по умолчанию
    /// (<c>Steering</c> / <c>Variable brake</c> / <c>Brake</c>) и их переопределение через <c>"params"</c>.
    /// </summary>
    private void RunWheelBlockContractTests()
    {
        GD.Print("-- wheel block contract: hub lands in the centre of cell (0,0,0), default input node ids and their override");
        const float cell = BuildSpace.CellSize;

        // ---- геометрия: модель колеса 3x3 (шина 6 м в диаметре; коннектор выступает за клетку по +X)
        var model = new Aabb(new Vector3(-1, -3, -3), new Vector3(4.14f, 6, 6));
        var scale = BlockModelLayout.DefaultScaleVector;
        var anchor = new Vector3(0, 0.5f, 0.5f);
        var transform = BlockModelLayout.ModelTransform(model, scale, anchor);
        var hub = transform * Vector3.Zero;
        Check(hub.IsEqualApprox(new Vector3(cell / 2, cell / 2, cell / 2)), "anchor (0, 0.5, 0.5) puts the model origin (the hub) at (0.125, 0.125, 0.125) m of the block frame", $"{hub}");

        var footprintMin = new Vector3I(0, -1, -1);
        var footprintSize = new Vector3I(2, 3, 3);
        bool containsRoot = true;
        for (int axis = 0; axis < 3; axis++) containsRoot &= footprintMin[axis] <= 0 && 0 < footprintMin[axis] + footprintSize[axis];
        Check(containsRoot, "the manual footprint min (0,-1,-1), size (2,3,3) includes the root cell (0,0,0)");
        var (sanitizedMin, sanitizedSize) = BlockModelLayout.SanitizeFootprint(footprintMin, footprintSize);
        Check(sanitizedMin == footprintMin && sanitizedSize == footprintSize, "...so the editor leaves it as typed");

        var physicsHub = FunctionalBlockGeometry.ComputeNodeAnchor(Vector3I.Zero, footprintMin, footprintSize, cell);
        Check(physicsHub.IsEqualApprox(hub), "the point the physics takes as the wheel centre (centre of cell (0,0,0), WheelPhysics.Rebuild) is exactly where the model's hub sits", $"{physicsHub} vs {hub}");

        var bounds = BlockModelLayout.BoundsInBlockFrame(model, scale, anchor);
        Check(bounds.Position.IsEqualApprox(new Vector3(0, -2 * cell / 2, -2 * cell / 2)) && bounds.End.IsEqualApprox(new Vector3(4.14f * 0.125f, 0.5f, 0.5f)),
            "the model bbox in the block frame: X from the tire's back face at 0 to the connector at 0.5175 m, Y and Z from -0.25 to +0.5 m (tire radius 3 cells around the hub)", $"{bounds}");

        // ---- id нод колеса по умолчанию и переопределение
        var plain = WheelSettings.From(MakeFunctional("{ \"behavior\": \"Wheel\" }"));
        Check(plain.SteerSignal == "Steering" && plain.BrakeSignal == "Variable brake" && plain.FullBrakeSignal == "Brake",
            "default input node ids: Steering (steer), Variable brake (brake 0..1), Brake (full brake)", $"{plain.SteerSignal}/{plain.BrakeSignal}/{plain.FullBrakeSignal}");
        Check(plain.SteerSignal == WheelSettings.DefaultSteerSignal && plain.BrakeSignal == WheelSettings.DefaultBrakeSignal && plain.FullBrakeSignal == WheelSettings.DefaultFullBrakeSignal,
            "...and they are the public default constants");
        Check(plain.SteerNode == "Steer" && plain.SpinNode == "Spin" && plain.RimNode == "Rim" && plain.TireNode == "Tire", "model node names stay Steer / Spin / Rim / Tire");

        var overridden = WheelSettings.From(MakeFunctional("{ \"behavior\": \"Wheel\", \"params\": { \"steerSignal\": \"s\", \"brakeSignal\": \"b\", \"fullBrakeSignal\": \"f\" } }"));
        Check(overridden.SteerSignal == "s" && overridden.BrakeSignal == "b" && overridden.FullBrakeSignal == "f", "\"params\" overrides each input node id", $"{overridden.SteerSignal}/{overridden.BrakeSignal}/{overridden.FullBrakeSignal}");
        var partial = WheelSettings.From(MakeFunctional("{ \"params\": { \"brakeSignal\": \"b\" } }"));
        Check(partial.SteerSignal == "Steering" && partial.BrakeSignal == "b" && partial.FullBrakeSignal == "Brake", "...one at a time: the others keep their defaults");
        var modelRadii = WheelSettings.From(MakeFunctional("{ \"params\": { \"modelRimRadius\": 0.75 } }"));
        Check(Math.Abs(modelRadii.ModelRimRadiusCells - 0.75) < 1e-9 && Math.Abs(modelRadii.ModelTireRadiusCells - 1.5) < 1e-9, "modelRimRadius comes from \"params\"; modelTireRadius defaults to 1.5");

        // ---- поведение читает именно эти id
        BlockBehaviorRegistry.TryGet(WheelBehavior.Key, out var behavior);
        var state = (WheelState)behavior.CreateState(MakeFunctional("{ \"behavior\": \"Wheel\" }"));
        behavior.SetInput(state, "Steering", new NodeValue(0.5));
        behavior.SetInput(state, "Variable brake", new NodeValue(0.25));
        behavior.SetInput(state, "Brake", NodeValue.FromBool(true));
        Check(Math.Abs(state.SteerInput - 0.5) < 1e-9 && Math.Abs(state.BrakeInput - 0.25) < 1e-9 && state.FullBrake, "the behavior reads Steering / Variable brake / Brake");
        var untouched = (WheelState)behavior.CreateState(MakeFunctional("{ \"behavior\": \"Wheel\" }"));
        behavior.SetInput(untouched, "steer", new NodeValue(1));
        behavior.SetInput(untouched, "brake", new NodeValue(1));
        behavior.SetInput(untouched, "full_brake", NodeValue.FromBool(true));
        Check(untouched.SteerInput == 0 && untouched.BrakeInput == 0 && !untouched.FullBrake, "the old ids (steer / brake / full_brake) are no longer read");
    }
}
