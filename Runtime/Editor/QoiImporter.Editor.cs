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
        SerializedProperty _format;
        SerializedProperty _platformSettings;
        SerializedProperty _compressor;

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
            _format = Find("format");
            _platformSettings = Find("platformSettings");
            _compressor = Find("compressor");
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

            // Size and compression, per platform. The first tab (Unknown) holds the default settings.
            GUILayout.Space(20);
            var group = BeginBuildTargetSelectionGrouping();
            if (group == BuildTargetGroup.Unknown)
                DrawSettings(_maxTextureSize, _format, _compression, _compressionQuality, _crunchedCompression, _compressor, group);
            else
                DrawOverride(group);
            EndBuildTargetSelectionGrouping();

            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }

        private void DrawOverride(BuildTargetGroup group)
        {
            string name = group.ToString();
            SerializedProperty entry = null;
            for (int i = 0; i < _platformSettings.arraySize; i++)
            {
                var e = _platformSettings.GetArrayElementAtIndex(i);
                if (e.FindPropertyRelative("buildTarget").stringValue == name) entry = e;
            }

            bool overridden = entry != null && entry.FindPropertyRelative("overridden").boolValue;
            EditorGUI.BeginChangeCheck();
            bool on = Toggle($"Override For {ObjectNames.NicifyVariableName(name)}", overridden);
            if (EditorGUI.EndChangeCheck())
            {
                // A new override starts from the default settings, a disabled one keeps its values
                if (entry == null)
                {
                    int i = _platformSettings.arraySize;
                    _platformSettings.InsertArrayElementAtIndex(i);
                    entry = _platformSettings.GetArrayElementAtIndex(i);
                    entry.FindPropertyRelative("buildTarget").stringValue = name;
                    entry.FindPropertyRelative("maxTextureSize").intValue = _maxTextureSize.intValue;
                    entry.FindPropertyRelative("format").intValue = _format.intValue;
                    entry.FindPropertyRelative("compression").intValue = _compression.intValue;
                    entry.FindPropertyRelative("compressionQuality").intValue = _compressionQuality.intValue;
                    entry.FindPropertyRelative("crunchedCompression").boolValue = _crunchedCompression.boolValue;
                    entry.FindPropertyRelative("compressor").intValue = _compressor.intValue;
                }
                entry.FindPropertyRelative("overridden").boolValue = on;
            }

            if (on)
                DrawSettings(entry.FindPropertyRelative("maxTextureSize"), entry.FindPropertyRelative("format"),
                    entry.FindPropertyRelative("compression"), entry.FindPropertyRelative("compressionQuality"),
                    entry.FindPropertyRelative("crunchedCompression"), entry.FindPropertyRelative("compressor"), group);
            else
                using (new EditorGUI.DisabledScope(true))
                    DrawSettings(_maxTextureSize, _format, _compression, _compressionQuality, _crunchedCompression, _compressor, group);
        }

        private static void DrawSettings(SerializedProperty maxSize, SerializedProperty format, SerializedProperty compression,
            SerializedProperty quality, SerializedProperty crunch, SerializedProperty compressor, BuildTargetGroup group)
        {
            PropertyField(maxSize, new GUIContent("Max Size"));
            PropertyField(format, new GUIContent("Format",
                "Automatic: BC on desktop, ASTC on mobile, following Compression. BC, ETC and EAC need a width and height multiple of 4."));

            var f = (QoiTextureFormat)format.intValue;
            bool automatic = f == QoiTextureFormat.Automatic;
            using (new EditorGUI.DisabledScope(!automatic))
                PropertyField(compression, new GUIContent("Compression", "Used by the Automatic format"));

            bool uncompressed = f == QoiTextureFormat.Uncompressed ||
                                (automatic && compression.intValue == (int)TextureImporterCompression.Uncompressed);
            bool crunchable = automatic || f is QoiTextureFormat.BC1 or QoiTextureFormat.BC3 or QoiTextureFormat.ETC2_RGB or QoiTextureFormat.ETC2_RGBA;
            using (new EditorGUI.DisabledScope(uncompressed))
            {
                PropertyField(compressor, new GUIContent("Compressor",
                    "Unity: Unity's compressor. Fast: the package's multithreaded Burst compressor, for BC1, BC3, BC4 and BC5 " +
                    "of 8-bit images without crunch. Other formats fall back to Unity's compressor."));
                PropertyField(quality, new GUIContent("Compressor Quality",
                    "Quality to speed tradeoff. Unity compressor: its own quality setting. Fast compressor: search effort of ASTC " +
                    "(Fast: quickest, Normal, Best: about twice as slow as Normal, slightly better)."));
                using (new EditorGUI.DisabledScope(!crunchable))
                    PropertyField(crunch, new GUIContent("Crunch"));
            }

            bool mobile = group is BuildTargetGroup.Android or BuildTargetGroup.iOS;
            bool desktop = group is BuildTargetGroup.Standalone;
            bool bc = f is >= QoiTextureFormat.BC1 and <= QoiTextureFormat.BC7;
            bool mobileOnly = f >= QoiTextureFormat.ETC2_RGB;
            if ((mobile && bc) || (desktop && mobileOnly))
                HelpBox("Most GPUs of this platform can't sample this format: Unity decompresses it when loading.", MessageType.Warning);
            else if (f >= QoiTextureFormat.ASTC_HDR_4x4)
                HelpBox("ASTC HDR needs GPU support: check SystemInfo.IsFormatSupported on the target devices.", MessageType.Info);
        }

        private SerializedProperty Find(string n) => serializedObject.FindProperty(n);
    }
}
