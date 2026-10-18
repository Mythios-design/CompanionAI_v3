using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Rough damage an ally would deal with a full extra turn (full AP and MP, no attack limit), used to choose who
    /// gets Finest Hour!. For each of the ally's enemy-targeting attacks: if an enemy is within the attack's range
    /// plus the ally's full movement, damage = the mod's damage estimate against the best such enemy (times the
    /// enemies in the area for area attacks), times how many times full AP pays for it. The best attack wins.
    /// 0 when no attack can reach any enemy.
    /// Approximate: straight-line distance, no path or line-of-sight check, and no setup buffs counted.
    /// </summary>
    public static class ExtraTurnValue
    {
        private static readonly HashSet<string> FinestHourGuids = new HashSet<string>
        {
            "33c8c9db91694ca6ad3eff26e36dd0af",  // Finest Hour! (Heroic Act)
            "3f78bec2aea340a780e47f0a2a8dfb5f",  // Finest Hour! (Desperate Measure)
        };

        public static bool IsFinestHour(AbilityData ability)
        {
            string guid = ability?.Blueprint?.AssetGuid?.ToString();
            return guid != null && FinestHourGuids.Contains(guid);
        }

        public static float Estimate(BaseUnitEntity ally, List<BaseUnitEntity> enemies, out string detail)
        {
            detail = "no enemy in reach";
            if (ally == null || enemies == null) return 0f;
            try
            {
                float fullAP = ReadStat(ally, true, 4f);
                float fullMP = ReadStat(ally, false, 6f);
                float best = 0f;
                var raw = ally.Abilities?.RawFacts;
                if (raw == null) return 0f;

                foreach (var fact in raw)
                {
                    var ability = fact?.Data;
                    var bp = ability?.Blueprint;
                    if (bp == null || !bp.CanTargetEnemies || bp.IsHeroicAct || bp.IsDesperateMeasure) continue;
                    if (AbilityDatabase.IsReload(ability) || AbilityDatabase.IsGapCloser(ability)) continue;
                    if (ability.Weapon == null && !bp.IsPsykerAbility) continue;

                    float range = Math.Max(1f, CombatAPI.GetAbilityRangeInTiles(ability));
                    if (range >= 1000f) range = 100f;
                    float cost = Math.Max(1f, CombatAPI.GetAbilityAPCost(ability));
                    int uses = Math.Max(1, (int)(fullAP / cost));
                    float radius = CombatAPI.GetAoERadius(ability);

                    float perUse = 0f;
                    string perUseTarget = null;
                    int perUseHits = 1;
                    foreach (var enemy in enemies)
                    {
                        if (enemy == null || !enemy.IsConscious || enemy.LifeState.IsDead) continue;
                        if (CombatAPI.IsTargetImmuneToDamage(enemy, ally)) continue;
                        if (CombatAPI.GetDistanceInTiles(ally, enemy) > range + fullMP) continue;
                        int hits = radius > 0f ? CountWithin(enemy, enemies, radius) : 1;
                        float dmg = CombatAPI.EstimateDamage(ability, enemy) * hits;
                        if (dmg <= perUse) continue;
                        perUse = dmg;
                        perUseTarget = enemy.CharacterName;
                        perUseHits = hits;
                    }
                    float total = perUse * uses;
                    if (total <= best) continue;
                    best = total;
                    detail = $"{uses}x {ability.Name}" + (perUseHits > 1 ? $" on {perUseHits} enemies" : $" on {perUseTarget}");
                }
                return best;
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[ExtraTurnValue] estimate failed for {ally.CharacterName}");
                detail = "estimate failed";
                return 0f;
            }
        }

        /// <summary>
        /// Allies ordered by their full-turn damage, highest first; allies who can't reach an enemy are dropped.
        /// </summary>
        public static List<BaseUnitEntity> Rank(IEnumerable<BaseUnitEntity> allies, BaseUnitEntity caster, List<BaseUnitEntity> enemies,
            string abilityName, string roleName)
        {
            var scored = new List<(BaseUnitEntity ally, float value, string detail, bool acted)>();
            var parts = new List<string>();
            foreach (var ally in allies)
            {
                if (ally == null || ally == caster || !ally.IsConscious) continue;
                float v = Estimate(ally, enemies, out string detail);
                parts.Add($"{ally.CharacterName} {v:F0} ({detail})");
                if (v <= 0f) continue;
                scored.Add((ally, v, detail, ally.Initiative?.ActedThisRound == true));
            }
            // Highest damage first; on a tie, an ally who hasn't acted yet this round
            scored.Sort((a, b) =>
            {
                int c = b.value.CompareTo(a.value);
                return c != 0 ? c : a.acted.CompareTo(b.acted);
            });
            Log.Planning.Info($"[{roleName}] {abilityName} full-turn damage: {string.Join("; ", parts)}");
            var result = new List<BaseUnitEntity>();
            foreach (var s in scored) result.Add(s.ally);
            return result;
        }

        private static int CountWithin(BaseUnitEntity centre, List<BaseUnitEntity> enemies, float radius)
        {
            int n = 0;
            foreach (var e in enemies)
                if (e != null && e.IsConscious && !e.LifeState.IsDead && CombatAPI.GetDistanceInTiles(centre, e) <= radius) n++;
            return Math.Max(1, n);
        }

        /// <summary>Full AP (yellow) or MP (blue) from the unit's stats; fallback when unreadable.</summary>
        private static float ReadStat(BaseUnitEntity unit, bool ap, float fallback)
        {
            var stat = ap ? unit.CombatState?.WarhammerInitialAPYellow : unit.CombatState?.WarhammerInitialAPBlue;
            if (stat != null) return (float)stat.ModifiedValue;
            return fallback;
        }
    }
}
