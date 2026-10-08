using System;
using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Runtime;

/// <summary>Режим оси сиденья (параметр <c>*_mode</c>): <see cref="Reset"/> — ось возвращается в 0, когда клавишу отпустили; <see cref="Sticky"/> — ось остаётся там, где её оставили.</summary>
public enum SeatAxisMode
{
    Reset,
    Sticky,
}

/// <summary>Сырой ввод пилота за один кадр — то, что игровой мир (<c>World.GameWorld</c>) читает с клавиатуры/мыши и передаёт сиденью (<see cref="PilotSeatState.SetInput"/>).</summary>
/// <param name="Hotkeys">Клавиши 1–6 нажаты (шесть значений).</param>
/// <param name="Trigger1">Левая кнопка мыши.</param>
/// <param name="Trigger2">Правая кнопка мыши.</param>
/// <param name="Axes">Направление осей, каждая -1/0/1: [A/D, W/S, Left/Right, Up/Down] — D, W, стрелки вправо и вверх дают +1.</param>
public readonly record struct SeatInput(bool[] Hotkeys, bool Trigger1, bool Trigger2, double[] Axes)
{
    public static SeatInput None => new(new bool[PilotSeatBehavior.HotkeyCount], false, false, new double[PilotSeatBehavior.AxisCount]);
}

/// <summary>Состояние одного пилотского сиденья (<see cref="PilotSeatBehavior"/>).</summary>
public sealed class PilotSeatState : BlockState
{
    /// <summary>Сидит ли сейчас игрок — выставляет игровой мир при посадке/вставании (<see cref="SetOccupied"/>).</summary>
    public bool Occupied { get; private set; }

    public bool[] Hotkeys { get; } = new bool[PilotSeatBehavior.HotkeyCount];
    public bool Trigger1 { get; private set; }
    public bool Trigger2 { get; private set; }

    /// <summary>Сырое направление осей (-1/0/1), см. <see cref="SeatInput.Axes"/>.</summary>
    public double[] AxisInput { get; } = new double[PilotSeatBehavior.AxisCount];

    /// <summary>Текущее значение каждой оси, -1..1 — то, что уходит на выходные ноды.</summary>
    public double[] AxisValue { get; } = new double[PilotSeatBehavior.AxisCount];

    public SeatAxisMode[] Modes { get; init; } = new SeatAxisMode[PilotSeatBehavior.AxisCount];

    /// <summary>Скорость изменения оси, полных диапазонов в секунду (1 — от 0 до 1 за секунду).</summary>
    public double[] Sensitivity { get; init; } = new double[PilotSeatBehavior.AxisCount];

    // Порядок: занято, оси (4), горячие клавиши (6), триггеры (2).
    public override double[] CaptureNet()
    {
        var values = new List<double> { Occupied ? 1 : 0 };
        values.AddRange(AxisValue);
        foreach (bool hotkey in Hotkeys) values.Add(hotkey ? 1 : 0);
        values.Add(Trigger1 ? 1 : 0);
        values.Add(Trigger2 ? 1 : 0);
        return values.ToArray();
    }

    public override void ApplyNet(double[] values)
    {
        Occupied = At(values, 0) != 0;
        for (int i = 0; i < AxisValue.Length; i++) AxisValue[i] = At(values, 1 + i);
        for (int i = 0; i < Hotkeys.Length; i++) Hotkeys[i] = At(values, 1 + AxisValue.Length + i) != 0;
        Trigger1 = At(values, 1 + AxisValue.Length + Hotkeys.Length) != 0;
        Trigger2 = At(values, 2 + AxisValue.Length + Hotkeys.Length) != 0;
    }

    /// <summary>Игрок сел/встал. При вставании ввод обнуляется (кнопки не «залипают»), оси режима Reset сами вернутся в 0, оси Sticky остаются как были.</summary>
    public void SetOccupied(bool occupied)
    {
        Occupied = occupied;
        if (!occupied) SetInput(SeatInput.None);
    }

    /// <summary>Передаёт ввод кадра; пока никто не сидит — игнорируется (обнуляется).</summary>
    public void SetInput(SeatInput input)
    {
        for (int i = 0; i < Hotkeys.Length; i++) Hotkeys[i] = Occupied && input.Hotkeys.Length > i && input.Hotkeys[i];
        Trigger1 = Occupied && input.Trigger1;
        Trigger2 = Occupied && input.Trigger2;
        for (int i = 0; i < AxisInput.Length; i++) AxisInput[i] = Occupied && input.Axes.Length > i ? Math.Clamp(input.Axes[i], -1.0, 1.0) : 0;
    }
}

