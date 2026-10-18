using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Core;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Data
{
    /// <summary>
    /// Abilities that go on cooldown once cast, so they can be used only once per turn (e.g. Voice of Command).
    /// The game data doesn't say so up front (cooldown 0, unlimited charges), so this is learned: after a
    /// successful cast, the next analysis of that unit checks whether the ability is now on cooldown.
    /// Kept for the whole game session, since it's part of how the ability works.
    /// Weapon attacks are left out: their cooldown is the shared weapon attack limit, which
    /// WeaponAttackAllowance handles with its free uses and overrides.
    /// </summary>
    public static class OncePerTurnAbilities
    {
        private static readonly HashSet<string> _known = new HashSet<string>();
        // Per unit: abilities cast since its last analysis
        private static readonly Dictionary<string, List<AbilityData>> _pending = new Dictionary<string, List<AbilityData>>();

        public static bool IsOncePerTurn(AbilityData ability)
        {
            string guid = ability?.Blueprint?.AssetGuid?.ToString();
            return guid != null && _known.Contains(guid);
        }

        /// <summary>Called from TurnState.RecordAction for a successful action.</summary>
        public static void OnActionExecuted(BaseUnitEntity unit, PlannedAction action)
        {
            var ability = action?.Ability;
            if (unit == null || ability?.Blueprint == null || ability.Weapon != null) return;
            if (Planning.Planners.AbilityPolicy.EndsWeaponAttacks(ability)) return;
            string guid = ability.Blueprint.AssetGuid?.ToString();
            if (guid == null || _known.Contains(guid)) return;
            if (!_pending.TryGetValue(unit.UniqueId, out var list))
                _pending[unit.UniqueId] = list = new List<AbilityData>();
            list.Add(ability);
        }

        /// <summary>Called when a unit is analysed: learns from the casts made since the last analysis.</summary>
        public static void Observe(BaseUnitEntity unit)
        {
            if (unit == null || !_pending.TryGetValue(unit.UniqueId, out var list) || list.Count == 0) return;
            try
            {
                foreach (var ability in list)
                {
                    if (!IsOnCooldown(ability)) continue;
                    string guid = ability.Blueprint.AssetGuid?.ToString();
                    if (guid != null && _known.Add(guid))
                        Log.Persistence.Info($"[OncePerTurn] {ability.Name} went on cooldown after {unit.CharacterName} cast it - planned at most once per turn from now on");
                }
            }
            catch (Exception ex)
            {
                Log.Persistence.Error(ex, $"[OncePerTurn] Observe failed for {unit.CharacterName}");
            }
            finally
            {
                list.Clear();
            }
        }

        private static bool IsOnCooldown(AbilityData ability)
        {
            foreach (var r in ability.GetUnavailabilityReasons())
                if (r == AbilityData.UnavailabilityReasonType.IsOnCooldown) return true;
            return false;
        }

        /// <summary>Drops casts not yet checked (combat start and end). Learned abilities are kept.</summary>
        public static void ClearPending() => _pending.Clear();
    }
}
