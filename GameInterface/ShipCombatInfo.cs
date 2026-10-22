using System;
using System.Collections.Generic;
using Kingmaker.AI;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.SpaceCombat.StarshipLogic.Parts;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Mechanics.Actions;
using UnityEngine;
using Warhammer.SpaceCombat.Blueprints;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>Space combat reads shared by the ship AI patches and the ship post planner.</summary>
    public static class ShipCombatInfo
    {
        public static string Guid(AbilityData ability) => ability?.Blueprint?.AssetGuid?.ToString();

        /// <summary>A living enemy ship that isn't strike craft or a torpedo.</summary>
        public static bool IsCapitalShip(MechanicEntity entity)
            => entity is StarshipEntity ship && !ship.Blueprint.IsSoftUnit && !ship.LifeState.IsDead;

        public static List<StarshipEntity> CapitalEnemies(DecisionContext context)
        {
            var result = new List<StarshipEntity>();
            if (context?.Enemies == null) return result;
            foreach (var t in context.Enemies)
                if (IsCapitalShip(t?.Entity)) result.Add((StarshipEntity)t.Entity);
            return result;
        }

        public static float HullFraction(StarshipEntity ship)
        {
            int max = ship.Health.MaxHitPoints;
            return max > 0 ? (float)ship.Health.HitPointsLeft / max : 1f;
        }

        public static float ShieldFraction(StarshipEntity ship)
        {
            var shields = ship.Shields;
            return shields != null && shields.ShieldsMaxSum > 0 ? (float)shields.ShieldsSum / shields.ShieldsMaxSum : 1f;
        }

        /// <summary>Movement left this turn.</summary>
        public static float MovePoints(BaseUnitEntity ship) => ship?.CombatState?.ActionPointsBlue ?? 0f;

        /// <summary>Movement a ship gets at the start of its turn.</summary>
        public static float TurnMovePoints(BaseUnitEntity ship)
        {
            var stat = ship?.CombatState?.WarhammerInitialAPBlue;
            if (stat != null) return stat.ModifiedValue;
            return ship?.Blueprint != null ? ship.Blueprint.WarhammerInitialAPBlue : 0f;
        }

        public static StarshipWeaponType? WeaponType(AbilityData ability) => ability?.StarshipWeapon?.Blueprint?.WeaponType;

        /// <summary>
        /// A launch of strike craft, a wingman or an NPC torpedo salvo: the game's AI casts these ahead of the ship
        /// whatever the target (TaskNodeDoNextAction.IsSummoningUnitAbility). Launch bays sit in the arsenal slot, so
        /// these abilities have no ship weapon.
        /// </summary>
        public static bool IsLaunch(AbilityData ability)
        {
            var bp = ability?.Blueprint;
            if (bp == null) return false;
            if (bp.GetComponent<AbilityCustomStarshipNPCTorpedoLaunch>() != null) return true;
            var actions = bp.GetComponent<AbilityEffectRunAction>()?.Actions?.Actions;
            if (actions == null) return false;
            foreach (var action in actions)
                if (action is WarhammerContextActionSpawnChildStarship) return true;
            return false;
        }

        /// <summary>Longest range of the ship's weapons of that type (any type when null), 0 when it has none.</summary>
        public static int WeaponRange(BaseUnitEntity ship, StarshipWeaponType? type = null)
        {
            int best = 0;
            if (ship?.Abilities == null) return best;
            foreach (var fact in ship.Abilities.RawFacts)
            {
                var data = fact?.Data;
                var weaponType = WeaponType(data);
                if (weaponType == null || (type != null && weaponType != type)) continue;
                best = Math.Max(best, data.RangeCells);
            }
            return best;
        }

        /// <summary>The ship's ability whose blueprint is the first of the given GUIDs it has, or null.</summary>
        public static AbilityData FindAbility(BaseUnitEntity ship, params string[] guids)
        {
            if (ship?.Abilities == null) return null;
            foreach (var fact in ship.Abilities.RawFacts)
            {
                string guid = Guid(fact?.Data);
                if (guid == null) continue;
                foreach (var g in guids)
                    if (g == guid) return fact.Data;
            }
            return null;
        }

        /// <summary>True when the unit has a buff or feature with one of the given blueprint GUIDs.</summary>
        public static bool HasFact(MechanicEntity unit, params string[] guids)
        {
            if (unit?.Facts == null) return false;
            foreach (var fact in unit.Facts.List)
            {
                string guid = fact?.Blueprint?.AssetGuid?.ToString();
                if (guid == null) continue;
                foreach (var g in guids)
                    if (g == guid) return true;
            }
            return false;
        }

        /// <summary>
        /// The shield sector a shot from that point would hit, using the game's rule
        /// (RuleStarshipCalculateHitLocation): the eight grid directions around the ship's heading, one for Fore,
        /// three each side, one for Aft.
        /// </summary>
        public static StarshipSectorShieldsType SectorFacing(StarshipEntity ship, Vector3 from)
        {
            int heading = CustomGraphHelper.GuessDirection(ship.Forward);
            Vector3 toShooter = from - ship.Position;
            if (toShooter.magnitude < 0.01f) return StarshipSectorShieldsType.Fore;
            int dir = CustomGraphHelper.GuessDirection(toShooter.normalized);
            if (dir == heading) return StarshipSectorShieldsType.Fore;

            int left = heading, right = heading;
            for (int i = 0; i < 3; i++)
            {
                left = CustomGraphHelper.LeftNeighbourDirection[left];
                right = CustomGraphHelper.RightNeighbourDirection[right];
                if (dir == left) return StarshipSectorShieldsType.Port;
                if (dir == right) return StarshipSectorShieldsType.Starboard;
            }
            return StarshipSectorShieldsType.Aft;
        }

        /// <summary>Angle in degrees between a ship's bow and the direction to a point (0 dead ahead, 180 astern).</summary>
        public static float AngleFromBow(StarshipEntity ship, Vector3 point)
            => Mathf.Abs(Vector3.SignedAngle(point - ship.Position, ship.Forward, Vector3.up));
    }
}
