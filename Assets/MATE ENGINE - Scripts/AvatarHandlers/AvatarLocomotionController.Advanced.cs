using System;
using UnityEngine;

public sealed partial class AvatarLocomotionController
{
    void UpdateAdvancedCursorFollow(float now)
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (!TryGetBlockingInfo(out BlockingInfo blocking))
        {
            StopAdvancedGroundAnimation(MaoIntegration.MaoGroundLocomotionState.Idle);
            return;
        }

        float avatarCenterX = (blocking.effectiveMinGlobalX + blocking.effectiveMaxGlobalX) * 0.5f;
        if (now >= _nextCursorRetargetTime || !_cursorTargetReady)
        {
            _nextCursorRetargetTime = now + Mathf.Max(0.03f, CursorRetargetInterval);
            if (_useExplicitTarget)
            {
                _rawCursorTargetX = _explicitTargetX;
            }
            else
            {
                if (!GetCursorPos(out POINT cursor)) return;
                if (TryStartCursorFlight(cursor, avatarCenterX, now)) return;
                MaoIntegration.MaoCursorFollowDecision cursorDecision =
                    MaoIntegration.MaoCursorFollowLogic.Decide(
                        avatarCenterX,
                        cursor.X,
                        CursorSideOffsetPixels,
                        0f,
                        CursorMaximumSegmentPixels);
                _rawCursorTargetX = cursorDecision.TargetCenterX;
            }

            if (!_cursorTargetReady)
            {
                _filteredCursorTargetX = _rawCursorTargetX;
                _cursorTargetReady = true;
            }
        }

        _filteredCursorTargetX = MaoIntegration.MaoAdvancedLocomotionLogic.FilterTarget(
            _filteredCursorTargetX,
            _rawCursorTargetX,
            CursorTargetFilterSharpness,
            Time.unscaledDeltaTime);
        float delta = _filteredCursorTargetX - avatarCenterX;
        float distance = Mathf.Abs(delta);
        _lastTargetDistance = distance;

        MaoIntegration.MaoGroundLocomotionState distanceReference =
            CurrentCursorGait == MaoIntegration.MaoLocomotionGait.Run
                ? MaoIntegration.MaoGroundLocomotionState.Running
                : CurrentCursorGait == MaoIntegration.MaoLocomotionGait.Walk
                    ? MaoIntegration.MaoGroundLocomotionState.Walking
                    : MaoIntegration.MaoGroundLocomotionState.Idle;
        float runEnter = PreferRunning ? Mathf.Max(StopExitDistancePixels, RunExitDistancePixels) : RunEnterDistancePixels;
        MaoIntegration.MaoGroundLocomotionState gait =
            MaoIntegration.MaoAdvancedLocomotionLogic.SelectDistanceState(
                distanceReference,
                distance,
                StopEnterDistancePixels,
                StopExitDistancePixels,
                RunExitDistancePixels,
                runEnter);

        if (now < _cursorFollowReadyAt && _advancedSpeed <= 0.01f)
            gait = MaoIntegration.MaoGroundLocomotionState.Idle;

        int desiredDirection = delta < 0f ? -1 : delta > 0f ? 1 : 0;
        if (gait == MaoIntegration.MaoGroundLocomotionState.Idle || desiredDirection == 0)
        {
            StepAdvancedStop(distance, blocking);
            return;
        }

        _visualFacing.FaceDirection(desiredDirection);
        float signedFacingDelta = Mathf.DeltaAngle(_visualFacing.CurrentYaw, _visualFacing.DesiredYaw);
        bool requiresHardTurn = MaoIntegration.MaoAdvancedLocomotionLogic.RequiresTurnStop(
            signedFacingDelta,
            HardTurnAngleDegrees);
        if (requiresHardTurn && _advancedSpeed > 1f && _advancedMoveDirection != 0)
        {
            _advancedSpeed = MaoIntegration.MaoAdvancedLocomotionLogic.StepSpeed(
                _advancedSpeed,
                0f,
                MovementAccelerationPixelsPerSecondSquared,
                MovementDecelerationPixelsPerSecondSquared,
                Time.unscaledDeltaTime);
            CurrentGroundState = MaoIntegration.MaoGroundLocomotionState.Stopping;
            PlayAdvancedGroundState(CurrentGroundState, _advancedMoveDirection);
            StepAdvancedWindow(_advancedMoveDirection, _advancedSpeed, distance, blocking);
            return;
        }

