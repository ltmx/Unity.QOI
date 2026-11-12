![Unity.QOI Logo](https://github.com/LTMX/Unity.QOI/blob/main/.branding/LTMX_Unity_QOI_Github_Banner_Thin.png)

## QOI Image Format Importer & Exporter for Unity
![GitHub repo size](https://img.shields.io/github/repo-size/LTMX/Unity.QOI?style=for-the-badge)
![GitHub package.json version](https://img.shields.io/github/package-json/v/LTMX/Unity.QOI?style=for-the-badge&color=blueviolet)
![GitHub top language](https://img.shields.io/github/languages/top/LTMX/Unity.QOI?style=for-the-badge&color=success)
![GitHub](https://img.shields.io/github/license/LTMX/Unity.QOI?style=for-the-badge)
[![openupm](https://img.shields.io/npm/v/com.ltmx.unity.qoi?style=for-the-badge&label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.ltmx.unity.qoi/)
[![Made for Unity](https://img.shields.io/badge/Made%20for-Unity-57b9d3.svg?style=for-the-badge&logo=unity&color=blueviolet)](https://unity3d.com)

<!--
<br>
<img align="left" src="https://raw.githubusercontent.com/LTMX/Banners-And-Buttons/main/Support%20Me%20Kofi%20Banner%20Shader%20Graph%20Mastery.png" width="140px"/>
<br>
-->

Fast [QOI](https://qoiformat.org) import / export for Unity, on the CPU or the GPU.

## Features
- **Burst compiled, multithreaded CPU codec**: plain and seekable files encode on all cores, runtime and editor
- Bit exact with the reference [qoi.h](https://github.com/phoboslab/qoi)
- **GPU encoder / decoder** (compute shaders), up to 17× faster encodes, decodes straight into a render texture
- **Seekable QOI**: still readable by any QOI decoder, decodes on all cores or on the GPU
- **HDR (half / float) and single channel** extensions, lossless
- Importer: sRGB / linear, mipmaps, max size, NPOT scaling, sprites, **cubemaps**, per platform formats: BC1–BC7, ETC2 / EAC,
  ASTC and ASTC HDR, Crunch, and a multithreaded Burst compressor for BC1 / BC3 / BC4 / BC5 / BC7
- Exporter: any `Texture2D` (non readable and compressed included) to QOI / PNG / JPG

## Installation
Unity **2022.3+**, depends on `com.unity.burst` (installed automatically, preinstalled in recent Unity versions).
In `Window > Package Manager`, click `+ > Add package from git URL...` and paste:
```
https://github.com/LTMX/Unity.QOI.git
```
Or unzip a [release](https://github.com/LTMX/Unity.QOI/releases) into `Packages/com.ltmx.unity.qoi/`.

## Quick start
```csharp
using LTMX.QOI;

byte[] qoi = texture.EncodeToQOI();                   // CPU, any Texture
Texture2D tex = QoiTexture.LoadQOI(qoi);              // CPU decode

byte[] seekable = QoiGpu.Encode(texture);             // GPU encode
RenderTexture rt = QoiGpu.Decode(seekable);           // GPU decode, pixels never touch the CPU
```

<details>
<summary>Full API</summary>

```csharp
// Textures (CPU)
byte[] hdr = texture.EncodeToQOI(sampleFormat: QoiSampleFormat.Half, channels: QoiChannels.Rgb); // force a format
existingTexture2D.LoadQOI(qoi);                        // like ImageConversion.LoadImage

// GPU encode
QoiGpu.EncodeAsync(texture, bytes => ...);             // callback a few frames later (needs the player loop)
using var request = QoiGpu.RequestEncode(texture);     // poll it yourself: editor loops, batch mode
request.WaitForCompletion();                           // or request.Update() until request.Done
request.WriteTo(fileStream);                           // no managed array

// GPU decode (non seekable files fall back to the CPU)
QoiGpu.TryDecodeInto(seekable, existingUNormRT);       // reused render texture: fastest
Texture2D tex2D = QoiGpu.DecodeToTexture2D(seekable);  // slower: Unity allocates a CPU copy
QoiGpu.ReleaseResources();                             // frees the pooled GPU buffers

// Raw codec (no UnityEngine)
QoiImage img = QoiCodec.Decode(bytes, outputChannels: 4, flipY: true);
byte[] file = QoiCodec.Encode(rgba, width, height, 4, QoiChannels.Rgba, seekChunkPixels: QoiCodec.DefaultChunkPixels);
byte[] halfFile = QoiCodec.Encode(halfBits /* ushort[] or float[] */, width, height, 4, QoiChannels.Rgba);
Span<ushort> halfs = QoiCodec.Decode(halfFile).AsHalfs(); // AsFloats() for float files
bool isSeekable = QoiCodec.IsSeekable(bytes);

// Editor
texture2D.SaveToFile(TextureEncodingFormat.QOI);       // native save panel
```
</details>

## Editor
- **Import**: drop `.qoi` files in the project. Cubemaps: set *Texture Shape* to *Cube*. The layout is detected from the
  image ratio (2:1 lat-long, 6:1 / 1:6 strip in +X −X +Y −Y +Z −Z order), or you can pick it.
- **Platforms**: size and compression have per platform overrides (Standalone, Android…), like Unity's texture importer.
  *Automatic* picks BC on desktop and ASTC on mobile (4x4 for high quality and normal maps, 6x6, 8x8 for low quality).
  ASTC HDR is never picked automatically: choose it explicitly when the target GPUs support it.
- **Compressor**: *Fast* swaps Unity's compressor for the package's Burst one (BC1, BC3, BC4, BC5, BC7 of 8-bit images,
  with its own sRGB correct mipmaps). On 2048² textures: BC5 imports 1.9× faster at Unity's quality, BC4 1.2–1.7×
  faster, BC1 / BC3 match Unity's speed and quality. BC7 (modes 0, 1, 3, 4, 5, 6, 8 blocks at once with AVX2): +0.6 to
  +1.1 dB on every texture tested, 1.7× faster on color, 2.1× with alpha, 3.7× on normal maps. Other formats fall back
  to Unity's compressor.
- **Export**: right click a texture > `Export to...`, or `Window > QOI Export Window` (CPU / GPU encoder, sample format, channels).
- **Self-test**: `Tools > QOI > Run Self-Test` checks CPU and GPU byte for byte and benchmarks your machine.

<img width="300" src="https://raw.githubusercontent.com/LTMX/Unity.QOI/main/.documentation/LTMX%20Unity%20Qoi%20Importer%20Editor%20Inspector.png"> <img height="128" src="https://raw.githubusercontent.com/LTMX/Unity.QOI/main/.documentation/QOI%20Context%20Menu.png">

## Performance
RTX 4070 (D3D12), Xeon W-2135 (6c / 12t), Unity 6000.3, Release code optimization, textures from Unity's Megacity sample.
Median of 7 runs, best of two sessions. GPU times include upload / readback.
Green rows: the GPU is faster, red rows: the CPU is faster.

**Encode** (CPU: `EncodeToQOI()`, plain file. GPU: seekable file)

<img src=".documentation/benchmark-encode.svg" alt="Encode timings: the GPU is faster from 1024x1024 up, up to 17x on a 4096x4096 gradient (2.8 vs 48.7 ms); the CPU is faster at 512x512 (1.5 vs 26.8 ms)">

**Decode** (seekable files unless noted, GPU compared with the parallel CPU decode)

<img src=".documentation/benchmark-decode.svg" alt="Decode timings: the parallel CPU decode beats the GPU up to 2048x2048 albedo and on masks and gradients; the GPU is faster on 4096x4096 albedo and HDR textures, ties on normal maps, and is up to 2.8x faster when decoding into a reused render texture">

The codec's loops are Burst compiled: the editor's Debug / Release code optimization doesn't affect them.

## Format

| File | Other QOI decoders | This package |
|---|---|---|
| 8 bit RGB / RGBA | ✅ | ✅ multithreaded encode |
| Seekable | ✅ | ✅ multithreaded encode and decode / GPU |
| Single channel, HDR | ❌ rejected cleanly | ✅ |

<details>
<summary>Seekable QOI</summary>

The image is split into chunks of N pixels (4096 on the CPU, 1024 for GPU encodes). Each chunk starts with a
`QOI_OP_RGBA`, never continues a run and never references a color table entry written before it, so it decodes on its own
while the stream stays standard QOI. A trailer after the end marker lists the chunk offsets (≈0.1–0.4% overhead):

```
header | ops | end marker | u32 offsets[chunkCount] | u32 chunkPixels | u32 chunkCount | "qsek" | end marker
```
</details>

<details>
<summary>HDR and single channel</summary>

- Header channel byte: `channelCount | sampleFormat << 4`, channel count 1, 3 or 4, sample format 0 (8 bit), 1 (half), 2 (float).
- **Single channel**: index and run ops, `0x40 | (d + 32)` for a difference in [-32, 31], `0xFE v` for a literal.
- **HDR**: raw half / float bits (lossless, NaN included) split into byte planes, most significant first, encoded
  like an 8 bit image 2 or 4 times taller. Every op, seekable chunks and GPU decoding work unchanged.
- Formats are picked from the texture (R8 / RHalf / RFloat… → R, half / BC6H → half, float / 16 bit → float).
</details>

<details>
<summary>CPU codec</summary>

- The encode / decode loops are Burst compiled, called directly from C#.
- **Seekable files**: chunks are encoded and decoded on all cores.
- **Plain files, encode**: the image is cut where a pixel differs from the previous one (no run crosses the cut), and the
  encoder's color table at each cut is rebuilt from the last pixel of every hash before it. Segments are encoded on all
  cores and joined: the bytes are exactly those of a single threaded encode.
- **Plain files, decode**: the ops are cut into segments, each one decoded in parallel from a guessed state, then checked
  in order against the true state until both agree. Most files decode about 2× faster; some
  (here normal maps and HDR textures) rarely resynchronize and decode in order at the usual speed.
  Corrupted data always falls back to the single threaded decoder.
</details>

<details>
<summary>GPU codec</summary>

- **Encode**: pass 1 measures every chunk (one thread per chunk), a prefix sum gives the offsets, pass 2 writes every
  chunk at its final offset. Output is byte identical to the CPU seekable encoder.
- **Decode**: one thread per chunk writes into a random write render texture (raw-copied to sRGB when needed).
- GPU buffers are pooled. Non seekable files and machines without compute shaders use the CPU codec.
</details>

## Contribute
- Please post bug reports or (small) feature requests as an [Issue](https://github.com/LTMX/Unity.QOI/issues)
- [Pull Requests](https://github.com/LTMX/Unity.QOI/pulls) are welcome and encouraged!

## Credits
- [QOI Image Format](https://github.com/phoboslab/qoi)
- Earlier versions used [qoi.csharp](https://github.com/kroppt/qoi.csharp) and [QoiSharp](https://github.com/alanmcgovern/QoiSharp), since replaced by a built-in codec
- <a href="https://github.com/Ben1138/unity-qoi">Importer from Ben1138</a>, though modified a lot

## License
This project is licensed under the MIT License ([License](LICENSE))
