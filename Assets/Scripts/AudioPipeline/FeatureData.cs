using System;

namespace VerbalProcess
{
    /// <summary>
    /// VAD에서 추출된 음성 특징(Feature) 데이터를 담는 DTO
    /// </summary>
    [Serializable]
    public class FeatureData
    {
        public float speakingTime;
        public int meaningfulPauseCount;
        public float volumeVariance;
        public float lowVolumeRatio;
        public float averageVolume;
        public float responseTime;

        public FeatureData(VoiceActivityDetector.VoiceFeatures features)
        {
            this.speakingTime = features.speakingTime;
            this.meaningfulPauseCount = features.meaningfulPauseCount;
            this.volumeVariance = features.volumeVariance;
            this.lowVolumeRatio = features.lowVolumeRatio;
            this.averageVolume = features.averageVolume;
            this.responseTime = features.responseTime;
        }
    }
}
