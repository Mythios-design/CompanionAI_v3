using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.Diagnostics;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Plans
{
    public abstract partial class BasePlan
    {
        #region Post-Plan Validation (v3.8.68)

        /// <summary>
        /// ★ v3.8.68: Post-plan 공격 도달 가능 여부 검증 + 복구
        /// 1. 이동 목적지(또는 현재 위치)에서 모든 공격의 도달 가능 여부 검증
        /// 2. 도달 불가 공격 제거 (AP 복구)
        /// 3. 모든 공격 제거 시 공격 전 버프도 제거 (AP 복구)
        /// 4. didPlanAttack 업데이트
        /// </summary>
        /// <returns>제거된 공격 수</returns>
        protected int ValidateAndRemoveUnreachableAttacks(
            List<PlannedAction> actions,
            Situation situation,
            ref bool didPlanAttack,
            ref float remainingAP)
        {
            var firstMoveAction = CollectionHelper.FirstOrDefault(actions, a => a.Type == ActionType.Move);
            UnityEngine.Vector3 validationPosition;
            bool hasMoveForValidation = firstMoveAction?.MoveDestination != null;

            if (hasMoveForValidation)
                validationPosition = firstMoveAction.MoveDestination.Value;
            else
                validationPosition = situation.Unit.Position;

            // ★ v3.9.10: 게임 API (CanTargetFromPosition) 사용 — Analyzer와 동일한 LOS 검증
            // 기존 CanReachTargetFromPosition은 LosCalculations.HasLos() 사용 → 게임 API와 결과 불일치
            var validationNode = validationPosition.GetNearestNodeXZ() as CustomGridNodeBase;
            var invalidAttacks = new List<PlannedAction>();

            if (validationNode == null)
            {
                Log.Planning.Warn($"[{RoleName}] Attack validation skipped: validation node not found");
                return 0;
            }

            foreach (var action in actions)
            {
                if (action.Type != ActionType.Attack && action.Type != ActionType.Debuff) continue;
                if (action.Ability == null) continue;

                var targetEntity = action.Target?.Entity as BaseUnitEntity;
                if (targetEntity == null) continue;  // Point 타겟은 스킵

                string reason;
                if (!CombatAPI.CanTargetFromPosition(action.Ability, validationNode, targetEntity, out reason))
                {
                    invalidAttacks.Add(action);
                    Log.Planning.Warn($"[{RoleName}] Attack validation FAILED: {action.Ability.Name} -> {targetEntity.CharacterName} " +
                        $"({reason}, from {(hasMoveForValidation ? "move destination" : "current position")})");
                }
            }

            if (invalidAttacks.Count == 0) return 0;

            // 도달 불가 공격 제거 + AP 복구
            foreach (var invalid in invalidAttacks)
            {
                actions.Remove(invalid);
                remainingAP += invalid.APCost;
                Log.Planning.Info($"[{RoleName}] ★ Removed unreachable attack: {invalid.Ability?.Name} -> {invalid.Target?.Entity?.ToString() ?? "?"}");
            }

            // didPlanAttack 업데이트
            didPlanAttack = CollectionHelper.Any(actions, a => a.Type == ActionType.Attack);

            // 모든 공격이 제거됐으면 공격 전 버프도 제거 (낭비 방지)
            if (!didPlanAttack)
            {
                // ★ v3.8.78: LINQ → CollectionHelper (0 할당)
                CollectionHelper.FillWhere(actions, _tempActions,
                    a => a.Type == ActionType.Buff && a.Ability != null && IsPreAttackBuff(a.Ability));

                for (int bi = 0; bi < _tempActions.Count; bi++)
                {
                    var buff = _tempActions[bi];
                    actions.Remove(buff);
                    remainingAP += buff.APCost;
                    Log.Planning.Info($"[{RoleName}] ★ Removed orphaned pre-attack buff: {buff.Ability?.Name} (no attacks remaining)");
                }
            }

            return invalidAttacks.Count;
        }

        /// <summary>
        /// ★ v3.8.68: 공격 전 버프 여부 판별
        /// </summary>
        private static bool IsPreAttackBuff(AbilityData ability)
        {
            var timing = AbilityDatabase.GetTiming(ability);
            return timing == AbilityTiming.PreAttackBuff || timing == AbilityTiming.RighteousFury;
        }

        /// <summary>
        /// ★ v3.8.76: 전략 옵션 평가 (공격-이동 조합 선택)
        /// Emergency/Reload Phase 이후, 공격/이동 Phase 전에 호출
        /// </summary>
        protected TacticalEvaluation EvaluateTacticalOptions(Situation situation)
        {
            if (!TacticalOptionEvaluator.ShouldEvaluate(situation))
                return null;

            bool needsRetreat = ShouldRetreat(situation);
            var result = TacticalOptionEvaluator.Evaluate(situation, needsRetreat, RoleName);
            // ★ v3.20.1: TacticalEval 결정 JSON 리포트에 기록
            if (result != null)
                CombatReportCollector.Instance.LogPhase($"TacticalEval: {result}");
            return result;
        }

        /// <summary>
        /// ★ v3.8.76: 전략 평가 결과를 Plan 실행에 적용
        /// MoveToAttack → 이동 액션 생성 + HittableEnemies 재계산
        /// AttackThenRetreat → deferRetreat=true
        /// </summary>
        protected PlannedAction ApplyTacticalStrategy(
            TacticalEvaluation eval,
            Situation situation,
            out bool shouldMoveBeforeAttack,
            out bool shouldDeferRetreat)
        {
            shouldMoveBeforeAttack = false;
            shouldDeferRetreat = false;

            if (eval == null || !eval.WasEvaluated)
                return null;

            switch (eval.ChosenStrategy)
            {
                case TacticalStrategy.AttackFromCurrent:
                    // 이동 불필요 - 현재 위치에서 공격 진행
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] TacticalStrategy: AttackFromCurrent (hittable={eval.ExpectedHittableCount})");
                    break;

                case TacticalStrategy.MoveToAttack:
                    // 공격 전 이동 필요
                    shouldMoveBeforeAttack = true;
                    if (eval.MoveDestination.HasValue)
                    {
                        // HittableEnemies 재계산
                        RecalculateHittableFromDestination(situation, eval.MoveDestination.Value);
                        Log.Planning.Info($"[{RoleName}] TacticalStrategy: MoveToAttack → ({eval.MoveDestination.Value.x:F1},{eval.MoveDestination.Value.z:F1}), hittable={eval.ExpectedHittableCount}");
                        return PlannedAction.Move(eval.MoveDestination.Value,
                            $"Tactical pre-attack move (hittable: {eval.ExpectedHittableCount})");
                    }
                    break;

                case TacticalStrategy.AttackThenRetreat:
                    // 공격 먼저, 후퇴는 나중에
                    shouldDeferRetreat = true;
                    Log.Planning.Info($"[{RoleName}] TacticalStrategy: AttackThenRetreat (attack first, retreat after)");
                    break;

                case TacticalStrategy.MoveOnly:
                    // 공격 불가 - Phase 8에서 이동 처리
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] TacticalStrategy: MoveOnly (no attack possible)");
                    break;
            }

            return null;
        }

        /// <summary>
        /// ★ v3.8.76: 이동 후 위치에서 HittableEnemies 재계산
        ///
        /// 핵심 문제: SituationAnalyzer는 턴 시작 위치에서 HittableEnemies를 판정하지만,
        /// Phase 1.6 후퇴/Phase 8 이동 후에는 새 위치에서 LOS/사거리가 달라짐.
        /// 이동이 계획된 후 공격 Phase 전에 호출하여 정확한 공격 대상 목록 유지.
        ///
        /// 이것이 없으면: 이동 후 도달 불가 공격 계획 → ValidateAndRemoveUnreachableAttacks에서 사후 제거 → AP 낭비
        /// 이것이 있으면: 이동 목적지에서 실제 공격 가능한 적만 대상으로 공격 계획
        /// </summary>
        protected void RecalculateHittableFromDestination(Situation situation, Vector3 destination)
        {
            var destNode = destination.GetNearestNodeXZ() as CustomGridNodeBase;
            if (destNode == null)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] RecalculateHittable: destination node not found");
                return;
            }

            var unit = situation.Unit;
            int oldCount = situation.HittableEnemies.Count;
            // ★ v3.9.10: new List<> → 정적 리스트 재사용 (GC 할당 제거)
            _sharedOldHittable.Clear();
            _sharedOldHittable.AddRange(situation.HittableEnemies);

            // 이동 후 위치에서 각 적의 도달 가능성 재검사
            // AvailableAttacks 중 하나라도 해당 적을 타겟 가능하면 Hittable
            _sharedNewHittable.Clear();

            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead) continue;
                // ★ v3.40.8: 데미지 면역 적 제외 (구조물 등)
                if (CombatAPI.IsTargetImmuneToDamage(enemy, situation.Unit)) continue;

                bool canHit = false;
                foreach (var attack in situation.AvailableAttacks)
                {
                    if (CombatHelpers.ShouldExcludeFromAttack(attack, false)) continue;

                    string reason;
                    if (CombatAPI.CanTargetFromPosition(attack, destNode, enemy, out reason))
                    {
                        // ★ v3.9.24: 대형 유닛 거리 보정 — CanTargetFromNode vs CanUseAbilityOn 불일치 방지
                        // CanTargetFromNode가 대형 유닛에 대해 거리를 잘못 판정할 수 있음
                        // WarhammerGeometryUtils.DistanceToInCells (SizeRect 반영)로 이중 검증
                        if (!CombatAPI.IsPointTargetAbility(attack))
                        {
                            float rangeTiles = CombatAPI.GetAbilityRangeInTiles(attack);
                            float distTiles = CombatAPI.GetDistanceInTiles(destination, enemy);
                            if (distTiles > rangeTiles)
                                continue;
                        }

                        // ★ v3.9.24: DangerousAoE Directional 패턴 거리 검증
                        // CanTargetFromPosition은 무기 RangeCells만 체크 — 패턴 반경 미체크
                        if (AbilityDatabase.IsDangerousAoE(attack))
                        {
                            var patternInfo = CombatAPI.GetPatternInfo(attack);
                            if (patternInfo != null && patternInfo.IsValid && patternInfo.CanBeDirectional)
                            {
                                float distTiles = CombatAPI.GetDistanceInTiles(destination, enemy);
                                if (distTiles > patternInfo.Radius)
                                    continue;  // 이 attack은 패턴 범위 밖 → 다음 attack 시도
                            }
                        }

                        // ★ v3.9.24: 아군 안전 체크 (기존 누락)
                        if (!CombatHelpers.IsAttackSafeForTargetFromPosition(
                            attack, destNode.Vector3Position, situation.Unit, enemy, situation.Allies))
                            continue;

                        canHit = true;
                        break;
                    }
                }

                if (canHit)
                    _sharedNewHittable.Add(enemy);
            }

            // Situation 업데이트
            situation.HittableEnemies.Clear();
            for (int i = 0; i < _sharedNewHittable.Count; i++)
                situation.HittableEnemies.Add(_sharedNewHittable[i]);

            // A Blood Oath target hittable from the destination stays the target
            var oathTarget = Planners.BladedancerTactics.GetOathTarget(situation);
            if (oathTarget != null && _sharedNewHittable.Contains(oathTarget))
                situation.BestTarget = oathTarget;

            // BestTarget이 더 이상 Hittable이 아니면 새 BestTarget 선택
            if (situation.BestTarget != null && !_sharedNewHittable.Contains(situation.BestTarget))
            {
                var oldBest = situation.BestTarget;
                situation.BestTarget = _sharedNewHittable.Count > 0 ? _sharedNewHittable[0] : null;
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] BestTarget changed after move: {oldBest.CharacterName} → {situation.BestTarget?.CharacterName ?? "null"}");
            }

            Log.Planning.Info($"[{RoleName}] ★ RecalculateHittable from ({destination.x:F1},{destination.z:F1}): {oldCount} → {_sharedNewHittable.Count} hittable");

            // 소실된 타겟 로깅 (디버그)
            if (_sharedNewHittable.Count < oldCount)
            {
                int logged = 0;
                foreach (var enemy in _sharedOldHittable)
                {
                    if (!_sharedNewHittable.Contains(enemy))
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] Lost target after move: {enemy.CharacterName}");
                        if (++logged >= 3) break;
                    }
                }
            }
        }

        #endregion
    }
}
