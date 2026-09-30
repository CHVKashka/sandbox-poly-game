using Godot;

namespace SandboxPolyGame.World;

/// <summary>
/// Верстак — точка входа в редактор построек. Модель — импортированная сцена <c>res://meshes/workbench.glb</c>
/// (см. Docs/05-world-and-vehicle-systems.md, «Формат моделей»). <see cref="StaticBody3D"/> (не просто
/// <see cref="Node3D"/>) — нужен коллайдер: открытие меню теперь по наведению взглядом (рейкаст из камеры игрока,
/// см. <see cref="GameWorld"/>), не по близости, как было раньше.
/// </summary>
public partial class Workbench : StaticBody3D
{
	/// <summary>Зона, где материализуются постройки, спавненные с этого верстака — к одной зоне может быть
	/// привязано несколько верстаков (см. <see cref="SpawnArea"/> class doc). Задаётся сразу после конструктора,
	/// до <see cref="_Ready"/> — сам верстак от неё ничего не требует, только передаёт дальше при спавне.</summary>
	public SpawnArea SpawnArea { get; set; } = null!;

	public override void _Ready()
	{
		var scene = GD.Load<PackedScene>("res://meshes/workbench.glb");
		if (scene != null)
		{
			AddChild(scene.Instantiate());
		}
		else
		{
			// Заглушка на случай отсутствующей/повреждённой модели — лучше видимый серый ящик, чем невидимый верстак.
			GD.PrintErr("[workbench] res://meshes/workbench.glb not found, using a placeholder box");
			AddChild(new MeshInstance3D
			{
				Mesh = new BoxMesh { Size = new Vector3(1f, 0.9f, 0.6f) },
				Position = new Vector3(0, 0.45f, 0),
				MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.5f, 0.35f, 0.2f) },
			});
		}

		// Коллайдер под наведение (см. class doc) - с запасом больше самой модели, чтобы навестись было легко.
		AddChild(new CollisionShape3D
		{
			Name = "Collision",
			Shape = new BoxShape3D { Size = new Vector3(1.4f, 1.4f, 1.4f) },
			Position = new Vector3(0, 0.7f, 0),
		});
	}
}
