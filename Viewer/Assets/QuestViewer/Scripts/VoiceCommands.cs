using System;
using System.Collections.Concurrent;
using UnityEngine;

/// Offline voice commands for Marvin:
///   "Marvin, send this to the Quest" / "Marvin, could you send the model"
///        asks the SolidWorks add-in to send the open document (the model stays where it is)
///   "Marvin, hologram / style (solid or X-ray) / explode / rotate / cut / fixed / inspect / reset"
///        press that keypad button
///   "Thanks Marvin"  Marvin answers with droid beeps
/// Standalone Quest: Vosk with a small English model (StreamingAssets/vosk-model), limited to these phrases.
/// Editor / PC: Windows' built-in speech recognition. Audio never leaves the device.
public class VoiceCommands : MonoBehaviour
{
    public ModelServer server;
    public Keypad keypad;
    public ModelHost host;
    [Tooltip("Ignore the same command again within this many seconds.")]
    public float cooldown = 2f;
    [Tooltip("After \"send\", wait this long before recentring a model that's already there.")]
    public float recenterDelay = 2f;

    const string Send = "send", Thanks = "thanks", Unlock = "unlock", Closer = "closer", Farther = "farther", FreeAll = "free all", Tablet = "tablet";

    /// Everything the recognizers listen for, and what it means: "send", "thanks", or a keypad button label.
    static readonly (string phrase, string command)[] Phrases =
    {
        ("marvin send this to the quest", Send),
        ("marvin send this to quest", Send),
        ("marvin send it to the quest", Send),
        ("marvin send to the quest", Send),
        ("marvin send to quest", Send),
        ("marvin could you send the model", Send),
        ("marvin send the model", Send),
        ("marvin hologram", "HOLO"),
        ("marvin style", "STYLE"),
        ("marvin x ray", "STYLE"),
        ("marvin explode", "EXPLODE"),
        ("marvin rotate", "ROTATE"),
        ("marvin cut", "CUT"),
        ("marvin section cut", "CUT"),
        ("marvin fixed", "FIXED"),
        ("marvin fixed scale", "FIXED"),
        ("marvin fix scale", "FIXED"),
        ("marvin inspect", "INSPECT"),
        ("marvin lock", "LOCK"),
        ("marvin unlock", Unlock),
        ("marvin lock the model", "LOCK"),
        ("marvin fix location", "LOCK"),
        ("marvin box", "BOX"),
        ("marvin bounding box", "BOX"),
        ("marvin grab box", "BOX"),
        ("marvin isolate", "ISOLATE"),
        ("marvin lock part", "LOCK PART"),
        ("marvin lock the part", "LOCK PART"),
        ("marvin unlock part", "LOCK PART"),
        ("marvin move", "MOVE"),
        ("marvin move parts", "MOVE"),
        ("marvin fix part", "FIX PART"),
        ("marvin fix parts", "FIX PART"),
        ("marvin free all", FreeAll),
        ("marvin unlock all", FreeAll),
        ("marvin free everything", FreeAll),
        ("marvin release all", FreeAll),
        ("marvin tablet", Tablet),
        ("marvin keypad", Tablet),
        ("marvin bring the tablet", Tablet),
        ("marvin where is the tablet", Tablet),
        ("marvin settings", "SETTINGS"),
        ("marvin options", "SETTINGS"),
        ("marvin controls", "CONTROLS"),
        ("marvin help", "CONTROLS"),
        ("marvin show controls", "CONTROLS"),
        ("marvin closer", Closer),
        ("marvin farther", Farther),
        ("marvin further", Farther),
        ("marvin further away", Farther),
        ("marvin reset", "RESET"),
        ("marvin recenter", "RESET"),
        ("thanks marvin", Thanks),
        ("thank you marvin", Thanks),
        ("marvin thanks", Thanks),
        ("marvin thank you", Thanks),
    };

