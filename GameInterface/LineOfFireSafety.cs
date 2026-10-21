using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.RuleSystem;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components.ProjectileAttack;
using Kingmaker.View.Covers;
using UnityEngine;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// Friendly fire along a single shot's line (AbilityProjectileAttackLine.CalculateHits in the game):
    ///  - Cover: when the shot hits the target's cover, the unit giving that cover takes it. An ally standing between
    ///    the shooter and the target as its cover is at risk.
    ///  - Overpenetration: the shot's line starts at the target and runs on to the weapon's range
    ///    (AbilityProjectileAttack.CollectNodes). If the weapon's penetration beats the target's armour
    ///    (RuleCalculateOverpenetration), the shot carries on and rolls against every unit on the rest of the line.
    /// Only for single-projectile ranged weapon attacks: areas, scatter and burst have their own checks.
    /// </summary>
    public static class LineOfFireSafety
    {
        public static bool AppliesTo(AbilityData ability)
            => ability?.Weapon != null && !ability.IsMelee && !ability.IsScatter && !ability.IsBurstAttack
               && ability.Blueprint?.PatternSettings == null;

        /// <summary>Party members (not immune to friendly fire) the shot from fromPosition at target can hit besides the target.</summary>
        public static int AlliesAtRisk(AbilityData ability, Vector3 fromPosition, BaseUnitEntity caster, BaseUnitEntity target,
            List<BaseUnitEntity> allies, out string who)
        {
            who = null;
            if (!AppliesTo(ability) || caster == null || target == null || allies == null) return 0;
            try
            {
                // Cheap first pass: nobody near the shot's line (shooter to target and on to the weapon's range)
                if (!allies.Exists(a => IsAlly(a, caster, target, allies) && NearShotLine(fromPosition, target.Position, a.Position, ability.RangeCells)))
                    return 0;

                var fromNode = fromPosition.GetNearestNodeXZ() as CustomGridNodeBase;
                var targetNode = target.Position.GetNearestNodeXZ() as CustomGridNodeBase;
                if (fromNode == null || targetNode == null) return 0;

                var atRisk = new List<BaseUnitEntity>();

                // Cover between the shooter and the target
                var los = LosCalculations.GetWarhammerLos(fromNode, caster.SizeRect, targetNode, target.SizeRect);
                if (los.ObstacleEntity is BaseUnitEntity cover && IsAlly(cover, caster, target, allies))
                    atRisk.Add(cover);

                // Behind the target, when the shot overpenetrates it
                var behind = AlliesOnLineBehind(ability, fromNode, caster, target, allies);
                if (behind.Count > 0 && Overpenetrates(ability, caster, target))
                    foreach (var a in behind)
                        if (!atRisk.Contains(a)) atRisk.Add(a);

                if (atRisk.Count > 0)
                    who = string.Join(", ", atRisk.ConvertAll(a => a.CharacterName));
                return atRisk.Count;
            }
            catch (Exception ex)
            {
                Log.Engine.Error(ex, $"[LineOfFire] check failed: {ability.Name} -> {target.CharacterName}");
                return 0;
            }
        }

        private static List<BaseUnitEntity> AlliesOnLineBehind(AbilityData ability, CustomGridNodeBase fromNode,
            BaseUnitEntity caster, BaseUnitEntity target, List<BaseUnitEntity> allies)
        {
            var found = new List<BaseUnitEntity>();
            int range = ability.RangeCells;
            if (range <= 0) return found;
            var line = AbilityProjectileAttack.CollectNodes(fromNode, target, range);
            foreach (var node in line.Nodes)
            {
                if (node == null) continue;
                foreach (var ally in allies)
                {
                    if (found.Contains(ally) || !IsAlly(ally, caster, target, allies)) continue;
                    foreach (var occ in ally.GetOccupiedNodes())
                        if (occ == node) { found.Add(ally); break; }
                }
            }
            return found;
        }

        /// <summary>The weapon's penetration left after this target's armour (the game's own rule), above zero.</summary>
        private static bool Overpenetrates(AbilityData ability, BaseUnitEntity caster, BaseUnitEntity target)
        {
            var rule = new RuleCalculateOverpenetration(caster, target, ability);
            Rulebook.Trigger(rule);
            return rule.OverpenetrationDamage != null && rule.OverpenetrationDamage.OverpenetrationFactorPercents > 0;
        }

        /// <summary>Within 2 tiles of the line from the shooter through the target, up to rangeCells from the shooter.</summary>
        private static bool NearShotLine(Vector3 from, Vector3 target, Vector3 ally, int rangeCells)
        {
            Vector3 dir = target - from;
            dir.y = 0f;
            float length = dir.magnitude;
            if (length < 0.01f) return false;
            dir /= length;
            Vector3 toAlly = ally - from;
            toAlly.y = 0f;
            float along = Vector3.Dot(toAlly, dir);
            if (along <= 0f || CombatAPI.MetersToTiles(along) > rangeCells + 1) return false;
            float across = (toAlly - dir * along).magnitude;
            return CombatAPI.MetersToTiles(across) <= 2f;
        }

        private static bool IsAlly(BaseUnitEntity unit, BaseUnitEntity caster, BaseUnitEntity target, List<BaseUnitEntity> allies)
            => unit != null && unit != caster && unit != target && unit.IsConscious && !unit.LifeState.IsDead
               && unit.IsInPlayerParty && allies.Contains(unit) && !CombatAPI.IsImmuneToFriendlyFire(unit);
    }
}
