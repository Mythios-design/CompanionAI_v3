using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// MP abilities (Reckless Rush and the like) cast before moving, so the unit reaches the enemy this turn. The
    /// analyzer files every MP-restoring ability as Run and Gun, which the plans only cast after the first action.
    /// An ability that needs an attack first (the Soldier's Run and Gun) fails its caster check here and is left to
    /// that post-action step.
    ///  - Rush to engage: no attack possible on current MP, but a move-and-attack is with the extra MP.
    ///  - Rush to close in: melee, no attack possible even with it, and the rush gets meaningfully closer on foot.
    ///  - After the first action: only when a walking route toward the enemy exists.
    /// </summary>
    public static class RushPlanner
    {
        /// <summary>A walk this much closer (tiles) is worth the rush when no attack is possible this turn.</summary>
        private const float MinCloserTiles = 2f;

        /// <summary>An MP ability the unit can cast now and hasn't planned yet, with the MP it gives; null if none.</summary>
        public static AbilityData FindUsable(Situation situation, List<PlannedAction> actions, out float mp)
        {
            mp = 0f;
            var candidates = new List<AbilityData>();
            if (situation.MPBuffAbility != null) candidates.Add(situation.MPBuffAbility);
            if (situation.RunAndGunAbility != null) candidates.Add(situation.RunAndGunAbility);
            if (situation.PostFirstActionAbilities != null) candidates.AddRange(situation.PostFirstActionAbilities);

            var self = new TargetWrapper(situation.Unit);
            foreach (var ability in candidates)
            {
                if (ability == null || IsPlanned(actions, ability)) continue;
                float recovery = CombatAPI.GetAbilityMPRecovery(ability);
                if (recovery <= 0f) continue;
                if (!CombatAPI.CanUseAbilityOn(ability, self, out _)) continue;
                mp = recovery;
                return ability;
            }
            return null;
        }

        /// <summary>Keeps the ability out of the plan's later MP and post-action steps.</summary>
        public static void MarkPlanned(Situation situation, AbilityData ability)
        {
            string guid = Guid(ability);
            if (guid == null) return;
            if (Guid(situation.MPBuffAbility) == guid)
            {
                situation.MPBuffAbility = null;
                situation.MPBuffExpectedRecovery = 0f;
            }
            if (Guid(situation.RunAndGunAbility) == guid) situation.RunAndGunAbility = null;
            situation.PostFirstActionAbilities?.RemoveAll(a => Guid(a) == guid);
            situation.AvailableBuffs?.RemoveAll(a => Guid(a) == guid);
        }

        /// <summary>AP of the cheapest attack the unit has; 1 when it has none listed.</summary>
        public static float CheapestAttackCost(Situation situation)
        {
            float best = float.MaxValue;
            foreach (var attack in situation.AvailableAttacks ?? new List<AbilityData>())
            {
                if (attack == null) continue;
                float cost = CombatAPI.GetAbilityAPCost(attack);
                if (cost < best) best = cost;
            }
            return best == float.MaxValue ? 1f : best;
        }

        /// <summary>
        /// Rush to close in: a melee unit with no attack this turn (even with the rush) casts it before walking when
        /// a walking route exists and the rush ends at least MinCloserTiles nearer the enemy.
        /// </summary>
        public static PlannedAction PlanCloseIn(Situation situation, List<PlannedAction> actions, ref float remainingAP,
            ref float remainingMP, string roleName)
        {
            if (situation.HasHittableEnemies || !situation.HasLivingEnemies || situation.PrefersRanged) return null;
            if (actions.Exists(a => a.Type == ActionType.Move)) return null;
            var target = situation.NearestEnemy;
            if (target == null) return null;

            var rush = FindUsable(situation, actions, out float mp);
            if (rush == null) return null;
            float cost = CombatAPI.GetAbilityAPCost(rush);
            if (cost > remainingAP + 0.01f) return null;

            var unit = situation.Unit;
            float mpNow = Mathf.Max(remainingMP, 0f);
            var withRush = MovementAPI.FindBestApproachPosition(unit, target, mpNow + mp);
            if (withRush == null)
            {
                Log.Planning.Info($"[{roleName}] {rush.Name} held: no walking route toward {target.CharacterName}");
                return null;
            }
            var withoutRush = mpNow > 0f ? MovementAPI.FindBestApproachPosition(unit, target, mpNow) : null;
            float endNow = Tiles(withoutRush?.Position ?? unit.Position, target.Position);
            float endRush = Tiles(withRush.Position, target.Position);
            if (endRush > endNow - MinCloserTiles)
            {
                Log.Planning.Info($"[{roleName}] {rush.Name} held: ends {endRush:F0} tiles from {target.CharacterName}, " +
                    $"{endNow:F0} without it");
                return null;
            }

            remainingAP -= cost;
            remainingMP = mpNow + mp;
            MarkPlanned(situation, rush);
            Log.Planning.Info($"[{roleName}] {rush.Name} to close in: +{mp:F0} MP, ends {endRush:F0} tiles from " +
                $"{target.CharacterName} instead of {endNow:F0}");
            return PlannedAction.Buff(rush, unit, $"{rush.Name} to close in", cost);
        }

        /// <summary>
        /// For an MP ability planned after the first action with no attack planned: its MP can only be used to walk
        /// toward the enemy, so there must be a walking route. Null when it's worth casting, else the reason.
        /// </summary>
        public static string WhyNoApproach(Situation situation, AbilityData ability, bool attackPlanned)
        {
            if (attackPlanned || situation.HasHittableEnemies) return null;
            float mp = CombatAPI.GetAbilityMPRecovery(ability);
            var target = situation.NearestEnemy;
            if (mp <= 0f || target == null) return null;
            var approach = MovementAPI.FindBestApproachPosition(situation.Unit, target, situation.CurrentMP + mp);
            return approach == null ? $"no walking route toward {target.CharacterName}" : null;
        }

        private static bool IsPlanned(List<PlannedAction> actions, AbilityData ability)
        {
            string guid = Guid(ability);
            return actions != null && actions.Exists(a => Guid(a?.Ability) == guid);
        }

        private static float Tiles(Vector3 a, Vector3 b) => CombatAPI.MetersToTiles(Vector3.Distance(a, b));

        private static string Guid(AbilityData a) => a?.Blueprint?.AssetGuid?.ToString();
    }
}