    /// Words the recogniser often hears for "marvin" (SolidWorks fork): treated as his name.
    static readonly string[] NameSoundsLike = { "marvel", "martin" };

    /// Everything the recogniser listens for: each phrase, the same with a sound-alike name, and the command alone
    /// ("explode"), which only counts right after his name (so you can try again without repeating it).
    static string[] AllPhrases()
    {
        var list = new System.Collections.Generic.List<string>();
        foreach (var p in Phrases)
        {
            list.Add(p.phrase);
            foreach (var alias in NameSoundsLike) list.Add(p.phrase.Replace("marvin", alias));
            var bare = Bare(p.phrase);
            if (bare != null && !list.Contains(bare)) list.Add(bare);
        }
        list.Add("marvin");
        foreach (var alias in NameSoundsLike) list.Add(alias);
        return list.ToArray();
    }

    /// "marvin explode" -> "explode"; null for phrases with the name elsewhere ("thanks marvin").
    static string Bare(string phrase) => phrase.StartsWith("marvin ", StringComparison.Ordinal) ? phrase.Substring(7) : null;

    static string Normalize(string text)
    {
        foreach (var alias in NameSoundsLike)
        {
            if (text == alias) return "marvin";
            if (text.StartsWith(alias + " ", StringComparison.Ordinal)) return "marvin" + text.Substring(alias.Length);
            if (text.EndsWith(" " + alias, StringComparison.Ordinal)) return text.Substring(0, text.Length - alias.Length) + "marvin";
        }
        return text;
    }

    readonly ConcurrentQueue<string> heard = new ConcurrentQueue<string>();
    readonly ConcurrentQueue<string> problems = new ConcurrentQueue<string>();
    string lastCommand;
    float lastTime = -10f;
    IDisposable recognizer;
    VoiceFeedback feedback;

    void Start()
    {
        feedback = GetComponent<VoiceFeedback>();
        if (feedback == null) feedback = gameObject.AddComponent<VoiceFeedback>();
        var phrases = AllPhrases();
#if UNITY_ANDROID && !UNITY_EDITOR
        recognizer = new VoskRecognizer(phrases, heard.Enqueue, problems.Enqueue);
#elif UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        recognizer = WindowsRecognizer.Create(phrases, heard.Enqueue, problems.Enqueue);
#else
        Debug.Log("[Voice] No speech recognizer on this platform.");
#endif
    }

    void OnDestroy()
    {
        recognizer?.Dispose();
        recognizer = null;
    }

