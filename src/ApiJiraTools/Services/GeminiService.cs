using System.Text;
using System.Text.Json;
using ApiJiraTools.Configuration;
using Microsoft.Extensions.Options;

namespace ApiJiraTools.Services;

public class GeminiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<GeminiService> _logger;

    public GeminiService(IOptions<GeminiSettings> options, ILogger<GeminiService> logger)
    {
        _apiKey = options.Value.ApiKey;
        _model = options.Value.Model;
        _logger = logger;
        _httpClient = new HttpClient();
    }

    public async Task<string> GenerateAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return "Error: Gemini API key no configurada.";

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

        var body = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.3,
                maxOutputTokens = 8192
            }
        };

        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogInformation("Llamando a Gemini ({Model}). Prompt length: {Length}", _model, prompt.Length);

        using var response = await _httpClient.PostAsync(url, content);
        var responseText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Error Gemini. HTTP {StatusCode}: {Response}", (int)response.StatusCode, responseText.Length > 500 ? responseText[..500] : responseText);
            // Extraer mensaje de error de la respuesta JSON si es posible
            var errorMsg = $"HTTP {(int)response.StatusCode}";
            try
            {
                using var errDoc = JsonDocument.Parse(responseText);
                if (errDoc.RootElement.TryGetProperty("error", out var errProp) && errProp.TryGetProperty("message", out var msgProp))
                    errorMsg += $": {msgProp.GetString()}";
            }
            catch { /* ignore parse errors */ }
            return $"Error al llamar a Gemini: {errorMsg}";
        }

        // Extraer texto de la respuesta
        using var doc = JsonDocument.Parse(responseText);
        var candidates = doc.RootElement.GetProperty("candidates");
        if (candidates.GetArrayLength() == 0)
            return "Gemini no devolvió respuesta.";

        var parts = candidates[0].GetProperty("content").GetProperty("parts");
        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var textProp))
                sb.Append(textProp.GetString());
        }

        return sb.ToString();
    }
}
