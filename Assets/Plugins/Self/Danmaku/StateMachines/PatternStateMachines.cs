﻿using System.Collections.Generic;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using Danmaku;
using Core;
using DMath;
using JetBrains.Annotations;
using UnityEngine;
using static Danmaku.Enums;

namespace SM {
/// <summary>
/// `pattern`: Top-level controller for SMs involving danmaku or level control.
/// Does not encompass text control (<see cref="ScriptTSM"/>).
/// </summary>
public class PatternSM : SequentialSM {
    /// <summary>
    /// This is sort of a hack. On `clear phase`, data is not cleared until the end
    /// of the frame to ensure that bullets can cull before data is cleared. Therefore we also wait.
    /// We wait for 2 frames because the data clearing occurs "at the end of the next frame".
    /// We allow this functionality to be disabled during testing because it interferes with timing calculations.
    /// </summary>
    public static bool PHASE_BUFFER = true;
    private readonly PatternProperties props;

    public PatternSM(List<StateMachine> states, PatternProperties props) : base(states) {
        this.props = props;
        phases = states.Select(x => x as PhaseSM).ToArray();
        foreach (var p in phases) p.props.LoadDefaults(props);
    }

    public readonly PhaseSM[] phases;

    private int RemainingLives(int fromIndex) {
        int ct = 0;
        for (int ii = fromIndex; ii < phases.Length; ++ii) {
            if (phases[ii].props.phaseType?.IsSpell() ?? false) ++ct;
        }
        return ct;
    }

    private static void SetUniqueBossUI(SMHandoff smh, BossConfig b) {
        UIManager.SetNameTitle(b.displayName, b.displayTitle);
        UIManager.SetProfile(b.profile);
        UIManager.SetBossColor(b.colors.uiColor, b.colors.uiHPColor);
    }

    private static List<BottomTracker> ConfigureAllBosses(SMHandoff smh, BossConfig main,
        [CanBeNull] BossConfig[] all) {
        all = all ?? new[] {main};
        var bts = new List<BottomTracker>();
        var subbosses = new List<Enemy>();
        foreach (var (i,b) in all.Enumerate()) {
            var target = smh.Exec;
            Enemy e;
            if (i > 0) {
                target = UnityEngine.Object.Instantiate(b.boss).GetComponent<BehaviorEntity>();
                if (target.TryAsEnemy(out e)) e.distorter = null; // i dont really like this but it overlaps weirdly
                target.Initialize(new Velocity(new Vector2(-50f, 0f), 0f), MovementModifiers.Default, SMRunner.Null);
            }
            if (target.TryAsEnemy(out e)) {
                if (b.colors.cardColorR.a > 0 || b.colors.cardColorG.a > 0 || b.colors.cardColorB.a > 0) {
                    e.RequestCardCircle(b.colors.cardColorR, b.colors.cardColorG, b.colors.cardColorB, b.Rotator);
                }
                e.SetSpellCircleColors(b.colors.spellColor1, b.colors.spellColor2, b.colors.spellColor3);
                e.SetDamageable(false);
                if (i > 0) subbosses.Add(e);
            }
            if (b.trackName.Length > 0) bts.Add(UIManager.TrackBEH(target, b.trackName, smh.cT));
        }
        smh.Exec.Enemy.Subbosses = subbosses;
        return bts;
    }

    public override async Task Start(SMHandoff smh) {
        var bts = new List<BottomTracker>();
        if (props.boss != null) {
            GameManagement.campaign.OpenBoss();
            UIManager.SetBossHPLoader(smh.Exec.Enemy);
            SetUniqueBossUI(smh, props.bosses == null ? props.boss : props.bosses[props.bossUI.GetBounded(0, 0)]);
            bts = ConfigureAllBosses(smh, props.boss, props.bosses);
        }
        for (var next = smh.Exec.phaseController.WhatIsNextPhase();
            next > -1 && next < phases.Length;
            next = smh.Exec.phaseController.WhatIsNextPhase(next + 1)) {
            if (phases[next].props.skip) continue;
            if (PHASE_BUFFER) await WaitingUtils.WaitForUnchecked(smh.Exec, smh.cT, ETime.FRAME_TIME * 2f, false);
            smh.ThrowIfCancelled();
            if (props.bgms != null) AudioTrackService.InvokeBGM(props.bgms.GetBounded(next, null));
            if (props.bossUI != null && props.bosses != null) {
                SetUniqueBossUI(smh, props.bosses[props.bossUI.GetBounded(next, 0)]); 
            }
            //don't show lives on setup phase
            if (next > 0 && props.boss != null) UIManager.ShowBossLives(RemainingLives(next));
            await phases[next].Start(smh);
        }
        foreach (var bt in bts) bt.Finish();
        if (props.boss != null) {
            UIManager.ShowBossLives(0);
            GameManagement.campaign.CloseBoss();
            UIManager.CloseBoss();
        }
    }
}

/// <summary>
/// `finish`: Child of PhaseSM. When the executing BEH finishes this phase due to timeout or loss of HP,
/// runs the child SM on a new inode.
/// <br/>Does not run if the executing BEH is destroyed by a cull command, or goes out of range, or the scene is changed.
/// </summary>
public class FinishPSM : StateMachine {

