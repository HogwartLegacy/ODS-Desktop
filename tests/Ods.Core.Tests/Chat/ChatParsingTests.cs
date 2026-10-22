using System.Text;
using Ods.Core.Chat;

namespace Ods.Core.Tests.Chat;

public sealed class ChatParsingTests
{
    private static async Task<List<string>> Events(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var events = new List<string>();
        await foreach (var data in SseReader.ReadEventsAsync(stream, TestContext.Current.CancellationToken)) events.Add(data);
        return events;
    }

    [Fact]
    public async Task Server_sent_events_are_split_on_blank_lines()
    {
        var events = await Events(": keep-alive\n\ndata: {\"a\":1}\n\ndata: line one\ndata: line two\n\ndata: [DONE]\n\n");

        Assert.Equal(["{\"a\":1}", "line one\nline two", "[DONE]"], events);
    }

    [Fact]
    public void Content_reasoning_and_timings_are_read_from_chunks()
    {
        var content = ChatClient.ParseChunk("{\"choices\":[{\"delta\":{\"content\":\"Hel\"},\"finish_reason\":null}]}");
        Assert.Equal("Hel", content!.Content);

        var reasoning = ChatClient.ParseChunk("{\"choices\":[{\"delta\":{\"reasoning_content\":\"thinking\"}}]}");
        Assert.Equal("thinking", reasoning!.Reasoning);

        var last = ChatClient.ParseChunk("{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"timings\":{\"prompt_n\":12,\"prompt_ms\":80.5,\"predicted_n\":64,\"predicted_ms\":2000}}");
        Assert.Equal("stop", last!.FinishReason);
        Assert.Equal(32.0, last.Timings!.TokensPerSecond);

        Assert.Null(ChatClient.ParseChunk("{\"choices\":[{\"delta\":{}}]}"));
    }

    [Fact]
    public void Server_errors_in_the_stream_surface_as_chat_errors()
    {
        var error = Assert.Throws<ChatException>(() => ChatClient.ParseChunk("{\"error\":{\"message\":\"the request exceeds the available context size\"}}"));

        Assert.Contains("context", error.Message);
    }

    [Fact]
    public void Conversation_titles_come_from_the_first_line()
    {
        Assert.Equal("Explain GGUF", ConversationStore.TitleFrom("Explain GGUF\nin detail please"));
        Assert.True(ConversationStore.TitleFrom(new string('x', 200)).Length <= 48);
    }
}
