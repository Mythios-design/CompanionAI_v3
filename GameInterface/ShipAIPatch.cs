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
    /// without one (UsesStandInBrain); enemy ships and wingmen keep their own brains.
    ///  1. Destination: best at 3 tiles from the nearest capital ship (the brain default).
    ///  2. Trajectory: destination score plus, for each ability, its best value anywhere on the path, plus the
    ///     angle score toward each enemy capital ship that can reach us next turn: SternBonus behind it,
    ///     -FlankPenalty on its flank, -BowPenalty in front of it (the game's "stay behind" brain option sized
    ///     against shot values). A path through a tile the ship can't stop on scores 0 and ending in a meteor
    ///     stream costs 100, as with a brain. The game only takes a path scoring above 0, so every other path
    ///     starts from a base that outweighs the worst angle score.
    ///  3. Ability value per target (ShipTargetValue). The game uses it to pick the path and, when firing, shoots
    ///     the first enemy whose value matches the planned one, so values are frozen for the ship's turn. Weapons
    ///     are worth more against the turn's focus ship (ShipFocus), so paths that bring more guns onto it win.
    /// The ship's post abilities and ultimates are cast by ShipPostPlanner (ShipPostPatch).
    /// </summary>
    [HarmonyPatch]
    public static class ShipAIPatch
    {
        /// <summary>The game's default BlueprintStarshipBrain.FearOfMeteors, used by every brain in the game.</summary>
        private const float MeteorPenalty = 100f;
        private const float DesiredDistance = 3f;
        /// <summary>Ending within 90 degrees of an enemy capital ship's bow, where it needs no turn to fire.</summary>
        private const float BowPenalty = 3f;
        /// <summary>Ending 90 to 135 degrees off its bow.</summary>
        private const float FlankPenalty = 1f;
        /// <summary>Ending more than 135 degrees off its bow, where it needs most of its turn to bring guns to bear.</summary>
        private const float SternBonus = 3f;

        private const string SHIELD_PULSE = "46c2d6cde7eb46c2a0b464764d670b72";       // MA_ShieldGen_ShieldsPulse (spends 25% of our shields)
        private const string BOARDING_TEAM = "738ca17f7a52449c9f9fc16bb59319d1";      // UA_Bridge_BoardingTeam_Ability
        private const string EMPYREAN_STORM = "f9b7259637d54f04bfa36753ce00a2b8";     // UA_Astropatic_EmpyreanStorm_Ability
        private const string AELDARI_HOLOFIELD = "4c893bc4be2e434b8b77c3c45fa2f308";  // Shield Pulse strips these
        private const string DRUKHARI_SHADOWFIELD = "3911cbfc8a554c7aa4f9ad417c280073";
        private const string NECRON_FALLING_APART = "175d2f02c141491baa8d38105ce62ee2";

        /// <summary>Ability values for this ship turn, key ability GUID | target id.</summary>
        private static readonly Dictionary<string, int> _turnValues = new Dictionary<string, int>();
        /// <summary>Enemy reach (weapon range + movement) for this ship turn, key enemy id.</summary>
        private static readonly Dictionary<string, int> _enemyReach = new Dictionary<string, int>();

        /// <summary>Called at the start of the delegated ship's turn.</summary>
        public static void ResetTurn()
        {
            _turnValues.Clear();
            _enemyReach.Clear();
            ShipFocus.Reset();
        }

        /// <summary>Forgets the frozen ability values, so they are worked out again (the focus ship changed).</summary>
        public static void ClearTurnValues() => _turnValues.Clear();

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
            string key = ShipCombatInfo.Guid(ability) + "|" + (target?.UniqueId ?? "none");
            if (!_turnValues.TryGetValue(key, out int value))
            {
                value = ShipTargetValue(ability, target);
                _turnValues[key] = value;
            }
            __result = value;
        }

        public static bool UsesStandInBrain(BaseUnitEntity unit)
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
            Vector3 end = targetNode.node.Vector3Position;
            var enemies = ShipCombatInfo.CapitalEnemies(context);
            float pathBase = 1f + BowPenalty * enemies.Count;
            return pathBase + DestinationScore(context, end) + total - MeteorThreat(targetNode.node) + EnemyAngleScore(enemies, end);
        }

        private static float MeteorThreat(CustomGridNodeBase node)
        {
            var dangerous = Game.Instance?.MeteorStreamController?.GetDangerousNodes();
            return dangerous != null && node != null && dangerous.ContainsKey(node) ? MeteorPenalty : 0f;
        }

        /// <summary>
        /// Where we end relative to each enemy capital ship that can reach us next turn. Ships turn slowly, so a ship
        /// we end behind needs most of its turn to bring its guns to bear (the game's TryToStayBehind brain option
        /// weighs the same angles).
        /// </summary>
        private static float EnemyAngleScore(List<StarshipEntity> enemies, Vector3 end)
        {
            float score = 0f;
            foreach (var enemy in enemies)
            {
                if (enemy.DistanceToInCells(end) > EnemyReach(enemy)) continue;
                float angle = ShipCombatInfo.AngleFromBow(enemy, end);
                if (angle < 90f) score -= BowPenalty;
                else if (angle < 135f) score -= FlankPenalty;
                else score += SternBonus;
            }
            return score;
        }

        private static int EnemyReach(StarshipEntity enemy)
        {
            string id = enemy.UniqueId;
            if (!_enemyReach.TryGetValue(id, out int reach))
            {
                reach = ShipCombatInfo.WeaponRange(enemy) + (int)ShipCombatInfo.TurnMovePoints(enemy);
                _enemyReach[id] = reach;
            }
            return reach;
        }

        /// <summary>0.95 / (1 + |distance to the nearest capital ship - 3|), the game's formula with a brain's defaults.</summary>
        private static float DestinationScore(DecisionContext context, Vector3 destination)
        {
            int minDist = int.MaxValue;
            foreach (var ship in ShipCombatInfo.CapitalEnemies(context))
                minDist = Math.Min(minDist, ship.DistanceToInCells(destination));
            if (minDist == int.MaxValue) return 0f;
            return 0.95f / (1f + Math.Abs(minDist - DesiredDistance));
        }

        /// <summary>
        /// What one use of the ability on the target is worth.
        ///  - Weapons: capital ships first, more when their hull is low or their shields are mostly down, and
        ///    ShipFocus.Bonus more against the turn's focus ship. Strike
        ///    craft and torpedoes take 1 damage per hit unless the ammo is area-effect, and direct torpedo hits on
        ///    them always miss, so they come last.
        ///  - Launches (strike craft, wingmen): 1, as the game's AI casts them ahead of the ship whatever the target.
        ///  - No ship weapon otherwise: 0 unless it targets an enemy ship directly (ramming and Swing Run aim at a point, and
        ///    the game's firing step only aims at ships, so they would only pull the path toward shots that never
        ///    happen); otherwise 1 on capital ships, 0 on small craft.
        ///  - Shield Pulse (the version that spends 25% of our shields): only on a ship with an Aeldari holo-field,
        ///    Drukhari shadow-field or Necron falling-apart effect, or while our shields are at half or more.
        ///  - Boarding Team and Empyrean Storm: the ship gets one ultimate per combat, so only on a capital ship
        ///    under half hull.
        /// </summary>
        internal static int ShipTargetValue(AbilityData ability, MechanicEntity target)
        {
            if (!(target is StarshipEntity ship)) return 1;
            var weapon = ability?.StarshipWeapon;
            bool soft = ship.Blueprint.IsSoftUnit;

            if (weapon == null)
            {
                if (ShipCombatInfo.IsLaunch(ability)) return 1;
                if (ability == null || !ability.CanTargetEnemies || soft) return 0;
                string guid = ShipCombatInfo.Guid(ability);
                if (guid == BOARDING_TEAM || guid == EMPYREAN_STORM)
                    return ShipCombatInfo.HullFraction(ship) < 0.5f ? 6 : 0;
                if (guid == SHIELD_PULSE && !ShieldPulsePays(ability, ship)) return 0;
                return 1;
            }

            if (soft)
            {
                if (weapon.Blueprint.WeaponType == StarshipWeaponType.TorpedoTubes) return 0;
                return weapon.Ammo != null && weapon.IsAEAmmo ? 3 : 1;
            }

            int value = 4;
            float hull = ShipCombatInfo.HullFraction(ship);
            if (hull < 0.5f) value += 2;
            if (hull < 0.25f) value += 2;
            if (ShipCombatInfo.ShieldFraction(ship) <= 0.25f) value += 1;
            if (ship == ShipFocus.Current(ability.Caster as StarshipEntity)) value += ShipFocus.Bonus;
            return value;
        }

        private static bool ShieldPulsePays(AbilityData pulse, StarshipEntity target)
        {
            if (ShipCombatInfo.HasFact(target, AELDARI_HOLOFIELD, DRUKHARI_SHADOWFIELD, NECRON_FALLING_APART)) return true;
            return pulse.Caster is StarshipEntity own && ShipCombatInfo.ShieldFraction(own) >= 0.5f;
        }
    }
}
