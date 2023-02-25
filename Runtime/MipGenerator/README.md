# Unity Mip Generator
## Generate custom mips for RenderTextures

Basic utility to allow you to supply a custom shader for mip generation in Unity. Not a common need but if you got here from a google search you probably have a good use for it.

## Usage

Check StandardMip.shader for an example of how to set up your shader (here demonstrating the typical 4-texel box average). Each mip level is generated via the same shader; at each step _MainTex will contain the contents of the preceding mip level.
To use, just call `MipGenerator.GenerateCustomMips` with a rendertexture and a material that wraps your shader.

It might do weird/bad stuff with non-power-of-two or rectangular textures.
