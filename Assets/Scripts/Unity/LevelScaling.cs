using UnityEngine;

/// <summary>
/// Central difficulty formulas for level progression.
///   Level 1 → 40x40 map, 4 rooms, 2 monsters, 8 hazards
///   Level 5+ → 64x64 map (capped), 15+ rooms
/// </summary>
public static class LevelScaling
{
    public const int MinMapSize = 40;
    public const int MaxMapSize = 64;

    /// <summary>Map side length. Level 1 starts at 40, grows 8 per level, capped at 64.</summary>
    public static int MapSize(int level)
        => Mathf.Clamp(MinMapSize + (level - 1) * 8, MinMapSize, MaxMapSize);

    /// <summary>Target number of rooms for the generator. Lower values = sparser maps.</summary>
    public static int TargetRoomCount(int level)
        => Mathf.Clamp(2 + level * 2, 4, 15);

    /// <summary>Monsters scale with level, bounded by available rooms.</summary>
    public static int MonsterCount(int level, int availableRooms)
        => Mathf.Min(1 + level, Mathf.Max(0, availableRooms * 2 / 3));

    /// <summary>Random path tile hazards sprinkled on floors.</summary>
    public static int PathHazardCount(int level) => Mathf.Min(level * 8, 40);
}
