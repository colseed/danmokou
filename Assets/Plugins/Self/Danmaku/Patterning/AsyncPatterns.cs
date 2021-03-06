﻿
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DMath;
using Core;
using JetBrains.Annotations;
using SM;
using UnityEngine;
using static Core.Events;
using ExFXY = System.Func<TEx<float>, TEx<float>>;
using ExBPY = System.Func<DMath.TExPI, TEx<float>>;
using ExTP = System.Func<DMath.TExPI, TEx<UnityEngine.Vector2>>;
using ExBPRV2 = System.Func<DMath.TExPI, TEx<DMath.V2RV2>>;
using GCP = Danmaku.GenCtxProperty;
using static Danmaku.Enums;

namespace Danmaku {
/// <summary>
/// Functions that describe actions performed over time.
/// The full type is Func{AsyncHandoff, IEnumerator}.
/// </summary>
[SuppressMessage("ReSharper", "UnusedMember.Global")]
public static partial class AsyncPatterns {
    private struct APExecutionTracker {
        private LoopControl<AsyncPattern> looper;
        private AsyncHandoff abh;
        [CanBeNull] private readonly Action parent_done;
        public APExecutionTracker(GenCtxProperties<AsyncPattern> props, AsyncHandoff abh, out bool isClipped) {
            looper = new LoopControl<AsyncPattern>(props, abh.ch, out isClipped);
            this.abh = abh;
            parent_done = abh.done;
            abh.done = null;
            tmp_ret = null;
            elapsedFrames = 0f;
            wasPaused = false;
            tmp_ret = ListCache<GenCtx>.Get();
        }

        public bool CleanupIfCancelled() {
            if (abh.Cancelled) {
                DisposeAll();
                ListCache<GenCtx>.Consign(tmp_ret);
                parent_done?.Invoke();
                return true;
            }
            return false;
        }

        private void DisposeAll() {
            for (int ii = 0; ii < tmp_ret.Count; ++ii) tmp_ret[ii].Dispose();
            tmp_ret.Clear();
        }

        public bool RemainsExceptLast => looper.RemainsExceptLast;
        public bool PrepareIteration() => looper.PrepareIteration();

        public bool PrepareLastIteration() => looper.PrepareLastIteration();
        private readonly List<GenCtx> tmp_ret;
        public void DoSIteration(SyncPattern[] target) {
            if (looper.props.childSelect != null) {
                abh.ch = looper.Handoff.CopyGCX();
                tmp_ret.Add(target[(int)looper.props.childSelect(looper.GCX) % target.Length](new SyncHandoff(ref abh, elapsedFrames)));
            } else {
                for (int ii = 0; ii < target.Length; ++ii) {
                    abh.ch = looper.Handoff.CopyGCX();
                    tmp_ret.Add(target[ii](new SyncHandoff(ref abh, elapsedFrames)));
                }
            }
        }
        public void FinishIteration() => looper.FinishIteration(tmp_ret);
        public void WaitStep() {
            abh.WaitStep();
            looper.WaitStep();
            if (!looper.IsUnpaused) wasPaused = true;
            else {
                if (wasPaused && looper.props.unpause != null) {
                    _ = looper.GCX.exec.RunExternalSM(SMRunner.Run(looper.props.unpause, abh.ch.cT, looper.GCX));
                }
                wasPaused = false;
                ++elapsedFrames;
            }
        }

        private bool wasPaused;

        private float elapsedFrames;
        public bool IsWaiting => !looper.IsUnpaused || elapsedFrames < 0;
        public void StartInitialDelay() => elapsedFrames -= looper.props.delay(looper.GCX);
        public void StartWait() => elapsedFrames -= looper.props.wait(looper.GCX);
        public void AllSDone() {
            ListCache<GenCtx>.Consign(tmp_ret);
            looper.IAmDone();
            parent_done?.Invoke();
        }
    }
    private struct IPExecutionTracker {
        private LoopControl<AsyncPattern> looper;
        /// <summary>
        /// Basic AsyncHandoff to pass around. Note that this is dirty and the ch property is repeatedly modified.
        /// </summary>
        private AsyncHandoff abh;
        [CanBeNull] private readonly Action parent_done;
        private readonly bool waitChild;
        public IPExecutionTracker(GenCtxProperties<AsyncPattern> props, AsyncHandoff abh, out bool isClipped) {
            looper = new LoopControl<AsyncPattern>(props, abh.ch, out isClipped);
            this.abh = abh;
            parent_done = abh.done;
            abh.done = null;
            tmp_ret = null;
            elapsedFrames = 0f;
            waitChild = props.waitChild;
            checkIsChildDone = null;
            wasPaused = false;
            tmp_ret = ListCache<GenCtx>.Get();
        }

