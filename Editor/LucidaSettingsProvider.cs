using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lucida.Editor
{
    /// <summary>Project Settings > Lucida: creates and edits <c>Assets/Resources/LucidaSettings.asset</c>.</summary>
    public static class LucidaSettingsProvider
    {
        public const string AssetPath = "Assets/Resources/" + LucidaSettings.ResourceName + ".asset";

        /// <summary>The settings asset, created on first use.</summary>
        public static LucidaSettings GetOrCreate()
        {
            var s = AssetDatabase.LoadAssetAtPath<LucidaSettings>(AssetPath);
            if (s != null) return s;
            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
            s = ScriptableObject.CreateInstance<LucidaSettings>();
            AssetDatabase.CreateAsset(s, AssetPath);
            AssetDatabase.SaveAssets();
            return s;
        }

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider("Project/Lucida", SettingsScope.Project)
            {
                label = "Lucida",
                keywords = new[] { "lucida", "analytics", "replay", "flags" },
                guiHandler = _ =>
                {
                    var so = new SerializedObject(GetOrCreate());
                    EditorGUILayout.PropertyField(so.FindProperty("ApiKey"), new GUIContent("Project key (lc_pk_...)"));
                    EditorGUILayout.PropertyField(so.FindProperty("Host"));
                    EditorGUILayout.PropertyField(so.FindProperty("ConsentMode"));
                    EditorGUILayout.PropertyField(so.FindProperty("AutoInit"), new GUIContent("Start before the first scene"));
                    EditorGUILayout.PropertyField(so.FindProperty("SessionReplay"), new GUIContent("Session replay (wireframe)"));
                    EditorGUILayout.PropertyField(so.FindProperty("CaptureScene"), new GUIContent("Replay 3D objects as boxes"));
                    so.ApplyModifiedProperties();
                    var problem = ((LucidaSettings)so.targetObject).Validate();
                    if (problem != null) EditorGUILayout.HelpBox(problem, MessageType.Error);
                    EditorGUILayout.HelpBox("Text, inputs and images are masked in replay by default. Add LucidaReplayUnmask or LucidaReplayBlock to a GameObject to change that.", MessageType.Info);
                },
            };
        }
    }
}
