using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using static UnityEditor.EditorGUILayout;

namespace LTMX.QOI.Editor
{
    /// <summary>
    /// Qoi Importer Editor
    /// </summary>
    [CustomEditor(typeof(QoiImporter))]
    public class QoiImporterEditor : ScriptedImporterEditor
    {
        SerializedProperty _textureType;
        SerializedProperty _textureShape;
        SerializedProperty _cubemapLayout;
        SerializedProperty _alphaIsTransparency;
        SerializedProperty _mipMapEnabled;
        SerializedProperty _npotScale;
        SerializedProperty _sRGBTexture;

        SerializedProperty _wrapMode;
        SerializedProperty _filterMode;
        SerializedProperty _anisoLevel;
        SerializedProperty _maxTextureSize;

        // Compression
        SerializedProperty _compression;
        SerializedProperty _compressionQuality;
        SerializedProperty _crunchedCompression;

        public override void OnEnable()
        {
            base.OnEnable();
            _textureType = Find("textureType");
            _textureShape = Find("textureShape");
            _cubemapLayout = Find("cubemapLayout");
            _alphaIsTransparency = Find("alphaIsTransparency");
            _mipMapEnabled = Find("mipMapEnabled");
            _npotScale = Find("npotScale");
            _sRGBTexture = Find("sRGBTexture");

            _wrapMode = Find("wrapMode");
            _filterMode = Find("filterMode");
            _anisoLevel = Find("anisoLevel");
            _maxTextureSize = Find("maxTextureSize");

            _compression = Find("compression");
            _compressionQuality = Find("compressionQuality");
            _crunchedCompression = Find("crunchedCompression");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            PropertyField(_textureType, new GUIContent("Texture Type"));

            // Only 2D and Cube are supported
            bool isCube = _textureShape.intValue == (int)TextureImporterShape.TextureCube;
            EditorGUI.BeginChangeCheck();
            int shape = Popup("Texture Shape", isCube ? 1 : 0, new[] { "2D", "Cube" });
            if (EditorGUI.EndChangeCheck())
                _textureShape.intValue = (int)(shape == 1 ? TextureImporterShape.TextureCube : TextureImporterShape.Texture2D);
            if (shape == 1)
            {
                EditorGUI.indentLevel++;
                PropertyField(_cubemapLayout, new GUIContent("Mapping",
                    "Auto: 2:1 = latitude-longitude (Skybox/Panoramic convention), 6:1 / 1:6 = face strip (+X -X +Y -Y +Z -Z)"));
                EditorGUI.indentLevel--;
            }

            PropertyField(_alphaIsTransparency, new GUIContent("Alpha is Transparency"));
            PropertyField(_mipMapEnabled, new GUIContent("Enable MipMaps"));
            using (new EditorGUI.DisabledScope(_textureType.enumValueFlag == (int)TextureImporterType.Sprite))
                PropertyField(_npotScale, new GUIContent("Non Power of 2", "Rescales non power of two textures (not applied to sprites)"));
            PropertyField(_sRGBTexture, new GUIContent("sRGB", "sRGB Texture (ignored for Normal Maps)"));

            GUILayout.Space(20);
            PropertyField(_wrapMode, new GUIContent("Wrap Mode"));
            PropertyField(_filterMode, new GUIContent("Filter Mode"));
            PropertyField(_anisoLevel, new GUIContent("Aniso Level"));
            PropertyField(_maxTextureSize, new GUIContent("Max Size"));

            GUILayout.Space(20);
            GUILayout.Label("Compression", EditorStyles.boldLabel);
            PropertyField(_compression, new GUIContent("Compression", "Block compression needs a width and height multiple of 4"));
            using (new EditorGUI.DisabledScope(_compression.enumValueIndex == (int)TextureImporterCompression.Uncompressed))
            {
                PropertyField(_compressionQuality, new GUIContent("Quality"));
                PropertyField(_crunchedCompression, new GUIContent("Crunch"));
            }

            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }

        private SerializedProperty Find(string n) => serializedObject.FindProperty(n);
    }
}
