namespace SandboxPolyGame.Blocks;

/// <summary>Направление порта функционального блока — потребляет ресурс или отдаёт его.</summary>
public enum PortDirection
{
    In,
    Out,
}

/// <summary>
/// Один вход/выход функционального блока (см. <see cref="FunctionalBlockComponent.Ports"/>): какой ресурс, в какую
/// сторону. <see cref="Id"/> — стабильный идентификатор порта внутри блока (на будущее — для адресации соединений,
/// см. Docs/05-world-and-vehicle-systems.md, «Соединения»), сейчас используется только для отображения.
/// </summary>
public sealed class ResourcePort
{
    public required string Id { get; init; }
    public required ResourceType Resource { get; init; }
    public required PortDirection Direction { get; init; }
}
