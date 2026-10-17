using System;
using System.Collections.Generic;
using System.Reflection;
using Kingmaker.Blueprints;
using Kingmaker.ElementsSystem;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Mechanics.Actions;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Data
{
    /// <summary>
    /// Finds the extra-turn action (ContextActionStartAdditionalTurn) in an ability. It isn't always a top-level action:
    /// variants and talents can put it inside conditionals, other action lists or a buff the ability applies, so the
    /// search walks every action list it can reach. Abilities known to grant a turn are listed by ID as a fallback.
    /// </summary>
    public static class TurnGrantDetector
    {
        /// <summary>Known turn grants: AP and MP the extra turn gives (from the game's description).</summary>
        private static readonly Dictionary<string, (int ap, int mp)> KnownGrants = new Dictionary<string, (int, int)>
        {
            { "5bfccf14d8604ae1a041fb347fb8f19f", (2, 0) },  // Bring It Down! (LeaderKeepFightingAbility)
        };

        private const int MaxDepth = 8;
        private static readonly Dictionary<string, ContextActionStartAdditionalTurn> _cache = new Dictionary<string, ContextActionStartAdditionalTurn>();
        private static readonly Dictionary<Type, FieldInfo[]> _listFields = new Dictionary<Type, FieldInfo[]>();

        /// <summary>The extra-turn action in the ability, or null if none was found.</summary>
        public static ContextActionStartAdditionalTurn Find(BlueprintAbility bp)
        {
            string guid = bp?.AssetGuid?.ToString();
            if (guid == null) return null;
            if (_cache.TryGetValue(guid, out var cached)) return cached;

            ContextActionStartAdditionalTurn found = null;
            try
            {
                var visited = new HashSet<object>();
                found = SearchComponents(bp, visited, 0);
            }
            catch (Exception ex)
            {
                Log.Persistence.Error(ex, $"[TurnGrantDetector] search failed for {bp.name}");
            }
            _cache[guid] = found;
            return found;
        }

        /// <summary>AP and MP for a known turn grant when the action can't be read.</summary>
        public static bool TryGetKnown(BlueprintAbility bp, out int ap, out int mp)
        {
            ap = 0; mp = 0;
            string guid = bp?.AssetGuid?.ToString();
            if (guid == null || !KnownGrants.TryGetValue(guid, out var v)) return false;
            ap = v.ap; mp = v.mp;
            return true;
        }

        public static bool IsTurnGrant(BlueprintAbility bp) => Find(bp) != null || TryGetKnown(bp, out _, out _);

        private static ContextActionStartAdditionalTurn SearchComponents(BlueprintScriptableObject bp, HashSet<object> visited, int depth)
        {
            if (bp == null || depth > MaxDepth || !visited.Add(bp)) return null;
            var components = bp.ComponentsArray;
            if (components == null) return null;
            foreach (var component in components)
            {
                var hit = SearchObject(component, visited, depth + 1);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>Looks through every ActionList field of the object (a component or an action).</summary>
        private static ContextActionStartAdditionalTurn SearchObject(object obj, HashSet<object> visited, int depth)
        {
            if (obj == null || depth > MaxDepth) return null;
            foreach (var field in ListFields(obj.GetType()))
            {
                var value = field.GetValue(obj);
                if (value is ActionList list)
                {
                    var hit = SearchList(list, visited, depth + 1);
                    if (hit != null) return hit;
                }
                else if (value is ActionList[] lists)
                {
                    foreach (var l in lists)
                    {
                        var hit = SearchList(l, visited, depth + 1);
                        if (hit != null) return hit;
                    }
                }
            }
            return null;
        }

        private static ContextActionStartAdditionalTurn SearchList(ActionList list, HashSet<object> visited, int depth)
        {
            if (list?.Actions == null || depth > MaxDepth || !visited.Add(list)) return null;
            foreach (var action in list.Actions)
            {
                if (action == null) continue;
                if (action is ContextActionStartAdditionalTurn turn) return turn;
                // A buff the ability applies can grant the turn from its own components
                if (action is ContextActionApplyBuff applyBuff)
                {
                    var hit = SearchComponents(applyBuff.Buff, visited, depth + 1);
                    if (hit != null) return hit;
                }
                var nested = SearchObject(action, visited, depth + 1);
                if (nested != null) return nested;
            }
            return null;
        }

        private static FieldInfo[] ListFields(Type type)
        {
            if (_listFields.TryGetValue(type, out var fields)) return fields;
            var result = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.FieldType == typeof(ActionList) || f.FieldType == typeof(ActionList[])) result.Add(f);
                }
            }
            fields = result.ToArray();
            _listFields[type] = fields;
            return fields;
        }
    }
}
