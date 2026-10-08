using System.Collections.Generic;
using System.Linq;
using Godot;
using SandboxPolyGame.Blocks;
using SandboxPolyGame.Core;
using SandboxPolyGame.Editor.Ui;

namespace SandboxPolyGame.Editor;

/// <summary>
/// Инструменты «Nodes» (<see cref="ToolMode.Wire"/>) и «Parameters» (<see cref="ToolMode.Parameters"/>) редактора построек (часть <see cref="BuildEditor"/>).
/// <para/>
/// <b>Nodes.</b> Блоки становятся полупрозрачными, поверх них видны ноды ОДНОГО слоя (<see cref="WireLayer"/>: электричество или логика — вкладки на тулбаре;
/// Number — зелёные, Boolean — красные, Electricity — жёлтые). ЛКМ на ноде выбирает её, перетаскивание к другой ноде <b>переключает</b> провод между ними — нет провода
/// создаётся (с любого конца: значение всё равно идёт от выхода ко входу), есть — убирается. <b>Ctrl</b> запоминает якорь — ноду, выбранную до нажатия Ctrl (или первую,
/// на которую нажали после): пока Ctrl зажат, нажатие/перетаскивание к любой другой ноде соединяет её с якорем (или разъединяет), так одну ноду подключают к нескольким.
/// ПКМ по ноде убирает все её провода, Esc отменяет перетаскивание/снимает выбор. Все правки идут через <see cref="ApplyEdit"/> (<see cref="NetEditKind.Connect"/>/
/// <see cref="NetEditKind.Disconnect"/>), то есть работают и в сетевой сессии, и попадают в Undo.
/// <para/>
/// <b>Parameters.</b> Блоки без настроек полупрозрачны, блоки со схемой параметров (<see cref="ParametersComponent"/>) закрашены тёмно-фиолетовым, блок под курсором —
/// голубым. Клик по такому блоку открывает слева панель его параметров (<see cref="ParametersPanelUi"/>), клик по пустому месту или Esc её закрывают. Правка параметра —
/// <see cref="NetEditKind.SetParameter"/>.
/// </summary>
public partial class BuildEditor
{
    /// <summary>Непрозрачность постройки под инструментами «Nodes»/«Parameters» (1 — обычная): достаточно прозрачна, чтобы видеть ноды внутри.</summary>
    public const float ToolGhostOpacity = 0.28f;

    private static readonly Color ParameterBlockColor = new(0.27f, 0.07f, 0.45f);
    private static readonly Color ParameterHoverColor = new(0.15f, 0.82f, 1.0f);

    private WireOverlay _wireOverlay = null!;
    private NodeRef? _wireDragFrom;   // нода, от которой тянут провод прямо сейчас (ЛКМ зажата)
    private NodeRef? _wireSelected;   // выбранная нода: от неё Ctrl-якорь, если Ctrl зажмут позже
    private NodeRef? _wireAnchor;     // якорь Ctrl: пока Ctrl зажат, нажатия на другие ноды соединяют их с ним
    private bool _wireCtrl;
    private int _parameterHoverId;
    private int _parameterSelectedId;
    private readonly Dictionary<int, MeshInstance3D> _parameterBoxes = new();

    public WireOverlay WireOverlay => _wireOverlay;
    public NodeRef? WireDragFrom => _wireDragFrom;
    public NodeRef? WireSelected => _wireSelected;
    public NodeRef? WireAnchor => _wireAnchor;
    public bool WireCtrlHeld => _wireCtrl;
    public int ParametersHoverInstanceId => _parameterHoverId;
    public int ParametersSelectedInstanceId => _parameterSelectedId;

    /// <summary>Сколько боксов-подсветок нарисовано у блоков с настройками БЕЗ модели (для самотестов).</summary>
    public int ParameterBoxCount => _parameterBoxes.Count;

