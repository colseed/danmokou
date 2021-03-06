﻿using System;
using System.Linq.Expressions;
using DMath;
using JetBrains.Annotations;
using UnityEngine;
using Random = System.Random;

/// <summary>
/// Centralized class for access to randomness.
/// Provides functions for deterministic randomness, as well as "offFrame" functions that
/// should be used for non-gameplay-related randomness (camera shake, particle effects, etc).  
/// </summary>
public static class RNG {
    private static Random rand = new Random();
    //Use this instead when the code is not in the RU loop
    private static readonly Random offFrame = new Random();
    
    public static void Seed(int seed) {
        rand = new Random(seed);
    }

    private static void RNGGuard() {
        if (GameStateManager.IsLoadingOrPaused && !SceneIntermediary.LOADING) {
            Log.Unity("You are invoking random functions while replay data is not being saved. " +
                      "This will desync replays.", true, Log.Level.WARNING);
        }
    }

    public static uint GetUInt() {
        RNGGuard();
        /*var u = (((uint) rand.Next(1 << 30)) << 2) | (uint) rand.Next(1 << 2);
        Log.Unity(u.ToString());
        return u;*/
        return (((uint) rand.Next(1 << 30)) << 2) | (uint) rand.Next(1 << 2);
    }

    public const uint HalfMax = uint.MaxValue / 2;

    private const long knuth = 2654435761;
    public static uint Rehash(uint x) => (uint)(knuth * x);
    public static Expression Rehash(Expression uintx) => Expression.Convert(Expression.Multiply(Expression.Constant(knuth), Expression.Convert(uintx, typeof(long))), typeof(uint));

    public static int Rehash(int x) => (int) (knuth * x);

    private static int GetInt(int low, int high, Random r) {
        return r.Next(low, high);
    }

    public static int GetIntOffFrame(int low, int high) => GetInt(low, high, offFrame);
    private static float GetFloat(float low, float high, Random r) {
        return low + (high - low) * r.Next() / int.MaxValue;
    }

    public static float GetFloat(float low, float high) {
        RNGGuard();
        return GetFloat(low, high, rand);
    }

    public static Vector2 GetPointInCircle(float lowR, float highR) =>
        GetFloat(lowR, highR) * M.CosSin(GetFloat(0, M.TAU));
    
    private static readonly ExFunction getFloat = ExUtils.Wrap(typeof(RNG), "GetFloat", new[] {typeof(float), typeof(float)});
    public static Expression GetFloat(Expression low, Expression high) => getFloat.Of(low, high);
    public static float GetFloatOffFrame(float low, float high) => GetFloat(low, high, offFrame);
    [UsedImplicitly]
    public static float GetSeededFloat(float low, float high, uint seedv) {
        return low + (high - low) * Rehash(seedv) / uint.MaxValue;
    }
    [UsedImplicitly]
    public static float GetSeededFloat(float low, float high, int seedv) {
        return low + (high - low) * (0.5f + ((float)Rehash(seedv) / int.MaxValue));
    }

    private const string CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    public static string _RandString(Random r, int len) {
        var stringChars = new char[len];
        for (int i = 0; i < stringChars.Length; i++) {
            stringChars[i] = CHARS[r.Next(CHARS.Length)];
        }
        return new string(stringChars);
    }

    public static string RandString(int len = 8) {
        RNGGuard();
        return _RandString(rand, len);
    }

    public static string RandStringOffFrame(int len = 16) => _RandString(offFrame, len);
}
