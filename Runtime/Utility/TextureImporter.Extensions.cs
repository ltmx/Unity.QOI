using System;
using System.Runtime.InteropServices;
using Mono.Cecil;
using UnityEditor;
using UnityEngine;
using Unity.Mathematics;
using static Unity.Mathematics.math;
using Unity.Burst;

public static class TextureImporterExtensions
{
    public static TextureImporter SetReadWrite(this TextureImporter t, bool state) {
        t.isReadable = state;
        return t;
    }

    public static TextureImporter SetCrunchCompression(this TextureImporter t, bool state) {
        t.crunchedCompression = state;
        return t;
    }

    public static TextureImporter SetAlphaIsTransparency(this TextureImporter t, bool state) {
        t.alphaIsTransparency = state;
        return t;
    }

    public static TextureImporter SetAsNormalMap(this TextureImporter t) {
        t.textureType = TextureImporterType.NormalMap;
        return t;
    }

    public static TextureImporter SetType(this TextureImporter t, TextureImporterType type) {
        t.textureType = type;
        return t;
    }

    public static Texture2D SetReadWriteAndRefresh(this Texture2D texture, bool state) {
        texture?.GetImporter()?.SetReadWrite(state).SaveAndReimport();
        return texture;
    }

    public static Texture2D SetReadWrite(this Texture2D texture, bool state) {
        texture?.GetImporter()?.SetReadWrite(state);
        return texture;
    }

    public static Texture2D SetCrunchCompression(this Texture2D texture, bool state) {
        texture?.GetImporter()?.SetCrunchCompression(state).SaveAndReimport();
        return texture;
    }

    public static Texture2D SetAlphaIsTransparency(this Texture2D texture, bool state) {
        texture?.GetImporter()?.SetAlphaIsTransparency(state).SaveAndReimport();
        return texture;
    }

    public static Texture2D SetAsNormalMap(this Texture2D texture) {
        texture?.GetImporter()?.SetType(TextureImporterType.NormalMap).SaveAndReimport();
        return texture;
    }

    public static void SetImporterType(this Texture2D texture, TextureImporterType type) => texture?.GetImporter()?.SetType(type).SaveAndReimport();
    public static TextureImporter GetImporter(this Texture2D t) => AssetImporter.GetAtPath(t.GetPath()) as TextureImporter;
    
}