    /// <summary>Создаёт слой нод и подписывается на изменения постройки (вызывается из <c>_Ready</c>).</summary>
    private void InitializeTools()
    {
        _wireOverlay = new WireOverlay { Name = "WireOverlay", Visible = false };
        AddChild(_wireOverlay);
        _world.Construction.Changed += OnConstructionChangedForTools;
    }

    /// <summary>Подписки панели параметров (вызывается из <c>_Ready</c> после создания интерфейса).</summary>
    private void InitializeToolsUi()
    {
        _ui.ParametersPanel.ParameterEdited += OnParameterEdited;
        _ui.ParametersPanel.CloseRequested += CloseParametersPanel;
    }

    private void OnConstructionChangedForTools()
    {
        if (_state.Tool == ToolMode.Wire)
        {
            _wireOverlay.Rebuild(_world.Construction, BlockCatalog.Instance);
            ValidateWireSelection();
        }

        if (_state.Tool != ToolMode.Parameters) return;

        // Выбранный блок могли удалить/откатить Undo - панель не должна остаться на несуществующем экземпляре.
        if (_parameterSelectedId != 0 && _world.Construction.GetInstance(_parameterSelectedId) is { } selected) _ui.ParametersPanel.Refresh(selected);
        else if (_parameterSelectedId != 0) CloseParametersPanel();
        RefreshParameterHighlights();
    }

    /// <summary>Приводит отображение постройки в соответствие с активным инструментом: прозрачность, слой нод, подсветка параметров, закрытие лишнего.</summary>
    private void ApplyToolDisplay()
    {
        bool wire = _state.Tool == ToolMode.Wire;
        bool parameters = _state.Tool == ToolMode.Parameters;

        _world.Opacity = wire || parameters ? ToolGhostOpacity : 1f;
        _wireOverlay.Visible = wire;
        if (wire)
        {
            _wireOverlay.Layer = _state.WireLayer;
            _wireOverlay.Rebuild(_world.Construction, BlockCatalog.Instance);
            ValidateWireSelection();
        }
        else
        {
            CancelWireDrag();
            ClearWireSelection();
        }

        if (!parameters) CloseParametersPanel();
        RefreshParameterHighlights();
    }

    // ------------------------------------------------------------------ Nodes

    /// <summary>Нода под курсором (экранные пиксели) — для самотестов и ввода.</summary>
    public NodeRef? PickWireNode(Vector2 mouse) => _wireOverlay.PickNode(_camera, mouse);

    private Vector3I CellOfInstance(int instanceId) => _world.Construction.GetInstance(instanceId)?.Origin ?? Vector3I.Zero;

    private void SendConnect(NodeRef from, NodeRef to, bool recordUndo) =>
        ApplyEdit(NetEditKind.Connect, CellOfInstance(from.InstanceId), CellOfInstance(to.InstanceId), NetEditOps.EncodeNodes(from.NodeId, to.NodeId),
            Colors.White, Vector3I.Zero, Vector3I.Zero, recordUndo: recordUndo);

    private void SendDisconnect(NodeWire wire, bool recordUndo) =>
        ApplyEdit(NetEditKind.Disconnect, CellOfInstance(wire.FromInstance), CellOfInstance(wire.ToInstance), NetEditOps.EncodeNodes(wire.FromNode, wire.ToNode),
            Colors.White, Vector3I.Zero, Vector3I.Zero, recordUndo: recordUndo);

    /// <summary>Ctrl зажат/отпущен. Зажатие запоминает якорь — выбранную ноду (если выбор уже был); если выбора не было, якорем станет первая нода, на которую нажмут. Отпускание забывает якорь.</summary>
    public void SetWireCtrl(bool held)
    {
        if (held == _wireCtrl) return;

        _wireCtrl = held;
        _wireAnchor = held ? _wireSelected : null;
        RefreshWireSelection();
    }

    private void RefreshWireSelection() => _wireOverlay.SetSelected(_wireAnchor ?? _wireSelected, _wireAnchor != null);

