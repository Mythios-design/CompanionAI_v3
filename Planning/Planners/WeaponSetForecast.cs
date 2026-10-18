using System;
using System.Collections.Generic;
using System.Text;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.Pathfinding;
using Kingmaker.View.Covers;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Predicts what a weapon set could do this turn, without switching to it. The game keeps the abilities of the
    /// set that isn't in hand switched off, so this works from the loadout profile plus live ammo, positions, line of
    /// sight and the same friendly-fire rules the active weapon gets. It is a forecast: once switched, the normal
    /// analysis re-checks everything with the real abilities.
    /// </summary>
    public static class WeaponSetForecast
    {
        public sealed class Result
        {
            public int SetIndex;
            public string SetName;
            /// <summary>Enemies the set can hit from the current tile.</summary>
            public int HittableHere;
            /// <summary>Enemies attackable after spending the remaining MP: melee in reach, ranged in range with line of sight from a reachable tile.</summary>
            public int ReachableAfterMove;
            /// <summary>Why the set can't attack at all, or null.</summary>
            public string Unusable;
            /// <summary>Enemies ruled out because of allies (chain, spread), for the log.</summary>
            public readonly List<string> AllyBlocked = new List<string>();

            public bool CanAttackHere => Unusable == null && HittableHere > 0;

            public string Summary()
            {
                if (Unusable != null) return $"{SetName} can't attack ({Unusable})";
                var sb = new StringBuilder($"{SetName} hits {HittableHere}");
                if (ReachableAfterMove > HittableHere) sb.Append($" ({ReachableAfterMove} after moving)");
                if (AllyBlocked.Count > 0) sb.Append($", ally risk: {string.Join(", ", AllyBlocked)}");
                return sb.ToString();
            }
        }

        public static Result Evaluate(Situation situation, int setIndex)
        {
            var result = new Result { SetIndex = setIndex, SetName = $"Set {setIndex}" };
            var unit = situation?.Unit;
            var sets = LoadoutProfiles.Get(unit);
            if (sets == null || setIndex < 0 || setIndex >= sets.Length || !sets[setIndex].HasWeapons)
            {
                result.Unusable = "no weapons";
                return result;
            }

            var set = sets[setIndex];
            result.SetName = set.Primary?.Name ?? set.Secondary?.Name ?? result.SetName;
            try
            {
                Fill(situation, set, result);
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[WeaponForecast] {unit?.CharacterName} set {setIndex}");
                result.Unusable = "forecast failed";
            }
            return result;
        }

        private static void Fill(Situation situation, WeaponSetProfile set, Result result)
        {
            var unit = situation.Unit;
            float ap = situation.CurrentAP;
            bool engaged = CombatAPI.IsInThreateningArea(unit);

            // Attacks the set could afford now (reloading first counts if AP allows both)
            var usable = new List<(WeaponProfile weapon, WeaponAttackProfile attack)>();
            string lastReason = null;
            foreach (var weapon in set.Weapons)
            {
                float reloadCost = 0f;
                if (!weapon.IsMelee && weapon.MaxAmmo > 0 && weapon.Item.CurrentAmmo <= 0)
                {
                    if (weapon.ReloadAP < 0) { lastReason = $"{weapon.Name} out of ammo"; continue; }
                    reloadCost = weapon.ReloadAP;
                }
                foreach (var attack in weapon.Attacks)
                {
                    if (engaged && !attack.UsableWhenEngaged) { lastReason = $"{weapon.Name} can't fire next to enemies"; continue; }
                    if (attack.BaseAP + reloadCost > ap + 0.01f)
                    {
                        lastReason = reloadCost > 0 ? $"{weapon.Name} needs {weapon.ReloadAP} AP reload" : $"not enough AP for {attack.Name}";
                        continue;
                    }
                    usable.Add((weapon, attack));
                }
            }
            if (usable.Count == 0)
            {
                result.Unusable = lastReason ?? "no attacks";
                return;
            }

            var fromNode = unit.CurrentUnwalkableNode;
            bool avoidFriendlyFire = situation.CharacterSettings?.AvoidFriendlyFire ?? true;
            bool arcInsulated = CombatAPI.HasArcInsulationBuff(unit);
            int maxAlliesHit = AIConfig.GetAoEConfig()?.MaxPlayerAlliesHit ?? 1;
            float mp = situation.CurrentMP;
            List<CustomGridNodeBase> moveTiles = null;  // tiles reachable this turn, built on first ranged check

            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null || !enemy.IsConscious || enemy.LifeState.IsDead) continue;
                if (CombatAPI.IsTargetImmuneToDamage(enemy, unit)) continue;

                float dist = CombatAPI.GetDistanceInTiles(unit, enemy);
                bool hereInRange = false, reachable = false, hereHit = false, allyBlocked = false;
                float rangedReach = 0f;  // longest ranged attack that could reach this enemy after moving

                foreach (var (weapon, attack) in usable)
                {
                    float range = weapon.IsMelee ? Math.Max(attack.RangeTiles, 1) + 0.5f : attack.RangeTiles;
                    // A chain onto allies depends only on where the target stands, so moving doesn't help:
                    // that enemy is neither hittable here nor reachable after moving with this attack
                    bool chainBlocked = avoidFriendlyFire && ChainHitsAllies(attack, unit, enemy, arcInsulated, maxAlliesHit);
                    if (!chainBlocked && dist <= range + mp)
                    {
                        if (weapon.IsMelee) reachable = true;
                        else rangedReach = Math.Max(rangedReach, range);
                    }
                    if (dist > range) continue;
                    hereInRange = true;
                    if (chainBlocked) { allyBlocked = true; continue; }

                    if (!weapon.IsMelee && !HasLineOfSight(fromNode, unit, enemy)) continue;
                    if (avoidFriendlyFire && !IsAllySafe(attack, unit, enemy, situation.Allies, arcInsulated, maxAlliesHit))
                    {
                        allyBlocked = true;
                        continue;
                    }
                    hereHit = true;
                    break;
                }

                if (hereHit) { result.HittableHere++; reachable = true; }
                else if (hereInRange && allyBlocked) result.AllyBlocked.Add(enemy.CharacterName);
                if (!reachable && rangedReach > 0f)
                {
                    if (moveTiles == null) moveTiles = ReachableTiles(unit, mp);
                    reachable = CanSeeFromAnyTile(moveTiles, unit, enemy, rangedReach);
                }
                if (reachable) result.ReachableAfterMove++;
            }
        }

        private static List<CustomGridNodeBase> ReachableTiles(BaseUnitEntity unit, float mp)
        {
            var list = new List<CustomGridNodeBase>();
            if (mp <= 0f) return list;
            var tiles = MovementAPI.FindAllReachableTilesWithThreatsSync(unit, mp);
            if (tiles == null) return list;
            foreach (var cell in tiles.Values)
                if (cell.IsCanStand && cell.Node is CustomGridNodeBase node) list.Add(node);
            return list;
        }

        /// <summary>A tile reachable this turn that is within range of the enemy and can see it.</summary>
        private static bool CanSeeFromAnyTile(List<CustomGridNodeBase> tiles, BaseUnitEntity unit, BaseUnitEntity enemy, float range)
        {
            foreach (var node in tiles)
            {
                if (CombatAPI.GetDistanceInTiles(node.Vector3Position, enemy) > range) continue;
                if (HasLineOfSight(node, unit, enemy)) return true;
            }
            return false;
        }

        private static bool HasLineOfSight(CustomGridNodeBase fromNode, BaseUnitEntity unit, BaseUnitEntity enemy)
        {
            var enemyNode = enemy.CurrentUnwalkableNode;
            if (fromNode == null || enemyNode == null) return false;
            var los = LosCalculations.GetWarhammerLos(fromNode, unit.SizeRect, enemyNode, enemy.SizeRect);
            return los.CoverType != LosCalculations.CoverType.Invisible;
        }

        private static bool ChainHitsAllies(WeaponAttackProfile attack, BaseUnitEntity unit, BaseUnitEntity enemy,
            bool arcInsulated, int maxAlliesHit)
            => attack.ChainsToAllies && !arcInsulated
               && AoESafetyChecker.SimulateWeaponChainAllyHits(enemy, unit, attack.ChainRadius, attack.ChainMax) > maxAlliesHit;

        private static bool IsAllySafe(WeaponAttackProfile attack, BaseUnitEntity unit, BaseUnitEntity enemy,
            List<BaseUnitEntity> allies, bool arcInsulated, int maxAlliesHit)
        {
            if (ChainHitsAllies(attack, unit, enemy, arcInsulated, maxAlliesHit))
                return false;

            if (attack.Spreads && allies != null)
            {
                // Same idea as the active weapon's spread check: no ally close to the line of fire or the target
                Vector3 from = unit.Position, to = enemy.Position;
                Vector3 line = to - from;
                float lengthSq = line.sqrMagnitude;
                foreach (var ally in allies)
                {
                    if (ally == null || ally == unit || !ally.IsConscious || CombatAPI.IsImmuneToFriendlyFire(ally)) continue;
                    if (CombatAPI.GetDistanceInTiles(ally, enemy) <= 1.5f) return false;
                    if (lengthSq < 0.01f) continue;
                    float t = Vector3.Dot(ally.Position - from, line) / lengthSq;
                    if (t <= 0f || t > 1.2f) continue;
                    float perp = CombatAPI.MetersToTiles(Vector3.Distance(ally.Position, from + line * t));
                    if (perp < 1.5f) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// True when a weapon attack that ends weapon attacks was made this turn: every weapon attack in hand is on
        /// cooldown, so the other set's are too (they share the game's weapon attack group).
        /// </summary>
        public static bool WeaponAttacksSpent(BaseUnitEntity unit)
        {
            var raw = unit?.Abilities?.RawFacts;
            if (raw == null) return false;
            var primary = unit.Body?.PrimaryHand?.MaybeItem as ItemEntityWeapon;
            var secondary = unit.Body?.SecondaryHand?.MaybeItem as ItemEntityWeapon;
            bool any = false;
            foreach (var fact in raw)
            {
                var data = fact?.Data;
                if (data?.Weapon == null || (data.Weapon != primary && data.Weapon != secondary)) continue;
                if (AbilityDatabase.IsReload(data) || !AbilityPolicy.EndsWeaponAttacks(data)) continue;
                any = true;
                if (!data.IsOnCooldown) return false;
            }
            return any;
        }
    }
}
