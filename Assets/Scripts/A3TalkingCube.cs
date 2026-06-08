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
    [SerializeField] private string azureResourceName = "csci5629-group7-resource";
    [SerializeField] private string azureApiKey = "ARYJdWqRyecxUE9YypoD7NVcsEkm8gNIr449zh347r4dnsaWuItrJQQJ99CDACHYHv6XJ3w3AAAAACOGHhB0";
    [SerializeField] private string azureApiVersion = "2025-03-01-preview";

    [Header("Azure — deployments")]
    [SerializeField] private string chatDeployment = "gpt-5";
    [SerializeField] private string sttDeployment = "gpt-4o-mini-transcribe";
    [SerializeField] private string ttsDeployment = "gpt-4o-mini-tts";
    [SerializeField] private string systemPrompt = "You are a friendly talking cube with eyes. " +
        "You can see what the user sees through their headset camera. " +
        "When the user asks about something in their view, describe what you see. Keep answers short.";

    [Header("Recording")]
    [SerializeField] private int maxRecordingSeconds = 15;
    [SerializeField] private int sampleRate = 16000;

    [Header("Visual Feedback")]
    [SerializeField] private Color idleColor = Color.white;
    [SerializeField] private Color recordingColor = Color.red;
    [SerializeField] private Color processingColor = Color.yellow;
    [SerializeField] private Color speakingColor = Color.green;

    [Header("Camera")]
    [SerializeField] private A4CameraFrameProvider cameraFrameProvider;
    [SerializeField] private SceneNavigator sceneNavigator;
    private readonly List<Tool> assistantTools = new();



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

        var domain = "openai.azure.com";
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

        if (sceneNavigator != null)
        {
            assistantTools.Add(Tool.FromFunc<string, string>(
                "move_to_target",
                sceneNavigator.MoveToTarget,
                "Move the cube to the exact target id returned by get_scene_candidates."));

            assistantTools.Add(Tool.FromFunc<string, string, string>(
                "build_voxel_object",
                sceneNavigator.BuildVoxelObject,
                "Move to the exact target id and build a voxel object there. blocksJson is optional; if omitted, build one cube."));

            systemPrompt +=
                "\nIf the user asks you to move toward or go to an object, use the provided scene candidates JSON." +
                "\nChoose one exact candidate id from that list and call move_to_target with that id." +
                "\nDo not invent target ids or object metadata that the tool did not return." +
                "\nIf the user asks to build, create, place, or stack a block object on a target, call build_voxel_object." +
                "\nFor a simple request, omit blocksJson so it builds one cube." +
                "\nIf the user clearly wants a larger voxel structure, include a small blocksJson layout." +
                "\nDo not call move_to_target before build_voxel_object.";
        }

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

            string sceneCandidatesJson = sceneNavigator != null
                ? sceneNavigator.GetSceneCandidates()
                : string.Empty;

            var userPrompt = string.IsNullOrWhiteSpace(sceneCandidatesJson)
                ? userText
                : $"{userText}\n\nScene candidates JSON:\n{sceneCandidatesJson}";

            Texture2D snapshot = null;
            if (cameraFrameProvider != null)
            {
                snapshot = await cameraFrameProvider.CaptureFrameAsync();
            }

            //Text → GPT
            // Always store text-only in history to avoid sending images repeatedly
            history.Add(new Message(Role.User, userPrompt));

            // Build the request messages: use history as-is for all past turns,
            // but replace the last message with a multimodal version if we have a snapshot
            var requestMessages = new List<Message>(history);
            if (snapshot != null)
            {
                requestMessages.RemoveAt(requestMessages.Count - 1);
                requestMessages.Add(new Message(Role.User, new List<Content>
                {
                    new Content(snapshot),
                    new Content(userText)
                }));
            }

            string reply = await GetAssistantReplyAsync(requestMessages, token);
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
    private async Task<string> GetAssistantReplyAsync(List<Message> conversation, CancellationToken token)
    {
        while (true)
        {
            var chatRequest = assistantTools != null && assistantTools.Count > 0
                ? new ChatRequest(conversation, assistantTools, toolChoice: "auto", model: chatDeployment)
                : new ChatRequest(conversation, model: chatDeployment);

            var response = await chatClient.ChatEndpoint.GetCompletionAsync(chatRequest, token);
            var assistantMessage = response.FirstChoice.Message;

            if (response.FirstChoice.FinishReason != "tool_calls")
            {
                return assistantMessage.Content?.ToString() ?? string.Empty;
            }

            conversation.Add(assistantMessage);

            foreach (var toolCall in assistantMessage.ToolCalls)
            {
                string result;
                try
                {
                    result = await toolCall.InvokeFunctionAsync<string>(token);
                }
                catch (Exception ex)
                {
                    result = $"Error: {ex.Message}";
                }

                conversation.Add(new Message(toolCall, result));
            }
        }
    }
}