        MaoIntegration.MaoGroundLocomotionState directionalState =
            MaoIntegration.MaoAdvancedLocomotionLogic.SelectDirectionalState(
                signedFacingDelta,
                gait,
                StrafeStartAngleDegrees,
                HardTurnAngleDegrees);
        if (directionalState == MaoIntegration.MaoGroundLocomotionState.TurningLeft ||
            directionalState == MaoIntegration.MaoGroundLocomotionState.TurningRight)
        {
            _turningForMovement = true;
            _pendingDirection = desiredDirection;
            _advancedSpeed = 0f;
            CurrentGroundState = directionalState;
            CurrentCursorGait = MaoIntegration.MaoLocomotionGait.Idle;
            PlayAdvancedGroundState(CurrentGroundState, desiredDirection);
            return;
        }

        _turningForMovement = false;
        _pendingDirection = 0;
        _advancedMoveDirection = desiredDirection;
        _dir = desiredDirection;
        _walking = true;
        CurrentCursorGait = gait == MaoIntegration.MaoGroundLocomotionState.Running
            ? MaoIntegration.MaoLocomotionGait.Run
            : MaoIntegration.MaoLocomotionGait.Walk;
        float targetSpeed = CurrentCursorGait == MaoIntegration.MaoLocomotionGait.Run
            ? AdvancedRunSpeedPixelsPerSecond
            : AdvancedWalkSpeedPixelsPerSecond;
        _advancedSpeed = MaoIntegration.MaoAdvancedLocomotionLogic.StepSpeed(
            _advancedSpeed,
            targetSpeed,
            MovementAccelerationPixelsPerSecondSquared,
            MovementDecelerationPixelsPerSecondSquared,
            Time.unscaledDeltaTime);

        if (CurrentGroundState == MaoIntegration.MaoGroundLocomotionState.Jumping && !GroundJumpFinished(now))
        {
            PlayAdvancedGroundState(MaoIntegration.MaoGroundLocomotionState.Jumping, desiredDirection);
        }
        else
        {
            CurrentGroundState = directionalState;
            if (ShouldStartGroundJump(now, distance)) StartGroundJump(now);
            else PlayAdvancedGroundState(CurrentGroundState, desiredDirection);
        }

