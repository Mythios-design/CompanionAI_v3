using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Kingmaker;
using Kingmaker.AI;
using Kingmaker.AI.BehaviourTrees;
using Kingmaker.AI.BehaviourTrees.Nodes;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Pathfinding;
using CompanionAI_v3.Core;
using CompanionAI_v3.Settings;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.GameInterface
{
    /// <summary>
    /// 메인 AI 패치 - 게임 상태 제어
    ///
    /// v3.117.59: SelectAbilityTarget_Prefix + FindBetterPlace_Prefix fallback 제거.
    ///   CustomBehaviourTreePatch (BehaviourTree 전체 교체) 가 v3.5.28부터 모든 controlled
    ///   유닛에 적용되어, 게임 native 의 TaskNodeSelectAbilityTarget/TaskNodeFindBetterPlace 가
    ///   호출되지 않음. fallback 경로는 v3.117.57 로그 분석에서 0회 fire 확인.
    ///
    /// 남은 패치:
    ///    ├─ IsAiTurn_Postfix: 게임에 "AI 턴" 보고
    ///    ├─ IsPlayerTurn_Postfix: 게임에 "플레이어 턴 아님" 보고
    ///    ├─ IsAIEnabled_Postfix: Brain 활성화 상태 제어
    ///    └─ IsUsualMeleeUnit_Postfix: 원거리 유닛 돌진 방지
    /// </summary>
    [HarmonyPatch]
    public static class MainAIPatch
    {

        #region Turn Controller Patches

        [HarmonyPatch(typeof(Kingmaker.Controllers.TurnBased.TurnController), "IsAiTurn", MethodType.Getter)]
        [HarmonyPostfix]
        public static void IsAiTurn_Postfix(ref bool __result)
        {
            if (!Main.Enabled) return;

            var currentUnit = Game.Instance?.TurnController?.CurrentUnit as BaseUnitEntity;
            if (currentUnit == null) return;

            if (TurnOrchestrator.Instance.ShouldControl(currentUnit))
            {
                __result = true;
            }
            // ★ v3.21.6: 함선 AI 위임 — 게임 네이티브 AI가 제어하도록 AI 턴으로 전환
            else if (TurnOrchestrator.IsShipAIDelegated(currentUnit))
            {
                __result = true;
            }
        }

        [HarmonyPatch(typeof(Kingmaker.Controllers.TurnBased.TurnController), "IsPlayerTurn", MethodType.Getter)]
        [HarmonyPostfix]
        public static void IsPlayerTurn_Postfix(ref bool __result)
        {
            if (!Main.Enabled) return;

            var currentUnit = Game.Instance?.TurnController?.CurrentUnit as BaseUnitEntity;
            if (currentUnit == null) return;

            if (TurnOrchestrator.Instance.ShouldControl(currentUnit))
            {
                __result = false;
            }
            // ★ v3.21.6: 함선 AI 위임
            else if (TurnOrchestrator.IsShipAIDelegated(currentUnit))
            {
                __result = false;
            }
        }

        #endregion

        #region Brain Patches

        [HarmonyPatch(typeof(Kingmaker.UnitLogic.PartUnitBrain), "IsAIEnabled", MethodType.Getter)]
        [HarmonyPostfix]
        public static void IsAIEnabled_Postfix(Kingmaker.UnitLogic.PartUnitBrain __instance, ref bool __result)
        {
            if (!Main.Enabled) return;

            var unit = __instance.Owner as BaseUnitEntity;
            if (unit == null) return;

            if (TurnOrchestrator.Instance.ShouldControl(unit))
            {
                __result = true;
            }
            // ★ v3.21.6: 함선 AI 위임 — 게임 네이티브 Brain이 동작하도록 활성화
            else if (TurnOrchestrator.IsShipAIDelegated(unit))
            {
                __result = true;
            }
        }

        /// <summary>
        /// ★ IsUsualMeleeUnit 패치 - 원거리 선호 캐릭터가 적에게 돌진하지 않도록
        /// (Cassia 워프 가이드 스태프 같은 IsMelee=true 무기 보유 시 문제 방지)
        /// </summary>
        [HarmonyPatch(typeof(Kingmaker.UnitLogic.PartUnitBrain), "IsUsualMeleeUnit", MethodType.Getter)]
        [HarmonyPostfix]
        public static void IsUsualMeleeUnit_Postfix(Kingmaker.UnitLogic.PartUnitBrain __instance, ref bool __result)
        {
            if (!Main.Enabled) return;
            if (!__result) return;  // 이미 false면 패스

            var unit = __instance.Owner as BaseUnitEntity;
            if (unit == null) return;

            if (!TurnOrchestrator.Instance.ShouldControl(unit)) return;

            var settings = Main.Settings?.GetOrCreateSettings(unit.UniqueId, unit.CharacterName);
            if (settings == null) return;

            // 원거리 선호면 근접 유닛 취급 안 함 → 적에게 돌진하지 않음
            if (settings.RangePreference == RangePreference.PreferRanged)
            {
                __result = false;
                Log.Engine.Debug($"[MainAIPatch] {unit.CharacterName}: IsUsualMeleeUnit = false (PreferRanged)");
            }
        }

        #endregion
    }

    /// <summary>
    /// ★ v3.5.96: 세이브/로드 시 PerSaveSettings 처리
    /// GameId 기반 파일 저장 방식 - LoadRoutine Postfix에서 캐시 클리어 및 로드
    /// </summary>
    [HarmonyPatch]
    public static class SaveLoadPatch
    {
        /// <summary>
        /// 수동 패치 적용 (Main.Load에서 호출 - 이제 빈 메서드)
        /// 하위 호환성을 위해 유지
        /// </summary>
        public static void ApplyManualPatches(Harmony harmony)
        {
            // ★ v3.5.96: InGameSettings 패치 더 이상 필요 없음
            // GameId 기반 파일 저장 방식으로 변경됨
        }

        /// <summary>
        /// 게임 저장 시 PerSaveSettings를 파일로 저장
        /// </summary>
        [HarmonyPatch(typeof(Kingmaker.EntitySystem.Persistence.SaveManager), nameof(Kingmaker.EntitySystem.Persistence.SaveManager.SaveRoutine))]
        [HarmonyPrefix]
        public static void SaveRoutine_Prefix()
        {
            try
            {
                Settings.PerSaveSettings.Save();
                Log.Engine.Debug("[SaveLoadPatch] Settings saved before game save");
            }
            catch (Exception ex)
            {
                Log.Engine.Error($"[SaveLoadPatch] SaveRoutine error: {ex.Message}");
            }
        }

        /// <summary>
        /// 게임 로드 완료 후 캐시 클리어 (새 GameId로 자동 로드됨)
        /// </summary>
        [HarmonyPatch(typeof(Kingmaker.EntitySystem.Persistence.SaveManager), nameof(Kingmaker.EntitySystem.Persistence.SaveManager.LoadRoutine))]
        [HarmonyPostfix]
        public static void LoadRoutine_Postfix()
        {
            try
            {
                // 캐시 클리어 → 다음 Instance 접근 시 새 GameId로 자동 로드
                Settings.PerSaveSettings.ClearCache();

                // ★ v3.72.0: Clear stale event/dialogue buffers to prevent context pollution
                MachineSpirit.GameEventCollector.ClearDialogueBuffer();
                MachineSpirit.GameEventCollector.ClearEvents();

                // Heroic Act usage belongs to the session before the load
                Core.HeroicActPolicy.Instance.ResetForCombat();

                Log.Engine.Info("[SaveLoadPatch] Cache cleared after load - settings will reload on next access");
            }
            catch (Exception ex)
            {
                Log.Engine.Error($"[SaveLoadPatch] LoadRoutine error: {ex.Message}");
            }
        }
    }
}
