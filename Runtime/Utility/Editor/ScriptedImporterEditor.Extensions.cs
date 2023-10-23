using UnityEditor;
using UnityEditor.AssetImporters;

namespace Utility
{
    public static class ScriptedImporterEditorExtensions
    {
        public static SerializedProperty FindProperty(this ScriptedImporterEditor s, string name)
        {
            return s.serializedObject.FindProperty(name);
        }
    }
}