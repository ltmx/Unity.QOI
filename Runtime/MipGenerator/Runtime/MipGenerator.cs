using System;
using UnityEngine;
using static UnityEngine.RenderTexture;

public static class MipGenerator
{
    //NOTE: This requires the blit shader to be inside a Resources folder, or you'll get build errors.
    //If you hate Resources you can construct this material yourself, make everything here static, and just pass it into BlitToMip().
    private static Material blitMaterial = new(Shader.Find("MipGenerator/Blit"));
    private static readonly int mainTex = Shader.PropertyToID("_MainTex");

    public static void GenerateCustomMips(this RenderTexture target, Material mipMaterial)
    {
        int size = target.width;
        int numMips = (int) (Math.Log(target.width) / Math.Log(2)) + 1;
        var mipTextures = new RenderTexture[numMips];
        for (int i = 0; i < numMips; i++)
        {
            int mipRes = (int) (size / Math.Pow(2, i));

            RenderTexture rt = GetTemporary(target.descriptor);
            rt.width = mipRes;
            rt.height = mipRes;
            rt.depth = 0;
            rt.filterMode = FilterMode.Point;
            rt.useMipMap = false;

            mipTextures[i] = rt;
        }
        
        Graphics.Blit(target, mipTextures[0]);
        for (int i = 1; i < numMips; i++)
        {
            Graphics.Blit(mipTextures[i - 1], mipTextures[i], mipMaterial);
            BlitToMip(mipTextures[i], target, i);
        }

        for (int i = 0; i < numMips; i++)
        {
            ReleaseTemporary(mipTextures[i]);
        }
    }
    
    public static void BlitToMip(RenderTexture source, RenderTexture target, int mipLevel=0)
    {
        var previousTarget = active;
        
        GL.PushMatrix();
        Graphics.SetRenderTarget(target, mipLevel);
        blitMaterial.SetTexture(mainTex, source);
        blitMaterial.SetPass(0);
        
        GL.LoadOrtho();
        GL.Begin(GL.QUADS);
        GL.TexCoord2(0, 0); GL.Vertex3(0, 0, 0); //BOTTOM LEFT
        GL.TexCoord2(0, 1); GL.Vertex3(0, 1, 0); //TOP LEFT
        GL.TexCoord2(1, 1); GL.Vertex3(1, 1, 0); //TOP RIGHT
        GL.TexCoord2(1, 0); GL.Vertex3(1, 0, 0); //BOTTOM RIGHT

        GL.End();
        
        GL.PopMatrix();
        
        active = previousTarget;
    }
}
