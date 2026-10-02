using System.Text.Json.Serialization;

namespace DevPilot.Domain.Enums;

/// <summary>Wire protocol used to talk to a model endpoint.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiAdapterType
{
    /// <summary>OpenAI chat-completions protocol: OpenAI, Kimi, DeepSeek, Qwen, Mistral, OpenRouter, Ollama, LM Studio, vLLM...</summary>
    OpenAiCompatible = 0,

    /// <summary>Anthropic Messages API (Claude).</summary>
    Claude = 1,

    /// <summary>Google Gemini API.</summary>
    Gemini = 2,
}
