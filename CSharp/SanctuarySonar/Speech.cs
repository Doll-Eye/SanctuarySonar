// Speech: NVDA through its controller client when the DLL is beside the executable and NVDA
// is running; otherwise SAPI through its own SpVoice COM object. Either way one short line at
// a time. The DLL is NV Access's nvda_<version>_controllerClient.zip, x64\nvdaControllerClient.dll,
// LGPL; it is copied beside the exe as nvdaControllerClient64.dll (the name imported here).
//
// Not System.Speech: on 27 Sep 2026 its SpeechSynthesizer constructor crashed the process
// (access violation reading null inside System.Speech → SAPI) on a machine with
// NaturalVoiceSAPIAdapter installed — the adapter many NVDA users have for natural voices.
// The crash cannot be caught from managed code and left a one-line log. SpVoice is what NVDA's
// own sapi5 driver drives, and it works on the same machine; each step here is logged before
// it runs so a failure in the speech layer always leaves a trace.
using System.Runtime.InteropServices;

namespace SanctuarySonar.Shell;

public sealed class Speech : ISpeech
{
    readonly bool nvda;
    readonly dynamic? sapi;
    const int SVSFlagsAsync = 1;

    [DllImport("nvdaControllerClient64.dll", CharSet = CharSet.Unicode)] static extern int nvdaController_testIfRunning();
    [DllImport("nvdaControllerClient64.dll", CharSet = CharSet.Unicode)] static extern int nvdaController_speakText(string text);
    [DllImport("nvdaControllerClient64.dll", CharSet = CharSet.Unicode)] static extern int nvdaController_cancelSpeech();

    public Speech()
    {
        Log.log("Speech: looking for NVDA");
        try { nvda = nvdaController_testIfRunning() == 0; }
        catch (DllNotFoundException) { Log.log("Speech: no nvdaControllerClient64.dll beside the exe"); nvda = false; }
        catch (Exception e) { Log.log($"NVDA controller: {e.Message}"); nvda = false; }
        if (!nvda)
        {
            Log.log("Speech: opening SAPI (SpVoice)");
            try
            {
                var type = Type.GetTypeFromProgID("SAPI.SpVoice", throwOnError: false);
                if (type == null) Log.log("Speech: SAPI is not registered");
                else sapi = Activator.CreateInstance(type);
            }
            catch (Exception e) { Log.log($"SAPI unavailable: {e.Message}"); }
        }
        string voice = "";
        if (sapi != null)
        {
            try { voice = $" ({sapi.Voice.GetDescription()})"; } catch (Exception e) { voice = $" (voice name unavailable: {e.Message})"; }
        }
        Log.log(nvda ? "Speech: NVDA" : sapi != null ? $"Speech: SAPI{voice}; put nvdaControllerClient64.dll beside the exe for NVDA" : "Speech: none");
    }

    public bool screenReaderRunning => nvda || sapi != null;

    public void say(string text)
    {
        try
        {
            if (nvda) nvdaController_speakText(text);
            else sapi?.Speak(text, SVSFlagsAsync);
        }
        catch (Exception e) { Log.log($"Speech failed: {e.Message}"); }
    }
}
