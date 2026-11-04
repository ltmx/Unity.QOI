using UnityEditor;
using UnityEngine;
using static LTMX.QOI.Editor.ImageSaveUtility;

namespace LTMX.QOI.Editor
{
    public class QOIEditorWindow : EditorWindow
    {
        private const string EncoderPref = "LTMX.QOI.ExportEncoder";
        private const string SamplesPref = "LTMX.QOI.ExportSamples";
        private const string ChannelsPref = "LTMX.QOI.ExportChannels";

        private enum SampleOption { Auto, Byte, Half, Float }
        private enum ChannelOption { Auto, R, Rgb, Rgba }

        private Texture2D tex;
        private QoiEncoder encoder;
        private SampleOption samples;
        private ChannelOption channels;
        private string lastExport;

        [MenuItem("Window/QOI Export Window")]
        static void Init()
        {
            // Get existing open window or if none, make a new one:
            var window = GetWindow<QOIEditorWindow>();
            window.Show();
        }

        void OnEnable()
        {
            encoder = (QoiEncoder)EditorPrefs.GetInt(EncoderPref, (int)QoiEncoder.Cpu);
            samples = (SampleOption)EditorPrefs.GetInt(SamplesPref, 0);
            channels = (ChannelOption)EditorPrefs.GetInt(ChannelsPref, 0);
        }

        void OnGUI()
        {
            GUILayout.BeginVertical();
            tex = TextureField("Texture", tex);
            GUILayout.Space(10);

            EditorGUI.BeginChangeCheck();
            encoder = (QoiEncoder)EditorGUILayout.EnumPopup(new GUIContent("Encoder",
                "CPU: plain QOI file.\nGPU: compute shader, seekable QOI file (readable by any QOI decoder, decodes in parallel)."), encoder);
            samples = (SampleOption)EditorGUILayout.EnumPopup(new GUIContent("Samples",
                "Auto: half / float for HDR textures, bytes otherwise.\nHalf and Float are HDR extensions of QOI, not readable by other QOI decoders."), samples);
            channels = (ChannelOption)EditorGUILayout.EnumPopup(new GUIContent("Channels",
                "Auto: R for single channel textures, RGBA when some pixels are transparent, RGB otherwise.\nR is an extension of QOI, not readable by other QOI decoders."), channels);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetInt(EncoderPref, (int)encoder);
                EditorPrefs.SetInt(SamplesPref, (int)samples);
                EditorPrefs.SetInt(ChannelsPref, (int)channels);
            }
            if (encoder == QoiEncoder.Gpu && !QoiGpu.IsSupported)
                EditorGUILayout.HelpBox("Compute shaders are not supported here, the CPU encoder will be used.", MessageType.Warning);

            GUILayout.Space(10);
            if (GUILayout.Button("Export To QOI") && tex != null)
            {
                QoiSampleFormat? sampleFormat = samples == SampleOption.Auto ? null : (QoiSampleFormat)(samples - 1);
                QoiChannels? channelCount = channels switch
                {
                    ChannelOption.R => QoiChannels.R,
                    ChannelOption.Rgb => QoiChannels.Rgb,
                    ChannelOption.Rgba => QoiChannels.Rgba,
                    _ => null
                };
                lastExport = tex.SaveToFile(TextureEncodingFormat.QOI, encoder, sampleFormat, channelCount) ?? lastExport;
                // The save panel interrupts the GUI layout
                GUIUtility.ExitGUI();
            }

            if (!string.IsNullOrEmpty(lastExport))
                EditorGUILayout.HelpBox(lastExport, MessageType.Info);
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
