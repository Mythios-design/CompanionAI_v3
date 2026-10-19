using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.View.Covers;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// End of a plan with MP left: step to the nearest reachable tile with better cover against the enemies that can
    /// see it. For ranged characters only: not Bladedancers (they go into the crowd), not tanks (a taunter should be
    /// seen), not melee characters, not when engaged. The step must not provoke attacks, enter a hazard, put the tile
    /// in reach of more melee enemies, or leave fewer enemies within weapon range than where the character stands.
    /// </summary>
    public static class CoverPositioning
    {
        public static void PlanCoverMove(Situation situation, List<PlannedAction> actions, ref float remainingMP, string roleName)
        {
            var unit = situation?.Unit;
            if (unit == null || actions == null || remainingMP < 1f || !situation.CanMove) return;
            if (BladedancerPositioning.IsBladedancer(unit)) return;
            if (situation.EffectiveRangePreference == RangePreference.PreferMelee) return;
            var role = situation.CharacterSettings?.Role ?? AIRole.Auto;
            if (role == AIRole.Tank || roleName == "Tank") return;
            try
            {
                // A move already in the plan is the plan's own positioning; this runs on the plan after it
                foreach (var a in actions)
                    if (a.Type == ActionType.Move) return;
                if (CombatAPI.IsInThreateningArea(unit)) return;
                if (situation.Enemies == null || situation.Enemies.Count == 0) return;

                float range = CombatAPI.GetWeaponRangeProfile(unit).MaxRange;
                var here = unit.CurrentUnwalkableNode;
                if (here == null) return;
                int hereCover = CoverScore(unit, here, situation.Enemies, out int hereSeen);
                int hereInRange = InRange(here.Vector3Position, situation.Enemies, range);
                int hereReach = CombatAPI.CountMeleeEnemiesThatCanReach(unit, here.Vector3Position, situation.Enemies);
                if (hereSeen == 0) return;  // no enemy sees this tile

                var tiles = MovementAPI.FindAllReachableTilesWithThreatsSync(unit, remainingMP);
                if (tiles == null) return;
                bool avoidHazard = !CombatAPI.IsUnitInHazardZone(unit);

                CustomGridNodeBase best = null;
                int bestCover = hereCover;
                float bestPath = float.MaxValue;
                foreach (var cell in tiles.Values)
                {
                    if (!cell.IsCanStand || !(cell.Node is CustomGridNodeBase node) || node == here) continue;
                    if (cell.ProvokedAttacks > 0 || cell.EnteredAoE > 0 || cell.Length > remainingMP + 0.01f) continue;
                    var pos = node.Vector3Position;
                    if (avoidHazard && CombatAPI.IsPositionInHazardZone(pos, unit)) continue;
                    if (!BattlefieldGrid.Instance.ValidateNode(unit, node)) continue;

                    int cover = CoverScore(unit, node, situation.Enemies, out _);
                    if (cover < bestCover || (cover == bestCover && cell.Length >= bestPath)) continue;
                    if (cover == hereCover) continue;  // must improve on where the character stands
                    if (InRange(pos, situation.Enemies, range) < hereInRange) continue;
                    if (CombatAPI.CountMeleeEnemiesThatCanReach(unit, pos, situation.Enemies) > hereReach) continue;
                    best = node;
                    bestCover = cover;
                    bestPath = cell.Length;
                }
                if (best == null)
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{roleName}] Cover move: no better covered tile within {remainingMP:F0} MP (cover here {hereCover})");
                    return;
                }

                int insertAt = actions.Count;
                for (int i = 0; i < actions.Count; i++)
                    if (actions[i].Ability != null && AbilityDatabase.IsTurnEnding(actions[i].Ability)) { insertAt = i; break; }
                if (insertAt > 0 && actions[insertAt - 1].Type == ActionType.EndTurn) insertAt--;
                actions.Insert(insertAt, PlannedAction.Move(best.Vector3Position, $"Into cover (cover {hereCover} -> {bestCover})"));
                remainingMP -= bestPath;
                Log.Planning.Info($"[{roleName}] Cover move to ({best.Vector3Position.x:F1}, {best.Vector3Position.z:F1}): " +
                    $"cover {hereCover} -> {bestCover} against {hereSeen} enemies, {bestPath:F1} MP");
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[CoverPositioning] cover move failed for {unit.CharacterName}");
            }
        }

        /// <summary>Sum over living enemies of the tile's cover from them: none 0, half 1, full or out of sight 2.</summary>
        public static int CoverScore(BaseUnitEntity unit, CustomGridNodeBase node, List<BaseUnitEntity> enemies, out int seenBy)
        {
            int score = 0;
            seenBy = 0;
            foreach (var e in enemies)
            {
                if (e == null || !e.IsConscious || e.LifeState.IsDead) continue;
                var enemyNode = e.CurrentUnwalkableNode;
                if (enemyNode == null) continue;
                var cover = LosCalculations.GetWarhammerLos(enemyNode, e.SizeRect, node, unit.SizeRect).CoverType;
                switch (cover)
                {
                    case LosCalculations.CoverType.None: seenBy++; break;
                    case LosCalculations.CoverType.Half: seenBy++; score += 1; break;
                    default: score += 2; break;  // full cover or no line of sight
                }
            }
            return score;
        }

        private static int InRange(Vector3 pos, List<BaseUnitEntity> enemies, float range)
        {
            if (range <= 0f) return 0;
            int n = 0;
            foreach (var e in enemies)
                if (e != null && e.IsConscious && CombatAPI.GetDistanceInTiles(pos, e) <= range) n++;
            return n;
        }
    }
}
