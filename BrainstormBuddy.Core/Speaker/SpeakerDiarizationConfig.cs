namespace BrainstormBuddy.Core.Speaker;

/// <summary>
/// Секция конфига "SpeakerDiarization". Диаризация опциональна (opt-in),
/// отказ/отсутствие модели никогда не блокирует STT-пайплайн.
/// Единственный движок — Nemotron-3 Sortformer (GGUF через nemo_speech_asr);
/// поле Engine из старых конфигов при десериализации просто игнорируется.
/// </summary>
public class SpeakerDiarizationConfig
{
    public bool Enabled { get; set; } = true;               // 2.18.0: из коробки; opt-out в настройках
    // Бэкенд nemo-speech для Nemotron: "auto" (cuda → vulkan → cpu) | "cpu" | "vulkan" | "cuda".
    public string NemotronBackend { get; set; } = "auto";
    // Путь к GGUF Nemotron-3; null = каскад: %APPDATA%\models → exe\models → artifacts\speaker.
    public string? NemotronModelPath { get; set; }
    // Пресет nemo-speech ("v3-streaming" | "v3-offline" | "streaming" | "offline"); null/"" = дефолт dll.
    public string? NemotronPreset { get; set; } = "v3-streaming";
}
