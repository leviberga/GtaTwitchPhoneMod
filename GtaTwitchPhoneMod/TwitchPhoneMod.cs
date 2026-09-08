using Fleck;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using NAudio.Wave;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;

public class TwitchPhoneMod : Script
{
    // ------------------------------------------------------------
    // CONFIG
    // ------------------------------------------------------------
    private const string AnimDict = "cellphone@";
    private const string AnimName = "cellphone_text_to_call";

    // eScriptedAnimFlags: AF_HOLD_LAST_FRAME(2) + AF_UPPERBODY(16) + AF_SECONDARY(32)
    // NOTE: we intentionally do NOT use AF_LOOPING(1) here — this anim is a one-shot
    // "raise phone to ear" transition, not an idle loop. Looping it makes the arm
    // repeatedly rise and fall. HOLD_LAST_FRAME freezes the pose once it finishes.
    private const int AnimFlags = 2 + 16 + 32;

    private const int AnimDictLoadTimeoutMs = 5000; // safety timeout while waiting for the dict

    private WebSocketServer server;
    private IWebSocketConnection clientSocket;

    // Messages arrive on Fleck's own thread. We never touch game state or call
    // natives directly from OnMessage — we just enqueue, and drain the queue
    // from OnTick (the game's script thread). This removes the race condition
    // that was very likely behind the end-of-call crash.
    private readonly ConcurrentQueue<string> incomingMessages = new ConcurrentQueue<string>();

    private enum CallState
    {
        Idle,             // nothing happening
        Ringing,          // phone is ringing, waiting for first tick to start the ring sound
        Talking,          // ringing, waiting for the player to press ENTER / BACKSPACE
        LoadingAnimation, // ENTER pressed — attaching phone + waiting for anim dict to load
        InCall,           // animation triggered, voice audio playing, subtitles shown
        Ending            // audio finished — short grace period before touching NAudio objects
    }

    private CallState currentState = CallState.Idle;

    private string callerName = "";
    private string subtitleText = "";
    private string audioPath = @"C:\Program Files\Epic Games\GTAVEnhanced\scripts\twitch_audio.wav";

    private int soundId = -1;
    private int phoneObjectEntity = 0;

    private int animLoadStartTime = 0; // Game.GameTime snapshot, used for the load timeout

    private AudioFileReader audioFile;
    private WaveOutEvent outputDevice;

    // Set only from NAudio's own PlaybackStopped callback (see OnPlaybackStopped).
    // OnTick just reads this flag — it never touches outputDevice/audioFile directly
    // to decide when the call ended, avoiding a race with NAudio's internal thread.
    private volatile bool audioPlaybackEnded = false;

    // Grace period between "audio finished" and actually disposing the NAudio
    // objects. NAudio's own author warns against calling back into the driver
    // (Stop/Dispose) from inside PlaybackStopped itself, since depending on the
    // backend that thread may still be unwinding — so we wait a beat and do the
    // disposal from the main game thread instead.
    private const int AudioCleanupGraceMs = 250;
    private int audioEndedAtGameTime = 0;

    public TwitchPhoneMod()
    {
        Tick += OnTick;

        Aborted += (s, e) =>
        {
            CleanUpAudio();
            DeletePhoneObject();
            try { server?.Dispose(); } catch { /* ignore on shutdown */ }
        };

        Task.Run(() =>
        {
            try
            {
                server = new WebSocketServer("ws://127.0.0.1:8080");

                server.Start(socket =>
                {
                    socket.OnOpen = () => clientSocket = socket;
                    // Only enqueue here — never mutate state or call natives from this thread.
                    socket.OnMessage = message => incomingMessages.Enqueue(message);
                    socket.OnClose = () => clientSocket = null;
                });
            }
            catch (Exception)
            {
            }
        });
    }

