using GitExtensions.AITools.LlmProviders;
using GitExtensions.Extensibility.Git;
using System.Diagnostics;
using GitExtensions.Extensibility.Settings;
using GitExtensions.Extensibility.Translations;
using ResourceManager;
using System.Reflection;

namespace GitExtensions.AITools;

internal sealed class CommitMessageFeature : IAiFeature, ITranslate
{
    private const string TemplateKey = "AI: Generate commit message";
    private const string LegacyTemplateKey = "AI commit template";

    private readonly AiToolsHost _host;
    private readonly BoolSetting _autoFillSetting = new("AI commit message auto-fill", "Auto-fill on stage/unstage", true);
    //private readonly StringSetting _commitTypesSetting = new("AI commit types", "Commit types (comma-separated)", CommitMessageGenerator.DefaultCommitTypes, true);
    private readonly MultilineStringSetting _customInstructionsSetting = new("AI custom instructions", "Custom instructions (appended to built-in prompt)", "");

    private readonly TranslationString _triggerText = new("AI: Generate commit message...");
    private readonly TranslationString _noApiKeyMessage = new("[AI Tools: No API key configured. Open Plugins > AI Tools to configure.]");
    private readonly TranslationString _generatingMessage = new("Generating AI commit message...");
    private readonly TranslationString _cancelledMessage = new("[AI Commit Message: Generation was cancelled.]");
    private readonly TranslationString _errorMessage = new("[AI Commit Message Error: {0}]");
    private readonly TranslationString _cancelAiText = new("Cancel AI");

    private CancellationTokenSource? _cancellationTokenSource;
    private SynchronizationContext? _uiContext;
    private ILlmProvider? _currentProvider;
    private string? _currentCustomInstructions;
    private string? _currentCommitTypes;
    private IGitUICommands? _currentGitUiCommands;
    private Control? _messageControl;
    private bool _listeningToTextChanged;
    private Button? _commitButton;
    private Button? _commitAndPushButton;
    private Button? _cancelAiButton;
    private SplitContainer? _commitPanelSplitContainer;
    private int _originalCommitPanelMinSize;
    private string _messageBeforeGeneration = string.Empty;
    private FileSystemWatcher? _indexWatcher;
    private System.Threading.Timer? _debounceTimer;
    private long _watcherStartTicks;
    private string? _configError;
    private readonly object _stateLock = new();
    private long _indexChangeVersion;
    private bool _regenerateRequested;
    private bool _buttonsDisabled;
    private IGitModule? _currentModule;

    public CommitMessageFeature(AiToolsHost host)
    {
        _host = host;
    }

    public IEnumerable<ISetting> GetSettings()
    {
        return [_autoFillSetting, /*_commitTypesSetting,*/ _customInstructionsSetting];
    }

    public void Register(IGitUICommands gitUiCommands)
    {
        //MigrateEmptySetting(_commitTypesSetting, _host.Settings);

        gitUiCommands.RemoveCommitTemplate(LegacyTemplateKey);

        gitUiCommands.PreCommit += OnPreCommit;
        gitUiCommands.PostCommit += OnPostCommit;
    }

    public void Unregister(IGitUICommands gitUiCommands)
    {
        CleanUp(gitUiCommands);
        gitUiCommands.PreCommit -= OnPreCommit;
        gitUiCommands.PostCommit -= OnPostCommit;
    }

    public void Translate()
    {
        // Handled by the plugin's translation infrastructure
    }

    public void AddTranslationItems(ITranslation translation)
    {
        TranslationUtils.AddTranslationItemsFromFields("AiCommitMessagePlugin", this, translation);
    }

    public void TranslateItems(ITranslation translation)
    {
        TranslationUtils.TranslateItemsFromFields("AiCommitMessagePlugin", this, translation);
    }

    public void Dispose()
    {
        // Cleanup handled by Unregister
    }

