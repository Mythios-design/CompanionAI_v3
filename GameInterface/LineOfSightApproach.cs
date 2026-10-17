using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.View.Covers;
using Pathfinding;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// For a ranged character when no tile reachable this turn can see an enemy: look at the tiles reachable within
    /// two turns of movement, find the nearest (by walking distance) that can see an enemy within weapon range, and
    /// return the tile on the path to it that this turn's movement reaches. That follows the real route around walls
    /// instead of walking straight at the enemy into a wall.
    /// </summary>
    public static class LineOfSightApproach
    {
        private const float MinStepTiles = 1f;

        public static MovementAPI.PositionScore FindStepTowardSight(
            BaseUnitEntity unit,
            Dictionary<GraphNode, WarhammerPathAiCell> thisTurnTiles,
            float mp)
        {
            if (unit == null || thisTurnTiles == null || mp <= 0f) return null;
            try
            {
                var profile = CombatAPI.GetWeaponRangeProfile(unit);
                if (profile.IsMelee) return null;
                float range = profile.MaxRange > 0f ? profile.MaxRange : profile.EffectiveRange;
                if (range <= 0f) return null;

                var enemies = CombatAPI.GetEnemies(unit)
                    .Where(e => e != null && e.IsConscious && !e.LifeState.IsDead && e.CurrentUnwalkableNode != null)
                    .ToList();
                if (enemies.Count == 0) return null;

                float twoTurns = mp + CombatAPI.GetNextTurnMP(unit);
                var far = MovementAPI.FindAllReachableTilesWithThreatsSync(unit, twoTurns);
                if (far == null || far.Count == 0) return null;

                bool avoidHazard = !CombatAPI.IsUnitInHazardZone(unit);

                // Nearest viewing tile by walking distance
                WarhammerPathAiCell goal = default;
                BaseUnitEntity seen = null;
                bool found = false;
                // For the log: how much was searched, and the closest any tile got to an enemy
                int checkedTiles = 0, inRangeTiles = 0;
                float furthestWalk = 0f, closestDist = float.MaxValue;
                Vector3 closestTile = Vector3.zero;
                BaseUnitEntity closestEnemy = null;
                foreach (var cell in far.Values.OrderBy(c => c.Length))
                {
                    if (!cell.IsCanStand || !(cell.Node is CustomGridNodeBase node)) continue;
                    if (avoidHazard && CombatAPI.IsPositionInHazardZone(node.Vector3Position, unit)) continue;
                    checkedTiles++;
                    if (cell.Length > furthestWalk) furthestWalk = cell.Length;
                    bool anyInRange = false;
                    foreach (var enemy in enemies)
                    {
                        float dist = CombatAPI.GetDistanceInTiles(node.Vector3Position, enemy);
                        if (dist < closestDist) { closestDist = dist; closestTile = node.Vector3Position; closestEnemy = enemy; }
                        if (dist > range) continue;
                        anyInRange = true;
                        var los = LosCalculations.GetWarhammerLos(node, unit.SizeRect, enemy.CurrentUnwalkableNode, enemy.SizeRect);
                        if (los.CoverType == LosCalculations.CoverType.Invisible) continue;
                        goal = cell;
                        seen = enemy;
                        found = true;
                        break;
                    }
                    if (anyInRange) inRangeTiles++;
                    if (found) break;
                }
                if (!found)
                {
                    Log.Engine.Info($"[LoSApproach] {unit.CharacterName}: no tile within two turns ({twoTurns:F0} MP) can see an enemy - " +
                        $"{checkedTiles} of {far.Count} tiles checked (furthest {furthestWalk:F0} MP), {inRangeTiles} within weapon range {range:F0}; " +
                        $"closest tile ({closestTile.x:F1},{closestTile.y:F1},{closestTile.z:F1}) is {closestDist:F1} tiles from {closestEnemy?.CharacterName ?? "no enemy"}");
                    return null;
                }

                // Walk back along the path to the furthest tile this turn's movement reaches
                var step = goal;
                int guard = 0;
                while (guard++ < 200)
                {
                    bool reachable = thisTurnTiles.TryGetValue(step.Node, out var here) && here.IsCanStand && here.Length <= mp + 0.01f
                        && !(avoidHazard && step.Node is CustomGridNodeBase sn && CombatAPI.IsPositionInHazardZone(sn.Vector3Position, unit));
                    if (reachable) break;
                    if (step.ParentNode == null || !far.TryGetValue(step.ParentNode, out var parent)) return null;
                    step = parent;
                }

                var stepNode = step.Node as CustomGridNodeBase;
                if (stepNode == null) return null;
                float moved = CombatAPI.MetersToTiles(Vector3.Distance(stepNode.Vector3Position, unit.Position));
                if (moved < MinStepTiles) return null;

                bool arrives = step.Node == goal.Node;
                Log.Engine.Info($"[LoSApproach] {unit.CharacterName}: no line of sight from any reachable tile ({checkedTiles} of {far.Count} tiles checked) - heading for " +
                    $"({goal.Position.x:F1},{goal.Position.z:F1}), sees {seen.CharacterName}, {goal.Length:F0} MP to walk" +
                    (arrives ? " (reached this turn)" : $", stopping at ({stepNode.Vector3Position.x:F1},{stepNode.Vector3Position.z:F1}) this turn"));

                return new MovementAPI.PositionScore
                {
                    Node = stepNode,
                    CanStand = true,
                    APCost = step.Length,
                    ProvokedAttacks = step.ProvokedAttacks,
                    HasLosToEnemy = arrives,
                    HittableEnemyCount = arrives ? 1 : 0
                };
            }
            catch (Exception ex)
            {
                Log.Engine.Error(ex, $"[LoSApproach] {unit?.CharacterName}");
                return null;
            }
        }
    }
}