        StepAdvancedWindow(desiredDirection, _advancedSpeed, distance, blocking);
        if (!_cursorFollowStartedLogged)
        {
            _cursorFollowStartedLogged = true;
            UnityEngine.Debug.Log("[Mao Movement] event=advanced_cursor_follow_started");
        }
#endif
    }

    void StepAdvancedStop(float distance, BlockingInfo blocking)
    {
        _advancedSpeed = MaoIntegration.MaoAdvancedLocomotionLogic.StepSpeed(
            _advancedSpeed,
            0f,
            MovementAccelerationPixelsPerSecondSquared,
            MovementDecelerationPixelsPerSecondSquared,
            Time.unscaledDeltaTime);
        if (_advancedSpeed > 1f && _advancedMoveDirection != 0)
        {
            CurrentGroundState = MaoIntegration.MaoGroundLocomotionState.Stopping;
            PlayAdvancedGroundState(CurrentGroundState, _advancedMoveDirection);
            StepAdvancedWindow(_advancedMoveDirection, _advancedSpeed, distance, blocking);
            return;
        }

        _walking = false;
        _dir = 0;
        _advancedMoveDirection = 0;
        _lastVelocityPixelsPerSecond = 0f;
        CurrentCursorGait = MaoIntegration.MaoLocomotionGait.Idle;
        CurrentGroundState = MaoIntegration.MaoGroundLocomotionState.Idle;
        _visualFacing.FaceDirection(0);
        PlayAdvancedGroundState(CurrentGroundState, 0);
    }

    void StepAdvancedWindow(
        int direction,
        float speedPixelsPerSecond,
        float distance,
        BlockingInfo blocking)
    {
        if (direction == 0 || speedPixelsPerSecond <= 0.01f || !GetWindowRect(_hwnd, out RECT windowRect))
            return;
        int windowWidth = windowRect.Right - windowRect.Left;
        int minimumX;
        int maximumX;
        if (UseAvatarBoundsBlocking)
        {
            minimumX = blocking.minWindowX;
            maximumX = blocking.maxWindowX;
        }
        else
        {
            if (!TryGetMonitorBounds(_hwnd, out int monitorLeft, out int monitorRight)) return;
            minimumX = monitorLeft;
            maximumX = Mathf.Max(minimumX, monitorRight - windowWidth);
        }

        float move = Mathf.Min(
            Mathf.Max(0f, speedPixelsPerSecond) * Time.unscaledDeltaTime,
            Mathf.Max(0f, distance));
        int targetX = windowRect.Left + Mathf.RoundToInt(move * direction);
        int clampedX = Mathf.Clamp(targetX, minimumX, maximumX);
        int signedMoved = clampedX - windowRect.Left;
        if (signedMoved == 0)
        {
            _advancedSpeed = 0f;
            return;
        }
        if (!SetWindowPos(_hwnd, IntPtr.Zero, clampedX, windowRect.Top, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
            return;

        _lastVelocityPixelsPerSecond = Time.unscaledDeltaTime > 0.0001f
            ? signedMoved / Time.unscaledDeltaTime
            : 0f;
        bool forwardGait = CurrentGroundState == MaoIntegration.MaoGroundLocomotionState.Walking ||
                           CurrentGroundState == MaoIntegration.MaoGroundLocomotionState.Running ||
                           CurrentGroundState == MaoIntegration.MaoGroundLocomotionState.Jumping;
        if (forwardGait) ValidateFacingInvariant();
    }

    void PlayAdvancedGroundState(MaoIntegration.MaoGroundLocomotionState state, int direction)
    {
        if (!AdvancedGroundMotionReady) return;
        float normalizedSpeed = AdvancedRunSpeedPixelsPerSecond > 0.01f
            ? Mathf.Clamp01(_advancedSpeed / AdvancedRunSpeedPixelsPerSecond)
            : 0f;
        SetFloatIfPresent("MaoMoveSpeed", normalizedSpeed);
        SetFloatIfPresent("MaoMoveX", direction);
        SetFloatIfPresent("MaoTurn",
            state == MaoIntegration.MaoGroundLocomotionState.TurningLeft ? -1f :
            state == MaoIntegration.MaoGroundLocomotionState.TurningRight ? 1f : 0f);
        SetFloatIfPresent("MaoStrafe",
            state == MaoIntegration.MaoGroundLocomotionState.StrafeWalkLeft ||
            state == MaoIntegration.MaoGroundLocomotionState.StrafeLeft ? -1f :
            state == MaoIntegration.MaoGroundLocomotionState.StrafeWalkRight ||
            state == MaoIntegration.MaoGroundLocomotionState.StrafeRight ? 1f : 0f);
        SetBoolIfPresent("MaoGrounded", state != MaoIntegration.MaoGroundLocomotionState.Jumping);
        if (_lastPlayedGroundState == state) return;

        string animatorState = GroundAnimatorStateName(state);
        if (string.Equals(_lastPlayedGroundAnimatorState, animatorState, StringComparison.Ordinal))
        {
            _lastPlayedGroundState = state;
            return;
        }
        Animator.CrossFadeInFixedTime(
            GroundLocomotionLayerName + "." + animatorState,
            state == MaoIntegration.MaoGroundLocomotionState.Jumping ? 0.08f : 0.14f,
            _groundLocomotionLayerIndex,
            0f);
        _lastPlayedGroundState = state;
        _lastPlayedGroundAnimatorState = animatorState;
    }

    static string GroundAnimatorStateName(MaoIntegration.MaoGroundLocomotionState state)
    {
        switch (state)
        {
            case MaoIntegration.MaoGroundLocomotionState.TurningLeft: return GroundTurnLeftState;
            case MaoIntegration.MaoGroundLocomotionState.TurningRight: return GroundTurnRightState;
            case MaoIntegration.MaoGroundLocomotionState.StrafeWalkLeft: return GroundStrafeWalkLeftState;
            case MaoIntegration.MaoGroundLocomotionState.StrafeWalkRight: return GroundStrafeWalkRightState;
            case MaoIntegration.MaoGroundLocomotionState.StrafeLeft: return GroundStrafeLeftState;
            case MaoIntegration.MaoGroundLocomotionState.StrafeRight: return GroundStrafeRightState;
            case MaoIntegration.MaoGroundLocomotionState.Jumping: return GroundJumpState;
            default: return GroundBlendState;
        }
    }

    bool ShouldStartGroundJump(float now, float distance)
    {
        return EnableOccasionalGroundJump &&
               distance >= GroundJumpMinimumDistancePixels &&
               CurrentCursorGait == MaoIntegration.MaoLocomotionGait.Run &&
               MaoIntegration.MaoAdvancedLocomotionLogic.CanStartGroundJump(
                   now,
                   _groundJumpStartedAt,
                   Mathf.Max(0f, _nextGroundJumpAt - _groundJumpStartedAt),
                   CurrentCursorFlightPhase == MaoCursorFlightPhase.Grounded,
                   _advancedSpeed > AdvancedWalkSpeedPixelsPerSecond,
                   IsMovementInteractionLocked(out _)) &&
               _visualFacing.IsAligned(FacingAlignmentToleranceDegrees) &&
               now >= _nextGroundJumpAt;
    }

    void StartGroundJump(float now)
    {
        CurrentGroundState = MaoIntegration.MaoGroundLocomotionState.Jumping;
        _groundJumpStartedAt = now;
        if (HasAnimatorParameter("MaoGroundJump", AnimatorControllerParameterType.Trigger))
            Animator.SetTrigger("MaoGroundJump");
        _lastPlayedGroundState = MaoIntegration.MaoGroundLocomotionState.InteractionLocked;
        _lastPlayedGroundAnimatorState = string.Empty;
        PlayAdvancedGroundState(CurrentGroundState, _advancedMoveDirection);
        ScheduleNextGroundJump();
        UnityEngine.Debug.Log("[Mao Movement] event=ground_jump");
    }

    bool GroundJumpFinished(float now)
    {
        if (now - _groundJumpStartedAt > 2.8f) return true;
        AnimatorStateInfo current = Animator.GetCurrentAnimatorStateInfo(_groundLocomotionLayerIndex);
        AnimatorStateInfo next = Animator.GetNextAnimatorStateInfo(_groundLocomotionLayerIndex);
        bool currentIsJump = current.IsName(GroundLocomotionLayerName + "." + GroundJumpState) ||
                             current.IsName(GroundJumpState);
        if (currentIsJump) return current.normalizedTime >= 0.92f;
        bool transitioningIntoJump = Animator.IsInTransition(_groundLocomotionLayerIndex) &&
                                     (next.IsName(GroundLocomotionLayerName + "." + GroundJumpState) ||
                                      next.IsName(GroundJumpState));
        return !transitioningIntoJump;
    }

    void ScheduleNextGroundJump()
    {
        float minimum = Mathf.Max(1f, GroundJumpCooldownMin);
        _nextGroundJumpAt = Time.unscaledTime + UnityEngine.Random.Range(
            minimum,
            Mathf.Max(minimum, GroundJumpCooldownMax));
    }

}
