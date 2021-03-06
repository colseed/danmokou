﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using JetBrains.Annotations;
using SM;
using UnityEngine;
using static SM.SMAnalysis;
using GameLowRequest = DU<Danmaku.CampaignRequest, Danmaku.BossPracticeRequest, 
    ChallengeRequest, Danmaku.StagePracticeRequest>;
using static GameManagement;
using static SceneIntermediary;
using static StaticNullableStruct;
using static Danmaku.Enums;

//https://issuetracker.unity3d.com/issues/build-that-contains-a-script-with-a-struct-which-has-a-static-nullable-reference-to-itself-fails-on-il2cpp
public static class StaticNullableStruct {
    public static Danmaku.GameRequest? LastGame { get; set; } = null;
}

namespace Danmaku {

public readonly struct BossPracticeRequest {
    public readonly AnalyzedBoss boss;
    public readonly Phase phase;
    public Enums.PhaseType PhaseType => phase.type;

    public BossPracticeRequest(AnalyzedBoss boss, Phase? phase = null) {
        this.boss = boss;
        //0th listed phase is phase index 1
        this.phase = phase ?? boss.phases[0];
    }

    public ((string, int), int) Key => (boss.Key, phase.index);
    public static BossPracticeRequest Reconstruct(((string, int), int) key) {
        var boss = AnalyzedBoss.Reconstruct(key.Item1);
        return new BossPracticeRequest(boss, boss.phases.First(p => p.index == key.Item2));
    }
}

public readonly struct StagePracticeRequest {
    public readonly AnalyzedStage stage;
    public readonly int phase;
    public readonly LevelController.LevelRunMethod method;

    public StagePracticeRequest(AnalyzedStage stage, int phase, LevelController.LevelRunMethod method = LevelController.LevelRunMethod.CONTINUE) {
        this.stage = stage;
        this.phase = phase;
        this.method = method;
    }
    
    public ((string, int), int) Key => (stage.Key, phase);
    public static StagePracticeRequest Reconstruct(((string, int), int) key) =>
        new StagePracticeRequest(AnalyzedStage.Reconstruct(key.Item1), key.Item2);
}

public readonly struct CampaignRequest {
    public readonly AnalyzedCampaign campaign;

    public CampaignRequest(AnalyzedCampaign campaign) {
        this.campaign = campaign;
    }
    
    public string Key => campaign.Key;
    public static CampaignRequest Reconstruct(string key) =>
        new CampaignRequest(AnalyzedCampaign.Reconstruct(key));
    
}

public readonly struct GameRequest {
    [CanBeNull] public readonly Func<bool> cb;
    public readonly bool newCampaign;
    [CanBeNull] public readonly ShotConfig shot;
    public readonly DifficultySet difficulty;
    public readonly CampaignMode mode;
    public readonly Replay? replay;
    public readonly GameLowRequest lowerRequest;
    public readonly int seed;

    public GameRequest(Func<bool> cb, GameLowRequest lowerRequest, 
        Replay replay) : this(cb, replay.metadata.Difficulty, lowerRequest, true, 
        replay.metadata.Shot, replay) {}

    public GameRequest(Func<bool> cb, 
        DifficultySet difficulty = DifficultySet.Abex, CampaignRequest? campaign = null,
        BossPracticeRequest? boss = null, ChallengeRequest? challenge = null, StagePracticeRequest? stage = null,
        bool newCampaign = true, ShotConfig shot = null, Replay? replay = null) : 
        this(cb, difficulty,  GameLowRequest.FromNullable(
            campaign, boss, challenge, stage) ?? throw new Exception("No valid request type made of GameReq"), 
            newCampaign, shot, replay) { }

    private GameRequest(Func<bool> cb, DifficultySet difficulty,
        GameLowRequest lowerRequest,
        bool newCampaign, ShotConfig shot, Replay? replay) {
        this.mode = lowerRequest.Resolve(
            _ => CampaignMode.MAIN, 
            _ => CampaignMode.CARD_PRACTICE, 
            _ => CampaignMode.SCENE_CHALLENGE, 
            _ => CampaignMode.STAGE_PRACTICE);
        this.cb = cb;
        this.newCampaign = newCampaign;
        this.shot = shot;
        this.difficulty = difficulty;
        this.replay = replay;
        this.lowerRequest = lowerRequest;
        this.seed = replay?.metadata.Seed ?? new System.Random().Next();
    }

    public void SetupOrCheckpoint() {
        if (newCampaign) {
            Log.Unity(
                $"Starting game with mode {mode} on difficulty {difficulty.Describe()}.");
            GameManagement.Difficulty = difficulty;
            GameManagement.NewCampaign(mode, shot);
            if (replay == null) Replayer.BeginRecording();
            else Replayer.BeginReplaying(replay.Value.frames);
        } else Checkpoint();
    }

    private void Checkpoint() {
        GameManagement.CheckpointCampaignData();
    }

    public bool Finish() => cb?.Invoke() ?? true;
    public bool FinishAndPostReplay() {
        if (Finish()) {
            Replayer.End(this);
            return true;
        } else return false;
    }
    public void vFinishAndPostReplay() => FinishAndPostReplay();

    public bool Run() {
        var r = this;
        LastGame = r;
        RNG.Seed(seed);
        replay?.metadata.ApplySettings();
        return lowerRequest.Resolve(
            c => SelectCampaign(c, r),
            b => SelectBoss(b, r),
            c => SelectChallenge(c, r),
            s => SelectStage(s, r));
    }

    public static bool? Rerun() {
        if (LastGame == null) return null;
        else if (LastGame.Value.Run()) {
            if (LastGame.Value.mode.PreserveReloadAudio()) AudioTrackService.PreserveBGM();
            return true;
        } else return false;
    }

    public void vRun() => Run();

    private static bool SelectCampaign(CampaignRequest c, GameRequest req) {
        bool ExecuteStage(int index) {
            if (index < c.campaign.stages.Length) {
                var s = c.campaign.stages[index];
                return SceneIntermediary.LoadScene(new SceneRequest(s.stage.sceneConfig,
                    SceneRequest.Reason.RUN_SEQUENCE,
                    (index == 0) ? req.SetupOrCheckpoint : (Action)req.Checkpoint,
                    //Note: this load during onHalfway is for the express purpose of preventing load lag
                    () => StateMachineManager.FromText(s.stage.stateMachine),
                    () => LevelController.Request(new LevelController.LevelRunRequest(1, () => ExecuteStage(index + 1), 
                        LevelController.LevelRunMethod.CONTINUE, s.stage))));
            } else if (req.FinishAndPostReplay()) {
                Log.Unity("Stage sequence finished.");
                return true;
            } else return false;
        }
        return ExecuteStage(0);
    }
    
    private static bool SelectStage(StagePracticeRequest s, GameRequest req) =>
        SceneIntermediary.LoadScene(new SceneRequest(s.stage.stage.sceneConfig,
            SceneRequest.Reason.START_ONE,
            req.SetupOrCheckpoint,
            //Note: this load during onHalfway is for the express purpose of preventing load lag
            () => StateMachineManager.FromText(s.stage.stage.stateMachine),
            () => LevelController.Request(
                new LevelController.LevelRunRequest(s.phase, req.vFinishAndPostReplay, s.method, s.stage.stage))));
    

    private static bool SelectBoss(BossPracticeRequest ab, GameRequest req) {
        var b = ab.boss.boss;
        BackgroundOrchestrator.NextSceneStartupBGC = b.Background(ab.PhaseType);
        return SceneIntermediary.LoadScene(new SceneRequest(References.unitScene,
            SceneRequest.Reason.START_ONE,
            req.SetupOrCheckpoint,
            //Note: this load during onHalfway is for the express purpose of preventing load lag
            () => StateMachineManager.FromText(b.stateMachine),
            () => {
                var beh = UnityEngine.Object.Instantiate(b.boss).GetComponent<BehaviorEntity>();
                beh.behaviorScript = b.stateMachine;
                beh.phaseController.Override(ab.phase.index, req.vFinishAndPostReplay);
            }));
    }

    private static bool SelectChallenge(ChallengeRequest cr, GameRequest req) {
        BackgroundOrchestrator.NextSceneStartupBGC = cr.Boss.Background(cr.phase.phase.type);
        return SceneIntermediary.LoadScene(new SceneRequest(References.unitScene,
            SceneRequest.Reason.START_ONE,
            req.SetupOrCheckpoint,
            () => ChallengeManager.TrackChallenge(req, cr),
            () => {
                var beh = UnityEngine.Object.Instantiate(cr.Boss.boss).GetComponent<BehaviorEntity>();
                ChallengeManager.LinkBEH(beh);
            }));
    }
    


    private static SceneConfig MaybeSaveReplayScene => 
        (References.replaySaveMenu != null && (Replayer.IsRecording || Replayer.PostedReplay != null)) ?
        References.replaySaveMenu : References.mainMenu;

    public static bool WaitDefaultReturn() {
        if (SceneIntermediary.LOADING) return false;
        GlobalSceneCRU.Main.RunDroppableRIEnumerator(WaitingUtils.WaitFor(1f, Cancellable.Null, () =>
            LoadScene(new SceneRequest(MaybeSaveReplayScene,
                SceneRequest.Reason.FINISH_RETURN))));
        return true;
    }

    public static bool DefaultReturn() => SceneIntermediary.LoadScene(
        new SceneRequest(MaybeSaveReplayScene, SceneRequest.Reason.FINISH_RETURN)
    );
    
    public static bool ShowPracticeSuccessMenu() {
        GameStateManager.SendSuccessEvent();
        return true;
    }
    public static bool WaitShowPracticeSuccessMenu() {
        if (SceneIntermediary.LOADING) return false;
        GlobalSceneCRU.Main.RunDroppableRIEnumerator(WaitingUtils.WaitFor(1f, Cancellable.Null, GameStateManager.SendSuccessEvent));
        return true;
    }
    public static bool ViewReplay(Replay r) {
        return new GameRequest(WaitDefaultReturn, r.metadata.ReconstructedRequest, r).Run();
    }

    public static bool ViewReplay(Replay? r) => r != null && ViewReplay(r.Value);
    

    public static void RunCampaign([CanBeNull] AnalyzedCampaign campaign, [CanBeNull] Func<bool> cb, DifficultySet difficulty, 
        [CanBeNull] ShotConfig shot) {
        if (campaign == null) return;
        var req = new GameRequest(cb.Then(() => LoadScene(new SceneRequest(MaybeSaveReplayScene, 
            SceneRequest.Reason.FINISH_RETURN, () => {
                GameManagement.CheckpointCampaignData();
                if (!Replayer.IsReplaying) {
                    SaveData.r.CompletedCampaigns.Add(campaign.campaign.key);
                    SaveData.SaveRecord();
                }
            }))), difficulty, campaign: new CampaignRequest(campaign), shot: shot);


        if (SaveData.r.TutorialDone || References.miniTutorial == null) req.Run();
        else LoadScene(new SceneRequest(References.miniTutorial,
            SceneRequest.Reason.START_ONE,
            //Prevents hangover information from previous campaign, will be overriden anyways
            req.SetupOrCheckpoint,
            null, 
            () => MiniTutorial.RunMiniTutorial(req.vRun)));
    }

    public static bool RunTutorial() => 
        SceneIntermediary.LoadScene(new SceneRequest(References.tutorial, SceneRequest.Reason.START_ONE, 
            () => GameManagement.NewCampaign(CampaignMode.TUTORIAL, null)));


}
}
