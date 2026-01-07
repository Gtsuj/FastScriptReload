using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImmersiveVRTools.Editor.Common.Utilities;
using ImmersiveVrToolsCommon.Runtime.Logging;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace FastScriptReload.Editor
{
    [InitializeOnLoad]
    // [PreventHotReload]
    public class FastScriptReloadManager
    {
        private static FastScriptReloadManager _instance;
        public static FastScriptReloadManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new FastScriptReloadManager();
                    LoggerScoped.LogDebug("Created Manager");
                }

                return _instance;
            }
        }

        private static string DataPath = Application.dataPath;


        public const string FileWatcherReplacementTokenForApplicationDataPath = "<Application.dataPath>";

        public Dictionary<string, Func<string>> FileWatcherTokensToResolvePathFn = new Dictionary<string, Func<string>>
        {
            [FileWatcherReplacementTokenForApplicationDataPath] = () => DataPath
        };

        private Dictionary<string, DynamicFileHotReloadState> _lastProcessedDynamicFileHotReloadStatesInSession = new Dictionary<string, DynamicFileHotReloadState>();
        public IReadOnlyDictionary<string, DynamicFileHotReloadState> LastProcessedDynamicFileHotReloadStatesInSession => _lastProcessedDynamicFileHotReloadStatesInSession;
        public event Action<List<DynamicFileHotReloadState>> HotReloadFailed;
        public event Action<List<DynamicFileHotReloadState>> HotReloadSucceeded;

        private bool _wasLockReloadAssembliesCalled;
        private PlayModeStateChange _lastPlayModeStateChange;
        private List<IDisposable> _fileWatchers = new List<IDisposable>();
        private IEnumerable<string> _currentFileExclusions;
        private int _triggerDomainReloadIfOverNDynamicallyLoadedAssembles = 100;

        private List<DynamicFileHotReloadState> _dynamicFileHotReloadStateEntries = new List<DynamicFileHotReloadState>();

        public List<DynamicFileHotReloadState> DynamicFileHotReloadStateEntries => _dynamicFileHotReloadStateEntries;

        private DateTime _lastTimeChangeBatchRun = default(DateTime);
        private bool _isEditorModeHotReloadEnabled = true; // Editor Hot-Reload is now enabled by default
        private int _hotReloadPerformedCount = 0;

        private void OnWatchedFileChange(object source, FileSystemEventArgs e)
        {
            if (ShouldIgnoreFileChange()) return;

            var filePathToUse = e.FullPath;
            if (!File.Exists(filePathToUse))
            {
                if (!TryWorkaroundForUnityFileWatcherBug(e, ref filePathToUse))
                    return;
            }

            AddFileChangeToProcess(filePathToUse);
        }

        public void AddFileChangeToProcess(string filePath)
        {
            if (!File.Exists(filePath))
            {
                LoggerScoped.LogWarning($"Specified file: '{filePath}' does not exist. Hot-Reload will not be performed.");
                return;
            }

            if (_currentFileExclusions != null && _currentFileExclusions.Any(fp => filePath.Replace("\\", "/").EndsWith(fp)))
            {
                LoggerScoped.LogWarning($"FastScriptReload: File: '{filePath}' changed, but marked as exclusion. Hot-Reload will not be performed. You can manage exclusions via" +
                                        $"\r\nRight click context menu (Fast Script Reload > Add / Remove Hot-Reload exclusion)" +
                                        $"\r\nor via Window -> Fast Script Reload -> Start Screen -> Exclusion menu");

                return;
            }

            const int msThresholdToConsiderSameChangeFromDifferentFileWatchers = 500;
            var isDuplicatedChangesComingFromDifferentFileWatcher = _dynamicFileHotReloadStateEntries
                .Any(f => f.FullFileName == filePath
                          && (DateTime.UtcNow - f.FileChangedOn).TotalMilliseconds < msThresholdToConsiderSameChangeFromDifferentFileWatchers);
            if (isDuplicatedChangesComingFromDifferentFileWatcher)
            {
                LoggerScoped.LogWarning($"FastScriptReload: Looks like change to: {filePath} have already been added for processing. This can happen if you have multiple file watchers set in a way that they overlap.");
                return;
            }

            _dynamicFileHotReloadStateEntries.Add(new DynamicFileHotReloadState(filePath, DateTime.UtcNow));
        }

        public bool ShouldIgnoreFileChange()
        {
            if (!_isEditorModeHotReloadEnabled && _lastPlayModeStateChange != PlayModeStateChange.EnteredPlayMode)
            {
#if ImmersiveVrTools_DebugEnabled
            // LoggerScoped.Log($"Application not playing, change to: {e.Name} won't be compiled and hot reloaded");
#endif
                return true;
            }

            return false;
        }

        private void StartWatchingDirectoryAndSubdirectories(string directoryPath, string filter, bool includeSubdirectories)
        {
            foreach (var kv in FileWatcherTokensToResolvePathFn)
            {
                directoryPath = directoryPath.Replace(kv.Key, kv.Value());
            }

            var directoryInfo = new DirectoryInfo(directoryPath);
            if (!directoryInfo.Exists)
            {
                LoggerScoped.LogWarning($"FastScriptReload: Directory: '{directoryPath}' does not exist, make sure file-watcher setup is correct. You can access via: Window -> Fast Script Reload -> File Watcher (Advanced Setup)");
            }

            switch ((FileWatcherImplementation)FastScriptReloadPreference.FileWatcherImplementationInUse.GetEditorPersistedValueOrDefault())
            {
                case FileWatcherImplementation.UnityDefault:
                    var fileWatcher = new FileSystemWatcher();

                    fileWatcher.Path = directoryInfo.FullName;
                    fileWatcher.IncludeSubdirectories = includeSubdirectories;
                    fileWatcher.Filter = filter;
                    fileWatcher.NotifyFilter = NotifyFilters.LastWrite;
                    fileWatcher.Changed += OnWatchedFileChange;

                    fileWatcher.EnableRaisingEvents = true;

                    _fileWatchers.Add(fileWatcher);

                    break;
#if UNITY_2021_1_OR_NEWER && UNITY_EDITOR_WIN
                case FileWatcherImplementation.DirectWindowsApi: 
                // On Windows, this is a WindowsFileSystemWatcher.
                // On other platforms, it's the default Mono implementation.
                // The WindowsFileSystemWatcher has much lower latency on Windows.
                // However, there's a small issue:
                // The WindowsFileSystemWatcher can, theoretically, miss events.
                // This is true in Microsoft's implementation as well as ours.
                // (Actually, ours should be slightly better.)
                // This can happen if a change occurs during the brief moment
                // during which the previous batch of changes are being
                // recorded and queued.
                // It can also happen if too many changes occur at once, overwhelming
                // the buffer.
                // People seem to routinely use the basic MS filewatcher and ignore
                // these issues, treating them as acceptably unlikely.
                // Our current implementation here does that too, but we may want
                // to look at eliminating this issue.
                // Unfortunately, it's a limitation of the Windows API, and to
                // my knowledge can't be avoided directly.
                // The solution is to combine the file watcher with a polling
                // mechanism which can (slowly, but reliably) catch any missed events.
                var windowsFileSystemWatcher = new WindowsFileSystemWatcher()
                {
                    Path = directoryInfo.FullName,
                    IncludeSubdirectories = includeSubdirectories,
                    Filter = filter,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                };
                windowsFileSystemWatcher.Changed += OnWatchedFileChange;

                // Visual Studio is annoying.
                // It doesn't actually trigger a nice Changed event.
                // Instead, it goes through some elaborate procedure.
                // When changing player.cs, it does:
                // CREATE: Code\ua4tt1aw.4ae~
                // CHANGE: Code\ua4tt1aw.4ae~
                // CREATE: Code\Player.cs~RF70560f7.TMP
                // REMOVE: Code\Player.cs~RF70560f7.TMP
                // RENAME: Code\Player.cs -> Code\Player.cs~RF70560f7.TMP
                // RENAME: Code\ua4tt1aw.4ae~ -> Code\Player.cs
                // REMOVE: Code\Player.cs~RF70560f7.TMP - again, somehow?
                //
                // This was fine before, because the watcher implementation was polling.
                // I guess this usually happens fast between polls, so it just looks like a change.
                // Note that that may mean there's a potential bug if polling happens in the middle of this procedure.
                //
                // This fix is a temporary measure.
                // We should probably think more seriously about maybe being able to catch file additions and renames.
                // If we dealt with those extra events smoothly, this would probably just work.
                //
                // Other IDEs may do similar but different things. We want a general purpose solution, not a VS specific one.
                // Perhaps the approach should be to keep track of touched files, then do some diff procedure to work out what's happened to them?
                // This could, perhaps, be integrated into the file watcher robustness polling solution discussed above.
                // The two systems share a need for some type of event debouncing.
                windowsFileSystemWatcher.Renamed += (source, e) =>
                {
                    if (e.Name.EndsWith(".cs"))
                        OnWatchedFileChange(source, e);
                };
        
                windowsFileSystemWatcher.EnableRaisingEvents = true;
                                    
                _fileWatchers.Add(windowsFileSystemWatcher);
                break;
#endif

                case FileWatcherImplementation.CustomPolling:
                    CustomFileWatcher.InitializeSingularFilewatcher(directoryPath, filter, includeSubdirectories);
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        static FastScriptReloadManager()
        {
            EditorApplication.update += Instance.Update;
            EditorApplication.playModeStateChanged += Instance.OnEditorApplicationOnplayModeStateChanged;

            if ((bool)FastScriptReloadPreference.WatchOnlySpecified.GetEditorPersistedValueOrDefault() && SessionState.GetBool("NEED_EDITOR_SESSION_INIT", true))
            {
                SessionState.SetBool("NEED_EDITOR_SESSION_INIT", false);
                FastScriptReloadContextMenu.ClearFileWatchersEntries();
            }
        }

        ~FastScriptReloadManager()
        {
            LoggerScoped.LogDebug("Destroying FSR Manager ");
            if (_instance != null)
            {
                if (_lastPlayModeStateChange == PlayModeStateChange.EnteredPlayMode)
                {
                    LoggerScoped.LogError("Manager is being destroyed in play session, this indicates some sort of issue where static variables were reset, hot reload will not function properly please reset. " +
                                          "This is usually caused by Unity triggering that reset for some reason that's outside of asset control - other static variables will also be affected and recovering just hot reload would hide wider issue.");
                }
                ClearFileWatchers();
            }
        }

        public void Update()
        {
            if (_lastPlayModeStateChange == PlayModeStateChange.ExitingPlayMode && Instance._fileWatchers.Any())
            {
                ClearFileWatchers();
            }

            if (_isEditorModeHotReloadEnabled)
            {
                EnsureInitialized();
            }
            else if (_lastPlayModeStateChange == PlayModeStateChange.EnteredPlayMode)
            {
                EnsureInitialized();
            }

            if ((bool)FastScriptReloadPreference.EnableAutoReloadForChangedFiles.GetEditorPersistedValueOrDefault() &&
                (DateTime.UtcNow - _lastTimeChangeBatchRun).TotalSeconds > (int)FastScriptReloadPreference.BatchScriptChangesAndReloadEveryNSeconds.GetEditorPersistedValueOrDefault())
            {
                TriggerReloadForChangedFiles();
            }
        }

        public static void ClearFileWatchers()
        {
            foreach (var fileWatcher in Instance._fileWatchers)
            {
                fileWatcher.Dispose();
            }

            Instance._fileWatchers.Clear();
        }

        public void TriggerReloadForChangedFiles()
        {
            if (!Application.isPlaying && _hotReloadPerformedCount > _triggerDomainReloadIfOverNDynamicallyLoadedAssembles)
            {
                _hotReloadPerformedCount = 0;
                LoggerScoped.LogWarning($"Dynamically created assembles reached over: {_triggerDomainReloadIfOverNDynamicallyLoadedAssembles} - triggering full domain reload to clean up. You can adjust that value in settings.");
#if UNITY_2019_3_OR_NEWER
                CompilationPipeline.RequestScriptCompilation(); //TODO: add some timer to ensure this does not go into some kind of loop
#elif UNITY_2017_1_OR_NEWER
                 var editorAssembly = Assembly.GetAssembly(typeof(Editor));
                 var editorCompilationInterfaceType = editorAssembly.GetType("UnityEditor.Scripting.ScriptCompilation.EditorCompilationInterface");
                 var dirtyAllScriptsMethod = editorCompilationInterfaceType.GetMethod("DirtyAllScripts", BindingFlags.Static | BindingFlags.Public);
                 dirtyAllScriptsMethod.Invoke(editorCompilationInterfaceType, null);
#endif
                ClearLastProcessedDynamicFileHotReloadStates();
            }

            var changesAwaitingHotReload = _dynamicFileHotReloadStateEntries
                .Where(e => e.IsAwaitingCompilation)
                .ToList();

            if (changesAwaitingHotReload.Any())
            {
                UpdateLastProcessedDynamicFileHotReloadStates(changesAwaitingHotReload);
                foreach (var c in changesAwaitingHotReload)
                {
                    c.IsBeingProcessed = true;
                }

                Task.Run(() =>
                {
                    FastScriptReloadSceneOverlay.NotifyHookStart();

                    // 按程序集分组
                    var filesByAssembly = changesAwaitingHotReload
                        .GroupBy(e => TypeInfoHelper.GetAssemblyName(e.FullFileName))
                        .ToDictionary(g => g.Key, g => g.GroupBy(e => e.FullFileName)
                            .Select(e => e.First().FullFileName).ToList());

                    foreach (var pair in filesByAssembly)
                    {
                        ReloadChangedFiles(changesAwaitingHotReload, pair.Key, pair.Value);
                    }

                    FastScriptReloadSceneOverlay.NotifyHookComplete();
                });
            }

            _lastTimeChangeBatchRun = DateTime.UtcNow;
        }

        private void ReloadChangedFiles(List<DynamicFileHotReloadState> changesAwaitingHotReload, string assemblyName, List<string> files)
        {
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                var diffResults = ReloadHelper.CompileAndDiff(assemblyName, files);
                if (diffResults == null)
                {
                    changesAwaitingHotReload.ForEach(c => c.ErrorOn = DateTime.UtcNow);
                    
                    // 通知SceneOverlay显示无变化状态
                    FastScriptReloadSceneOverlay.NotifyNoChange();
                    
                    return;
                }

                LoggerScoped.LogDebug($"CompileAndDiff耗时: {stopwatch.ElapsedMilliseconds}ms");

                // 删除冗余部分，将改动的方法转换为静态方法
                var assemblyPath = ReloadHelper.ModifyCompileAssembly(assemblyName, diffResults);

                changesAwaitingHotReload.ForEach(c =>
                {
                    c.FileCompiledOn = DateTime.UtcNow;
                    c.AssemblyNameCompiledIn = assemblyPath;
                });
                LoggerScoped.LogDebug($"ModifyCompileAssembly耗时: {stopwatch.ElapsedMilliseconds}ms");

                // 应用热重载Hook
                ReloadHelper.ApplyHooks(diffResults);
                LoggerScoped.LogDebug($"ApplyHooks耗时: {stopwatch.ElapsedMilliseconds}ms");

                changesAwaitingHotReload.ForEach(c =>
                {
                    c.HotSwappedOn = DateTime.UtcNow;
                    c.IsBeingProcessed = false;
                });

                _hotReloadPerformedCount++;

                SafeInvoke(HotReloadSucceeded, changesAwaitingHotReload);
            }
            catch (Exception ex)
            {
                var errorMsg = $"Error when updating files: '{(files != null ? string.Join(",", files.Select(fn => new FileInfo(fn).Name)) : "unknown")}', {ex}";
                LoggerScoped.LogError(errorMsg);
                FastScriptReloadHookDetailsWindow.NotifyHookFailed(errorMsg);

                changesAwaitingHotReload.ForEach(c =>
                {
                    c.ErrorOn = DateTime.UtcNow;
                    c.ErrorText = ex.Message;
                });

                SafeInvoke(HotReloadFailed, changesAwaitingHotReload);
            }
        }

        private void SafeInvoke(Action<List<DynamicFileHotReloadState>> ev, List<DynamicFileHotReloadState> changesAwaitingHotReload)
        {
            try
            {
                ev?.Invoke(changesAwaitingHotReload);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error when executing event, {e}");
            }
        }

        private void AddToLastProcessedDynamicFileHotReloadStates(DynamicFileHotReloadState c)
        {
            var assetGuid = AssetDatabaseHelper.AbsolutePathToGUID(c.FullFileName);
            if (!string.IsNullOrEmpty(assetGuid))
            {
                _lastProcessedDynamicFileHotReloadStatesInSession[assetGuid] = c;
            }
        }

        private void ClearLastProcessedDynamicFileHotReloadStates()
        {
            _lastProcessedDynamicFileHotReloadStatesInSession.Clear();
        }

        //Success entries will always be cleared - errors will remain till another change fixes them
        private void UpdateLastProcessedDynamicFileHotReloadStates(List<DynamicFileHotReloadState> changesToHotReload)
        {
            var succeededReloads = _lastProcessedDynamicFileHotReloadStatesInSession
                .Where(s => s.Value.IsChangeHotSwapped).ToList();
            foreach (var kv in succeededReloads)
            {
                _lastProcessedDynamicFileHotReloadStatesInSession.Remove(kv.Key);
            }

            foreach (var changeToHotReload in changesToHotReload)
            {
                AddToLastProcessedDynamicFileHotReloadStates(changeToHotReload);
            }
        }

        private void OnEditorApplicationOnplayModeStateChanged(PlayModeStateChange obj)
        {
            Instance._lastPlayModeStateChange = obj;

            if ((bool)FastScriptReloadPreference.IsForceLockAssembliesViaCode.GetEditorPersistedValueOrDefault())
            {
                if (obj == PlayModeStateChange.EnteredPlayMode)
                {
                    EditorApplication.LockReloadAssemblies();
                    _wasLockReloadAssembliesCalled = true;
                }
            }

            if (obj == PlayModeStateChange.EnteredEditMode && _wasLockReloadAssembliesCalled)
            {
                EditorApplication.UnlockReloadAssemblies();
                _wasLockReloadAssembliesCalled = false;
            }
        }

        private static bool TryWorkaroundForUnityFileWatcherBug(FileSystemEventArgs e, ref string filePathToUse)
        {
            LoggerScoped.LogWarning(@"Fast Script Reload - Unity File Path Bug - Warning!
Path for changed file passed by Unity does not exist. This is a known editor bug, more info: https://issuetracker.unity3d.com/issues/filesystemwatcher-returns-bad-file-path
                    
Best course of action is to update editor as issue is already fixed in newer (minor and major) versions.
                    
As a workaround asset will try to resolve paths via directory search.
                    
Workaround will search in all folders (under project root) and will use first found file. This means it's possible it'll pick up wrong file as there's no directory information available.");

            var changedFileName = new FileInfo(filePathToUse).Name;
            //TODO: try to look in all file watcher configured paths, some users might have code outside of assets, eg packages
            // var fileFoundInAssets = FastScriptReloadPreference.FileWatcherSetupEntries.GetElementsTyped().SelectMany(setupEntries => Directory.GetFiles(DataPath, setupEntries.path, SearchOption.AllDirectories)).ToList();

            var fileFoundInAssets = Directory.GetFiles(DataPath, changedFileName, SearchOption.AllDirectories);
            if (fileFoundInAssets.Length == 0)
            {
                LoggerScoped.LogError($"FileWatcherBugWorkaround: Unable to find file '{changedFileName}', changes will not be reloaded. Please update unity editor.");
                return false;
            }
            else if (fileFoundInAssets.Length == 1)
            {
                LoggerScoped.Log($"FileWatcherBugWorkaround: Original Unity passed file path: '{e.FullPath}' adjusted to found: '{fileFoundInAssets[0]}'");
                filePathToUse = fileFoundInAssets[0];
                return true;
            }
            else
            {
                LoggerScoped.LogWarning($"FileWatcherBugWorkaround: Multiple files found. Original Unity passed file path: '{e.FullPath}' adjusted to found: '{fileFoundInAssets[0]}'");
                filePathToUse = fileFoundInAssets[0];
                return true;
            }
        }

        private static bool HotReloadDisabled_WarningMessageShownAlready;
        private static void EnsureInitialized()
        {
            if (!(bool)FastScriptReloadPreference.EnableAutoReloadForChangedFiles.GetEditorPersistedValueOrDefault()
                && !(bool)FastScriptReloadPreference.WatchOnlySpecified.GetEditorPersistedValueOrDefault())
            {
                if (!HotReloadDisabled_WarningMessageShownAlready)
                {
                    LoggerScoped.LogWarning($"Neither auto hot reload or watch specific is specified, file watchers will not be initialized. Please adjust settings and restart if you want hot reload to work.");
                    HotReloadDisabled_WarningMessageShownAlready = true;
                }
                return;
            }

            var isUsingCustomFileWatchers = (FileWatcherImplementation)FastScriptReloadPreference.FileWatcherImplementationInUse.GetEditorPersistedValueOrDefault()
                                            == FileWatcherImplementation.CustomPolling;
            if (!isUsingCustomFileWatchers)
            {
                if (Instance._fileWatchers.Count == 0 || FastScriptReloadPreference.FileWatcherSetupEntriesChanged)
                {
                    FastScriptReloadPreference.FileWatcherSetupEntriesChanged = false;

                    InitializeFromFileWatcherSetupEntries();
                }
            }
            else if (!CustomFileWatcher.InitSignaled)
            {
                CustomFileWatcher.TryEnableLivewatching();
                InitializeFromFileWatcherSetupEntries();
                CustomFileWatcher.InitSignaled = true;
            }
        }

        private static void InitializeFromFileWatcherSetupEntries()
        {
            var fileWatcherSetupEntries = FastScriptReloadPreference.FileWatcherSetupEntries.GetElementsTyped();
            if (fileWatcherSetupEntries.Count == 0)
            {
                LoggerScoped.LogWarning($"There are no file watcher setup entries. Tool will not be able to pick changes automatically");
            }

            foreach (var fileWatcherSetupEntry in fileWatcherSetupEntries)
            {
                Instance.StartWatchingDirectoryAndSubdirectories(
                    fileWatcherSetupEntry.path,
                    fileWatcherSetupEntry.filter,
                    fileWatcherSetupEntry.includeSubdirectories
                );
            }
        }

        public static bool IsFileWatcherSetupEntryAlreadyPresent(FileWatcherSetupEntry fileWatcherSetupEntry)
        {
            return FastScriptReloadPreference.FileWatcherSetupEntries.GetElementsTyped()
                .Any(e => e.path == fileWatcherSetupEntry.path
                          && e.filter == fileWatcherSetupEntry.filter
                          && e.includeSubdirectories == fileWatcherSetupEntry.includeSubdirectories);
        }

        public static bool IsFileWatcherSetupEntryAlreadyPresent(DefaultAsset selectedAsset)
        {
            FileWatcherSetupEntry fileWatcherSetupEntry;
            return IsFileWatcherSetupEntryAlreadyPresent(selectedAsset, out fileWatcherSetupEntry);
        }

        public static bool IsFileWatcherSetupEntryAlreadyPresent(DefaultAsset selectedAsset, out FileWatcherSetupEntry fileWatcherSetupEntry)
        {
            var path = FileWatcherReplacementTokenForApplicationDataPath + AssetDatabase.GetAssetPath(selectedAsset).Remove(0, "Assets".Length);
            fileWatcherSetupEntry = new FileWatcherSetupEntry(path, "*.cs", true);

            var isFileWatcherSetupEntryAlreadyPresent = IsFileWatcherSetupEntryAlreadyPresent(fileWatcherSetupEntry);
            return isFileWatcherSetupEntryAlreadyPresent;
        }

        public static bool IsFileWatcherSetupEntryAlreadyPresent(MonoScript selectedMonoScript)
        {
            FileWatcherSetupEntry fileWatcherSetupEntry;
            return IsFileWatcherSetupEntryAlreadyPresent(selectedMonoScript, out fileWatcherSetupEntry);
        }

        public static bool IsFileWatcherSetupEntryAlreadyPresent(MonoScript selectedMonoScript, out FileWatcherSetupEntry fileWatcherSetupEntry)
        {
            var path = FileWatcherReplacementTokenForApplicationDataPath + AssetDatabase.GetAssetPath(selectedMonoScript).Remove(0, "Assets".Length);
            var fileSeperatorIndex = path.LastIndexOf('/');
            var fileName = path.Substring(fileSeperatorIndex + 1);
            path = path.Substring(0, fileSeperatorIndex);

            fileWatcherSetupEntry = new FileWatcherSetupEntry(path, fileName, false);
            var isFileWatcherSetupEntryAlreadyPresent = IsFileWatcherSetupEntryAlreadyPresent(fileWatcherSetupEntry);
            return isFileWatcherSetupEntryAlreadyPresent;
        }
    }

    public class DynamicFileHotReloadState
    {
        public string FullFileName { get; set; }
        public DateTime FileChangedOn { get; set; }
        public bool IsAwaitingCompilation => !IsFileCompiled && !ErrorOn.HasValue && !IsBeingProcessed;
        public bool IsFileCompiled => FileCompiledOn.HasValue;
        public DateTime? FileCompiledOn { get; set; }

        public string AssemblyNameCompiledIn { get; set; }

        public bool IsAwaitingHotSwap => IsFileCompiled && !HotSwappedOn.HasValue;
        public DateTime? HotSwappedOn { get; set; }
        public bool IsChangeHotSwapped => HotSwappedOn.HasValue;

        public string ErrorText { get; set; }
        public DateTime? ErrorOn { get; set; }
        public bool IsFailed => ErrorOn.HasValue;
        public bool IsBeingProcessed { get; set; }
        public string SourceCodeCombinedFilePath { get; set; }

        public DynamicFileHotReloadState(string fullFileName, DateTime fileChangedOn)
        {
            FullFileName = fullFileName;
            FileChangedOn = fileChangedOn;
        }
    }

    public enum FileWatcherImplementation
    {
        UnityDefault = 0,
#if UNITY_EDITOR_WIN && UNITY_2021_1_OR_NEWER
        DirectWindowsApi = 1,
#endif
        CustomPolling = 2
    }
}