    /// <summary>Снимает выбор ноды и якорь Ctrl (клик по пустому месту, Esc, смена слоя/инструмента).</summary>
    public void ClearWireSelection()
    {
        _wireSelected = null;
        _wireAnchor = null;
        RefreshWireSelection();
    }

    /// <summary>Выбранная/якорная нода, которой больше нет на экране (блок удалили, другой слой), сбрасывается.</summary>
    private void ValidateWireSelection()
    {
        if (_wireSelected is { } selected && _wireOverlay.PositionOf(selected) == null) _wireSelected = null;
        if (_wireAnchor is { } anchor && _wireOverlay.PositionOf(anchor) == null) _wireAnchor = null;
        if (_wireDragFrom is { } drag && _wireOverlay.PositionOf(drag) == null) CancelWireDrag();
        RefreshWireSelection();
    }

    /// <summary>
    /// ЛКМ нажата в режиме «Nodes». Без Ctrl: нода становится выбранной и от неё начинается перетаскивание (нажатие на пустое место снимает выбор). С Ctrl: соединение всегда
    /// идёт от АНКЕРА — ноды, выбранной до нажатия Ctrl (или первой, на которую нажали после него); пока Ctrl зажат, нажатия на другие ноды соединяют/разъединяют их с якорем,
    /// так одну ноду подключают к нескольким. Сам провод создаётся/убирается при отпускании кнопки (<see cref="WireToolRelease"/>).
    /// </summary>
    public void WireToolPress(Vector2 mouse, bool ctrl)
    {
        SetWireCtrl(ctrl);
        var node = PickWireNode(mouse);

        if (_wireCtrl)
        {
            if (node == null) return;
            if (_wireAnchor == null)
            {
                _wireAnchor = node;
                _wireSelected = node;
                RefreshWireSelection();
            }

            _wireDragFrom = _wireAnchor;
            return;
        }

        _wireSelected = node;
        RefreshWireSelection();
        _wireDragFrom = node;
    }

    /// <summary>
    /// ЛКМ отпущена: если курсор над другой нодой — между нодами провод переключается: есть — убирается (в любом направлении), нет — создаётся (правила —
    /// <see cref="Construction.CheckWire"/>, причина отказа — в строке статуса). Отпустили на пустом месте или на той же ноде — ничего (нода остаётся выбранной).
    /// </summary>
    public void WireToolRelease(Vector2 mouse)
    {
        if (_wireDragFrom is not { } from) return;

        var target = PickWireNode(mouse);
        _wireDragFrom = null;
        _wireOverlay.HidePreview();

        if (target is { } to && to != from) ToggleWire(from, to);
    }

    /// <summary>Соединяет две ноды или, если они уже соединены напрямую, разрывает провод; одно действие — один шаг Undo.</summary>
    public bool ToggleWire(NodeRef from, NodeRef to)
    {
        var construction = _world.Construction;
        if (construction.FindWireBetween(from.InstanceId, from.NodeId, to.InstanceId, to.NodeId) is { } existing)
        {
            SendDisconnect(existing, recordUndo: true);
            _ui.SetStatus($"Disconnected {from.NodeId} and {to.NodeId}");
            return true;
        }

        if (!construction.CheckWire(from.InstanceId, from.NodeId, to.InstanceId, to.NodeId, out _, out string error))
        {
            _ui.SetStatus("Cannot connect: " + error);
            return false;
        }

        SendConnect(from, to, recordUndo: true);
        _ui.SetStatus($"Connected {from.NodeId} and {to.NodeId}");
        return true;
    }

    /// <summary>Esc/смена инструмента во время перетаскивания: перетаскивание отменяется, ничего не меняется.</summary>
    private void CancelWireDrag()
    {
        if (_wireDragFrom == null) return;
        _wireDragFrom = null;
        _wireOverlay.HidePreview();
    }

