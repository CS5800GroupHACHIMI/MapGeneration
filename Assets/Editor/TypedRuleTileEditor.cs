using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// ── Rule Tile grid: show names instead of raw neighbor numbers ────────────────

[CustomEditor(typeof(TypedRuleTile))]
public class TypedRuleTileEditor : RuleTileEditor
{
    // Lazy lookup: neighbor int value → public const name (e.g. 4 → "Wall")
    private Dictionary<int, string> _constNames;

    private Dictionary<int, string> GetConstNames()
    {
        if (_constNames != null) return _constNames;
        _constNames = new Dictionary<int, string>();
        var fields = typeof(TypedRuleTile.Neighbor).GetFields(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        foreach (var f in fields)
            _constNames[(int)f.GetValue(null)] = f.Name;
        return _constNames;
    }

    public override void RuleOnGUI(Rect rect, Vector3Int position, int neighbor)
    {
        // This / NotThis: keep the default arrow icons
        if (neighbor == RuleTile.TilingRuleOutput.Neighbor.This ||
            neighbor == RuleTile.TilingRuleOutput.Neighbor.NotThis)
        {
            base.RuleOnGUI(rect, position, neighbor);
            return;
        }

        string label = ResolveLabel(neighbor);

        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize   = 10,
            wordWrap   = false,
        };

        // Shrink font until the text fits inside the cell
        var content = new GUIContent(label);
        while (style.fontSize > 6 && style.CalcSize(content).x > rect.width - 2)
            style.fontSize--;

        GUI.Label(rect, content, style);
    }

    // Returns a human-readable label for any neighbor constant.
    private string ResolveLabel(int neighbor)
    {
        const int orGroupOffset = 20;
        const int orGroupCount  = 8;

        if (neighbor >= orGroupOffset && neighbor < orGroupOffset + orGroupCount)
        {
            int idx   = neighbor - orGroupOffset;
            var typed = tile as TypedRuleTile;
            var grp   = typed?.neighborGroups != null && idx < typed.neighborGroups.Length
                        ? typed.neighborGroups[idx] : null;
            string custom = grp?.label ?? "";
            return string.IsNullOrWhiteSpace(custom) ? $"Or{idx}" : custom;
        }

        return GetConstNames().TryGetValue(neighbor, out var name) ? name : neighbor.ToString();
    }
}

// ── Inspector: neighborGroups array — show label as foldout header ────────────

[CustomPropertyDrawer(typeof(TypedRuleTile.NeighborGroup))]
public class NeighborGroupDrawer : PropertyDrawer
{
    private static readonly string[] GroupNames =
    {
        "OrGroup0", "OrGroup1", "OrGroup2", "OrGroup3",
        "OrGroup4", "OrGroup5", "OrGroup6", "OrGroup7",
    };

    public override void OnGUI(Rect pos, SerializedProperty prop, GUIContent _)
    {
        float lineH   = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;

        int    idx       = ExtractIndex(prop.propertyPath);
        string constName = idx >= 0 && idx < GroupNames.Length ? GroupNames[idx] : "OrGroup?";

        var labelProp = prop.FindPropertyRelative("label");
        string custom = labelProp?.stringValue ?? "";
        string header = string.IsNullOrWhiteSpace(custom)
                        ? constName
                        : $"{constName}  ·  {custom}";

        // Foldout header row
        prop.isExpanded = EditorGUI.Foldout(
            new Rect(pos.x, pos.y, pos.width, lineH), prop.isExpanded, header, true);

        if (!prop.isExpanded) return;

        EditorGUI.indentLevel++;
        float y = pos.y + lineH + spacing;

        // "label" text field
        EditorGUI.PropertyField(new Rect(pos.x, y, pos.width, lineH), labelProp);
        y += lineH + spacing;

        // "types" array
        var typesProp = prop.FindPropertyRelative("types");
        float typesH  = EditorGUI.GetPropertyHeight(typesProp, GUIContent.none, true);
        EditorGUI.PropertyField(new Rect(pos.x, y, pos.width, typesH), typesProp, true);

        EditorGUI.indentLevel--;
    }

    public override float GetPropertyHeight(SerializedProperty prop, GUIContent _)
    {
        float lineH   = EditorGUIUtility.singleLineHeight;
        float spacing = EditorGUIUtility.standardVerticalSpacing;

        if (!prop.isExpanded) return lineH;

        return lineH + spacing                                                           // header
             + lineH + spacing                                                           // label field
             + EditorGUI.GetPropertyHeight(prop.FindPropertyRelative("types"),
                                           GUIContent.none, true);                      // types
    }

    // Extracts array index from a path like "neighborGroups.Array.data[2]"
    private static int ExtractIndex(string path)
    {
        int s = path.LastIndexOf('[');
        int e = path.LastIndexOf(']');
        return s >= 0 && e > s && int.TryParse(path.Substring(s + 1, e - s - 1), out int i) ? i : -1;
    }
}