    private void OnPreCommit(object? sender, GitUIEventArgs e)
    {
        if (!_host.EnabledSetting.ValueOrDefault(_host.Settings))
        {
            return;
        }

        // Generation and its UI updates share the commit dialog's UI thread.
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        //string commitPrefixes = _commitTypesSetting.ValueOrDefault(_host.Settings);
        string customInstructions = _customInstructionsSetting.ValueOrDefault(_host.Settings);

        _configError = null;
        _currentProvider = _host.CreateProvider(out string? configError);

        if (_currentProvider is null)
        {
            _configError = configError is not null
                ? $"[AI Tools] {configError}"
                : _noApiKeyMessage.Text;
        }

        _currentCustomInstructions = customInstructions;
        //_currentCommitTypes = commitPrefixes;
        _currentGitUiCommands = e.GitUICommands;
        _messageControl = null;

        if (_autoFillSetting.ValueOrDefault(_host.Settings))
        {
            StartIndexWatcher(e.GitUICommands.Module);
        }

        if (_configError is not null)
        {
            e.GitUICommands.AddCommitTemplate(TemplateKey, () => _configError, _host.Icon);
        }
        else
        {
            e.GitUICommands.AddCommitTemplate(TemplateKey, () => GetGeneratedMessage(), _host.Icon);
        }
    }

    private void OnPostCommit(object? sender, GitUIPostActionEventArgs e)
    {
        CleanUp(e.GitUICommands);
    }

    private void CleanUp(IGitUICommands gitUiCommands)
    {
        StopIndexWatcher();
        CancelPendingWork();
        _currentModule = null;
        UnhookTextChanged();
        FinishGenerationUi();
        if (_cancelAiButton is not null)
        {
            _cancelAiButton.Click -= OnCancelAiClick;
            _cancelAiButton.Dispose();
            _cancelAiButton = null;
        }
        _uiContext = null;
        _configError = null;
        _currentProvider = null;
        _currentCustomInstructions = null;
        _currentCommitTypes = null;
        _currentGitUiCommands = null;
        _messageControl = null;
        _commitButton = null;
        _commitAndPushButton = null;
        gitUiCommands.RemoveCommitTemplate(TemplateKey);
    }

    private string GetGeneratedMessage()
    {
        EnsureTextChangedHooked();
        return _triggerText.Text;
    }

    private void EnsureTextChangedHooked()
    {
        if (_listeningToTextChanged)
        {
            return;
        }

        _messageControl ??= FindMessageControl();
        if (_messageControl is null)
        {
            return;
        }

        _messageControl.TextChanged += OnMessageTextChanged;
        _listeningToTextChanged = true;
    }

    private void UnhookTextChanged()
    {
        if (_listeningToTextChanged && _messageControl is not null && !_messageControl.IsDisposed)
        {
            _messageControl.TextChanged -= OnMessageTextChanged;
        }

        _listeningToTextChanged = false;
    }

    private void OnMessageTextChanged(object? sender, EventArgs e)
    {
        if (sender is not Control control || control.Text != _triggerText.Text)
        {
            return;
        }

        if (_currentProvider is not null && _currentGitUiCommands is not null)
        {
            StartGeneration(_currentGitUiCommands.Module, autoFill: true);
        }
    }

    private void StartGeneration(IGitModule module, bool autoFill)
    {
        _currentModule = module;
        if (_cancellationTokenSource is not null)
        {
            _regenerateRequested = true;
            CancelRequest(_cancellationTokenSource);
            return;
        }

        _regenerateRequested = false;
        BeginGeneration(module, autoFill);
    }

