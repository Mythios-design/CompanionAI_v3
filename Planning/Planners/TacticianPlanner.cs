using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Master Tactician: tactical advantage stacks (TacticianTacticalAdvantageBuff rank). Inspire, Linchpin,
    /// Strongpoint and Press the Advantage each scale with the stacks and cost half of them, so at most one is used per
    /// turn, once the stacks are worth spending (ArchetypeConditions holds the rest):
    ///  1. Inspire at 10+ (+5% damage per 10 stacks), on the heroic-act carry (+25 momentum if they use a heroic
    ///     act), else the ally with the most damage this turn.
    ///  2. Press the Advantage at 5+ (+4% damage per stack on the next attack), before the Tactician's own attack.
    ///  3. Strongpoint at 4+ (+1 temporary wound per 2 stacks), on the ally most enemies can reach next turn.
    ///  4. Linchpin at 5+ (+1 resolve per 5 stacks), on the carry, else the ally with the most damage.
    /// Fervour (20% of max wounds for stacks, and makes those three self-only this turn) only at half wounds or
    /// more, and not on a turn that spends stacks on an ally.
    /// </summary>
    public static class TacticianPlanner
    {
        private const string TACTICAL_ADVANTAGE = "2606d221cbd84502957939547b6f4d21";  // TacticianTacticalAdvantageBuff
        public const string INSPIRE = "b34dd87c65af46c69c35502e3e1cfe93";
        public const string LINCHPIN = "ca8040427b6d4a3d859991b62609b318";
        public const string STRONGPOINT = "70ab234e53764c85a6f3d19de7bb182d";
        public const string PRESS_THE_ADVANTAGE = "60a2011e90b949f2a38fe4a6cefb5100";
        public const string FERVOUR = "305858b91e6e4d89bff75431fa6030e6";

        private const int InspireStacks = 10, PressStacks = 5, StrongpointStacks = 4, LinchpinStacks = 5;
        private const float FervourMinHP = 50f;

        public static bool IsSpender(AbilityData a)
        {
            string g = Guid(a);
            return g == INSPIRE || g == LINCHPIN || g == STRONGPOINT || g == PRESS_THE_ADVANTAGE;
        }

        public static bool TargetsAlly(string guid) => guid == INSPIRE || guid == LINCHPIN || guid == STRONGPOINT;

        public static int Stacks(BaseUnitEntity unit)
        {
            if (unit?.Buffs == null) return 0;
            foreach (var buff in unit.Buffs.RawFacts)
                if (buff?.Blueprint?.AssetGuid?.ToString() == TACTICAL_ADVANTAGE) return buff.Rank;
            return 0;
        }

        /// <summary>
        /// The one stack-spender to keep this turn (its GUID), or null with the reason. Null once one was used this turn.
        /// </summary>
        public static string ChosenSpender(Situation situation, out string why)
        {
            // Asked once per held ability: worked out once per analysis (the ally ranking logs). The situation object
            // is reused between analyses, so the cache is keyed by frame and unit.
            string key = UnityEngine.Time.frameCount + "|" + situation.Unit?.UniqueId;
            if (key != _cachedKey)
            {
                _cachedChoice = Choose(situation, out _cachedWhy);
                _cachedKey = key;
            }
            why = _cachedWhy;
            return _cachedChoice;
        }

        private static string _cachedKey, _cachedChoice, _cachedWhy;

        private static string Choose(Situation situation, out string why)
        {
            var unit = situation.Unit;
            int stacks = Stacks(unit);
            var turn = TurnOrchestrator.Instance?.GetCurrentTurnState();
            if (turn != null && turn.HasExecutedAbilityMatching(IsSpender))
            {
                why = "tactical advantage already spent this turn";
                return null;
            }
            if (stacks >= InspireStacks && Usable(unit, INSPIRE) && AllyTargets(INSPIRE, situation).Count > 0) { why = null; return INSPIRE; }
            if (stacks >= PressStacks && Usable(unit, PRESS_THE_ADVANTAGE) && situation.HasHittableEnemies) { why = null; return PRESS_THE_ADVANTAGE; }
            if (stacks >= StrongpointStacks && Usable(unit, STRONGPOINT) && AllyTargets(STRONGPOINT, situation).Count > 0) { why = null; return STRONGPOINT; }
            if (stacks >= LinchpinStacks && Usable(unit, LINCHPIN) && AllyTargets(LINCHPIN, situation).Count > 0) { why = null; return LINCHPIN; }
            why = $"{stacks} tactical advantage stacks, saving them";
            return null;
        }

        /// <summary>Why Fervour waits, or null.</summary>
        public static string WhyHoldFervour(Situation situation)
        {
            if (situation.HPPercent < FervourMinHP) return $"costs 20% of max wounds, at {situation.HPPercent:F0}%";
            string chosen = ChosenSpender(situation, out _);
            if (chosen != null && TargetsAlly(chosen)) return "would make this turn's ally order self-only";
            return null;
        }

        /// <summary>Ally targets for Inspire, Linchpin and Strongpoint, best first; null for other abilities.</summary>
        public static List<BaseUnitEntity> AllyTargets(string guid, Situation situation)
        {
            var allies = situation.CombatantAllies?.Where(a => a != null && a != situation.Unit && a.IsConscious && !a.LifeState.IsDead).ToList()
                         ?? new List<BaseUnitEntity>();
            if (guid == STRONGPOINT)
            {
                var threatened = new List<(BaseUnitEntity ally, float threat)>();
                foreach (var ally in allies)
                {
                    float threat = 0f;
                    foreach (var e in situation.Enemies ?? new List<BaseUnitEntity>())
                        if (e != null && e.IsConscious && !e.LifeState.IsDead) threat += CombatAPI.GetEnemyTurnThreatScore(e, ally.Position);
                    if (threat > 0f) threatened.Add((ally, threat));
                }
                return threatened.OrderByDescending(t => t.threat).Select(t => t.ally).ToList();
            }
            if (guid == INSPIRE || guid == LINCHPIN)
            {
                string name = guid == INSPIRE ? "Inspire" : "Linchpin";
                var ranked = ExtraTurnValue.Rank(allies, situation.Unit, situation.Enemies, name, "Tactician");
                string carryId = TeamBlackboard.Instance?.HeroicActPriorityUnitId;
                var carry = allies.FirstOrDefault(a => a.UniqueId == carryId);
                if (carry != null)
                {
                    ranked.Remove(carry);
                    ranked.Insert(0, carry);
                }
                return ranked;
            }
            return null;
        }

        private static bool Usable(BaseUnitEntity unit, string guid)
        {
            if (unit?.Abilities == null) return false;
            foreach (var fact in unit.Abilities.RawFacts)
                if (Guid(fact?.Data) == guid) return fact.Data.IsAvailable;
            return false;
        }

        private static string Guid(AbilityData a) => a?.Blueprint?.AssetGuid?.ToString();
    }
}