    private void OnTick(object sender, EventArgs e)
    {
        try
        {
            // Drain any messages that arrived from Java since the last tick.
            // This always runs on the main script thread, so it's safe to
            // touch currentState / call natives here.
            while (incomingMessages.TryDequeue(out string message))
            {
                HandleJavaMessage(message);
            }

            Ped playerPed = Game.Player.Character;

            if (currentState != CallState.Idle)
            {
                RenderPremiumPhoneHUD();
            }

            switch (currentState)
            {
                case CallState.Ringing:
                    if (soundId == -1)
                    {
                        soundId = Function.Call<int>(Hash.GET_SOUND_ID);

                        Function.Call(
                            Hash.PLAY_SOUND_FRONTEND,
                            soundId,
                            "Remote_Ring",
                            "Phone_SoundSet_Default",
                            true
                        );
                    }

                    currentState = CallState.Talking;
                    break;

                case CallState.Talking:
                    if (Game.IsControlJustPressed(Control.FrontendAccept))
                    {
                        StopRingtone();

                        // Attach the physical 3D phone to the hand (unchanged — already working).
                        CreateAndAttachPhone(playerPed);

                        Function.Call(Hash.REQUEST_ANIM_DICT, AnimDict);

                        animLoadStartTime = Game.GameTime;
                        currentState = CallState.LoadingAnimation;
                    }
                    else if (Game.IsControlJustPressed(Control.FrontendCancel))
                    {
                        StopCallComplete();
                    }
                    break;

                case CallState.LoadingAnimation:
                    // Wait for the dict to actually finish loading before playing the
                    // anim. This is the fix for "the animation sometimes doesn't play at all".
                    if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, AnimDict))
                    {
                        Function.Call(
                            Hash.TASK_PLAY_ANIM,
                            playerPed,
                            AnimDict,
                            AnimName,
                            8.0f,     // blend-in speed
                            -8.0f,    // blend-out speed
                            -1,       // duration: play the full clip once
                            AnimFlags,
                            0.0f,     // playback rate
                            false,
                            false,
                            false
                        );

                        // Only now — with the animation actually queued — do we start audio.
                        StartVoicePlayback();
                        currentState = CallState.InCall;
                    }
                    else if (Game.GameTime - animLoadStartTime > AnimDictLoadTimeoutMs)
                    {
                        // Safety net: if the dict never loads (disk hiccup, bad name, etc.)
                        // don't hang forever in this state — bail out cleanly.
                        Notification.Show("~r~Falha ao carregar animação do telefone.");
                        StopCallComplete();
                    }
                    break;

                case CallState.InCall:
                    if (audioPlaybackEnded)
                    {
                        audioEndedAtGameTime = Game.GameTime;
                        currentState = CallState.Ending;
                    }
                    else
                    {
                        GTA.UI.Screen.ShowSubtitle($"~b~{callerName}: ~w~{subtitleText}", 200);
                    }
                    break;

                case CallState.Ending:
                    // Give NAudio's internal thread a moment to fully unwind before
                    // we touch outputDevice/audioFile at all.
                    if (Game.GameTime - audioEndedAtGameTime >= AudioCleanupGraceMs)
                    {
                        StopCallComplete();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            // A single bad frame should never be able to crash the whole game.
            // Log it, reset to a clean state, and keep going.
            Notification.Show($"~r~TwitchPhoneMod erro: {ex.Message}");
            StopCallComplete();
        }
    }

    private void HandleJavaMessage(string message)
    {
        if (message.StartsWith("TOCAR_LIGACAO|") && currentState == CallState.Idle)
        {
            string[] parts = message.Split('|');

            if (parts.Length > 2)
            {
                callerName = parts[1];
                subtitleText = parts[2];

                currentState = CallState.Ringing;
            }
        }
    }

    private void CreateAndAttachPhone(Ped ped)
    {
        DeletePhoneObject();

        uint phoneModelHash = (uint)Game.GenerateHash("p_amb_phone_01");

        Function.Call(Hash.REQUEST_MODEL, phoneModelHash);

        while (!Function.Call<bool>(Hash.HAS_MODEL_LOADED, phoneModelHash))
        {
            Script.Wait(10);
        }

        Vector3 pPos = ped.Position;

        phoneObjectEntity = Function.Call<int>(
            Hash.CREATE_OBJECT,
            (int)phoneModelHash,
            pPos.X, pPos.Y, pPos.Z,
            true, true, false
        );

        // Bone ID 28422 = PH_R_Hand (right hand palm bone).
        int boneIndex = Function.Call<int>(Hash.GET_PED_BONE_INDEX, ped, 28422);

        Function.Call(
            Hash.ATTACH_ENTITY_TO_ENTITY,
            phoneObjectEntity,
            ped.Handle,
            boneIndex,
            0.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 0.0f,
            true, true, false, false, 2, true
        );

        Function.Call(Hash.SET_MODEL_AS_NO_LONGER_NEEDED, phoneModelHash);
    }

    private void DeletePhoneObject()
    {
        if (phoneObjectEntity != 0 && Function.Call<bool>(Hash.DOES_ENTITY_EXIST, phoneObjectEntity))
        {
            // Deliberately using the raw native here, NOT GTA.Entity.FromHandle().
            // FromHandle() goes through SHVDN's NativeMemory pattern-scanning layer
            // (GetEntityAddress), which is what actually crashed on this GTA5
            // Enhanced build (see notes below). The plain native call below is the
            // same call path CreateAndAttachPhone/ATTACH_ENTITY_TO_ENTITY already use
            // successfully, so it doesn't touch that fragile subsystem at all.
            Function.Call(Hash.DELETE_OBJECT, phoneObjectEntity);
            phoneObjectEntity = 0;
        }
    }

