using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// AP of the next Apprehend in a plan (game data):
    ///  - MastiffPet_Apprehend_Feature: 1 AP plus a use counter that resets at round start, so each use within a
    ///    round costs 1 more.
    ///  - Rite of Calibration (Master_Talent_08): a combat-long buff takes 1 AP off the Overseer ability group and is
    ///    removed by the first ability of that group used, so the discount goes to the plan's first one.
    /// The game's current cost already holds the counter and the discount; the plan adds what its own earlier
    /// actions will change.
    /// </summary>
    public static class ApprehendCost
    {
        private const string RITE_OF_CALIBRATION_BUFF = "37e9257ba57b49b7bbc9459dadbfa540";  // Master_Talent_08_Additional_Buff
        private const string OVERSEER_ABILITY_GROUP = "6b9a39059860455c817e4bde3e00dccf";

        public static float Next(BaseUnitEntity master, AbilityData apprehend, List<PlannedAction> actions)
        {
            float cost = CombatAPI.GetAbilityAPCost(apprehend);
            if (actions == null) return cost;

            int planned = 0;
            bool overseerAbilityPlanned = false;
            foreach (var action in actions)
            {
                var ability = action?.Ability;
                if (ability == null) continue;
                if (FamiliarAbilities.IsApprehendAbility(ability)) planned++;
                if (InGroup(ability, OVERSEER_ABILITY_GROUP)) overseerAbilityPlanned = true;
            }
            cost += planned;
            // The discount in the current cost goes to an Overseer ability planned earlier
            if (overseerAbilityPlanned && InGroup(apprehend, OVERSEER_ABILITY_GROUP) && HasBuff(master, RITE_OF_CALIBRATION_BUFF))
                cost += 1f;
            return cost;
        }

        private static bool InGroup(AbilityData ability, string groupGuid)
        {
            var groups = ability?.AbilityGroups;
            if (groups == null) return false;
            foreach (var g in groups)
                if (g?.AssetGuid?.ToString() == groupGuid) return true;
            return false;
        }

        private static bool HasBuff(BaseUnitEntity unit, string guid)
        {
            if (unit?.Buffs == null) return false;
            foreach (var buff in unit.Buffs.RawFacts)
                if (buff?.Blueprint?.AssetGuid?.ToString() == guid) return true;
            return false;
        }
    }
}
