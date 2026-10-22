using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Advanced-archetype abilities that only pay off in a given situation. AbilityPolicy holds them out of every
    /// pick while WhyHold returns a reason, and logs it. Matched by GUID.
    /// </summary>
    public static class ArchetypeConditions
    {
        // Bounty Hunter
        private const string RAID = "1394e5c9a6a347db841d961a87f375f4";  // JoinThePack_Hunter_Ability

        /// <summary>Why the ability isn't worth casting now, or null when it is (or has no condition here).</summary>
        public static string WhyHold(AbilityData ability, Situation situation)
        {
            switch (ability?.Blueprint?.AssetGuid?.ToString())
            {
                case RAID:
                    return PreyInAlliesReach(situation) ? null : "no prey within an ally's reach";
                case TacticianPlanner.INSPIRE:
                case TacticianPlanner.LINCHPIN:
                case TacticianPlanner.STRONGPOINT:
                case TacticianPlanner.PRESS_THE_ADVANTAGE:
                {
                    string chosen = TacticianPlanner.ChosenSpender(situation, out string why);
                    if (chosen == ability.Blueprint.AssetGuid.ToString()) return null;
                    return why ?? "tactical advantage goes to another order this turn";
                }
                case TacticianPlanner.FERVOUR:
                    return TacticianPlanner.WhyHoldFervour(situation);
                default:
                    return null;
            }
        }

        /// <summary>Raid only helps allies' attacks on prey (Hunt Down the Prey).</summary>
        private static bool PreyInAlliesReach(Situation situation)
        {
            var allies = OtherAllies(situation);
            foreach (var enemy in situation.Enemies ?? new List<BaseUnitEntity>())
            {
                if (enemy == null || !enemy.IsConscious || enemy.LifeState.IsDead || !CombatAPI.IsMarkedAsPrey(enemy)) continue;
                foreach (var ally in allies)
                    if (CombatAPI.GetEnemyTurnThreatScore(ally, enemy.Position) > 0f) return true;
            }
            return false;
        }

        /// <summary>Conscious allies other than the unit itself, familiars excluded.</summary>
        internal static List<BaseUnitEntity> OtherAllies(Situation situation)
        {
            var result = new List<BaseUnitEntity>();
            foreach (var a in situation.Allies ?? new List<BaseUnitEntity>())
                if (a != null && a != situation.Unit && !a.IsPet && a.IsConscious && !a.LifeState.IsDead) result.Add(a);
            return result;
        }
    }
}
