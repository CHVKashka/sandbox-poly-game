using Godot;
using SandboxPolyGame.Blocks;

namespace SandboxPolyGame.Core;

/// <summary>
/// Сырые данные форм не-кубических блоков: вершины в единичном пространстве (0..1 по каждой оси, ориджин вершин —
/// в нулевых координатах угла) и грани (кольцо индексов вершин по контуру + желаемая внешняя нормаль). Порядок
/// вершин в кольце можно задавать в любом обходе — <see cref="ShapeMeshBuilder"/> сам чинит его под нужную нормаль.
/// Куб (<see cref="BlockShape.Cube"/>) сюда не входит — он по-прежнему рисуется <see cref="ChunkMesher"/>.
/// </summary>
public static class BlockGeometry
{
    /// <summary>
    /// Скос: (0,0,0),(1,0,0),(0,1,0),(1,1,0),(0,0,1),(1,0,1) — полная высота у грани z=0, нулевая у z=1
    /// (треугольная призма-рампа, поднимается от z=1 к z=0).
    /// </summary>
    private static readonly Vector3[] WedgeVertices =
    {
        new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0), new(0, 0, 1), new(1, 0, 1),
    };

    private static readonly (int[] Ring, Vector3 Normal)[] WedgeFaces =
    {
        (new[] { 0, 1, 5, 4 }, new Vector3(0, -1, 0)),           // низ (y=0)
        (new[] { 0, 1, 3, 2 }, new Vector3(0, 0, -1)),            // задняя стенка (z=0), полная высота
        (new[] { 2, 3, 5, 4 }, new Vector3(0, 1, 1).Normalized()), // рампа (плоскость y+z=1)
        (new[] { 0, 2, 4 }, new Vector3(-1, 0, 0)),               // левый треугольный борт (x=0)
        (new[] { 1, 3, 5 }, new Vector3(1, 0, 0)),                // правый треугольный борт (x=1)
    };

    /// <summary>
    /// Пирамида: (0,0,0),(1,0,0),(0,0,1),(0,1,0) — тетраэдр, угловой кусок куба у вершины (0,0,0),
    /// отсечённый плоскостью через (1,0,0),(0,1,0),(0,0,1).
    /// </summary>
    private static readonly Vector3[] PyramidVertices =
    {
        new(0, 0, 0), new(1, 0, 0), new(0, 0, 1), new(0, 1, 0),
    };

    private static readonly (int[] Ring, Vector3 Normal)[] PyramidFaces =
    {
        (new[] { 0, 1, 2 }, new Vector3(0, -1, 0)),                 // основание (y=0)
        (new[] { 0, 1, 3 }, new Vector3(0, 0, -1)),                 // грань z=0
        (new[] { 0, 2, 3 }, new Vector3(-1, 0, 0)),                 // грань x=0
        (new[] { 1, 2, 3 }, new Vector3(1, 1, 1).Normalized()),     // наклонная грань (плоскость x+y+z=1)
    };

    /// <summary>
    /// Инвертированная пирамида: куб без одной вершины (1,1,1) — то есть без углового тетраэдра, который
    /// у обычной Пирамиды как раз и есть весь блок. 7 вершин, 3 полные квадратные грани (x=0, y=0, z=0),
    /// 3 усечённые до треугольника грани (x=1, y=1, z=1) и новая треугольная грань среза (плоскость x+y+z=2).
    /// </summary>
    private static readonly Vector3[] InversePyramidVertices =
    {
        new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(1, 1, 0), new(1, 0, 1), new(0, 1, 1),
    };

    private static readonly (int[] Ring, Vector3 Normal)[] InversePyramidFaces =
    {
        (new[] { 0, 2, 6, 3 }, new Vector3(-1, 0, 0)),              // x=0, полная
        (new[] { 0, 1, 5, 3 }, new Vector3(0, -1, 0)),              // y=0, полная
        (new[] { 0, 1, 4, 2 }, new Vector3(0, 0, -1)),              // z=0, полная
        (new[] { 1, 4, 5 }, new Vector3(1, 0, 0)),                  // x=1, усечена до треугольника
        (new[] { 2, 4, 6 }, new Vector3(0, 1, 0)),                  // y=1, усечена до треугольника
        (new[] { 3, 5, 6 }, new Vector3(0, 0, 1)),                  // z=1, усечена до треугольника
        (new[] { 4, 5, 6 }, new Vector3(1, 1, 1).Normalized()),     // срез (плоскость x+y+z=2)
    };

    /// <summary>false для <see cref="BlockShape.Cube"/> (и любого другого значения без явной геометрии) — такие
    /// блоки рисует <see cref="ChunkMesher"/> как обычно.</summary>
    public static bool TryGet(BlockShape shape, out Vector3[] vertices, out (int[] Ring, Vector3 Normal)[] faces)
    {
        switch (shape)
        {
            case BlockShape.Slope:
                vertices = WedgeVertices;
                faces = WedgeFaces;
                return true;
            case BlockShape.Pyramid:
                vertices = PyramidVertices;
                faces = PyramidFaces;
                return true;
            case BlockShape.InvertedPyramid:
                vertices = InversePyramidVertices;
                faces = InversePyramidFaces;
                return true;
            default:
                vertices = System.Array.Empty<Vector3>();
                faces = System.Array.Empty<(int[], Vector3)>();
                return false;
        }
    }
}