    /// <summary>Esc в режиме «Nodes»: сначала отмена перетаскивания, затем снятие выбора. true — было что отменять.</summary>
    private bool EscapeWireTool()
    {
        if (_wireDragFrom != null)
        {
            CancelWireDrag();
            return true;
        }

        if (_wireSelected == null && _wireAnchor == null) return false;
        ClearWireSelection();
        return true;
    }

    /// <summary>ПКМ по ноде: убрать все её провода.</summary>
    public void WireToolRemove(Vector2 mouse)
    {
        if (PickWireNode(mouse) is not { } node || !_world.Construction.WiresOf(node.InstanceId, node.NodeId).Any()) return;

        ApplyEdit(NetEditKind.Disconnect, CellOfInstance(node.InstanceId), Vector3I.Zero, NetEditOps.EncodeNodes(node.NodeId, ""), Colors.White, Vector3I.Zero, Vector3I.Zero);
        _ui.SetStatus($"Removed the wires of {node.NodeId}");
    }

    /// <summary>
    /// Каждый кадр в режиме «Nodes»: подсветка ноды под курсором и «резинка» от ноды-источника (тянущейся или якоря Ctrl) до курсора: зелёная — соединение возможно,
    /// оранжевая — между этими нодами уже есть провод (отпускание его уберёт), красная — нельзя.
    /// </summary>
    private void UpdateWireTool()
    {
        if (_state.Tool != ToolMode.Wire)
        {
            _wireOverlay.SetHover(null);
            return;
        }

        // Ctrl могли отпустить вне окна (потеря фокуса) - события отпускания тогда нет.
        if (_wireCtrl && !Input.IsKeyPressed(Key.Ctrl)) SetWireCtrl(false);

        var hovered = _looking || _ui.IsPointOverUi(_mousePosition) ? null : PickWireNode(_mousePosition);
        _wireOverlay.SetHover(hovered);

        var source = _wireDragFrom ?? (_wireCtrl ? _wireAnchor : null);
        if (source is not { } from || _wireOverlay.PositionOf(from) is not { } fromPosition)
        {
            if (_wireDragFrom != null) CancelWireDrag();
            _wireOverlay.HidePreview();
            return;
        }

        Vector3 end;
        Color color = Colors.White;
        if (hovered is { } over && over != from && _wireOverlay.PositionOf(over) is { } overPosition)
        {
            end = overPosition;
            var construction = _world.Construction;
            color = construction.FindWireBetween(from.InstanceId, from.NodeId, over.InstanceId, over.NodeId) != null ? new Color(1f, 0.6f, 0.1f)
                : construction.CheckWire(from.InstanceId, from.NodeId, over.InstanceId, over.NodeId, out _, out _) ? new Color(0.3f, 1f, 0.4f)
                : new Color(1f, 0.3f, 0.25f);
        }
        else
        {
            // Свободный конец — на луче курсора на том же расстоянии от камеры, что и начальная нода.
            end = _camera.ProjectRayOrigin(_mousePosition) + _camera.ProjectRayNormal(_mousePosition) * (float)_camera.GlobalPosition.DistanceTo(fromPosition);
        }

        _wireOverlay.ShowPreview(fromPosition, end, color);
    }

    // ------------------------------------------------------------------ Parameters

    private BlockInstance? ConfigurableUnderCursor()
    {
        if (!_hover.IsBlock) return null;
        var owner = _world.Construction.GetOwner(_hover.BlockCell);
        return owner != null && BlockCatalog.Instance.TryGetBySlug(owner.BlockSlug, out var definition) && Construction.ParametersOf(definition) != null ? owner : null;
    }

    /// <summary>Клик ЛКМ в режиме «Parameters»: по блоку с настройками — открыть его панель, по пустому месту/блоку без настроек — закрыть панель.</summary>
    public void ParametersToolClick()
    {
        var instance = ConfigurableUnderCursor();
        if (instance == null)
        {
            CloseParametersPanel();
            return;
        }

        OpenParametersPanel(instance);
    }

