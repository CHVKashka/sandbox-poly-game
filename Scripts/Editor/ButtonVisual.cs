using System.Collections.Generic;
using Godot;
using SandboxPolyGame.Runtime;

namespace SandboxPolyGame.Editor;

/// <summary>
/// Визуал ОДНОЙ кнопки на экземпляре её модели (см. <see cref="FunctionalBlockView"/>): анимация нажатия и подсветка
/// крышки — два НЕЗАВИСИМЫХ канала, подсветка не запечена в клип Blender.
/// <list type="bullet">
/// <item><see cref="PressProgress"/> (0..1) плавно идёт к цели (нажата = 1, отпущена = 0) со скоростью
/// <see cref="PressSpeed"/>; по нему проигрывается ОДИН клип AnimationPlayer через <c>Seek(progress * длина, true)</c> —
/// вперёд при нажатии, назад при отпускании, прерванное нажатие разворачивается с текущего места без рывка. Клип —
/// "press", а если такого нет, первый не-RESET.</item>
/// <item><see cref="GlowBrightness"/> = запитан × <see cref="PressProgress"/>, сглаженная ОТДЕЛЬНО (скорость
/// <see cref="GlowSpeed"/>): пропало питание посреди нажатия — свечение гаснет плавно, не рывком. Применяется к
/// ЛИЧНОЙ копии материала узла-крышки (<see cref="ButtonSettings.GlowNode"/>): материалы импортированной сцены общие для
/// всех экземпляров, без <c>Duplicate</c> загорелись бы все кнопки сразу.</item>
/// </list>
/// Нет AnimationPlayer/клипа или узла-крышки — соответствующий канал просто не работает (<see cref="HasAnimation"/>/
/// <see cref="HasGlow"/>), ошибок нет; <see cref="PressProgress"/> при этом всё равно считается.
/// Иконка в хотбаре и призрак установки этот класс не используют — они статичны.
/// </summary>
public sealed class ButtonVisual
{
    /// <summary>Доля хода нажатия в секунду (8 = полный ход за ~0.125 с).</summary>
    public const double PressSpeed = 8.0;

    /// <summary>Скорость сглаживания яркости свечения, единиц яркости в секунду.</summary>
    public const double GlowSpeed = 5.0;

    /// <summary>Множитель <c>EmissionEnergyMultiplier</c> при яркости 1.</summary>
    public const double MaxEmissionEnergy = 2.0;

    public const string PreferredClip = "press";

    private readonly AnimationPlayer? _player;
    private readonly string? _clip;
    private readonly double _clipLength;
    private readonly List<BaseMaterial3D> _glowMaterials = new();
    private double _lastSeek = -1;
    private double _lastGlow = -1;

    public double PressProgress { get; private set; }
    public double GlowBrightness { get; private set; }
    public bool HasAnimation => _clip != null;
    public bool HasGlow => _glowMaterials.Count > 0;

    /// <summary>Личные копии материалов крышки этого экземпляра (для тестов и отладки).</summary>
    public IReadOnlyList<BaseMaterial3D> GlowMaterials => _glowMaterials;

    public ButtonVisual(Node modelRoot, ButtonSettings settings)
    {
        _player = FindPlayer(modelRoot);
        if (_player != null)
        {
            _clip = PickClip(_player);
            if (_clip != null)
            {
                _clipLength = _player.GetAnimation(_clip).Length;
                if (_clipLength <= 0) _clip = null;
            }

            if (_clip != null)
            {
                _player.Stop(); // клип ведём вручную через Seek, не проигрыванием
                _player.AssignedAnimation = _clip;
            }
        }

        if (modelRoot.FindChild(settings.GlowNode, true, false) is MeshInstance3D cap && cap.Mesh != null)
        {
            for (int surface = 0; surface < cap.Mesh.GetSurfaceCount(); surface++)
            {
                var source = cap.GetActiveMaterial(surface);
                BaseMaterial3D copy;
                if (source == null) copy = new StandardMaterial3D();
                else if (source is BaseMaterial3D baseMaterial) copy = (BaseMaterial3D)baseMaterial.Duplicate();
                else continue; // шейдерный материал - подсвечивать нечем, оставляем как есть

                copy.EmissionEnabled = true;
                copy.Emission = settings.GlowColor;
                copy.EmissionEnergyMultiplier = 0f;
                cap.SetSurfaceOverrideMaterial(surface, copy);
                _glowMaterials.Add(copy);
            }
        }
    }

    private static AnimationPlayer? FindPlayer(Node root)
    {
        if (root is AnimationPlayer self) return self;
        var found = root.FindChildren("*", "AnimationPlayer", true, false);
        return found.Count > 0 ? found[0] as AnimationPlayer : null;
    }

    private static string? PickClip(AnimationPlayer player)
    {
        if (player.HasAnimation(PreferredClip)) return PreferredClip;
        foreach (string name in player.GetAnimationList())
        {
            if (name != "RESET" && name != "") return name;
        }

        return null;
    }

    /// <summary>Один кадр: <paramref name="pressed"/> — цель хода (для toggle — состояние-защёлка, см. <see cref="ButtonState.Active"/>),
    /// <paramref name="powered"/> — запитан ли блок.</summary>
    public void Update(double delta, bool pressed, bool powered)
    {
        PressProgress = Mathf.MoveToward(PressProgress, pressed ? 1.0 : 0.0, PressSpeed * delta);
        GlowBrightness = Mathf.MoveToward(GlowBrightness, powered ? PressProgress : 0.0, GlowSpeed * delta);

        if (_player != null && _clip != null && PressProgress != _lastSeek)
        {
            _player.Seek(PressProgress * _clipLength, true);
            _lastSeek = PressProgress;
        }

        if (GlowBrightness != _lastGlow)
        {
            foreach (var material in _glowMaterials) material.EmissionEnergyMultiplier = (float)(GlowBrightness * MaxEmissionEnergy);
            _lastGlow = GlowBrightness;
        }
    }
}
