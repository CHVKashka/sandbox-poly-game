using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Godot;

namespace SandboxPolyGame.Blocks;

/// <summary>
/// Реестр всех блоков игры (data-driven): каждый блок — отдельный XML-файл в <c>res://blocks/</c>
/// (см. формат в <see cref="ParseFile"/>). Внутри XML на блок навешиваются компоненты (<see cref="BlockComponent"/>)
/// с параметрами в JSON. Файлы читаются в алфавитном порядке имени и получают числовой <see cref="BlockDefinition.RuntimeId"/>
/// по порядку (1, 2, 3, ...) — детерминированно, но НЕ стабильно между наборами файлов (не хранить на диск,
/// см. <c>Core.ConstructionIO</c>, который ссылается на блоки по <see cref="BlockDefinition.Slug"/>).
/// </summary>
public sealed class BlockCatalog
{
    public const string BlocksDirectory = "res://blocks";

    private static BlockCatalog? _instance;

    /// <summary>Загружается один раз при первом обращении (лениво) и переиспользуется всю сессию игры.</summary>
    public static BlockCatalog Instance => _instance ??= Load(BlocksDirectory);

    /// <summary>
    /// Для самотестов: на время <c>using</c> подменяет <see cref="Instance"/> заданным каталогом (например, формами из настоящего
    /// <c>blocks/</c> плюс временные функциональные блоки-фикстуры), по выходу возвращает прежний. Остальной код читает блоки именно из
    /// <see cref="Instance"/> (по слагу и по <see cref="BlockDefinition.RuntimeId"/>), поэтому на подменённом каталоге работают
    /// настоящие <c>VehicleSpawner</c>/<c>BuildEditor</c>, а не их копии.
    /// </summary>
    public static IDisposable OverrideInstanceForTesting(BlockCatalog catalog)
    {
        var previous = _instance;
        _instance = catalog;
        return new Restore(() => _instance = previous);
    }

    private sealed class Restore : IDisposable
    {
        private Action? _action;

        public Restore(Action action) => _action = action;

        public void Dispose()
        {
            _action?.Invoke();
            _action = null;
        }
    }

    private readonly List<BlockDefinition> _all;
    private readonly Dictionary<string, BlockDefinition> _bySlug;
    private readonly Dictionary<ushort, BlockDefinition> _byRuntimeId;

    public IReadOnlyList<BlockDefinition> All => _all;

    private BlockCatalog(List<BlockDefinition> definitions)
    {
        _all = definitions;
        _bySlug = definitions.ToDictionary(d => d.Slug);
        _byRuntimeId = definitions.ToDictionary(d => d.RuntimeId);
    }

    public BlockDefinition Get(string slug) => _bySlug[slug];

    public bool TryGetBySlug(string slug, out BlockDefinition definition) =>
        _bySlug.TryGetValue(slug, out definition!);

    public BlockDefinition Get(ushort runtimeId) => _byRuntimeId[runtimeId];

    public bool TryGetByRuntimeId(ushort runtimeId, out BlockDefinition definition) =>
        _byRuntimeId.TryGetValue(runtimeId, out definition!);

    public static BlockCatalog Load(string directoryPath)
    {
        var definitions = new List<BlockDefinition>();

        using var dir = DirAccess.Open(directoryPath);
        if (dir == null)
        {
            GD.PrintErr($"[blocks] directory not found: {directoryPath}");
            return new BlockCatalog(definitions);
        }

        var files = new List<string>();
        dir.ListDirBegin();
        for (var name = dir.GetNext(); name != ""; name = dir.GetNext())
        {
            if (!dir.CurrentIsDir() && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) files.Add(name);
        }

        dir.ListDirEnd();
        files.Sort(StringComparer.Ordinal); // алфавитный порядок -> детерминированные RuntimeId между запусками

        ushort nextId = 1;
        foreach (var file in files)
        {
            string path = $"{directoryPath}/{file}";
            try
            {
                var definition = ParseFile(path);
                definition.RuntimeId = nextId++;
                definitions.Add(definition);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[blocks] failed to load '{path}': {ex.Message}");
            }
        }

        return new BlockCatalog(definitions);
    }

    /// <summary>
    /// Формат файла блока:
    /// <code>
    /// &lt;Block id="steel" name="Steel"&gt;
    ///   &lt;Color&gt;#808890&lt;/Color&gt;
    ///   &lt;Component type="BaseComponent"&gt;{ "mass": 40, "durability": 220, "damageResistance": 0.25 }&lt;/Component&gt;
    ///   &lt;Component type="BuildingBlock"&gt;{ "shape": "Cube", "minSize": [1,1,1], "maxSize": [8,8,8] }&lt;/Component&gt;
    /// &lt;/Block&gt;
    /// </code>
    /// <c>id</c> — стабильный слаг блока (обычно совпадает с именем файла, но это не проверяется), <c>name</c> —
    /// отображаемое имя (по умолчанию = id). Порядок и набор &lt;Component&gt; произвольны.
    /// </summary>
    private static BlockDefinition ParseFile(string path)
    {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (file == null) throw new InvalidOperationException($"cannot open file (error {FileAccess.GetOpenError()})");

        var root = XDocument.Parse(file.GetAsText()).Root
                   ?? throw new InvalidOperationException("empty XML document");
        if (root.Name.LocalName != "Block")
        {
            throw new InvalidOperationException($"root element must be <Block>, got <{root.Name.LocalName}>");
        }

        string slug = (string?)root.Attribute("id")
                      ?? throw new InvalidOperationException("<Block> is missing the 'id' attribute");
        string name = (string?)root.Attribute("name") ?? slug;
        string colorHex = root.Element("Color")?.Value.Trim() ?? "#ffffff";
        var color = Color.FromHtml(colorHex);

        var components = new Dictionary<Type, BlockComponent>();
        foreach (var node in root.Elements("Component"))
        {
            string? type = (string?)node.Attribute("type");
            if (string.IsNullOrEmpty(type)) throw new InvalidOperationException("<Component> is missing the 'type' attribute");

            var component = CreateComponent(type);
            string json = node.Value.Trim();
            if (json.Length > 0)
            {
                using var doc = JsonDocument.Parse(json);
                component.LoadFromJson(doc.RootElement);
            }

            components[component.GetType()] = component;
        }

        return new BlockDefinition(components) { Slug = slug, Name = name, DefaultColor = color };
    }

    private static BlockComponent CreateComponent(string type) => type switch
    {
        BaseComponent.ComponentType => new BaseComponent(),
        BuildingBlockComponent.ComponentType => new BuildingBlockComponent(),
        FunctionalBlockComponent.ComponentType => new FunctionalBlockComponent(),
        ParametersComponent.ComponentType => new ParametersComponent(),
        _ => throw new InvalidOperationException($"unknown component type '{type}'"),
    };
}
