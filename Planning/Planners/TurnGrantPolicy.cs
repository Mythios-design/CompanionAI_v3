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
                // Each value is null when it isn't a plain number (e.g. scales with a stat).
                ReadGrantedPoints(grant, out int? ap, out int? mp, out string detail);

                // AP only (Bring It Down!): ranged allies must be able to shoot an enemy from where they stand;
                // melee allies must be engaged, with an enemy within reach of a melee weapon attack.
                if (ap > 0 && mp == 0)
                {
                    bool melee = IsMeleeCharacter(target);
                    if (CanHitFromCurrentPosition(target, meleeOnly: melee)) return true;
                    why = melee
                        ? $"{target.CharacterName} is a melee character and not engaged (bonus turn gives {ap} AP, no MP)"
                        : $"{target.CharacterName} can't hit any enemy from where they stand (bonus turn gives {ap} AP, no MP)";
                    return false;
                }

                // Movement only: known to give no AP. The MP amount doesn't matter here.
                if (ap == 0 && mp != 0)
                {
                    if (CombatAPI.GetEffectiveRangePreference(target) == RangePreference.PreferMelee && CanHitFromCurrentPosition(target))
                    {
                        why = $"{target.CharacterName} already has an enemy in melee reach (bonus turn gives movement, no AP)";
                        return false;
                    }
                    return true;
                }

                if ((ap == null || mp == null) && _unreadableLogged.Add(grant.Name ?? ""))
                    Log.Planning.Info($"[TurnGrantPolicy] {grant.Name}: granted points not fully readable ({detail}) - not checking this grant");
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[TurnGrantPolicy] check failed for {grant?.Name} -> {target?.CharacterName}");
            }
            return true;
        }

        /// <summary>AP and MP the bonus turn gives, each null when it isn't a plain number.</summary>
        private static void ReadGrantedPoints(AbilityData grant, out int? ap, out int? mp, out string detail)
        {
            ap = null;
            mp = null;
            detail = "no turn grant action";
            var turn = TurnGrantDetector.Find(grant.Blueprint);
            if (turn != null)
            {
                var apValue = GrantedAPField?.GetValue(turn) as ContextValue;
                var mpValue = GrantedMPField?.GetValue(turn) as ContextValue;
                if (apValue != null && apValue.IsValueSimple) ap = apValue.Value;
                if (mpValue != null && mpValue.IsValueSimple) mp = mpValue.Value;
                detail = $"AP={(ap?.ToString() ?? apValue?.ValueType.ToString() ?? "missing")}, MP={(mp?.ToString() ?? mpValue?.ValueType.ToString() ?? "missing")}";
            }

            // Fall back to the known values for abilities whose extra-turn action can't be found or read
            if ((ap == null || mp == null) && TurnGrantDetector.TryGetKnown(grant.Blueprint, out int knownAP, out int knownMP))
            {
                ap = ap ?? knownAP;
                mp = mp ?? knownMP;
                detail = $"AP={ap}, MP={mp} (known values)";
            }
        }

        /// <summary>Set to prefer melee, or holding a melee weapon in the main hand.</summary>
        private static bool IsMeleeCharacter(BaseUnitEntity unit)
        {
            if (CombatAPI.GetEffectiveRangePreference(unit) == RangePreference.PreferMelee) return true;
            var weapon = unit.Body?.PrimaryHand?.MaybeWeapon;
            return weapon?.Blueprint != null && weapon.Blueprint.IsMelee;
        }

        /// <summary>
        /// Can any of the unit's weapon attacks reach an enemy from its current tile? Ignores cooldowns: the game
        /// resets them for the bonus turn, and the ally may have attacked already this round. With meleeOnly, only
        /// melee weapon attacks count (an enemy within melee reach = engaged).
        /// </summary>
        private static bool CanHitFromCurrentPosition(BaseUnitEntity unit, bool meleeOnly = false)
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
                // Gap closers move the unit and are often held back (e.g. Death From Above kept in reserve), so they
                // don't count as hitting from where the unit stands
                if (AbilityDatabase.IsGapCloser(ability)) continue;
                if (meleeOnly && !ability.IsMelee) continue;
                if (CombatAPI.CountHittableEnemiesFromPosition(unit, node, enemies, ability) > 0) return true;
            }
            return false;
        }
    }
}
