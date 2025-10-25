using System;
using System.Text;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;

public class GameManager : MonoBehaviour
{
    [Header("UI")]
    public TextMeshProUGUI text;                 // assign in Inspector

    [Header("Ollama (offline, local)")]
    [SerializeField] private string ollamaHost = "http://127.0.0.1:11434";
    [SerializeField] private string modelName = "glados";   // your Ollama model
    [SerializeField] private string defaultSpice = "medium"; // "mild" | "medium" | "hot"
    [SerializeField] private int hotAfterAttempts = 6;

    [Header("Guess Logic (0..100 inclusive)")]
    [SerializeField] private int minValue = 0;
    [SerializeField] private int maxValue = 100;

    // Guessing state
    private int low;          // inclusive
    private int high;         // exclusive (for int Random.Range)
    private int guess;
    private int attempt;
    private bool gameOver;
    private bool ollamaAvailable;

    // --- JSON payloads (Unity JsonUtility-friendly) ---
    [Serializable] private class GenerateRequest { public string model; public string prompt; public bool stream; }
    [Serializable] private class GenerateResponse { public string response; }

    [Serializable] private class IntroPrompt { public string mode = "intro"; public string spice; }
    [Serializable] private class AskPrompt { public string mode = "ask"; public int guess; public int attempt; public string spice; }
    [Serializable] private class WinPrompt { public string mode = "win"; public int guess; public int attempt; public string spice; }
    [Serializable] private class ErrorPrompt { public string mode = "error"; public string spice = "medium"; }

    private void Start()
    {
        // init bounds
        low = minValue;
        high = maxValue + 1;     // exclusive upper makes 0..100 inclusive
        attempt = 0;
        gameOver = false;
        ollamaAvailable = false;

        // preflight Ollama, then intro + first ask
        StartCoroutine(PreflightOllama(
            onOk: () => {
                ollamaAvailable = true;
                StartCoroutine(RequestIntro());
                NewGuess();
            },
            onFail: () => {
                ollamaAvailable = false;
                text.text = "Think of a number from 0 to 100; answer higher or lower.";
                NewGuess(); // code fallback keeps play going
            }
        ));
    }

    // UI buttons hook these:
    public void OnHigher()
    {
        if (gameOver) return;
        low = guess + 1;
        if (low >= high) { StartCoroutine(RequestError("(logic) low >= high")); return; }
        NewGuess();
    }

    public void OnLower()
    {
        if (gameOver) return;
        high = guess;
        if (low >= high) { StartCoroutine(RequestError("(logic) low >= high")); return; }
        NewGuess();
    }

    public void OnCorrect()
    {
        if (gameOver) return;
        gameOver = true;
        StartCoroutine(RequestWin());
    }

    public void OnReplay()
    {
        StopAllCoroutines();
        low = minValue;
        high = maxValue + 1;
        attempt = 0;
        gameOver = false;

        StartCoroutine(PreflightOllama(
            onOk: () => {
                ollamaAvailable = true;
                StartCoroutine(RequestIntro());
                NewGuess();
            },
            onFail: () => {
                ollamaAvailable = false;
                text.text = "Think of a number from 0 to 100; answer higher or lower.";
                NewGuess();
            }
        ));
    }

    // --- internals ---

    private void NewGuess()
    {
        if (gameOver) return;
        guess = UnityEngine.Random.Range(low, high);
        attempt++;
        StartCoroutine(RequestAsk());
    }

    private string SpiceFor(int a) => a >= hotAfterAttempts ? "hot" : defaultSpice;

    // ---------- Ollama calls (UnityWebRequest + JsonUtility) ----------
    private IEnumerator RequestIntro()
    {
        if (!ollamaAvailable) { text.text = "Think of a number from 0 to 100; answer higher or lower."; yield break; }
        var promptObj = new IntroPrompt { spice = defaultSpice };
        yield return PostToOllama(JsonUtility.ToJson(promptObj),
            onOk: line => text.text = line,
            onFail: _ => text.text = "Think of a number from 0 to 100; answer higher or lower.");
    }

