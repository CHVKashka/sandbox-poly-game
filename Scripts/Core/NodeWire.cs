namespace SandboxPolyGame.Core;

/// <summary>
/// Провод между двумя логическими нодами (<see cref="Blocks.LogicNode"/>) двух блоков постройки: значение идёт от ВЫХОДНОЙ ноды
/// (<see cref="FromNode"/> блока <see cref="FromInstance"/>) к ВХОДНОЙ (<see cref="ToNode"/> блока <see cref="ToInstance"/>) — независимо от того,
/// с какого конца игрок тянул провод инструментом «Nodes». Блоки названы по <see cref="BlockInstance.InstanceId"/>; в файле постройки
/// провод хранится по индексу блока в списке (идентификаторы не переживают загрузку, см. <see cref="ConstructionIO"/>).
/// </summary>
public readonly record struct NodeWire(int FromInstance, string FromNode, int ToInstance, string ToNode);
