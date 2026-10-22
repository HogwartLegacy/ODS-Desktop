using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ods.Core.Chat;

public sealed record ChatMessage(string Role, string Content);

/// <summary>llama-server's per-response timings (tokens and milliseconds).</summary>
public sealed record ChatTimings(int PromptTokens, double PromptMs, int PredictedTokens, double PredictedMs)
{
    public double TokensPerSecond => PredictedMs > 0 ? PredictedTokens / (PredictedMs / 1000.0) : 0;
}

/// <summary>One streamed piece of a reply.</summary>
public sealed record ChatDelta(string? Content, string? Reasoning, string? FinishReason, ChatTimings? Timings);

public sealed record ChatRequestOptions(double Temperature = 0.7, int? MaxTokens = null);

/// <summary>The model server answered with an error the user should see.</summary>
public sealed class ChatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Streams OpenAI-compatible chat completions from the local llama-server.</summary>
public sealed class ChatClient(HttpClient http)
{
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        Uri baseUri,
        string apiKey,
        string model,
        IReadOnlyList<ChatMessage> messages,
        ChatRequestOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["temperature"] = options.Temperature,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
        };
        if (options.MaxTokens is int maxTokens) body["max_tokens"] = maxTokens;

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/v1/chat/completions"))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException error)
        {
            throw new ChatException("ODS could not reach the model server. Check that it is running on the Home page.", error);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ChatException(DescribeError((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken)));
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (var data in SseReader.ReadEventsAsync(stream, cancellationToken))
            {
                if (data == "[DONE]") yield break;
                var delta = ParseChunk(data);
                if (delta is not null) yield return delta;
            }
        }
    }

    /// <summary>Parse one streamed chat.completion.chunk.</summary>
    public static ChatDelta? ParseChunk(string data)
    {
        JsonNode? chunk;
        try
        {
            chunk = JsonNode.Parse(data);
        }
        catch (JsonException parseError)
        {
            throw new ChatException($"The model server sent an unreadable stream chunk: {data}", parseError);
        }
        if (chunk?["error"] is JsonNode serverError)
        {
            throw new ChatException((string?)serverError["message"] ?? serverError.ToJsonString());
        }
        var choice = chunk?["choices"]?.AsArray().FirstOrDefault();
        var deltaNode = choice?["delta"];
        var timings = chunk?["timings"] is JsonObject t
            ? new ChatTimings((int?)t["prompt_n"] ?? 0, (double?)t["prompt_ms"] ?? 0, (int?)t["predicted_n"] ?? 0, (double?)t["predicted_ms"] ?? 0)
            : null;
        var content = (string?)deltaNode?["content"];
        var reasoning = (string?)deltaNode?["reasoning_content"];
        var finish = (string?)choice?["finish_reason"];
        if (content is null && reasoning is null && finish is null && timings is null) return null;
        return new ChatDelta(content, reasoning, finish, timings);
    }

    private static string DescribeError(int status, string body)
    {
        string? message = null;
        try
        {
            message = (string?)JsonNode.Parse(body)?["error"]?["message"];
        }
        catch (JsonException)
        {
            // Not JSON; the raw body below is shown instead.
        }
        message ??= body.Length > 300 ? body[..300] : body;
        if (message.Contains("context", StringComparison.OrdinalIgnoreCase) && message.Contains("exceed", StringComparison.OrdinalIgnoreCase))
        {
            return "This conversation is longer than the model's context window. Start a new chat, or choose a larger context on the Models page.";
        }
        return status == 503
            ? "The model is still loading. Try again in a moment."
            : $"The model server answered HTTP {status}: {message}";
    }
}
