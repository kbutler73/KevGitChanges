using System.Threading.Tasks;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;

namespace KevGitChanges
{
    /// <summary>
    /// Interaction logic for ToolWindow1Control.
    /// </summary>
    public partial class ToolWindow1Control : UserControl
    {
        private string currentWorkDir;
        private string currentRepoRoot;
        private string currentBaseBranch;
        private string currentBranch;
        private string currentMainRef;
        private const string selectedRemote = "origin";
        private const string LoadingLabel = "Loading...";
        private EnvDTE.SolutionEvents solutionEvents;
        private EnvDTE.BuildEvents buildEvents;
        private EnvDTE.DebuggerEvents debuggerEvents;
        private ImageSource folderIcon;
        private ImageSource fileIcon;
        private bool showWorkspace = true;
        private bool showLocal = true;
        private bool showRemote = true;
        private IVsOutputWindowPane outputPane;
        private static readonly Guid OutputPaneGuid = new Guid("1C6450F7-14B0-4D9D-8C41-9B2D95C0F4D2");
        private bool ignoreCheckoutSelection;
        private bool iconDiagnosticsLogged;
        private int iconDiagnosticsCount;
        private string lastIconDiagnostics;
        private System.IO.FileSystemWatcher workspaceWatcher;
        private System.IO.FileSystemWatcher gitMetadataWatcher;
        private System.Windows.Threading.DispatcherTimer workspaceRefreshDebounceTimer;
        private System.Windows.Threading.DispatcherTimer periodicRefreshTimer;
        private bool refreshInProgress;
        private bool refreshPending;
        private string pendingRefreshReason = "Initial load";
        private string lastRefreshReason = string.Empty;
        private string deferredAutoRefreshReason;
        private DateTime lastRefreshUtc = DateTime.MinValue;
        private DateTime suppressGitMetadataEventsUntilUtc = DateTime.MinValue;
        private bool isVsBuildActive;
        private bool isVsDebugActive;
        private bool autoRefreshDeferredByVsState;
        private static readonly TimeSpan WorkspaceRefreshDebounce = TimeSpan.FromMilliseconds(900);
        private static readonly TimeSpan PeriodicRefreshCheckInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PeriodicRefreshThreshold = TimeSpan.FromMinutes(10);

        [DataContract]
        private class RepoSettings
        {
            [DataMember(Name = "baseBranch", EmitDefaultValue = false)]
            public string BaseBranch { get; set; }

            [DataMember(Name = "autoRefreshEnabled", EmitDefaultValue = false)]
            public bool? AutoRefreshEnabled { get; set; }
        }

        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<ChangeScope, string>> scopeMap =
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<ChangeScope, string>>(System.StringComparer.OrdinalIgnoreCase);