    public FinishPSM(StateMachine state) : base(state) { }

    public void Trigger(BehaviorEntity Exec, GenCtx gcx, ICancellee cT) {
        _ = Exec.GetINode("finish-triggered", null).RunExternalSM(SMRunner.Cull(this.states[0], cT, gcx));
    }

    public override Task Start(SMHandoff smh) {
        throw new NotImplementedException("Do not call Start on FinishSM");
    }
}


/// <summary>
/// `phase`: A high-level SM that controls a "phase", which is a sequence of SMs that may run for variable time
/// under various ending conditions.
/// <br/>Phases are the basic unit for implementing cards or similar concepts.
/// <br/>Phases also generally share some state due to data hoisting, events, etc. If you use the `type` property to declare a card type, or you use the `clear` property, then this state and all bullets will be cleared on phase end.
/// </summary>
public class PhaseSM : SequentialSM {
    //Note that this is only for planned cancellation (eg. phase shift/ synchronization),
    //and is primary used for bosses/multiphase enemies.
    [CanBeNull] private readonly EndPSM endPhase = null;
    /// <summary>
    /// This is a callback invoked for planned cancellation as well as the case where
    /// an enemy is killed via normal sources (player bullets, not culling). Use for revenge fire
    /// </summary>
    [CanBeNull] private readonly FinishPSM finishPhase = null;
    private readonly float _timeout = 0;
    private float Timeout => ChallengeManager.TimeoutOverride(props.Boss) ?? _timeout;
    public readonly PhaseProperties props;

    /// <summary>
    /// </summary>
    /// <param name="states">Substates, run sequentially</param>
    /// <param name="timeout">Timeout in seconds before the phase automatically ends. Set to zero for no timeout</param>
    /// <param name="props">Properties describing miscellaneous features of this phase</param>
    public PhaseSM(List<StateMachine> states, float timeout, PhaseProperties props) : base(states) {
        this._timeout = timeout;
        this.props = props;
        for (int ii = 0; ii < states.Count; ++ii) {
            if (states[ii] is EndPSM) {
                endPhase = states[ii] as EndPSM;
                states.RemoveAt(ii);
                break;
            }
        }
        for (int ii = 0; ii < states.Count; ++ii) {
            if (states[ii] is FinishPSM) {
                finishPhase = states[ii] as FinishPSM;
                states.RemoveAt(ii);
                break;
            }
        }
    }

    private void PrepareGraphics(SMHandoff smh, out Task cutins) {
        cutins = Task.CompletedTask;
        if (props.cardTitle != null || props.phaseType != null) UIManager.SetSpellname(props.cardTitle);
        UIManager.ShowPhaseType(props.phaseType);
        if (!props.hideTimeout && smh.Exec.triggersUITimeout) UIManager.ShowStaticTimeout(Timeout);
        if (props.livesOverride.HasValue) UIManager.ShowBossLives(props.livesOverride.Value);
        if (smh.Exec.isEnemy) {
            var hp = props.hp ?? props.phaseType?.DefaultHP();
            if (hp.HasValue) smh.Exec.Enemy.SetHP(hp.Value, hp.Value);
            var hpbar = props.hpbar ?? props.phaseType?.HPBarLength();
            if (hpbar.HasValue) smh.Exec.Enemy.SetHPBar(hpbar.Value, props.phaseType ?? PhaseType.NONSPELL);
            if (props.phaseType?.RequiresHPGuard() ?? false) smh.Exec.Enemy.SetDamageable(false);
        }
        bool forcedBG = false;
        if (GameManagement.campaign.mode != CampaignMode.CARD_PRACTICE && !SaveData.Settings.TeleportAtPhaseStart) {
            if (props.bossCutin) {
                GameManagement.campaign.ExternalLenience(props.Boss.bossCutinTime);
                SFXService.Request("x-boss-cutin");
                RaikoCamera.Shake(props.Boss.bossCutinTime / 2f, null, 1f, smh.cT, () => { });
                UnityEngine.Object.Instantiate(props.Boss.bossCutin);
                BackgroundOrchestrator.QueueTransition(props.Boss.bossCutinTrIn);
                BackgroundOrchestrator.ConstructTarget(props.Boss.bossCutinBg, true);
                cutins = WaitingUtils.WaitFor(smh, props.Boss.bossCutinTime, false).ContinueWithSync(() => {
                    BackgroundOrchestrator.QueueTransition(props.Boss.bossCutinTrOut);
                    BackgroundOrchestrator.ConstructTarget(props.Background, true);
                }, smh.cT);
                forcedBG = true;
            } else if (props.GetSpellCutin(out var sc)) {
                SFXService.Request("x-spell-cutin");
                RaikoCamera.Shake(1.5f, null, 1f, smh.cT, () => { });
                UnityEngine.Object.Instantiate(sc);
            }
        }
        if (!forcedBG && props.Background != null) {
            if (props.BgTransitionIn != null) BackgroundOrchestrator.QueueTransition(props.BgTransitionIn);
            BackgroundOrchestrator.ConstructTarget(props.Background, true);
        }
    }

