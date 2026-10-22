using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker;
using Kingmaker.AI;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using UnityEngine;
using Warhammer.SpaceCombat.AI;
using Warhammer.SpaceCombat.AI.BehaviourTrees;
using Warhammer.SpaceCombat.Blueprints;
using CompanionAI_v3.Core;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// The player's ship in space combat, run by the game's own starship AI (Ship Combat AI setting).
    /// The player ship has no BlueprintStarshipBrain, and the game's scoring returns 0 for every path without one,
    /// so the ship would do nothing. These patches stand in for the brain, only for a delegated player ship
    /// without one (TurnOrchestrator.IsShipAIDelegated); enemy ships and wingmen keep their own brains.
    ///  1. Destination: best at 3 tiles from the nearest capital ship (the brain default).
    ///  2. Trajectory: destination score plus, for each ability, its best value anywhere on the path; paths
    ///     through a tile the ship can't stop on score 0, and ending in a meteor stream costs 100. Both match
    ///     what the game does with a brain.
    ///  3. Ability value per target (ShipTargetValue): the game uses it both to pick the path and, when
    ///     firing, to pick the first enemy with the best value.
    /// </summary>
    [HarmonyPatch]
    public static class ShipAIPatch
    {
        /// <summary>The game's default BlueprintStarshipBrain.FearOfMeteors, used by every brain in the game.</summary>
        private const float MeteorPenalty = 100f;
        private const float DesiredDistance = 3f;

        [HarmonyPatch(typeof(TaskNodeFindBestTrajectory), "CalculateDestinationScore")]
        [HarmonyPrefix]
        public static bool CalculateDestinationScore_Prefix(DecisionContext context, Vector3 destination, ref float __result)
        {
            if (!UsesStandInBrain(context?.Unit)) return true;
            __result = DestinationScore(context, destination);
            return false;
        }

        [HarmonyPatch(typeof(TaskNodeFindBestTrajectory), "CalculateTrajectoryScore")]
        [HarmonyPrefix]
        public static bool CalculateTrajectoryScore_Prefix(SpaceCombatDecisionContext context,
            HashSet<ShipPath.DirectionalPathNode> reachableTiles, ShipPath.DirectionalPathNode targetNode, ref float __result)
        {
            if (!UsesStandInBrain(context?.Unit)) return true;
            __result = TrajectoryScore(context, targetNode);
            return false;
        }

        [HarmonyPatch(typeof(PartUnitBrain), nameof(PartUnitBrain.GetAbilityValue))]
        [HarmonyPostfix]
        public static void GetAbilityValue_Postfix(PartUnitBrain __instance, AbilityData ability, MechanicEntity target, ref int __result)
        {
            if (!UsesStandInBrain(__instance?.Owner as BaseUnitEntity)) return;
            __result = ShipTargetValue(ability, target);
        }

        private static bool UsesStandInBrain(BaseUnitEntity unit)
            => unit != null && !(unit.Brain?.Blueprint is BlueprintStarshipBrain) && TurnOrchestrator.IsShipAIDelegated(unit);

        private static float TrajectoryScore(SpaceCombatDecisionContext context, ShipPath.DirectionalPathNode targetNode)
        {
            var best = new Dictionary<BlueprintAbility, int>();
            for (var node = targetNode; node != null; node = node.parent)
            {
                // The ship would stop short of a tile it can't stand on and lose the rest of its plan.
                if (!node.canStand) return 0f;
                foreach (var ability in context.Unit.Abilities.RawFacts)
                {
                    int value = context.AbilityValueCache.GetValue(node, ability);
                    if (!best.TryGetValue(ability.Blueprint, out int old) || old < value)
                        best[ability.Blueprint] = value;
                }
            }

            int total = 0;
            foreach (int v in best.Values) total += v;
            return DestinationScore(context, targetNode.node.Vector3Position) + total - MeteorThreat(targetNode.node);
        }

        private static float MeteorThreat(CustomGridNodeBase node)
        {
            var dangerous = Game.Instance?.MeteorStreamController?.GetDangerousNodes();
            return dangerous != null && node != null && dangerous.ContainsKey(node) ? MeteorPenalty : 0f;
        }

        /// <summary>0.95 / (1 + |distance to the nearest capital ship - 3|), the game's formula with a brain's defaults.</summary>
        private static float DestinationScore(DecisionContext context, Vector3 destination)
        {
            if (context?.Enemies == null) return 0f;
            int minDist = int.MaxValue;
            foreach (var enemy in context.Enemies)
            {
                if (enemy.Entity is StarshipEntity ship && !ship.Blueprint.IsSoftUnit)
                    minDist = Math.Min(minDist, ship.DistanceToInCells(destination));
            }
            if (minDist == int.MaxValue) return 0f;
            return 0.95f / (1f + Math.Abs(minDist - DesiredDistance));
        }

        /// <summary>
        /// What one use of the ability on the target is worth. Capital ships come first, more when their hull is
        /// low or their shields are mostly down. Strike craft and torpedoes (soft units) take 1 damage per hit
        /// unless the ammo is area-effect, and direct torpedo hits on them always miss, so they come last.
        /// Abilities with no ship weapon (ramming, Full Ahead) are kept off soft units and rank lowest otherwise.
        /// </summary>
        internal static int ShipTargetValue(AbilityData ability, MechanicEntity target)
        {
            if (!(target is StarshipEntity ship)) return 1;
            var weapon = ability?.StarshipWeapon;

            if (ship.Blueprint.IsSoftUnit)
            {
                if (weapon == null || weapon.Blueprint.WeaponType == StarshipWeaponType.TorpedoTubes) return 0;
                return weapon.Ammo != null && weapon.IsAEAmmo ? 3 : 1;
            }

            if (weapon == null) return 1;
            int value = 4;
            int maxHp = ship.Health.MaxHitPoints;
            if (maxHp > 0)
            {
                float hull = (float)ship.Health.HitPointsLeft / maxHp;
                if (hull < 0.5f) value += 2;
                if (hull < 0.25f) value += 2;
            }
            var shields = ship.Shields;
            if (shields.ShieldsMaxSum > 0 && shields.ShieldsSum * 4 <= shields.ShieldsMaxSum) value += 1;
            return value;
        }
    }
}
