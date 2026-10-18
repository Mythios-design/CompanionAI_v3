using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using Kingmaker.View.Covers;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Blood Phial is the lowest-priority AP spend. AbilityPolicy takes it out of every attack list after analysis
    /// (into Situation.HeldBloodPhial), and this planner uses it as the last step of a plan, with AP left after
    /// everything else.
    /// The pool covers the target cell and the 8 around it. Allies standing in it gain bloodblessed (+damage),
    /// enemies bloodstained (+damage taken), so both count: 2 per enemy (helps every attacker), 1 per ally,
    /// the thrower included. Thrown at the best cell the game lets her target from where she ends her moves,
    /// and only if the pool covers at least one creature; otherwise the charge is kept.
    /// Allies only gain stacks from standing there when it lands or moving in later; later moves aren't predicted.
    /// </summary>
    public static class BloodPhialPlanner
    {
        private const string BLOOD_PHIAL = "29dd732891604e3db9913065a7f4e2d0";  // Grenade_BloodPhial_Ability
        private const int EnemyWeight = 2;
        private const int AllyWeight = 1;

        public static bool IsBloodPhial(AbilityData ability)
            => ability?.Blueprint?.AssetGuid?.ToString() == BLOOD_PHIAL;

        /// <summary>Adds Blood Phial with the AP left, before any turn-ending ability already in the plan.</summary>
        public static void Append(List<PlannedAction> actions, Situation situation, ref float remainingAP, string roleName)
        {
            var phial = situation?.HeldBloodPhial;
            if (phial == null || actions == null) return;
            try
            {
                float cost = CombatAPI.GetAbilityAPCost(phial);
                if (cost > remainingAP + 0.01f) return;

                Vector3 from = situation.Unit.Position;
                foreach (var a in actions)
                    if (a.Type == ActionType.Move && a.MoveDestination.HasValue) from = a.MoveDestination.Value;
                var fromNode = from.GetNearestNodeXZ() as CustomGridNodeBase;
                if (fromNode == null) return;

                if (!FindBestCell(phial, situation, fromNode, out var cell, out int enemies, out int allies, out string why))
                {
                    Log.Planning.Info($"[{roleName}] Blood Phial kept ({remainingAP:F0} AP left): {why}");
                    return;
                }

                var action = PlannedAction.PositionalAttack(phial, cell.Vector3Position,
                    $"Blood Phial (last AP): {enemies} enemies, {allies} allies in the pool", cost, requiresEnemyOccupancy: false);
                int insertAt = actions.Count;
                for (int i = 0; i < actions.Count; i++)
                    if (actions[i].Ability != null && AbilityDatabase.IsTurnEnding(actions[i].Ability)) { insertAt = i; break; }
                actions.Insert(insertAt, action);
                remainingAP -= cost;
                Log.Planning.Info($"[{roleName}] Blood Phial (last AP) at ({cell.Vector3Position.x:F1}, {cell.Vector3Position.z:F1}): {enemies} enemies, {allies} allies in the pool");
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[BloodPhial] planning failed for {situation.Unit?.CharacterName}");
            }
        }

        private static bool FindBestCell(AbilityData phial, Situation situation, CustomGridNodeBase fromNode,
            out CustomGridNodeBase best, out int bestEnemies, out int bestAllies, out string why)
        {
            best = null;
            bestEnemies = bestAllies = 0;
            why = null;
            int bestScore = 0;
            int scored = 0, refused = 0;
            string topRefusal = null;
            int topRefusedScore = 0;

            var enemies = new List<BaseUnitEntity>();
            foreach (var e in situation.Enemies)
                if (e != null && e.IsConscious && !e.LifeState.IsDead) enemies.Add(e);
            var allies = new List<BaseUnitEntity> { situation.Unit };
            foreach (var a in situation.Allies)
                if (a != null && a != situation.Unit && a.IsConscious && !a.LifeState.IsDead) allies.Add(a);

            // Candidate centres: every creature's cells and the cells around them
            var candidates = new Dictionary<CustomGridNodeBase, bool>();
            foreach (var u in enemies) AddAround(u, candidates);
            foreach (var u in allies) AddAround(u, candidates);

            foreach (var centre in candidates.Keys)
            {
                int e = Count(enemies, centre), a = Count(allies, centre);
                int score = e * EnemyWeight + a * AllyWeight;
                if (score > 0) scored++;
                if (score <= bestScore) continue;
                if (!phial.CanTargetFromNode(fromNode, centre, new TargetWrapper(centre.Vector3Position),
                        out int _, out LosCalculations.CoverType _, out AbilityData.UnavailabilityReasonType? reason))
                {
                    refused++;
                    if (score > topRefusedScore)
                    {
                        topRefusedScore = score;
                        topRefusal = $"best refused cell ({centre.Vector3Position.x:F1}, {centre.Vector3Position.z:F1}) {e} enemies/{a} allies: {reason?.ToString() ?? "refused"}";
                    }
                    continue;
                }
                best = centre;
                bestScore = score;
                bestEnemies = e;
                bestAllies = a;
            }
            if (best == null)
                why = $"{candidates.Count} cells near creatures, {scored} would cover someone, {refused} refused by the game" +
                      (topRefusal != null ? $"; {topRefusal}" : "");
            return best != null;
        }

        private static void AddAround(BaseUnitEntity unit, Dictionary<CustomGridNodeBase, bool> candidates)
        {
            foreach (var occ in unit.GetOccupiedNodes())
            {
                if (!(occ is CustomGridNodeBase node)) continue;
                var centre = node.Vector3Position;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        var p = centre + new Vector3(dx * CombatAPI.GridCellSize, 0f, dz * CombatAPI.GridCellSize);
                        if (p.GetNearestNodeXZ() is CustomGridNodeBase n && n.Walkable) candidates[n] = true;
                    }
            }
        }

        /// <summary>Creatures with any occupied cell in the 3x3 square around the centre.</summary>
        private static int Count(List<BaseUnitEntity> units, CustomGridNodeBase centre)
        {
            int count = 0;
            foreach (var u in units)
            {
                foreach (var occ in u.GetOccupiedNodes())
                {
                    if (!(occ is CustomGridNodeBase node)) continue;
                    if (Math.Abs(node.XCoordinateInGrid - centre.XCoordinateInGrid) <= 1
                        && Math.Abs(node.ZCoordinateInGrid - centre.ZCoordinateInGrid) <= 1)
                    {
                        count++;
                        break;
                    }
                }
            }
            return count;
        }
    }
}