    void OnApplicationPause(bool paused)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        (recognizer as VoskRecognizer)?.Pause(paused);
#endif
    }

    void Update()
    {
        while (problems.TryDequeue(out var p))
        {
            Debug.LogWarning("[Voice] " + p);
            Status.Error("Voice commands off\n" + p);
        }
        while (heard.TryDequeue(out var text)) Handle(text);
    }

    /// 'raw' is a recogniser result: "!" in front for a finished utterance, else a partial one (still speaking).
    void Handle(string raw)
    {
        bool final = raw.StartsWith("!", StringComparison.Ordinal);
        string text = Normalize(final ? raw.Substring(1) : raw);
        bool listening = feedback != null && feedback.IsListening;
        string command = null;
        foreach (var p in Phrases)
            if (p.phrase == text || (listening && Bare(p.phrase) == text)) { command = p.command; text = p.phrase; break; }
        if (command == null)
        {
            bool named = text.StartsWith("marvin", StringComparison.Ordinal) || text.EndsWith("marvin", StringComparison.Ordinal);
            if (feedback == null) return;
            if (named) feedback.OnName();
            else if (listening) feedback.OnSpeech();
            // Done speaking and it wasn't a command: say so right away, and keep listening for another try.
            // (His name alone just waits for the command.)
            if (final && (named || listening) && text != "marvin" && text.Length > 0) feedback.OnMissed();
            return;
        }
        if (command == lastCommand && Time.unscaledTime - lastTime < cooldown) return;
        lastCommand = command;
        lastTime = Time.unscaledTime;
        Debug.Log($"[Voice] Heard \"{text}\" -> {command}");
        if (feedback != null) feedback.OnCommand(text.Replace("marvin", "").Trim());

        // Marvin answers every command with his beeps.
        switch (command)
        {
            case Send:
                if (server != null && server.RequestSend())
                {
                    Say(DroidVoice.Answer(DroidVoice.Reaction.OnIt));
                    Status.Busy("Asking SolidWorks to send…");
                    // The model stays where you left it (say "Marvin, reset" to bring it back).
                }
                else
                {
                    feedback?.OnFailed();
                    Say(DroidVoice.Answer(DroidVoice.Reaction.Sad));
                    Status.Error("SolidWorks isn't connected\nOpen SolidWorks with the Quest add-in");
                }
                break;
            case Unlock: // always unlocks (LOCK itself toggles)
                if (keypad != null && keypad.IsOn("LOCK")) keypad.Press("LOCK");
                Say(DroidVoice.Answer(DroidVoice.Reaction.Off));
                break;
            case Thanks:
                var (clip, mood) = DroidVoice.Reply();
                Say(clip);
                Status.Ok(mood, 2.5f);
                break;
            case FreeAll: // every part held still in MOVE mode can move again
                if (keypad != null) keypad.FreeAllHeld();
                Say(DroidVoice.Answer(DroidVoice.Reaction.Okay));
                break;
            case Tablet: // the keypad back where it first appears (RESET leaves it alone)
                var pad = keypad != null ? keypad.GetComponent<SpawnInFront>() : null;
                if (pad != null) pad.Respawn();
                Say(DroidVoice.Answer(DroidVoice.Reaction.Okay));
                break;
            case Closer: // spawn distance, half a foot at a time
            case Farther:
                if (keypad != null) keypad.StepSpawn(command == Closer ? -1f : 1f);
                Say(DroidVoice.Answer(DroidVoice.Reaction.Okay));
                break;
            default:
                if (keypad == null || !keypad.Press(command))
                {
                    Debug.LogWarning("[Voice] No keypad button " + command);
                    feedback?.OnFailed();
                    Say(DroidVoice.Answer(DroidVoice.Reaction.Sad));
                    break;
                }
                var reaction = command == "RESET" ? DroidVoice.Reaction.Okay
                    : keypad.IsOn(command) ? DroidVoice.Reaction.On : DroidVoice.Reaction.Off;
                Say(DroidVoice.Answer(reaction));
                break;
        }
    }

    System.Collections.IEnumerator RecenterLater()
    {
        yield return new WaitForSeconds(recenterDelay);
        if (keypad != null) keypad.Recenter();
    }

    void Say(AudioClip clip)
    {
        if (keypad != null) keypad.Say(clip);
        Destroy(clip, clip.length + 1f);
    }
}

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
/// Windows speech recognition, limited to the phrases. Uses the default microphone (over Link that can be
/// the headset's, if Windows has it as the default recording device).
sealed class WindowsRecognizer : IDisposable
{
    UnityEngine.Windows.Speech.KeywordRecognizer keywords;

