using System;
using System.Collections.Generic;
using System.Linq;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Runtime;

/// <summary>
/// Рантайм-слой функциональных блоков ОДНОЙ постройки: для каждого экземпляра, у чьего блока есть зарегистрированное поведение
/// (<see cref="BlockBehaviorRegistry"/>), держит его <see cref="BlockState"/> — отдельно от <see cref="BlockInstance"/> (см. <see cref="BlockState"/> doc),
/// по <see cref="BlockInstance.InstanceId"/> — и гоняет по проводам постройки сигналы, электричество и вращение. Чистая логика без сцены (тестируется без
/// окна). Состояния создаются/удаляются автоматически вслед за <see cref="Construction"/> (по <see cref="Construction.Changed"/>; <see cref="Sync"/> можно
/// вызвать и вручную): появился экземпляр — получил начальное состояние (с его настройками <see cref="BlockInstance.Parameters"/>), исчез — состояние
/// выброшено. Не сохраняется на диск — при каждом спавне постройки состояния начинаются заново. В сетевой игре считает только сервер, у клиентов — зеркало (<see cref="IsMirror"/>), которому сервер присылает снимки (<see cref="CaptureNet"/>/<see cref="ApplyNet"/>).
/// <para/>
/// <b>Шаг (<see cref="Tick"/>)</b>:
/// <list type="number">
/// <item><b>Сигналы.</b> Для каждой входной Boolean/Number ноды берётся значение выхода на другом конце провода (<see cref="Construction.Wires"/>), нет провода — 0
/// (<see cref="IBlockBehavior.SetInput"/>). Значения выходов — с прошлого тика, так что петли в схеме безопасны (задержка один тик).</item>
/// <item><b>Электричество.</b> Ноды Electricity, соединённые проводами, образуют сеть. Потребители (<see cref="IBlockBehavior.GetPowerDemand"/>) делят энергию
/// источников сети (<see cref="IBlockBehavior.GetStoredEnergy"/> — аккумуляторы): если энергии не хватает на всех, каждому достаётся одна и та же ДОЛЯ запроса
/// (<see cref="PowerReport.Ratio"/>); источники отдают пропорционально своему запасу (<see cref="IBlockBehavior.DrawEnergy"/>). Ничего не подключено или источники
/// пусты — потребитель не запитан.</item>
/// <item><b>Поведения</b> шагают (<see cref="IBlockBehavior.Tick"/>): мотор набирает обороты, ось сиденья движется.</item>
/// <item><b>Вращение.</b> Блоки, чьи порты вала стоят впритык, образуют сеть (<see cref="TorqueNetwork"/>); её обороты — у самого быстрого источника
/// (<see cref="IBlockBehavior.GetTorqueRpm"/>), их читает визуал валов (<see cref="GetNetworkRpm"/>).</item>
/// </list>
/// </summary>
public sealed class FunctionalBlockRuntime : IDisposable
{
    private sealed record Entry(int InstanceId, FunctionalBlockComponent Block, IBlockBehavior Behavior, BlockState State, string ParametersKey);

    private readonly record struct NodeKey(int Instance, string Node);

    private sealed class ElectricNet
    {
        public readonly List<NodeKey> Consumers = new();
        public readonly List<NodeKey> Sources = new();
    }

    private readonly Construction _construction;
    private readonly BlockCatalog _catalog;
    private readonly Dictionary<int, Entry> _entries = new();

    // Кэш топологии, пересобирается лениво после любого изменения постройки (провода/блоки).
    private bool _topologyDirty = true;
    private readonly Dictionary<NodeKey, NodeKey> _signalSource = new();
    private readonly List<ElectricNet> _nets = new();
    private TorqueNetwork _torque = TorqueNetwork.Empty;
    private double[] _networkRpm = Array.Empty<double>();

    public FunctionalBlockRuntime(Construction construction, BlockCatalog catalog)
    {
        _construction = construction;
        _catalog = catalog;
        _construction.Changed += Sync;
        Sync();
    }

