using System;
using System.Collections.Generic;
using System.Linq;
using FastScriptReload.Editor;
using UnityEditor;
using UnityEngine;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// Context menu items for Fast Script Reload in the Unity Editor
    /// </summary>
    public static class FastScriptReloadContextMenu
    {
        private const int BaseMenuItemPriority_ManualScriptOverride = 100;
        private const int BaseMenuItemPriority_Exclusions = 200;
        private const int BaseMenuItemPriority_FileWatcher = 300;
        
        private const string WatchSpecificFileOrFolderMenuItemName = "Assets/Fast Script Reload/Watch File\\Folder";
        
        #region File Watcher Menu Items
        
        [MenuItem(WatchSpecificFileOrFolderMenuItemName, true, BaseMenuItemPriority_FileWatcher + 1)]
        public static bool ToggleSelectionFileWatchersSetupValidation()
        {
            if (!(bool)FastScriptReloadPreference.WatchOnlySpecified.GetEditorPersistedValueOrDefault())
            {
                return false;
            }
            
            Menu.SetChecked(WatchSpecificFileOrFolderMenuItemName, false);

            var isSelectionContaininingFolderOrScript = false;
            for (var i = 0; i < Selection.objects.Length; i++)
            {
                if (Selection.objects[i] is MonoScript selectedMonoScript)
                {
                    isSelectionContaininingFolderOrScript = true;

                    if (FastScriptReloadManager.IsFileWatcherSetupEntryAlreadyPresent(selectedMonoScript))
                    {
                        Menu.SetChecked(WatchSpecificFileOrFolderMenuItemName, true);
                        break;
                    }
                }
                else if (Selection.objects[i] is DefaultAsset selectedAsset)
                {
                    isSelectionContaininingFolderOrScript = true;

                    if (FastScriptReloadManager.IsFileWatcherSetupEntryAlreadyPresent(selectedAsset))
                    {
                        Menu.SetChecked(WatchSpecificFileOrFolderMenuItemName, true);
                        break;
                    }
                }
            }

            return isSelectionContaininingFolderOrScript;
        }

        /// <summary>Used to add/remove scripts/folders to the <see cref="FastScriptReloadPreference.FileWatcherSetupEntries"/></summary>
        [MenuItem(WatchSpecificFileOrFolderMenuItemName, false, BaseMenuItemPriority_FileWatcher + 1)]
        public static void ToggleSelectionFileWatchersSetup()
        {
            var isFileWatchersChange = false;
            for (var i = 0; i < Selection.objects.Length; i++)
            {
                if (Selection.objects[i] is MonoScript selectedMonoScript)
                {
                    if (FastScriptReloadManager.IsFileWatcherSetupEntryAlreadyPresent(selectedMonoScript, out var foundFileWatcherSetupEntry))
                    {
                        FastScriptReloadPreference.FileWatcherSetupEntries.RemoveElement(JsonUtility.ToJson(foundFileWatcherSetupEntry));
                    }
                    else
                    {
                        FastScriptReloadPreference.FileWatcherSetupEntries.AddElement(JsonUtility.ToJson(foundFileWatcherSetupEntry));
                    }
                    
                    isFileWatchersChange = true;
                }
                else if (Selection.objects[i] is DefaultAsset selectedAsset)
                {
                    if (FastScriptReloadManager.IsFileWatcherSetupEntryAlreadyPresent(selectedAsset, out var foundFileWatcherSetupEntry))
                    {
                        FastScriptReloadPreference.FileWatcherSetupEntries.RemoveElement(JsonUtility.ToJson(foundFileWatcherSetupEntry));
                    }
                    else
                    {
                        FastScriptReloadPreference.FileWatcherSetupEntries.AddElement(JsonUtility.ToJson(foundFileWatcherSetupEntry));
                    }
                    
                    isFileWatchersChange = true;
                }
            }

            if (isFileWatchersChange)
            {
                FastScriptReloadPreference.FileWatcherSetupEntriesChanged = true; // Ensures file watcher are updated in play mode

                /// When in <see cref="FastScriptReloadPreference.WatchOnlySpecified"/> mode, <see cref="FastScriptReloadPreference.EnableAutoReloadForChangedFiles"/> state is managed automatically (disabled when no file watcher)
                if ((bool)FastScriptReloadPreference.WatchOnlySpecified.GetEditorPersistedValueOrDefault())
                {
                    var isAnyFileWatcherSet = FastScriptReloadPreference.FileWatcherSetupEntries.GetElements().Any();
                    FastScriptReloadPreference.EnableAutoReloadForChangedFiles.SetEditorPersistedValue(isAnyFileWatcherSet);
                }
            }
        }

        [MenuItem("Assets/Fast Script Reload/Clear Watched Files", true, BaseMenuItemPriority_FileWatcher + 2)]
        public static bool ClearFastScriptReloadValidation()
        {
            if (!(bool)FastScriptReloadPreference.WatchOnlySpecified.GetEditorPersistedValueOrDefault())
            {
                return false;
            }

            return FastScriptReloadPreference.FileWatcherSetupEntries.GetElements().Any();
        }
        
        [MenuItem("Assets/Fast Script Reload/Clear Watched Files", false, BaseMenuItemPriority_FileWatcher + 2)]
        public static void ClearFileWatchersEntries()
        {
            foreach (var item in FastScriptReloadPreference.FileWatcherSetupEntries.GetElements())
            {
                FastScriptReloadPreference.FileWatcherSetupEntries.RemoveElement(item);
            }
            Debug.LogWarning("File Watcher Setup has been cleared - make sure to add some.");

            FastScriptReloadPreference.EnableAutoReloadForChangedFiles.SetEditorPersistedValue(false);

            FastScriptReloadManager.ClearFileWatchers();
        }
        
        #endregion
        
        #region Exclusion Menu Items
        
        [MenuItem("Assets/Fast Script Reload/Add Hot-Reload Exclusion", false, BaseMenuItemPriority_Exclusions + 1)]
        public static void AddFileAsExcluded()
        {
            FastScriptReloadPreference.FilesExcludedFromHotReload.AddElement(ResolveRelativeToAssetDirectoryFilePath(Selection.activeObject));
        }

        [MenuItem("Assets/Fast Script Reload/Add Hot-Reload Exclusion", true)]
        public static bool AddFileAsExcludedValidateFn()
        {
            return Selection.activeObject is MonoScript
                   && !((FastScriptReloadPreference.FilesExcludedFromHotReload.GetEditorPersistedValueOrDefault() as IEnumerable<string>) ?? Array.Empty<string>())
                       .Contains(ResolveRelativeToAssetDirectoryFilePath(Selection.activeObject));
        }

        [MenuItem("Assets/Fast Script Reload/Remove Hot-Reload Exclusion", false, BaseMenuItemPriority_Exclusions + 2)]
        public static void RemoveFileAsExcluded()
        {
            FastScriptReloadPreference.FilesExcludedFromHotReload.RemoveElement(ResolveRelativeToAssetDirectoryFilePath(Selection.activeObject));
        }
    
        [MenuItem("Assets/Fast Script Reload/Remove Hot-Reload Exclusion", true)]
        public static bool RemoveFileAsExcludedValidateFn()
        {
            return Selection.activeObject is MonoScript
                   && ((FastScriptReloadPreference.FilesExcludedFromHotReload.GetEditorPersistedValueOrDefault() as IEnumerable<string>) ?? Array.Empty<string>())
                   .Contains(ResolveRelativeToAssetDirectoryFilePath(Selection.activeObject));
        }
    
        [MenuItem("Assets/Fast Script Reload/Show Exclusions", false, BaseMenuItemPriority_Exclusions + 3)]
        public static void ShowExcludedFilesInUi()
        {
            var window = FastScriptReloadWelcomeScreen.Init();
            window.OpenExclusionsSection();
        }
        
        #endregion
        
        #region Helper Methods
        
        private static string ResolveRelativeToAssetDirectoryFilePath(UnityEngine.Object obj)
        {
            return AssetDatabase.GetAssetPath(obj.GetInstanceID());
        }
        
        #endregion
    }
}

