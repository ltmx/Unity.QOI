using UnityEditor;
using UnityEngine;
using UnityEditor.AssetImporters;
using static UnityEditor.EditorGUILayout;

[CustomEditor(typeof(QoiImporter))]
public class QoiImporterEditor: ScriptedImporterEditor
{
    
    public override void OnInspectorGUI()
    {
        if (PropertyField(this.FindProperty("Crunch"),  new GUIContent("Crunch")))
            HasModified();
        
        ApplyRevertGUI();
    }
}