        public bool CleanupIfCancelled() {
            if (abh.Cancelled) {
                //Note: since wait-child under gir is stored in tmp_ret,
                //this is only valid if gir children cancel under the same conditions as parent. 
                //Note that we only do this because wait-child does not put Dispose in CB.
                DisposeAll();
                ListCache<GenCtx>.Consign(tmp_ret);
                parent_done?.Invoke();
                return true;
            }
            return false;
        }

        private void DisposeAll() {
            for (int ii = 0; ii < tmp_ret.Count; ++ii) tmp_ret[ii].Dispose();
            tmp_ret.Clear();
        }

        public bool RemainsExceptLast => looper.RemainsExceptLast;
        public bool PrepareIteration() => looper.PrepareIteration();

        public bool PrepareLastIteration() => looper.PrepareLastIteration();
        private readonly List<GenCtx> tmp_ret;
        public void FinishIteration() => looper.FinishIteration(tmp_ret);

        [CanBeNull] private Func<bool> checkIsChildDone;

        public void DoAIteration(AsyncPattern[] target) {
            if (looper.props.childSelect == null) {
                Action cb = null;
                if (waitChild) {
                    cb = WaitingUtils.GetManyCondition(target.Length, out checkIsChildDone);
                    //On the frame that the child finishes, the waitstep will increment elapsedFrames
                    //even though it should not. However, it is difficult to tell the waitstep whether
                    //the child was finished or not finished before that frame. This is the easiest solution.
                    --elapsedFrames; 
                } else checkIsChildDone = null;
                for (int ii = 0; ii < target.Length; ++ii) {
                    DoAIteration(target[ii], cb);
                }
            } else DoAIteration(target[(int) looper.props.childSelect(looper.GCX) % target.Length]);
        }

        private void DoAIteration(AsyncPattern target, [CanBeNull] Action waitChildDone=null) {
            abh.ch = looper.Handoff.CopyGCX();
            if (waitChild) {
                tmp_ret.Add(abh.ch.gcx);
                abh.done = waitChildDone ?? WaitingUtils.GetCondition(out checkIsChildDone);
            } else abh.done = abh.ch.gcx.Dispose;
            //RunPrepend steps the coroutine and places it before the current one,
            //so we can continue running on the same frame that the child finishes (if using waitchild). 
            abh.RunPrependRIEnumerator(target(abh));
        }

        private static Action ReconcileAndCallback([CanBeNull] Action parent_done, List<GenCtx> tmp_ret, LoopControl<AsyncPattern> looper) {
            return () => {
                if (!looper.Handoff.cT.Cancelled) {
                    looper.FinishIteration(tmp_ret);
                }
                ListCache<GenCtx>.Consign(tmp_ret);
                looper.IAmDone();
                parent_done?.Invoke();
            };
        }

        public void ForceADone() => ReconcileAndCallback(parent_done, tmp_ret, looper)();
        public void DoLastAIteration(AsyncPattern[] target) {
            if (looper.props.childSelect == null) {
                //Always track the done command. Even if we are not waiting-child, a IRepeat is only done
                //when its last invokee is done.
                var cb = WaitingUtils.GetManyCallback(target.Length, ReconcileAndCallback(parent_done, tmp_ret, looper));
                for (int ii = 0; ii < target.Length; ++ii) {
                    DoLastAIteration(target[ii], cb);
                }
            } else DoLastAIteration(target[(int) looper.props.childSelect(looper.GCX) % target.Length]);
        }

        private void DoLastAIteration(AsyncPattern target, [CanBeNull] Action reconcileDone = null) {
            abh.ch = looper.Handoff.CopyGCX();
            tmp_ret.Add(abh.ch.gcx);
            abh.done = reconcileDone ?? ReconcileAndCallback(parent_done, tmp_ret, looper);
            abh.RunPrependRIEnumerator(target(abh));
        }
        
