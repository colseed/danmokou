﻿using UnityEngine;
using System;
using System.Collections.Generic;
using System.Linq;
using Core;
using Danmaku;
using DMath;
using JetBrains.Annotations;
using SM;
using UnityEditor;
using static Danmaku.Enums;
using static SM.SMAnalysis;

public struct CampaignData {
    private const int startLives = 7;
    public static int? StartLives(CampaignMode mode) {
        if (mode == CampaignMode.MAIN || mode == CampaignMode.TUTORIAL || mode == CampaignMode.STAGE_PRACTICE) return 7;
        if (mode.OneLife()) return 1;
        return null;
    }
    private const int defltContinues = 42;
    public const long valueItemPoints = 3142;
    public long maxScore { get; private set; }
    public long score { get; private set; }
    private long lastScore;
    public long UIVisibleScore { get; private set; }
    private double remVisibleScoreLerpTime;
    public const double visibleScoreLerpTime = 1f;
    public int Lives { get; private set; }
    public int LifeItems { get; private set; }
    public int NextLifeItems => pointLives.Try(nextItemLifeIndex, 9001);
    public long Graze { get; private set; }
    public double PIV { get; private set; }
    private double EffectivePIV => PIV + 0.01 * (long)(Graze / 42);
    private const double pivPerPoint = 0.01;
    public const double pivFallStep = 0.1;
    public double PIVDecay { get; private set; }
    private double pivDecayLenience;
    public double UIVisiblePIVDecayLenienceRatio { get; private set; }
    private const double pivDecayRate = 0.12;
    private double pivDecayRateMultiplier;
    private const double pivDecayRateMultiplierBoss = 0.7;
    private const double pivDecayLenienceFall = 3;
    private const double pivDecayLenienceValue = 0.4;
    private const double pivDecayLeniencePointPP = 0.5;
    private const double pivDecayLenienceGraze = 0.2;
    private const double pivDecayLenienceEnemyDestroy = 0.3;
    private const double pivDecayBoostValue = 0.02;
    private const double pivDecayBoostPointPP = 0.4;
    private const double pivDecayBoostGraze = 0.03;
    private const double pivDecayBoostEnemyDestroy = 0.07;
    
    private const double pivDecayLeniencePhase = 3;
    public bool Reloaded { get; set; }
    
    public int Continues { get; private set; }
    public int HitsTaken { get; private set; }
    public int EnemiesDestroyed { get; private set; }

    private int nextScoreLifeIndex;
    private int nextItemLifeIndex;
    public readonly CampaignMode mode;
    public bool Continued { get; private set; }
    [CanBeNull] public ShotConfig Shot { get; private set; }
    
    //TODO: this can cause problems if multiple phases are declared lenient at the same time, but that's not a current use case
    public bool Lenience { get; set; }

    private static readonly long[] scoreLives = {
         1000000,
         2000000,
         3000000,
         4000000,
         5000000,
         6000000,
         7000000,
         8000000,
         9000000,
        10000000,
        12000000,
        14000000,
        16000000,
        18000000,
        20000000,
        23000000,
        26000000,
        29000000,
        30000000,
        34000000,
        38000000,
        40000000,
        45000000,
        50000000,
        60000000,
        70000000,
        80000000,
        90000000,
        100000000,
    };
    private static readonly int[] pointLives = {
        42,
        69,
        127,
        255,
        314,
        420,
        533,
        666,
        789,
        859,
        999,
        1111,
        1204,
        1337,
        1414,
        1667,
        1799,
        2048,
        2718,
        3142,
        4200,
        6666,
        9001
    };

    public CampaignData(CampaignMode mode, long maxScore, ShotConfig shot = null) {
        this.mode = mode;
        this.maxScore = maxScore;
        this.Lives = StartLives(mode) ?? startLives;
        this.score = 0;
        this.PIV = 1;
        nextScoreLifeIndex = 0;
        nextItemLifeIndex = 0;
        remVisibleScoreLerpTime = 0;
        lastScore = 0;
        UIVisibleScore = 0;
        LifeItems = 0;
        PIVDecay = 1f;
        pivDecayLenience = 0f;
        UIVisiblePIVDecayLenienceRatio = 0f;
        Continues = mode.OneLife() ? 0 : defltContinues;
        Continued = false;
        Reloaded = false;
        HitsTaken = 0;
        Shot = shot;
        pivDecayRateMultiplier = 1f;
        EnemiesDestroyed = 0;
        Lenience = false;
        Graze = 0;
    }

