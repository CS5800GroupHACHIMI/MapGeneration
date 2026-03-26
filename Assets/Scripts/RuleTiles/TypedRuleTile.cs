using System.Collections.Generic;
using Data;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// A RuleTile that can match specific TileType neighbors (Air, Wall, Floor, …).
///
/// Extensibility contract
/// ----------------------
/// When a new TileType is added to the enum and registered in TileRegistry,
/// the only change needed here is ONE new constant in the Neighbor class:
///
///   public const int Water = NeighborTypeOffset + (int)TileType.Water; // e.g. 6
///
/// RuleMatch never needs to change — it resolves types dynamically via the
/// registry instead of a hardcoded switch.
///
/// How neighbor constants work
/// ---------------------------
/// Unity's built-in constants occupy 1 (This) and 2 (NotThis).
/// Our constants start at TypeOffset (3) and are laid out as:
///
///   neighbor value = TypeOffset + (int)TileType
///
/// This makes the mapping between enum values and neighbor IDs implicit
/// and collision-free.
/// </summary>
[CreateAssetMenu(fileName = "TypedRuleTile", menuName = "Tiles/Typed Rule Tile")]
public class TypedRuleTile : RuleTile<TypedRuleTile.Neighbor>
{
    // ── Neighbor constants ────────────────────────────────────────────────────
    // To add a new tile type: add ONE line below following the same pattern.
    // Do NOT edit RuleMatch.
    // Offset chosen to avoid built-in This=1 / NotThis=2.
    // Not placed inside Neighbor — the Rule Tile editor reflects all public const int
    // in that class and would treat a duplicate value as a stuck state.
    private const int NeighborTypeOffset = 3;

    public class Neighbor : RuleTile.TilingRule.Neighbor
    {
        public const int Air   = 3; // NeighborTypeOffset + (int)TileType.Air   (0)
        public const int Wall  = 4; // NeighborTypeOffset + (int)TileType.Wall  (1)
        public const int Floor = 5; // NeighborTypeOffset + (int)TileType.Floor (2)
    }

    // ── Fields ────────────────────────────────────────────────────────────────
    [Tooltip("The shared TileRegistry asset. Assign in the Inspector.")]
    public TileRegistry tileRegistry;

    // Reverse lookup: TileBase asset → TileType.
    // Built lazily from tileRegistry; reset on OnEnable so asset reloads are safe.
    private Dictionary<TileBase, TileType> _reverseRegistry;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private void OnEnable() => _reverseRegistry = null;

    // ── Core matching ─────────────────────────────────────────────────────────
    public override bool RuleMatch(int neighbor, TileBase tile)
    {
        // Delegate built-in This / NotThis to the base class
        if (neighbor < NeighborTypeOffset || tileRegistry == null)
            return base.RuleMatch(neighbor, tile);

        // Decode which TileType this rule cell is targeting
        var targetType = (TileType)(neighbor - NeighborTypeOffset);

        // A null TileBase on the Tilemap means the cell is empty → Air
        if (tile == null)
            return targetType == TileType.Air;

        // Look up the actual type of the neighbor tile via reverse registry
        return BuildReverseRegistry().TryGetValue(tile, out var actualType)
               && actualType == targetType;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private Dictionary<TileBase, TileType> BuildReverseRegistry()
    {
        if (_reverseRegistry != null) return _reverseRegistry;

        _reverseRegistry = new Dictionary<TileBase, TileType>();

        // Enumerate every known TileType; TryGetAs skips unregistered entries silently
        foreach (TileType type in System.Enum.GetValues(typeof(TileType)))
        {
            if (tileRegistry.TryGetAs<TileBase>(type, out var tileBase))
                _reverseRegistry[tileBase] = type;
        }

        return _reverseRegistry;
    }
}
