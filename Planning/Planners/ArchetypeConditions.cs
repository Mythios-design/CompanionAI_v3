using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.GameInterface;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Advanced-archetype abilities that only pay off in a given situation. AbilityPolicy holds them out of every
    /// pick while WhyHold returns a reason, and logs it. Matched by GUID.
    /// </summary>
    public static class ArchetypeConditions
    {
        // Bounty Hunter
        private const string RAID = "1394e5c9a6a347db841d961a87f375f4";  // JoinThePack_Hunter_Ability

        // Executioner
        private const string PAIN_RESONANCE = "fd305e7458de42b5a28fa8b80437cf03";     // Executioner_Aggravate_Ability
        private const string GIFT_OF_TORMENT = "401f97698d794e058adf5bee28aa2f1e";    // Executioner_PainAnticipation_Ability
        private const string WHERE_IT_HURTS = "cfdb72a6653c49ffa792fa99abfed128";     // Executioner_Keystone_Active_Ability
        private const string TERRIFYING_STRIKE = "d0c7cc80880242b1ba1ded8192c53ea4";
        private const string MERCILESS_VERDICT = "914b9182e9634dd1a3139c16485d091f";  // Executioner_DebiliatingShot_Ability
        // Grand Strategist
        private const string MOBILITY_ZONE = "2249e4658f5d4a4ab8180a1c7f15ed79";      // Strategist_Talent08_Ability

        private const float PainResonanceRange = 4f;
        private const int PainResonanceMinEnemies = 2;
        private const int GiftOfTormentMinCreatures = 3;

        /// <summary>Why the ability isn't worth casting now, or null when it is (or has no condition here).</summary>
        public static string WhyHold(AbilityData ability, Situation situation)
        {
            switch (ability?.Blueprint?.AssetGuid?.ToString())
            {
                case RAID:
                    return PreyInAlliesReach(situation) ? null : "no prey within an ally's reach";
                case TacticianPlanner.INSPIRE:
                case TacticianPlanner.LINCHPIN:
                case TacticianPlanner.STRONGPOINT:
                case TacticianPlanner.PRESS_THE_ADVANTAGE:
                {
                    string chosen = TacticianPlanner.ChosenSpender(situation, out string why);
                    if (chosen == ability.Blueprint.AssetGuid.ToString()) return null;
                    return why ?? "tactical advantage goes to another order this turn";
                }
                case TacticianPlanner.FERVOUR:
                    return TacticianPlanner.WhyHoldFervour(situation);
                case PAIN_RESONANCE:
                {
                    int n = CountDoT(situation.Enemies, situation.Unit, PainResonanceRange);
                    return n >= PainResonanceMinEnemies ? null : $"{n} enemies with damage over time within {PainResonanceRange:F0} tiles, needs {PainResonanceMinEnemies}";
                }
                case GIFT_OF_TORMENT:
                {
                    var all = new List<BaseUnitEntity>(situation.Enemies ?? new List<BaseUnitEntity>());
                    all.AddRange(situation.Allies ?? new List<BaseUnitEntity>());
                    int n = CountDoT(all, null, 0f);
                    return n >= GiftOfTormentMinCreatures ? null : $"{n} creatures with damage over time, needs {GiftOfTormentMinCreatures}";
                }
                case WHERE_IT_HURTS:
                    return CountDoT(situation.HittableEnemies, null, 0f) > 0 ? null : "no enemy in reach has damage over time";
                case TERRIFYING_STRIKE:
                    return IsMeleeAttack(situation.PrimaryAttack) && situation.HasMeleeHittableEnemies
                        ? null : "the next attack isn't a melee attack";
                case MERCILESS_VERDICT:
                    return IsRangedOrPsychic(situation.PrimaryAttack) && situation.HittableEnemies?.Count > 0
                        ? null : "the next attack isn't ranged or psychic";
                case MOBILITY_ZONE:
                    // A 1-cell step without attacks of opportunity, given to allies in a stratagem's area. Its use is
                    // leaving melee, which the AI has no rule for; unregistered it would be auto-classified as a move
                    return "a 1-cell disengage step, no AI rule uses it";
                default:
                    return null;
            }
        }

        /// <summary>Raid only helps allies' attacks on prey (Hunt Down the Prey).</summary>
        private static bool PreyInAlliesReach(Situation situation)
        {
            var allies = OtherAllies(situation);
            foreach (var enemy in situation.Enemies ?? new List<BaseUnitEntity>())
            {
                if (enemy == null || !enemy.IsConscious || enemy.LifeState.IsDead || !CombatAPI.IsMarkedAsPrey(enemy)) continue;
                foreach (var ally in allies)
                    if (CombatAPI.GetEnemyTurnThreatScore(ally, enemy.Position) > 0f) return true;
            }
            return false;
        }

        /// <summary>Living creatures with bleeding, burning or toxin; within range of the unit when one is given.</summary>
        private static int CountDoT(List<BaseUnitEntity> units, BaseUnitEntity from, float range)
        {
            int n = 0;
            foreach (var u in units ?? new List<BaseUnitEntity>())
            {
                if (u == null || u.LifeState.IsDead || !Data.SpecialAbilityHandler.HasDoT(u)) continue;
                if (from != null && CombatAPI.GetDistanceInTiles(from, u) > range) continue;
                n++;
            }
            return n;
        }

        private static bool IsMeleeAttack(AbilityData a) => a?.Weapon != null && a.Weapon.Blueprint.IsMelee;

        private static bool IsRangedOrPsychic(AbilityData a)
            => a != null && ((a.Weapon != null && !a.Weapon.Blueprint.IsMelee) || Data.AbilityDatabase.IsPsychic(a));

        /// <summary>Conscious allies other than the unit itself, familiars excluded.</summary>
        internal static List<BaseUnitEntity> OtherAllies(Situation situation)
        {
            var result = new List<BaseUnitEntity>();
            foreach (var a in situation.Allies ?? new List<BaseUnitEntity>())
                if (a != null && a != situation.Unit && !a.IsPet && a.IsConscious && !a.LifeState.IsDead) result.Add(a);
            return result;
        }
    }
}
