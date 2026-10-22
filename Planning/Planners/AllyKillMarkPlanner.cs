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
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Marks that pay off when an ally, not the caster, makes the kill.
    ///  - Hot on the Trail (Bounty Hunter, 0 AP, range 10): ends the turn. If an ally kills the trailed enemy,
    ///    the Bounty Hunter gets an extra turn with the AP and MP left when it was cast. Held out of every pick
    ///    (AbilityPolicy) and cast as the plan's last action, only with AP left for that extra turn.
    ///  - Assign Objective (Master Tactician, 1 AP, range 20): the ally who kills the mark before the Tactician's next
    ///    turn gains momentum and +5 to all characteristics. Held out of the marks and cast with leftover AP at the
    ///    end of the plan, before any turn-ending action.
    /// An enemy an ally can finish: under half wounds, not attacked in this plan (the caster's own kill would waste
    /// the mark), and within reach of an ally who isn't the caster or a familiar.
    /// </summary>
    public static class AllyKillMarkPlanner
    {
        private const string HOT_ON_THE_TRAIL = "56bd0b3c54784098ae7044ceae173c7c";  // PaveTheTrail_Hunter_Ability
        private const float FinishableHP = 0.5f;

        private const string ASSIGN_OBJECTIVE = "91d40472299d48ffa675c249c4226d64";  // TacticianAssignObjectiveAbility

        public static bool IsHotOnTheTrail(AbilityData ability) => Guid(ability) == HOT_ON_THE_TRAIL;
        public static bool IsAssignObjective(AbilityData ability) => Guid(ability) == ASSIGN_OBJECTIVE;

        /// <summary>Adds Assign Objective with leftover AP, before any turn-ending action, on an enemy an ally can finish.</summary>
        public static void AppendAssignObjective(List<PlannedAction> actions, Situation situation, ref float remainingAP, string roleName)
        {
            var mark = situation?.HeldAssignObjective;
            if (mark == null || actions == null) return;
            try
            {
                float cost = CombatAPI.GetAbilityAPCost(mark);
                if (cost > remainingAP + 0.01f) return;
                if (WalksWithoutAttacking(actions))
                {
                    Log.Planning.Info($"[{roleName}] Assign Objective held: the plan still walks, attacks come after the move");
                    return;
                }
                var target = FindFinishableEnemy(mark, situation, actions, out string why);
                if (target == null)
                {
                    Log.Planning.Info($"[{roleName}] Assign Objective held: {why}");
                    return;
                }
                int insertAt = actions.FindIndex(a => a.Ability != null && Data.AbilityDatabase.IsTurnEnding(a.Ability));
                if (insertAt < 0) insertAt = actions.Count;
                actions.Insert(insertAt, PlannedAction.Debuff(mark, target, $"Assign Objective on {target.CharacterName}", cost));
                remainingAP -= cost;
                Log.Planning.Info($"[{roleName}] Assign Objective -> {target.CharacterName} ({HPText(target)}): an ally's kill earns the reward");
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[AllyKillMark] Assign Objective planning failed for {situation.Unit?.CharacterName}");
            }
        }

        /// <summary>Adds Hot on the Trail as the last action. Keeps the AP left for the extra turn, so later AP spends skip.</summary>
        public static void AppendHotOnTheTrail(List<PlannedAction> actions, Situation situation, ref float remainingAP, string roleName)
        {
            var trail = situation?.HeldHotOnTheTrail;
            if (trail == null || actions == null) return;
            try
            {
                if (remainingAP < 1f)
                {
                    Log.Planning.Info($"[{roleName}] Hot on the Trail held: no AP left for the extra turn");
                    return;
                }
                if (WalksWithoutAttacking(actions))
                {
                    Log.Planning.Info($"[{roleName}] Hot on the Trail held: the plan still walks, attacks come after the move");
                    return;
                }
                if (actions.Exists(a => a.Ability != null && Data.AbilityDatabase.IsTurnEnding(a.Ability)))
                    return;

                var target = FindFinishableEnemy(trail, situation, actions, out string why);
                if (target == null)
                {
                    Log.Planning.Info($"[{roleName}] Hot on the Trail held: {why}");
                    return;
                }

                float cost = CombatAPI.GetAbilityAPCost(trail);
                actions.Add(PlannedAction.Debuff(trail, target, $"Hot on the Trail on {target.CharacterName} (ends the turn)", cost));
                Log.Planning.Info($"[{roleName}] Hot on the Trail -> {target.CharacterName} ({HPText(target)}): " +
                    $"ends the turn, {remainingAP:F0} AP kept for the extra turn if an ally kills it");
                remainingAP = 0f;
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[AllyKillMark] Hot on the Trail planning failed for {situation.Unit?.CharacterName}");
            }
        }

        /// <summary>
        /// The lowest-wounds enemy the ability can reach from where the plan ends, under half wounds, not attacked in
        /// this plan, and within reach of an ally next turn. Null with the reason when there is none.
        /// </summary>
        internal static BaseUnitEntity FindFinishableEnemy(AbilityData mark, Situation situation, List<PlannedAction> actions, out string why)
        {
            why = null;
            var attacked = new HashSet<BaseUnitEntity>();
            foreach (var a in actions)
                if (a.Type == ActionType.Attack && a.Target?.Entity is BaseUnitEntity t) attacked.Add(t);

            var fromNode = EndPosition(situation, actions).GetNearestNodeXZ() as CustomGridNodeBase;
            var allies = situation.Allies?.Where(a => a != null && a != situation.Unit && !a.IsPet
                                                      && a.IsConscious && !a.LifeState.IsDead).ToList()
                         ?? new List<BaseUnitEntity>();
            if (allies.Count == 0) { why = "no ally to make the kill"; return null; }

            BaseUnitEntity best = null;
            float bestHP = float.MaxValue;
            int healthy = 0, unreached = 0, outOfRange = 0;
            foreach (var enemy in situation.Enemies ?? new List<BaseUnitEntity>())
            {
                if (enemy == null || !enemy.IsConscious || enemy.LifeState.IsDead || attacked.Contains(enemy)) continue;
                float hp = HPFraction(enemy);
                if (hp > FinishableHP) { healthy++; continue; }
                if (!allies.Any(ally => CombatAPI.GetEnemyTurnThreatScore(ally, enemy.Position) > 0f)) { unreached++; continue; }
                if (fromNode != null && !mark.CanTargetFromNode(fromNode, null, new TargetWrapper(enemy),
                        out int _, out LosCalculations.CoverType _, out AbilityData.UnavailabilityReasonType? _))
                { outOfRange++; continue; }
                if (hp < bestHP) { best = enemy; bestHP = hp; }
            }
            if (best == null)
                why = $"no enemy an ally can finish ({healthy} above half wounds, {unreached} out of allies' reach, {outOfRange} out of its range)";
            return best;
        }

        private static bool WalksWithoutAttacking(List<PlannedAction> actions)
        {
            int lastMove = actions.FindLastIndex(a => a.Type == ActionType.Move);
            return lastMove >= 0 && !actions.Skip(lastMove + 1).Any(a => a.Type == ActionType.Attack);
        }

        private static Vector3 EndPosition(Situation situation, List<PlannedAction> actions)
        {
            Vector3 from = situation.Unit.Position;
            foreach (var a in actions)
                if (a.Type == ActionType.Move && a.MoveDestination.HasValue) from = a.MoveDestination.Value;
            return from;
        }

        private static float HPFraction(BaseUnitEntity unit)
        {
            int max = unit.Health.MaxHitPoints;
            return max > 0 ? (float)unit.Health.HitPointsLeft / max : 1f;
        }

        private static string HPText(BaseUnitEntity unit) => $"{HPFraction(unit):P0} wounds";

        private static string Guid(AbilityData ability) => ability?.Blueprint?.AssetGuid?.ToString();
    }
}
