using UnityEditor;
using UnityEditor.AssetImporters;

public static class ScriptedImporterEditorExtensions
{
    public static SerializedProperty FindProperty(this ScriptedImporterEditor s, string name)
    {
        return s.serializedObject.FindProperty(name);
    }
}