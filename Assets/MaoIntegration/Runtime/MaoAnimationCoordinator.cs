using System.Collections.Generic;
using MaoIntegration;
using UnityEngine;

/// <summary>
/// Per-avatar bridge between the deterministic ownership arbiter and Unity's
/// Animator-driven systems.  Persistent owners represent modes (locomotion,
/// pose, guitar, gravity); short leases represent transient gestures.
/// </summary>
[DefaultExecutionOrder(-200)]
[DisallowMultipleComponent]
public sealed class MaoAnimationCoordinator : MonoBehaviour
{
    private readonly MaoAnimationOwnership ownership = new MaoAnimationOwnership();
    private Animator animator;
    private bool idlePersistent;
    private bool locomotionPersistent;
    private bool cursorFlightPersistent;
    private bool posePersistent;
    private bool seatingPersistent;
    private bool guitarPersistent;
    private bool customDancePersistent;
    private bool bigScreenPersistent;
    private bool sleepPersistent;
    private bool gravityPersistent;
    // Some legacy producers share the same logical owner (for example the
    // taskbar sitter and the window-snap sitter).  Keep their claims separate
    // so one producer cannot release another producer's mode accidentally.
    private readonly Dictionary<MaoAnimationOwner, HashSet<string>> persistentSources =
        new Dictionary<MaoAnimationOwner, HashSet<string>>();
    private MaoAnimationOwner lastLoggedOwner = MaoAnimationOwner.None;

    public MaoAnimationOwner CurrentOwner
    {
        get
        {
            Tick();
            return ownership.CurrentOwner;
        }
    }

    public float CurrentOwnerRemaining => ownership.Remaining(Time.unscaledTime);
    public string RuntimeSnapshot
    {
        get
        {
            Tick();
            string state = animator != null && animator.isActiveAndEnabled && animator.layerCount > 0
                ? animator.GetCurrentAnimatorStateInfo(0).fullPathHash.ToString()
                : "none";
            return "owner=" + ownership.CurrentOwner + ";remaining=" + CurrentOwnerRemaining.ToString("0.00") + ";base_state=" + state;
        }
    }

    /// <summary>
    /// Additive head/body micro-motion must yield to any transient body gesture
    /// or exclusive full-body mode, but remains available during locomotion.
    /// </summary>
    public bool SuppressAdditiveMotion
    {
        get
        {
            MaoAnimationOwner current = CurrentOwner;
            return current == MaoAnimationOwner.Interaction ||
                   current == MaoAnimationOwner.CursorFlight ||
                   current == MaoAnimationOwner.Pose ||
                   current == MaoAnimationOwner.Seating ||
                   current == MaoAnimationOwner.Guitar ||
                   current == MaoAnimationOwner.CustomDance ||
                   current == MaoAnimationOwner.BigScreen ||
                   current == MaoAnimationOwner.Sleep ||
                   current == MaoAnimationOwner.Gravity ||
                   current == MaoAnimationOwner.Idle;
        }
    }

    public void Bind(Animator target)
    {
        animator = target;
    }

    public bool CanAcquire(MaoAnimationOwner requested)
    {
        Tick();
        return ownership.CanAcquire(requested, Time.unscaledTime);
    }

    public bool TryAcquire(MaoAnimationOwner requested, float holdSeconds)
    {
        Tick();
        float now = Time.unscaledTime;
        bool acquired = ownership.TryAcquire(requested, now, holdSeconds);
        if (!acquired && requested != MaoAnimationOwner.None)
        {
            Debug.Log($"[Mao Animation] event=blocked requested={requested} current={ownership.CurrentOwner}");
        }
        ReportOwnerChange();
        return acquired;
    }

    /// <summary>
    /// Registers a long-lived mode only when it can actually own the body
    /// channel.  Producers must call this before mutating Animator parameters;
    /// a lower-priority mode is never left latent behind a pose/gravity mode.
    /// </summary>
    public bool TrySetPersistent(MaoAnimationOwner requested, bool enabled)
    {
        if (requested == MaoAnimationOwner.None) return false;
        if (enabled && !CanAcquire(requested))
        {
            Debug.Log($"[Mao Animation] event=blocked_persistent requested={requested} current={CurrentOwner}");
            return false;
        }
        SetPersistentFlag(requested, enabled);
        if (!enabled && !HasPersistentClaim(requested) && ownership.IsOwnedBy(requested, Time.unscaledTime))
            ownership.Release(requested);
        RefreshPersistent();
        ReportOwnerChange();
        return true;
    }

