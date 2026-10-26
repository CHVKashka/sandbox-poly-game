using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;

namespace SandboxPolyGame.Editor;

/// <summary>Ссылка на ноду конкретного блока постройки.</summary>
public readonly record struct NodeRef(int InstanceId, string NodeId);

/// <summary>
/// Визуальный слой инструмента «Nodes» в редакторе построек (<see cref="ToolMode.Wire"/>): маркеры ВСЕХ нод всех функциональных блоков (выход — шарик, вход —
/// кубик; цвет — по типу ноды), провода между ними цветными трубками и «резинка» от ноды, с которой сейчас тянут провод, до курсора. Маркеры рисуются поверх
/// полупрозрачных блоков (без теста глубины). Выбор ноды мышью — по ближайшему маркеру в экранных пикселях (<see cref="PickNode"/>), как и выбор якоря в
/// редакторе блоков: маркеры мелкие, а точный луч в них попасть не даёт. Ноды разных типов в одной клетке разнесены (<see cref="FunctionalBlockGeometry.NodeSpread"/>).
/// <para/>
/// Показан ОДИН слой логики (<see cref="Layer"/>): электричество или логика (Number/Boolean) — ноды и провода чужого слоя не рисуются и не выбираются.
/// <para/>
/// Слой пассивен: <see cref="Rebuild"/> пересобирает его по постройке (вызывает <see cref="BuildEditor"/> на каждое изменение, пока инструмент включён).
/// </summary>
public partial class WireOverlay : Node3D
{
    /// <summary>Радиус захвата ноды курсором, пикселей.</summary>
    public const float PickPixels = 14f;

    private static readonly Dictionary<NodeType, Color> TypeColors = new()
    {
        [NodeType.Electricity] = new Color(1.0f, 0.85f, 0.2f),
        [NodeType.Boolean] = new Color(1.0f, 0.3f, 0.28f),
        [NodeType.Number] = new Color(0.3f, 0.9f, 0.4f),
    };

    private sealed record Marker(NodeRef Ref, string Title, LogicNode Node, Vector3 Position, MeshInstance3D Mesh);

    private readonly List<Marker> _markers = new();
    private readonly Dictionary<Color, StandardMaterial3D> _materials = new();
    private Node3D _wires = null!;
    private Node3D _preview = null!;
    private Label3D _label = null!;
    private NodeRef? _hover;
    private NodeRef? _selected;
    private bool _selectedIsAnchor;
    private WireLayer _layer = WireLayer.Electricity;

    public static Color ColorOf(NodeType type) => TypeColors.GetValueOrDefault(type, Colors.White);

    /// <summary>К какому слою относится нода такого типа.</summary>
    public static WireLayer LayerOf(NodeType type) => type == NodeType.Electricity ? WireLayer.Electricity : WireLayer.Logic;

    /// <summary>Показанный слой. Смена пересобирает маркеры и провода при следующем <see cref="Rebuild"/> (его вызывает <see cref="BuildEditor"/>).</summary>
    public WireLayer Layer
    {
        get => _layer;
        set => _layer = value;
    }

    public NodeRef? Selected => _selected;
    public bool SelectedIsAnchor => _selectedIsAnchor;

    /// <summary>Сколько маркеров нод сейчас показано (для самотестов).</summary>
    public int MarkerCount => _markers.Count;

    /// <summary>Сколько проводов сейчас нарисовано (для самотестов).</summary>
    public int WireCount => _wires.GetChildren().Count(c => !c.IsQueuedForDeletion());

    public NodeRef? Hovered => _hover;

    public override void _Ready()
    {
        _wires = new Node3D { Name = "Wires" };
        AddChild(_wires);
        _preview = new Node3D { Name = "Preview", Visible = false };
        AddChild(_preview);
        _label = new Label3D
        {
            Name = "Label",
            FontSize = 28,
            PixelSize = 0.0022f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            Visible = false,
            RenderPriority = 10,
            OutlineSize = 8,
        };
        AddChild(_label);
    }