    private void StopCallComplete()
    {
        StopRingtone();
        CleanUpAudio();
        DeletePhoneObject();

        Ped playerPed = Game.Player.Character;

        // Clears whichever task slot the anim is in (works whether it ended up
        // primary or secondary) without needing the exact clip to still be "active".
        Function.Call(Hash.STOP_ANIM_TASK, playerPed, AnimDict, AnimName, 3.0f);
        Function.Call(Hash.CLEAR_PED_SECONDARY_TASK, playerPed);

        soundId = -1;
        audioPlaybackEnded = false;
        currentState = CallState.Idle;
    }

    private void RenderPremiumPhoneHUD()
    {
        float screenWidth = GTA.UI.Screen.Width;
        float screenHeight = GTA.UI.Screen.Height;

        float phoneWidth = screenWidth * 0.125f;
        float phoneHeight = screenHeight * 0.52f;

        float phoneX = screenWidth * 0.85f;
        float phoneY = screenHeight - phoneHeight - 40f;

        float displayWidth = phoneWidth * 0.90f;
        float displayHeight = phoneHeight * 0.72f;

        float displayX = phoneX + ((phoneWidth - displayWidth) / 2f);
        float displayY = phoneY + (phoneHeight * 0.10f);

        float cornerRadius = 12f;

        System.Drawing.Color chassiColor = System.Drawing.Color.FromArgb(255, 12, 12, 12);

        new ContainerElement(new PointF(phoneX + cornerRadius, phoneY), new SizeF(phoneWidth - (cornerRadius * 2f), phoneHeight), chassiColor).Draw();
        new ContainerElement(new PointF(phoneX, phoneY + cornerRadius), new SizeF(phoneWidth, phoneHeight - (cornerRadius * 2f)), chassiColor).Draw();
        new ContainerElement(new PointF(phoneX, phoneY), new SizeF(cornerRadius * 2f, cornerRadius * 2f), chassiColor).Draw();
        new ContainerElement(new PointF(phoneX + phoneWidth - (cornerRadius * 2f), phoneY), new SizeF(cornerRadius * 2f, cornerRadius * 2f), chassiColor).Draw();
        new ContainerElement(new PointF(phoneX, phoneY + phoneHeight - (cornerRadius * 2f)), new SizeF(cornerRadius * 2f, cornerRadius * 2f), chassiColor).Draw();
        new ContainerElement(new PointF(phoneX + phoneWidth - (cornerRadius * 2f), phoneY + phoneHeight - (cornerRadius * 2f)), new SizeF(cornerRadius * 2f, cornerRadius * 2f), chassiColor).Draw();

        float speakerW = phoneWidth * 0.28f;
        float speakerX = phoneX + ((phoneWidth - speakerW) / 2f);

        new ContainerElement(new PointF(speakerX, phoneY + (phoneHeight * 0.04f)), new SizeF(speakerW, 4f), Color.FromArgb(255, 45, 45, 45)).Draw();
        new ContainerElement(new PointF(speakerX - 12f, phoneY + (phoneHeight * 0.038f)), new SizeF(5f, 5f), Color.FromArgb(255, 10, 25, 40)).Draw();

        float btnHomeSize = phoneWidth * 0.20f;
        float btnHomeX = phoneX + ((phoneWidth - btnHomeSize) / 2f);
        float spaceRemaining = phoneHeight - (displayY - phoneY) - displayHeight;
        float btnHomeY = (displayY + displayHeight) + ((spaceRemaining - btnHomeSize) / 2f);

        new ContainerElement(new PointF(btnHomeX, btnHomeY), new SizeF(btnHomeSize, btnHomeSize), Color.FromArgb(255, 32, 32, 32)).Draw();
        new ContainerElement(new PointF(btnHomeX + 2f, btnHomeY + 2f), new SizeF(btnHomeSize - 4f, btnHomeSize - 4f), chassiColor).Draw();

        new ContainerElement(new System.Drawing.PointF(displayX, displayY), new System.Drawing.SizeF(displayWidth, displayHeight), System.Drawing.Color.FromArgb(255, 20, 20, 20)).Draw();
        new ContainerElement(new System.Drawing.PointF(displayX, displayY), new System.Drawing.SizeF(displayWidth, 20f), System.Drawing.Color.FromArgb(255, 12, 12, 12)).Draw();

        new TextElement("TWITCH NETWORK", new PointF(displayX + 6f, displayY + 3f), 0.16f, System.Drawing.Color.Gray, GTA.UI.Font.ChaletLondon, Alignment.Left).Draw();
        new TextElement("CONTATO:", new PointF(displayX + 10f, displayY + 28f), 0.20f, System.Drawing.Color.FromArgb(255, 0, 140, 255), GTA.UI.Font.ChaletLondon, Alignment.Left).Draw();

        float nameScale = (callerName.Length > 10) ? 0.32f : 0.38f;

        new TextElement(callerName.ToUpper(), new PointF(displayX + 10f, displayY + 44f), nameScale, System.Drawing.Color.White, GTA.UI.Font.ChaletLondon, Alignment.Left).Draw();

        // "Chamada Recebida..." while waiting for the player to answer (Talking),
        // "Em linha ativa" once the call is actually connected (InCall).
        string statusTexto = (currentState == CallState.InCall) ? "• Em linha ativa" : "Chamada Recebida...";

        System.Drawing.Color statusColor = (currentState == CallState.InCall)
            ? System.Drawing.Color.FromArgb(255, 35, 175, 35)
            : System.Drawing.Color.FromArgb(255, 240, 190, 0);

        new TextElement(statusTexto, new PointF(displayX + 10f, displayY + 80f), 0.24f, statusColor, GTA.UI.Font.ChaletLondon, Alignment.Left).Draw();

        if (currentState == CallState.Talking)
        {
            float btnY1 = displayY + displayHeight - 70f;

            new ContainerElement(new System.Drawing.PointF(displayX + 8f, btnY1), new System.Drawing.SizeF(displayWidth - 16f, 26f), System.Drawing.Color.FromArgb(255, 35, 135, 35)).Draw();
            new TextElement("[ENTER] ATENDER", new PointF(displayX + 14f, btnY1 + 4f), 0.21f, System.Drawing.Color.White, GTA.UI.Font.ChaletLondon, Alignment.Left).Draw();

            float btnY2 = displayY + displayHeight - 38f;

            new ContainerElement(new System.Drawing.PointF(displayX + 8f, btnY2), new System.Drawing.SizeF(displayWidth - 16f, 26f), System.Drawing.Color.FromArgb(255, 155, 35, 35)).Draw();
            new TextElement("[BACKSPACE] RECUSAR", new PointF(displayX + 11f, btnY2 + 4f), 0.21f, System.Drawing.Color.White, GTA.UI.Font.ChaletLondon, Alignment.Left).Draw();
        }
        else if (currentState == CallState.LoadingAnimation)
        {
            new TextElement("Conectando...", new PointF(displayX + 10f, displayY + 110f), 0.21f, System.Drawing.Color.DarkGray, GTA.UI.Font.ChaletLondon, Alignment.Left).Draw();
        }
    }

