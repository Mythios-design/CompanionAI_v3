using HarmonyLib;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Pathfinding;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// A ship's movement search starts on the nearest cell that holds no other unit's blocker
    /// (ConstraintWithRespectToTraversalProvider), and that check counts strike craft, which may share a ship's cell
    /// (Fury Interceptors end their move on their target). With a strike craft on the ship's cell the search starts
    /// on the next cell, and the game's starship AI then finds the ship "off course" every turn and ends it without
    /// moving or firing. When the ship itself stands on the cell it was moved off, the search starts there again.
    /// Applies to every ship. May run on a pathfinding thread, so it only reads blockers and positions.
    /// </summary>
    [HarmonyPatch]
    public static class ShipPathStartPatch
    {
        private static readonly AccessTools.FieldRef<ConstraintWithRespectToTraversalProvider, WarhammerSingleNodeBlocker> Blocker =
            AccessTools.FieldRefAccess<ConstraintWithRespectToTraversalProvider, WarhammerSingleNodeBlocker>("m_Blocker");

        private static readonly System.Action<ShipPath, CustomGridNode> SetStartNode =
            AccessTools.MethodDelegate<System.Action<ShipPath, CustomGridNode>>(AccessTools.PropertySetter(typeof(ShipPath), "startNode"));

        private static readonly object _logLock = new object();
        private static string _lastLogged;

        [HarmonyPatch(typeof(ShipPath), "Prepare")]
        [HarmonyPostfix]
        public static void Prepare_Postfix(ShipPath __instance)
        {
            if (__instance.startNode == null || !(__instance.nnConstraint is ConstraintWithRespectToTraversalProvider constraint)) return;
            var own = AstarPath.active?.GetNearest(__instance.startPoint, NNConstraint.None).node as CustomGridNode;
            if (own == null || own == __instance.startNode) return;

            // Only the ship's own cell: its blocker stands there (the same lookup the blocker uses)
            var owner = Blocker(constraint)?.OwnerUnit;
            if (owner == null || AstarPath.active.GetNearest(owner.Position, NNConstraint.None).node != own) return;

            SetStartNode(__instance, own);
            string name = (owner as BaseUnitEntity)?.CharacterName ?? owner.ToString();
            string key = name + "|" + own.NodeIndex;
            lock (_logLock)
            {
                if (key == _lastLogged) return;
                _lastLogged = key;
            }
            Log.Engine.Info($"[Ship] {name}: another unit shares its cell, movement starts from its own cell");
        }
    }
}
