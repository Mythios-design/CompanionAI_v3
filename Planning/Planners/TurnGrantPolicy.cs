using System;
using System.Reflection;
using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.Utility;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Rules for abilities that give an ally a bonus turn (Bring It Down!, Move, Move, Move! and others with
    /// ContextActionStartAdditionalTurn). A grant is only worth it when the ally can use what it gives:
    ///  - AP but no MP (Bring It Down!): the ally must be able to hit an enemy from where they stand.
    ///  - MP but no AP (Move, Move, Move!): wasted on a melee ally who already has an enemy in reach.
    /// Checked when planning and again just before the cast.
    /// </summary>
    public static class TurnGrantPolicy
    {
        // Not public on the game type
        private static readonly FieldInfo GrantedAPField = AccessTools.Field(typeof(ContextActionStartAdditionalTurn), "GrantedAP");
        private static readonly FieldInfo GrantedMPField = AccessTools.Field(typeof(ContextActionStartAdditionalTurn), "GrantedMP");
        private static readonly System.Collections.Generic.HashSet<string> _unreadableLogged = new System.Collections.Generic.HashSet<string>();

        public static bool IsWorthGranting(AbilityData grant, BaseUnitEntity target, out string why)
        {
            why = null;
            if (grant == null || target == null) return true;
            try
            {
                if (!AbilityDatabase.IsTurnGrantAbility(grant)) return true;
                if (!TryGetGrantedPoints(grant, out int ap, out int mp))
                {
                    // values not readable: don't block
                    if (_unreadableLogged.Add(grant.Name ?? ""))
                        Log.Planning.Info($"[TurnGrantPolicy] {grant.Name}: granted AP/MP not readable - not checking this grant");
                    return true;
                }

                if (ap > 0 && mp <= 0)
                {
                    if (CanHitFromCurrentPosition(target)) return true;
                    why = $"{target.CharacterName} can't hit any enemy from where they stand (bonus turn gives {ap} AP, no MP)";
                    return false;
                }

                if (mp > 0 && ap <= 0)
                {
                    if (CombatAPI.GetEffectiveRangePreference(target) == RangePreference.PreferMelee && CanHitFromCurrentPosition(target))
                    {
                        why = $"{target.CharacterName} already has an enemy in melee reach (bonus turn gives {mp} MP, no AP)";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[TurnGrantPolicy] check failed for {grant?.Name} -> {target?.CharacterName}");
            }
            return true;
        }

        /// <summary>AP and MP the bonus turn gives. False when the ability has no turn grant or the values aren't simple numbers.</summary>
        private static bool TryGetGrantedPoints(AbilityData grant, out int ap, out int mp)
        {
            ap = 0;
            mp = 0;
            var actions = BlueprintCache.GetCachedRunAction(grant.Blueprint)?.Actions?.Actions;
            if (actions == null) return false;
            foreach (var action in actions)
            {
                if (!(action is ContextActionStartAdditionalTurn turn)) continue;
                var apValue = GrantedAPField?.GetValue(turn) as ContextValue;
                var mpValue = GrantedMPField?.GetValue(turn) as ContextValue;
                if (apValue == null || mpValue == null) return false;
                if (!apValue.IsValueSimple || !mpValue.IsValueSimple) return false;
                ap = apValue.Value;
                mp = mpValue.Value;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Can any of the unit's weapon attacks reach an enemy from its current tile? Ignores cooldowns: the game
        /// resets them for the bonus turn, and the ally may have attacked already this round.
        /// </summary>
        private static bool CanHitFromCurrentPosition(BaseUnitEntity unit)
        {
            var node = unit.Position.GetNearestNodeXZ() as CustomGridNodeBase;
            var raw = unit.Abilities?.RawFacts;
            if (node == null || raw == null) return false;
            var enemies = CombatAPI.GetEnemies(unit);
            if (enemies.Count == 0) return false;

            foreach (var fact in raw)
            {
                var ability = fact?.Data;
                if (ability?.Weapon == null || ability.Blueprint == null || !ability.Blueprint.CanTargetEnemies) continue;
                if (AbilityDatabase.IsReload(ability) || CombatHelpers.IsGrenadeOrExplosive(ability)) continue;
                if (CombatAPI.CountHittableEnemiesFromPosition(unit, node, enemies, ability) > 0) return true;
            }
            return false;
        }
    }
}