        public void WaitStep() {
            abh.WaitStep();
            looper.WaitStep();
            if (!looper.IsUnpaused) wasPaused = true;
            else {
                if (wasPaused && looper.props.unpause != null) {
                    _ = looper.GCX.exec.RunExternalSM(SMRunner.Run(looper.props.unpause, abh.ch.cT, looper.GCX));
                }
                wasPaused = false;
                if (checkIsChildDone?.Invoke() ?? true) ++elapsedFrames;
            }
        }

        private bool wasPaused;

        private float elapsedFrames;
        public bool IsWaiting => !looper.IsUnpaused || !(checkIsChildDone?.Invoke() ?? true) || elapsedFrames < 0;
        public void StartInitialDelay() => elapsedFrames -= looper.props.delay(looper.GCX);
        public void StartWait() => elapsedFrames -= looper.props.wait(looper.GCX);
    }
    /// <summary>
    /// The generic C-level repeater function.
    /// Takes any number of functionality-modifying properties as an array.
    /// </summary>
    /// <param name="props">Array of properties</param>
    /// <param name="target">Child SyncPatterns to run</param>
    /// <returns></returns>
    [Alias("GCR")]
    public static AsyncPattern GCRepeat(GenCtxProperties<AsyncPattern> props, SyncPattern[] target) {
        IEnumerator Inner(AsyncHandoff abh) {
            APExecutionTracker tracker = new APExecutionTracker(props, abh, out bool isClipped);
            if (isClipped) {
                tracker.AllSDone();
                yield break;
            }
            if (tracker.CleanupIfCancelled()) yield break;
            for (tracker.StartInitialDelay(); tracker.IsWaiting; tracker.WaitStep()) {
                yield return null;
                if (tracker.CleanupIfCancelled()) yield break;
            }
            while (tracker.RemainsExceptLast && tracker.PrepareIteration()) { 
                tracker.DoSIteration(target);
                for (tracker.StartWait(); tracker.IsWaiting; tracker.WaitStep()) {
                    yield return null;
                    if (tracker.CleanupIfCancelled()) yield break;
                }
                tracker.FinishIteration();
            }
            if (tracker.PrepareLastIteration()) {
                tracker.DoSIteration(target);
                tracker.FinishIteration();
            }
            tracker.AllSDone();
        }
        return Inner;
    }
    
    /// <summary>
    /// Like GCRepeat, but has specific handling for the WAIT, TIMES, and rpp properties.
    /// </summary>
    /// <param name="wait">Frames to wait between invocations</param>
    /// <param name="times">Number of invocations</param>
    /// <param name="rpp">Amount to increment rv2 between invocations</param>
    /// <param name="props">Other properties</param>
    /// <param name="target">Child SyncPatterns to run</param>
    /// <returns></returns>
    [Alias("GCR2")]
    public static AsyncPattern GCRepeat2(GCXF<float> wait, GCXF<float> times, GCXF<V2RV2> rpp, GenCtxProperty[] props, SyncPattern[] target) =>
        GCRepeat(new GenCtxProperties<AsyncPattern>(props.Append(GenCtxProperty.Async(wait, times, rpp))), target);

    /// <summary>
    /// Like GCRepeat, but has specific handling for the WAIT, TIMES, and rpp properties,
    /// where WAIT and TIMES are mutated by the difficulty reference (wait / difficulty, times * difficulty)
    /// </summary>
    /// <param name="difficulty">Difficulty multiplier</param>
    /// <param name="wait">Frames to wait between invocations</param>
    /// <param name="times">Number of invocations</param>
    /// <param name="rpp">Amount to increment rv2 between invocations</param>
    /// <param name="props">Other properties</param>
    /// <param name="target">Child SyncPatterns to run</param>
    /// <returns></returns>
    [Alias("GCR2d")]
    public static AsyncPattern GCRepeat2d(ExBPY difficulty, ExBPY wait, ExBPY times, GCXF<V2RV2> rpp, GenCtxProperty[] props, SyncPattern[] target) =>
        GCRepeat(new GenCtxProperties<AsyncPattern>(props.Append(GenCtxProperty.AsyncD(difficulty, wait, times, rpp))), target);
    /// <summary>
    /// Like GCRepeat, but has specific handling for the WAIT, TIMES, and rpp properties,
    /// where all three are adjusted for difficulty.
    /// </summary>
    [Alias("GCR2dr")]
    public static AsyncPattern GCRepeat2dr(ExBPY difficulty, ExBPY wait, ExBPY times, ExBPRV2 rpp, GenCtxProperty[] props, SyncPattern[] target) =>
        GCRepeat(new GenCtxProperties<AsyncPattern>(props.Append(GenCtxProperty.AsyncDR(difficulty, wait, times, rpp))), target);
    
