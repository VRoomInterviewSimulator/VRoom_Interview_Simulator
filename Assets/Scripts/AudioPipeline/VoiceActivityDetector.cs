using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace VerbalProcess
{
    /// <summary>
    /// 마이크 입력을 캡처하고 RMS 기반으로 발화 상태(Speaking/Silence)를 감지하며 특징점을 추출합니다.
    /// 침묵이 감지될 때마다(Pause) 부분적인 음성 청크를 이벤트로 발생시킵니다.
    /// </summary>
    public class VoiceActivityDetector : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private float threshold = 0.02f;
        [SerializeField] private float defaultSilenceThreshold = 3.0f;
        [SerializeField] private float shortSilenceThreshold = 1.0f;
        private float silenceDurationThreshold = 3.0f;
        [SerializeField] private float chunkSendInterval = 0.3f; // 0.3초마다 청크 전송
        [SerializeField] private int sampleRate = 16000; //whisper는 16khz를 사용함
        [SerializeField] private int bufferLengthSeconds = 300;
        //최대 샘플 수는 sampleRate * bufferLengthSeconds
        [Header("Events")]
        public Action<VoiceFeatures> OnUtteranceEnded; //응답 종료 플래그와 피쳐 값 전달용 이벤트
        public Action<AudioClip> OnAudioChunkCaptured; // 부분 음성 전달용 이벤트
        public Action OnSpeakingStarted;

        [Header("Scoring Tolerances")]
        public float meaningfulPauseThreshold = 0.4f;
        public float lowVolumeRatioThreshold = 0.3f;

        private AudioClip micClip;
        private string micDevice;
        private bool isSpeaking = false;
        private float silenceTimer = 0f;
        private float chunkTimer = 0f;

        private int lastChunkEndSample = 0; // 마지막으로 보낸 청크의 끝 지점
        private float utteranceStartTime;
        private int meaningfulPauseCount = 0;
        private bool wasMeaningfulSilence = false;
        private List<float> rmsSamples = new List<float>();

        private int lastSamplePosition = 0;
        private float[] reusableSampleBuffer; // GC 최적화를 위한 재사용 버퍼

        public struct VoiceFeatures
        {
            public float speakingTime;
            public int meaningfulPauseCount;
            public float volumeVariance;
            public float lowVolumeRatio;
            public float averageVolume;
            public float responseTime;
        }

        void Start()
        {
            InitializeMicrophone();
            // 최대 발생 가능한 샘플 수만큼 버퍼 미리 할당 (예: 0.1초 분량이면 충분)
            reusableSampleBuffer = new float[sampleRate / 10];
            
            // 첫 질문 재생 완료 시점까지 마이크 유입에 따른 레이스 컨디션을 방지하기 위해 초기 비활성화 상태로 기동
            enabled = false;
            Debug.Log("[VAD] Initialized and disabled by default until the first question playback finishes.");
        }

        private void InitializeMicrophone()
        {
            if (Microphone.devices.Length > 0)
            {
                micDevice = Microphone.devices[0];
                micClip = Microphone.Start(micDevice, true, bufferLengthSeconds, sampleRate);

                if (micClip == null)
                {
                    Debug.LogError("Failed to initialize Microphone Clip!");
                    return;
                }
                Debug.Log($"Microphone started: {micDevice}");
            }
            else
            {
                Debug.LogError("No microphone detected!");
            }
        }

        void Update()
        {
            if (micClip == null || !Microphone.IsRecording(micDevice)) return;

            int currentPosition = Microphone.GetPosition(micDevice);
            if (currentPosition < 0 || currentPosition == lastSamplePosition) return;

            ProcessMicSamples(currentPosition);
            lastSamplePosition = currentPosition;
        }

        private void ProcessMicSamples(int currentPosition)
        {
            int sampleCount = (currentPosition - lastSamplePosition + micClip.samples) % micClip.samples;
            if (sampleCount <= 0) return;

            // 실제 오디오 샘플 개수를 기반으로 한 정확한 시간(초) 계산
            float audioDuration = (float)sampleCount / sampleRate;

            // 버퍼 크기 부족 시 재할당 (드문 경우)
            if (reusableSampleBuffer.Length < sampleCount)
            {
                reusableSampleBuffer = new float[sampleCount];
            }

            micClip.GetData(reusableSampleBuffer, lastSamplePosition);

            float sum = 0f;
            for (int i = 0; i < sampleCount; i++)
            {
                sum += reusableSampleBuffer[i] * reusableSampleBuffer[i];
            }
            float rms = Mathf.Sqrt(sum / sampleCount);

            ProcessVAD(rms, audioDuration, currentPosition);
        }

        private void ProcessVAD(float rms, float duration, int currentPosition)
        {
            if (rms > threshold)
            {
                if (!isSpeaking) {
                    StartSpeaking(currentPosition);
                }
                else
                {
                    // 말하는 도중 주기적으로 청크 전송
                    chunkTimer += duration;
                    if (chunkTimer >= chunkSendInterval)
                    {
                        SendChunk(currentPosition);
                        lastChunkEndSample = currentPosition;
                        chunkTimer = 0f;
                    }
                }
                if (silenceTimer > 0f)
                {
                    // 문장 종결 감지 후 침묵 임계값이 짧아진 상태에서 발화가 재개되면
                    // 다음 침묵부터는 기본 임계값을 사용합니다.
                    silenceDurationThreshold = defaultSilenceThreshold;
                    wasMeaningfulSilence = false;
                    silenceTimer = 0f;
                }
                rmsSamples.Add(rms);
            }
            else if (isSpeaking)
            {
                // 침묵이 시작되는 첫 프레임에서도 청크 전송 (남은 부분)
                if (silenceTimer == 0f)
                {
                    SendChunk(currentPosition);
                    lastChunkEndSample = currentPosition;
                    chunkTimer = 0f;
                }

                silenceTimer += duration;
                if (silenceTimer >= meaningfulPauseThreshold && !wasMeaningfulSilence)
                {
                    meaningfulPauseCount++;
                    wasMeaningfulSilence = true;
                }

                if (silenceTimer >= silenceDurationThreshold)
                {
                    EndSpeaking(currentPosition, silenceDurationThreshold);
                }
            }
        }

        private void StartSpeaking(int currentPosition)
        {
            isSpeaking = true;
            utteranceStartTime = Time.time;
            lastChunkEndSample = currentPosition; // 청크 시작 지점 초기화
            silenceDurationThreshold = defaultSilenceThreshold;
            meaningfulPauseCount = 0;
            wasMeaningfulSilence = false;
            rmsSamples.Clear();
            OnSpeakingStarted?.Invoke();
            Debug.Log("Speaking Started");
        }

        /// <summary>
        /// STT의 부분 전사에서 문장 종결이 감지되면 침묵 종료 임계값을 줄입니다.
        /// 최종 전사 결과나 채점 데이터에는 관여하지 않습니다.
        /// </summary>
        public void SetSentenceCompleted()
        {
            if (isSpeaking)
            {
                silenceDurationThreshold = shortSilenceThreshold;
                Debug.Log($"[VAD] Sentence completed detected. Silence threshold reduced to {silenceDurationThreshold}s.");
            }
        }

        private void SendChunk(int currentPosition)
        {
            AudioClip trimmedClip = AudioUtils.TrimAudio(micClip, lastChunkEndSample, currentPosition);
            OnAudioChunkCaptured?.Invoke(trimmedClip);
        }

        private void OnEnable()
        {
            // 다시 활성화될 때, 비활성 기간 동안의 오디오를 무시하기 위해 포인터를 현재 위치로 동기화
            if (micClip != null && Microphone.IsRecording(micDevice))
            {
                lastSamplePosition = Microphone.GetPosition(micDevice);
                lastChunkEndSample = lastSamplePosition;
                silenceTimer = 0f;
                chunkTimer = 0f;
                isSpeaking = false;
                silenceDurationThreshold = defaultSilenceThreshold;
                Debug.Log("[VAD] Re-enabled. Syncing sample position.");
            }
        }

        private void EndSpeaking(int currentPosition, float silenceDuration)
        {
            isSpeaking = false;
            silenceDurationThreshold = defaultSilenceThreshold;
            
            // 침묵 임계값만큼 이전이 실제 발화가 종료된 시점
            int silenceSamples = (int)(silenceDuration * sampleRate);
            int utteranceEndSample = (currentPosition - silenceSamples + micClip.samples) % micClip.samples;

            // 🌟 방어 코드: 반올림 오차 및 프레임 지연으로 인해 utteranceEndSample이 lastChunkEndSample과 너무 가깝거나 
            // 미세하게 역전되어 버퍼 전체(300초)를 한 바퀴 도는 현상을 방지합니다.
            AudioClip trimmedClip = null;
            int sampleDifference = (utteranceEndSample - lastChunkEndSample + micClip.samples) % micClip.samples;
            
            // 0.05초(800샘플) 이상의 유의미한 잔여 데이터가 남았을 때만 마지막 청크 전송
            if (sampleDifference > 800 && sampleDifference < (micClip.samples - 800))
            {
                trimmedClip = AudioUtils.TrimAudio(micClip, lastChunkEndSample, utteranceEndSample);
            }

            float duration = Time.time - utteranceStartTime - silenceDuration;
            float avgRms = rmsSamples.Count > 0 ? rmsSamples.Average() : 0f;

            float volumeVariance = 0f;
            float lowVolumeRatio = 0f;
            if (rmsSamples.Count > 0)
            {
                float sumSq = 0f;
                int lowCount = 0;
                float lowThresh = avgRms * lowVolumeRatioThreshold;
                foreach(var r in rmsSamples) {
                    float diff = r - avgRms;
                    sumSq += diff * diff;
                    if (r < lowThresh) lowCount++;
                }
                volumeVariance = sumSq / rmsSamples.Count;
                lowVolumeRatio = (float)lowCount / rmsSamples.Count;
            }

            VoiceFeatures features = new VoiceFeatures
            {
                speakingTime = Mathf.Max(0, duration),
                meaningfulPauseCount = meaningfulPauseCount,
                volumeVariance = volumeVariance,
                lowVolumeRatio = lowVolumeRatio,
                averageVolume = avgRms,
                responseTime = 0f
            };

            // 유효한 마지막 잔여 조각이 있는 경우 서버 전송 이벤트 발생
            if (trimmedClip != null)
            {
                OnAudioChunkCaptured?.Invoke(trimmedClip);
                Debug.Log($"[VAD] Final tail chunk sent. Length: {trimmedClip.length:F2}s");
            }

            OnUtteranceEnded?.Invoke(features);

            // [로그 설명]
            // tail chunk(마지막 잔여 클립)가 null인 것은 오류가 아닌 정상 동작입니다.
            // 침묵이 시작되는 첫 프레임(ProcessVAD의 silenceTimer == 0f 분기)에서
            // 이미 SendChunk()로 해당 구간을 전송하고 lastChunkEndSample을 앞당겨 두기 때문에,
            // utteranceEndSample과 lastChunkEndSample 사이의 잔여 샘플이 800개(0.05초) 미만이 되어
            // tail clip 생성 조건을 통과하지 못합니다.
            // features 값은 trimmedClip 생성 여부와 무관하게 rmsSamples로 독립적으로 계산됩니다.
            string tailInfo = trimmedClip != null
                ? $"Tail chunk sent: {trimmedClip.length:F2}s"
                : "No tail chunk (already flushed at silence start)";
            Debug.Log($"Speaking Ended. {tailInfo}, Avg Volume: {features.averageVolume:F4}, Speaking Time: {features.speakingTime:F2}s");

            // 발화가 끝나면 다음 입력을 막기 위해 스스로를 비활성화 (Barge-in 미사용 시)
            this.enabled = false;
        }
    }
}
