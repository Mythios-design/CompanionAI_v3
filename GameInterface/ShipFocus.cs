using System.Collections.Generic;
using Kingmaker.AI;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.SpaceCombat.StarshipLogic.Parts;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// The enemy capital ship the delegated player ship masses its guns on this turn: the one the most of its
    /// weapons can hit from a single reachable cell (firing arcs included), ties to the least hull, then the least
    /// shields. Weapons are worth Bonus more against it (ShipAIPatch.ShipTargetValue), so the path search prefers
    /// routes that bring more guns onto it and the firing step shoots it first; guns that can't reach it still fire
    /// at other ships. Picked before the path search; when it dies the turn is planned again with a new one.
    /// </summary>
    public static class ShipFocus
    {
        public const int Bonus = 3;

        private static string _shipId;
        private static StarshipEntity _focus;

        public static void Reset()
        {
            _shipId = null;
            _focus = null;
        }

        /// <summary>The ship's focus this turn, or null.</summary>
        public static StarshipEntity Current(StarshipEntity ship)
            => ship != null && ship.UniqueId == _shipId ? _focus : null;

        /// <summary>True when the ship's focus was destroyed since it was picked.</summary>
        public static bool FocusDied(StarshipEntity ship)
            => ship != null && ship.UniqueId == _shipId && _focus != null && _focus.LifeState.IsDead;

        /// <summary>
        /// Picks the focus once per turn, and again after it dies. True when the focus changed after being
        /// picked, so the frozen ability values are stale. Call while the ship isn't moving.
        /// </summary>
        public static bool Ensure(StarshipEntity ship, DecisionContext context)
        {
            if (ship == null) return false;
            bool picked = ship.UniqueId == _shipId;
            if (picked && !FocusDied(ship)) return false;
            // Cleared first: if the pick fails, a dead focus must not keep the finish-turn step restarting the tree
            _shipId = ship.UniqueId;
            _focus = null;
            _focus = Pick(ship, context);
            return picked;
        }

        private static StarshipEntity Pick(StarshipEntity ship, DecisionContext context)
        {
            var enemies = ShipCombatInfo.CapitalEnemies(context);
            var weapons = new List<AbilityData>();
            foreach (var fact in ship.Abilities.RawFacts)
                if (fact?.Data?.StarshipWeapon != null && fact.Data.IsAvailable) weapons.Add(fact.Data);
            if (enemies.Count == 0 || weapons.Count == 0) return null;

            var navigation = ship.GetStarshipNavigationOptional();
            if (navigation == null) return null;
            navigation.UpdateReachableTiles_Blocking();
            var tiles = navigation.RawReachableTiles;
            if (tiles == null) return null;

            StarshipEntity best = null;
            int bestGuns = 0;
            foreach (var enemy in enemies)
            {
                int guns = MostGunsFromOneCell(enemy, weapons, tiles);
                if (guns == 0) continue;
                if (best == null || guns > bestGuns || (guns == bestGuns && Weaker(enemy, best)))
                {
                    best = enemy;
                    bestGuns = guns;
                }
            }

            if (best == null)
                Log.Engine.Info($"[Ship] {ship.CharacterName}: no focus ship, no capital ship in reach of a gun this turn");
            else
                Log.Engine.Info($"[Ship] {ship.CharacterName}: focus {best.CharacterName}, up to {bestGuns} of {weapons.Count} guns " +
                    $"from one cell, hull {ShipCombatInfo.HullFraction(best) * 100f:F0}%, shields {ShipCombatInfo.ShieldFraction(best) * 100f:F0}%");
            return best;
        }

        private static int MostGunsFromOneCell(StarshipEntity enemy, List<AbilityData> weapons, HashSet<Kingmaker.Pathfinding.ShipPath.DirectionalPathNode> tiles)
        {
            var target = new TargetWrapper(enemy);
            int most = 0;
            foreach (var tile in tiles)
            {
                if (tile == null || !tile.canStand) continue;
                int guns = 0;
                foreach (var weapon in weapons)
                    if (weapon.CanTargetFromNode(tile.node, null, target, out _, out _)
                        && weapon.IsTargetInsideRestrictedFiringArc(target, tile.node, tile.direction))
                        guns++;
                if (guns > most)
                {
                    most = guns;
                    if (most == weapons.Count) break;
                }
            }
            return most;
        }

        private static bool Weaker(StarshipEntity a, StarshipEntity b)
        {
            float ha = ShipCombatInfo.HullFraction(a), hb = ShipCombatInfo.HullFraction(b);
            if (ha != hb) return ha < hb;
            return ShipCombatInfo.ShieldFraction(a) < ShipCombatInfo.ShieldFraction(b);
        }
    }
}