    /// <summary>
    /// Like GCRepeat, but has specific handling for the WAIT and TIMES properties with CIRCLE.
    /// </summary>
    /// <param name="wait">Frames to wait between invocations</param>
    /// <param name="times">Number of invocations</param>
    /// <param name="props">Other properties</param>
    /// <param name="target">Child SyncPatterns to run</param>
    /// <returns></returns>
    [Alias("GCR2c")]
    public static AsyncPattern GCRepeat2c(GCXF<float> wait, GCXF<float> times, GenCtxProperty[] props, SyncPattern[] target) =>
        GCRepeat(new GenCtxProperties<AsyncPattern>(props.Append(GenCtxProperty.Wait(wait))
            .Append(GenCtxProperty.TimesCircle(times))), target);
    
    [Alias("GCRf")]
    public static AsyncPattern GCRepeatFRV2(GCXF<float> wait, GCXF<float> times, GCXF<V2RV2> frv2, GenCtxProperty[] props, SyncPattern[] target) =>
        GCRepeat(new GenCtxProperties<AsyncPattern>(props.Append(GenCtxProperty.WT(wait, times)).Append(GenCtxProperty.FRV2(frv2))), target);
    /// <summary>
    /// Like GCRepeat, but has specific handling for the WAIT, FOR, and rpp properties (times is set to infinity).
    /// </summary>
    /// <param name="wait">Frames to wait between invocations</param>
    /// <param name="forTime">Maximum length of time to run these invocations for</param>
    /// <param name="rpp">Amount to increment rv2 between invocations</param>
    /// <param name="props">Other properties</param>
    /// <param name="target">Child SyncPatterns to run</param>
    /// <returns></returns>
    [Alias("GCR3")]
    public static AsyncPattern GCRepeat3(GCXF<float> wait, GCXF<float> forTime, GCXF<V2RV2> rpp, GenCtxProperty[] props, SyncPattern[] target) =>
        GCRepeat(new GenCtxProperties<AsyncPattern>(props.Append(GenCtxProperty.AsyncFor(wait, forTime, rpp))), target);
    public static AsyncPattern _AsGCR(SyncPattern target, params GenCtxProperty[] props) =>
        _AsGCR(new[] {target}, props);
    private static AsyncPattern _AsGCR(SyncPattern target, GenCtxProperty[] props1, params GenCtxProperty[] props) =>
        _AsGCR(new[] {target}, props1, props);
    private static AsyncPattern _AsGCR(SyncPattern[] target, GenCtxProperty[] props1, params GenCtxProperty[] props) =>
        GCRepeat(new GenCtxProperties<AsyncPattern>(props1.Extend(props)), target);
    private static AsyncPattern _AsGCR(SyncPattern[] target, params GenCtxProperty[] props) =>
        GCRepeat(new GenCtxProperties<AsyncPattern>(props), target);
    // WARNING!!!
    // All non-passthrough async functions should have 
    //     if (abh.Cancelled) { abh.done(); yield break; }
    // as their first line.

    /*
     * COROUTINE FUNCTIONS
     */

    /// <summary>
    /// Execute the child SyncPattern once.
    /// </summary>
    /// <param name="target">Child SyncPattern to run unchanged</param>
    /// <returns></returns>
    [Fallthrough(1)]
    public static AsyncPattern COnce(SyncPattern target) => _AsGCR(target, GCP.Times(_ => 1));
    