    /// <summary>
    /// Source-scoped variant used by legacy components that share a logical
    /// owner.  Claims are reference-counted by a stable source key; disabling
    /// one source leaves the owner active while another source still holds it.
    /// </summary>
    public bool TrySetPersistent(MaoAnimationOwner requested, bool enabled, string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return TrySetPersistent(requested, enabled);
        if (requested == MaoAnimationOwner.None) return false;

        string key = source.Trim();
        if (enabled)
        {
            if (!CanAcquire(requested))
            {
                Debug.Log($"[Mao Animation] event=blocked_persistent requested={requested} source={key} current={CurrentOwner}");
                return false;
            }
            if (!persistentSources.TryGetValue(requested, out var claims))
            {
                claims = new HashSet<string>(System.StringComparer.Ordinal);
                persistentSources[requested] = claims;
            }
            claims.Add(key);
        }
        else if (persistentSources.TryGetValue(requested, out var existing))
        {
            existing.Remove(key);
            if (existing.Count == 0) persistentSources.Remove(requested);
        }

        if (!enabled && !HasPersistentClaim(requested) && ownership.IsOwnedBy(requested, Time.unscaledTime))
            ownership.Release(requested);
        RefreshPersistent();
        ReportOwnerChange();
        return true;
    }

    public void SetPersistent(MaoAnimationOwner requested, bool enabled)
    {
        TrySetPersistent(requested, enabled);
    }

    public void SetPersistent(MaoAnimationOwner requested, bool enabled, string source)
    {
        TrySetPersistent(requested, enabled, source);
    }

    public bool Release(MaoAnimationOwner requested, string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return Release(requested);
        bool removed = false;
        if (persistentSources.TryGetValue(requested, out var claims))
        {
            removed = claims.Remove(source.Trim());
            if (claims.Count == 0) persistentSources.Remove(requested);
        }
        if (removed && !HasPersistentClaim(requested) && ownership.IsOwnedBy(requested, Time.unscaledTime))
            ownership.Release(requested);
        RefreshPersistent();
        ReportOwnerChange();
        return removed;
    }

    public bool IsOwnedBy(MaoAnimationOwner requested)
    {
        return ownership.IsOwnedBy(requested, Time.unscaledTime);
    }

    public bool Release(MaoAnimationOwner requested)
    {
        bool released = ownership.Release(requested);
        if (released) RefreshPersistent();
        ReportOwnerChange();
        return released;
    }

    public void ForceRelease(string reason)
    {
        ownership.ForceRelease();
        idlePersistent = locomotionPersistent = cursorFlightPersistent = posePersistent = false;
        seatingPersistent = guitarPersistent = customDancePersistent = bigScreenPersistent = sleepPersistent = false;
        gravityPersistent = false;
        persistentSources.Clear();
        lastLoggedOwner = MaoAnimationOwner.None;
        Debug.Log("[Mao Animation] event=force_release reason=" + (reason ?? "unspecified") + " avatar=" + gameObject.name);
    }

    private void Update()
    {
        Tick();
        ReportOwnerChange();
    }

    private void Tick()
    {
        float now = Time.unscaledTime;
        // Touching CurrentOwner expires a lease deterministically even if the
        // component is queried between Update ticks.
        MaoAnimationOwner current = ownership.CurrentOwner;
        ownership.Remaining(now);
        if (current == MaoAnimationOwner.None || !IsPersistent(current)) RefreshPersistent();
    }

    private void RefreshPersistent()
    {
        MaoAnimationOwner desired = HighestPersistentOwner();
        if (desired == MaoAnimationOwner.None) return;

        MaoAnimationOwner current = ownership.CurrentOwner;
        if (current == desired) return;
        if (current != MaoAnimationOwner.None && MaoAnimationOwnership.PriorityOf(desired) <= MaoAnimationOwnership.PriorityOf(current))
            return;

        ownership.TryAcquire(desired, Time.unscaledTime, float.PositiveInfinity);
    }

