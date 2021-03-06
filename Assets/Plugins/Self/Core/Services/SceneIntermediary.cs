﻿using System;
using System.Collections;
using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Provides functions for scene transitions.
/// </summary>
public static class SceneIntermediary {
    public static bool IsFirstScene { get; private set; } = true;

    public readonly struct SceneRequest {
        public readonly SceneConfig scene;
        [CanBeNull] public readonly Action onQueued;
        [CanBeNull] public readonly Action onLoaded;
        [CanBeNull] public readonly Action onFinished;
        public readonly Reason reason;

        public enum Reason {
            RELOAD,
            START_ONE,
            RUN_SEQUENCE,
            ABORT_RETURN,
            FINISH_RETURN
        }
        public SceneRequest(SceneConfig sc, Reason reason, [CanBeNull] Action onQueue = null, [CanBeNull] Action onLoad = null,
            [CanBeNull] Action onFinish = null) {
            scene = sc;
            onQueued = onQueue;
            onLoaded = onLoad;
            onFinished = onFinish;
            this.reason = reason;
        }

        public override string ToString() => $"{scene.sceneName} ({reason})";
    }

    private static SceneRequest? LastSceneRequest = null;
    public static bool IsReloading => LOADING && LastSceneRequest?.reason == SceneRequest.Reason.RELOAD;
    
    private static SceneConfig false_scfg;
    private static CameraTransitionConfig defaultTransition;

    public static void Setup(SceneConfig sc, CameraTransitionConfig dfltTransition) {
        false_scfg = sc;
        defaultTransition = dfltTransition;
    }

    /// <summary>
    /// Use GameManagement.ReloadScene instead. Also preferably deprecate-ish this.
    /// </summary>
    public static bool _ReloadScene(Action onLoaded) {
        false_scfg.sceneName = SceneManager.GetActiveScene().name;
        return LoadScene(new SceneRequest(false_scfg, SceneRequest.Reason.RELOAD, null, onLoaded, null));
    }

    public static bool LoadScene(SceneRequest req) {
        if (!GameStateManager.IsLoading && !LOADING) {
            Log.Unity($"Successfully requested scene load for {req}.");
            req.onQueued?.Invoke();
            IsFirstScene = false;
            LastSceneRequest = req;
            LOADING = true;
            GameStateManager.SetLoading(true);
            CoroutineRegularUpdater.GlobalDuringPause.RunRIEnumerator(WaitForSceneLoad(req, true));
            return true;
        } else Log.Unity($"REJECTED scene load for {req}.");
        return false;
    }

    //Use a bool here since GameStateManager is updated at end of frame.
    //We need to keep track of whether or not this process has been queued
    public static bool LOADING { get; private set; } = false;
    private static IEnumerator WaitForSceneLoad(SceneRequest req, bool transitionOnSame) {
        var currScene = SceneManager.GetActiveScene().name;
        float waitOut = 0f;
        if (transitionOnSame || currScene != req.scene.sceneName) {
            var transition = req.scene.transitionIn == null ? defaultTransition : req.scene.transitionIn;
            CameraTransition.Fade(transition, out float waitIn, out waitOut);
            Log.Unity($"Performing fade transition for {waitIn}s before loading scene.");
            for (; waitIn > ETime.FRAME_YIELD; waitIn -= ETime.FRAME_TIME) yield return null;
        }
        Log.Unity($"Scene loading for {req} started.", level: Log.Level.DEBUG3);
        StaticPreSceneUnloaded();
        var op = SceneManager.LoadSceneAsync(req.scene.sceneName);
        while (!op.isDone) {
            yield return null;
        }
        Log.Unity($"Unity finished loading the new scene. Waiting for transition ({waitOut}s) before yielding control to player.", level: Log.Level.DEBUG3);
        req.onLoaded?.Invoke();
        for (; waitOut > ETime.FRAME_YIELD; waitOut -= ETime.FRAME_TIME) yield return null;
        req.onFinished?.Invoke();
        LOADING = false;
        GameStateManager.SetLoading(false);
    }

    private static readonly List<Action> sceneLoadDelegates = new List<Action>();
    private static readonly List<Action> sceneUnloadDelegates = new List<Action>();
    private static readonly List<Action> presceneUnloadDelegates = new List<Action>();
    private static void StaticSceneLoaded(Scene s, LoadSceneMode lsm) {
        //Log.Unity("Static scene loading procedures (invoked by Unity)");
        for (int ii = 0; ii < sceneLoadDelegates.Count; ++ii) {
            sceneLoadDelegates[ii]();
        }
    }
    private static void StaticSceneUnloaded(Scene s) {
        for (int ii = 0; ii < sceneUnloadDelegates.Count; ++ii) {
            sceneUnloadDelegates[ii]();
        }
    }
    private static void StaticPreSceneUnloaded() {
        for (int ii = 0; ii < presceneUnloadDelegates.Count; ++ii) {
            presceneUnloadDelegates[ii]();
        }
    }

    public static void RegisterSceneLoad(Action act) {
        sceneLoadDelegates.Add(act);
    }
    public static void RegisterSceneUnload(Action act) {
        sceneUnloadDelegates.Add(act);
    }
    public static void RegisterPreSceneUnload(Action act) {
        presceneUnloadDelegates.Add(act);
    }

    //Invoked by ETime
    public static void Attach() {
        SceneManager.sceneLoaded += StaticSceneLoaded;
        SceneManager.sceneUnloaded += StaticSceneUnloaded;
    }
}