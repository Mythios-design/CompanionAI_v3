using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Arch-Militant stances and Versatility's 4-stack threshold (game data, Veteran abilities):
    ///  - While Confident Approach is on, reaching 4 stacks gives maximum damage until the end of combat; Cautious
    ///    Approach gives the cleanse and immunity instead. Both are granted the moment the stance goes on or the stacks
    ///    reach 4, and stay after the stacks drop.
    ///  - Reckless Rush adds 3 temporary stacks (only with at least one) and MP. With 1 to 3 stacks it is cast right
    ///    before the stance, or alone when the stance is already on, if that stance's 4-stack bonus is missing.
    ///  - Casting the active stance again turns it off, so a stance is never recast (BuffPlanner.PlanApproachStance).
    /// </summary>
    public static class VersatilityPlanner
    {
        private const string RECKLESS_RUSH = "801926b855d64391b465b6d75796a19a";
        private const string CONFIDENT_BUFF = "5cfab28bc62242c19c2c2a633f3342f0";
        private const string CONFIDENT_MAX_DAMAGE = "2844a2fc661443239f41afaf1481d922";
        private const string CAUTIOUS_BUFF = "4d56fa57bcab4c01869c5138aea0eeb9";
        private const string CAUTIOUS_IMMUNITY = "54595fca008d47b9b13646f0ba8c09ad";
        public const int StanceThreshold = 4;
        private const int RushStacks = 3;

        /// <summary>The unit has, or Reckless Rush can give it, 4 or more stacks this turn.</summary>
        public static bool CanReachThreshold(BaseUnitEntity unit)
        {
            if (!Versatility.HasKeystone(unit)) return false;
            int stacks = Versatility.Stacks(unit);
            return stacks >= StanceThreshold || (stacks >= 1 && UsableRush(unit) != null);
        }

        public static bool HasCautiousImmunity(BaseUnitEntity unit) => HasFact(unit, CAUTIOUS_IMMUNITY);

        /// <summary>
        /// Reckless Rush to lift the stacks to 4 for the stance planned this turn, or the one already on, when its
        /// 4-stack bonus is missing. Keeps 1 AP for an attack.
        /// </summary>
        public static PlannedAction PlanRush(Situation situation, PlannedAction plannedStance, ref float remainingAP, string roleName)
        {
            var unit = situation?.Unit;
            if (!Versatility.HasKeystone(unit)) return null;
            int stacks = Versatility.Stacks(unit);
            if (stacks < 1 || stacks >= StanceThreshold || stacks + RushStacks < StanceThreshold) return null;

            bool confident, cautious;
            if (plannedStance?.Ability != null)
            {
                confident = AbilityDatabase.IsConfidentApproach(plannedStance.Ability);
                cautious = AbilityDatabase.IsCautiousApproach(plannedStance.Ability);
            }
            else
            {
                confident = HasFact(unit, CONFIDENT_BUFF);
                cautious = HasFact(unit, CAUTIOUS_BUFF);
            }
            if (!confident && !cautious) return null;
            if (HasFact(unit, confident ? CONFIDENT_MAX_DAMAGE : CAUTIOUS_IMMUNITY)) return null;

            var rush = UsableRush(unit);
            if (rush == null) return null;
            float cost = CombatAPI.GetAbilityAPCost(rush);
            string stance = confident ? "Confident Approach" : "Cautious Approach";
            if (cost + 1f > remainingAP + 0.01f)
            {
                Log.Planning.Info($"[{roleName}] Reckless Rush held: {stacks} Versatility stacks, but {remainingAP:F0} AP leaves none for an attack");
                return null;
            }

            remainingAP -= cost;
            // Not planned again by the move or post-action rules in this plan
            RushPlanner.MarkPlanned(situation, rush);

            Log.Planning.Info($"[{roleName}] Reckless Rush: {stacks} -> {stacks + RushStacks} Versatility stacks, " +
                $"{stance}'s 4-stack bonus for the rest of the combat");
            return PlannedAction.Buff(rush, unit, $"Reckless Rush to 4 Versatility stacks for {stance}", cost);
        }

        private static AbilityData UsableRush(BaseUnitEntity unit)
        {
            if (unit?.Abilities == null) return null;
            foreach (var fact in unit.Abilities.RawFacts)
            {
                var data = fact?.Data;
                if (data?.Blueprint?.AssetGuid?.ToString() != RECKLESS_RUSH) continue;
                if (!data.IsAvailable || !CombatAPI.CanUseAbilityOn(data, new TargetWrapper(unit), out _)) return null;
                return data;
            }
            return null;
        }

        private static bool HasFact(BaseUnitEntity unit, string guid)
        {
            if (unit?.Facts == null) return false;
            foreach (var fact in unit.Facts.List)
                if (fact?.Blueprint?.AssetGuid?.ToString() == guid) return true;
            return false;
        }
    }
}
