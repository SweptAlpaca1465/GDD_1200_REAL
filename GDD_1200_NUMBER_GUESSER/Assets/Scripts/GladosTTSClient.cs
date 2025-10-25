using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

[RequireComponent(typeof(AudioSource))]
public class GladosTTSClient : MonoBehaviour
{
    [Header("Local GLaDOS TTS server")]
    [Tooltip("engine-remote.py default endpoint")]
    public string endpoint = "http://127.0.0.1:8124/synthesize/";

    [Header("Playback")]
    public AudioSource output;       // assign or auto-grab
    [Range(0f, 1f)] public float volume = 1f;
    public bool logLines = false;

    void Reset()
    {
        output = GetComponent<AudioSource>();
        if (output != null) { output.playOnAwake = false; output.loop = false; }
    }

    void Awake()
    {
        if (!output) output = GetComponent<AudioSource>();
        if (output) { output.playOnAwake = false; output.loop = false; }
    }

    /// <summary>Speaks text via local GLaDOS TTS. Returns a coroutine you can yield.</summary>
    public Coroutine Speak(string text, Action onDone = null) =>
        StartCoroutine(SpeakCo(text, onDone));

    private IEnumerator SpeakCo(string text, Action onDone)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            onDone?.Invoke();
            yield break;
        }

        var url = endpoint.TrimEnd('/') + "/?text=" + UnityWebRequest.EscapeURL(text);

        // Server returns WAV audio; fetch directly as an AudioClip.
        using var uwr = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.WAV);
        yield return uwr.SendWebRequest();

        if (uwr.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[GLaDOS TTS] HTTP error: {uwr.responseCode} {uwr.error}");
            onDone?.Invoke();
            yield break;
        }

        var clip = DownloadHandlerAudioClip.GetContent(uwr);
        if (!clip)
        {
            Debug.LogWarning("[GLaDOS TTS] Failed to decode WAV.");
            onDone?.Invoke();
            yield break;
        }

        if (logLines) Debug.Log($"[GLaDOS TTS] {text}");

        output.volume = volume;
        output.clip = clip;
        output.Play();

        // Wait until playback finishes (simple polling).
        while (output.isPlaying) yield return null;

        onDone?.Invoke();
    }

    /// <summary>Fast probe to see if server is reachable.</summary>
    public Coroutine Ping(Action<bool> result) => StartCoroutine(PingCo(result));
    private IEnumerator PingCo(Action<bool> result)
    {
        var url = endpoint.TrimEnd('/') + "/?text=" + UnityWebRequest.EscapeURL("ping");
        using var uwr = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.WAV);
        uwr.timeout = 5;
        yield return uwr.SendWebRequest();
        result?.Invoke(uwr.result == UnityWebRequest.Result.Success);
    }
}
