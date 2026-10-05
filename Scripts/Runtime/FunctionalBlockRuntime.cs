using System;
using System.Collections.Generic;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Runtime;

/// <summary>
/// Рантайм-слой функциональных блоков ОДНОЙ постройки: для каждого экземпляра, у чьего блока есть зарегистрированное
/// поведение (<see cref="BlockBehaviorRegistry"/>), держит его <see cref="BlockState"/> — отдельно от
/// <see cref="BlockInstance"/> (см. <see cref="BlockState"/> doc), по <see cref="BlockInstance.InstanceId"/>.
/// Чистая логика без сцены (тестируется без движка-окна). Состояния создаются/удаляются автоматически вслед за
/// <see cref="Construction"/> (по <see cref="Construction.Changed"/>; <see cref="Sync"/> можно вызвать и вручную):
/// появился экземпляр — получил начальное состояние, исчез — состояние выброшено. Пока НЕ сохраняется на диск и не
/// реплицируется по сети — при каждом спавне постройки состояния начинаются заново (см. Docs/05, «Рантайм функциональных блоков»).
/// </summary>
public sealed class FunctionalBlockRuntime : IDisposable
{
    private readonly record struct Entry(IBlockBehavior Behavior, BlockState State);

    private readonly Construction _construction;
    private readonly BlockCatalog _catalog;
    private readonly Dictionary<int, Entry> _entries = new();

    public FunctionalBlockRuntime(Construction construction, BlockCatalog catalog)
    {
        _construction = construction;
        _catalog = catalog;
        _construction.Changed += Sync;
        Sync();
    }

    /// <summary>Сколько экземпляров сейчас имеют рантайм-состояние.</summary>
    public int Count => _entries.Count;

    public bool HasState(int instanceId) => _entries.ContainsKey(instanceId);

    /// <summary>Состояние экземпляра как <typeparamref name="TState"/>; null — у экземпляра нет состояния или оно другого типа.</summary>
    public TState? GetState<TState>(int instanceId) where TState : BlockState =>
        _entries.TryGetValue(instanceId, out var entry) ? entry.State as TState : null;

    /// <summary>Приводит набор состояний в соответствие с постройкой: создаёт недостающие, выбрасывает лишние
    /// (состояния уцелевших экземпляров не трогает).</summary>
    public void Sync()
    {
        var alive = new HashSet<int>();
        foreach (var instance in _construction.Instances)
        {
            if (!_catalog.TryGetBySlug(instance.BlockSlug, out var definition)) continue;
            var functional = definition.GetComponent<FunctionalBlockComponent>();
            if (functional == null || !BlockBehaviorRegistry.TryGet(functional.Behavior, out var behavior)) continue;

            alive.Add(instance.InstanceId);
            if (!_entries.ContainsKey(instance.InstanceId))
            {
                _entries[instance.InstanceId] = new Entry(behavior, behavior.CreateState(functional));
            }
        }

        foreach (int id in new List<int>(_entries.Keys))
        {
            if (!alive.Contains(id)) _entries.Remove(id);
        }
    }

    /// <summary>Передаёт действие поведению экземпляра; false — у экземпляра нет состояния (нет такого блока или у него нет поведения).</summary>
    public bool Interact(int instanceId, BlockInteraction interaction)
    {
        if (!_entries.TryGetValue(instanceId, out var entry)) return false;
        entry.Behavior.Interact(entry.State, interaction);
        return true;
    }

    /// <summary>
    /// ОТЛАДОЧНОЕ питание (F2 в мире, см. <c>World.GameWorld</c>): пока нод логики нет, это единственный источник
    /// питания — true запитывает ВСЕ блоки с поведением. Исчезнет вместе с заглушкой в <see cref="ResolvePowered"/>.
    /// </summary>
    public bool DebugForcePowered { get; set; }

    /// <summary>
    /// ЕДИНСТВЕННОЕ место, откуда рантайм берёт "запитан ли блок" (ЗАГЛУШКА). Когда появятся ноды логики и сеть
    /// электричества (см. Docs/05, «Логика и рантайм функциональных блоков»), здесь нужно спросить сеть: есть ли
    /// питание на входной ноде <see cref="NodeType.Electricity"/> экземпляра <paramref name="instanceId"/>.
    /// Остальной код (поведения, визуал) питание нигде больше не вычисляет.
    /// </summary>
    private bool ResolvePowered(int instanceId) => DebugForcePowered;

    /// <summary>Шаг симуляции всех блоков с состоянием: сначала обновляет питание, затем шагает поведение.</summary>
    public void Tick(double delta)
    {
        foreach (var (instanceId, entry) in _entries)
        {
            entry.Behavior.SetPowered(entry.State, ResolvePowered(instanceId));
            entry.Behavior.Tick(entry.State, delta);
        }
    }

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

    public void Dispose() => _construction.Changed -= Sync;
}