        private static readonly Brush StatusAddedBrush = new SolidColorBrush(Color.FromRgb(34, 197, 94));
        private static readonly Brush StatusModifiedBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246));
        private static readonly Brush StatusDeletedBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        private static readonly Brush StatusOtherBrush = new SolidColorBrush(Color.FromRgb(156, 163, 175));

        private enum ChangeScope
        {
            Workspace,
            Local,
            Remote,
            Main
        }

        static ToolWindow1Control()
        {
            FreezeBrush(StatusAddedBrush);
            FreezeBrush(StatusModifiedBrush);
            FreezeBrush(StatusDeletedBrush);
            FreezeBrush(StatusOtherBrush);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ToolWindow1Control"/> class.
        /// </summary>
        public ToolWindow1Control()
        {
            this.InitializeComponent();
            this.Loaded += ToolWindow1Control_Loaded;
            this.Unloaded += ToolWindow1Control_Unloaded;
            this.IsVisibleChanged += ToolWindow1Control_IsVisibleChanged;
        }

        private void ToolWindow1Control_Loaded(object sender, RoutedEventArgs e)
        {
            // determine solution dir as default work dir
            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                var solutionPath = dte?.Solution?.FullName;
                if (!string.IsNullOrWhiteSpace(solutionPath) && System.IO.File.Exists(solutionPath))
                {
                    currentWorkDir = System.IO.Path.GetDirectoryName(solutionPath);
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(currentWorkDir))
            {
                currentWorkDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            }

            currentRepoRoot = ResolveRepoRoot(currentWorkDir);
            ApplySavedAutoRefreshSetting(currentRepoRoot);

            EnsureOutputPane();
            EnsureIcons();
            WriteOutput("Icon diagnostics: EnsureIcons invoked on load.");
            EnsureVsActivityHooks();
            UpdateLastRefreshDisplay();
            EnsureAutoRefreshInfrastructure();
            RefreshAutoRefreshSubscriptions();
            InitializeCompareOptions();
            UpdateScopeLabels();
            SetCheckoutLoading();

            // populate base branches from origin for the default workdir
            PopulateBaseBranches(currentWorkDir, selectedRemote);
            PopulateCheckoutBranches(currentWorkDir);

            // start initial refresh only once the solution is loaded
            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte != null)
                {
                    var sol = dte?.Solution;
                    var solPath = sol?.FullName;
                    if (!string.IsNullOrWhiteSpace(solPath) && System.IO.File.Exists(solPath))
                    {
                        // solution already open
                        StartRefresh("Solution loaded");
                    }
                    else
                    {
                        // wait for solution opened
                        solutionEvents = dte.Events.SolutionEvents;
                        solutionEvents.Opened += SolutionEvents_Opened;
                    }
                }
                else
                {
                    // no DTE available, start refresh
                    StartRefresh("Initial load");
                }
            }
            catch
            {
                StartRefresh("Initial load");
            }
        }

        private void SolutionEvents_Opened()
        {
            try
            {
                // unsubscribe
                if (solutionEvents != null)
                {
                    solutionEvents.Opened -= SolutionEvents_Opened;
                    solutionEvents = null;
                }
            }
            catch { }

            // start refresh now that the solution is opened
            StartRefresh("Solution opened");
        }

        private void ToolWindow1Control_Unloaded(object sender, RoutedEventArgs e)
        {
            UnhookVsActivityEvents();
            StopAutoRefreshInfrastructure();
        }

        private void ToolWindow1Control_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            RefreshAutoRefreshSubscriptions();
        }

        private void StartRefresh(string reason = null)
        {
            var refreshReason = string.IsNullOrWhiteSpace(reason) ? pendingRefreshReason : reason;
            if (string.IsNullOrWhiteSpace(refreshReason))
            {
                refreshReason = "Refresh";
            }

            if (refreshInProgress)
            {
                refreshPending = true;
                pendingRefreshReason = refreshReason;
                return;
            }

            refreshInProgress = true;
            refreshPending = false;
            pendingRefreshReason = refreshReason;
            lastRefreshReason = refreshReason;
            suppressGitMetadataEventsUntilUtc = DateTime.UtcNow.AddSeconds(5);

            // show busy indicator and run refresh
            Dispatcher.Invoke(() =>
            {
                BusyIndicator.Visibility = Visibility.Visible;
                UpdateRefreshReasonDisplay(refreshReason);
                try { BaseBranchCombo.IsEnabled = false; } catch { }
                try { refreshButton.IsEnabled = false; } catch { }
            });
            _ = Task.Run(async () =>
            {
                await DoRefreshAsync();
                Dispatcher.Invoke(() =>
                {
                    lastRefreshUtc = DateTime.UtcNow;
                    UpdateLastRefreshDisplay();
                    refreshInProgress = false;
                    BusyIndicator.Visibility = Visibility.Collapsed;
                    UpdateRefreshReasonDisplay(lastRefreshReason);
                    try { BaseBranchCombo.IsEnabled = true; } catch { }
                    try { refreshButton.IsEnabled = true; } catch { }
                    if (refreshPending)
                    {
                        StartRefresh();
                    }
                });
            });
        }

        private async Task DoRefreshAsync()
        {
            // reuse existing Refresh_Click logic but without UI event args
            await Task.Run(() =>
            {
                try
                {
                    // Use currentWorkDir determined on load; pick selected remote from UI
                    string workDir = currentWorkDir;

                    if (string.IsNullOrWhiteSpace(workDir))
                    {
                        // Prefer to use the opened solution path when available
                        workDir = null;
                    }
                    try
                    {
                        var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                        var solutionPath = dte?.Solution?.FullName;
                        if (!string.IsNullOrWhiteSpace(solutionPath) && System.IO.File.Exists(solutionPath))
                        {
                            workDir = System.IO.Path.GetDirectoryName(solutionPath);
                        }
                    }
                    catch
                    {
                        // ignore and fallback
                    }

                    if (string.IsNullOrWhiteSpace(workDir))
                    {
                        var repoPath = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

                        // Try to find .git by walking up to solution root
                        var dir = new System.IO.DirectoryInfo(repoPath);
                        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, ".git")))
                        {
                            dir = dir.Parent;
                        }

                        if (dir == null)
                        {
                            UpdateStatus("No git repository found.");
                            return;
                        }

                        workDir = dir.FullName;
                    }

                    RunGit(workDir, "fetch --all --prune");

                    currentBranch = RunGit(workDir, "rev-parse --abbrev-ref HEAD");
                    if (string.IsNullOrWhiteSpace(currentBranch))
                    {
                        UpdateStatus("Unable to determine current branch.");
                        return;
                    }
                    currentBranch = currentBranch.Trim();

                    // Refresh base branch list from remote
                    PopulateBaseBranches(workDir, selectedRemote);
                    PopulateCheckoutBranches(workDir);

                    string baseBranch = null;
                    string selectedBase = null;
                    this.Dispatcher.Invoke(() =>
                    {
                        selectedBase = BaseBranchCombo.SelectedItem as string;
                    });

                    if (!string.IsNullOrWhiteSpace(selectedBase))
                    {
                        baseBranch = selectedBase;
                    }
                    else
                    {
                        // Build a candidate list: remote branches (origin/*) and common local main/master/develop
                        var candidates = new System.Collections.Generic.List<string>();

                        // remote branches (query origin directly)
                        var remoteList = RunGit(workDir, $"ls-remote --heads {selectedRemote}");
                        if (!string.IsNullOrWhiteSpace(remoteList))
                        {
                            using (var sr = new System.IO.StringReader(remoteList))
                            {
                                string ln;
                                while ((ln = sr.ReadLine()) != null)
                                {
                                    ln = ln.Trim();
                                    if (string.IsNullOrWhiteSpace(ln)) continue;
                                    var parts = ln.Split('\t');
                                    if (parts.Length == 2)
                                    {
                                        var refname = parts[1];
                                        if (refname.StartsWith("refs/heads/"))
                                        {
                                            var bname = refname.Substring("refs/heads/".Length);
                                            candidates.Add(selectedRemote + "/" + bname);
                                        }
                                    }
                                }
                            }
                        }

                        // add common branch names
                        candidates.AddRange(new[] { selectedRemote + "/main", selectedRemote + "/master", selectedRemote + "/develop", selectedRemote + "/dev", "main", "master", "develop", "dev" });

                        // remove duplicates and exclude current
                        var uniq = new System.Collections.Generic.HashSet<string>(candidates, System.StringComparer.OrdinalIgnoreCase);
                        uniq.Remove("origin/" + currentBranch);
                        uniq.Remove(currentBranch);

                        // For each candidate compute merge-base timestamp; pick the candidate with the most recent merge-base
                        long bestTs = 0;
                        string bestCandidate = null;
                        foreach (var cand in uniq)
                        {
                            if (string.IsNullOrWhiteSpace(cand)) continue;
                            // Ensure candidate exists: rev-parse
                            var exists = RunGit(workDir, $"rev-parse --verify {cand}");
                            if (string.IsNullOrWhiteSpace(exists) && exists.Trim().StartsWith("fatal")) continue;

                            var mergeBase = RunGit(workDir, $"merge-base {currentBranch} {cand}");
                            if (string.IsNullOrWhiteSpace(mergeBase)) continue;
                            mergeBase = mergeBase.Trim();

                            var tsStr = RunGit(workDir, $"show -s --format=%ct {mergeBase}");
                            if (long.TryParse((tsStr ?? string.Empty).Trim(), out long ts))
                            {
                                if (ts > bestTs)
                                {
                                    bestTs = ts;
                                    bestCandidate = cand;
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(bestCandidate)) baseBranch = bestCandidate;
                        else
                        {
                            // final fallback
                            var hasMain = RunGit(workDir, $"rev-parse --verify {selectedRemote}/main");
                            if (!string.IsNullOrWhiteSpace(hasMain)) baseBranch = selectedRemote + "/main";
                            else baseBranch = selectedRemote + "/master";
                        }
                    }

                    // show chosen base branch in UI
                    currentWorkDir = workDir;
                    currentRepoRoot = ResolveRepoRoot(workDir);
                    Dispatcher.Invoke(() =>
                    {
                        ApplySavedAutoRefreshSetting(currentRepoRoot);
                        RefreshAutoRefreshSubscriptions();
                    });
                    currentBaseBranch = baseBranch;

                    // If BaseBranchCombo has selection prefer that as the base branch
                    this.Dispatcher.Invoke(() =>
                    {
                        var selected = BaseBranchCombo.SelectedItem as string;
                        if (!string.IsNullOrWhiteSpace(selected))
                        {
                            currentBaseBranch = selected;
                        }
                    });

                    UpdateScopeLabels();

                    // Build scope maps for workspace, local, remote, main
                    scopeMap.Clear();

                    var workspaceTask = Task.Run(() => RunGit(workDir, "status --porcelain=v1"));
                    var originBranch = selectedRemote + "/" + currentBranch;
                    var baseExists = RefExists(workDir, currentBaseBranch);
                    var originExists = RefExists(workDir, originBranch);

                    var localTask = Task.Run(() =>
                    {
                        if (originExists)
                        {
                            // local commits not yet pushed
                            return RunGit(workDir, $"diff --name-status {originBranch}...{currentBranch}");
                        }
                        if (baseExists)
                        {
                            return RunGit(workDir, $"diff --name-status {currentBaseBranch}...{currentBranch}");
                        }
                        return string.Empty;
                    });
                    var remoteTask = Task.Run(() =>
                    {
                        if (baseExists && originExists)
                        {
                            // pushed commits not yet merged to the release branch
                            return RunGit(workDir, $"diff --name-status {currentBaseBranch}...{originBranch}");
                        }
                        return string.Empty;
                    });
                    var mainTask = Task.Run(() =>
                    {
                        currentMainRef = currentBaseBranch;
                        if (string.IsNullOrWhiteSpace(currentMainRef) || !baseExists) return string.Empty;
                        // overall delta vs the selected release branch
                        return RunGit(workDir, $"diff --name-status {currentBaseBranch}...{currentBranch}");
                    });

                    Task.WaitAll(workspaceTask, localTask, remoteTask, mainTask);

                    AddScopeStatusFromPorcelain(scopeMap, workspaceTask.Result, ChangeScope.Workspace);
                    AddScopeStatusFromNameStatus(scopeMap, localTask.Result, ChangeScope.Local);
                    AddScopeStatusFromNameStatus(scopeMap, remoteTask.Result, ChangeScope.Remote);
                    AddScopeStatusFromNameStatus(scopeMap, mainTask.Result, ChangeScope.Main);

                    UpdateListViews(scopeMap);
                }
                catch (System.Exception ex)
                {
                    UpdateStatus("Error: " + ex.Message);
                }
            });
        }

        // Remote selection removed; always use 'origin'

        private void BaseBranchCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var sel = BaseBranchCombo.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(sel) && !IsLoadingLabel(sel))
            {
                currentBaseBranch = sel;
                // persist selection for this repo
                try
                {
                    SaveSelectedBaseBranch(currentRepoRoot ?? currentWorkDir, sel);
                }
                catch { }
            }
            UpdateScopeLabels();
        }

        private void PopulateBaseBranches(string repoRoot, string remote)
        {
            if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(remote)) return;
            var branches = new System.Collections.Generic.List<string>();
            try
            {
                var outp = RunGit(repoRoot, $"for-each-ref refs/remotes/{remote} --format=%(refname:short)");
                if (IsGitRepoError(outp))
                {
                    branches.Add(LoadingLabel);
                    this.Dispatcher.Invoke(() =>
                    {
                        BaseBranchCombo.ItemsSource = branches;
                        BaseBranchCombo.SelectedIndex = 0;
                    });
                    return;
                }
                if (!string.IsNullOrWhiteSpace(outp))
                {
                    using (var sr = new System.IO.StringReader(outp))
                    {
                        string ln;
                        while ((ln = sr.ReadLine()) != null)
                        {
                            ln = ln.Trim();
                            if (string.IsNullOrWhiteSpace(ln)) continue;
                            var full = ln.Trim();
                            if (!string.IsNullOrWhiteSpace(full))
                            {
                                if (!branches.Contains(full)) branches.Add(full);
                            }
                        }
                    }
                }
            }
            catch { }

            this.Dispatcher.Invoke(() =>
            {
                BaseBranchCombo.ItemsSource = branches;
                // try to restore previously selected base for this repo
                var saved = LoadSelectedBaseBranch(repoRoot);
                if (!string.IsNullOrWhiteSpace(saved) && branches.Contains(saved))
                {
                    BaseBranchCombo.SelectedItem = saved;
                }
                else if (branches.Count > 0)
                {
                    BaseBranchCombo.SelectedIndex = 0;
                }
            });
        }

        private void PopulateCheckoutBranches(string repoRoot)
        {
            if (string.IsNullOrWhiteSpace(repoRoot)) return;
            var branches = new System.Collections.Generic.List<string>();
            try
            {
                var outp = RunGit(repoRoot, "for-each-ref refs/heads --format=%(refname:short)");
                if (IsGitRepoError(outp))
                {
                    SetCheckoutLoading();
                    return;
                }
                if (!string.IsNullOrWhiteSpace(outp))
                {
                    using (var sr = new System.IO.StringReader(outp))
                    {
                        string ln;
                        while ((ln = sr.ReadLine()) != null)
                        {
                            var name = ln.Trim();
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            branches.Add(name);
                        }
                    }
                }
            }
            catch { }

            branches.Sort(StringComparer.OrdinalIgnoreCase);
            var current = currentBranch;

            this.Dispatcher.Invoke(() =>
            {
                if (CheckoutBranchCombo == null) return;
                ignoreCheckoutSelection = true;
                if (branches.Count == 0)
                {
                    CheckoutBranchCombo.ItemsSource = new System.Collections.Generic.List<string> { LoadingLabel };
                    CheckoutBranchCombo.SelectedIndex = 0;
                }
                else
                {
                    CheckoutBranchCombo.ItemsSource = branches;
                    if (!string.IsNullOrWhiteSpace(current) && branches.Contains(current))
                    {
                        CheckoutBranchCombo.SelectedItem = current;
                    }
                    else if (branches.Count > 0)
                    {
                        CheckoutBranchCombo.SelectedIndex = 0;
                    }
                }
                ignoreCheckoutSelection = false;
            });
        }

        private void SetCheckoutLoading()
        {
            try
            {
                this.Dispatcher.Invoke(() =>
                {
                    if (CheckoutBranchCombo == null) return;
                    ignoreCheckoutSelection = true;
                    CheckoutBranchCombo.ItemsSource = new System.Collections.Generic.List<string> { LoadingLabel };
                    CheckoutBranchCombo.SelectedIndex = 0;
                    ignoreCheckoutSelection = false;
                });
            }
            catch { }
        }

        private void CheckoutBranchCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ignoreCheckoutSelection) return;
            try
            {
                var target = CheckoutBranchCombo.SelectedItem as string;
                if (string.IsNullOrWhiteSpace(target)) return;
                if (!string.IsNullOrWhiteSpace(currentBranch) && target.Equals(currentBranch, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(currentWorkDir)) return;
                WriteOutput($"Checking out {target}...");
                Task.Run(() =>
                {
                    var res = RunGit(currentWorkDir, $"checkout -q \"{target}\"");
                    if (IsGitError(res))
                    {
                        UpdateStatus("Checkout failed: " + res.Trim());
                        return;
                    }
                    currentBranch = target;
                    WriteOutput($"Checked out {target}.");
                    PopulateCheckoutBranches(currentWorkDir);
                    StartRefresh("Branch checkout");
                });
            }
            catch
            {
                WriteOutput("Checkout failed.");
            }
        }

        private void SaveSelectedBaseBranch(string repoRoot, string branch)
        {
            if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(branch) || IsLoadingLabel(branch)) return;
            try
            {
                var settings = LoadRepoSettings(repoRoot) ?? new RepoSettings();
                settings.BaseBranch = branch;
                SaveRepoSettings(repoRoot, settings);
            }
            catch { }
        }

        private string LoadSelectedBaseBranch(string repoRoot)
        {
            try
            {
                var settings = LoadRepoSettings(repoRoot);
                return string.IsNullOrWhiteSpace(settings?.BaseBranch) ? null : settings.BaseBranch.Trim();
            }
            catch { }
            return null;
        }

        private void SaveAutoRefreshSetting(string repoRoot, bool isEnabled)
        {
            if (string.IsNullOrWhiteSpace(repoRoot)) return;
            try
            {
                var settings = LoadRepoSettings(repoRoot) ?? new RepoSettings();
                settings.AutoRefreshEnabled = isEnabled;
                SaveRepoSettings(repoRoot, settings);
            }
            catch { }
        }

        private bool? LoadAutoRefreshSetting(string repoRoot)
        {
            try
            {
                return LoadRepoSettings(repoRoot)?.AutoRefreshEnabled;
            }
            catch { }
            return null;
        }

        private void ApplySavedAutoRefreshSetting(string repoRoot)
        {
            try
            {
                var saved = LoadAutoRefreshSetting(repoRoot);
                if (saved.HasValue && AutoRefreshCheckBox != null)
                {
                    AutoRefreshCheckBox.IsChecked = saved.Value;
                }
            }
            catch { }
        }

        private RepoSettings LoadRepoSettings(string repoRoot)
        {
            try
            {
                var file = GetRepoSettingsPath(repoRoot);
                if (System.IO.File.Exists(file))
                {
                    using (var stream = System.IO.File.OpenRead(file))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(RepoSettings));
                        return serializer.ReadObject(stream) as RepoSettings;
                    }
                }
            }
            catch
            {
                // fall back to legacy migration
            }

            return TryMigrateLegacyRepoSettings(repoRoot);
        }

        private void SaveRepoSettings(string repoRoot, RepoSettings settings)
        {
            if (string.IsNullOrWhiteSpace(repoRoot) || settings == null) return;
            try
            {
                var file = GetRepoSettingsPath(repoRoot);
                var dir = System.IO.Path.GetDirectoryName(file);
                if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                using (var stream = System.IO.File.Create(file))
                {
                    var serializer = new DataContractJsonSerializer(typeof(RepoSettings));
                    serializer.WriteObject(stream, settings);
                }
            }
            catch { }
        }

        private RepoSettings TryMigrateLegacyRepoSettings(string repoRoot)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(repoRoot)) return null;
                var settings = new RepoSettings();
                var hasAny = false;

                var baseBranch = LoadLegacyBaseBranchSetting(repoRoot);
                if (!string.IsNullOrWhiteSpace(baseBranch))
                {
                    settings.BaseBranch = baseBranch;
                    hasAny = true;
                }

                var autoRefresh = LoadLegacyAutoRefreshSetting(repoRoot);
                if (autoRefresh.HasValue)
                {
                    settings.AutoRefreshEnabled = autoRefresh.Value;
                    hasAny = true;
                }

                if (!hasAny) return null;

                SaveRepoSettings(repoRoot, settings);
                DeleteLegacyRepoSettings(repoRoot);
                return settings;
            }
            catch { }
            return null;
        }

        private string LoadLegacyBaseBranchSetting(string repoRoot)
        {
            try
            {
                var file = GetLegacyRepoSettingsPath(repoRoot, "basebranch");
                if (!System.IO.File.Exists(file)) return null;
                var txt = System.IO.File.ReadAllText(file);
                return string.IsNullOrWhiteSpace(txt) ? null : txt.Trim();
            }
            catch { }
            return null;
        }

        private bool? LoadLegacyAutoRefreshSetting(string repoRoot)
        {
            try
            {
                var file = GetLegacyRepoSettingsPath(repoRoot, "autorefresh");
                if (!System.IO.File.Exists(file)) return null;
                var txt = System.IO.File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(txt)) return null;
                txt = txt.Trim();
                if (txt == "1") return true;
                if (txt == "0") return false;
                if (bool.TryParse(txt, out var value)) return value;
            }
            catch { }
            return null;
        }

        private void DeleteLegacyRepoSettings(string repoRoot)
        {
            try
            {
                var baseFile = GetLegacyRepoSettingsPath(repoRoot, "basebranch");
                if (System.IO.File.Exists(baseFile)) System.IO.File.Delete(baseFile);
            }
            catch { }

            try
            {
                var autoFile = GetLegacyRepoSettingsPath(repoRoot, "autorefresh");
                if (System.IO.File.Exists(autoFile)) System.IO.File.Delete(autoFile);
            }
            catch { }
        }

        private static string GetRepoSettingsPath(string repoRoot)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = System.IO.Path.Combine(appData, "KevGitChanges");
            var normalizedRoot = NormalizeRepoSettingsKey(repoRoot);
            var key = normalizedRoot.ToLowerInvariant();
            var hash = ComputeSha1(key);
            var repoName = System.IO.Path.GetFileName(normalizedRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            repoName = SanitizeFileName(string.IsNullOrWhiteSpace(repoName) ? "repo" : repoName);
            return System.IO.Path.Combine(dir, $"{repoName}.{hash}.settings.json");
        }

        private static string GetLegacyRepoSettingsPath(string repoRoot, string settingName)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = System.IO.Path.Combine(appData, "KevGitChanges");
            var normalizedRoot = NormalizeRepoSettingsKey(repoRoot);
            var key = normalizedRoot.ToLowerInvariant();
            var hash = ComputeSha1(key);
            var repoName = System.IO.Path.GetFileName(normalizedRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            repoName = SanitizeFileName(string.IsNullOrWhiteSpace(repoName) ? "repo" : repoName);
            var suffix = SanitizeFileName(string.IsNullOrWhiteSpace(settingName) ? "setting" : settingName);
            return System.IO.Path.Combine(dir, $"{repoName}.{hash}.{suffix}");
        }

        private static string NormalizeRepoSettingsKey(string repoRoot)
        {
            if (string.IsNullOrWhiteSpace(repoRoot)) return string.Empty;
            try
            {
                return ResolveRepoRootStatic(repoRoot) ?? repoRoot.Trim();
            }
            catch
            {
                return repoRoot.Trim();
            }
        }

        private static string ComputeSha1(string input)
        {
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(input ?? string.Empty);
                var hash = sha1.ComputeHash(bytes);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var ch in name)
            {
                sb.Append(System.Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }
            return sb.ToString();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            // Use StartRefresh to run refresh with busy indicator
            StartRefresh("Manual");
        }

        private static string ResolveRepoRootStatic(string workDir)
        {
            if (string.IsNullOrWhiteSpace(workDir)) return null;
            try
            {
                var dir = new System.IO.DirectoryInfo(workDir);
                if (!dir.Exists) return workDir;
                while (dir != null)
                {
                    if (System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, ".git")))
                    {
                        return dir.FullName;
                    }
                    dir = dir.Parent;
                }
            }
            catch
            {
                // ignore
            }
            return workDir;
        }

        private void AutoRefreshCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            SaveAutoRefreshSetting(currentRepoRoot, AutoRefreshCheckBox?.IsChecked == true);
            RefreshAutoRefreshSubscriptions();
        }

        private void EnsureVsActivityHooks()
        {
            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte == null) return;

                if (buildEvents == null)
                {
                    buildEvents = dte.Events.BuildEvents;
                    buildEvents.OnBuildBegin += BuildEvents_OnBuildBegin;
                    buildEvents.OnBuildDone += BuildEvents_OnBuildDone;
                }

                if (debuggerEvents == null)
                {
                    debuggerEvents = dte.Events.DebuggerEvents;
                    debuggerEvents.OnEnterRunMode += DebuggerEvents_OnEnterRunMode;
                    debuggerEvents.OnEnterBreakMode += DebuggerEvents_OnEnterBreakMode;
                    debuggerEvents.OnEnterDesignMode += DebuggerEvents_OnEnterDesignMode;
                }

                UpdateVsActivityState();
            }
            catch { }
        }

        private void UnhookVsActivityEvents()
        {
            try
            {
                if (buildEvents != null)
                {
                    buildEvents.OnBuildBegin -= BuildEvents_OnBuildBegin;
                    buildEvents.OnBuildDone -= BuildEvents_OnBuildDone;
                    buildEvents = null;
                }
            }
            catch { }

            try
            {
                if (debuggerEvents != null)
                {
                    debuggerEvents.OnEnterRunMode -= DebuggerEvents_OnEnterRunMode;
                    debuggerEvents.OnEnterBreakMode -= DebuggerEvents_OnEnterBreakMode;
                    debuggerEvents.OnEnterDesignMode -= DebuggerEvents_OnEnterDesignMode;
                    debuggerEvents = null;
                }
            }
            catch { }
        }

        private void BuildEvents_OnBuildBegin(EnvDTE.vsBuildScope Scope, EnvDTE.vsBuildAction Action)
        {
            isVsBuildActive = true;
        }

        private void BuildEvents_OnBuildDone(EnvDTE.vsBuildScope Scope, EnvDTE.vsBuildAction Action)
        {
            isVsBuildActive = false;
            TryResumeDeferredAutoRefresh();
        }

        private void DebuggerEvents_OnEnterRunMode(EnvDTE.dbgEventReason Reason)
        {
            isVsDebugActive = true;
        }

        private void DebuggerEvents_OnEnterBreakMode(EnvDTE.dbgEventReason Reason, ref EnvDTE.dbgExecutionAction ExecutionAction)
        {
            isVsDebugActive = true;
        }

        private void DebuggerEvents_OnEnterDesignMode(EnvDTE.dbgEventReason Reason)
        {
            isVsDebugActive = false;
            TryResumeDeferredAutoRefresh();
        }

        private void UpdateVsActivityState()
        {
            try
            {
                var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte == null) return;

                try
                {
                    isVsBuildActive = dte.Solution?.SolutionBuild?.BuildState == EnvDTE.vsBuildState.vsBuildStateInProgress;
                }
                catch { }

                try
                {
                    isVsDebugActive = dte.Debugger != null && dte.Debugger.CurrentMode != EnvDTE.dbgDebugMode.dbgDesignMode;
                }
                catch { }
            }
            catch { }
        }

        private bool IsAutoRefreshBlockedByVsActivity()
        {
            UpdateVsActivityState();
            return isVsBuildActive || isVsDebugActive;
        }

        private bool TryStartAutoRefresh(string reason)
        {
            if (IsAutoRefreshBlockedByVsActivity())
            {
                autoRefreshDeferredByVsState = true;
                deferredAutoRefreshReason = string.IsNullOrWhiteSpace(reason) ? "Auto refresh deferred" : reason;
                UpdateRefreshReasonDisplay(isVsBuildActive ? "deferred during build" : "deferred during debug");
                return false;
            }

            autoRefreshDeferredByVsState = false;
            deferredAutoRefreshReason = null;
            StartRefresh(reason);
            return true;
        }

        private void TryResumeDeferredAutoRefresh()
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (!autoRefreshDeferredByVsState || AutoRefreshCheckBox?.IsChecked != true) return;
                if (IsAutoRefreshBlockedByVsActivity()) return;

                autoRefreshDeferredByVsState = false;
                var reason = string.IsNullOrWhiteSpace(deferredAutoRefreshReason) ? "Deferred auto refresh" : deferredAutoRefreshReason;
                deferredAutoRefreshReason = null;
                StartRefresh(reason);
            }));
        }

        private void UpdateLastRefreshDisplay()
        {
            if (LastRefreshText == null) return;
            LastRefreshText.Text = lastRefreshUtc == DateTime.MinValue
                ? string.Empty
                : "Last refreshed " + lastRefreshUtc.ToLocalTime().ToString("h:mm tt").ToLowerInvariant();
        }

        private void UpdateRefreshReasonDisplay(string reason)
        {
            if (RefreshReasonText == null) return;
            RefreshReasonText.Text = string.IsNullOrWhiteSpace(reason)
                ? string.Empty
                : "Trigger: " + reason;
        }

        private void EnsureAutoRefreshInfrastructure()
        {
            if (workspaceRefreshDebounceTimer == null)
            {
                workspaceRefreshDebounceTimer = new System.Windows.Threading.DispatcherTimer();
                workspaceRefreshDebounceTimer.Interval = WorkspaceRefreshDebounce;
                workspaceRefreshDebounceTimer.Tick += WorkspaceRefreshDebounceTimer_Tick;
            }

            if (periodicRefreshTimer == null)
            {
                periodicRefreshTimer = new System.Windows.Threading.DispatcherTimer();
                periodicRefreshTimer.Interval = PeriodicRefreshCheckInterval;
                periodicRefreshTimer.Tick += PeriodicRefreshTimer_Tick;
            }
        }

        private void StopAutoRefreshInfrastructure()
        {
            try
            {
                if (workspaceRefreshDebounceTimer != null)
                {
                    workspaceRefreshDebounceTimer.Stop();
                }
                if (periodicRefreshTimer != null)
                {
                    periodicRefreshTimer.Stop();
                }
                if (workspaceWatcher != null)
                {
                    workspaceWatcher.EnableRaisingEvents = false;
                    workspaceWatcher.Created -= WorkspaceWatcher_Changed;
                    workspaceWatcher.Changed -= WorkspaceWatcher_Changed;
                    workspaceWatcher.Deleted -= WorkspaceWatcher_Changed;
                    workspaceWatcher.Renamed -= WorkspaceWatcher_Renamed;
                    workspaceWatcher.Dispose();
                    workspaceWatcher = null;
                }
                if (gitMetadataWatcher != null)
                {
                    gitMetadataWatcher.EnableRaisingEvents = false;
                    gitMetadataWatcher.Created -= GitMetadataWatcher_Changed;
                    gitMetadataWatcher.Changed -= GitMetadataWatcher_Changed;
                    gitMetadataWatcher.Deleted -= GitMetadataWatcher_Changed;
                    gitMetadataWatcher.Renamed -= GitMetadataWatcher_Renamed;
                    gitMetadataWatcher.Dispose();
                    gitMetadataWatcher = null;
                }
            }
            catch
            {
                // ignore watcher shutdown failures
            }
        }

        private void RefreshAutoRefreshSubscriptions()
        {
            if (!IsLoaded || !IsVisible || AutoRefreshCheckBox?.IsChecked != true || string.IsNullOrWhiteSpace(currentRepoRoot) || !System.IO.Directory.Exists(currentRepoRoot))
            {
                StopAutoRefreshInfrastructure();
                return;
            }

            EnsureAutoRefreshInfrastructure();

            try
            {
                if (workspaceWatcher == null || !string.Equals(workspaceWatcher.Path, currentRepoRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (workspaceWatcher != null)
                    {
                        workspaceWatcher.EnableRaisingEvents = false;
                        workspaceWatcher.Created -= WorkspaceWatcher_Changed;
                        workspaceWatcher.Changed -= WorkspaceWatcher_Changed;
                        workspaceWatcher.Deleted -= WorkspaceWatcher_Changed;
                        workspaceWatcher.Renamed -= WorkspaceWatcher_Renamed;
                        workspaceWatcher.Dispose();
                    }

                    workspaceWatcher = new System.IO.FileSystemWatcher(currentRepoRoot)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.DirectoryName | System.IO.NotifyFilters.LastWrite
                    };
                    workspaceWatcher.Created += WorkspaceWatcher_Changed;
                    workspaceWatcher.Changed += WorkspaceWatcher_Changed;
                    workspaceWatcher.Deleted += WorkspaceWatcher_Changed;
                    workspaceWatcher.Renamed += WorkspaceWatcher_Renamed;
                }

                workspaceWatcher.EnableRaisingEvents = true;
                EnsureGitMetadataWatcher();
                periodicRefreshTimer?.Start();
            }
            catch (Exception ex)
            {
                WriteOutput("Auto refresh setup failed: " + ex.Message);
            }
        }

        private void WorkspaceWatcher_Changed(object sender, System.IO.FileSystemEventArgs e)
        {
            if (ShouldIgnoreWorkspacePath(e.FullPath)) return;
            QueueWorkspaceRefresh(GetWorkspaceRefreshReason(e.ChangeType, e.FullPath));
        }

        private void WorkspaceWatcher_Renamed(object sender, System.IO.RenamedEventArgs e)
        {
            if (!ShouldIgnoreWorkspacePath(e.OldFullPath) || !ShouldIgnoreWorkspacePath(e.FullPath))
            {
                QueueWorkspaceRefresh(GetWorkspaceRenameRefreshReason(e.OldFullPath, e.FullPath));
            }
        }

        private void GitMetadataWatcher_Changed(object sender, System.IO.FileSystemEventArgs e)
        {
            if (DateTime.UtcNow < suppressGitMetadataEventsUntilUtc) return;
            if (!ShouldRefreshForGitMetadataPath(e.FullPath)) return;
            QueueWorkspaceRefresh(GetGitRefreshReason(e.ChangeType, e.FullPath));
        }

        private void GitMetadataWatcher_Renamed(object sender, System.IO.RenamedEventArgs e)
        {
            if (DateTime.UtcNow < suppressGitMetadataEventsUntilUtc) return;
            if (ShouldRefreshForGitMetadataPath(e.OldFullPath) || ShouldRefreshForGitMetadataPath(e.FullPath))
            {
                QueueWorkspaceRefresh(GetGitRenameRefreshReason(e.OldFullPath, e.FullPath));
            }
        }

        private void QueueWorkspaceRefresh(string reason)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (!IsLoaded || !IsVisible) return;
                if (IsAutoRefreshBlockedByVsActivity())
                {
                    autoRefreshDeferredByVsState = true;
                    deferredAutoRefreshReason = string.IsNullOrWhiteSpace(reason) ? "File change" : reason;
                    UpdateRefreshReasonDisplay(isVsBuildActive ? "deferred during build" : "deferred during debug");
                    return;
                }
                EnsureAutoRefreshInfrastructure();
                pendingRefreshReason = string.IsNullOrWhiteSpace(reason) ? "File change" : reason;
                UpdateRefreshReasonDisplay(pendingRefreshReason);
                workspaceRefreshDebounceTimer.Stop();
                workspaceRefreshDebounceTimer.Start();
            }));
        }

        private void WorkspaceRefreshDebounceTimer_Tick(object sender, EventArgs e)
        {
            workspaceRefreshDebounceTimer.Stop();
            TryStartAutoRefresh(pendingRefreshReason);
        }

        private void PeriodicRefreshTimer_Tick(object sender, EventArgs e)
        {
            if (!IsLoaded || !IsVisible || refreshInProgress) return;
            if (lastRefreshUtc != DateTime.MinValue && (DateTime.UtcNow - lastRefreshUtc) < PeriodicRefreshThreshold) return;
            TryStartAutoRefresh("Periodic");
        }

        private bool ShouldIgnoreWorkspacePath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return true;

            var normalized = fullPath.Replace('\\', '/');
            var segments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase)) return true;
                if (segment.Equals(".vs", StringComparison.OrdinalIgnoreCase)) return true;
                if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase)) return true;
                if (segment.Equals("obj", StringComparison.OrdinalIgnoreCase)) return true;
                if (segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) return true;
            }

            var fileName = System.IO.Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
            if (fileName.EndsWith(".user", StringComparison.OrdinalIgnoreCase)) return true;
            if (fileName.EndsWith(".suo", StringComparison.OrdinalIgnoreCase)) return true;
            if (IsGitIgnoredPath(fullPath)) return true;

            return false;
        }

        private bool IsGitIgnoredPath(string fullPath)
        {
            try
            {
                var repoRoot = !string.IsNullOrWhiteSpace(currentRepoRoot) ? currentRepoRoot : currentWorkDir;
                if (string.IsNullOrWhiteSpace(repoRoot) || !System.IO.Directory.Exists(repoRoot) || string.IsNullOrWhiteSpace(fullPath))
                {
                    return false;
                }

                var relativePath = GetRelativePath(repoRoot, fullPath);
                if (string.IsNullOrWhiteSpace(relativePath)) return false;

                int exitCode;
                RunGit(repoRoot, $"check-ignore -q -- \"{relativePath}\"", out exitCode);
                return exitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static string GetRelativePath(string rootPath, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(fullPath)) return null;

            var rootUri = new Uri(AppendDirectorySeparator(rootPath));
            var pathUri = new Uri(fullPath);
            if (!rootUri.IsBaseOf(pathUri)) return null;

            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', '\\');
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            return path.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + System.IO.Path.DirectorySeparatorChar;
        }

        private string GetWorkspaceRefreshReason(System.IO.WatcherChangeTypes changeType, string fullPath)
        {
            return "Workspace " + changeType.ToString().ToLowerInvariant() + ": " + FormatRefreshPath(fullPath);
        }

        private string GetWorkspaceRenameRefreshReason(string oldFullPath, string newFullPath)
        {
            return "Workspace renamed: " + FormatRefreshPath(oldFullPath) + " -> " + FormatRefreshPath(newFullPath);
        }

        private string GetGitRefreshReason(System.IO.WatcherChangeTypes changeType, string fullPath)
        {
            return ".git " + changeType.ToString().ToLowerInvariant() + ": " + FormatRefreshPath(fullPath);
        }

        private string GetGitRenameRefreshReason(string oldFullPath, string newFullPath)
        {
            return ".git renamed: " + FormatRefreshPath(oldFullPath) + " -> " + FormatRefreshPath(newFullPath);
        }

        private string FormatRefreshPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return "(unknown)";

            var repoRoot = !string.IsNullOrWhiteSpace(currentRepoRoot) ? currentRepoRoot : currentWorkDir;
            var relativePath = GetRelativePath(repoRoot, fullPath);
            return string.IsNullOrWhiteSpace(relativePath) ? System.IO.Path.GetFileName(fullPath) : relativePath.Replace('\\', '/');
        }

        private void EnsureGitMetadataWatcher()
        {
            var gitDir = ResolveGitMetadataRoot(currentWorkDir);
            if (string.IsNullOrWhiteSpace(gitDir) || !System.IO.Directory.Exists(gitDir))
            {
                if (gitMetadataWatcher != null)
                {
                    gitMetadataWatcher.EnableRaisingEvents = false;
                }
                return;
            }

            if (gitMetadataWatcher == null || !string.Equals(gitMetadataWatcher.Path, gitDir, StringComparison.OrdinalIgnoreCase))
            {
                if (gitMetadataWatcher != null)
                {
                    gitMetadataWatcher.EnableRaisingEvents = false;
                    gitMetadataWatcher.Created -= GitMetadataWatcher_Changed;
                    gitMetadataWatcher.Changed -= GitMetadataWatcher_Changed;
                    gitMetadataWatcher.Deleted -= GitMetadataWatcher_Changed;
                    gitMetadataWatcher.Renamed -= GitMetadataWatcher_Renamed;
                    gitMetadataWatcher.Dispose();
                }

                gitMetadataWatcher = new System.IO.FileSystemWatcher(gitDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.DirectoryName | System.IO.NotifyFilters.LastWrite
                };
                gitMetadataWatcher.Created += GitMetadataWatcher_Changed;
                gitMetadataWatcher.Changed += GitMetadataWatcher_Changed;
                gitMetadataWatcher.Deleted += GitMetadataWatcher_Changed;
                gitMetadataWatcher.Renamed += GitMetadataWatcher_Renamed;
            }

            gitMetadataWatcher.EnableRaisingEvents = true;
        }

        private string ResolveGitMetadataRoot(string workDir)
        {
            if (string.IsNullOrWhiteSpace(workDir)) return null;
            var gitDir = RunGit(workDir, "rev-parse --git-dir");
            if (string.IsNullOrWhiteSpace(gitDir)) return null;

            var trimmed = gitDir.Trim();
            if (trimmed.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (System.IO.Path.IsPathRooted(trimmed)) return trimmed;
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(workDir, trimmed));
        }

        private bool ShouldRefreshForGitMetadataPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return false;

            var normalized = fullPath.Replace('\\', '/');
            if (normalized.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.EndsWith("/index", StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.EndsWith("/packed-refs", StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.IndexOf("/refs/heads/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (normalized.IndexOf("/refs/remotes/", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (normalized.IndexOf("/logs/HEAD", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private class ChangeItem
        {
            public string Path { get; set; }
            public string WStatus { get; set; }
            public string LStatus { get; set; }
            public string RStatus { get; set; }
            public string MStatus { get; set; }
        }

        // TreeNode is defined in TreeNode.cs as a public type for XAML access

        private void UpdateListViews(System.Collections.Generic.IDictionary<string, System.Collections.Generic.Dictionary<ChangeScope, string>> scopes)
        {
            var localItems = new System.Collections.Generic.List<ChangeItem>();
            CaptureFilterState();

            if (scopes != null)
            {
                var sortedPaths = new System.Collections.Generic.List<string>(scopes.Keys);
                sortedPaths.Sort(System.StringComparer.OrdinalIgnoreCase);
                foreach (var line in sortedPaths)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var trimmed = line.Trim();
                    var nodeScopes = scopes[trimmed];
                    var item = BuildChangeItem(trimmed, nodeScopes);
                    if (item == null) continue;
                    localItems.Add(item);
                }
            }

            var localTree = BuildTree(localItems);

            this.Dispatcher.Invoke(() =>
            {
                if (LocalTree != null) LocalTree.ItemsSource = localTree;
                if (LocalCount != null) LocalCount.Text = $"({localItems.Count})";
            });
        }

        private void CaptureFilterState()
        {
            try
            {
                this.Dispatcher.Invoke(() =>
                {
                    showWorkspace = ShowWorkspace == null || ShowWorkspace.IsChecked == true;
                    showLocal = ShowLocal == null || ShowLocal.IsChecked == true;
                    showRemote = ShowRemote == null || ShowRemote.IsChecked == true;
                });
            }
            catch
            {
                showWorkspace = showLocal = showRemote = true;
            }
        }

        private static void AddScopeStatusFromNameStatus(System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<ChangeScope, string>> target, string payload, ChangeScope scope)
        {
            if (target == null || string.IsNullOrWhiteSpace(payload)) return;
            var trimmedPayload = payload.TrimStart();
            if (trimmedPayload.StartsWith("fatal:", System.StringComparison.OrdinalIgnoreCase) ||
                trimmedPayload.StartsWith("error:", System.StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            using (var sr = new System.IO.StringReader(payload))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("fatal:", System.StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("error:", System.StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    var statusToken = parts[0];
                    var statusChar = statusToken.Length > 0 ? statusToken[0] : '?';
                    var path = parts[parts.Length - 1];
                    AddScopeStatus(target, path, scope, MapStatusChar(statusChar));
                }
            }
        }

        private static void AddScopeStatusFromPorcelain(System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<ChangeScope, string>> target, string payload, ChangeScope scope)
        {
            if (target == null || string.IsNullOrWhiteSpace(payload)) return;
            using (var sr = new System.IO.StringReader(payload))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.Length < 4) continue;
                    var statusPair = line.Substring(0, 2);
                    var path = line.Substring(3).Trim();
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var statusChar = ExtractPorcelainStatus(statusPair);
                    AddScopeStatus(target, path, scope, MapStatusChar(statusChar));
                }
            }
        }

        private static void AddScopeStatus(System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<ChangeScope, string>> target, string path, ChangeScope scope, string status)
        {
            if (target == null || string.IsNullOrWhiteSpace(path)) return;
            if (!target.TryGetValue(path, out var map))
            {
                map = new System.Collections.Generic.Dictionary<ChangeScope, string>();
                target[path] = map;
            }
            map[scope] = status;
        }

        private ChangeItem BuildChangeItem(string path, System.Collections.Generic.Dictionary<ChangeScope, string> scopes)
        {
            if (scopes == null) return null;
            var item = new ChangeItem { Path = path };
            var any = false;

            if (ShouldShowScope(ChangeScope.Workspace) && scopes.TryGetValue(ChangeScope.Workspace, out var wStatus))
            {
                item.WStatus = wStatus;
                any = true;
            }
            if (ShouldShowScope(ChangeScope.Local) && scopes.TryGetValue(ChangeScope.Local, out var lStatus))
            {
                item.LStatus = lStatus;
                any = true;
            }
            if (ShouldShowScope(ChangeScope.Remote) && scopes.TryGetValue(ChangeScope.Remote, out var rStatus))
            {
                item.RStatus = rStatus;
                any = true;
            }
            return any ? item : null;
        }

        private static char ExtractPorcelainStatus(string statusPair)
        {
            if (string.IsNullOrWhiteSpace(statusPair)) return '?';
            if (statusPair.Length >= 2 && statusPair[0] == '?' && statusPair[1] == '?') return 'A';
            if (statusPair[0] != ' ' && statusPair[0] != '\t') return statusPair[0];
            if (statusPair.Length >= 2) return statusPair[1];
            return '?';
        }

        private static string MapStatusChar(char status)
        {
            switch (status)
            {
                case 'A':
                    return "A";

                case 'M':
                    return "M";

                case 'D':
                    return "D";

                case 'R':
                    return "R";

                case 'C':
                    return "C";

                case 'U':
                    return "U";

                case 'T':
                    return "T";

                default:
                    return "?";
            }
        }

        private static Brush StatusBrush(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return null;
            switch (status)
            {
                case "A":
                    return StatusAddedBrush;

                case "M":
                    return StatusModifiedBrush;

                case "D":
                    return StatusDeletedBrush;

                default:
                    return StatusOtherBrush;
            }
        }

        private bool ShouldShowScope(ChangeScope scope)
        {
            switch (scope)
            {
                case ChangeScope.Workspace:
                    return showWorkspace;

                case ChangeScope.Local:
                    return showLocal;

                case ChangeScope.Remote:
                    return showRemote;

                default:
                    return true;
            }
        }

        private string GetMainRef(string workDir)
        {
            if (!string.IsNullOrWhiteSpace(currentBaseBranch))
            {
                return IsLoadingLabel(currentBaseBranch) ? null : currentBaseBranch;
            }
            var mainRef = selectedRemote + "/main";
            var hasMain = RunGit(workDir, $"rev-parse --verify {mainRef}");
            if (!string.IsNullOrWhiteSpace(hasMain) && !hasMain.TrimStart().StartsWith("fatal:"))
            {
                return mainRef;
            }
            var masterRef = selectedRemote + "/master";
            var hasMaster = RunGit(workDir, $"rev-parse --verify {masterRef}");
            if (!string.IsNullOrWhiteSpace(hasMaster) && !hasMaster.TrimStart().StartsWith("fatal:"))
            {
                return masterRef;
            }
            return null;
        }

        private bool RefExists(string workDir, string refName)
        {
            if (string.IsNullOrWhiteSpace(workDir) || string.IsNullOrWhiteSpace(refName)) return false;
            var res = RunGit(workDir, $"rev-parse --verify {refName}");
            if (string.IsNullOrWhiteSpace(res)) return false;
            return !res.TrimStart().StartsWith("fatal:", System.StringComparison.OrdinalIgnoreCase);
        }

        private System.Collections.Generic.List<TreeNode> BuildTree(System.Collections.Generic.List<ChangeItem> items)
        {
            var roots = new System.Collections.Generic.List<TreeNode>();
            var map = new System.Collections.Generic.Dictionary<string, TreeNode>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                var parts = it.Path.Split(new[] { '/', '\\' }, System.StringSplitOptions.RemoveEmptyEntries);
                string accum = string.Empty;
                System.Collections.Generic.List<TreeNode> currentList = roots;
                TreeNode parent = null;
                for (int i = 0; i < parts.Length; i++)
                {
                    var part = parts[i];
                    accum = string.IsNullOrEmpty(accum) ? part : (accum + "/" + part);
                    if (!map.TryGetValue(accum, out var node))
                    {
                        node = new TreeNode { Name = part };
                        map[accum] = node;
                        currentList.Add(node);
                    }
                    parent = node;
                    currentList = node.Children;
                    if (i == parts.Length - 1)
                    {
                        node.FullPath = it.Path;
                        node.WStatus = it.WStatus;
                        node.LStatus = it.LStatus;
                        node.RStatus = it.RStatus;
                        node.MStatus = it.MStatus;
                        node.WBrush = StatusBrush(it.WStatus);
                        node.LBrush = StatusBrush(it.LStatus);
                        node.RBrush = StatusBrush(it.RStatus);
                        node.MBrush = StatusBrush(it.MStatus);
                        node.WToolTip = GetScopeDisplay("Workspace");
                        node.LToolTip = GetScopeDisplay("Local");
                        node.RToolTip = GetScopeDisplay("Remote");
                        node.MToolTip = GetScopeDisplay("Main");
                    }
                }
            }
            CompressTree(roots);
            foreach (var root in roots)
            {
                ApplyNodeVisuals(root);
            }
            SortTree(roots);
            AssignDepth(roots, 0);
            return roots;
        }

        private void AssignDepth(System.Collections.Generic.List<TreeNode> nodes, int depth)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
            {
                node.Depth = depth;
                if (node.Children.Count > 0)
                {
                    AssignDepth(node.Children, depth + 1);
                }
            }
        }

        private void SortTree(System.Collections.Generic.List<TreeNode> nodes)
        {
            if (nodes == null) return;
            nodes.Sort((a, b) =>
            {
                if (a == null && b == null) return 0;
                if (a == null) return 1;
                if (b == null) return -1;

                var aIsFile = a.IsFile;
                var bIsFile = b.IsFile;
                if (aIsFile != bIsFile)
                {
                    return aIsFile ? 1 : -1; // folders first
                }

                return string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase);
            });

            foreach (var node in nodes)
            {
                SortTree(node.Children);
            }
        }

        private void CompressTree(System.Collections.Generic.List<TreeNode> roots)
        {
            if (roots == null) return;
            foreach (var node in roots)
            {
                CompressNode(node);
            }
        }

        private void CompressNode(TreeNode node)
        {
            if (node == null) return;
            for (int i = 0; i < node.Children.Count; i++)
            {
                CompressNode(node.Children[i]);
            }

            while (node.FullPath == null && node.Children.Count == 1 && !node.Children[0].IsFile)
            {
                var child = node.Children[0];
                node.Name = node.Name + "/" + child.Name;
                node.Children.Clear();
                node.Children.AddRange(child.Children);
            }
        }

        private void ApplyNodeVisuals(TreeNode node)
        {
            if (node == null) return;
            node.IsExpanded = node.Children.Count > 0;
            node.Icon = node.IsFile ? fileIcon : folderIcon;
            node.IconMoniker = node.IsFile ? KnownMonikers.TextFile : KnownMonikers.FolderClosed;
            if (iconDiagnosticsCount < 1)
            {
                iconDiagnosticsCount++;
                WriteOutput($"Icon diagnostics: folder={DescribeImage(folderIcon)}, file={DescribeImage(fileIcon)}");
            }
            foreach (var child in node.Children)
            {
                ApplyNodeVisuals(child);
            }
        }

        private void EnsureIcons()
        {
            if (folderIcon != null && fileIcon != null)
            {
                if (!iconDiagnosticsLogged)
                {
                    iconDiagnosticsLogged = true;
                    WriteOutput("Icon diagnostics: already set before EnsureIcons.");
                }
                return;
            }
            folderIcon = GetVsMonikerImage(KnownMonikers.FolderClosed, "FolderClosed");
            fileIcon = GetVsMonikerImage(KnownMonikers.Document, "Document");
            if (folderIcon == null) folderIcon = CreateFolderIcon();
            if (fileIcon == null) fileIcon = GetShellIcon(false);
            if (folderIcon == null) folderIcon = fileIcon;
            if (!iconDiagnosticsLogged)
            {
                iconDiagnosticsLogged = true;
                WriteOutput($"Icon diagnostics: folder={DescribeImage(folderIcon)}, file={DescribeImage(fileIcon)}");
            }
        }

        private static string DescribeImage(ImageSource image)
        {
            if (image == null) return "null";
            return image.GetType().Name;
        }

        private ImageSource GetVsMonikerImage(ImageMoniker moniker, string name)
        {
            try
            {
                ImageSource result = null;
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var imageService = Package.GetGlobalService(typeof(SVsImageService)) as IVsImageService2;
                    if (imageService == null)
                    {
                        WriteOutput("Icon diagnostics: SVsImageService2 unavailable.");
                        return;
                    }

                    WriteOutput($"Icon diagnostics: resolving {name} moniker (Id={moniker.Id}).");
                    var attrs1 = new ImageAttributes();
                    attrs1.StructSize = Marshal.SizeOf(typeof(ImageAttributes));
                    attrs1.ImageType = (uint)_UIImageType.IT_Bitmap;
                    attrs1.Format = (uint)_UIDataFormat.DF_WPF;
                    attrs1.LogicalWidth = 16;
                    attrs1.LogicalHeight = 16;
                    attrs1.Flags = (uint)_ImageAttributesFlags.IAF_RequiredFlags;
                    if (TryGetMonikerImage(imageService, moniker, attrs1, name, "bitmap", out var img1))
                    {
                        result = img1;
                        return;
                    }

                    var attrs2 = new ImageAttributes();
                    attrs2.StructSize = Marshal.SizeOf(typeof(ImageAttributes));
                    attrs2.ImageType = (uint)_UIImageType.IT_Icon;
                    attrs2.Format = (uint)_UIDataFormat.DF_WPF;
                    attrs2.LogicalWidth = 16;
                    attrs2.LogicalHeight = 16;
                    attrs2.Flags = (uint)_ImageAttributesFlags.IAF_RequiredFlags;
                    if (TryGetMonikerImage(imageService, moniker, attrs2, name, "icon", out var img2))
                    {
                        result = img2;
                        return;
                    }
                });
                return result;
            }
            catch
            {
                WriteOutput("Icon diagnostics: exception while resolving moniker.");
                return null;
            }
        }

        private bool TryGetMonikerImage(IVsImageService2 imageService, ImageMoniker moniker, ImageAttributes attrs, string name, string flavor, out ImageSource img)
        {
            img = null;
            try
            {
                var uiObj = imageService.GetImage(moniker, attrs);
                if (uiObj == null)
                {
                    WriteOutput($"Icon diagnostics: {name} moniker returned null ({flavor}).");
                    return false;
                }
                uiObj.get_Data(out object data);
                if (data is ImageSource imgSrc)
                {
                    img = imgSrc;
                    WriteOutput($"Icon diagnostics: {name} moniker resolved ({flavor}) as ImageSource.");
                    return true;
                }
                if (data is System.Drawing.Bitmap bmp)
                {
                    var hBmp = bmp.GetHbitmap();
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHBitmap(hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
                        if (src.CanFreeze) src.Freeze();
                        img = src;
                        WriteOutput($"Icon diagnostics: {name} moniker resolved ({flavor}) as Bitmap.");
                        return true;
                    }
                    finally
                    {
                        DeleteObject(hBmp);
                    }
                }
                if (data is System.Drawing.Icon ico)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
                    if (src.CanFreeze) src.Freeze();
                    img = src;
                    WriteOutput($"Icon diagnostics: {name} moniker resolved ({flavor}) as Icon.");
                    return true;
                }
                var typeName = data == null ? "null" : data.GetType().Name;
                WriteOutput($"Icon diagnostics: {name} moniker data not ImageSource ({flavor}): {typeName}.");
                return false;
            }
            catch (Exception ex)
            {
                WriteOutput($"Icon diagnostics: {name} moniker exception ({flavor}): {ex.Message}");
                return false;
            }
        }

        private static ImageSource CreateFolderIcon()
        {
            try
            {
                var border = new SolidColorBrush(Color.FromRgb(194, 154, 0));
                var tabFill = new SolidColorBrush(Color.FromRgb(255, 224, 102));
                var bodyFill = new SolidColorBrush(Color.FromRgb(255, 209, 77));
                border.Freeze();
                tabFill.Freeze();
                bodyFill.Freeze();

                var group = new DrawingGroup();
                group.Children.Add(new GeometryDrawing(tabFill, new Pen(border, 1), new RectangleGeometry(new Rect(1, 3, 7, 3))));
                group.Children.Add(new GeometryDrawing(bodyFill, new Pen(border, 1), new RectangleGeometry(new Rect(1, 5, 14, 9))));
                if (group.CanFreeze) group.Freeze();
                return new DrawingImage(group);
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource GetShellIcon(bool isFolder)
        {
            try
            {
                uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;
                uint attrs = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
                var info = new SHFILEINFO();
                var result = SHGetFileInfo(
                    isFolder ? "C:\\" : "file.txt",
                    attrs,
                    out info,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    flags);

                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
                    if (source.CanFreeze) source.Freeze();
                    return source;
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
            catch
            {
                return null;
            }
        }

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        private static void FreezeBrush(Brush brush)
        {
            if (brush is Freezable freezable && freezable.CanFreeze)
            {
                freezable.Freeze();
            }
        }

        private void UpdateStatus(string msg)
        {
            this.Dispatcher.Invoke(() =>
            {
                if (!string.IsNullOrWhiteSpace(msg) &&
                    msg.IndexOf("fatal: not a git repository", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    msg = "Loading...";
                }
                MessageBox.Show(msg, "Kev Git Changes");
            });
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            if (mi == null) return;
            // determine which TreeView was the source and get selected node
            string file = null;
            try
            {
                TreeNode node = LocalTree.SelectedItem as TreeNode;

                if (node != null) file = node.FullPath;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(file)) return;
            // open the local working copy if available
            if (!string.IsNullOrWhiteSpace(currentWorkDir))
            {
                var full = ResolveWorkspacePath(file);
                if (System.IO.File.Exists(full))
                {
                    // open in Visual Studio
                    try
                    {
                        var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                        dte.ItemOperations.OpenFile(full);
                    }
                    catch
                    {
                        System.Diagnostics.Process.Start(full);
                    }
                }
            }
        }

        private void CompareWithBase_Click(object sender, RoutedEventArgs e)
        {
            var file = GetSelectedFile();
            if (string.IsNullOrWhiteSpace(file)) return;
            CompareScopes(file, "Workspace", "Main");
        }

        private void CompareSelected_Click(object sender, RoutedEventArgs e)
        {
            var file = GetSelectedFile();
            if (string.IsNullOrWhiteSpace(file)) return;
            var left = GetScopeKeyFromCombo(CompareFromCombo);
            var right = GetScopeKeyFromCombo(CompareToCombo);
            CompareScopes(file, left, right);
        }

        private void CompareWithMain_Click(object sender, RoutedEventArgs e)
        {
            var file = GetSelectedFile();
            if (string.IsNullOrWhiteSpace(file)) return;
            CompareScopes(file, "Workspace", "Main");
        }

        private void CompareWithRemote_Click(object sender, RoutedEventArgs e)
        {
            var file = GetSelectedFile();
            if (string.IsNullOrWhiteSpace(file)) return;
            CompareScopes(file, "Workspace", "Remote");
        }

        private void CompareWithLocal_Click(object sender, RoutedEventArgs e)
        {
            var file = GetSelectedFile();
            if (string.IsNullOrWhiteSpace(file)) return;
            CompareScopes(file, "Workspace", "Local");
        }

        private void TreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            var item = FindAncestor<TreeViewItem>(dep);
            if (item != null)
            {
                item.IsSelected = true;
                item.Focus();
            }
        }

        private void LocalTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            try
            {
                if (!(LocalTree?.SelectedItem is TreeNode node) || !node.IsFile)
                {
                    e.Handled = true;
                }
            }
            catch
            {
                e.Handled = true;
            }
        }

        private void LocalTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                var dep = e.OriginalSource as DependencyObject;
                var item = FindAncestor<TreeViewItem>(dep);
                if (item?.DataContext is TreeNode node && node.IsFile)
                {
                    item.IsSelected = true;
                    item.Focus();
                    if (!string.IsNullOrWhiteSpace(node.FullPath))
                    {
                        CompareScopes(node.FullPath, "Workspace", "Main");
                        e.Handled = true;
                    }
                }
            }
            catch { }
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private string GetSelectedFile()
        {
            try
            {
                if (LocalTree.SelectedItem is TreeNode node && !string.IsNullOrWhiteSpace(node.FullPath))
                {
                    return node.FullPath;
                }
            }
            catch { }
            return null;
        }

        private void InitializeCompareOptions()
        {
            try
            {
                UpdateCompareCombos("Workspace", "Local");
            }
            catch { }
        }

        private void CompareSelection_Changed(object sender, SelectionChangedEventArgs e)
        {
            UpdateCompareMenuHeader();
        }

        private void FilterChanged(object sender, RoutedEventArgs e)
        {
            UpdateListViews(scopeMap);
        }

        private void CompareScopes(string file, string leftScope, string rightScope)
        {
            if (string.IsNullOrWhiteSpace(currentWorkDir) || string.IsNullOrWhiteSpace(file)) return;
            var left = ScopeToRef(leftScope);
            var right = ScopeToRef(rightScope);
            var validationError = GetCompareValidationError(file, leftScope, left, rightScope, right);
            if (!string.IsNullOrWhiteSpace(validationError))
            {
                UpdateStatus(validationError);
                return;
            }

            if (IsMockCompareEnabled())
            {
                WriteCompareDiagnostics(file, leftScope, rightScope, left, right, "Mock compare enabled");
                if (!TryLaunchGitDifftool(file, left, right))
                {
                    UpdateStatus("Unable to launch git difftool.");
                }
                return;
            }

            if (!HasGitDifftoolConfigured(currentWorkDir))
            {
                if (!LaunchVsCompare(file, leftScope, rightScope))
                {
                    UpdateStatus("Unable to open compare window.");
                }
                return;
            }

            try
            {
                if (!TryLaunchGitDifftool(file, left, right))
                {
                    UpdateStatus("Unable to launch git difftool.");
                }
            }
            catch (System.Exception ex)
            {
                UpdateStatus("Unable to launch difftool: " + ex.Message);
            }
        }

        private string ScopeToRef(string scope)
        {
            switch ((scope ?? string.Empty).Trim())
            {
                case "Workspace":
                    return null;

                case "Local":
                    return "HEAD";

                case "Remote":
                    return selectedRemote + "/" + currentBranch;

                case "Main":
                    return IsLoadingLabel(currentBaseBranch) ? null : currentBaseBranch;

                default:
                    return null;
            }
        }

        private static string BuildDiffArgs(string leftRef, string rightRef, string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            if (string.IsNullOrWhiteSpace(leftRef) && string.IsNullOrWhiteSpace(rightRef))
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(leftRef))
            {
                return $"difftool -y {rightRef} -- \"{file}\"";
            }
            if (string.IsNullOrWhiteSpace(rightRef))
            {
                return $"difftool -y {leftRef} -- \"{file}\"";
            }
            return $"difftool -y {leftRef} {rightRef} -- \"{file}\"";
        }

        private string GetCompareValidationError(string file, string leftScope, string leftRef, string rightScope, string rightRef)
        {
            var leftMissing = GetMissingCompareTargetLabel(file, leftScope, leftRef);
            if (!string.IsNullOrWhiteSpace(leftMissing))
            {
                return $"Cannot compare because {leftMissing} does not contain {file}.";
            }

            var rightMissing = GetMissingCompareTargetLabel(file, rightScope, rightRef);
            if (!string.IsNullOrWhiteSpace(rightMissing))
            {
                return $"Cannot compare because {rightMissing} does not contain {file}.";
            }

            return null;
        }

        private string GetMissingCompareTargetLabel(string file, string scopeKey, string refName)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;

            if (string.IsNullOrWhiteSpace(scopeKey) || string.Equals(scopeKey, "Workspace", StringComparison.OrdinalIgnoreCase))
            {
                var fullPath = ResolveWorkspacePath(file);
                return System.IO.File.Exists(fullPath) ? null : "Workspace";
            }

            if (string.IsNullOrWhiteSpace(refName)) return GetScopeDisplay(scopeKey) ?? scopeKey;

            GetFileContentAtRef(currentWorkDir, refName, file, out bool ok);
            return ok ? null : (GetScopeDisplay(scopeKey) ?? refName);
        }

        private string ToWorkDirRelativeGitPath(string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return file;

            var normalizedFile = file.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalizedFile)) return normalizedFile;
            if (System.IO.Path.IsPathRooted(file)) return normalizedFile;
            if (string.IsNullOrWhiteSpace(currentRepoRoot) || string.IsNullOrWhiteSpace(currentWorkDir)) return normalizedFile;

            try
            {
                var repoRootNormalized = currentRepoRoot.Replace('\\', '/').TrimEnd('/');
                var workDirNormalized = currentWorkDir.Replace('\\', '/').TrimEnd('/');
                if (string.IsNullOrWhiteSpace(repoRootNormalized) || string.IsNullOrWhiteSpace(workDirNormalized))
                {
                    return normalizedFile;
                }

                var fileFullPath = repoRootNormalized + "/" + normalizedFile;
                var baseUri = new Uri(AppendTrailingSlash(workDirNormalized), UriKind.Absolute);
                var fileUri = new Uri(fileFullPath, UriKind.Absolute);
                var relativeUri = baseUri.MakeRelativeUri(fileUri);
                var relativePath = Uri.UnescapeDataString(relativeUri.ToString()).Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    return normalizedFile;
                }
                return relativePath;
            }
            catch
            {
                // fall back to the original path
            }

            return normalizedFile;
        }

        private static string AppendTrailingSlash(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            return path.EndsWith("/", StringComparison.Ordinal) ? path : (path + "/");
        }

        private bool HasGitDifftoolConfigured(string workDir)
        {
            if (string.IsNullOrWhiteSpace(workDir)) return false;
            var tool = RunGit(workDir, "config --get diff.tool");
            if (string.IsNullOrWhiteSpace(tool)) return false;
            var trimmed = tool.Trim();
            if (trimmed.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        private bool IsMockCompareEnabled()
        {
            try
            {
                var env = Environment.GetEnvironmentVariable("KEVGITCHANGES_MOCK_COMPARE");
                if (!string.IsNullOrWhiteSpace(env) && IsTruthy(env)) return true;
            }
            catch { }

            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dir = System.IO.Path.Combine(appData, "KevGitChanges");
                var file = System.IO.Path.Combine(dir, "compare.mock");
                if (!System.IO.File.Exists(file)) return false;
                var txt = System.IO.File.ReadAllText(file);
                return string.IsNullOrWhiteSpace(txt) || IsTruthy(txt);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            return v.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                   v.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        private void WriteCompareDiagnostics(string file, string leftScope, string rightScope, string leftRef, string rightRef, string reason)
        {
            try
            {
                var tool = RunGit(currentWorkDir, "config --get diff.tool");
                var toolCmd = string.Empty;
                if (!string.IsNullOrWhiteSpace(tool))
                {
                    var t = tool.Trim();
                    toolCmd = RunGit(currentWorkDir, $"config --get difftool.{t}.cmd");
                    if (string.IsNullOrWhiteSpace(toolCmd))
                    {
                        toolCmd = RunGit(currentWorkDir, $"config --get difftool.{t}.path");
                    }
                }

                WriteOutput("Compare diagnostics:");
                WriteOutput($"  Reason: {reason}");
                WriteOutput($"  File: {file}");
                WriteOutput($"  LeftScope: {leftScope}  LeftRef: {leftRef}");
                WriteOutput($"  RightScope: {rightScope}  RightRef: {rightRef}");
                WriteOutput($"  WorkDir: {currentWorkDir}");
                if (!string.IsNullOrWhiteSpace(tool)) WriteOutput($"  diff.tool: {tool.Trim()}");
                if (!string.IsNullOrWhiteSpace(toolCmd)) WriteOutput($"  difftool cmd/path: {toolCmd.Trim()}");
            }
            catch
            {
                // ignore diagnostics failures
            }
        }

        private struct CompareFile
        {
            public string Path;
            public string Label;
        }

        private bool LaunchVsCompare(string file, string leftScope, string rightScope)
        {
            try
            {
                var left = GetFileForScope(currentWorkDir, leftScope, file);
                var right = GetFileForScope(currentWorkDir, rightScope, file);

                var diffService = Package.GetGlobalService(typeof(SVsDifferenceService));
                if (diffService == null)
                {
                    UpdateStatus("Visual Studio compare service not available.");
                    WriteOutput("Compare diagnostics: SVsDifferenceService unavailable.");
                    return false;
                }

                var caption = $"{left.Label} vs {right.Label}";
                try
                {
                    dynamic svc = diffService;
                    svc.OpenComparisonWindow2(left.Path, right.Path, left.Label, right.Label, caption, null, null, 0);
                    return true;
                }
                catch (Exception ex)
                {
                    WriteOutput("Compare diagnostics: OpenComparisonWindow2 failed: " + ex.Message);
                }

                try
                {
                    dynamic svc = diffService;
                    svc.OpenComparisonWindow(left.Path, right.Path, left.Label, right.Label, caption, null, null, 0);
                    return true;
                }
                catch (Exception ex)
                {
                    WriteOutput("Compare diagnostics: OpenComparisonWindow failed: " + ex.Message);
                    UpdateStatus("Unable to open Visual Studio compare window.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                UpdateStatus("Unable to launch Visual Studio compare: " + ex.Message);
                WriteOutput("Compare diagnostics: VS compare launch exception: " + ex.Message);
                return false;
            }
        }

        private CompareFile GetFileForScope(string workDir, string scopeKey, string relPath)
        {
            var label = GetScopeDisplay(scopeKey);
            if (string.IsNullOrWhiteSpace(scopeKey) || string.Equals(scopeKey, "Workspace", StringComparison.OrdinalIgnoreCase))
            {
                var full = ResolveWorkspacePath(relPath);
                if (System.IO.File.Exists(full))
                {
                    return new CompareFile { Path = full, Label = label };
                }
                var tempMissing = WriteTempFile(relPath, "workspace", string.Empty);
                return new CompareFile { Path = tempMissing, Label = label + " (missing)" };
            }

            var refName = ScopeToRef(scopeKey);
            if (string.IsNullOrWhiteSpace(refName))
            {
                var tempEmpty = WriteTempFile(relPath, "unknown", string.Empty);
                return new CompareFile { Path = tempEmpty, Label = label + " (missing)" };
            }

            var content = GetFileContentAtRef(workDir, refName, relPath, out bool ok);
            var tempPath = WriteTempFile(relPath, refName, content ?? string.Empty);
            return new CompareFile { Path = tempPath, Label = ok ? label : (label + " (missing)") };
        }

        private string GetFileContentAtRef(string workDir, string refName, string relPath, out bool ok)
        {
            ok = false;
            if (string.IsNullOrWhiteSpace(workDir) || string.IsNullOrWhiteSpace(refName) || string.IsNullOrWhiteSpace(relPath)) return null;
            var gitPath = relPath.Replace('\\', '/');
            var payload = RunGit(workDir, $"show {refName}:\"{gitPath}\"");
            if (string.IsNullOrWhiteSpace(payload)) return null;
            var trimmed = payload.TrimStart();
            if (trimmed.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            ok = true;
            return payload;
        }

        private string WriteTempFile(string relPath, string refName, string content)
        {
            var tempRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KevGitChangesDiff");
            if (!System.IO.Directory.Exists(tempRoot)) System.IO.Directory.CreateDirectory(tempRoot);

            var fileName = System.IO.Path.GetFileName(relPath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "file";
            var ext = System.IO.Path.GetExtension(fileName);
            var baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
            var key = $"{refName}:{relPath}".ToLowerInvariant();
            var hash = ComputeSha1(key);
            var safeBase = SanitizeFileName(baseName);
            var tempFile = System.IO.Path.Combine(tempRoot, $"{safeBase}.{hash}{ext}");
            System.IO.File.WriteAllText(tempFile, content ?? string.Empty);
            return tempFile;
        }

        private bool TryLaunchGitDifftool(string file, string leftRef, string rightRef)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(currentWorkDir)) return false;
                var gitFile = ToWorkDirRelativeGitPath(file);
                var args = BuildDiffArgs(leftRef, rightRef, gitFile);
                if (string.IsNullOrWhiteSpace(args)) return false;
                WriteOutput("Compare diagnostics: launching git difftool.");
                WriteOutput($"  args: {args}");
                var psi = new System.Diagnostics.ProcessStartInfo("git", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    WorkingDirectory = currentWorkDir
                };
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                WriteOutput("Compare diagnostics: git difftool failed: " + ex.Message);
                return false;
            }
        }

        private string ResolveRepoRoot(string workDir)
        {
            if (string.IsNullOrWhiteSpace(workDir)) return null;
            try
            {
                var res = RunGit(workDir, "rev-parse --show-toplevel");
                if (!string.IsNullOrWhiteSpace(res))
                {
                    var trimmed = res.Trim();
                    if (!trimmed.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmed;
                    }
                }
            }
            catch
            {
                // fall through to .git walk
            }

            try
            {
                var dir = new System.IO.DirectoryInfo(workDir);
                while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, ".git")))
                {
                    dir = dir.Parent;
                }
                return dir?.FullName ?? workDir;
            }
            catch
            {
                return workDir;
            }
        }

        private string ResolveWorkspacePath(string relPath)
        {
            if (string.IsNullOrWhiteSpace(relPath)) return relPath;
            if (System.IO.Path.IsPathRooted(relPath)) return relPath;
            var root = !string.IsNullOrWhiteSpace(currentRepoRoot) ? currentRepoRoot : currentWorkDir;
            if (string.IsNullOrWhiteSpace(root)) return relPath;
            return System.IO.Path.Combine(root, relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        private string RunGit(string workingDirectory, string arguments)
        {
            int ignored;
            return RunGit(workingDirectory, arguments, out ignored);
        }

        private string RunGit(string workingDirectory, string arguments, out int exitCode)
        {
            exitCode = -1;
            try
            {
                WriteOutput($"git {arguments}");
                var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory
                };

                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var outStr = p.StandardOutput.ReadToEnd();
                    var errStr = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    exitCode = p.ExitCode;
                    if (p.ExitCode != 0 && string.IsNullOrWhiteSpace(outStr))
                    {
                        return errStr;
                    }
                    return outStr;
                }
            }
            catch (System.Exception ex)
            {
                return ex.Message;
            }
        }

        private void WriteOutput(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.StartsWith("Icon diagnostics:", StringComparison.OrdinalIgnoreCase))
            {
                lastIconDiagnostics = line;
            }
            try
            {
                if (ThreadHelper.CheckAccess())
                {
                    EnsureOutputPaneSync();
                    outputPane?.OutputString(line + Environment.NewLine);
                    return;
                }
            }
            catch
            {
                // fall through to async path
            }
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var outputWindow = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                    if (outputWindow == null)
                    {
                        System.Diagnostics.Debug.WriteLine(line);
                        return;
                    }

                    if (outputPane == null)
                    {
                        var paneGuid = OutputPaneGuid;
                        outputWindow.CreatePane(ref paneGuid, "Kev Git Changes", 1, 1);
                        outputWindow.GetPane(ref paneGuid, out outputPane);
                    }

                    outputPane?.OutputStringThreadSafe(line + Environment.NewLine);
                    outputPane?.Activate();
                }
                catch
                {
                    // ignore logging failures
                }
            });
        }

        private void EnsureOutputPane()
        {
            try
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var outputWindow = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                    if (outputWindow == null) return;
                    if (outputPane != null) return;
                    var paneGuid = OutputPaneGuid;
                    outputWindow.CreatePane(ref paneGuid, "Kev Git Changes", 1, 1);
                    outputWindow.GetPane(ref paneGuid, out outputPane);
                    outputPane?.Activate();
                });
            }
            catch
            {
                // ignore
            }
        }

        private void EnsureOutputPaneSync()
        {
            try
            {
                var outputWindow = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                if (outputWindow == null) return;
                if (outputPane != null) return;
                var paneGuid = OutputPaneGuid;
                outputWindow.CreatePane(ref paneGuid, "Kev Git Changes", 1, 1);
                outputWindow.GetPane(ref paneGuid, out outputPane);
                outputPane?.Activate();
            }
            catch
            {
                // ignore
            }
        }

        private string GetScopeKeyFromCombo(ComboBox combo)
        {
            if (combo == null) return null;
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                return tag;
            }
            if (combo.SelectedItem is string s)
            {
                return s;
            }
            return null;
        }

        private string GetScopeDisplay(string scopeKey)
        {
            switch ((scopeKey ?? string.Empty).Trim())
            {
                case "Workspace":
                    return "Workspace";

                case "Local":
                    return string.IsNullOrWhiteSpace(currentBranch) ? "Local" : $"{currentBranch} (local)";

                case "Remote":
                    return string.IsNullOrWhiteSpace(currentBranch) ? "Remote" : $"{selectedRemote}/{currentBranch} (remote)";

                case "Main":
                    if (IsLoadingLabel(currentBaseBranch) || IsGitRepoError(currentBaseBranch)) return $"{LoadingLabel} (base)";
                    return string.IsNullOrWhiteSpace(currentBaseBranch) ? "Base" : $"{currentBaseBranch} (base)";

                default:
                    return scopeKey;
            }
        }

        private string GetScopeFilterLabel(string scopeKey)
        {
            switch ((scopeKey ?? string.Empty).Trim())
            {
                case "Workspace":
                    return "Workspace";

                case "Local":
                    return string.IsNullOrWhiteSpace(currentBranch) ? "Local" : currentBranch;

                case "Remote":
                    return string.IsNullOrWhiteSpace(currentBranch) ? "Remote" : $"{selectedRemote}/{currentBranch}";

                case "Main":
                    if (IsLoadingLabel(currentBaseBranch) || IsGitRepoError(currentBaseBranch)) return LoadingLabel;
                    return string.IsNullOrWhiteSpace(currentBaseBranch) ? "Base" : currentBaseBranch;

                default:
                    return scopeKey;
            }
        }

        private void UpdateScopeLabels()
        {
            var localLabel = GetScopeDisplay("Local");
            var remoteLabel = GetScopeDisplay("Remote");
            var mainLabel = GetScopeDisplay("Main");
            this.Dispatcher.Invoke(() =>
            {
                if (ShowWorkspaceLabel != null) ShowWorkspaceLabel.Text = GetScopeFilterLabel("Workspace");
                if (ShowLocalLabel != null) ShowLocalLabel.Text = GetScopeFilterLabel("Local");
                if (ShowRemoteLabel != null) ShowRemoteLabel.Text = GetScopeFilterLabel("Remote");

                UpdateCompareCombos("Workspace", "Local");
                UpdateCompareMenuHeader();

                if (CompareWithLocalMenu != null)
                {
                    CompareWithLocalMenu.Header = $"Compare vs {localLabel}";
                }
                if (CompareWithMainMenu != null)
                {
                    CompareWithMainMenu.Header = $"Compare vs {mainLabel}";
                }
                if (CompareWithRemoteMenu != null)
                {
                    CompareWithRemoteMenu.Header = $"Compare vs {remoteLabel}";
                }
            });
        }

        private void UpdateCompareCombos(string defaultFrom, string defaultTo)
        {
            var fromKey = GetScopeKeyFromCombo(CompareFromCombo) ?? defaultFrom;
            var toKey = GetScopeKeyFromCombo(CompareToCombo) ?? defaultTo;

            var items = BuildCompareItems();
            var items2 = BuildCompareItems();
            CompareFromCombo.ItemsSource = items;
            CompareToCombo.ItemsSource = items2;
            SelectComboByTag(CompareFromCombo, fromKey);
            SelectComboByTag(CompareToCombo, toKey);
        }

        private System.Collections.Generic.List<ComboBoxItem> BuildCompareItems()
        {
            return new System.Collections.Generic.List<ComboBoxItem>
            {
                new ComboBoxItem { Tag = "Workspace", Content = GetScopeDisplay("Workspace") },
                new ComboBoxItem { Tag = "Local", Content = GetScopeDisplay("Local") },
                new ComboBoxItem { Tag = "Remote", Content = GetScopeDisplay("Remote") },
                new ComboBoxItem { Tag = "Main", Content = GetScopeDisplay("Main") }
            };
        }

        private void SelectComboByTag(ComboBox combo, string tag)
        {
            if (combo == null || string.IsNullOrWhiteSpace(tag)) return;
            foreach (var obj in combo.Items)
            {
                if (obj is ComboBoxItem item && item.Tag is string t &&
                    string.Equals(t, tag, System.StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void UpdateCompareMenuHeader()
        {
            if (CompareSelectionMenu == null) return;
            var fromKey = GetScopeKeyFromCombo(CompareFromCombo);
            var toKey = GetScopeKeyFromCombo(CompareToCombo);
            var fromLabel = GetScopeDisplayForCompareSelection(fromKey) ?? "A";
            var toLabel = GetScopeDisplayForCompareSelection(toKey) ?? "B";
            CompareSelectionMenu.Header = $"Compare {fromLabel} to {toLabel}";
        }

        private string GetScopeDisplayForCompareSelection(string scopeKey)
        {
            switch ((scopeKey ?? string.Empty).Trim())
            {
                case "Local":
                    return string.IsNullOrWhiteSpace(currentBranch) ? "Local" : currentBranch;

                default:
                    return GetScopeDisplay(scopeKey);
            }
        }

        private static bool IsGitRepoError(string payload)
        {
            return !string.IsNullOrWhiteSpace(payload) &&
                payload.IndexOf("fatal: not a git repository", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsGitError(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return false;
            var trimmed = payload.TrimStart();
            return trimmed.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("error:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLoadingLabel(string value)
        {
            return string.Equals(value, LoadingLabel, StringComparison.OrdinalIgnoreCase);
        }
    }
}
