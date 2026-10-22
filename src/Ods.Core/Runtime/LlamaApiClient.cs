using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Ods.Core.Runtime;

public sealed record LlamaProps(int ContextSize, string ModelPath, string BuildInfo, int TotalSlots);

/// <summary>The small set of llama-server endpoints ODS uses to prove a model is ready.</summary>
public sealed class LlamaApiClient(HttpClient http)
{
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>GET /health: 200 when ready, 503 while the model loads, 0 when nothing answers.</summary>
    public async Task<HttpStatusCode> HealthAsync(Uri baseUri, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(Request(HttpMethod.Get, baseUri, "/health", apiKey), cancellationToken);
            return response.StatusCode;
        }
        catch (HttpRequestException)
        {
            return 0;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    public async Task<string?> ModelIdAsync(Uri baseUri, string apiKey, CancellationToken cancellationToken)
    {
        var body = await GetJsonAsync(baseUri, "/v1/models", apiKey, cancellationToken);
        return (string?)body["data"]?.AsArray().FirstOrDefault()?["id"];
    }

    public async Task<LlamaProps> PropsAsync(Uri baseUri, string apiKey, CancellationToken cancellationToken)
    {
        var body = await GetJsonAsync(baseUri, "/props", apiKey, cancellationToken);
        return new LlamaProps(
            (int?)body["default_generation_settings"]?["n_ctx"] ?? 0,
            (string?)body["model_path"] ?? "",
            (string?)body["build_info"] ?? "",
            (int?)body["total_slots"] ?? 0);
    }

    /// <summary>A short, deterministic chat completion (the generation proof).</summary>
    public async Task<string> CompleteAsync(Uri baseUri, string apiKey, string model, string prompt, int maxTokens, CancellationToken cancellationToken)
    {
        var request = Request(HttpMethod.Post, baseUri, "/v1/chat/completions", apiKey);
        request.Content = JsonContent.Create(new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0,
            ["stream"] = false,
        });
        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"llama-server answered HTTP {(int)response.StatusCode}: {text}");
        var body = JsonNode.Parse(text)!;
        return (string?)body["choices"]?[0]?["message"]?["content"] ?? "";
    }

    private async Task<JsonNode> GetJsonAsync(Uri baseUri, string path, string apiKey, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Get, baseUri, path, apiKey), cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"llama-server {path} answered HTTP {(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text) ?? throw new HttpRequestException($"llama-server {path} returned no JSON.");
    }

    private static HttpRequestMessage Request(HttpMethod method, Uri baseUri, string path, string apiKey)
    {
        var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }
}
