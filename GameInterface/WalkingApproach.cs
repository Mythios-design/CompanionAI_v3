using System;
using System.Collections.Generic;
using System.Diagnostics;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using Kingmaker.View.Covers;
using Pathfinding;
using UnityEngine;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// Approach by walking distance, for when no tile reachable this turn can attack.
    ///  1. One walking map per unit per turn: the game's own reachable-tile search from where the unit stands, out to
    ///     about four turns of movement, ignoring threat costs. Every tile has its true walking length and the tile it
    ///     was reached from, so the way back is exact (around pits and walls) and nothing is judged by straight lines.
    ///  2. Goal tiles: where the unit's attack can hit an enemy (the game's own targeting check from that tile: melee
    ///     reach, or weapon range and line of sight). For an ally target (an Overseer's familiar): tiles next to it.
    ///  3. Which enemy: the one it went for last turn, else the planner's target, as long as that is no more than one
    ///     turn's walk further than the nearest enemy by walking; otherwise the nearest by walking.
    ///  4. This turn's step: back from the goal along the walking map to the furthest tile this turn's movement
    ///     reaches (a tile it can stop on, no attacks of opportunity, not into a hazard).
    ///  5. No enemy reachable on foot within the map: no approach at all (no straight-line or dead-end walk).
    /// </summary>
    public static class WalkingApproach
    {
        private const float LookaheadTurns = 4f;
        private const float MinLookaheadMP = 12f;
        private const float MaxLookaheadMP = 40f;
        private const float MinStepTiles = 1f;
        private const int MaxTargetChecks = 600;

        // Walking map of the unit currently planning: rebuilt when the unit, round or position changes
        private static string _mapUnit;
        private static int _mapRound = -1;
        private static Vector3 _mapFrom;
        private static float _mapReach;
        private static Dictionary<GraphNode, WarhammerPathPlayerCell> _map;

        // Enemy each unit is walking toward, kept across turns so it doesn't flip between enemies (per combat)
        private static readonly Dictionary<string, string> _committed = new Dictionary<string, string>();

        public static void Reset()
        {
            _committed.Clear();
            _map = null;
            _mapUnit = null;
        }

        /// <summary>
        /// This turn's step toward the nearest place (by walking) the unit can attack from, or null: when it is already
        /// as close as this turn's movement allows, or when no enemy is reachable on foot (logged).
        /// </summary>
        public static MovementAPI.PositionScore FindStep(BaseUnitEntity unit, BaseUnitEntity target,
            Dictionary<GraphNode, WarhammerPathAiCell> thisTurnTiles, float mp)
        {
            if (unit == null || thisTurnTiles == null || mp <= 0f) return null;
            try
            {
                var map = WalkingMap(unit, mp, out float reach);
                if (map == null || map.Count == 0) return null;

                bool allyTarget = target != null && unit.CombatGroup?.IsEnemy(target) != true;
                WarhammerPathPlayerCell goal;
                BaseUnitEntity goalUnit;
                if (allyTarget)
                {
                    if (!FindAllyGoal(unit, target, map, out goal)) return null;
                    goalUnit = target;
                }
                else if (!FindEnemyGoal(unit, target, map, mp, reach, out goal, out goalUnit)) return null;

                return StepToward(unit, goal, goalUnit, map, thisTurnTiles, mp);
            }
            catch (Exception ex)
            {
                Log.Engine.Error(ex, $"[WalkingApproach] {unit?.CharacterName}");
                return null;
            }
        }

        private static Dictionary<GraphNode, WarhammerPathPlayerCell> WalkingMap(BaseUnitEntity unit, float mp, out float reach)
        {
            int round = Game.Instance?.TurnController?.CombatRound ?? -1;
            reach = Mathf.Clamp(Math.Max(mp, CombatAPI.GetNextTurnMP(unit)) * LookaheadTurns, MinLookaheadMP, MaxLookaheadMP);
            if (_map != null && _mapUnit == unit.UniqueId && _mapRound == round
                && Vector3.Distance(_mapFrom, unit.Position) < 0.1f && _mapReach >= reach - 0.01f)
            {
                reach = _mapReach;
                return _map;
            }
            var agent = unit.View?.MovementAgent;
            if (agent == null) return null;
            var watch = Stopwatch.StartNew();
            var map = PathfindingService.Instance.FindAllReachableTiles_Blocking(agent, unit.Position, reach, ignoreThreateningAreaCost: true);
            watch.Stop();
            Log.Engine.Info($"[WalkingApproach] {unit.CharacterName}: walking map {map?.Count ?? 0} tiles within {reach:F0} MP ({watch.ElapsedMilliseconds}ms)");
            _map = map;
            _mapUnit = unit.UniqueId;
            _mapRound = round;
            _mapFrom = unit.Position;
            _mapReach = reach;
            return map;
        }

        /// <summary>
        /// The goal tile: for each enemy, its nearest tile (by walking) the unit's attack can hit it from. Picks the
        /// committed enemy, else the planner's target, if within one turn's walk of the nearest; else the nearest.
        /// </summary>
        private static bool FindEnemyGoal(BaseUnitEntity unit, BaseUnitEntity plannerTarget,
            Dictionary<GraphNode, WarhammerPathPlayerCell> map, float mp, float reach,
            out WarhammerPathPlayerCell goal, out BaseUnitEntity goalEnemy)
        {
            goal = default;
            goalEnemy = null;
            var enemies = new List<BaseUnitEntity>();
            foreach (var e in CombatAPI.GetEnemies(unit))
                if (e != null && e.IsConscious && !e.LifeState.IsDead && e.CurrentUnwalkableNode != null
                    && !CombatAPI.IsTargetImmuneToDamage(e, unit)) enemies.Add(e);
            if (enemies.Count == 0) return false;

            var profile = CombatAPI.GetWeaponRangeProfile(unit);
            float range = Math.Max(1f, profile.MaxRange > 0f ? profile.MaxRange : profile.EffectiveRange);
            var attack = CombatAPI.FindAnyAttackAbility(unit, CombatAPI.GetEffectiveRangePreference(unit));

            _committed.TryGetValue(unit.UniqueId, out string committedId);
            var cells = new List<WarhammerPathPlayerCell>(map.Values);
            cells.Sort((a, b) => a.Length.CompareTo(b.Length));

            // Nearest goal per enemy, scanning tiles in walking order until one turn past the nearest found
            var nearest = new Dictionary<BaseUnitEntity, WarhammerPathPlayerCell>();
            float firstFound = float.MaxValue;
            int checks = 0;
            foreach (var cell in cells)
            {
                if (cell.Length > firstFound + mp + 0.01f || checks > MaxTargetChecks) break;
                if (!cell.IsCanStand || !(cell.Node is CustomGridNodeBase node)) continue;
                foreach (var e in enemies)
                {
                    if (nearest.ContainsKey(e)) continue;
                    if (CombatAPI.GetDistanceInTiles(node.Vector3Position, e) > range + 0.5f) continue;
                    checks++;
                    if (!CanHitFrom(unit, attack, node, e, profile.IsMelee)) continue;
                    nearest[e] = cell;
                    if (cell.Length < firstFound) firstFound = cell.Length;
                }
                if (nearest.Count == enemies.Count) break;
            }
            if (nearest.Count == 0)
            {
                Log.Engine.Info($"[WalkingApproach] {unit.CharacterName}: no enemy reachable on foot within {reach:F0} MP " +
                    $"({map.Count} tiles, {checks} attack checks) - holding position");
                return false;
            }

            BaseUnitEntity best = null;
            foreach (var kv in nearest)
                if (best == null || kv.Value.Length < nearest[best].Length) best = kv.Key;
            float bestLength = nearest[best].Length;

            BaseUnitEntity chosen = best;
            string why = "nearest by walking";
            BaseUnitEntity committed = null;
            foreach (var e in nearest.Keys) if (e.UniqueId == committedId) { committed = e; break; }
            if (committed != null && nearest[committed].Length <= bestLength + mp + 0.01f)
            {
                chosen = committed;
                why = committed == best ? "nearest by walking" : "kept from last turn";
            }
            else if (plannerTarget != null && nearest.TryGetValue(plannerTarget, out var plannerGoal)
                     && plannerGoal.Length <= bestLength + mp + 0.01f)
            {
                chosen = plannerTarget;
                why = plannerTarget == best ? "nearest by walking" : "planner's target";
            }
            else if (plannerTarget != null && plannerTarget != best)
            {
                why = nearest.TryGetValue(plannerTarget, out var far)
                    ? $"{plannerTarget.CharacterName} is {far.Length:F0} MP away on foot"
                    : $"{plannerTarget.CharacterName} not reachable on foot within {reach:F0} MP";
            }

            goal = nearest[chosen];
            goalEnemy = chosen;
            _committed[unit.UniqueId] = chosen.UniqueId;
            Log.Engine.Info($"[WalkingApproach] {unit.CharacterName}: going for {chosen.CharacterName}, {goal.Length:F0} MP away on foot ({why}; " +
                $"nearest {best.CharacterName} {bestLength:F0} MP, {nearest.Count} of {enemies.Count} enemies reachable)");
            return true;
        }

        /// <summary>The game's own targeting from that tile; without an attack ability, distance and line of sight.</summary>
        private static bool CanHitFrom(BaseUnitEntity unit, AbilityData attack, CustomGridNodeBase node, BaseUnitEntity enemy, bool melee)
        {
            var enemyNode = enemy.CurrentUnwalkableNode;
            if (attack != null)
                return attack.CanTargetFromNode(node, enemyNode, new TargetWrapper(enemy),
                    out int _, out LosCalculations.CoverType _, out AbilityData.UnavailabilityReasonType? _);
            var los = LosCalculations.GetWarhammerLos(node, unit.SizeRect, enemyNode, enemy.SizeRect);
            return los.CoverType != LosCalculations.CoverType.Invisible
                && (!melee || Math.Abs(node.Vector3Position.y - enemy.Position.y) < 1.6f);
        }

        private static bool FindAllyGoal(BaseUnitEntity unit, BaseUnitEntity ally, Dictionary<GraphNode, WarhammerPathPlayerCell> map,
            out WarhammerPathPlayerCell goal)
        {
            goal = default;
            bool found = false;
            foreach (var cell in map.Values)
            {
                if (!cell.IsCanStand || !(cell.Node is CustomGridNodeBase node)) continue;
                if (CombatAPI.GetDistanceInTiles(node.Vector3Position, ally) > 1.5f) continue;
                if (found && cell.Length >= goal.Length) continue;
                goal = cell;
                found = true;
            }
            if (!found) Log.Engine.Info($"[WalkingApproach] {unit.CharacterName}: {ally.CharacterName} not reachable on foot - holding position");
            return found;
        }

        /// <summary>Back from the goal along the walking map to the furthest tile this turn's movement reaches safely.</summary>
        private static MovementAPI.PositionScore StepToward(BaseUnitEntity unit, WarhammerPathPlayerCell goal, BaseUnitEntity goalUnit,
            Dictionary<GraphNode, WarhammerPathPlayerCell> map, Dictionary<GraphNode, WarhammerPathAiCell> thisTurnTiles, float mp)
        {
            bool avoidHazard = !CombatAPI.IsUnitInHazardZone(unit);
            var step = goal;
            WarhammerPathAiCell here = default;
            bool reachable = false;
            int guard = 0;
            while (guard++ < 400)
            {
                if (thisTurnTiles.TryGetValue(step.Node, out here) && here.IsCanStand && here.Length <= mp + 0.01f
                    && here.ProvokedAttacks == 0
                    && !(avoidHazard && step.Node is CustomGridNodeBase sn && CombatAPI.IsPositionInHazardZone(sn.Vector3Position, unit)))
                {
                    reachable = true;
                    break;
                }
                if (step.ParentNode == null || !map.TryGetValue(step.ParentNode, out var parent)) break;
                step = parent;
            }
            var stepNode = step.Node as CustomGridNodeBase;
            if (!reachable || stepNode == null)
            {
                Log.Engine.Info($"[WalkingApproach] {unit.CharacterName}: no safe tile this turn on the way to {goalUnit.CharacterName}");
                return null;
            }
            if (CombatAPI.MetersToTiles(Vector3.Distance(stepNode.Vector3Position, unit.Position)) < MinStepTiles)
            {
                Log.Engine.Info($"[WalkingApproach] {unit.CharacterName}: already as close to {goalUnit.CharacterName} as this turn's movement allows");
                return null;
            }

            bool arrives = step.Node == goal.Node;
            Log.Engine.Info($"[WalkingApproach] {unit.CharacterName}: step to ({stepNode.Vector3Position.x:F1},{stepNode.Vector3Position.z:F1}), " +
                $"{here.Length:F0} MP this turn, {Math.Max(0f, goal.Length - step.Length):F0} MP left to " +
                $"({goal.Position.x:F1},{goal.Position.z:F1}) to reach {goalUnit.CharacterName}" + (arrives ? " (arrives this turn)" : ""));
            return new MovementAPI.PositionScore
            {
                Node = stepNode,
                CanStand = true,
                APCost = here.Length,
                ProvokedAttacks = here.ProvokedAttacks,
                PathRiskScore = here.EnteredAoE + here.StepsInsideDamagingAoE,
                DistanceScore = 100f - Vector3.Distance(stepNode.Vector3Position, goalUnit.Position),
                HasLosToEnemy = arrives,
                HittableEnemyCount = arrives ? 1 : 0
            };
        }
    }
}