    private StandardMaterial3D MaterialFor(Color color)
    {
        if (_materials.TryGetValue(color, out var material)) return material;

        material = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = color, NoDepthTest = true, RenderPriority = 5 };
        _materials[color] = material;
        return material;
    }

    /// <summary>Мировая точка ноды (центр клетки + разнос по типу); null — такой ноды нет на экране.</summary>
    public Vector3? PositionOf(NodeRef node) => _markers.FirstOrDefault(m => m.Ref == node)?.Position;

    public LogicNode? NodeOf(NodeRef node) => _markers.FirstOrDefault(m => m.Ref == node)?.Node;

    /// <summary>Пересобирает маркеры и провода по постройке.</summary>
    public void Rebuild(Construction construction, BlockCatalog catalog)
    {
        foreach (var marker in _markers) marker.Mesh.QueueFree();
        _markers.Clear();
        foreach (var child in _wires.GetChildren()) child.QueueFree();

        foreach (var instance in construction.Instances)
        {
            if (!catalog.TryGetBySlug(instance.BlockSlug, out var definition)) continue;
            var block = definition.GetComponent<FunctionalBlockComponent>();
            if (block == null) continue;

            foreach (var node in block.Nodes)
            {
                if (LayerOf(node.Type) != _layer) continue;
                var position = FunctionalBlockGeometry.NodeWorldPosition(instance, block, node);
                var color = ColorOf(node.Type);
                var mesh = new MeshInstance3D
                {
                    Mesh = node.Direction == PortDirection.Out
                        ? new SphereMesh { Radius = 0.022f, Height = 0.044f, RadialSegments = 12, Rings = 6 }
                        : new BoxMesh { Size = new Vector3(0.04f, 0.04f, 0.04f) },
                    MaterialOverride = MaterialFor(color),
                    Position = position,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };
                AddChild(mesh);
                _markers.Add(new Marker(new NodeRef(instance.InstanceId, node.Id), $"{definition.Name}: {node.Id} ({node.Direction}, {node.Type})", node, position, mesh));
            }
        }

        foreach (var wire in construction.Wires)
        {
            var from = PositionOf(new NodeRef(wire.FromInstance, wire.FromNode));
            var to = PositionOf(new NodeRef(wire.ToInstance, wire.ToNode));
            var node = NodeOf(new NodeRef(wire.FromInstance, wire.FromNode));
            if (from == null || to == null || node == null) continue;
            _wires.AddChild(MakeTube(from.Value, to.Value, ColorOf(node.Type)));
        }

        ApplyHover();
    }

    private MeshInstance3D MakeTube(Vector3 from, Vector3 to, Color color, float radius = 0.005f)
    {
        var direction = to - from;
        float length = Mathf.Max((float)direction.Length(), 1e-4f);
        var up = direction / length;
        var right = up.Cross(Vector3.Forward);
        if (right.LengthSquared() < 1e-6) right = up.Cross(Vector3.Right);
        right = right.Normalized();
        var forward = right.Cross(up).Normalized();

        return new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 1f, RadialSegments = 6, Rings = 1 },
            MaterialOverride = MaterialFor(color),
            Transform = new Transform3D(new Basis(right, up * length, forward), (from + to) * 0.5f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    /// <summary>Нода под курсором <paramref name="mouse"/> (экранные пиксели) — ближайший маркер в пределах <see cref="PickPixels"/>; null — курсор не на ноде.</summary>
    public NodeRef? PickNode(Camera3D camera, Vector2 mouse)
    {
        NodeRef? best = null;
        double bestDistance = PickPixels;
        foreach (var marker in _markers)
        {
            if (camera.IsPositionBehind(marker.Position)) continue;
            double distance = camera.UnprojectPosition(marker.Position).DistanceTo(mouse);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = marker.Ref;
            }
        }

        return best;
    }

    /// <summary>Подсвечивает ноду под курсором (увеличенный маркер + подпись); null — снять подсветку.</summary>
    public void SetHover(NodeRef? node)
    {
        if (_hover == node) return;
        _hover = node;
        ApplyHover();
    }

    private void ApplyHover()
    {
        foreach (var marker in _markers)
        {
            bool selected = _selected == marker.Ref;
            marker.Mesh.Scale = _hover == marker.Ref ? new Vector3(1.8f, 1.8f, 1.8f) : selected ? new Vector3(1.5f, 1.5f, 1.5f) : Vector3.One;
            marker.Mesh.MaterialOverride = MaterialFor(selected ? (_selectedIsAnchor ? AnchorColor : SelectedColor) : ColorOf(marker.Node.Type));
        }

        var hovered = _hover.HasValue ? _markers.FirstOrDefault(m => m.Ref == _hover.Value) : null;
        _label.Visible = hovered != null;
        if (hovered == null) return;

        _label.Text = hovered.Title;
        _label.Modulate = ColorOf(hovered.Node.Type);
        _label.Position = hovered.Position + new Vector3(0, 0.07f, 0);
    }

    private static readonly Color SelectedColor = new(1f, 1f, 1f);
    private static readonly Color AnchorColor = new(0.3f, 0.85f, 1f);

    /// <summary>Выбранная нода (белая, чуть крупнее) — от неё идёт соединение; <paramref name="isAnchor"/> — это якорь Ctrl (голубая): пока Ctrl зажат, ею соединяют сразу несколько нод. null — выбора нет.</summary>
    public void SetSelected(NodeRef? node, bool isAnchor)
    {
        if (_selected == node && _selectedIsAnchor == isAnchor) return;
        _selected = node;
        _selectedIsAnchor = isAnchor;
        ApplyHover();
    }

    /// <summary>«Резинка» тянущегося провода от <paramref name="from"/> до <paramref name="to"/>; цвет — допустимо ли соединение (зелёный/красный) или нейтральный.</summary>
    public void ShowPreview(Vector3 from, Vector3 to, Color color)
    {
        foreach (var child in _preview.GetChildren()) child.QueueFree();
        _preview.AddChild(MakeTube(from, to, color, 0.004f));
        _preview.Visible = true;
    }

    public void HidePreview()
    {
        foreach (var child in _preview.GetChildren()) child.QueueFree();
        _preview.Visible = false;
    }
}
