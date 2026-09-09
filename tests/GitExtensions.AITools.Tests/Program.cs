using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using GitExtensions.AITools;
using GitExtensions.AITools.LlmProviders;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtensions.Extensibility.Settings;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Control.CheckForIllegalCrossThreadCalls = true;
        (string Name, Action Test)[] tests =
        [
            ("Completion restores the controls", CompletionRestoresControls),
            ("Provider failures restore the controls", FailureRestoresControls),
            ("Cancel restores the draft and ignores a late response", CancelRestoresDraft),
            ("Cancel clears the no-staged-changes placeholder", CancelClearsNoStagedChanges),
            ("Cancel preserves edits made during generation", CancelPreservesEdits),
            ("Old completion cannot affect a newer request", OldCompletionCannotAffectNewRequest),
            ("Stage changes regenerate normally", StageChangesRegenerate),
            ("Cancel clears queued regeneration and debounce", CancelClearsQueuedWork),
            ("Unregister cancels work and removes the button", UnregisterCleansUp),
            ("Cancel aborts YouTrack before calling the AI provider", CancelDuringYouTrack),
        ];
        try
        {
            foreach ((string name, Action test) in tests)
            {
                test();
                Console.WriteLine($"PASS: {name}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CompletionRestoresControls()
    {
        using Fixture fixture = new();
        fixture.Start();
        fixture.WaitForRequests(1);
        Check(!fixture.Commit.Enabled && !fixture.CommitAndPush.Enabled, "Commit controls must be disabled.");
        Check(fixture.Cancel.Visible && fixture.Cancel.Text == "Cancel AI", "Cancel button must be visible.");
        Check(fixture.Panel.Controls.GetChildIndex(fixture.Cancel) == fixture.Panel.Controls.Count - 1, "Cancel must be the last control.");
        Check(fixture.Cancel.Bottom <= fixture.Panel.ClientSize.Height, "Cancel must fit in the message panel.");
        fixture.Provider.Requests[0].Result.SetResult("feat: generated message");
        PumpUntil(() => fixture.Commit.Enabled);
        Check(fixture.Message.Text == "feat: generated message", "Generated message must be applied.");
        Check(fixture.CommitAndPush.Enabled && !fixture.Cancel.Visible, "Completion must restore controls.");
    }

    private static void CancelRestoresDraft()
    {
        using Fixture fixture = new();
        fixture.Message.Text = "My draft";
        fixture.Start();
        fixture.WaitForRequests(1);
        CancellationTokenSource request = fixture.CurrentRequest;
        fixture.Cancel.PerformClick();
        Check(fixture.Provider.Requests[0].Token.IsCancellationRequested, "The provider must receive cancellation.");
        Check(fixture.Commit.Enabled && fixture.CommitAndPush.Enabled && !fixture.Cancel.Visible, "Cancel must restore controls immediately.");
        Check(fixture.Message.Text == "My draft", "Cancel must restore the previous draft.");
        Check(fixture.Split.Panel2MinSize == Fixture.OriginalMinimum, "The original panel minimum must be restored.");
        fixture.Message.Text = "My manual edit";
        fixture.Provider.Requests[0].Result.SetResult("late response");
        PumpUntil(() => IsDisposed(request));
        Check(fixture.Message.Text == "My manual edit", "A cancelled response must never overwrite manual edits.");
    }

    private static void FailureRestoresControls()
    {
        using Fixture fixture = new();
        fixture.Start();
        fixture.WaitForRequests(1);
        fixture.Provider.Requests[0].Result.SetException(new HttpRequestException("Simulated API failure"));
        PumpUntil(() => fixture.Commit.Enabled);
        Check(fixture.CommitAndPush.Enabled && !fixture.Cancel.Visible, "A failed request must restore the controls.");
        Check(fixture.Message.Text.Contains("Simulated API failure"), "The failure must still be reported.");
    }

    private static void CancelClearsNoStagedChanges()
    {
        using Fixture fixture = new();
        fixture.Message.Text = "[No staged changes found. Stage some changes before generating a commit message.]";
        SetField(fixture.Feature, "_watcherStartTicks", Environment.TickCount64 - 4000);
        Invoke(fixture.Feature, "OnGitIndexChanged", fixture.Watcher, new FileSystemEventArgs(WatcherChangeTypes.Changed, "C:\\test", "index"));
        fixture.WaitForRequests(1);
        CancellationTokenSource request = fixture.CurrentRequest;
        fixture.Cancel.PerformClick();
        Check(fixture.Message.Text == string.Empty, "Cancel must clear the previous no-staged-changes placeholder.");
        fixture.Provider.Requests[0].Result.SetResult("late response");
        PumpUntil(() => IsDisposed(request));
        Check(fixture.Message.Text == string.Empty, "The message must stay empty after a cancelled response arrives.");
    }

    private static void CancelPreservesEdits()
    {
        using Fixture fixture = new();
        fixture.Start();
        fixture.WaitForRequests(1);
        CancellationTokenSource request = fixture.CurrentRequest;
        fixture.Message.Text = "Typed while AI was running";
        fixture.Cancel.PerformClick();
        Check(fixture.Message.Text == "Typed while AI was running", "Cancel must preserve edits made while waiting.");
        fixture.Provider.Requests[0].Result.SetResult("late response");
        PumpUntil(() => IsDisposed(request));
    }

    private static void OldCompletionCannotAffectNewRequest()
    {
        using Fixture fixture = new();
        fixture.Start();
        fixture.WaitForRequests(1);
        CancellationTokenSource old = fixture.CurrentRequest;
        fixture.Cancel.PerformClick();
        fixture.Start();
        fixture.WaitForRequests(2);
        fixture.Provider.Requests[0].Result.SetResult("old response");
        PumpUntil(() => IsDisposed(old));
        Check(!fixture.Commit.Enabled && fixture.Cancel.Visible, "Old completion must not unlock a newer request.");
        Check(fixture.Message.Text != "old response", "Old text must not be applied.");
        fixture.Provider.Requests[1].Result.SetResult("new response");
        PumpUntil(() => fixture.Commit.Enabled);
        Check(fixture.Message.Text == "new response", "The new request must still complete normally.");
    }

    private static void CancelClearsQueuedWork()
    {
        using Fixture fixture = new();
        fixture.Start();
        fixture.WaitForRequests(1);
        CancellationTokenSource request = fixture.CurrentRequest;
        fixture.Start(); // A stage change during generation queues a replacement.
        SetField(fixture.Feature, "_watcherStartTicks", Environment.TickCount64 - 4000);
        Invoke(fixture.Feature, "OnGitIndexChanged", fixture.Watcher, new FileSystemEventArgs(WatcherChangeTypes.Changed, "C:\\test", "index"));
        Invoke(fixture.Feature, "OnDebounceTimerElapsed", GetField<long>(fixture.Feature, "_indexChangeVersion"));
        fixture.Cancel.PerformClick(); // Also invalidate the callback already posted to the UI thread.
        fixture.Provider.Requests[0].Result.SetResult("cancelled response");
        PumpUntil(() => IsDisposed(request));
        Check(fixture.Provider.Requests.Length == 1, "Cancel must discard all queued work.");
        SetField(fixture.Feature, "_watcherStartTicks", Environment.TickCount64 - 4000);
        Invoke(fixture.Feature, "OnGitIndexChanged", fixture.Watcher, new FileSystemEventArgs(WatcherChangeTypes.Changed, "C:\\test", "index"));
        fixture.WaitForRequests(2); // A fresh stage change must still work after cancellation.
        fixture.Provider.Requests[1].Result.SetResult("after a new stage change");
        PumpUntil(() => fixture.Commit.Enabled);
    }

    private static void UnregisterCleansUp()
    {
        using Fixture fixture = new();
        fixture.Start();
        fixture.WaitForRequests(1);
        CancellationTokenSource request = fixture.CurrentRequest;
        Button cancel = fixture.Cancel;
        fixture.Feature.Unregister(fixture.Commands);
        Check(cancel.IsDisposed && !fixture.Panel.Controls.Contains(cancel), "Unregister must remove the dynamic button.");
        Check(fixture.Commit.Enabled && fixture.CommitAndPush.Enabled, "Unregister must restore commit controls.");
        fixture.Message.Text = "Closed session";
        fixture.Provider.Requests[0].Result.SetResult("stale session");
        PumpUntil(() => IsDisposed(request));
        Check(fixture.Message.Text == "Closed session", "Closed sessions must ignore completions.");
    }

    private static void StageChangesRegenerate()
    {
        using Fixture fixture = new();
        fixture.Message.Text = "Original draft";
        fixture.Start();
        fixture.WaitForRequests(1);
        fixture.Start();
        Check(fixture.Provider.Requests[0].Token.IsCancellationRequested, "A stage change must cancel the superseded request.");
        fixture.Provider.Requests[0].Result.SetResult("superseded message");
        fixture.WaitForRequests(2);
        Check(!fixture.Commit.Enabled && fixture.Cancel.Visible, "The replacement request must keep the controls locked.");
        Check(fixture.Message.Text != "superseded message", "Superseded text must not be applied.");
        CancellationTokenSource request = fixture.CurrentRequest;
        fixture.Cancel.PerformClick();
        Check(fixture.Message.Text == "Original draft", "Cancel after regeneration must restore the original draft.");
        fixture.Provider.Requests[1].Result.SetResult("cancelled replacement");
        PumpUntil(() => IsDisposed(request));
    }

    private static void CancelDuringYouTrack()
    {
        using TcpListener server = new(IPAddress.Loopback, 0);
        server.Start();
        using Fixture fixture = new();
        fixture.Settings.Values["youtrack-url"] = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}";
        fixture.Settings.Values["youtrack-token"] = "test-only-token";
        Task<TcpClient> connection = server.AcceptTcpClientAsync();
        fixture.Start();
        CancellationTokenSource request = fixture.CurrentRequest;
        PumpUntil(() => connection.IsCompleted);
        using TcpClient client = connection.GetAwaiter().GetResult(); // Accept but never send a response.
        fixture.Cancel.PerformClick();
        Check(fixture.Commit.Enabled && !fixture.Cancel.Visible, "Cancel must unlock while YouTrack is pending.");
        PumpUntil(() => IsDisposed(request));
        Check(fixture.Provider.Requests.Length == 0, "The AI provider must not start after a cancelled YouTrack lookup.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        do
        {
            Application.DoEvents();
            if (condition()) return;
            Thread.Sleep(10);
        } while (timeout.Elapsed < TimeSpan.FromSeconds(10));
        throw new TimeoutException("The expected asynchronous operation did not complete.");
    }

    private static bool IsDisposed(CancellationTokenSource source)
    {
        try { _ = source.Token; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private static void Invoke(object instance, string method, params object?[] args) =>
        instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void SetField(object instance, string field, object? value) =>
        instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static T GetField<T>(object instance, string field) =>
        (T)instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private sealed class Fixture : IDisposable
    {
        public const int OriginalMinimum = 100;
        private readonly Form _form = new() { ShowInTaskbar = false, Opacity = 0, Size = new Size(700, 500) };
        public SplitContainer Split { get; } = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        public FlowLayoutPanel Panel { get; } = new() { Dock = DockStyle.Left, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Width = 180 };
        public Button Commit { get; } = new() { Text = "Commit", Size = new Size(171, 26) };
        public Button CommitAndPush { get; } = new() { Text = "Commit & push", Size = new Size(171, 26) };
        public TextBox Message { get; } = new() { Multiline = true, Dock = DockStyle.Fill };
        public MemorySettings Settings { get; } = new();
        public ControlledProvider Provider { get; } = new();
        public FileSystemWatcher Watcher { get; } = new();
        public CommitMessageFeature Feature { get; }
        public IGitModule Module { get; }
        public IGitUICommands Commands { get; }
        public Button Cancel => GetField<Button>(Feature, "_cancelAiButton");
        public CancellationTokenSource CurrentRequest => GetField<CancellationTokenSource>(Feature, "_cancellationTokenSource");

        public Fixture()
        {
            Panel.Controls.AddRange([Commit, CommitAndPush, new Button { Text = "Existing action", Size = Commit.Size }]);
            Split.Panel2.Controls.Add(Message);
            Split.Panel2.Controls.Add(Panel);
            _form.Controls.Add(Split);
            _form.Show(); // Invisible, but with real WinForms handles and synchronization context.
            Split.Panel2MinSize = OriginalMinimum;
            Split.SplitterDistance = Split.Height - Split.SplitterWidth - OriginalMinimum;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            IExecutable executable = Stub<IExecutable>((method, args) =>
            {
                if (method.Name != "Start") throw new NotSupportedException(method.Name);
                string output = args![0]!.ToString()!.Contains("--stat") ? "file.txt | 1 +" : "+a staged change";
                StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(output)));
                return Stub<IProcess>((member, _) => member.Name switch
                {
                    "get_StandardOutput" => reader,
                    "WaitForExitAsync" => Task.FromResult(0),
                    "Dispose" => null,
                    _ => throw new NotSupportedException(member.Name),
                });
            });
            Module = Stub<IGitModule>((method, _) => method.Name switch
            {
                "get_GitExecutable" => executable,
                "GetSelectedBranch" => "test-branch",
                _ => throw new NotSupportedException(method.Name),
            });
            Commands = Stub<IGitUICommands>((method, _) => method.Name == "get_Module" ? Module : null);
            AiToolsHost host = new(Settings, null)
            {
                EnabledSetting = new("enabled", "Enabled", true),
                ProviderSetting = new("provider", "Provider", ["Fake"], "Fake"),
                ApiKeySetting = new("key", "Key", ""),
                ModelSetting = new("model", "Model", ""),
                YouTrackUrlSetting = new("youtrack-url", "YouTrack URL", ""),
                YouTrackTokenSetting = new("youtrack-token", "YouTrack token", ""),
            };
            Feature = new CommitMessageFeature(host);
            SetField(Feature, "_uiContext", SynchronizationContext.Current);
            SetField(Feature, "_currentGitUiCommands", Commands);
            SetField(Feature, "_currentProvider", Provider);
            SetField(Feature, "_messageControl", Message);
            SetField(Feature, "_commitButton", Commit);
            SetField(Feature, "_commitAndPushButton", CommitAndPush);
            SetField(Feature, "_indexWatcher", Watcher);
        }

        public void Start() => Invoke(Feature, "StartGeneration", Module, true);
        public void WaitForRequests(int count) => PumpUntil(() => Provider.Requests.Length == count);
        public void Dispose()
        {
            Feature.Unregister(Commands);
            foreach (ControlledProvider.Request request in Provider.Requests) request.Result.TrySetCanceled();
            Application.DoEvents();
            _form.Dispose();
        }
    }

    private sealed class MemorySettings : SettingsSource
    {
        public Dictionary<string, string?> Values { get; } = new();
        public override string? GetValue(string name) => Values.GetValueOrDefault(name);
        public override void SetValue(string name, string? value) => Values[name] = value;
    }

    private sealed class ControlledProvider : ILlmProvider
    {
        public sealed record Request(CancellationToken Token, TaskCompletionSource<string> Result);
        private readonly ConcurrentQueue<Request> _requests = new();
        public Request[] Requests => _requests.ToArray();
        public string Name => "Fake";
        public (string, bool) GetStatus(string apiKey) => ("Ready", true);
        public Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
        {
            TaskCompletionSource<string> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Enqueue(new(cancellationToken, result));
            return result.Task; // Intentionally ignore cancellation to exercise late responses.
        }
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        T proxy = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)proxy).Handler = invoke;
        return proxy;
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
