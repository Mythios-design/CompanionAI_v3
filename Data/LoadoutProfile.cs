using System;
using System.Collections.Generic;
using System.Text;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.Items.Equipment;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Data
{
    /// <summary>One attack a weapon gives, read from its blueprint.</summary>
    public sealed class WeaponAttackProfile
    {
        public BlueprintAbility Blueprint;
        public string Name;
        public WeaponAbilityType Type;
        public int BaseAP;
        public int RangeTiles;
        /// <summary>Using it puts all weapon attacks on cooldown for the turn.</summary>
        public bool EndsWeaponAttacks;
        public bool UsableWhenEngaged;
        /// <summary>Burst, scatter or area pattern: can hit units near the line of fire.</summary>
        public bool Spreads;
        public bool ChainsToAllies;
        public int ChainRadius;
        public int ChainMax;
    }

    public sealed class WeaponProfile
    {
        public ItemEntityWeapon Item;
        public string Name;
        public bool IsMelee;
        public int RangeTiles;
        public int MaxAmmo;
        /// <summary>AP to reload, -1 when the weapon has no reload.</summary>
        public int ReloadAP = -1;
        public readonly List<WeaponAttackProfile> Attacks = new List<WeaponAttackProfile>();
    }

    public sealed class WeaponSetProfile
    {
        public int Index;
        public WeaponProfile Primary;
        public WeaponProfile Secondary;
        public bool HasWeapons => Primary != null || Secondary != null;

        public IEnumerable<WeaponProfile> Weapons
        {
            get
            {
                if (Primary != null) yield return Primary;
                if (Secondary != null) yield return Secondary;
            }
        }
    }

    /// <summary>
    /// What each character carries in each weapon set. Equipment can't change during combat, so this is built
    /// once per combat (when a character is first seen) and kept until combat ends. Ammo, cooldowns and positions
    /// change during the fight and are read live by the callers.
    /// </summary>
    public static class LoadoutProfiles
    {
        private sealed class Entry
        {
            public string Signature;
            public WeaponSetProfile[] Sets;
        }

        private static readonly Dictionary<string, Entry> _profiles = new Dictionary<string, Entry>();
        private const string WeaponAttackGroupName = "WeaponAttackAbilityGroup";

        public static void Reset() => _profiles.Clear();

        /// <summary>Weapon sets for the unit, or null if it has fewer than two.</summary>
        public static WeaponSetProfile[] Get(BaseUnitEntity unit)
        {
            var sets = unit?.Body?.HandsEquipmentSets;
            if (sets == null || sets.Count < 2) return null;
            try
            {
                string signature = BuildSignature(unit);
                if (_profiles.TryGetValue(unit.UniqueId, out var entry) && entry.Signature == signature)
                    return entry.Sets;

                bool rebuilt = entry != null;
                entry = new Entry { Signature = signature, Sets = Build(unit) };
                _profiles[unit.UniqueId] = entry;
                Log.Analysis.Info($"[Loadout] {unit.CharacterName}{(rebuilt ? " (equipment changed, rebuilt)" : "")}: {Describe(entry.Sets)}");
                return entry.Sets;
            }
            catch (Exception ex)
            {
                Log.Analysis.Error(ex, $"[Loadout] failed to build for {unit?.CharacterName}");
                return null;
            }
        }

        private static string BuildSignature(BaseUnitEntity unit)
        {
            var sb = new StringBuilder();
            foreach (var set in unit.Body.HandsEquipmentSets)
            {
                sb.Append((set?.PrimaryHand?.MaybeItem as ItemEntityWeapon)?.UniqueId ?? "-").Append('/');
                sb.Append((set?.SecondaryHand?.MaybeItem as ItemEntityWeapon)?.UniqueId ?? "-").Append('|');
            }
            return sb.ToString();
        }

        private static WeaponSetProfile[] Build(BaseUnitEntity unit)
        {
            var sets = unit.Body.HandsEquipmentSets;
            var result = new WeaponSetProfile[sets.Count];
            for (int i = 0; i < sets.Count; i++)
            {
                result[i] = new WeaponSetProfile
                {
                    Index = i,
                    // MaybeItem: MaybeWeapon returns null for the set that isn't in hand
                    Primary = BuildWeapon(sets[i]?.PrimaryHand?.MaybeItem as ItemEntityWeapon),
                    Secondary = BuildWeapon(sets[i]?.SecondaryHand?.MaybeItem as ItemEntityWeapon),
                };
            }
            return result;
        }

        private static WeaponProfile BuildWeapon(ItemEntityWeapon weapon)
        {
            var bp = weapon?.Blueprint;
            if (bp == null) return null;

            var profile = new WeaponProfile
            {
                Item = weapon,
                Name = weapon.Name,
                IsMelee = bp.IsMelee,
                RangeTiles = weapon.AttackRange,
                MaxAmmo = bp.IsMelee ? 0 : bp.WarhammerMaxAmmo,
            };

            bool weaponChains = CombatAPI.TryGetOnHitChain(bp.OnHitActions, bp.AssetGuid?.ToString(), weapon.Name,
                out int wRadius, out TargetType wType, out int wMax) && wType != TargetType.Enemy;

            foreach (var wa in bp.WeaponAbilities.All)
            {
                if (wa == null || wa.IsNone || wa.Ability == null) continue;
                if (wa.Type == WeaponAbilityType.Reload)
                {
                    profile.ReloadAP = wa.AP;
                    continue;
                }

                var ability = wa.Ability;
                var attack = new WeaponAttackProfile
                {
                    Blueprint = ability,
                    Name = ability.Name,
                    Type = wa.Type,
                    BaseAP = wa.AP,
                    RangeTiles = profile.RangeTiles,
                    EndsWeaponAttacks = InWeaponAttackGroup(ability),
                    UsableWhenEngaged = bp.IsMelee
                        || ability.UsingInThreateningArea != BlueprintAbility.UsingInThreateningAreaType.CannotUse,
                    Spreads = !bp.IsMelee && IsSpread(wa, ability),
                };

                if (weaponChains)
                {
                    attack.ChainsToAllies = true;
                    attack.ChainRadius = wRadius;
                    attack.ChainMax = wMax;
                }
                else if (CombatAPI.TryGetOnHitChain(wa.OnHitActions, ability.AssetGuid?.ToString(), ability.Name,
                    out int aRadius, out TargetType aType, out int aMax) && aType != TargetType.Enemy)
                {
                    attack.ChainsToAllies = true;
                    attack.ChainRadius = aRadius;
                    attack.ChainMax = aMax;
                }

                profile.Attacks.Add(attack);
            }
            return profile;
        }

        private static bool IsSpread(WeaponAbility wa, BlueprintAbility ability)
        {
            if (wa.Type == WeaponAbilityType.Burst || wa.Type == WeaponAbilityType.AOE) return true;
            if (ability.IsBurst || ability.IsAoE) return true;
            var attackType = ability.AttackType;
            if (attackType.HasValue && attackType.Value != AttackAbilityType.SingleShot)
            {
                // Controlled scatter skips allies on its own
                bool controlled = ability.GetComponent<Kingmaker.UnitLogic.Abilities.Components.WarhammerAbilityAttackDelivery>()?.ControlledScatter ?? false;
                return !controlled;
            }
            return false;
        }

        private static bool InWeaponAttackGroup(BlueprintAbility ability)
        {
            var groups = ability?.AbilityGroups;
            if (groups == null) return false;
            foreach (var g in groups)
                if (g != null && g.name == WeaponAttackGroupName) return true;
            return false;
        }

        public static string Describe(WeaponSetProfile[] sets)
        {
            if (sets == null) return "no weapon sets";
            var sb = new StringBuilder();
            foreach (var set in sets)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append($"Set {set.Index}: ");
                if (!set.HasWeapons) { sb.Append("empty"); continue; }
                bool first = true;
                foreach (var w in set.Weapons)
                {
                    if (!first) sb.Append(" + ");
                    first = false;
                    sb.Append(w.Name).Append(w.IsMelee ? " (melee" : $" (range {w.RangeTiles}, ammo {w.MaxAmmo}, reload {w.ReloadAP} AP");
                    sb.Append("; ");
                    for (int i = 0; i < w.Attacks.Count; i++)
                    {
                        var a = w.Attacks[i];
                        if (i > 0) sb.Append(", ");
                        sb.Append($"{a.Name} {a.BaseAP} AP");
                        if (a.Spreads) sb.Append(" spread");
                        if (a.ChainsToAllies) sb.Append(" chains");
                        if (!a.UsableWhenEngaged) sb.Append(" not-engaged");
                    }
                    sb.Append(')');
                }
            }
            return sb.ToString();
        }
    }
}
