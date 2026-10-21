using System;
using System.Collections.Generic;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// One-time AP discounts at the end of every plan. The game reports a discounted cost for every ability a bonus
    /// use applies to (a talent's free attack, Run and Gun's -1 AP) until the use is spent, so a plan could book two
    /// attacks at the discounted cost with only one use: the second then fails for lack of AP. Each bonus entry
    /// covers as many planned actions as it has uses (the game spends the entry with the biggest discount); later
    /// matching actions are booked at their blueprint cost, and dropped from the end if the plan can't pay.
    /// </summary>
    public static class DiscountBooking
    {
        public static void Arrange(List<PlannedAction> actions, Situation situation, ref float remainingAP, string roleName)
        {
            if (actions == null || situation?.Unit == null) return;
            try
            {
                var uses = WeaponAttackAllowance.ReadBonusUses(situation.Unit).FindAll(u => u.CostBonus < 0);
                if (uses.Count == 0) return;

                var left = new int[uses.Count];
                for (int i = 0; i < uses.Count; i++) left[i] = uses[i].Count;
                var raised = new List<PlannedAction>();

                foreach (var action in actions)
                {
                    var ability = action.Ability;
                    if (ability?.Blueprint == null || action.Type == ActionType.Move) continue;
                    float full = CombatAPI.GetBaseAPCost(ability);
                    if (action.APCost >= full - 0.01f) continue;

                    int best = -1;
                    for (int i = 0; i < uses.Count; i++)
                        if (uses[i].AppliesTo(ability) && (best < 0 || uses[i].CostBonus < uses[best].CostBonus)) best = i;
                    if (best < 0) continue;  // discounted by something else
                    if (left[best] > 0) { left[best]--; continue; }

                    remainingAP -= full - action.APCost;
                    Log.Planning.Info($"[{roleName}] {ability.Name} booked at {full:F0} AP: its free use goes to an earlier action");
                    action.APCost = full;
                    raised.Add(action);
                }
                DropUnaffordable(actions, raised, ref remainingAP, roleName, "not enough AP once the free use is spent");
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[DiscountBooking] arrange failed for {situation.Unit.CharacterName}");
            }
        }

        /// <summary>Removes the last of the raised actions until the plan's AP is no longer below zero.</summary>
        public static void DropUnaffordable(List<PlannedAction> actions, List<PlannedAction> raised, ref float remainingAP,
            string roleName, string why)
        {
            for (int i = raised.Count - 1; i >= 0 && remainingAP < -0.01f; i--)
            {
                remainingAP += raised[i].APCost;
                actions.Remove(raised[i]);
                Log.Planning.Info($"[{roleName}] {raised[i].Ability?.Name} dropped: {why}");
            }
        }
    }
}
