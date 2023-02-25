using UnityEditor;
using UnityEngine;
using UnityEditor.AssetImporters;
using static UnityEditor.EditorGUILayout;

[CustomEditor(typeof(QoiImporter))]
public class QoiImporterEditor: ScriptedImporterEditor
{
    SerializedProperty _greyscaleToAlpha;
    SerializedProperty _alphaIsTransparency;
    SerializedProperty _mipMapEnabled;
    SerializedProperty _npotScale;
    SerializedProperty _sRGBTexture;
    // SerializedProperty _textureFormat;
    SerializedProperty _compressionQuality;
    SerializedProperty _crunchedCompressionQuality;
    SerializedProperty _crunchedCompression;
    SerializedProperty _textureType;
    // SerializedProperty _linearTexture;
    // SerializedProperty _textureShape;
    SerializedProperty _wrapMode;
    SerializedProperty _filterMode;
    SerializedProperty _anisoLevel;
    SerializedProperty _maxTextureSize;
    SerializedProperty _dimension;

    // public bool grayscaleToAlpha;
    // public bool alphaIsTransparency;
    // // public TextureImporterMipFilter mipmapFilter;
    // // public int mipMapBias = 0;
    // public bool mipMapEnabled = true;
    // public TextureImporterNPOTScale npotScale = TextureImporterNPOTScale.None;
    // // public bool isReadable;
    // // public bool streamingMipmaps = true;
    // // public int streamingMipmapsPriority = 0;
    // public bool sRGBTexture = true;
    // public TextureFormat textureFormat = TextureFormat.DXT5;
    // public TextureCompressionQuality compressionQuality;
    // public TextureCompressionQuality crunchedCompressionQuality = TextureCompressionQuality.Best;
    // public bool crunchedCompression = false;
    // public TextureImporterType textureType = TextureImporterType.Default;
    // public bool linearTexture = false;
    // public TextureImporterShape textureShape;
    // public TextureWrapMode wrapMode;
    // public FilterMode filterMode;
    // public int anisoLevel = 1;
    // public int maxTextureSize = 2048;
    // public TextureDimension dimension = TextureDimension.Tex2D;

    private void Initialize()
    {
        _greyscaleToAlpha = Find("grayscaleToAlpha");
        _alphaIsTransparency = Find("alphaIsTransparency");
        _mipMapEnabled = Find("mipMapEnabled");
        _npotScale = Find("npotScale");
        _sRGBTexture = Find("sRGBTexture");
        // _textureFormat = Find("textureFormat");
        _compressionQuality = Find("compressionQuality");
        // _crunchedCompressionQuality = Find("crunchedCompressionQuality");
        _crunchedCompression = Find("crunchedCompression");
        _textureType = Find("textureType");
        // _linearTexture = Find("linearTexture");
        // _textureShape = Find("textureShape");
        _wrapMode = Find("wrapMode");
        _filterMode = Find("filterMode");
        _anisoLevel = Find("anisoLevel");
        _maxTextureSize = Find("maxTextureSize");
        // _dimension = Find("dimension");
    }

    public override void OnInspectorGUI()
    {
        Initialize();

        PropertyField(_textureType, new GUIContent("Texture Type"));
        // PropertyField(_linearTexture, new GUIContent("Linear Texture"));
        
        GUILayout.Space(20);
        PropertyField(_greyscaleToAlpha, new GUIContent("Grayscale to Alpha"));
        PropertyField(_alphaIsTransparency, new GUIContent("Alpha is Transparency"));
        PropertyField(_mipMapEnabled, new GUIContent("Enable MipMaps"));
        PropertyField(_npotScale, new GUIContent("NPOT Scale"));
        PropertyField(_sRGBTexture, new GUIContent("sRGB Texture"));
        // PropertyField(_textureFormat, new GUIContent("Texture Format"));


        // PropertyField(_textureShape, new GUIContent("Texture Shape"));
        PropertyField(_wrapMode, new GUIContent("Wrap Mode"));
        PropertyField(_filterMode, new GUIContent("Filter Mode"));
        PropertyField(_anisoLevel, new GUIContent("Aniso Level"));
        PropertyField(_maxTextureSize, new GUIContent("Max Texture Size"));
        // PropertyField(_dimension, new GUIContent("Dimension"));
        
        // [Header("CompressionLevel")]
        GUILayout.Space(20);
        GUILayout.Label("Compression", EditorStyles.boldLabel);
        PropertyField(_compressionQuality, new GUIContent("Compression Quality"));
        // PropertyField(_crunchedCompressionQuality, new GUIContent("Crunched Compression Quality"));
        PropertyField(_crunchedCompression, new GUIContent("Crunch"));
        // HasModified();

        ApplyRevertGUI();
    }


    private SerializedProperty Find(string n) => serializedObject.FindProperty(n);
}

