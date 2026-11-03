using UnityEditor;
using UnityEngine;
using static LTMX.QOI.Editor.ImageSaveUtility;

namespace LTMX.QOI.Editor
{
    public class QOIEditorWindow : EditorWindow
    {
        private Texture2D tex;

        [MenuItem("Window/QOI Export Window")]
        static void Init()
        {
            // Get existing open window or if none, make a new one:
            var window = GetWindow<QOIEditorWindow>();
            window.Show();
        }

        void OnGUI()
        {
            GUILayout.BeginVertical();
            tex = TextureField("Texture", tex);
            GUILayout.Space(20);
            if (GUILayout.Button("Export To QOI") && tex != null)
                tex.SaveToFile(TextureEncodingFormat.QOI);
            GUILayout.EndVertical();
        }

        private static Texture2D TextureField(string name, Texture2D texture)
        {
            GUILayout.BeginVertical();
            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperCenter,
                fixedWidth = 70
            };
            GUILayout.Label(name, style);
            var result = (Texture2D)EditorGUILayout.ObjectField(texture, typeof(Texture2D), false, GUILayout.Width(70), GUILayout.Height(70));
            GUILayout.EndVertical();
            return result;
        }
    }
}
