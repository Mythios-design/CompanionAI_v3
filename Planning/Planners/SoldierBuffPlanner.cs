using System;
using System.Collections.Generic;
using Kingmaker.Blueprints;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Abilities.Components.ProjectileAttack;
using Kingmaker.Utility;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Soldier career abilities, run at the end of every plan.
    ///  - Rapid Fire and Concentrated Fire each give a buff that waits on the caster until one attack type uses it
    ///    (their blueprints' buff conditions): Rapid Fire a ranged scatter attack (Burst Fire), Concentrated Fire a
    ///    ranged pattern attack that isn't scatter (cones and the like: shotguns, flamers). Held out of every pick
    ///    (AbilityPolicy) and put directly before the first planned attack of that type; dropped when there is none
    ///    or the buff is already on.
    ///  - The first Soldier career ability used in a combat is free when no one else in the party is a Soldier.
    ///    Until it is used the game reports 0 AP for all of them, so only the first one in a plan is booked free and
    ///    the rest at their full cost. A plan that can't pay for them drops the last ones.
    /// </summary>
    public static class SoldierBuffPlanner
    {
        private const string RAPID_FIRE = "d42db343de7e4b08a3ea0d7bc36af41f";         // Soldier_RapidFire_Ability
        private const string CONCENTRATED_FIRE = "81ccaba57178473da52e89e99a42392a";  // OffhandShot_Soldier_Ability
        private const string SOLDIER_GROUP = "7ff157320f2a4f2ab7d2e1ac8e7cdeb7";      // SoldierCareerAbilityGroup

        public static bool IsRapidFire(AbilityData ability) => Guid(ability) == RAPID_FIRE;
        public static bool IsConcentratedFire(AbilityData ability) => Guid(ability) == CONCENTRATED_FIRE;

        public static void Arrange(List<PlannedAction> actions, Situation situation, ref float remainingAP, string roleName)
        {
            if (actions == null || situation?.Unit == null) return;
            try
            {
                Place(actions, situation, situation.HeldRapidFire, UsesRapidFire, "scatter attack", ref remainingAP, roleName);
                Place(actions, situation, situation.HeldConcentratedFire, UsesConcentratedFire, "ranged pattern attack", ref remainingAP, roleName);
                BookFreeUse(actions, ref remainingAP, roleName);
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[SoldierBuffs] arrange failed for {situation.Unit.CharacterName}");
            }
        }

        /// <summary>Rapid Fire's buff is used by a ranged scatter attack.</summary>
        private static bool UsesRapidFire(AbilityData attack) => IsRangedWeaponAttack(attack) && attack.IsScatter;

        /// <summary>
        /// Concentrated Fire's buff is used by a ranged attack with a pattern that isn't scatter, or by any attack
        /// delivery when the caster counts all attacks as area attacks (the game's CheckAbilityIsAoEGetter).
        /// </summary>
        private static bool UsesConcentratedFire(AbilityData attack)
        {
            if (!IsRangedWeaponAttack(attack)) return false;
            var bp = attack.Blueprint;
            var pattern = bp.PatternSettings;
            if (pattern != null && !(pattern is ScatterPattern)) return true;
            var features = attack.Caster?.Features;
            return features != null && (bool)features.AllAttacksCountAsAoe
                && (bp.GetComponent<WarhammerAbilityAttackDelivery>() != null || bp.GetComponent<AbilityMeleeBurst>() != null);
        }

        private static bool IsRangedWeaponAttack(AbilityData attack)
            => attack?.Blueprint != null && attack.Weapon != null && !attack.Weapon.Blueprint.IsMelee;

        private static void Place(List<PlannedAction> actions, Situation situation, AbilityData buff,
            Func<AbilityData, bool> usedBy, string attackKind, ref float remainingAP, string roleName)
        {
            if (buff == null) return;
            var unit = situation.Unit;
            if (AllyStateCache.HasBuff(unit, buff))
            {
                Log.Planning.Info($"[{roleName}] {buff.Name} held: already on, waiting for a {attackKind}");
                return;
            }

            int index = actions.FindIndex(a => a.Type == ActionType.Attack && usedBy(a.Ability));
            if (index < 0)
            {
                if (actions.Exists(a => a.Type == ActionType.Attack))
                    Log.Planning.Info($"[{roleName}] {buff.Name} held: no {attackKind} in this plan");
                return;
            }

            float cost = CombatAPI.GetAbilityAPCost(buff);
            if (cost > remainingAP + 0.01f)
            {
                Log.Planning.Info($"[{roleName}] {buff.Name} held: needs {cost:F0} AP, {remainingAP:F0} left");
                return;
            }
            if (!CombatAPI.CanUseAbilityOn(buff, new TargetWrapper(unit), out string reason))
            {
                Log.Planning.Info($"[{roleName}] {buff.Name} held: {reason}");
                return;
            }

            var attack = actions[index].Ability;
            actions.Insert(index, PlannedAction.Buff(buff, unit, $"{buff.Name} before {attack.Name}", cost));
            remainingAP -= cost;
            Log.Planning.Info($"[{roleName}] {buff.Name} before {attack.Name} ({cost:F0} AP)");
        }

        /// <summary>
        /// Only the first discounted Soldier career ability in the plan keeps its discount; later ones are booked at
        /// their blueprint cost. When that leaves the plan short of AP, the last raised ones are dropped.
        /// </summary>
        private static void BookFreeUse(List<PlannedAction> actions, ref float remainingAP, string roleName)
        {
            bool freeTaken = false;
            var raised = new List<PlannedAction>();
            foreach (var action in actions)
            {
                var bp = action.Ability?.Blueprint;
                if (bp == null || !InSoldierGroup(action.Ability)) continue;
                float full = bp.ActionPointCost;
                if (action.APCost >= full - 0.01f) continue;
                if (!freeTaken) { freeTaken = true; continue; }

                remainingAP -= full - action.APCost;
                Log.Planning.Info($"[{roleName}] {action.Ability.Name} booked at {full:F0} AP: the free Soldier ability goes to an earlier one");
                action.APCost = full;
                raised.Add(action);
            }

            for (int i = raised.Count - 1; i >= 0 && remainingAP < -0.01f; i--)
            {
                remainingAP += raised[i].APCost;
                actions.Remove(raised[i]);
                Log.Planning.Info($"[{roleName}] {raised[i].Ability.Name} dropped: not enough AP once the free use is spent");
            }
        }

        private static bool InSoldierGroup(AbilityData ability)
        {
            var groups = ability?.AbilityGroups;
            if (groups == null) return false;
            foreach (var g in groups)
                if (g?.AssetGuid?.ToString() == SOLDIER_GROUP) return true;
            return false;
        }

        private static string Guid(AbilityData ability) => ability?.Blueprint?.AssetGuid?.ToString();
    }
}