    public static WindowsRecognizer Create(string[] phrases, Action<string> onText, Action<string> onProblem)
    {
        try
        {
            var r = new WindowsRecognizer { keywords = new UnityEngine.Windows.Speech.KeywordRecognizer(phrases) };
            r.keywords.OnPhraseRecognized += e => onText(e.text);
            r.keywords.Start();
            Debug.Log("[Voice] Windows speech recognition listening.");
            return r;
        }
        catch (Exception e)
        {
            onProblem("Windows speech recognition unavailable: " + e.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (keywords == null) return;
        if (keywords.IsRunning) keywords.Stop();
        keywords.Dispose();
        keywords = null;
    }
}
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
/// Vosk (org.vosk, Apache 2.0) through its Android library. The model is copied from the APK's assets
/// to app storage on first start (StorageService.unpack), then a SpeechService listens on the microphone.
sealed class VoskRecognizer : IDisposable
{
    const float SampleRate = 16000f;
    readonly string grammar;
    readonly Action<string> onText;
    readonly Action<string> onProblem;
    AndroidJavaObject service;
    bool disposed;

    public VoskRecognizer(string[] phrases, Action<string> onText, Action<string> onProblem)
    {
        this.onText = onText;
        this.onProblem = onProblem;
        var quoted = Array.ConvertAll(phrases, p => "\"" + p + "\"");
        grammar = "[" + string.Join(",", quoted) + ",\"[unk]\"]";

        const string mic = UnityEngine.Android.Permission.Microphone;
        if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(mic)) Unpack();
        else
        {
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => Unpack();
            callbacks.PermissionDenied += _ => onProblem("Microphone permission denied");
            UnityEngine.Android.Permission.RequestUserPermission(mic, callbacks);
        }
    }

    void Unpack()
    {
        if (disposed) return;
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var storage = new AndroidJavaClass("org.vosk.android.StorageService"))
            {
                var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                storage.CallStatic("unpack", activity, "vosk-model", "vosk",
                    new Callback(Listen), new Callback(e => onProblem("Model: " + Message(e))));
            }
        }
        catch (Exception e) { onProblem(e.Message); }
    }

    void Listen(AndroidJavaObject model)
    {
        if (disposed) return;
        try
        {
            var recognizer = new AndroidJavaObject("org.vosk.Recognizer", model, SampleRate, grammar);
            service = new AndroidJavaObject("org.vosk.android.SpeechService", recognizer, SampleRate);
            service.Call<bool>("startListening", new Listener(this));
            Debug.Log("[Voice] Vosk listening.");
        }
        catch (Exception e) { onProblem(e.Message); }
    }

    public void Pause(bool paused)
    {
        try { service?.Call("setPause", paused); } catch (Exception) { }
    }

    public void Dispose()
    {
        disposed = true;
        try
        {
            service?.Call<bool>("stop");
            service?.Call("shutdown");
        }
        catch (Exception) { }
        service = null;
    }

    static string Message(AndroidJavaObject e)
    {
        try { return e?.Call<string>("getMessage") ?? "unknown error"; } catch (Exception) { return "unknown error"; }
    }

    /// Vosk results are JSON like {"text": "marvin explode"} or {"partial": "marvin"}.
    void Result(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return;
        string marker = "\"" + key + "\"";
        int k = json.IndexOf(marker, StringComparison.Ordinal);
        if (k < 0) return;
        int start = json.IndexOf('"', json.IndexOf(':', k + marker.Length) + 1);
        int end = start < 0 ? -1 : json.IndexOf('"', start + 1);
        if (end <= start) return;
        var text = json.Substring(start + 1, end - start - 1).Trim();
        if (text.Length > 0) onText(key == "text" ? "!" + text : text);
    }

    sealed class Callback : AndroidJavaProxy
    {
        readonly Action<AndroidJavaObject> action;
        public Callback(Action<AndroidJavaObject> action) : base("org.vosk.android.StorageService$Callback") { this.action = action; }
        void onComplete(AndroidJavaObject result) => action(result);
    }

    sealed class Listener : AndroidJavaProxy
    {
        readonly VoskRecognizer owner;
        public Listener(VoskRecognizer owner) : base("org.vosk.android.RecognitionListener") { this.owner = owner; }
        // Partial results fire as soon as the whole phrase is recognized; the final one repeats it (cooldown).
        void onPartialResult(string hypothesis) => owner.Result(hypothesis, "partial");
        void onResult(string hypothesis) => owner.Result(hypothesis, "text");
        void onFinalResult(string hypothesis) => owner.Result(hypothesis, "text");
        void onError(AndroidJavaObject exception) => owner.onProblem(Message(exception));
        void onTimeout() { }
    }
}
#endif
