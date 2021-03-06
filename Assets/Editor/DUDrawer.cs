﻿using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class DisplayHelpers {
    public static IEnumerable<SerializedProperty> Properties(SerializedProperty property, IEnumerable<string> propNames) =>
        propNames.Select(property.FindPropertyRelative);

    public static float GetPropertyHeight(SerializedProperty property, IEnumerable<string> propNames, GUIContent label) {
        return -EditorGUIUtility.standardVerticalSpacing + Properties(property, propNames)
                   .Sum(sp => EditorGUIUtility.standardVerticalSpacing + EditorGUI.GetPropertyHeight(sp, true));
    }
}

public abstract class DUDrawer : PropertyDrawer {
    protected virtual string[] EnumValues { get; }
    protected virtual string propName(int enumIndex) => EnumValues[enumIndex]; //property names same as enum names by default
    protected virtual string enumProperty => "type";
    
    /// <summary> Cached style to use to draw the popup button. </summary>
    private GUIStyle popupStyle;

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
        if (popupStyle == null) {
            popupStyle = new GUIStyle(GUI.skin.GetStyle("PaneOptions"));
            popupStyle.imagePosition = ImagePosition.ImageOnly;
        }

        label = EditorGUI.BeginProperty(position, label, property);
        position = EditorGUI.PrefixLabel(position, label);

        EditorGUI.BeginChangeCheck();

        // Calculate rect for configuration button
        Rect buttonRect = new Rect(position);
        buttonRect.yMin += popupStyle.margin.top;
        buttonRect.width = popupStyle.fixedWidth + popupStyle.margin.right;
        position.xMin = buttonRect.xMax;

        // Store old indent level and set it to 0, the PrefixLabel takes care of it
        int indent = EditorGUI.indentLevel;
        EditorGUI.indentLevel = 0;
        
        SerializedProperty enumVal = property.FindPropertyRelative(enumProperty);
        enumVal.enumValueIndex = EditorGUI.Popup(buttonRect, enumVal.enumValueIndex, EnumValues, popupStyle);

        EditorGUI.PropertyField(position, property.FindPropertyRelative(propName(enumVal.enumValueIndex)), GUIContent.none);

        if (EditorGUI.EndChangeCheck())
            property.serializedObject.ApplyModifiedProperties();

        EditorGUI.indentLevel = indent;
        EditorGUI.EndProperty();
    }
}

public abstract class DUDisplayDrawer<T> : PropertyDrawer {
    protected virtual string[] EnumValues { get; }
    protected virtual string[] EnumDisplayValues => EnumValues;
    protected virtual string propName(int enumIndex) => EnumValues[enumIndex]; //property names same as enum names by default
    protected virtual string enumProperty => "type";
    protected virtual float EnumWidth => 0.2f;
    
    private const float LabelOffset = 0.05f;
    protected virtual string[] OtherPropNames() {
        var enumvals = new HashSet<string>(EnumValues);
        return typeof(T).GetFields().Select(x => x.Name).Where(x => !enumvals.Contains(x) && x != enumProperty).ToArray();
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label) {
        SerializedProperty enumVal = property.FindPropertyRelative(enumProperty);
        SerializedProperty duVal = property.FindPropertyRelative(propName(enumVal.enumValueIndex));
        return EditorGUI.GetPropertyHeight(enumVal) + EditorGUI.GetPropertyHeight(duVal, true) + 
               DisplayHelpers.GetPropertyHeight(property, OtherPropNames(), label) + 
               2 * EditorGUIUtility.standardVerticalSpacing;
    }

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
        label = EditorGUI.BeginProperty(position, label, property);
        var label_position = position;
        position = EditorGUI.PrefixLabel(position, label);

        EditorGUI.BeginChangeCheck();
        SerializedProperty enumVal = property.FindPropertyRelative(enumProperty);
        SerializedProperty duVal = property.FindPropertyRelative(propName(enumVal.enumValueIndex));

        //int indent = EditorGUI.indentLevel;
        //EditorGUI.indentLevel = 0;

        float pw = position.width;
        float lw = label_position.width;
        
        float h1 = EditorGUI.GetPropertyHeight(enumVal);
        Rect rect1 = new Rect(position.x, position.y, pw, h1);
        
        float prevHeight = position.y + h1; //Don't need to subtract vertical spacing since 1 is alreadyt required
        var propnames = OtherPropNames();
        var props = DisplayHelpers.Properties(property, propnames).ToArray();
        for (int ii = 0; ii < props.Length; ++ii) {
            var sp = props[ii];
            prevHeight += EditorGUIUtility.standardVerticalSpacing;
            var myHeight = EditorGUI.GetPropertyHeight(sp, true);
            Rect r = new Rect(label_position.x, prevHeight, lw, myHeight);
            EditorGUI.PropertyField(r, sp, new GUIContent(propnames[ii]));
            prevHeight += myHeight;
        }
        Rect rect2 = new Rect(label_position.x + lw * LabelOffset, prevHeight + EditorGUIUtility.standardVerticalSpacing, 
            lw * (1-LabelOffset), EditorGUI.GetPropertyHeight(duVal, true));

        EditorGUI.PropertyField(rect1, enumVal, GUIContent.none);
        EditorGUI.PropertyField(rect2, duVal, GUIContent.none);

        if (EditorGUI.EndChangeCheck())
            property.serializedObject.ApplyModifiedProperties();

        //EditorGUI.indentLevel = indent;
        EditorGUI.EndProperty();
    }
}
[CustomPropertyDrawer(typeof(BackgroundTransition))]
public class BGTransitionDrawer : DUDisplayDrawer<BackgroundTransition> {
    protected override string[] EnumValues { get; } = { "WipeTex", "Wipe1", "WipeFromCenter", "Shatter4", "WipeY" };
}