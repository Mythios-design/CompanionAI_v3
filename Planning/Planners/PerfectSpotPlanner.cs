using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Operative's Perfect Spot (Adept_SnipingSHot, 1 AP): +15 Ballistic Skill, +15 Perception and +25% cover strength
    /// for the caster until the end of combat, removed the moment the caster moves. So it is cast at the start of the
    /// turn, from the tile the unit shoots from, and then the unit holds still while it lasts.
    ///  - Arrange (end of plan, before the cover step): keep or add the cast only when the plan attacks and has no move,
    ///    and the tile has cover from the enemies that can see it; put it first. Otherwise drop it (AP refunded).
    ///  - While the buff is active: no cover step, no end-of-turn reposition, and attacking from here beats walking to
    ///    another attack tile (IsActive is checked there).
    /// </summary>
    public static class PerfectSpotPlanner
    {
        private const string PERFECT_SPOT = "d0f95ed472d542579026e8a8b8eb2bab";
        private const string PERFECT_SPOT_BUFF = "bca05f1815dc49ed870306461a4cd686";

        public static bool IsPerfectSpot(AbilityData ability) => ability?.Blueprint?.AssetGuid?.ToString() == PERFECT_SPOT;

        /// <summary>The unit has the Perfect Spot buff on (it ends when the unit moves).</summary>
        public static bool IsActive(BaseUnitEntity unit)
        {
            var buffs = unit?.Buffs?.Enumerable;
            if (buffs == null) return false;
            foreach (var b in buffs)
                if (b?.Blueprint?.AssetGuid?.ToString() == PERFECT_SPOT_BUFF) return true;
            return false;
        }

        public static void Arrange(List<PlannedAction> actions, Situation situation, ref float remainingAP, string roleName)
        {
            var unit = situation?.Unit;
            if (unit == null || actions == null) return;
            try
            {
                int planned = actions.FindIndex(a => IsPerfectSpot(a.Ability));
                AbilityData ability = planned >= 0 ? actions[planned].Ability : FindAbility(situation);
                if (ability == null) return;

                string drop = null;
                if (IsActive(unit)) drop = "already on";
                else if (actions.Exists(a => a.Type == ActionType.Move)) drop = "the plan moves, which would end it";
                else if (!actions.Exists(a => a.Type == ActionType.Attack)) drop = "no attack this turn";
                else
                {
                    var here = unit.CurrentUnwalkableNode;
                    int cover = here != null ? CoverPositioning.CoverScore(unit, here, situation.Enemies, out int seenBy) : 0;
                    if (cover == 0) drop = "no cover here";
                }

                if (drop != null)
                {
                    if (planned < 0) return;
                    remainingAP += actions[planned].APCost;
                    actions.RemoveAt(planned);
                    Log.Planning.Info($"[{roleName}] Perfect Spot dropped: {drop}");
                    return;
                }

                PlannedAction cast;
                if (planned >= 0)
                {
                    cast = actions[planned];
                    actions.RemoveAt(planned);
                }
                else
                {
                    float cost = CombatAPI.GetAbilityAPCost(ability);
                    if (cost > remainingAP + 0.01f)
                    {
                        Log.Planning.Info($"[{roleName}] Perfect Spot held: needs {cost:F0} AP, {remainingAP:F0} left after the attacks");
                        return;
                    }
                    if (!CombatAPI.CanUseAbilityOn(ability, new TargetWrapper(unit), out string reason))
                    {
                        Log.Planning.Info($"[{roleName}] Perfect Spot held: {reason}");
                        return;
                    }
                    cast = PlannedAction.Buff(ability, unit, "Perfect Spot before shooting (holds while the unit stays)", cost);
                    remainingAP -= cost;
                }
                actions.Insert(0, cast);
                Log.Planning.Info($"[{roleName}] Perfect Spot first: attacking from cover here, no move this turn");
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[PerfectSpot] arrange failed for {unit.CharacterName}");
            }
        }

        private static AbilityData FindAbility(Situation situation)
        {
            if (situation.AvailableBuffs != null)
                foreach (var a in situation.AvailableBuffs)
                    if (IsPerfectSpot(a)) return a;
            return null;
        }
    }
}
