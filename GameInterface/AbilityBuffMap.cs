using System;
using System.Collections.Generic;
using System.Reflection;
using Kingmaker.ElementsSystem;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.UnitLogic.Mechanics.Actions;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// Finds which buffs an ability puts on its target, so the AI can tell when a buff is already active.
    ///
    /// Many abilities don't apply their buff directly: it sits inside a saving throw result, a condition,
    /// or another wrapper action. Looking only at the top-level actions missed those, so the AI thought the
    /// target never had the buff and recast it every turn. This walks the whole action tree.
    ///
    /// Buffs applied to the caster (ToCaster, or inside "on caster" wrappers) are kept separately: they
    /// are often cooldown or bookkeeping markers rather than the effect the ability is cast for.
    /// </summary>
    public static class AbilityBuffMap
    {
        public sealed class Result
        {
            public readonly List<BlueprintBuff> TargetBuffs = new List<BlueprintBuff>();
            public readonly List<BlueprintBuff> CasterBuffs = new List<BlueprintBuff>();
        }

        private const int MaxDepth = 8;

        private static readonly Dictionary<string, Result> _cache = new Dictionary<string, Result>();
        private static readonly Dictionary<Type, FieldInfo[]> _actionFields = new Dictionary<Type, FieldInfo[]>();
        private static readonly object _lock = new object();

        public static Result Get(BlueprintAbility blueprint)
        {
            if (blueprint == null) return null;
            string guid = blueprint.AssetGuid?.ToString();
            if (string.IsNullOrEmpty(guid)) return null;

            lock (_lock)
            {
                if (_cache.TryGetValue(guid, out var cached)) return cached;
            }

            var result = new Result();
            try
            {
                var components = blueprint.ComponentsArray;
                if (components != null)
                {
                    foreach (var component in components)
                    {
                        if (component == null) continue;
                        WalkObject(component, casterScope: false, depth: 0, result);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Engine.Error(ex, $"[AbilityBuffMap] Failed to read buffs for {blueprint.name}");
            }

            lock (_lock)
            {
                _cache[guid] = result;
            }
            return result;
        }

        /// <summary>
        /// Buffs to check on the target. For abilities that can only target the caster, caster-side
        /// buffs are used when nothing else is found.
        /// </summary>
        public static List<BlueprintBuff> GetBuffsToCheck(BlueprintAbility blueprint)
        {
            var result = Get(blueprint);
            if (result == null) return null;
            if (result.TargetBuffs.Count > 0) return result.TargetBuffs;

            bool selfOnly = blueprint.CanTargetSelf && !blueprint.CanTargetFriends
                && !blueprint.CanTargetEnemies && !blueprint.CanTargetPoint;
            return selfOnly ? result.CasterBuffs : result.TargetBuffs;
        }

        public static void Clear()
        {
            lock (_lock) { _cache.Clear(); }
        }

        private static void WalkObject(object node, bool casterScope, int depth, Result result)
        {
            if (node == null || depth > MaxDepth) return;

            if (node is ContextActionApplyBuff applyBuff)
            {
                var buff = applyBuff.Buff;
                if (buff != null)
                {
                    var list = (casterScope || applyBuff.ToCaster) ? result.CasterBuffs : result.TargetBuffs;
                    if (!list.Contains(buff)) list.Add(buff);
                }
            }

            bool childCasterScope = casterScope || node is ContextActionOnContextCaster;

            foreach (var field in GetActionFields(node.GetType()))
            {
                object value;
                try { value = field.GetValue(node); }
                catch { continue; }  // a field that can't be read just has nothing for us
                if (value == null) continue;

                switch (value)
                {
                    case ActionList list:
                        WalkActions(list.Actions, childCasterScope, depth + 1, result);
                        break;
                    case ActionList[] lists:
                        foreach (var l in lists) if (l != null) WalkActions(l.Actions, childCasterScope, depth + 1, result);
                        break;
                    case GameAction[] actions:
                        WalkActions(actions, childCasterScope, depth + 1, result);
                        break;
                    case GameAction action:
                        WalkObject(action, childCasterScope, depth + 1, result);
                        break;
                }
            }
        }

        private static void WalkActions(GameAction[] actions, bool casterScope, int depth, Result result)
        {
            if (actions == null) return;
            foreach (var action in actions)
                WalkObject(action, casterScope, depth, result);
        }

        private static FieldInfo[] GetActionFields(Type type)
        {
            lock (_lock)
            {
                if (_actionFields.TryGetValue(type, out var cached)) return cached;
            }

            var found = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var ft = f.FieldType;
                    if (ft == typeof(ActionList) || ft == typeof(ActionList[])
                        || typeof(GameAction).IsAssignableFrom(ft) || ft == typeof(GameAction[])
                        || (ft.IsArray && typeof(GameAction).IsAssignableFrom(ft.GetElementType())))
                    {
                        found.Add(f);
                    }
                }
            }

            var arr = found.ToArray();
            lock (_lock) { _actionFields[type] = arr; }
            return arr;
        }
    }
}