    private void StartVoicePlayback()
    {
        try
        {
            if (File.Exists(audioPath))
            {
                CleanUpAudio();
                audioPlaybackEnded = false;

                audioFile = new AudioFileReader(audioPath);
                outputDevice = new WaveOutEvent();
                outputDevice.PlaybackStopped += OnPlaybackStopped;

                outputDevice.Init(audioFile);
                outputDevice.Play();
            }
            else
            {
                StopCallComplete();
            }
        }
        catch (Exception ex)
        {
            Notification.Show($"~r~Erro de Áudio: {ex.Message}");
            StopCallComplete();
        }
    }

    // Fired by NAudio itself once the buffer is fully drained — either because the
    // clip ended naturally or Stop() was called. NAudio's author explicitly warns
    // against calling back into the driver (Stop/Dispose) from inside this handler,
    // since depending on the backend the playback thread may not be fully done
    // unwinding yet, which can deadlock or crash the host process. So this handler
    // does nothing but flip a plain bool — actual cleanup happens later, on the main
    // thread, in StopCallComplete (via the Ending state's grace period).
    private void OnPlaybackStopped(object sender, StoppedEventArgs e)
    {
        audioPlaybackEnded = true;
    }

    private void StopRingtone()
    {
        if (soundId >= 0)
        {
            Function.Call(Hash.STOP_SOUND, soundId);
            Function.Call(Hash.RELEASE_SOUND_ID, soundId);
            soundId = -1;
        }
    }

    private void CleanUpAudio()
    {
        try
        {
            if (outputDevice != null)
            {
                outputDevice.PlaybackStopped -= OnPlaybackStopped;
                // No need to call Stop() here: this either runs from inside the
                // PlaybackStopped handler itself (already stopped) or from a
                // manual cancel — WaveOutEvent.Dispose() stops playback on its own.
                outputDevice.Dispose();
                outputDevice = null;
            }

            if (audioFile != null)
            {
                audioFile.Dispose();
                audioFile = null;
            }
        }
        catch (Exception ex)
        {
            Notification.Show($"~r~Erro ao limpar áudio: {ex.Message}");
            outputDevice = null;
            audioFile = null;
        }
    }
}