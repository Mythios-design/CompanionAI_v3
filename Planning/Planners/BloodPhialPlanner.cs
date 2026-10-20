using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Blood Phial is the lowest-priority AP spend. The analyzer takes it out of the attack list before the range
    /// filter (into Situation.HeldBloodPhial), and this planner uses it as the last step of a plan, with AP left after
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

        /// <summary>
        /// The unit has a Blood Phial but it wasn't among this turn's abilities (so the planner never got it): says why,
        /// from the game's own reasons. Silent for units without one and when no AP is left anyway.
        /// </summary>
        private static void LogNotHeld(Situation situation, float remainingAP, string roleName)
        {
            var raw = situation?.Unit?.Abilities?.RawFacts;
            if (raw == null || remainingAP < 1f) return;
            try
            {
                foreach (var fact in raw)
                {
                    var ability = fact?.Data;
                    if (!IsBloodPhial(ability)) continue;
                    var reasons = new List<string>();
                    foreach (var r in ability.GetUnavailabilityReasons()) reasons.Add(r.ToString());
                    if (ability.IsRestricted) reasons.Add("restricted");
                    Log.Planning.Info($"[{roleName}] Blood Phial not usable this turn ({remainingAP:F0} AP left): " +
                        (reasons.Count > 0 ? string.Join(", ", reasons) : $"no reason from the game, {ability.GetAvailableForCastCount()} charges"));
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[BloodPhial] reading why it isn't usable failed for {situation.Unit.CharacterName}");
            }
        }

        /// <summary>Adds Blood Phial with the AP left, before any turn-ending ability already in the plan.</summary>
        public static void Append(List<PlannedAction> actions, Situation situation, ref float remainingAP, string roleName)
        {
            var phial = situation?.HeldBloodPhial;
            if (actions == null) return;
            if (phial == null)
            {
                LogNotHeld(situation, remainingAP, roleName);
                return;
            }
            if (situation.Enemies == null || !situation.Enemies.Exists(e => e != null && e.IsConscious && !e.LifeState.IsDead))
            {
                Log.Planning.Info($"[{roleName}] Blood Phial kept: no enemies left");
                return;
            }
            // A plan that walks but has no attack after the walk gets its attacks from the replan after the move;
            // the phial waits for that plan instead of taking AP that an attack there would use
            int lastMove = actions.FindLastIndex(a => a.Type == ActionType.Move);
            if (lastMove >= 0 && !actions.Skip(lastMove + 1).Any(a => a.Type == ActionType.Attack))
            {
                Log.Planning.Info($"[{roleName}] Blood Phial kept: the plan still walks, attacks come after the move");
                return;
            }
            try
            {
                float cost = CombatAPI.GetAbilityAPCost(phial);
                if (cost > remainingAP + 0.01f)
                {
                    Log.Planning.Info($"[{roleName}] Blood Phial kept: costs {cost:F0} AP, {remainingAP:F0} left");
                    return;
                }

                Vector3 from = situation.Unit.Position;
                foreach (var a in actions)
                    if (a.Type == ActionType.Move && a.MoveDestination.HasValue) from = a.MoveDestination.Value;
                var fromNode = from.GetNearestNodeXZ() as CustomGridNodeBase;
                if (fromNode == null)
                {
                    Log.Planning.Info($"[{roleName}] Blood Phial kept: no grid node at the throwing position");
                    return;
                }

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