    /// <summary>
    /// Рантайм-наблюдатель (клиент в сетевой игре): сам НЕ считает — состояния приходят от сервера (<see cref="ApplyNet"/>), <see cref="Tick"/> ничего не делает.
    /// Сервер и одиночная игра — обычный рантайм (false).
    /// </summary>
    public bool IsMirror { get; set; }

    /// <summary>Снимок изменяемого состояния всех блоков для сети: id экземпляров, длины их наборов и плоский массив значений (<see cref="BlockState.CaptureNet"/>).</summary>
    public (int[] Ids, int[] Lengths, double[] Values) CaptureNet()
    {
        var ids = new List<int>();
        var lengths = new List<int>();
        var values = new List<double>();
        foreach (var entry in _entries.Values)
        {
            var captured = entry.State.CaptureNet();
            if (captured.Length == 0) continue;
            ids.Add(entry.InstanceId);
            lengths.Add(captured.Length);
            values.AddRange(captured);
        }

        return (ids.ToArray(), lengths.ToArray(), values.ToArray());
    }

    /// <summary>Применяет снимок сервера (<see cref="CaptureNet"/>); экземпляры, которых здесь нет, пропускаются.</summary>
    public void ApplyNet(int[] ids, int[] lengths, double[] values)
    {
        int offset = 0;
        for (int i = 0; i < ids.Length && i < lengths.Length; i++)
        {
            int length = lengths[i];
            if (offset + length > values.Length) break;
            if (_entries.TryGetValue(ids[i], out var entry))
            {
                var slice = new double[length];
                Array.Copy(values, offset, slice, 0, length);
                entry.State.ApplyNet(slice);
            }

            offset += length;
        }
    }

    /// <summary>Сколько экземпляров сейчас имеют рантайм-состояние.</summary>
    public int Count => _entries.Count;

    public bool HasState(int instanceId) => _entries.ContainsKey(instanceId);

    /// <summary>Состояние экземпляра как <typeparamref name="TState"/>; null — у экземпляра нет состояния или оно другого типа.</summary>
    public TState? GetState<TState>(int instanceId) where TState : BlockState =>
        _entries.TryGetValue(instanceId, out var entry) ? entry.State as TState : null;

    /// <summary>Приводит набор состояний в соответствие с постройкой: создаёт недостающие, выбрасывает лишние (состояния уцелевших экземпляров не трогает).</summary>
    public void Sync()
    {
        _topologyDirty = true;
        var alive = new HashSet<int>();
        foreach (var instance in _construction.Instances)
        {
            if (!_catalog.TryGetBySlug(instance.BlockSlug, out var definition)) continue;
            var functional = definition.GetComponent<FunctionalBlockComponent>();
            if (functional == null || !BlockBehaviorRegistry.TryGet(functional.Behavior, out var behavior)) continue;

            alive.Add(instance.InstanceId);

            // Состояние создаётся один раз; пересоздаётся (с нуля) только если у экземпляра ПОМЕНЯЛИ настройки - иначе мотор/ось продолжали бы жить со старыми.
            string parametersKey = ParametersKeyOf(instance);
            if (!_entries.TryGetValue(instance.InstanceId, out var existing) || existing.ParametersKey != parametersKey)
            {
                var parameters = new ParameterSet(Construction.ParametersOf(definition), instance.Parameters);
                _entries[instance.InstanceId] = new Entry(instance.InstanceId, functional, behavior, behavior.CreateState(functional, parameters), parametersKey);
            }
        }

        foreach (int id in new List<int>(_entries.Keys))
        {
            if (!alive.Contains(id)) _entries.Remove(id);
        }
    }

    private static string ParametersKeyOf(BlockInstance instance) =>
        instance.Parameters is { Count: > 0 } ? string.Join(";", instance.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) : "";

    /// <summary>Передаёт действие поведению экземпляра; false — у экземпляра нет состояния (нет такого блока или у него нет поведения).</summary>
    public bool Interact(int instanceId, BlockInteraction interaction)
    {
        if (!_entries.TryGetValue(instanceId, out var entry)) return false;
        entry.Behavior.Interact(entry.State, interaction);
        return true;
    }

