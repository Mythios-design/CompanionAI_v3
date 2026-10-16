using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Stops area taunts (e.g. Taunting Scream) being recast every turn while they still hold.
    ///
    /// A taunt is worth casting when at least one enemy in its area is neither taunted already nor
    /// recently exposed to it. Enemies that resisted (no taunt buff) are not retried until the taunt's
    /// duration has passed, otherwise the tank would spend every turn screaming at the same resisters.
    /// </summary>
    public static class TauntTracker
    {
        /// <summary>Rounds before an enemy that resisted is worth trying again.</summary>
        private const int RetryAfterRounds = 3;

        /// <summary>Area used when the taunt's own radius can't be read.</summary>
        private const float FallbackRadiusTiles = 3f;

        // casterId -> (enemyId -> round they were last caught in the taunt)
        private static readonly Dictionary<string, Dictionary<string, int>> _exposed =
            new Dictionary<string, Dictionary<string, int>>();

        public static void Reset() => _exposed.Clear();

        private static int CurrentRound => Kingmaker.Game.Instance?.TurnController?.CombatRound ?? 0;

        public static float GetRadiusTiles(AbilityData taunt)
        {
            float radius = CombatAPI.GetAoERadius(taunt);
            return radius > 0f ? radius : FallbackRadiusTiles;
        }

        /// <summary>
        /// True when casting the taunt now would affect at least one enemy it isn't already holding.
        /// Only applies to taunts that put a buff on enemies; anything else is left to the normal rules.
        /// </summary>
        public static bool IsWorthCasting(BaseUnitEntity caster, AbilityData taunt, List<BaseUnitEntity> enemies, out string reason)
        {
            reason = null;
            var buffs = AbilityBuffMap.Get(taunt?.Blueprint)?.TargetBuffs;
            if (caster == null || buffs == null || buffs.Count == 0 || enemies == null)
                return true;

            float radius = GetRadiusTiles(taunt);
            _exposed.TryGetValue(caster.UniqueId, out var exposedMap);
            int round = CurrentRound;

            int inArea = 0, held = 0, recentlyResisted = 0;
            foreach (var enemy in enemies)
            {
                if (enemy == null || !enemy.IsConscious) continue;
                if (CombatAPI.GetDistanceInTiles(caster, enemy) > radius) continue;
                inArea++;

                if (HasAnyBuff(enemy, buffs)) { held++; continue; }

                if (exposedMap != null && exposedMap.TryGetValue(enemy.UniqueId, out int lastRound)
                    && round - lastRound < RetryAfterRounds)
                {
                    recentlyResisted++;
                    continue;
                }

                reason = $"{enemy.CharacterName} not taunted yet";
                return true;
            }

            reason = inArea == 0
                ? "no enemies in range"
                : $"all {inArea} enemies in range already taunted ({held}) or recently resisted ({recentlyResisted})";
            return false;
        }

        /// <summary>Remember which enemies were caught by a taunt that was just cast.</summary>
        public static void RecordCast(BaseUnitEntity caster, AbilityData taunt, List<BaseUnitEntity> enemies)
        {
            if (caster == null || enemies == null) return;
            float radius = GetRadiusTiles(taunt);
            int round = CurrentRound;

            if (!_exposed.TryGetValue(caster.UniqueId, out var map))
            {
                map = new Dictionary<string, int>();
                _exposed[caster.UniqueId] = map;
            }

            int count = 0;
            foreach (var enemy in enemies)
            {
                if (enemy == null || !enemy.IsConscious) continue;
                if (CombatAPI.GetDistanceInTiles(caster, enemy) > radius) continue;
                map[enemy.UniqueId] = round;
                count++;
            }
            Log.Planning.Info($"[TauntTracker] {caster.CharacterName}: {taunt?.Name} caught {count} enemies (round {round})");
        }

        private static bool HasAnyBuff(BaseUnitEntity unit, List<Kingmaker.UnitLogic.Buffs.Blueprints.BlueprintBuff> buffs)
        {
            foreach (var buff in buffs)
            {
                if (unit.Buffs.GetBuff(buff) != null) return true;
            }
            return false;
        }
    }
}