    public bool TryContinue() {
        if (Continues > 0) {
            Continued = true;
            Replayer.Cancel();
            --Continues;
            score = lastScore = UIVisibleScore = nextItemLifeIndex = nextScoreLifeIndex = LifeItems = 0;
            PIV = 1;
            Lives = StartLives(mode) ?? startLives;
            remVisibleScoreLerpTime = PIVDecay = pivDecayLenience = 0;
            UIManager.UpdatePlayerUI();
            return true;
        } else return false;
    }

    public void AddLives(int delta) {
        Log.Unity($"Adding player lives: {delta}");
        if (delta < 0) ++HitsTaken;
        if (delta < 0 && mode.OneLife()) Lives = 0;
        else Lives = Math.Max(0, Lives + delta);
        if (Lives == 0) GameStateManager.HandlePlayerDeath();
        UIManager.UpdatePlayerUI();
    }

    /// <summary>
    /// Don't use this in the main campaign-- it will interfere with stats
    /// </summary>
    public void SetLives(int to) => AddLives(to - Lives);

    private void AddPIVDecay(double delta) => PIVDecay = Math.Min(1, PIVDecay + delta);
    private void AddPIVDecayLenience(double time) => pivDecayLenience = Math.Max(pivDecayLenience, time);
    public void ExternalLenience(double time) => AddPIVDecayLenience(time);

    public void AddValueItems(int delta) {
        AddPIVDecay(delta * pivDecayBoostValue);
        AddPIVDecayLenience(pivDecayLenienceValue);
        AddScore((long)Math.Round(delta * valueItemPoints * EffectivePIV));
    }
    public void AddGraze(int delta) {
        AddPIVDecay(delta * pivDecayBoostGraze);
        AddPIVDecayLenience(pivDecayLenienceGraze);
        Graze += delta;
        Counter.GrazeProc(delta);
        UIManager.UpdatePlayerUI();
    }

    public void AddPointPlusItems(int delta) {
        PIV += pivPerPoint * delta;
        AddPIVDecay(delta * pivDecayBoostPointPP);
        AddPIVDecayLenience(pivDecayLeniencePointPP);
        UIManager.UpdatePlayerUI();
    }

    private void LifeExtend() {
        ++Lives;
        SFXService.LifeExtend();
    }

    public void PhaseEnd(PhaseCompletion pc) {
        if (pc.props.phaseType?.IsPattern() ?? false) AddPIVDecayLenience(pivDecayLeniencePhase);
        SFXService.PhaseEndSound(pc.Captured);
        if (pc.props.phaseType == PhaseType.STAGE) SFXService.StageSectionEndSound();
        UIManager.CardCapture(pc);
        ChallengeManager.ReceivePhaseCompletion(pc);
    }
    
    private void AddScore(long delta) {
        lastScore = UIVisibleScore;
        score += delta;
        maxScore = Math.Max(maxScore, score);
        if (nextScoreLifeIndex < scoreLives.Length && score >= scoreLives[nextScoreLifeIndex]) {
            ++nextScoreLifeIndex;
            LifeExtend();
            UIManager.LifeExtendScore();
        }
        remVisibleScoreLerpTime = visibleScoreLerpTime;
        //updated in RegUpd
    }
    public void AddLifeItems(int delta) {
        LifeItems += delta;
        if (nextItemLifeIndex < pointLives.Length && LifeItems >= pointLives[nextItemLifeIndex]) {
            ++nextItemLifeIndex;
            LifeExtend();
            UIManager.LifeExtendItems();
        }
        UIManager.UpdatePlayerUI();
    }

    public void DestroyNormalEnemy() {
        EnemiesDestroyed++;
        AddPIVDecay(pivDecayBoostEnemyDestroy);
        AddPIVDecayLenience(pivDecayLenienceEnemyDestroy);
    }

    public void RegularUpdate() {
        if (remVisibleScoreLerpTime > 0) {
            remVisibleScoreLerpTime -= ETime.FRAME_TIME;
            if (remVisibleScoreLerpTime <= 0) UIVisibleScore = score;
            else UIVisibleScore = (long) M.Lerp(lastScore, score, 1 - remVisibleScoreLerpTime / visibleScoreLerpTime);
            UIManager.UpdatePlayerUI();
        }
        UIVisiblePIVDecayLenienceRatio = M.Lerp(UIVisiblePIVDecayLenienceRatio, pivDecayLenience / 3f, 6f * ETime.FRAME_TIME);
        if (PlayerInput.AllowPlayerInput && !Lenience && GameStateManager.IsRunning) {
            if (pivDecayLenience > 0) {
                pivDecayLenience = Math.Max(0, pivDecayLenience - ETime.FRAME_TIME);
            } else if (PIVDecay > 0) {
                PIVDecay = Math.Max(0, PIVDecay - ETime.FRAME_TIME * pivDecayRate * pivDecayRateMultiplier);
            } else if (PIV > 1) {
                PIV = Math.Max(1, PIV - pivFallStep);
                PIVDecay = 0.5f;
                pivDecayLenience = pivDecayLenienceFall;
                UIManager.UpdatePlayerUI();
            }
        }
    }