    /// <summary>
    /// ОТЛАДОЧНОЕ питание (F2 в мире, см. <c>World.GameWorld</c>): true запитывает ВСЕ потребители на полную мощность, не расходуя заряд — чтобы проверять
    /// кнопки/моторы без собранной схемы с аккумулятором.
    /// </summary>
    public bool DebugForcePowered { get; set; }

    /// <summary>Передаёт действие ВСЕМ экземплярам с поведением <typeparamref name="TBehavior"/> (отладка: "нажать все кнопки").</summary>
    public void InteractAll<TBehavior>(BlockInteraction interaction) where TBehavior : IBlockBehavior
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Behavior is TBehavior) entry.Behavior.Interact(entry.State, interaction);
        }
    }

    /// <summary>Значение выходной ноды <paramref name="nodeId"/> экземпляра; false — нет экземпляра/состояния/такого порта.</summary>
    public bool TryReadNode(int instanceId, string nodeId, out NodeValue value)
    {
        if (_entries.TryGetValue(instanceId, out var entry)) return entry.Behavior.TryReadNode(entry.State, nodeId, out value);
        value = NodeValue.Off;
        return false;
    }

    /// <summary>Обороты сети вращения, в которой стоит экземпляр (об/мин, со знаком); 0 — экземпляр не в сети или сеть стоит.</summary>
    public double GetNetworkRpm(int instanceId)
    {
        RebuildTopologyIfNeeded();
        int network = _torque.NetworkOf(instanceId);
        return network >= 0 && network < _networkRpm.Length ? _networkRpm[network] : 0;
    }

    // ------------------------------------------------------------------ топология

    private void RebuildTopologyIfNeeded()
    {
        if (!_topologyDirty) return;
        _topologyDirty = false;

        _signalSource.Clear();
        _nets.Clear();

        var electricParent = new Dictionary<NodeKey, NodeKey>();
        NodeKey Find(NodeKey x)
        {
            if (!electricParent.TryGetValue(x, out var p)) { electricParent[x] = x; return x; }
            if (p.Equals(x)) return x;
            return electricParent[x] = Find(p);
        }

        foreach (var wire in _construction.Wires)
        {
            var from = new NodeKey(wire.FromInstance, wire.FromNode);
            var to = new NodeKey(wire.ToInstance, wire.ToNode);
            var node = _construction.FindNode(wire.ToInstance, wire.ToNode, _catalog);
            if (node == null) continue;

            if (node.Type == NodeType.Electricity) electricParent[Find(from)] = Find(to);
            else _signalSource[to] = from;
        }

        // Группировка электрических нод по сетям: выход — источник, вход — потребитель (только у блоков с поведением, у остальных нечему ни потреблять, ни отдавать).
        var byRoot = new Dictionary<NodeKey, ElectricNet>();
        foreach (var key in new List<NodeKey>(electricParent.Keys))
        {
            if (!_entries.ContainsKey(key.Instance)) continue;
            var node = _construction.FindNode(key.Instance, key.Node, _catalog);
            if (node == null) continue;

            var root = Find(key);
            if (!byRoot.TryGetValue(root, out var net)) { byRoot[root] = net = new ElectricNet(); _nets.Add(net); }
            (node.Direction == PortDirection.Out ? net.Sources : net.Consumers).Add(key);
        }

        _torque = TorqueNetwork.Build(_construction, _catalog);
        if (_networkRpm.Length != _torque.NetworkCount) _networkRpm = new double[_torque.NetworkCount];
    }

    // ------------------------------------------------------------------ шаг

    /// <summary>Шаг симуляции всех блоков с состоянием (порядок — в class doc).</summary>
    public void Tick(double delta)
    {
        if (IsMirror) return;
        RebuildTopologyIfNeeded();
        PushSignals();
        SolveElectricity(delta);

        foreach (var entry in _entries.Values) entry.Behavior.Tick(entry.State, delta);

        UpdateTorque();
    }

    private void PushSignals()
    {
        foreach (var entry in _entries.Values)
        {
            foreach (var node in entry.Block.Nodes)
            {
                if (node.Direction != PortDirection.In || node.Type == NodeType.Electricity) continue;

                var value = NodeValue.Off;
                if (_signalSource.TryGetValue(new NodeKey(entry.InstanceId, node.Id), out var source) && _entries.TryGetValue(source.Instance, out var sourceEntry))
                {
                    sourceEntry.Behavior.TryReadNode(sourceEntry.State, source.Node, out value);
                }

                entry.Behavior.SetInput(entry.State, node.Id, value);
            }
        }
    }

    private void SolveElectricity(double delta)
    {
        var energizedInstances = new HashSet<int>();
        var reported = new HashSet<NodeKey>();

        foreach (var net in _nets)
        {
            double demandEnergy = 0;
            var demands = new List<(NodeKey Key, double Power)>(net.Consumers.Count);
            foreach (var key in net.Consumers)
            {
                var entry = _entries[key.Instance];
                double power = Math.Max(0, entry.Behavior.GetPowerDemand(entry.State, key.Node));
                demands.Add((key, power));
                demandEnergy += power * delta;
            }

            // Источник учитывается один раз на блок (у аккумулятора может быть несколько выходов электричества в одной сети — заряд общий).
            var sources = new List<(NodeKey Key, double Stored)>();
            var seen = new HashSet<int>();
            foreach (var key in net.Sources)
            {
                if (!seen.Add(key.Instance)) continue;
                var entry = _entries[key.Instance];
                sources.Add((key, Math.Max(0, entry.Behavior.GetStoredEnergy(entry.State, key.Node))));
            }

            double stored = sources.Sum(s => s.Stored);
            bool energized = DebugForcePowered || stored > 1e-9;
            double provided = DebugForcePowered ? demandEnergy : Math.Min(demandEnergy, stored);
            double ratio = demandEnergy > 1e-12 ? provided / demandEnergy : 1.0;

            foreach (var (key, power) in demands)
            {
                var entry = _entries[key.Instance];
                entry.Behavior.SetPower(entry.State, key.Node, new PowerReport(energized, energized ? ratio : 0));
                if (!DebugForcePowered && power > 0) entry.Behavior.ReceiveEnergy(entry.State, key.Node, power * delta * ratio);
                reported.Add(key);
                if (energized) energizedInstances.Add(key.Instance);
            }

            if (!DebugForcePowered && provided > 0 && stored > 0)
            {
                foreach (var (key, amount) in sources)
                {
                    var entry = _entries[key.Instance];
                    entry.Behavior.DrawEnergy(entry.State, key.Node, provided * amount / stored);
                }
            }
        }

        // Входы электричества без единого провода: питания нет (кроме отладочного F2).
        foreach (var entry in _entries.Values)
        {
            foreach (var node in entry.Block.Nodes)
            {
                if (node.Direction != PortDirection.In || node.Type != NodeType.Electricity) continue;
                var key = new NodeKey(entry.InstanceId, node.Id);
                if (reported.Contains(key)) continue;

                entry.Behavior.SetPower(entry.State, node.Id, DebugForcePowered ? new PowerReport(true, 1) : PowerReport.None);
                if (DebugForcePowered) energizedInstances.Add(entry.InstanceId);
            }

            entry.Behavior.SetPowered(entry.State, DebugForcePowered || energizedInstances.Contains(entry.InstanceId));
        }
    }

    private void UpdateTorque()
    {
        for (int network = 0; network < _networkRpm.Length; network++)
        {
            double best = 0;
            foreach (int member in _torque.Members(network))
            {
                if (!_entries.TryGetValue(member, out var entry)) continue;
                double rpm = entry.Behavior.GetTorqueRpm(entry.State);
                if (Math.Abs(rpm) > Math.Abs(best)) best = rpm;
            }

            _networkRpm[network] = best;
        }
    }

    public void Dispose() => _construction.Changed -= Sync;
}
