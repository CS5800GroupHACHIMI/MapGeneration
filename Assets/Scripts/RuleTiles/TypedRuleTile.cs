using System.Collections.Generic;
using Data;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// A RuleTile that can match specific TileType neighbors (Air, Wall, Floor, …),
/// including OR-groups that match if the neighbor belongs to ANY of the listed types.
///
/// Extensibility contract
/// ----------------------
/// • New TileType  → add ONE constant in Neighbor following the existing pattern.
/// • New OR-group  → increase OrGroupCount (or add a new OrGroupN constant) and
///                   configure the group's tile types in the Inspector.
/// RuleMatch never needs to change.
///
/// How neighbor constants work
/// ---------------------------
/// Built-in:  This=1, NotThis=2
/// Per-type:  value = NeighborTypeOffset + (int)TileType   (range 3–19)
/// OR-groups: value = OrGroupOffset + groupIndex           (range 20–27)
/// </summary>
[CreateAssetMenu(fileName = "TypedRuleTile", menuName = "Tiles/Typed Rule Tile")]
public class TypedRuleTile : RuleTile<TypedRuleTile.Neighbor>
{
    // ── Neighbor constants ────────────────────────────────────────────────────
    private const int NeighborTypeOffset = 3;  // per-type constants start here
    private const int OrGroupOffset      = 20; // OR-group constants start here (leaves room for 16 TileTypes)
    private const int OrGroupCount       = 8;  // number of configurable OR-groups

    public class Neighbor : RuleTile.TilingRule.Neighbor
    {
        // Per-type: one constant per TileType enum value.
        // To add a type: add ONE line following the pattern below.
        public const int Air   = 3; // NeighborTypeOffset + (int)TileType.Air   (0)
        public const int Wall  = 4; // NeighborTypeOffset + (int)TileType.Wall  (1)
        public const int Floor = 5; // NeighborTypeOffset + (int)TileType.Floor (2)
        public const int Path  = 6; // NeighborTypeOffset + (int)TileType.Path  (3)

        // OR-groups: configure their TileType lists in the TypedRuleTile Inspector.
        public const int OrGroup0 = 20;
        public const int OrGroup1 = 21;
        public const int OrGroup2 = 22;
        public const int OrGroup3 = 23;
        public const int OrGroup4 = 24;
        public const int OrGroup5 = 25;
        public const int OrGroup6 = 26;
        public const int OrGroup7 = 27;
    }

    // ── OR-group definition ───────────────────────────────────────────────────
    [System.Serializable]
    public class NeighborGroup
    {
        [Tooltip("Displayed name (for reference only).")]
        public string label = "Group";

        [Tooltip("The neighbor matches if its TileType is any of these.")]
        public TileType[] types = System.Array.Empty<TileType>();
    }

    // ── Fields ────────────────────────────────────────────────────────────────
    [Tooltip("The shared TileRegistry asset. Assign in the Inspector.")]
    public TileRegistry tileRegistry;

    [Tooltip("OR-groups used by OrGroup0–OrGroup7 neighbor constants.\n" +
             "Index 0 → OrGroup0, index 1 → OrGroup1, etc.")]
    public NeighborGroup[] neighborGroups = new NeighborGroup[OrGroupCount]
    {
        new NeighborGroup { label = "OrGroup0" },
        new NeighborGroup { label = "OrGroup1" },
        new NeighborGroup { label = "OrGroup2" },
        new NeighborGroup { label = "OrGroup3" },
        new NeighborGroup { label = "OrGroup4" },
        new NeighborGroup { label = "OrGroup5" },
        new NeighborGroup { label = "OrGroup6" },
        new NeighborGroup { label = "OrGroup7" },
    };

    // Reverse lookup: TileBase asset → TileType.
    private Dictionary<TileBase, TileType> _reverseRegistry;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private void OnEnable() => _reverseRegistry = null;

    // ── Core matching ─────────────────────────────────────────────────────────
    public override bool RuleMatch(int neighbor, TileBase tile)
    {
        // OR-group range
        if (neighbor >= OrGroupOffset && neighbor < OrGroupOffset + OrGroupCount)
        {
            int idx = neighbor - OrGroupOffset;
            var group = neighborGroups != null && idx < neighborGroups.Length
                        ? neighborGroups[idx] : null;

            if (group == null || group.types == null || group.types.Length == 0)
                return false;

            var actualType = ResolveType(tile);
            foreach (var t in group.types)
                if (t == actualType) return true;

            return false;
        }

        // Delegate built-in This / NotThis to the base class
        if (neighbor < NeighborTypeOffset || tileRegistry == null)
            return base.RuleMatch(neighbor, tile);

        // Per-type match
        var targetType = (TileType)(neighbor - NeighborTypeOffset);
        return ResolveType(tile) == targetType;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Returns the TileType of a cell's TileBase (null → Air).
    private TileType ResolveType(TileBase tile)
    {
        if (tile == null) return TileType.Air;
        return BuildReverseRegistry().TryGetValue(tile, out var t) ? t : TileType.Air;
    }

    private Dictionary<TileBase, TileType> BuildReverseRegistry()
    {
        if (_reverseRegistry != null) return _reverseRegistry;

        _reverseRegistry = new Dictionary<TileBase, TileType>();
        foreach (TileType type in System.Enum.GetValues(typeof(TileType)))
        {
            if (tileRegistry.TryGetAs<TileBase>(type, out var tileBase))
                _reverseRegistry[tileBase] = type;
        }

        return _reverseRegistry;
    }
}