    public void OpenBoss() {
        pivDecayRateMultiplier *= pivDecayRateMultiplierBoss;
    }

    public void CloseBoss() {
        pivDecayRateMultiplier /= pivDecayRateMultiplierBoss;
    }

    public void AddDecayRateMultiplier_Tutorial(double m) {
        pivDecayRateMultiplier *= m;
    }
}

/// <summary>
/// A singleton manager for persistent game data.
/// This is the only scene-persistent object in the game.
/// </summary>
public class GameManagement : RegularUpdater {
    public static readonly Version EngineVersion = new Version(2, 0, 0);
    public static bool Initialized { get; private set; } = false;
    public static DifficultySet Difficulty { get; set; } = 
#if UNITY_EDITOR
        DifficultySet.Lunatic;
#else
        DifficultySet.Easy;
#endif
    /// <summary>
    /// A difficulty value is a multiplier. By default, Easy=1 and Lunatic~2.3.
    /// </summary>
    public static float DifficultyValue => Difficulty.Value();
    /// <summary>
    /// A fixed-step difficulty value. By default, Easy=1, Normal=2, etc.
    /// </summary>
    public static float DifficultyCounter => Difficulty.Counter();
    public static string DifficultyString => Difficulty.Describe();

    public static float RelativeDifficulty(DifficultySet basis) => DifficultyValue / basis.Value();

    public static CampaignData campaign = new CampaignData(CampaignMode.NULL, 9001);
    private static CampaignData lastinfo = campaign;

    public static void NewCampaign(CampaignMode mode, ShotConfig shot) => lastinfo = campaign = new CampaignData(mode, 9002, shot);
    public static void CheckpointCampaignData() => lastinfo = campaign;
    public static void ReloadCampaignData() {
        Debug.Log("Reloading campaign from last stage.");
        lastinfo.Reloaded = true;
        Replayer.Cancel();
        campaign = lastinfo;
        UIManager.UpdatePlayerUI();
    }


    [ContextMenu("Add 1000 value")]
    public void YeetScore() => campaign.AddValueItems(1000);
    [ContextMenu("Add 10 PIV+")]
    public void YeetPIV() => campaign.AddPointPlusItems(10);
    [ContextMenu("Add 40 life")]
    public void YeetLife() => campaign.AddLifeItems(40);
    public static IEnumerable<DifficultySet> VisibleDifficulties => new[] {
        DifficultySet.Easier, DifficultySet.Easy, DifficultySet.Normal, DifficultySet.Hard,
        DifficultySet.Lunatic, DifficultySet.Ultra, 
        //DifficultySet.Abex, DifficultySet.Assembly
    };
    
    private static GameManagement gm;
    public GameUniqueReferences references;
    public static GameUniqueReferences References => gm.references;
    public GameObject ghostPrefab;
    public GameObject inodePrefab;
    public GameObject lifeItem;
    public GameObject valueItem;
    public GameObject pointppItem;
    public GameObject arbitraryCapturer;
    public static GameObject ArbitraryCapturer => gm.arbitraryCapturer;
    public SceneConfig defaultSceneConfig;

    private void Awake() {
        if (gm != null) {
            DestroyImmediate(gameObject);
            return;
        }
        Initialized = true;
        gm = this;
        DontDestroyOnLoad(this);
        SceneIntermediary.Setup(defaultSceneConfig, References.defaultTransition);
        ParticlePooler.Prepare();
        GhostPooler.Prepare(ghostPrefab);
        BEHPooler.Prepare(inodePrefab);
        ItemPooler.Prepare(lifeItem, valueItem, pointppItem);
        ETime.RegisterPersistentSOFInvoke(Replayer.BeginFrame);
        ETime.RegisterPersistentSOFInvoke(Enemy.FreezeEnemies);
        ETime.RegisterPersistentEOFInvoke(BehaviorEntity.PruneControls);
        ETime.RegisterPersistentEOFInvoke(CurvedTileRenderLaser.PruneControls);
        SceneIntermediary.RegisterSceneUnload(ClearForScene);
        SceneIntermediary.RegisterSceneLoad(Replayer.LoadLazy);
    #if UNITY_EDITOR
        Log.Unity($"Graphics Jobs: {PlayerSettings.graphicsJobs} {PlayerSettings.graphicsJobMode}; MTR {PlayerSettings.MTRendering}");
    #endif
        Log.Unity($"Graphics Render mode {SystemInfo.renderingThreadingMode}");
        Log.Unity($"Danmokou {EngineVersion}, {References.gameIdentifier} {References.gameVersion}");
    }

