using UnityEditor;
using UnityEngine;
using UnityEditor.AssetImporters;
using static UnityEditor.EditorGUILayout;

/// <summary>
/// Qoi Importer Editor
/// </summary>
[CustomEditor(typeof(QoiImporter))]
public class QoiImporterEditor: ScriptedImporterEditor
{
    // SerializedProperty _greyscaleToAlpha;
    SerializedProperty _alphaIsTransparency;
    SerializedProperty _mipMapEnabled;
    SerializedProperty _npotScale;
    SerializedProperty _sRGBTexture;

    SerializedProperty _textureType;
    // SerializedProperty _linearTexture; // Unused ?
    SerializedProperty _wrapMode;
    SerializedProperty _filterMode;
    SerializedProperty _anisoLevel;
    SerializedProperty _maxTextureSize;
    
    // Compression
    SerializedProperty _compressionQuality;
    SerializedProperty _crunchedCompression;

    private void Initialize()
    {
        _textureType = Find("textureType");
        // _greyscaleToAlpha = Find("grayscaleToAlpha");
        _alphaIsTransparency = Find("alphaIsTransparency");
        _mipMapEnabled = Find("mipMapEnabled");
        _npotScale = Find("npotScale");
        _sRGBTexture = Find("sRGBTexture");
        // _linearTexture = Find("linearTexture");
        
        _wrapMode = Find("wrapMode");
        _filterMode = Find("filterMode");
        _anisoLevel = Find("anisoLevel");
        _maxTextureSize = Find("maxTextureSize");
        
        _compressionQuality = Find("compressionQuality");
        _crunchedCompression = Find("crunchedCompression");
        
        
    }

    public override void OnInspectorGUI()
    {
        Initialize();

        PropertyField(_textureType, new GUIContent("Texture Type"));
        // PropertyField(_linearTexture, new GUIContent("Linear Texture"));
        
        
        // PropertyField(_greyscaleToAlpha, new GUIContent("Grayscale to Alpha"));
        PropertyField(_alphaIsTransparency, new GUIContent("Alpha is Transparency"));
        PropertyField(_mipMapEnabled, new GUIContent("Enable MipMaps"));
        PropertyField(_npotScale, new GUIContent("NPOT Scaling", "Non Power of Two Scaling"));
        PropertyField(_sRGBTexture, new GUIContent("sRGB", "sRGB Texture"));
        

        GUILayout.Space(20);
        // PropertyField(_textureShape, new GUIContent("Texture Shape"));
        PropertyField(_wrapMode, new GUIContent("Wrap Mode"));
        PropertyField(_filterMode, new GUIContent("Filter Mode"));
        PropertyField(_anisoLevel, new GUIContent("Aniso Level"));
        PropertyField(_maxTextureSize, new GUIContent("Max Size"));
        
        
        // [Header("CompressionLevel")]
        GUILayout.Space(20);
        GUILayout.Label("Compression", EditorStyles.boldLabel);
        PropertyField(_compressionQuality, new GUIContent("Quality"));
        // PropertyField(_crunchedCompressionQuality, new GUIContent("Crunched Compression Quality"));
        PropertyField(_crunchedCompression, new GUIContent("Crunch"));
        // HasModified();

        ApplyRevertGUI();
    }


    private SerializedProperty Find(string n) => serializedObject.FindProperty(n);
}

