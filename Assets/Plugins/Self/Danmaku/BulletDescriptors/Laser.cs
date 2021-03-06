﻿using DMath;
using JetBrains.Annotations;
using UnityEditor;
using UnityEngine;

namespace Danmaku {
public class Laser : FrameAnimBullet {
    public LaserRenderCfg config;
    public CurvedTileRenderLaser ctr;
    public readonly struct PointContainer {
        [CanBeNull] public readonly BehaviorEntity beh;
        public readonly bool exists;

        public PointContainer([CanBeNull] BehaviorEntity beh) {
            this.beh = beh;
            this.exists = beh != null;
        }
    }

    private PointContainer endpt;

    protected override void Awake() {
        ctr = new CurvedTileRenderLaser(config);
        rotationMethod = RotationMethod.Manual;
        base.Awake();
    }
    private void Initialize(bool isNew, BehaviorEntity parent, Velocity velocity, SOCircle _target, int firingIndex, uint bpiid, float cold,
        float hot, Recolor recolor, ref RealizedLaserOptions options) {
        ctr.SetYScale(options.yScale); //Needs to be done before Colorize sets first frame
        Colorize(recolor);
        base.Initialize(options.AsBEH, parent, velocity, firingIndex, bpiid, _target, out _); // Call after Awake/Reset
        if (options.endpoint != null) {
            var beh = BEHPooler.INode(Vector2.zero, DMath.V2RV2.Zero, Vector2.right, firingIndex, null, options.endpoint);
            endpt = new PointContainer(beh);
            ctr.SetupEndpoint(endpt);
        } else ctr.SetupEndpoint(new PointContainer(null));
        ctr.Initialize(this, material, isNew, bpi.id, firingIndex, ref options);
        ctr.UpdateLaserStyle(recolor.style);
        SFXService.Request(options.firesfx);
        SetColdHot(cold, hot, options.hotsfx, options.repeat);
        ctr.Activate(); //This invokes UpdateMesh
    }
    
    public override void RegularUpdateParallel() {
        ctr.UpdateMovement(ETime.FRAME_TIME);
    }

    protected override void RegularUpdateRender() {
        base.RegularUpdateRender();
        ctr.UpdateRender();
    }

    protected override DMath.CollisionResult CollisionCheck() => ctr.CheckCollision(collisionTarget);

    protected override void SetSprite(Sprite s, float yscale = 1f) {
        ctr.SetSprite(s, yscale);
    }

    /// <summary>
    /// TODO should I add support to FAB for time advancement?
    /// </summary>
    public override void SetTime(float t) {
        base.SetTime(t);
        ctr.SetLifetime(t);
    }

    public override void InvokeCull() {
        if (dying) return;
        ctr.Deactivate();
        if (endpt.exists) {
            endpt.beh.InvokeCull();
            endpt = new PointContainer(null);
        }
        base.InvokeCull();
    }

    public static void Request(Recolor prefab, BehaviorEntity parent, Velocity vel, int firingIndex, uint bpiid, 
        float cold, float hot, SOCircle collisionTarget, ref RealizedLaserOptions options) {
        Laser created = (Laser) BEHPooler.RequestUninitialized(prefab.prefab, out bool isNew);
        created.Initialize(isNew, parent, vel, collisionTarget, firingIndex, bpiid, cold, hot, prefab, ref options);
    }
    
    protected override void UpdateStyleControls() {
        base.UpdateStyleControls();
        ctr.UpdateLaserStyle(style);
    }

    protected override void SpawnSimple(string styleName) {
        ctr.SpawnSimple(styleName);
    }

    public V2RV2? Index(float time) => ctr.Index(time);
    
    private void OnDestroy() => ctr.Destroy();
    
#if UNITY_EDITOR
    private void OnDrawGizmosSelected() {
        ctr.Draw();
    }
#endif
}
}