    public static bool MainMenuExists => References.mainMenu != null;
    public static bool GoToMainMenu() => SceneIntermediary.LoadScene(
            new SceneIntermediary.SceneRequest(References.mainMenu, 
                SceneIntermediary.SceneRequest.Reason.ABORT_RETURN, Replayer.Cancel));
    public static bool GoToReplayScreen() => SceneIntermediary.LoadScene(
        new SceneIntermediary.SceneRequest(References.replaySaveMenu, 
            SceneIntermediary.SceneRequest.Reason.FINISH_RETURN));

    /// <summary>
    /// Reloads the specific level that is being run.
    /// This is for single-scene mini projects and is not generally exposed.
    /// </summary>
    /// <returns></returns>
    public static bool ReloadLevel() => SceneIntermediary._ReloadScene(ReloadCampaignData);
    /// <summary>
    /// Restarts the existing game if it exists, or reloads the specific level.
    /// </summary>
    /// <returns></returns>
    public static bool Restart() => GameRequest.Rerun() ?? ReloadLevel();
    
    public static void ClearForScene() {
        AudioTrackService.ClearAllAudio(false);
        BulletManager.ClearPoolControls();
        Events.Event0.DestroyAll();
        ETime.SlowdownReset();
        ETime.Timer.ResetAll();
        BulletManager.OrphanAll();
        DataHoisting.DestroyAll();
        StateMachineManager.ClearCachedSMs();
        BehaviorEntity.ClearPointers();
    }

    public static void LocalReset() {
        //AudioTrackService.ClearAllAudio();
        Events.Event0.DestroyAll();
        ETime.SlowdownReset();
        ETime.Timer.ResetAll();
        BehaviorEntity.DestroyAllSummons();
        DataHoisting.DestroyAll();
        StateMachineManager.ClearCachedSMs();
        BulletManager.ClearPoolControls();
        BulletManager.ClearEmpty();
        BulletManager.ClearAllBullets();
        BulletManager.DestroyCopiedPools();
        campaign = new CampaignData(CampaignMode.MAIN, 9001);
    }

    public static void ClearPhase() {
        BulletManager.ClearPoolControls();
        BulletManager.ClearEmpty();
        Events.Event0.Reset();
        ETime.SlowdownReset();
        ETime.Timer.ResetAll();
        //Delay this so copy pools can be softculled correctly
        ETime.QueueDelayedEOFInvoke(1, BulletManager.DestroyCopiedPools);
        //Delay this so that bullets referencing hosting data don't break down before
        //converting into softcull (note softcull bullets don't run velocity)
        ETime.QueueDelayedEOFInvoke(1, DataHoisting.ClearValues);
    }

    public static void ClearPhaseAutocull(string cullPool, string defaulter) {
        ClearPhase();
        BulletManager.Autocull(cullPool, defaulter);
        BehaviorEntity.Autocull(cullPool, defaulter);
    }
    


    [ContextMenu("Unload unused")]
    public void UnloadUnused() {
        Resources.UnloadUnusedAssets();
    }

    public override int UpdatePriority => UpdatePriorities.SYSTEM;

    public override void RegularUpdate() {
        campaign.RegularUpdate();
    }

    

    [CanBeNull] private static AnalyzedDays _days;
    public static AnalyzedDays Days => _days = _days ?? new AnalyzedDays(References.dayCampaign.days);
    private static IEnumerable<CampaignConfig> AllCampaignsRaw => new[] {References.campaign, References.exCampaign}.Where(c => c != null);

    [CanBeNull] private static AnalyzedCampaign[] _campaigns;
    public static AnalyzedCampaign[] Campaigns => _campaigns =
        _campaigns ?? AllCampaignsRaw.Select(c => new AnalyzedCampaign(c)).ToArray();

    public static IEnumerable<AnalyzedCampaign> FinishedCampaigns =>
        Campaigns.Where(c => SaveData.r.CompletedCampaigns.Contains(c.campaign.key));
    
    [CanBeNull]
    public static AnalyzedCampaign MainCampaign => Campaigns.First(c => c.campaign.key == References.campaign.key);
    [CanBeNull]
    public static AnalyzedCampaign ExtraCampaign => Campaigns.First(c => c.campaign.key == References.exCampaign.key);
    public static AnalyzedBoss[] PBosses => FinishedCampaigns.SelectMany(c => c.bosses).ToArray();
    public static AnalyzedStage[] PStages => FinishedCampaigns.SelectMany(c => c.practiceStages).ToArray();
    
    
}
