using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Party-wide ability rules applied after analysis:
    ///  - Desperate Measures are never used by the AI.
    ///  - Melee characters don't use abilities that end weapon attacks for the round while they can still
    ///    reach an enemy with a weapon attack. Those abilities come back once the weapon attacks are spent.
    /// </summary>
    public static class AbilityPolicy
    {
        // The game's own marker group: abilities in it put weapon attacks on cooldown for the round.
        private const string WeaponAttackGroupName = "WeaponAttackAbilityGroup";
        private const string BREAK_THROUGH = "54f73c2523524544a8d7ffc3807056f0";

        public static void Apply(Situation situation)
        {
            if (situation?.Unit == null) return;
            try
            {
                int removedDesperate = RemoveWhere(situation, a => a.Blueprint?.IsDesperateMeasure == true);
                if (removedDesperate > 0)
                    Log.Planning.Info($"[AbilityPolicy] {situation.Unit.CharacterName}: Desperate Measures held back (AI never uses them)");

                // Break Through (and its Slash attacks) can't be used in the same round as Charge. With an enemy
                // already in reach, Break Through is the better play, so don't Charge first.
                bool hasBreakThrough = situation.AvailableBuffs.Concat(situation.AvailableAttacks)
                    .Any(a => a?.Blueprint?.AssetGuid?.ToString() == BREAK_THROUGH);
                if (hasBreakThrough && situation.MeleeHittableEnemies != null && situation.MeleeHittableEnemies.Count > 0
                    && RemoveWhere(situation, a => a.IsCharge) > 0)
                    Log.Planning.Info($"[AbilityPolicy] {situation.Unit.CharacterName}: Charge held — enemy in reach, Break Through would be locked out");

                if (situation.RangePreference == RangePreference.PreferMelee && CanStillMakeWeaponAttack(situation, out string why))
                {
                    var held = new List<string>();
                    RemoveWhere(situation, a =>
                    {
                        if (a.Weapon != null || AbilityDatabase.IsGapCloser(a) || !EndsWeaponAttacks(a)) return false;
                        if (!held.Contains(a.Name)) held.Add(a.Name);
                        return true;
                    });
                    if (held.Count > 0)
                        Log.Planning.Info($"[AbilityPolicy] {situation.Unit.CharacterName}: holding {string.Join(", ", held)} until after weapon attacks ({why})");
                }
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, "[AbilityPolicy] Apply failed");
            }
        }

        /// <summary>Final check at execution: the AI never uses Desperate Measures.</summary>
        public static bool AllowAtExecution(AbilityData ability, out string reason)
        {
            reason = null;
            if (ability?.Blueprint?.IsDesperateMeasure == true)
            {
                reason = "Desperate Measures are not used by the AI";
                return false;
            }
            return true;
        }

        /// <summary>True if using this ability puts weapon attacks on cooldown for the round.</summary>
        public static bool EndsWeaponAttacks(AbilityData ability)
        {
            try
            {
                var groups = ability?.Blueprint?.AbilityGroups;
                if (groups == null) return false;
                foreach (var g in groups)
                    if (g != null && g.name == WeaponAttackGroupName) return true;
            }
            catch
            {
                // treat unreadable data as "doesn't end attacks"
            }
            return false;
        }

        private static bool CanStillMakeWeaponAttack(Situation situation, out string why)
        {
            why = null;
            var weaponAttacks = situation.AvailableAttacks.Where(a => a != null && (a.Weapon != null || AbilityDatabase.IsGapCloser(a))).ToList();
            if (weaponAttacks.Count == 0) return false;

            if (situation.MeleeHittableEnemies != null && situation.MeleeHittableEnemies.Count > 0)
            {
                why = "enemy in melee reach";
                return true;
            }

            float reach = situation.CurrentMP + 2f;
            foreach (var gap in weaponAttacks.Where(a => AbilityDatabase.IsGapCloser(a)))
                reach = Math.Max(reach, CombatAPI.GetAbilityRangeInTiles(gap));

            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null || !enemy.IsConscious || enemy.LifeState.IsDead) continue;
                if (CombatAPI.GetDistanceInTiles(situation.Unit, enemy) <= reach)
                {
                    why = $"{enemy.CharacterName} reachable this turn";
                    return true;
                }
            }
            return false;
        }

        private static int RemoveWhere(Situation s, Func<AbilityData, bool> predicate)
        {
            int removed = 0;
            foreach (var list in new[] { s.AvailableBuffs, s.AvailableAttacks, s.AvailableHeals, s.AvailableDebuffs,
                s.AvailableSpecialAbilities, s.AvailablePositionalBuffs, s.AvailableStratagems, s.AvailableMarkers,
                s.AvailableAoEAttacks, s.PostFirstActionAbilities, s.LastResortBuffs })
            {
                if (list == null) continue;
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i] != null && predicate(list[i])) { list.RemoveAt(i); removed++; }
            }
            if (s.MPBuffAbility != null && predicate(s.MPBuffAbility)) { s.MPBuffAbility = null; s.MPBuffExpectedRecovery = 0f; }
            if (s.RunAndGunAbility != null && predicate(s.RunAndGunAbility)) s.RunAndGunAbility = null;
            if (s.BestBuff != null && predicate(s.BestBuff)) s.BestBuff = null;
            return removed;
        }
    }
}
