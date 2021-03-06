﻿using System.Collections;
using System.Collections.Generic;
using Danmaku;
using DMath;
using UnityEngine;
using Collision = DMath.Collision;

public class CircDrawer : Drawer {
    private BPRV2 locate;

    public void Initialize(TP4 colorizer, BPRV2 locater) {
        base.Initialize(colorizer);
        locate = locater;
    }

    protected override V2RV2 GetLocScaleRot() => locate(bpi);

}
