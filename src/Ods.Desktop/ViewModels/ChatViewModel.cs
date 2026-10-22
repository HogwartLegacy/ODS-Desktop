using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ods.Core;
using Ods.Core.Chat;
using Ods.Core.Diagnostics;
using Ods.Core.Runtime;
using Ods.Desktop.Converters;
using Ods.Desktop.Services;

namespace Ods.Desktop.ViewModels;

public sealed partial class MessageViewModel(StoredMessage message) : ObservableObject
{
    public StoredMessage Message { get; } = message;

    public bool IsUser => Message.Role == "user";

    public string Author => IsUser ? "You" : Message.ModelName ?? "ODS";

    [ObservableProperty]
    private string _content = message.Content;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private string? _stats = message.TokensPerSecond is double speed ? $"{message.Tokens} tokens · {speed:0.0} tokens/s" : null;

    [RelayCommand]
    private void Copy() => Clipboard.SetText(Content);
}

public sealed partial class ConversationItem(Conversation conversation) : ObservableObject
{
    public Conversation Conversation { get; } = conversation;

    [ObservableProperty]
    private string _title = conversation.Title;

    [ObservableProperty]
    private string _when = Format.Relative(conversation.UpdatedAt);
}

/// <summary>Local chat against the model server, with history kept on this PC.</summary>
public sealed partial class ChatViewModel : ObservableObject
{
    private const string SystemPrompt =
        "You are ODS, a private assistant running locally on the user's Windows PC. Answer clearly and concisely. Use Markdown for code.";

    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(60);
    private readonly AppHost _host;
    private CancellationTokenSource? _generation;
    private TaskCompletionSource _idle = CompletedSignal();
    private bool _selectingNewConversation;

    public ChatViewModel(AppHost host)
    {
        _host = host;
        foreach (var conversation in host.Engine.Conversations.LoadAll()) Conversations.Add(new ConversationItem(conversation));
    }

    public ShellViewModel Shell => _host.Shell;

    private OdsEngine Engine => _host.Engine;