    private async void BeginGeneration(IGitModule module, bool autoFill)
    {
        SynchronizationContext uiContext = _uiContext!;
        CancellationTokenSource cts = new();
        _cancellationTokenSource = cts;
        CancellationToken ct = cts.Token;
        string? message = null;

        try
        {
            if (autoFill && !_buttonsDisabled)
            {
                _messageControl ??= FindMessageControl();
                _messageBeforeGeneration = _messageControl?.Text ?? string.Empty;
                if (_messageBeforeGeneration == _triggerText.Text
                    || _messageBeforeGeneration == CommitMessageGenerator.NoStagedChangesMessage)
                {
                    _messageBeforeGeneration = string.Empty;
                }
                SetCommitMessage(_messageControl, _generatingMessage.Text);

                _commitButton ??= FindButton("Commit");
                _commitAndPushButton ??= FindButton("CommitAndPush");
                _buttonsDisabled = true;
                SetCommitButtonsEnabled(false);
                ShowCancelAiButton();
            }

            // Snapshot settings before awaiting so an old request never reads a new session's state.
            ILlmProvider provider = _currentProvider!;
            string commitTypes = _currentCommitTypes ?? CommitMessageGenerator.DefaultCommitTypes;
            string customInstructions = _currentCustomInstructions ?? string.Empty;
            string youTrackUrl = _host.YouTrackUrlSetting.ValueOrDefault(_host.Settings);
            string youTrackToken = _host.YouTrackTokenSetting.ValueOrDefault(_host.Settings);
            message = await Task.Run(async () =>
            {
                if (!string.IsNullOrWhiteSpace(youTrackToken))
                {
                    try
                    {
                        string issues = await YouTrackIssueProvider.GetMyAssignedIssuesAsJsonAsync(
                            string.IsNullOrWhiteSpace(youTrackUrl) ? "https://dev-track.fileforce.jp" : youTrackUrl,
                            youTrackToken, ct).ConfigureAwait(false);
                        customInstructions += Environment.NewLine + Environment.NewLine +
                            "Currently active YouTrack tickets in JSON format (choose the one that fits):" +
                            Environment.NewLine + issues;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"YouTrack Error: {ex}");
                    }
                }

                ct.ThrowIfCancellationRequested();
                CommitMessageGenerator generator = new(provider, commitTypes, customInstructions);
                return await GenerateSafeAsync(generator, module, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation either starts the queued regeneration or has already restored the UI.
        }
        catch (Exception ex)
        {
            message = string.Format(_errorMessage.Text, ex.Message);
        }
        finally
        {
            try
            {
                uiContext.Post(_ => CompleteGeneration(cts, autoFill, message), null);
            }
            catch (InvalidOperationException ex)
            {
                // The dialog's UI thread may already have shut down.
                Debug.WriteLine(ex.Message);
                cts.Dispose();
            }
        }
    }

    private void CompleteGeneration(CancellationTokenSource cts, bool autoFill, string? message)
    {
        // Always invoked on the captured UI context, including work started by a timer.
        // Cancel detaches the request before restoring editing, so late responses are ignored.
        try
        {
            if (ReferenceEquals(_cancellationTokenSource, cts))
            {
                if (!cts.IsCancellationRequested && autoFill && message is not null)
                {
                    SetCommitMessage(_messageControl, message);
                }
                _cancellationTokenSource = null;
                bool regenerate = _regenerateRequested;
                _regenerateRequested = false;
                if (regenerate && _currentModule is not null && _currentProvider is not null)
                {
                    BeginGeneration(_currentModule, autoFill);
                }
                else
                {
                    FinishGenerationUi();
                }
            }
        }
        finally { cts.Dispose(); }
    }

    private void ShowCancelAiButton()
    {
        if (_commitButton?.Parent is not Control panel)
        {
            return;
        }

        if (_cancelAiButton is null || _cancelAiButton.IsDisposed)
        {
            _cancelAiButton = new Button
            {
                Name = "CancelAI",
                Text = _cancelAiText.Text,
                AutoSize = true,
                MinimumSize = _commitButton.Size,
                Size = _commitButton.Size,
                Margin = _commitAndPushButton?.Margin ?? _commitButton.Margin,
                Font = _commitButton.Font,
                BackColor = _commitButton.BackColor,
                ForeColor = _commitButton.ForeColor,
                FlatStyle = _commitButton.FlatStyle,
                UseVisualStyleBackColor = true,
                TabIndex = panel.Controls.Cast<Control>().Select(c => c.TabIndex).DefaultIfEmpty().Max() + 1,
            };
            _cancelAiButton.Click += OnCancelAiClick;
            panel.Controls.Add(_cancelAiButton);
        }

        _cancelAiButton.Visible = true;

        // GE sizes this panel once when opening the dialog. Make room for the extra
        // button when the message panel is at its minimum height.
        for (Control? ancestor = panel.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is SplitterPanel { Parent: SplitContainer split } && split.Panel2 == ancestor
                && split.Orientation == Orientation.Horizontal)
            {
                _commitPanelSplitContainer = split;
                _originalCommitPanelMinSize = split.Panel2MinSize;
                int availableHeight = split.ClientSize.Height - split.SplitterWidth - split.Panel1MinSize;
                int requiredHeight = Math.Min(availableHeight,
                    Math.Max(split.Panel2MinSize, panel.PreferredSize.Height + panel.Margin.Vertical));
                split.SplitterDistance = Math.Min(split.SplitterDistance,
                    split.ClientSize.Height - split.SplitterWidth - requiredHeight);
                split.Panel2MinSize = requiredHeight;
                break;
            }
        }
    }

    private void OnCancelAiClick(object? sender, EventArgs e)
    {
        CancelPendingDebounce();
        CancelPendingWork();
        if (_messageControl?.Text == _generatingMessage.Text)
        {
            SetCommitMessage(_messageControl, _messageBeforeGeneration);
        }
        FinishGenerationUi();
        _messageControl?.Focus();
    }

    private void FinishGenerationUi()
    {
        if (_buttonsDisabled)
        {
            _buttonsDisabled = false;
            SetCommitButtonsEnabled(true);
        }
        if (_cancelAiButton is not null && !_cancelAiButton.IsDisposed)
        {
            _cancelAiButton.Visible = false;
        }
        if (_commitPanelSplitContainer is not null && !_commitPanelSplitContainer.IsDisposed)
        {
            _commitPanelSplitContainer.Panel2MinSize = _originalCommitPanelMinSize;
        }
        _commitPanelSplitContainer = null;
    }

    private void StartIndexWatcher(IGitModule module)
    {
        StopIndexWatcher();

        try
        {
            string gitDir = module.WorkingDirGitDir.TrimEnd('\\', '/');
            _watcherStartTicks = Environment.TickCount64;
            _indexWatcher = new FileSystemWatcher(gitDir, "index*")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            _indexWatcher.Changed += OnGitIndexChanged;
            _indexWatcher.Created += OnGitIndexChanged;
            _indexWatcher.Renamed += OnGitIndexRenamed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void StopIndexWatcher()
    {
        FileSystemWatcher? watcher;
        lock (_stateLock)
        {
            watcher = _indexWatcher;
            _indexWatcher = null;
            CancelPendingDebounce();
        }

        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnGitIndexChanged;
            watcher.Created -= OnGitIndexChanged;
            watcher.Renamed -= OnGitIndexRenamed;
            watcher.Dispose();
        }
    }

    private void OnGitIndexRenamed(object sender, RenamedEventArgs e)
    {
        if (string.Equals(e.Name, "index", StringComparison.OrdinalIgnoreCase))
        {
            OnGitIndexChanged(sender, e);
        }
    }

    private void OnGitIndexChanged(object sender, FileSystemEventArgs e)
    {
        if (!string.Equals(e.Name, "index", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Ignore index events during the first 3 seconds after watcher start —
        // GE touches the index during dialog initialization.
        if (Environment.TickCount64 - Interlocked.Read(ref _watcherStartTicks) < 3000)
        {
            return;
        }

        lock (_stateLock)
        {
            if (!ReferenceEquals(sender, _indexWatcher))
            {
                return;
            }
            long version = Interlocked.Increment(ref _indexChangeVersion);
            _debounceTimer?.Dispose();
            _debounceTimer = new System.Threading.Timer(OnDebounceTimerElapsed, version, 1500, Timeout.Infinite);
        }
    }

    private void OnDebounceTimerElapsed(object? state)
    {
        long version = (long)state!;
        try
        {
            _uiContext?.Post(_ =>
            {
                if (version != Interlocked.Read(ref _indexChangeVersion))
                {
                    return;
                }
                try { OnGitIndexChangedCore(); }
                catch (Exception ex) { Debug.WriteLine(ex.Message); }
            }, null);
        }
        catch (InvalidOperationException ex)
        {
            // A queued timer may outlive the dialog's UI thread.
            Debug.WriteLine(ex.Message);
        }
    }

    private void CancelPendingDebounce()
    {
        lock (_stateLock)
        {
            Interlocked.Increment(ref _indexChangeVersion);
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private void OnGitIndexChangedCore()
    {
        if (_currentGitUiCommands is null)
        {
            return;
        }

        if (_currentProvider is null)
        {
            if (_configError is not null)
            {
                _messageControl ??= FindMessageControl();
                SetCommitMessage(_messageControl, _configError);
            }

            return;
        }

        StartGeneration(_currentGitUiCommands.Module, autoFill: true);
    }

    private static Control? FindMessageControl()
    {
        try
        {
            Form? formCommit = Application.OpenForms
                .Cast<Form>()
                .FirstOrDefault(f => f.GetType().Name == "FormCommit");

            if (formCommit is null)
            {
                return null;
            }

            FieldInfo? messageField = formCommit.GetType()
                .GetField("Message", BindingFlags.Instance | BindingFlags.NonPublic);

            return messageField?.GetValue(formCommit) as Control;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    private static Button? FindButton(string fieldName)
    {
        try
        {
            Form? formCommit = Application.OpenForms
                .Cast<Form>()
                .FirstOrDefault(f => f.GetType().Name == "FormCommit");

            if (formCommit is null)
            {
                return null;
            }

            FieldInfo? field = formCommit.GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);

            return field?.GetValue(formCommit) as Button;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    private void SetCommitButtonsEnabled(bool enabled)
    {
        SetButtonEnabled(_commitButton, enabled);
        SetButtonEnabled(_commitAndPushButton, enabled);
    }

    private static void SetButtonEnabled(Button? button, bool enabled)
    {
        if (button is null || button.IsDisposed)
        {
            return;
        }

        try
        {
            if (button.InvokeRequired)
            {
                button.BeginInvoke(() => button.Enabled = enabled);
            }
            else
            {
                button.Enabled = enabled;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    private static void SetCommitMessage(Control? messageControl, string message)
    {
        if (messageControl is null || messageControl.IsDisposed)
        {
            return;
        }

        try
        {
            if (messageControl.InvokeRequired)
            {
                messageControl.BeginInvoke(() => messageControl.Text = message);
            }
            else
            {
                messageControl.Text = message;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }
    }

    private async Task<string> GenerateSafeAsync(
        CommitMessageGenerator generator, IGitModule module, CancellationToken cancellationToken)
    {
        try
        {
            return await generator.GenerateAsync(module, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return _cancelledMessage.Text;
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine(ex.Message);
            return string.Empty;
        }
        catch (Exception ex)
        {
            return string.Format(_errorMessage.Text, ex.Message);
        }
    }

    private void CancelPendingWork()
    {
        _regenerateRequested = false;
        _currentModule = null;
        CancellationTokenSource? old = _cancellationTokenSource;
        _cancellationTokenSource = null;
        if (old is not null)
        {
            // The detached request owns disposal, after its asynchronous work has finished.
            CancelRequest(old);
        }
    }

    private static void CancelRequest(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }
    }

    private static void MigrateEmptySetting(StringSetting setting, SettingsSource settings)
    {
        if (string.IsNullOrEmpty(setting[settings]))
        {
            setting[settings] = null;
        }
    }
}