    private void PrepareCancellationTrigger(SMHandoff smh, Cancellable toCancel) {
        smh.Exec.PhaseShifter = toCancel;
        var timeout = Timeout;
        if (props.invulnTime != null && props.phaseType != PhaseType.TIMEOUT)
            WaitingUtils.WaitThenCB(smh.Exec, smh.cT, props.invulnTime.Value, false,
                () => smh.Exec.Enemy.SetDamageable(true));
        WaitingUtils.WaitThenCancel(smh.Exec, smh.cT, timeout, true, toCancel);
        if (props.phaseType?.IsSpell() ?? false) smh.Exec.Enemy.RequestSpellCircle(timeout, smh.cT);
        //else smh.exec.Enemy.DestroySpellCircle();
        if (!props.hideTimeout && smh.Exec.triggersUITimeout)
            UIManager.DoTimeout(props.phaseType?.IsCard() ?? false, timeout, smh.cT);
    }

    public override async Task Start(SMHandoff smh) {
        PrepareGraphics(smh, out Task cutins);
        if (props.Lenient) GameManagement.campaign.Lenience = true;
        if (props.rootMove != null) await props.rootMove.Start(smh);
        await cutins;
        smh.ThrowIfCancelled();
        var pcTS = new Cancellable();
        var joint_smh = smh.CreateJointCancellee(pcTS, out var joint);
        PrepareCancellationTrigger(joint_smh, pcTS);
        var start_campaign = GameManagement.campaign;
        if (props.phaseType != null) ChallengeManager.SetupBEHPhase(joint_smh);
        try {
            await base.Start(joint_smh);
            await WaitingUtils.WaitForUnchecked(smh.Exec, joint, 0f,
                true); //Wait for synchronization before returning to parent
            joint_smh.ThrowIfCancelled();
        } catch (OperationCanceledException) {
            if (smh.Exec.PhaseShifter == pcTS) smh.Exec.PhaseShifter = null;
            if (props.Lenient) GameManagement.campaign.Lenience = false;
            //TODO generalize targets in props
            if (props.phaseType != null) Log.Unity($"Cleared {props.phaseType.Value} phase: {props.cardTitle ?? ""}");
            if (props.Cleanup) GameManagement.ClearPhaseAutocull(props.autocullTarget, props.autocullDefault);
            if (smh.Exec.AllowFinishCalls) {
                finishPhase?.Trigger(smh.Exec, smh.GCX, smh.parentCT);
                OnFinish(smh, pcTS, in start_campaign);
            }
            if (smh.Cancelled) throw;
            if (endPhase != null) await endPhase.Start(smh);
        }
    }

    private const float defaultShakeMag = 0.7f;
    private const float defaultShakeTime = 0.6f;
    private static readonly FXY defaultShakeMult = x => M.Sin(M.PI * (x + 0.4f));