    public ObservableCollection<ConversationItem> Conversations { get; } = [];

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    public IReadOnlyList<string> Suggestions { get; } =
    [
        "Explain what a GGUF model file is, in two sentences.",
        "Write a PowerShell script that lists the 10 largest files in my Downloads folder.",
        "Draft a polite email asking to move a meeting to Thursday.",
        "What are the pros and cons of running AI models locally?",
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private ConversationItem? _selectedConversation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand))]
    private bool _isGenerating;

    public bool IsEmpty => Messages.Count == 0;

    /// <summary>Raised when the transcript should scroll to its end.</summary>
    public event Action? ScrollRequested;

    partial void OnSelectedConversationChanged(ConversationItem? value)
    {
        // A conversation created for the message being sent already shows its messages.
        if (IsGenerating || _selectingNewConversation) return;
        Messages.Clear();
        foreach (var message in value?.Conversation.Messages ?? []) Messages.Add(new MessageViewModel(message));
        OnPropertyChanged(nameof(IsEmpty));
        ScrollRequested?.Invoke();
    }

    /// <summary>Completes when no reply is streaming (the bootstrap switch waits on it).</summary>
    public async Task WaitUntilIdleAsync(CancellationToken cancellationToken)
    {
        while (IsGenerating) await _idle.Task.WaitAsync(cancellationToken);
    }

    private bool CanSend => !IsGenerating && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var status = Engine.Server.Status;
        if (status.State != ServerState.Ready || status.BaseUri is null || status.Model is null)
        {
            _host.Notify("The model is not running", "Start the model server on the Home page, then send your message again.");
            return;
        }
        var text = Input.Trim();
        Input = "";
        var conversation = SelectedConversation?.Conversation ?? NewConversation(text);
        var user = new StoredMessage { Role = "user", Content = text };
        conversation.Messages.Add(user);
        Messages.Add(new MessageViewModel(user));
        var assistant = new StoredMessage { Role = "assistant", ModelName = status.Model.Name };
        var reply = new MessageViewModel(assistant) { IsStreaming = true };
        Messages.Add(reply);
        OnPropertyChanged(nameof(IsEmpty));
        ScrollRequested?.Invoke();

        IsGenerating = true;
        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _generation = new CancellationTokenSource();
        var builder = new StringBuilder();
        var render = Stopwatch.StartNew();
        ChatTimings? timings = null;
        try
        {
            var model = Engine.FindModel(status.Model.Id).PrimaryFile;
            var history = BuildHistory(conversation, status.ContextSize ?? 8192);
            await foreach (var delta in Engine.Chat.StreamAsync(status.BaseUri, Engine.ApiKey, model, history, new ChatRequestOptions(), _generation.Token))
            {
                if (delta.Content is not null) builder.Append(delta.Content);
                timings = delta.Timings ?? timings;
                if (render.Elapsed >= RenderInterval)
                {
                    reply.Content = builder.ToString();
                    ScrollRequested?.Invoke();
                    render.Restart();
                }
            }
        }
        catch (OperationCanceledException)
        {
            builder.Append(builder.Length > 0 ? "\n\n*(stopped)*" : "*(stopped)*");
        }
        catch (ChatException error)
        {
            reply.IsError = true;
            builder.Append(builder.Length > 0 ? "\n\n" : "").Append(error.Message);
            AppLog.Warn($"Chat request failed: {error.Message}");
        }
        finally
        {
            reply.Content = builder.ToString();
            reply.IsStreaming = false;
            if (timings is not null)
            {
                assistant.Tokens = timings.PredictedTokens;
                assistant.TokensPerSecond = Math.Round(timings.TokensPerSecond, 1);
                reply.Stats = $"{timings.PredictedTokens} tokens · {timings.TokensPerSecond:0.0} tokens/s";
                _host.Home.ReportSpeed(timings.TokensPerSecond);
            }
            assistant.Content = reply.Content;
            conversation.Messages.Add(assistant);
            conversation.UpdatedAt = DateTimeOffset.Now;
            Engine.Conversations.Save(conversation);
            if (SelectedConversation is { } item)
            {
                item.When = Format.Relative(conversation.UpdatedAt);
                Conversations.Move(Conversations.IndexOf(item), 0);
            }
            IsGenerating = false;
            _generation.Dispose();
            _generation = null;
            _idle.TrySetResult();
            ScrollRequested?.Invoke();
        }
    }

    /// <summary>The system prompt plus as much recent history as fits in about 70% of the context.</summary>
    private static List<ChatMessage> BuildHistory(Conversation conversation, int contextTokens)
    {
        var budget = (int)(contextTokens * 0.7 * 3.5);
        var messages = new List<ChatMessage>();
        var used = 0;
        foreach (var message in conversation.Messages.AsEnumerable().Reverse())
        {
            used += message.Content.Length;
            if (used > budget && messages.Count > 0) break;
            messages.Insert(0, new ChatMessage(message.Role, message.Content));
        }
        messages.Insert(0, new ChatMessage("system", conversation.SystemPrompt ?? SystemPrompt));
        return messages;
    }

    private Conversation NewConversation(string firstMessage)
    {
        var conversation = new Conversation { Title = ConversationStore.TitleFrom(firstMessage) };
        var item = new ConversationItem(conversation);
        Conversations.Insert(0, item);
        _selectingNewConversation = true;
        SelectedConversation = item;
        _selectingNewConversation = false;
        return conversation;
    }

    private bool CanStop => IsGenerating;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _generation?.Cancel();

    [RelayCommand]
    private void NewChat()
    {
        if (IsGenerating) return;
        SelectedConversation = null;
        Messages.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void DeleteConversation(ConversationItem? item)
    {
        if (item is null || IsGenerating) return;
        Engine.Conversations.Delete(item.Conversation.Id);
        Conversations.Remove(item);
        if (SelectedConversation == item) NewChat();
    }

    [RelayCommand]
    private void UseSuggestion(string text) => Input = text;

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource();
        signal.SetResult();
        return signal;
    }
}
