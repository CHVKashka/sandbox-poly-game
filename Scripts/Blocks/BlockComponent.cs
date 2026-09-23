using System.Text.Json;

namespace SandboxPolyGame.Blocks;

/// <summary>
/// Базовый класс компонента блока. Компонент описывает часть функционала блока (данные и/или поведение);
/// какие компоненты навешены на блок и с какими параметрами — задаётся в XML-описании блока
/// (см. <see cref="BlockCatalog"/>): каждый компонент указан элементом <c>&lt;Component type="..."&gt;</c>,
/// внутри которого лежит JSON-объект с параметрами конкретно этого компонента.
/// </summary>
public abstract class BlockComponent
{
    /// <summary>Заполняет поля компонента данными из JSON-объекта, взятого из тела &lt;Component&gt; в XML.</summary>
    public abstract void LoadFromJson(JsonElement json);
}