    /// <summary>
    /// Delay a synchronous invokee by a given number of frames.
    /// </summary>
    /// <param name="delay">Frame delay</param>
    /// <param name="next">Synchronous invokee to delay</param>
    /// <returns></returns>
    public static AsyncPattern CDelay(GCXF<float> delay, SyncPattern next) => _AsGCR(next, GenCtxProperty.Delay(delay));
}

//IEnum nesting funcs here
//Please prefix all functions with "I"
//Use yield return where possible
//Cancellation checks at the beginning of nesting patterns may be unnecessary, but is good hygiene.
public static partial class AsyncPatterns {

    /// <summary>
    /// The generic I-level repeater function.
    /// Takes any number of functionality-modifying properties as an array.
    /// </summary>
    /// <param name="props">Array of properties</param>
    /// <param name="target">Child AsyncPatterns to run</param>
    /// <returns></returns>
    [Alias("GIR")]
    public static AsyncPattern GIRepeat(GenCtxProperties<AsyncPattern> props, AsyncPattern[] target) {
        IEnumerator Inner(AsyncHandoff abh) {
            IPExecutionTracker tracker = new IPExecutionTracker(props, abh, out bool isClipped);
            if (isClipped) {
                tracker.ForceADone();
                yield break;
            }
            if (tracker.CleanupIfCancelled()) yield break;
            for (tracker.StartInitialDelay(); tracker.IsWaiting; tracker.WaitStep()) {
                yield return null;
                if (tracker.CleanupIfCancelled()) yield break;
            }
            while (tracker.RemainsExceptLast && tracker.PrepareIteration()) { 
                tracker.DoAIteration(target);
                for (tracker.StartWait(); tracker.IsWaiting; tracker.WaitStep()) {
                    yield return null;
                    if (tracker.CleanupIfCancelled()) yield break;
                }
                tracker.FinishIteration();
            }
            if (tracker.PrepareLastIteration()) {
                tracker.DoLastAIteration(target);
                //FinishIteration is hoisted into the callback
            } else tracker.ForceADone();
        }
        return Inner;
    }
    
    /// <summary>
    /// Like GIRepeat, but has specific handling for the WAIT, TIMES, and rpp properties.
    /// </summary>
    /// <param name="wait">Frames to wait between invocations</param>
    /// <param name="times">Number of invocations</param>
    /// <param name="rpp">Amount to increment rv2 between invocations</param>
    /// <param name="props">Other properties</param>
    /// <param name="target">Child AsyncPatterns to run</param>
    /// <returns></returns>
    [Alias("GIR2")]
    public static AsyncPattern GIRepeat2(GCXF<float> wait, GCXF<float> times, GCXF<V2RV2> rpp, GenCtxProperty[] props, AsyncPattern[] target) =>
        GIRepeat(new GenCtxProperties<AsyncPattern>(props.Append(GenCtxProperty.Async(wait, times, rpp))), target);
    
    /// <summary>
    /// Like GIRepeat, but has specific handling for the WAIT, TIMES, and rpp properties,
    /// where WAIT and TIMES are mutated by the difficulty reference (wait / difficulty, times * difficulty)
    /// </summary>
    /// <param name="difficulty">Difficulty multiplier</param>
    /// <param name="wait">Frames to wait between invocations</param>
    /// <param name="times">Number of invocations</param>
    /// <param name="rpp">Amount to increment rv2 between invocations</param>
    /// <param name="props">Other properties</param>
    /// <param name="target">Child AsyncPatterns to run</param>
    /// <returns></returns>
    [Alias("GIR2d")]
    public static AsyncPattern GIRepeat2d(ExBPY difficulty, ExBPY wait, ExBPY times, GCXF<V2RV2> rpp, GenCtxProperty[] props, AsyncPattern[] target) =>
        GIRepeat(new GenCtxProperties<AsyncPattern>(props.Append(GenCtxProperty.AsyncD(difficulty, wait, times, rpp))), target);
    
