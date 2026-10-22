using System.Collections.Generic;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Abilities.Components.ProjectileAttack;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// Arch-Militant (Veteran) keystone, read from the game's own state (Veteran_Keystone_Feature).
    /// Each attack whose kind differs from the previous attack's kind adds a combat-long stack (+3 BS and WS each;
    /// 4 or more raise minimum damage and unlock the stance bonuses). The previous kind is a marker buff kept
    /// across turns. Six kinds, matched like the keystone's triggers:
    ///  - Melee weapon (not in the excluded group): area if the ability has a pattern (scatter included), burst if
    ///    it's a scatter attack (an ability can be both), single otherwise.
    ///  - Ranged weapon: area if it has a pattern that isn't scatter, burst if scatter, single if a single shot.
    ///  - Grenades count as area shots; psychic area, burst and single-target attacks as the matching shot.
    /// Item and psychic-power markers exist in the data but nothing ever sets them.
    /// </summary>
    public static class Versatility
    {
        public enum Kind { AreaMelee, AreaShot, SingleMelee, BurstShot, SingleShot, BurstMelee }

        private const string KEYSTONE = "7f7d109653f24774939f758d094b8812";       // Veteran_Keystone_Feature
        private const string STACKS = "2f7145a22ec44fa69b760af7750a467c";         // Veteran_Keystone_BuffStacks
        private const string EXCLUDED_MELEE_GROUP = "73f152d564dc482289fc8a753ab3d571";
        private const string GRENADE_GROUP = "1ee8edc6003840bb810840dc95e0de5f";
        private const string PSYKER_AREA_GROUP = "a74be656d48d43679f32caecb50ad68e";
        private const string PSYKER_BURST_GROUP = "3d68d893b2e14488b0699d4555a333e9";
        private const string PSYKER_SINGLE_GROUP = "bd95741e8ded4120bdb186dbeeb06303";

        private static readonly Dictionary<string, Kind> Markers = new Dictionary<string, Kind>
        {
            ["6ccaabf55b294788b1ce282066ea48a8"] = Kind.AreaMelee,
            ["037de94fa4324857955158762ea5992c"] = Kind.AreaShot,
            ["7e5dc8ae9cfc492f95e45d84b8a418ae"] = Kind.SingleMelee,
            ["3b66a03e3adb449c84f20036e74deada"] = Kind.BurstShot,
            ["b511876936f04be0af639f20efea6edc"] = Kind.SingleShot,
            ["d0c02ac122ba43bca7960d4119bd2108"] = Kind.BurstMelee,
        };

        public static bool HasKeystone(BaseUnitEntity unit)
        {
            if (unit?.Facts == null) return false;
            foreach (var fact in unit.Facts.List)
                if (fact?.Blueprint?.AssetGuid?.ToString() == KEYSTONE) return true;
            return false;
        }

        /// <summary>Current stacks, Reckless Rush's temporary ones included.</summary>
        public static int Stacks(BaseUnitEntity unit)
        {
            if (unit?.Buffs == null) return 0;
            foreach (var buff in unit.Buffs.RawFacts)
                if (buff?.Blueprint?.AssetGuid?.ToString() == STACKS) return buff.Rank;
            return 0;
        }

        /// <summary>The kind of the unit's last attack, or null before its first.</summary>
        public static Kind? LastKind(BaseUnitEntity unit)
        {
            if (unit?.Buffs == null) return null;
            foreach (var buff in unit.Buffs.RawFacts)
                if (Markers.TryGetValue(buff?.Blueprint?.AssetGuid?.ToString() ?? "", out var kind)) return kind;
            return null;
        }

        /// <summary>The kinds this ability counts as (none for abilities that aren't attacks).</summary>
        public static List<Kind> Kinds(AbilityData ability)
        {
            var kinds = new List<Kind>();
            var bp = ability?.Blueprint;
            if (bp == null) return kinds;
            var weapon = ability.Weapon;
            bool melee = weapon != null && weapon.Blueprint.IsMelee && !InGroup(ability, EXCLUDED_MELEE_GROUP);
            bool ranged = weapon != null && !weapon.Blueprint.IsMelee;

            if (melee && IsAoE(ability, includeScatter: true)) kinds.Add(Kind.AreaMelee);
            if ((ranged && IsAoE(ability, includeScatter: false)) || InGroup(ability, GRENADE_GROUP) || InGroup(ability, PSYKER_AREA_GROUP))
                kinds.Add(Kind.AreaShot);
            if (melee && !IsAoE(ability, includeScatter: true)) kinds.Add(Kind.SingleMelee);
            if ((ranged && ability.IsScatter) || InGroup(ability, PSYKER_BURST_GROUP)) kinds.Add(Kind.BurstShot);
            if ((ranged && ability.IsSingleShot) || InGroup(ability, PSYKER_SINGLE_GROUP)) kinds.Add(Kind.SingleShot);
            if (melee && ability.IsScatter) kinds.Add(Kind.BurstMelee);
            return kinds;
        }

        /// <summary>True when the unit has the keystone and this attack would add a stack (a kind other than the last one).</summary>
        public static bool EarnsStack(AbilityData ability, BaseUnitEntity unit)
        {
            if (!HasKeystone(unit)) return false;
            var kinds = Kinds(ability);
            if (kinds.Count == 0) return false;
            var last = LastKind(unit);
            foreach (var k in kinds)
                if (last != k) return true;
            return false;
        }

        /// <summary>The game's CheckAbilityIsAoEGetter (DoNotIncludeScatter = !includeScatter).</summary>
        private static bool IsAoE(AbilityData ability, bool includeScatter)
        {
            var bp = ability.Blueprint;
            var pattern = bp.PatternSettings;
            if (pattern != null && (includeScatter || !(pattern is ScatterPattern))) return true;
            var features = ability.Caster?.Features;
            return features != null && (bool)features.AllAttacksCountAsAoe
                && (bp.GetComponent<WarhammerAbilityAttackDelivery>() != null || bp.GetComponent<AbilityMeleeBurst>() != null);
        }

        private static bool InGroup(AbilityData ability, string groupGuid)
        {
            var groups = ability?.AbilityGroups;
            if (groups == null) return false;
            foreach (var g in groups)
                if (g?.AssetGuid?.ToString() == groupGuid) return true;
            return false;
        }
    }
}