    private MaoAnimationOwner HighestPersistentOwner()
    {
        // Include source-scoped legacy claims in the same priority search as
        // canonical boolean modes.  Otherwise a lower-priority legacy mode
        // would fail to resume after a higher-priority owner releases it.
        if (gravityPersistent || HasPersistentClaim(MaoAnimationOwner.Gravity)) return MaoAnimationOwner.Gravity;
        if (sleepPersistent || HasPersistentClaim(MaoAnimationOwner.Sleep)) return MaoAnimationOwner.Sleep;
        if (bigScreenPersistent || HasPersistentClaim(MaoAnimationOwner.BigScreen)) return MaoAnimationOwner.BigScreen;
        if (customDancePersistent || HasPersistentClaim(MaoAnimationOwner.CustomDance)) return MaoAnimationOwner.CustomDance;
        if (guitarPersistent || HasPersistentClaim(MaoAnimationOwner.Guitar)) return MaoAnimationOwner.Guitar;
        if (seatingPersistent || HasPersistentClaim(MaoAnimationOwner.Seating)) return MaoAnimationOwner.Seating;
        if (posePersistent || HasPersistentClaim(MaoAnimationOwner.Pose)) return MaoAnimationOwner.Pose;
        if (cursorFlightPersistent || HasPersistentClaim(MaoAnimationOwner.CursorFlight)) return MaoAnimationOwner.CursorFlight;
        if (locomotionPersistent || HasPersistentClaim(MaoAnimationOwner.Locomotion)) return MaoAnimationOwner.Locomotion;
        if (idlePersistent || HasPersistentClaim(MaoAnimationOwner.Idle)) return MaoAnimationOwner.Idle;
        return MaoAnimationOwner.None;
    }

    private bool IsPersistent(MaoAnimationOwner value)
    {
        switch (value)
        {
            case MaoAnimationOwner.Idle: return idlePersistent || HasPersistentClaim(value);
            case MaoAnimationOwner.Locomotion: return locomotionPersistent || HasPersistentClaim(value);
            case MaoAnimationOwner.CursorFlight: return cursorFlightPersistent || HasPersistentClaim(value);
            case MaoAnimationOwner.Pose: return posePersistent || HasPersistentClaim(value);
            case MaoAnimationOwner.Seating: return seatingPersistent || HasPersistentClaim(value);
            case MaoAnimationOwner.Guitar: return guitarPersistent || HasPersistentClaim(value);
            case MaoAnimationOwner.CustomDance: return customDancePersistent || HasPersistentClaim(value);
            case MaoAnimationOwner.BigScreen: return bigScreenPersistent || HasPersistentClaim(value);
            case MaoAnimationOwner.Sleep: return sleepPersistent || HasPersistentClaim(value);
            case MaoAnimationOwner.Gravity: return gravityPersistent || HasPersistentClaim(value);
            default: return false;
        }
    }

    private bool HasPersistentClaim(MaoAnimationOwner value)
    {
        return persistentSources.TryGetValue(value, out var claims) && claims.Count > 0;
    }

    private void SetPersistentFlag(MaoAnimationOwner value, bool enabled)
    {
        switch (value)
        {
            case MaoAnimationOwner.Idle: idlePersistent = enabled; break;
            case MaoAnimationOwner.Locomotion: locomotionPersistent = enabled; break;
            case MaoAnimationOwner.CursorFlight: cursorFlightPersistent = enabled; break;
            case MaoAnimationOwner.Pose: posePersistent = enabled; break;
            case MaoAnimationOwner.Seating: seatingPersistent = enabled; break;
            case MaoAnimationOwner.Guitar: guitarPersistent = enabled; break;
            case MaoAnimationOwner.CustomDance: customDancePersistent = enabled; break;
            case MaoAnimationOwner.BigScreen: bigScreenPersistent = enabled; break;
            case MaoAnimationOwner.Sleep: sleepPersistent = enabled; break;
            case MaoAnimationOwner.Gravity: gravityPersistent = enabled; break;
        }
    }

    private void ReportOwnerChange()
    {
        MaoAnimationOwner current = ownership.CurrentOwner;
        if (current == lastLoggedOwner) return;
        lastLoggedOwner = current;
        string animatorName = animator != null ? animator.name : gameObject.name;
        Debug.Log($"[Mao Animation] event=owner_changed owner={current} avatar={animatorName}");
    }
}