    /// <summary>
    /// Like GIRepeat, but has specific handling for the WAIT, FOR, and rpp properties (times is set to infinity).
    /// </summary>
    /// <param name="wait">Frames to wait between invocations</param>
    /// <param name="forTime">Maximum length of time to run these invocations for</param>
    /// <param name="rpp">Amount to increment rv2 between invocations</param>
    /// <param name="props">Other properties</param>
    /// <param name="target">Child AsyncPatterns to run</param>
    /// <returns></returns>
    [Alias("GIR3")]
    public static AsyncPattern GIRepeat3(GCXF<float> wait, GCXF<float> forTime, GCXF<V2RV2> rpp, GenCtxProperty[] props, AsyncPattern[] target) =>
        GIRepeat(new GenCtxProperties<AsyncPattern>(props.Append(GenCtxProperty.AsyncFor(wait, forTime, rpp))), target);
    private static AsyncPattern _AsGIR(AsyncPattern target, params GenCtxProperty[] props) =>
        _AsGIR(new[] {target}, props);
    private static AsyncPattern _AsGIR(AsyncPattern[] target, params GenCtxProperty[] props) =>
        GIRepeat(new GenCtxProperties<AsyncPattern>(props), target);

    /// <summary>
    /// Delay an asynchronous invokee by a given number of frames.
    /// </summary>
    /// <param name="delay">Frame delay</param>
    /// <param name="next">Asynchronous invokee to delay</param>
    /// <returns></returns>
    public static AsyncPattern IDelay(GCXF<float> delay, AsyncPattern next) => _AsGIR(next, GenCtxProperty.Delay(delay));
    
    public static AsyncPattern IColor(string color, AsyncPattern ap) => _AsGIR(ap, GCP.Color(new[] {color}));

    // The following functions have NOT been ported to _AsGIR. Most of them should be OK as is.

    /// <summary>
    /// Run an asynchronous invokee for a given number of frames before returning it to parent's control.
    /// This will adjust the time value on summoned bullets (TODO simple bullets only currently),
    /// as well as integrate over this time for velocity-based bullets.
    /// </summary>
    /// <param name="frames"></param>
    /// <param name="next"></param>
    /// <returns></returns>
    public static AsyncPattern ISimulate(float frames, AsyncPattern next) {
        IEnumerator Inner(AsyncHandoff abh) {
            if (abh.Cancelled) { abh.done(); yield break; }
            abh.AddSimulatedTime(frames);
            Coroutines cors = new Coroutines();
            cors.Run(next(abh));
            for (; frames > 0; --frames) {
                cors.Step();
            }
            yield return cors.AsIEnum();
        }
        return Inner;
    }

    //Pass-through IFunctions don't need inner ienums, and they don't need cancellation checks (unless they perform
    // nontrivial operations.)

    //But this one does. GetExecForID can't be called immediately, in case the beh is created on the same frame.
    //This holds primarily due to EMLaser, which doesn't construct the node until coroutine execution time.
    /// <summary>
    /// Any firees will be assigned the transform parent with the given BehaviorEntity ID.
    /// <para>Currently, this only works for lasers.</para>
    /// </summary>
    /// <param name="behid">BehaviorEntity ID</param>
    /// <param name="next">Asynchronous invokee to modify</param>
    /// <returns></returns>
    public static AsyncPattern IParent(string behid, AsyncPattern next) {
        IEnumerator Inner(AsyncHandoff abh) {
            if (abh.Cancelled) { abh.done(); yield break; }
            abh.ch.bc.transformParent = (behid == "this") ? abh.ch.gcx.exec : BehaviorEntity.GetExecForID(behid);
            yield return next(abh);
        }
        return Inner;
    }
    
    
    /// <summary>
    /// Play a sound effect and then run the child AsyncPattern.
    /// </summary>
    /// <param name="style">Sound effect style</param>
    /// <param name="next">Asynchronous invokee to modify</param>
    /// <returns></returns>
    public static AsyncPattern ISFX(string style, AsyncPattern next) {
        IEnumerator Inner(AsyncHandoff abh) {
            if (abh.Cancelled) { abh.done(); yield break; }
            SFXService.Request(style);
            yield return next(abh);
        }
        return Inner;
    }
    

    /// <summary>
    /// Saves the current location of the executing parent so all bullets fired will fire from
    /// the saved position.
    /// </summary>
    /// <param name="next">Asynchronous invokee to modify</param>
    /// <returns></returns>
    public static AsyncPattern ICacheLoc(AsyncPattern next) {
        return abh => {
            abh.ch.bc.CacheLoc();
            return next(abh);
        };
    }

    public static AsyncPattern WithP(GCXF<float> newP, AsyncPattern next) => abh => {
        abh.ch.gcx.index = (int) newP(abh.ch.gcx);
        return next(abh);
    };
}

}