    /// <summary>То же, что клик, но с явной позицией курсора (для самотестов без настоящей мыши).</summary>
    public void ParametersToolClickAt(Vector2 mouse)
    {
        _mousePosition = mouse;
        UpdateHover();
        ParametersToolClick();
    }

    private void OpenParametersPanel(BlockInstance instance)
    {
        if (!BlockCatalog.Instance.TryGetBySlug(instance.BlockSlug, out var definition) || Construction.ParametersOf(definition) is not { } schema) return;

        _parameterSelectedId = instance.InstanceId;
        _ui.ParametersPanel.Open(instance, definition, schema);
        RefreshParameterHighlights();
    }

    private void CloseParametersPanel()
    {
        if (_parameterSelectedId == 0 && !_ui.ParametersPanel.IsOpen) return;
        _parameterSelectedId = 0;
        _ui.ParametersPanel.Close();
        RefreshParameterHighlights();
    }

    private void OnParameterEdited(string parameterId, string text)
    {
        var instance = _world.Construction.GetInstance(_parameterSelectedId);
        if (instance == null) return;

        ApplyEdit(NetEditKind.SetParameter, instance.Origin, Vector3I.One, NetEditOps.EncodeParameter(parameterId, text), Colors.White, Vector3I.Zero, Vector3I.Zero);
        // Показать нормализованное значение (зажатое в границы/каноническую форму); в сетевой сессии правка придёт позже - обновит OnConstructionChangedForTools.
        if (_world.Construction.GetInstance(_parameterSelectedId) is { } updated) _ui.ParametersPanel.Refresh(updated);
    }

    /// <summary>Каждый кадр в режиме «Parameters»: какой блок с настройками под курсором — перекрашивается только при смене.</summary>
    private void UpdateParametersHover()
    {
        int hover = _state.Tool == ToolMode.Parameters && !_looking && !_ui.IsPointOverUi(_mousePosition) ? ConfigurableUnderCursor()?.InstanceId ?? 0 : 0;
        if (hover == _parameterHoverId) return;

        _parameterHoverId = hover;
        RefreshParameterHighlights();
    }

    /// <summary>
    /// Раскраска «Parameters»: блок с настройками — тёмно-фиолетовый, под курсором и выбранный — голубой; остальные блоки полупрозрачны (см. <see cref="ApplyToolDisplay"/>).
    /// Модель красится через <see cref="FunctionalBlockView.SetInstanceTint"/>; у блока БЕЗ модели (цветной куб-плейсхолдер из чанков) поверх рисуется непрозрачный бокс по его клеткам.
    /// </summary>
    private void RefreshParameterHighlights()
    {
        _world.FunctionalBlocks.ClearTints();
        foreach (var box in _parameterBoxes.Values) box.QueueFree();
        _parameterBoxes.Clear();
        if (_state.Tool != ToolMode.Parameters) return;

        foreach (var instance in _world.Construction.Instances)
        {
            if (!BlockCatalog.Instance.TryGetBySlug(instance.BlockSlug, out var definition) || Construction.ParametersOf(definition) == null) continue;

            var color = instance.InstanceId == _parameterHoverId || instance.InstanceId == _parameterSelectedId ? ParameterHoverColor : ParameterBlockColor;
            if (_world.FunctionalBlocks.GetView(instance.InstanceId) != null)
            {
                _world.FunctionalBlocks.SetInstanceTint(instance.InstanceId, color);
                continue;
            }

            const float grow = 0.012f;
            var size = new Vector3(instance.Size.X, instance.Size.Y, instance.Size.Z) * BuildSpace.CellSize + new Vector3(grow, grow, grow) * 2;
            var box = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = size },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.9f, EmissionEnabled = true, Emission = color, EmissionEnergyMultiplier = 0.35f },
                Position = (BuildSpace.CellMin(instance.Origin) + BuildSpace.CellMin(instance.Origin + instance.Size)) * 0.5f,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(box);
            _parameterBoxes[instance.InstanceId] = box;
        }
    }
}
