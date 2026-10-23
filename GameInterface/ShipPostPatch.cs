using System;
using System.Collections.Generic;
using HarmonyLib;
using Kingmaker;
using Kingmaker.AI;
using Kingmaker.AI.BehaviourTrees;
using Kingmaker.AI.BehaviourTrees.Nodes;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Commands;
using Warhammer.SpaceCombat.AI;
using Warhammer.SpaceCombat.AI.BehaviourTrees;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Planning.Planners;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// Runs ShipPostPlanner's casts inside the game's starship behaviour tree, for the delegated player ship only.
    ///  - Plan start: before the path search begins (its coroutine not yet created), one cast per tick; the node
    ///    reports Running until the cast is done, then the search runs with the effect on.
    ///  - After a shot: the next-action step casts Rapid Reload instead of its next action and marks the plan
    ///    broken, so the tree plans again from where the ship is (the game's own path for plan-breaking casts).
    ///  - Turn end: the finish-turn step casts instead of finishing; the tree then starts over, and finishes once
    ///    the planner has nothing left.
    ///  - Focus ship (ShipFocus): picked right before the path search. When it dies, the next action marks the
    ///    plan broken, or the finish-turn step starts the tree over, so the rest of the turn is planned again
    ///    around a new focus.
    /// </summary>
    [HarmonyPatch]
    public static class ShipPostPatch
    {
        private static readonly AccessTools.FieldRef<TaskNodeFindBestTrajectory, IEnumerator<Status>> WorkCoroutine =
            AccessTools.FieldRefAccess<TaskNodeFindBestTrajectory, IEnumerator<Status>>("WorkCoroutine");

        [HarmonyPatch(typeof(TaskNodeFindBestTrajectory), "TickInternal")]
        [HarmonyPrefix]
        public static bool PlanStart_Prefix(TaskNodeFindBestTrajectory __instance, Blackboard blackboard, ref Status __result)
        {
            if (WorkCoroutine(__instance) != null) return true;
            var ship = ShipOf(blackboard);
            if (ship == null) return true;
            if (IsBusy(ship)) { __result = Status.Running; return false; }
            if (TryCast(ShipPhase.PlanStart, ship, blackboard, Status.Running, ref __result)) return false;
            PickFocus(ship, blackboard);
            return true;
        }

        [HarmonyPatch(typeof(TaskNodeDoNextAction), "TickInternal")]
        [HarmonyPrefix]
        public static bool NextAction_Prefix(Blackboard blackboard, ref Status __result, out bool __state)
        {
            __state = false;
            var ship = ShipOf(blackboard);
            if (ship == null || !ShipPostPlanner.HasPendingShot(ship) || IsBusy(ship)) return true;
            __state = TryCast(ShipPhase.AfterShot, ship, blackboard, Status.Success, ref __result);
            return !__state;
        }

        /// <summary>Notes the volley the step just ordered; skipped when the prefix cast instead (CmdParams is then stale).</summary>
        [HarmonyPatch(typeof(TaskNodeDoNextAction), "TickInternal")]
        [HarmonyPostfix]
        public static void NextAction_Postfix(TaskNodeDoNextAction __instance, Blackboard blackboard, Status __result, bool __state)
        {
            if (__state || __result != Status.Success) return;
            var ship = ShipOf(blackboard);
            if (ship == null) return;
            if (__instance.CmdParams is UnitUseAbilityParams cast)
                ShipPostPlanner.NoteShot(ship, cast.Ability);
            if (ShipFocus.FocusDied(ship) && blackboard.DecisionContext is SpaceCombatDecisionContext context)
                context.IsLastActionBrokePlan = true;
        }

        [HarmonyPatch(typeof(TaskNodeTryFinishTurn), "TickInternal")]
        [HarmonyPrefix]
        public static bool FinishTurn_Prefix(Blackboard blackboard, ref Status __result)
        {
            var ship = ShipOf(blackboard);
            if (ship == null || IsBusy(ship)) return true;
            if (ShipFocus.FocusDied(ship))
            {
                // Success without finishing starts the tree over: the path search picks a new focus
                Log.Engine.Info($"[Ship] {ship.CharacterName}: focus ship destroyed, planning the rest of the turn again");
                __result = Status.Success;
                return false;
            }
            return !TryCast(ShipPhase.TurnEnd, ship, blackboard, Status.Success, ref __result);
        }

        private static void PickFocus(StarshipEntity ship, Blackboard blackboard)
        {
            try
            {
                if (ShipFocus.Ensure(ship, blackboard.DecisionContext)) ShipAIPatch.ClearTurnValues();
            }
            catch (Exception ex)
            {
                Log.Engine.Error(ex, $"[ShipPostPatch] focus pick failed for {ship.CharacterName}");
            }
        }

        /// <summary>The delegated player ship this tree belongs to, or null.</summary>
        private static StarshipEntity ShipOf(Blackboard blackboard)
        {
            var ship = blackboard?.Unit as StarshipEntity;
            return ship != null && ShipAIPatch.UsesStandInBrain(ship) ? ship : null;
        }

        /// <summary>The game's wait condition (TaskNodeWaitCommandsDone).</summary>
        private static bool IsBusy(StarshipEntity ship)
            => Game.Instance.AiBrainController.IsBusy || !ship.Commands.Empty;

        private static bool TryCast(ShipPhase phase, StarshipEntity ship, Blackboard blackboard, Status statusWhenCast, ref Status result)
        {
            ShipCast cast;
            try
            {
                cast = ShipPostPlanner.Next(phase, ship, blackboard.DecisionContext);
            }
            catch (Exception ex)
            {
                Log.Engine.Error(ex, $"[ShipPostPatch] {phase} planning failed for {ship.CharacterName}");
                return false;
            }
            if (cast == null) return false;

            ship.Commands.Run(new UnitUseAbilityParams(cast.Ability, cast.Target));
            if (cast.BreaksPlan && blackboard.DecisionContext is SpaceCombatDecisionContext context)
                context.IsLastActionBrokePlan = true;
            result = statusWhenCast;
            return true;
        }
    }
}
