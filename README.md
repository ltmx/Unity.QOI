<p><br><br>
    <img align="left" width="380px" src="https://raw.githubusercontent.com/LTMX/Unity.QOI/main/.documentation/Unity.QOI%20Logo.png" alt="Unity.QOI">
</p>
 
## QOI Importer & Exporter for Unity
![GitHub repo size](https://img.shields.io/github/repo-size/LTMX/Unity.QOI)
![GitHub package.json version](https://img.shields.io/github/package-json/v/LTMX/Unity.QOI?color=blueviolet)
![GitHub top language](https://img.shields.io/github/languages/top/LTMX/Unity.QOI?color=success)
![GitHub](https://img.shields.io/github/license/LTMX/Unity.QOI)

<br>
<img align="left" src="https://raw.githubusercontent.com/LTMX/Banners-And-Buttons/main/Support%20Me%20Kofi%20Banner%20Shader%20Graph%20Mastery.png" width="140px"/>
<br>

## Features

- Supports RGB & RGBA Formats
```csharp
anyTexture2D.EncodeToQOI() // Returns a byte array of the encoded Texture2D
anyTexture2D.SaveToFile(TextureEncodingFormat.QOI) // Opens a native window to save your file
```

<table>
    <tr>
      <td>
        <a href="selftitled.html">
          <div class="imgWrap floatleft">
            <h3>Right Click Action</h3>
            <img src=".documentation/QOI%20Context%20Menu.png" height="128">
          </div>
        </a>
      </td>
      <td>
        <a href="12x5.html">
          <div class="imgWrap floatleft">
            <h3>Output Folder</h3>
            <img src=".documentation/Qoi%20Output%20Folder.png" height="128">
          </div>
        </a>
      </td>
    </tr>
  </table>


### Importer Inspector Parameters
Most settings have been mapped from how unity imports other image formats, to ensure behaviour compatibility

<img width="300" src=".documentation/LTMX%20Unity%20Qoi%20Importer%20Editor%20Inspector.png">


<!--
### Editor Window

<img height="170" src=".documentation/Qoi%20Editor%20Window.png">
<img height="170" src=".documentation/Find%20Qoi%20Editor%20Window.png">
-->
    
## Installation
### UNITY PACKAGE MANAGER (recommended)
1. Copy this URL: ``https://github.com/radiatoryang/hedera.git``
2. In Unity menu bar, go to ``Window > Package Manager`` [Info / Help for Unity Package Manager](https://docs.unity3d.com/Manual/Packages.html)
3. In Unity Package Manager, click ``(+ button) > Add package from Git URL...`` (info / troubleshooting Git URL packages in Unity)
4. Paste URL from step 1 into the URL box and click ``Add``
5. Unity.QOI will now automatically update from GitHub
### Zip Package From Releases
- Or manually download the latest .ZIP from [Releases](https://https://github.com/LTMX/Unity.QOI/releases)
    and unzip to `/<your project folder>/Packages/com.ltmx.unity.qoi/`


## Contribute
- please post bug reports or (small) feature requests as an [Issue](https://github.com/LTMX/Unity.QOI/issues)
- [Pull Requests](https://github.com/LTMX/Unity.QOI/pulls) are welcome and encouraged !

## Credits
- <a href="https://github.com/phoboslab/qoi">QOI Image Format</a>
- <a href="https://github.com/alanmcgovern/QoiSharp/tree/perf-optimisations">QOISharp Encoder</a>
- <a href="https://github.com/Ben1138/unity-qoi">Asset Importer from Ben1138</a>

## LICENSE
<p>This project is licensed under the MIT License (<a href="LICENSE">License</a>)</p>