    private void OnFinish(SMHandoff smh, ICancellee prepared, in CampaignData start_campaign) {
        if (props.BgTransitionOut != null) BackgroundOrchestrator.QueueTransition(props.BgTransitionOut);
        //The shift-phase token is cancelled by timeout or by HP. 
        var completedBy = prepared.Cancelled ?
            (smh.Exec.isEnemy ?
                (smh.Exec.Enemy.HP <= 0 && (props.hp ?? 0) > 0) ?
                    (PhaseClearMethod?) PhaseClearMethod.HP :
                    null :
                null) ??
            PhaseClearMethod.TIMEOUT :
            PhaseClearMethod.CANCELLED;
        var pc = new PhaseCompletion(props, completedBy, smh.Exec, in start_campaign);
        if (pc.StandardCardFinish) {
            smh.Exec.DropItems(pc.DropItems, 1.4f, 0.6f, 0.9f);
            RaikoCamera.Shake(defaultShakeTime, defaultShakeMult, defaultShakeMag, smh.cT, () => { });
        }
        GameManagement.campaign.PhaseEnd(pc);
    }
}

public class DialoguePhaseSM : PhaseSM {
    public DialoguePhaseSM(string file, PhaseProperties props) : base(new List<StateMachine>() {
        new PhaseSequentialActionSM(new List<StateMachine>() {
            new ReflectableLASM(SMReflection.Dialogue(file)),
            new ReflectableLASM(SMReflection.ShiftPhase())
        }, 0f)
    }, 0, props) {
        
    }
    
}

public class PhaseJSM : PhaseSM {
    public PhaseJSM(List<StateMachine> states, float timeout, int from, PhaseProperties props)
    #if UNITY_EDITOR
        : base(states.Skip(from).ToList(), timeout, props) {
        if (this.states[0] is PhaseActionSM psm) psm.wait = 0f;
    #else
        : base(states, timeout, props) {
    #endif
    }
}

/// <summary>
/// `action`: A list of actions that are run in parallel. Place this under a PhaseSM.
/// </summary>
public class PhaseActionSM : ParallelSM {
    #if UNITY_EDITOR
    public float wait;
    #else
    private readonly float wait;
    #endif

    /// <summary>
    /// </summary>
    /// <param name="states">Actions to run in parallel</param>
    /// <param name="blocking">Whether or not this SM forces the next SM to wait for it</param>
    /// <param name="wait">Artificial delay before this SM starts executing</param>
    public PhaseActionSM(List<StateMachine> states, Blocking blocking, float wait) : base(states, blocking) {
        this.wait = wait;
    }

    public override async Task Start(SMHandoff smh) {
        await WaitingUtils.WaitForUnchecked(smh.Exec, smh.cT, wait, false);
        smh.ThrowIfCancelled();
        await base.Start(smh);
    }
}

/// <summary>
/// `paction`: same as `action`, but always blocking.
/// </summary>
public class PhaseParallelActionSM : PhaseActionSM {
    public PhaseParallelActionSM(List<StateMachine> states, float wait) : base(states, Blocking.BLOCKING, wait) { }
}
/// <summary>
/// `saction`: A list of actions that are run in sequence. Place this under a PhaseSM.
/// <br/>This SM is always blocking.
/// </summary>
public class PhaseSequentialActionSM : SequentialSM {
    private readonly float wait;

    /// <summary>
    /// </summary>
    /// <param name="states">Actions to run in parallel</param>
    /// <param name="wait">Artificial delay before this SM starts executing</param>
    public PhaseSequentialActionSM(List<StateMachine> states, float wait) : base(states) {
        this.wait = wait;
    }

    public override async Task Start(SMHandoff smh) {
        await WaitingUtils.WaitForUnchecked(smh.Exec, smh.cT, wait, false);
        smh.ThrowIfCancelled();
        await base.Start(smh);
    }
}

public abstract class MetaPASM : SequentialSM {
    public MetaPASM(List<StateMachine> states) : base(states) { }
}


/// <summary>
/// `end`: Child of PhaseSM. When a phase ends under normal conditions,
/// run these actions in parallel before moving to the next phase.
/// </summary>
public class EndPSM : ParallelSM {
    //does not inherit from PASM to avoid being eaten by MetaPASM
    public EndPSM(List<StateMachine> states) : base(states, Blocking.BLOCKING) { }
}

/// <summary>
/// The basic unit of control in SMs. These cannot be used directly and must instead be placed under `PhaseActionSM` or the like.
/// </summary>
public abstract class LineActionSM : StateMachine {
    public LineActionSM(params StateMachine[] states) : base(new List<StateMachine>(states)) { }
}

/// <summary>
/// Synchronization events for use in SMs.
/// </summary>
public static class Synchronization {

    /*
    public static Synchronizer Track(string alias, string keypoint) => smh => {
        if (keypoint == "end") {
            AudioTrackService.OnTrackEnd(alias, WaitingUtils.GetAwaiter(out Task t));
            return t;
        } else {
            AudioTrackService.AddTrackPointDelegate(alias, keypoint, WaitingUtils.GetAwaiter(out Task t));
            return t;
        }
    };*/
    /// <summary>
    /// Wait for some number of seconds.
    /// </summary>
    [Fallthrough(1)]
    public static Synchronizer Time(GCXF<float> time) => smh => WaitingUtils.WaitForUnchecked(smh.Exec, smh.cT, time(smh.GCX), false);

    /// <summary>
    /// Wait for the synchronization event, and then wait some number of seconds.
    /// </summary>
    public static Synchronizer Delay(float time, Synchronizer synchr) => async smh => {
        await synchr(smh);
        smh.ThrowIfCancelled();
        await WaitingUtils.WaitForUnchecked(smh.Exec, smh.cT, time, false);
    };
}

}