/// <summary>
/// Поведение <c>"PilotSeat"</c> (<c>blocks/pilot_seat.xml</c>): на сиденье можно сесть (E) и встать (Shift), пока игрок сидит, оно перехватывает ввод и
/// отдаёт его на выходные ноды: клавиши 1–6 — Boolean <c>hotkey_1</c>…<c>hotkey_6</c>; ЛКМ/ПКМ — <c>triger_1</c>/<c>triger_2</c>; <c>occuped</c> — «на сиденье
/// сидит игрок»; четыре оси -1..1 — Number <c>ad_out</c> (A/D), <c>ws_out</c> (W/S), <c>lf_out</c> (стрелки влево/вправо), <c>ud_out</c> (стрелки вверх/вниз).
/// Ноды находятся ПО ИМЕНИ (имена — часть контракта блока; блок без какой-то из них просто не отдаёт этот сигнал).
/// <para/>
/// Настройка осей — инструмент «Parameters» (параметры <c>ad_mode</c>/<c>ad_sensitivity</c>, то же для <c>ws</c>, <c>lr</c>, <c>ud</c>): режим <see cref="SeatAxisMode.Reset"/>
/// (после отпускания клавиши ось плавно возвращается в 0) или <see cref="SeatAxisMode.Sticky"/> (ось остаётся, как «ручка газа»), и чувствительность —
/// насколько быстро ось движется (полных диапазонов в секунду). Само «сидение» (камера, поза игрока, чтение клавиш) — на стороне мира, см. <c>World.GameWorld</c>.
/// </summary>
public sealed class PilotSeatBehavior : BlockBehavior<PilotSeatState>
{
    public const string Key = "PilotSeat";
    public const int HotkeyCount = 6;
    public const int AxisCount = 4;
    public const double DefaultSensitivity = Blocks.ParameterPresets.DefaultAxisSensitivity;

    /// <summary>Имена осей в порядке индексов <see cref="PilotSeatState.AxisValue"/>: параметры <c>{имя}_mode</c>/<c>{имя}_sensitivity</c>.</summary>
    public static readonly string[] AxisNames = { "ad", "ws", "lr", "ud" };

    /// <summary>Id выходных нод осей в порядке индексов (A/D, W/S, лево/право, верх/низ).</summary>
    public static readonly string[] AxisNodeIds = { "ad_out", "ws_out", "lf_out", "ud_out" };

    public static string HotkeyNodeId(int index) => $"hotkey_{index + 1}";
    public const string OccupiedNodeId = "occuped";
    public const string Trigger1NodeId = "triger_1";
    public const string Trigger2NodeId = "triger_2";

    protected override PilotSeatState CreateState(FunctionalBlockComponent definition, ParameterSet parameters)
    {
        var modes = new SeatAxisMode[AxisCount];
        var sensitivity = new double[AxisCount];
        for (int i = 0; i < AxisCount; i++)
        {
            modes[i] = parameters.GetString($"{AxisNames[i]}_mode", "reset") == "sticky" ? SeatAxisMode.Sticky : SeatAxisMode.Reset;
            sensitivity[i] = Math.Max(0, parameters.GetFloat($"{AxisNames[i]}_sensitivity", DefaultSensitivity));
        }

        return new PilotSeatState { Modes = modes, Sensitivity = sensitivity };
    }

    protected override void Tick(PilotSeatState state, double delta)
    {
        for (int i = 0; i < AxisCount; i++)
        {
            double step = state.Sensitivity[i] * delta;
            double value = state.AxisValue[i];
            if (state.Modes[i] == SeatAxisMode.Sticky)
            {
                value += state.AxisInput[i] * step;
            }
            else
            {
                double target = state.AxisInput[i];
                value += Math.Clamp(target - value, -step, step);
            }

            state.AxisValue[i] = Math.Clamp(value, -1.0, 1.0);
        }
    }

    protected override bool TryReadNode(PilotSeatState state, string nodeId, out NodeValue value)
    {
        for (int i = 0; i < AxisCount; i++)
        {
            if (nodeId != AxisNodeIds[i]) continue;
            value = new NodeValue(state.AxisValue[i]);
            return true;
        }

        for (int i = 0; i < HotkeyCount; i++)
        {
            if (nodeId != HotkeyNodeId(i)) continue;
            value = NodeValue.FromBool(state.Hotkeys[i]);
            return true;
        }

        switch (nodeId)
        {
            case OccupiedNodeId: value = NodeValue.FromBool(state.Occupied); return true;
            case Trigger1NodeId: value = NodeValue.FromBool(state.Trigger1); return true;
            case Trigger2NodeId: value = NodeValue.FromBool(state.Trigger2); return true;
        }

        value = NodeValue.Off;
        return false;
    }

    /// <summary>Параметры типа блока для мира: высота глаз над центром клетки ноды <c>occuped</c> (<c>"eyeHeight"</c>, м, по умолчанию 0.75) и в какую сторону
    /// смотрит сиденье (<c>"forward"</c> — имя <see cref="BlockFace"/>, по умолчанию <c>PosX</c>).</summary>
    public static (double EyeHeight, BlockFace Forward) ReadSeatSettings(FunctionalBlockComponent definition)
    {
        double eyeHeight = double.TryParse(definition.GetParam("eyeHeight", "0.75"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h) ? h : 0.75;
        var forward = Enum.TryParse<BlockFace>(definition.GetParam("forward", "PosX"), ignoreCase: true, out var f) ? f : BlockFace.PosX;
        return (eyeHeight, forward);
    }
}