    private IEnumerator RequestAsk()
    {
        if (!ollamaAvailable) { text.text = $"I guess {guess}. Higher or lower?"; yield break; }
        var promptObj = new AskPrompt { guess = guess, attempt = attempt, spice = SpiceFor(attempt) };
        yield return PostToOllama(JsonUtility.ToJson(promptObj),
            onOk: line => text.text = line,
            onFail: _ => text.text = $"I guess {guess}. Higher or lower?");
    }

    private IEnumerator RequestWin()
    {
        if (!ollamaAvailable) { text.text = $"It’s {guess}. Solved on try {attempt}."; yield break; }
        var promptObj = new WinPrompt { guess = guess, attempt = attempt, spice = SpiceFor(attempt) };
        yield return PostToOllama(JsonUtility.ToJson(promptObj),
            onOk: line => text.text = line,
            onFail: _ => text.text = $"It’s {guess}. Solved on try {attempt}.");
    }

    private IEnumerator RequestError(string reason = null)
    {
        if (!ollamaAvailable) { text.text = "Signals conflict. Reset the test." + (reason != null ? $" [{reason}]" : ""); yield break; }
        var promptObj = new ErrorPrompt();
        yield return PostToOllama(JsonUtility.ToJson(promptObj),
            onOk: line => text.text = line + (reason != null ? $" [{reason}]" : ""),
            onFail: _ => text.text = "Signals conflict. Reset the test.");
    }

    private IEnumerator PostToOllama(string promptJsonLine, Action<string> onOk, Action<string> onFail)
    {
        var req = new GenerateRequest
        {
            model = modelName,
            prompt = promptJsonLine, // Modelfile expects the JSON line here
            stream = false
        };

        var body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(req));
        using var uwr = new UnityWebRequest($"{ollamaHost.TrimEnd('/')}/api/generate", "POST")
        {
            uploadHandler = new UploadHandlerRaw(body),
            downloadHandler = new DownloadHandlerBuffer()
        };
        uwr.SetRequestHeader("Content-Type", "application/json");

        yield return uwr.SendWebRequest();

        if (uwr.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"Ollama error: {uwr.error}\n{uwr.downloadHandler.text}");
            onFail?.Invoke(uwr.error);
            yield break;
        }

        var json = uwr.downloadHandler.text;
        GenerateResponse resp = null;
        try { resp = JsonUtility.FromJson<GenerateResponse>(json); }
        catch (Exception e) { Debug.LogWarning($"Parse fail: {e}\nBody: {json}"); }

        var line = (resp != null && !string.IsNullOrWhiteSpace(resp.response))
            ? resp.response.Trim()
            : null;

        if (string.IsNullOrEmpty(line)) onFail?.Invoke("empty response");
        else onOk?.Invoke(line);
    }

    private IEnumerator PreflightOllama(Action onOk, Action onFail)
    {
        var probePromptLine = "{\"mode\":\"intro\",\"spice\":\"medium\"}";
        var body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new GenerateRequest
        {
            model = modelName,
            prompt = probePromptLine,
            stream = false
        }));

        using var uwr = new UnityWebRequest($"{ollamaHost.TrimEnd('/')}/api/generate", "POST")
        {
            uploadHandler = new UploadHandlerRaw(body),
            downloadHandler = new DownloadHandlerBuffer()
        };
        uwr.SetRequestHeader("Content-Type", "application/json");

        yield return uwr.SendWebRequest();

        bool ok = false;
        if (uwr.result == UnityWebRequest.Result.Success)
        {
            try
            {
                var resp = JsonUtility.FromJson<GenerateResponse>(uwr.downloadHandler.text);
                ok = resp != null && !string.IsNullOrWhiteSpace(resp.response);
            }
            catch { ok = false; }
        }

        if (ok) onOk?.Invoke();
        else onFail?.Invoke();
    }
}
