using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using System.Threading.Tasks;
using Utilities.Encoding.Wav;     // important
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using UnityEngine;

/// <summary>
/// Talking Cube — Speech → Text → GPT → TTS pipeline using Azure OpenAI.
///
/// Call BeginListening() on button-down and StopListening() on button-up.
///
/// Visual feedback:
///   White  = Idle
///   Red    = Recording
///   Yellow = Processing
///   Green  = Speaking
/// </summary>
public class A3TalkingCube : MonoBehaviour
{
    [SerializeField] private string azureResourceName = "ztche-mhz2wvb2-eastus2";
    [SerializeField] private string azureApiKey = "DlEk0NFQWybHC9HiobKtm9zOMsiWXPLWCQFh60oE4GT70rcDTXmrJQQJ99BKACHYHv6XJ3w3AAAAACOG4KTt";
    [SerializeField] private string azureApiVersion = "2025-03-01-preview";

    [Header("Azure — deployments")]
    [SerializeField] private string chatDeployment = "gpt-5-chat";
    [SerializeField] private string sttDeployment = "gpt-4o-mini-transcribe";
    [SerializeField] private string ttsDeployment = "gpt-4o-mini-tts";
    [SerializeField] private string systemPrompt = "You are a friendly talking cube. Keep answers short.";

    [Header("Recording")]
    [SerializeField] private int maxRecordingSeconds = 15;
    [SerializeField] private int sampleRate = 16000;

    [Header("Visual Feedback")]
    [SerializeField] private Color idleColor = Color.white;
    [SerializeField] private Color recordingColor = Color.red;
    [SerializeField] private Color processingColor = Color.yellow;
    [SerializeField] private Color speakingColor = Color.green;

    // ── State ────────────────────────────────────────────────────────────────
    private enum State { Idle, Recording, Processing, Speaking }
    private State currentState = State.Idle;

    // ── One client per deployment ────────────────────────────────────────────
    private OpenAIClient sttClient;
    private OpenAIClient chatClient;
    private OpenAIClient ttsClient;

    // ── Components ───────────────────────────────────────────────────────────
    private AudioSource audioSource;
    private Renderer cubeRenderer;
    private MaterialPropertyBlock propBlock;

    // ── Recording ────────────────────────────────────────────────────────────
    private string micDevice;
    private AudioClip recordingClip;

    // ── Conversation memory ──────────────────────────────────────────────────
    private readonly List<Message> history = new();
    private CancellationTokenSource cts;

    void Start()
    {
        var domain = "cognitiveservices.azure.com";
        var auth = new OpenAIAuthentication(azureApiKey);

        // Clients are already set up for you (do not change unless instructed).
        sttClient = new OpenAIClient(auth, new OpenAISettings(azureResourceName, sttDeployment, azureApiVersion, azureDomain: domain));
        chatClient = new OpenAIClient(auth, new OpenAISettings(azureResourceName, chatDeployment, azureApiVersion, azureDomain: domain));
        ttsClient = new OpenAIClient(auth, new OpenAISettings(azureResourceName, ttsDeployment, azureApiVersion, azureDomain: domain));

        // Unity components
        audioSource = gameObject.AddComponent<AudioSource>();
        cubeRenderer = GetComponent<Renderer>();
        propBlock = new MaterialPropertyBlock();

        if (Microphone.devices.Length > 0)
            micDevice = Microphone.devices[0];
        else
            Debug.LogWarning("[TalkingCube] No microphone found.");

        history.Add(new Message(Role.System, systemPrompt));

        ApplyColor();
    }

    /// <summary>Call this when the user presses the button.</summary>
    [ContextMenu("Begin Listening")]
    public void BeginListening()
    {
        if (micDevice == null || currentState != State.Idle) return;
        Microphone.GetDeviceCaps(micDevice, out int minFreq, out int maxFreq);
        recordingClip = Microphone.Start(micDevice, loop: false, maxRecordingSeconds, sampleRate);

        // Recording state should show red
        SetState(State.Recording);
    }

    /// <summary>Call this when the user releases the button.</summary>
    [ContextMenu("Stop Listening")]
    public void StopListening()
    {
        if (currentState != State.Recording) return;
        if (!Microphone.IsRecording(micDevice)) return;

        int capturedSamples = Microphone.GetPosition(micDevice);
        Microphone.End(micDevice);

        if (capturedSamples < sampleRate / 4)
        {
            Debug.Log("[TalkingCube] Recording too short, ignoring.");
            SetState(State.Idle);
            return;
        }

        // Processing state should show yellow
        SetState(State.Processing);

        cts = new CancellationTokenSource();
        _ = RunPipelineAsync(recordingClip, cts.Token);
    }

    // ── Pipeline (Part B) ─────────────────────────────────────────────────────
    async Task RunPipelineAsync(AudioClip clip, CancellationToken token)
    {
        try
        {
            //Speech → Text
            var wavBytes = clip.EncodeToWav(outputSampleRate: clip.frequency);
            using var wavStream = new MemoryStream(wavBytes);

            var transcriptionRequest = new AudioTranscriptionRequest(wavStream, "recording.wav");
            string userText = await sttClient.AudioEndpoint.CreateTranscriptionTextAsync(transcriptionRequest, token);

            if (string.IsNullOrWhiteSpace(userText))
            {
                Debug.Log("[TalkingCube] No speech detected.");
                SetState(State.Idle);
                return;
            }

            //Text → GPT
            history.Add(new Message(Role.User, userText));

            var chatRequest = new ChatRequest(history, chatDeployment);
            var response = await chatClient.ChatEndpoint.GetCompletionAsync(chatRequest, token);

            string reply = response.FirstChoice.Message.ToString();
            history.Add(new Message(Role.Assistant, reply));

            // Text → Speech
            var speechRequest = new SpeechRequest(reply, ttsDeployment);
            var speechClip = await ttsClient.AudioEndpoint.GetSpeechAsync(speechRequest);

            // Play + state transitions
            SetState(State.Speaking);
            audioSource.PlayOneShot(speechClip.AudioClip);

            int lengthInMs = (int)(speechClip.AudioClip.length * 1000);
            await Task.Delay(lengthInMs + 250, token);

            SetState(State.Idle);
        }
        catch (OperationCanceledException)
        {
            SetState(State.Idle);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TalkingCube] Error: {ex.Message}");
            SetState(State.Idle);
        }
    }

    // ── State + Visual Feedback ──────────────────────────────────────
    void SetState(State newState)
    {
        currentState = newState;
        ApplyColor();
    }

    void ApplyColor()
    {
        if (cubeRenderer == null) return;

        Color color = currentState switch
        {
            State.Recording => recordingColor,
            State.Processing => processingColor,
            State.Speaking => speakingColor,
            _ => idleColor
        };

        cubeRenderer.GetPropertyBlock(propBlock);
        propBlock.SetColor("_BaseColor", color);
        cubeRenderer.SetPropertyBlock(propBlock);
    }


    void OnDestroy()
    {
        cts?.Cancel();
        cts?.Dispose();
    }
}