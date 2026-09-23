using System;
using System.Collections.Generic;
using Godot;

namespace SandboxPolyGame.Blocks;

/// <summary>
/// Данные одного типа блока, загруженные из XML-файла в папке <c>blocks/</c> (см. <see cref="BlockCatalog"/>).
/// <see cref="Slug"/> — стабильный строковый id блока (имя файла без расширения), используется для ссылок
/// на блок в сохранённых постройках (JSON). <see cref="RuntimeId"/> — числовой id для <see cref="Core.VoxelGrid"/>,
/// присваивается заново при каждой загрузке каталога и не должен сохраняться на диск.
/// </summary>
public sealed class BlockDefinition
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required Color DefaultColor { get; init; }

    /// <summary>Присваивается <see cref="BlockCatalog"/> при загрузке. 0 зарезервирован под «пустую клетку».</summary>
    public ushort RuntimeId { get; internal set; }

    private readonly IReadOnlyDictionary<Type, BlockComponent> _components;

    public BlockDefinition(IReadOnlyDictionary<Type, BlockComponent> components) => _components = components;

    public T? GetComponent<T>() where T : BlockComponent =>
        _components.TryGetValue(typeof(T), out var component) ? (T)component : null;

    public bool HasComponent<T>() where T : BlockComponent => _components.ContainsKey(typeof(T));
}
