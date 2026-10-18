using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Buffs.Components;
using Kingmaker.UnitLogic.Parts;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// How many more attacks from the weapon attack group (Single Shot, Burst Fire and the like) the game allows
    /// this turn. Using one normally puts the whole group on cooldown, but the game has three ways around that,
    /// all read here from the game's own state so new talents are covered without naming them:
    ///  - Free uses: entries in the unit's bonus ability list (Never Stop Shooting, Run and Gun, Blood Oath's
    ///    kill reward, Finest Hour!, ...). Each matching entry adds its count.
    ///  - Cooldown override buffs (WarhammerOverrideGroupCooldown) on the weapon attack group: no limit.
    ///  - Ignore-cooldown rules (WarhammerIgnoreGroupCooldownByBuff), checked by the game's IsIgnoredByComponent:
    ///    no limit.
    /// When something can't be read the answer is "no limit", which is how the planner behaved before.
    /// Grants that arrive mid-turn are not seen here; the replan after them picks them up.
    /// </summary>
    public static class WeaponAttackAllowance
    {
        public const int Unlimited = int.MaxValue;
        private const string WeaponAttackGroupName = "WeaponAttackAbilityGroup";

        // The bonus list and its entry type are private on the game side
        private static readonly FieldInfo BonusesField = AccessTools.Field(typeof(UnitPartBonusAbility), "m_Bonuses");
        private static readonly Type BonusDataType = AccessTools.Inner(typeof(UnitPartBonusAbility), "BonusAbilityData");
        private static readonly PropertyInfo BonusCountProp = BonusDataType != null ? AccessTools.Property(BonusDataType, "Count") : null;
        private static readonly MethodInfo BonusMatchesMethod = BonusDataType != null ? AccessTools.Method(BonusDataType, "IsCorrectAbility") : null;
        private static bool _readFailureLogged;

        /// <summary>
        /// Weapon attacks the game still allows this turn for this ability, counting the one normal attack if the
        /// group isn't on cooldown yet. Unlimited when an override or ignore rule applies, or when unreadable.
        /// </summary>
        public static int Remaining(BaseUnitEntity unit, AbilityData ability, out string why)
        {
            why = null;
            if (unit == null || ability == null) return Unlimited;
            try
            {
                var group = FindWeaponAttackGroup(ability);
                if (group == null) { why = "not a weapon attack"; return Unlimited; }

                var cooldowns = unit.GetAbilityCooldownsOptional();
                if (cooldowns != null && cooldowns.IsIgnoredByComponent(group, ability))
                {
                    why = "ignores the weapon attack cooldown";
                    return Unlimited;
                }

                string overrideBuff = FindCooldownOverride(unit, group);
                if (overrideBuff != null)
                {
                    why = $"cooldown overridden by {overrideBuff}";
                    return Unlimited;
                }

                int normal = cooldowns != null && cooldowns.GroupIsOnCooldown(group) ? 0 : 1;
                if (!TryCountFreeUses(unit, ability, out int free))
                {
                    why = "free uses unreadable";
                    return Unlimited;
                }
                why = free > 0 ? $"{normal} normal + {free} free" : $"{normal} normal";
                return normal + free;
            }
            catch (Exception ex)
            {
                LogReadFailure(ex, $"Remaining failed for {unit.CharacterName} / {ability.Name}");
                why = "unreadable";
                return Unlimited;
            }
        }

        private static BlueprintAbilityGroup FindWeaponAttackGroup(AbilityData ability)
        {
            var groups = ability.Blueprint?.AbilityGroups;
            if (groups == null) return null;
            foreach (var g in groups)
                if (g != null && g.name == WeaponAttackGroupName) return g;
            return null;
        }

        /// <summary>Name of an active buff that overrides the weapon attack group's cooldown, else null.</summary>
        private static string FindCooldownOverride(BaseUnitEntity unit, BlueprintAbilityGroup group)
        {
            var buffs = unit.Buffs?.Enumerable;
            if (buffs == null) return null;
            foreach (var buff in buffs)
            {
                var components = buff?.Blueprint?.ComponentsArray;
                if (components == null) continue;
                foreach (var c in components)
                {
                    if (!(c is WarhammerOverrideGroupCooldown over)) continue;
                    var affected = over.AffectedGroup;
                    if (affected == null) continue;
                    var all = affected.GetAllAbilityGroups();
                    if (affected == group || (all != null && all.Contains(group)))
                        return buff.Blueprint.name;
                }
            }
            return null;
        }

        /// <summary>Sum of the counts of the unit's free-use entries that apply to this ability.</summary>
        private static bool TryCountFreeUses(BaseUnitEntity unit, AbilityData ability, out int free)
        {
            free = 0;
            var part = unit.GetBonusAbilityUseOptional();
            if (part == null) return true;
            if (BonusesField == null || BonusCountProp == null || BonusMatchesMethod == null)
            {
                LogReadFailure(null, "bonus ability list layout not found (game update?)");
                return false;
            }
            if (!(BonusesField.GetValue(part) is IList list)) return true;
            var args = new object[] { ability };
            foreach (var entry in list)
            {
                if (entry == null) continue;
                if (!(BonusMatchesMethod.Invoke(entry, args) is bool matches) || !matches) continue;
                if (BonusCountProp.GetValue(entry) is int count && count > 0) free += count;
            }
            return true;
        }

        private static void LogReadFailure(Exception ex, string context)
        {
            if (_readFailureLogged) return;
            _readFailureLogged = true;
            if (ex != null) Log.Engine.Error(ex, $"[WeaponAttackAllowance] {context} - planning weapon attacks without a limit");
            else Log.Engine.Warn($"[WeaponAttackAllowance] {context} - planning weapon attacks without a limit");
        }
    }
}
