using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.PubSubSystem.Core;
using Kingmaker.PubSubSystem.Core.Interfaces;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Core
{
    /// <summary>
    /// Player-configured Heroic Act rules, set per character in the mod settings:
    /// - AllowHeroicActs = false: the AI never uses that character's Heroic Acts.
    /// - HeroicActOrder = 1, 2, 3...: ranked characters use Heroic Acts in that order. A character waits
    ///   until every higher-ranked party member has used theirs this combat (or cannot: dead, out of
    ///   combat, disabled, or has no Heroic Act). Unranked characters (0) wait for all ranked ones.
    ///   With nobody ranked, no ordering applies.
    /// Usage is tracked from the game's ability events, so Heroic Acts you cast manually count too.
    /// </summary>
    public class HeroicActPolicy : IAbilityExecutionProcessHandler, ISubscriber
    {
        private static HeroicActPolicy _instance;
        public static HeroicActPolicy Instance => _instance ??= new HeroicActPolicy();

        private readonly HashSet<string> _usedThisCombat = new HashSet<string>();
        private bool _isSubscribed;

        public void Subscribe()
        {
            if (_isSubscribed) return;
            EventBus.Subscribe(this);
            _isSubscribed = true;
        }

        public void Unsubscribe()
        {
            if (!_isSubscribed) return;
            EventBus.Unsubscribe(this);
            _isSubscribed = false;
        }

        /// <summary>Called at combat start and end.</summary>
        public void ResetForCombat() => _usedThisCombat.Clear();

        public void HandleExecutionProcessStart(AbilityExecutionContext context)
        {
            try
            {
                if (context?.AbilityBlueprint == null || !context.AbilityBlueprint.IsHeroicAct) return;
                if (!(context.Caster is BaseUnitEntity caster)) return;
                if (_usedThisCombat.Add(caster.UniqueId))
                    Log.Planning.Info($"[HeroicActPolicy] {caster.CharacterName} used Heroic Act {context.AbilityBlueprint.name}");
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, "[HeroicActPolicy] HandleExecutionProcessStart failed");
            }
        }

        public void HandleExecutionProcessEnd(AbilityExecutionContext context) { }

        public bool HasUsedThisCombat(BaseUnitEntity unit) => unit != null && _usedThisCombat.Contains(unit.UniqueId);

        /// <summary>
        /// Whether the AI may use a Heroic Act for this unit right now.
        /// Also reports whether player ordering is in effect, so the automatic "carry" priority can step aside.
        /// </summary>
        public bool IsAllowed(BaseUnitEntity unit, out string reason, out bool orderingActive)
        {
            reason = null;
            orderingActive = false;
            if (unit == null) return false;

            var mySettings = FindSettings(unit);
            if (mySettings != null && !mySettings.AllowHeroicActs)
            {
                reason = "disabled for this character";
                return false;
            }

            var party = Game.Instance?.Player?.PartyAndPets;
            if (party == null) return true;

            int myRank = EffectiveRank(mySettings);
            foreach (var other in party)
            {
                if (other == null) continue;
                var otherSettings = FindSettings(other);
                if (otherSettings != null && otherSettings.HeroicActOrder > 0) orderingActive = true;
            }
            if (!orderingActive) return true;

            foreach (var other in party)
            {
                if (other == null || other.UniqueId == unit.UniqueId) continue;
                var otherSettings = FindSettings(other);
                if (EffectiveRank(otherSettings) >= myRank) continue;
                if (!IsWaitingOn(other, otherSettings)) continue;

                reason = $"waiting for {other.CharacterName} (order {otherSettings.HeroicActOrder})";
                return false;
            }
            return true;
        }

        /// <summary>A higher-ranked character still holds an unused Heroic Act they can use this combat.</summary>
        private bool IsWaitingOn(BaseUnitEntity other, CharacterSettings otherSettings)
        {
            if (otherSettings == null || !otherSettings.AllowHeroicActs) return false;
            if (_usedThisCombat.Contains(other.UniqueId)) return false;
            if (other.LifeState.IsDead || !other.IsConscious || !other.IsInCombat) return false;
            return HasUnusedHeroicAct(other);
        }

        /// <summary>
        /// Has a Heroic Act that is not on cooldown. The cooldown check also covers Heroic Acts used
        /// before a save was loaded, which the event tracking cannot see.
        /// </summary>
        private static bool HasUnusedHeroicAct(BaseUnitEntity unit)
        {
            var abilities = unit.Abilities?.RawFacts;
            if (abilities == null) return false;
            foreach (var ability in abilities)
            {
                if (ability?.Blueprint == null || !ability.Blueprint.IsHeroicAct) continue;
                if (!GameInterface.CombatAPI.IsAbilityOnCooldownWithGroups(ability.Data)) return true;
            }
            return false;
        }

        /// <summary>Ranked characters by their number; unranked ones after all ranked.</summary>
        private static int EffectiveRank(CharacterSettings settings)
            => settings != null && settings.HeroicActOrder > 0 ? settings.HeroicActOrder : int.MaxValue;

        /// <summary>Existing settings only — never creates entries for pets or unconfigured units.</summary>
        private static CharacterSettings FindSettings(BaseUnitEntity unit)
        {
            var all = PerSaveSettings.Instance?.CharacterSettings;
            if (all == null || unit == null) return null;
            return all.TryGetValue(unit.UniqueId, out var settings) ? settings : null;
        }
    }
}
