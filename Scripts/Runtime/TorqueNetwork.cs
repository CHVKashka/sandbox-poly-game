using System;
using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor;

namespace SandboxPolyGame.Runtime;

/// <summary>
/// Сети вращения (валы): блоки, чьи физические порты <see cref="ResourceType.Torque"/> стоят ВПРИТЫК друг к другу, образуют одну сеть — мотор, вал, угловой
/// вал, крест. Соединение автоматическое, без проводов: порт блока A на грани клетки <c>c</c> с нормалью <c>d</c> соединяется с портом блока B того же
/// ресурса на соседней клетке <c>c + d</c> с нормалью <c>-d</c> (порты вал-на-грани — см. <see cref="ResourcePort"/>). Положение порта считается так же,
/// как в редакторе блоков (<see cref="FunctionalBlockGeometry.FaceAxes"/>, ячейка грани зажимается в footprint), и поворачивается вместе с блоком вокруг
/// корневой клетки (<see cref="BlockFootprint"/>). Направление порта (In/Out) не важно — у вала оба на одном месте, а вращение двусторонне.
/// <para/>
/// Сеть строится чистой логикой по <see cref="Construction"/> и пересобирается рантаймом при любом изменении постройки; обороты сети —
/// у самого быстрого источника (<see cref="IBlockBehavior.GetTorqueRpm"/>, знак сохраняется), см. <see cref="FunctionalBlockRuntime.GetNetworkRpm"/>.
/// </summary>
public sealed class TorqueNetwork
{
    private readonly Dictionary<int, int> _networkOf = new();
    private readonly List<List<int>> _members = new();

    /// <summary>Пустая сеть (нет ни одного соединённого порта) — начальное значение, пока рантайм не построил настоящую.</summary>
    public static readonly TorqueNetwork Empty = new();

    public int NetworkCount => _members.Count;

    /// <summary>Номер сети экземпляра; -1 — у экземпляра нет ни одного соединённого порта вала.</summary>
    public int NetworkOf(int instanceId) => _networkOf.TryGetValue(instanceId, out int network) ? network : -1;

    public IReadOnlyList<int> Members(int network) => _members[network];

    private readonly record struct PortKey(Vector3I Cell, Vector3I Direction, ResourceType Resource);

    /// <summary>Мировое положение порта: клетка снаружи которой он смотрит и направление грани (оба — в клетках постройки).</summary>
    public static (Vector3I Cell, Vector3I Direction) WorldPort(BlockInstance instance, FunctionalBlockComponent block, ResourcePort port)
    {
        var (u, v) = FunctionalBlockGeometry.FaceAxes(port.Face);
        int normalAxis = 3 - u - v;
        bool positive = ((int)port.Face & 1) == 1;

        var local = Vector3I.Zero;
        local[u] = Math.Clamp(port.FaceCell.X, block.FootprintMin[u], block.FootprintMin[u] + Math.Max(block.Footprint[u] - 1, 0));
        local[v] = Math.Clamp(port.FaceCell.Y, block.FootprintMin[v], block.FootprintMin[v] + Math.Max(block.Footprint[v] - 1, 0));
        local[normalAxis] = positive ? block.FootprintMin[normalAxis] + block.Footprint[normalAxis] - 1 : block.FootprintMin[normalAxis];

        var normal = Vector3I.Zero;
        normal[normalAxis] = positive ? 1 : -1;

        var root = BlockFootprint.RootCell(instance.Origin, block.FootprintMin, block.Footprint, instance.RotationSteps);
        return (root + BlockFootprint.Rotate(local, instance.RotationSteps), BlockFootprint.Rotate(normal, instance.RotationSteps));
    }

    public static TorqueNetwork Build(Construction construction, BlockCatalog catalog)
    {
        var network = new TorqueNetwork();
        var byKey = new Dictionary<PortKey, List<int>>();
        var ports = new List<(int Instance, PortKey Key)>();

        foreach (var instance in construction.Instances)
        {
            if (!catalog.TryGetBySlug(instance.BlockSlug, out var definition)) continue;
            var block = definition.GetComponent<FunctionalBlockComponent>();
            if (block == null) continue;

            foreach (var port in block.Ports)
            {
                if (port.Resource != ResourceType.Torque) continue;
                var (cell, direction) = WorldPort(instance, block, port);
                var key = new PortKey(cell, direction, port.Resource);
                ports.Add((instance.InstanceId, key));
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<int>();
                if (!list.Contains(instance.InstanceId)) list.Add(instance.InstanceId);
            }
        }

        // Объединение (union-find) по соседним портам.
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            if (!parent.TryGetValue(x, out int p)) { parent[x] = x; return x; }
            return p == x ? x : parent[x] = Find(p);
        }

        void Union(int a, int b) => parent[Find(a)] = Find(b);

        foreach (var (instanceId, key) in ports)
        {
            var neighbor = new PortKey(key.Cell + key.Direction, -key.Direction, key.Resource);
            if (!byKey.TryGetValue(neighbor, out var others)) continue;
            foreach (int other in others)
            {
                if (other != instanceId) Union(instanceId, other);
            }
        }

        // Сеть — только если в ней больше одного блока (одинокий блок с портами ни к чему не подключён).
        var groups = new Dictionary<int, List<int>>();
        foreach (int instanceId in new List<int>(parent.Keys))
        {
            int root = Find(instanceId);
            if (!groups.TryGetValue(root, out var group)) groups[root] = group = new List<int>();
            group.Add(instanceId);
        }

        foreach (var group in groups.Values)
        {
            if (group.Count < 2) continue;
            group.Sort();
            int index = network._members.Count;
            network._members.Add(group);
            foreach (int member in group) network._networkOf[member] = index;
        }

        return network;
    }
}
