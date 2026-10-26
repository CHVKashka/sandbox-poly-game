using System.Collections.Generic;
using Godot;

namespace SandboxPolyGame.World;

/// <summary>
/// Чистая (без RPC) часть репликации заспавненных построек в сетевой игре: упаковка/распаковка поз тел и создание копий-наблюдателей. Сам обмен — в <see cref="Core.NetHub"/>
/// (партиал «Vehicles»), здесь только данные, поэтому всё проверяется самотестом без второго процесса.
/// <para/>
/// <b>Модель.</b> Физикой и рантаймом (<see cref="Runtime.FunctionalBlockRuntime"/>) владеет СЕРВЕР: тело — обычный <see cref="VehicleBody"/>. У каждого клиента та же постройка собирается из
/// того же JSON (<see cref="VehicleSpawner"/>) и превращается в копию-наблюдателя (<see cref="VehicleBody.MakePuppet"/>): поза приходит пакетами, изменяемое состояние блоков (кнопки,
/// сиденья, заряд, обороты) — снимками рантайма. Ввод игрока (посадка, клавиши пилота, кнопки) идёт на сервер запросами.
/// </summary>
public static class VehicleReplication
{
    /// <summary>Сколько чисел занимает поза одного тела в пакете: id, позиция (3), поворот-кватернион (4), линейная скорость (3).</summary>
    public const int PoseStride = 11;

    /// <summary>Пакет поз: на каждое тело — <see cref="PoseStride"/> чисел подряд.</summary>
    public static double[] CapturePoses(IEnumerable<VehicleBody> vehicles)
    {
        var values = new List<double>();
        foreach (var vehicle in vehicles)
        {
            if (!GodotObject.IsInstanceValid(vehicle) || !vehicle.IsInsideTree()) continue;

            var transform = vehicle.GlobalTransform;
            var rotation = transform.Basis.GetRotationQuaternion();
            var velocity = vehicle.LinearVelocity;
            values.Add(vehicle.NetId);
            values.Add(transform.Origin.X); values.Add(transform.Origin.Y); values.Add(transform.Origin.Z);
            values.Add(rotation.X); values.Add(rotation.Y); values.Add(rotation.Z); values.Add(rotation.W);
            values.Add(velocity.X); values.Add(velocity.Y); values.Add(velocity.Z);
        }

        return values.ToArray();
    }

    /// <summary>Разбирает пакет поз и отдаёт каждую найденную копию-наблюдателя через <paramref name="find"/>; неизвестные id пропускаются. Возвращает, сколько тел обновлено.</summary>
    public static int ApplyPoses(double[] values, System.Func<int, VehicleBody?> find, bool snap = false)
    {
        int applied = 0;
        for (int i = 0; i + PoseStride <= values.Length; i += PoseStride)
        {
            var vehicle = find((int)values[i]);
            if (vehicle == null || !GodotObject.IsInstanceValid(vehicle)) continue;

            var origin = new Vector3(values[i + 1], values[i + 2], values[i + 3]);
            var rotation = new Quaternion(values[i + 4], values[i + 5], values[i + 6], values[i + 7]).Normalized();
            var velocity = new Vector3(values[i + 8], values[i + 9], values[i + 10]);
            vehicle.SetPuppetTarget(new Transform3D(new Basis(rotation), origin), velocity, snap);
            applied++;
        }

        return applied;
    }

    /// <summary>Копия-наблюдатель серверного тела: та же постройка из того же JSON, заморожена, рантайм-зеркало. Поза — <paramref name="pose"/> (первый пакет ставит тело сразу).</summary>
    public static VehicleBody CreatePuppet(Node3D parent, int netId, string constructionJson, string workbenchName, Transform3D pose)
    {
        var body = VehicleSpawner.Spawn(parent, constructionJson, pose.Origin);
        body.NetId = netId;
        body.SourceWorkbenchName = workbenchName;
        body.MakePuppet();
        body.SetPuppetTarget(pose, Vector3.Zero, snap: true);
        return body;
    }
}
