using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastScriptReload.Runtime;
using ImmersiveVRTools.Editor.Common.Utilities;
using ImmersiveVRTools.Editor.Common.WelcomeScreen;
using ImmersiveVRTools.Editor.Common.WelcomeScreen.GuiElements;
using ImmersiveVRTools.Editor.Common.WelcomeScreen.PreferenceDefinition;
using ImmersiveVRTools.Editor.Common.WelcomeScreen.Utilities;
using ImmersiveVrToolsCommon.Runtime.Logging;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace FastScriptReload.Editor
{
    public class FastScriptReloadWelcomeScreen : ProductWelcomeScreenBase 
    {
        public static string BaseUrl = "https://immersivevrtools.com";
        public static string GenerateGetUpdatesUrl(string userId, string versionId)
        {
            //WARN: the URL can sometimes be adjusted, make sure updated correctly
            return $"{BaseUrl}/updates/fast-script-reload/{userId}?CurrentVersion={versionId}";
        }
        public static string VersionId = "1.8";
        private static readonly string ProjectIconName = "ProductIcon64";
        public static readonly string ProjectName = "fast-script-reload";

        private static Vector2 _WindowSizePx = new Vector2(650, 500);
        private static string _WindowTitle = "Fast Script Reload";

        public static ChangeMainViewButton ExclusionsSection { get; private set; }
        public static ChangeMainViewButton InspectError { get; private set; }
        public static ChangeMainViewButton ReloadSection { get; private set; }

        public static DynamicFileHotReloadState LastInspectFileHotReloadStateError;

        public void OpenInspectError(DynamicFileHotReloadState fileHotReloadState)
        {
            LastInspectFileHotReloadStateError = fileHotReloadState;
            InspectError.OnClick(this);
        }
        
        public void OpenExclusionsSection()
        {
            if (ExclusionsSection != null)
            {
                ExclusionsSection.OnClick(this);
            }
        }

        private static readonly List<GuiSection> LeftSections = CreateLeftSections(new List<ChangeMainViewButton>
            {
                new ChangeMainViewButton("On-Device\r\nHot-Reload",  
                    (screen) =>
                    {
                        EditorGUILayout.LabelField("Live Script Reload", screen.BoldTextStyle); 
                        
                        GUILayout.Space(10);
                        EditorGUILayout.LabelField(@"此资源有一个扩展，允许您在构建中包含热重载功能（独立版 / Android），请点击下面的按钮了解更多信息。", screen.TextStyle);

                        GUILayout.Space(20);
                        if (GUILayout.Button("在资源商店查看 Live Script Reload"))
                        {
                            Application.OpenURL($"{RedirectBaseUrl}/live-script-reload-extension");
                        }
                    }
                )
            }, 
            new LaunchSceneButton("Basic Example", (s) =>
            {
                var path = GetScenePath("ExampleScene");
                if (path == null)
                {
                    var userChoice = EditorUtility.DisplayDialogComplex("未找到示例",
                        "未找到示例场景。如果您通过包管理器获取了 FSR，请确保导入示例。", 
                        "确定", "关闭", "打开包管理器");
                    if (userChoice == 2)
                    {
                        UnityEditor.PackageManager.UI.Window.Open("com.fastscriptreload");
                    }
                }

                return path;
            }, (screen) =>
            {
                GUILayout.Label(
                    $@"工具使用非常简单：

1) 点击播放开始。
2) 打开 'FunctionLibrary.cs' ({@"Assets/FastScriptReload/Examples/Scripts/"})", screen.TextStyle);
                
                CreateOpenFunctionLibraryOnRippleMethodButton();

                
                GUILayout.Label(
                    $@"3) 修改 'Ripple' 方法（例如将 return 语句前的行改为 'p.z = v * 10'
4) 保存文件
5) 立即看到变化",
                    screen.TextStyle
                );
                
                GUILayout.Space(10);
                EditorGUILayout.HelpBox("热重载有一些限制，文档在 'limitations' 部分列出了这些限制。", MessageType.Warning);
            }));

        static void OnScriptHotReloadNoInstance() 
        { 
            Debug.Log("Reloaded - start");
            LastInspectFileHotReloadStateError = (DynamicFileHotReloadState) HarmonyLib.AccessTools
                .Field("FastScriptReload.Editor.FastScriptReloadWelcomeScreen:LastInspectFileHotReloadStateError")
                .GetValue(null);
            Debug.Log("Reloaded - end");
        }
        
        protected static List<GuiSection> CreateLeftSections(List<ChangeMainViewButton> additionalSections, LaunchSceneButton launchSceneButton)
        {
            return new List<GuiSection>() {
                new GuiSection("", new List<ClickableElement>
                {
                    (InspectError = new ChangeMainViewButton("Error - Inspect", (screen) =>
                    {
            if (FastScriptReloadWelcomeScreen.LastInspectFileHotReloadStateError == null)
            {
                GUILayout.Label(
                    @"未选择错误。可能已被域重载清除。

请选择左侧的其他页签。", screen.TextStyle);
                return;
            }


            EditorGUILayout.HelpBox(
                @"错误通常是由于编译/重写问题导致的。有一些方法可以缓解这些问题。",
                MessageType.Warning);
            GUILayout.Space(10);

            GUILayout.Label("1) 检查编译错误，特别关注导致错误的具体行号：");
            EditorGUILayout.HelpBox(
                @"例如，下面的错误显示第 940 行由于缺少 #endif 指令而导致编译问题。

System.Exception: Compiler failed to produce the assembly. 
Output: '<filepath>.SourceCodeCombined.cs(940,1): error CS1027: #endif directive expected'",
                MessageType.Info);

            GUILayout.Space(10);
            GUILayout.Label("错误：");
            GUILayout.TextArea(LastInspectFileHotReloadStateError.ErrorText);

            GUILayout.Space(10);
            if (GUILayout.Button("2) 点击此处打开编译失败时生成的文件"))
            {
                InternalEditorUtility.OpenFileAtLineExternal(LastInspectFileHotReloadStateError.SourceCodeCombinedFilePath, 1);
            }

            GUILayout.Label(
                @"错误可能是您在源文件中创建的普通编译问题（例如拼写错误），在这种情况下，请修复后它会重新编译。

编译失败可能是由于现有限制导致的，虽然我一直在努力缓解这些限制，但最好您能了解它们在哪里。

请查看文档（上面的链接）以更好地理解它们。
如果需要，文档中还包含解决方法。");

            GUILayout.Space(10);
            GUILayout.Label(@"您可以帮助改进 FSR！", screen.BoldTextStyle);
            EditorGUILayout.HelpBox(@"您能否通过提供错误详情来帮助改进这个工具？ 
我可以使用这些信息来重现问题并修复限制。

只需点击下面的按钮 - 它会自动创建支持包。

支持包包含：
1) 导致错误的原始脚本文件
2) 生成的补丁脚本文件
3) 错误消息", MessageType.Warning);

            if (GUILayout.Button("3) 点击此处创建支持包"))
            {
                try
                {
                    var folder = EditorUtility.OpenFolderPanel("选择文件夹", "", "");
                    var sourceCodeCombinedFile = new FileInfo(LastInspectFileHotReloadStateError.SourceCodeCombinedFilePath);
                    var originalFile = new FileInfo(LastInspectFileHotReloadStateError.FullFileName);
                    File.Copy(LastInspectFileHotReloadStateError.SourceCodeCombinedFilePath, Path.Combine(folder, sourceCodeCombinedFile.Name));
                    File.Copy(LastInspectFileHotReloadStateError.FullFileName, Path.Combine(folder, originalFile.Name));
                    File.WriteAllText(Path.Combine(folder, "error-message.txt"), LastInspectFileHotReloadStateError.ErrorText);
                    
                    EditorUtility.DisplayDialog("支持包已创建", $"谢谢！\r\n\r\n请将文件夹中的文件发送：\r\n'{folder}'\r\n\r\n到：\r\n\r\nsupport@immersivevrtools.com", "确定，复制邮箱到剪贴板");
                    EditorGUIUtility.systemCopyBuffer = "support@immersivevrtools.com";
                }
                catch (Exception e)
                {
                    Debug.LogError($"无法创建支持包。{e}");
                }
            }
                    })).WithShouldRender(() => LastInspectFileHotReloadStateError != null), 
                    new LastUpdateButton("New Update!", (screen) => LastUpdateUpdateScrollViewSection.RenderMainScrollViewSection(screen)),
                }),
                new GuiSection("Options", new List<ClickableElement>
                {
                    (ReloadSection = new ChangeMainViewButton("Reload", (screen) =>
                    {
                        const int sectionBreakHeight = 15;
                        GUILayout.Label(
                            @"工具会监控所有脚本文件，并在文件变化时自动进行热重载。",
                            screen.TextStyle
                        );
                
                        using (new EditorGUI.DisabledGroupScope((bool)FastScriptReloadPreference.WatchOnlySpecified.GetEditorPersistedValueOrDefault()))
                        using (LayoutHelper.LabelWidth(320))
                        {
                            // 手动渲染 EnableAutoReloadForChangedFiles 的 Toggle
                            var oldValue = FastScriptReloadPreference.EnableAutoReloadForChangedFiles;
                            var newValue = EditorGUILayout.Toggle("启用自动热重载", oldValue);
                            if (newValue != oldValue)
                            {
                                FastScriptReloadPreference.EnableAutoReloadForChangedFiles = newValue;
                            }
                        }
                        
                        GUILayout.Space(sectionBreakHeight);

                        using (LayoutHelper.LabelWidth(320))
                        {
                            ProductPreferenceBase.RenderGuiAndPersistInput(FastScriptReloadPreference.WatchOnlySpecified);
                        }

                        if ((bool)FastScriptReloadPreference.WatchOnlySpecified.GetEditorPersistedValueOrDefault())
                        {
                            EditorGUILayout.HelpBox(@"使用手动监控模式时，您需要在项目窗口中右键点击文件/文件夹，然后选择 'Watch File'", MessageType.Info);
                        }
                        GUILayout.Space(sectionBreakHeight);

                        GUILayout.Label(
                            @"出于性能考虑，脚本变更会被批处理，每 N 秒重载一次。",
                            screen.TextStyle
                        );

                        using (LayoutHelper.LabelWidth(300))
                        {
                            ProductPreferenceBase.RenderGuiAndPersistInput(FastScriptReloadPreference.BatchScriptChangesAndReloadEveryNSeconds);
                        }

                        GUILayout.Space(sectionBreakHeight);
                        
                        using (LayoutHelper.LabelWidth(350))
                        {
                            ProductPreferenceBase.RenderGuiAndPersistInput(FastScriptReloadPreference.IsForceLockAssembliesViaCode);
                        }
                        EditorGUILayout.HelpBox(
@"有时即使关闭了 Auto-Refresh，Unity 仍会在 Play Mode 下继续重载程序集。

使用此设置可以通过代码强制锁定程序集。"
, MessageType.Info);
                        GUILayout.Space(sectionBreakHeight);

                        using (LayoutHelper.LabelWidth(430))
                        {
                            ProductPreferenceBase.RenderGuiAndPersistInput(FastScriptReloadPreference.IsVisualHotReloadIndicationShownInProjectWindow);
                        }
                        
                        GUILayout.Space(sectionBreakHeight);
                    })),
                    (ExclusionsSection = new ChangeMainViewButton("Exclusions", (screen) => 
                    {
                        EditorGUILayout.HelpBox("排除列表最容易通过项目窗口管理，只需右键点击脚本文件并选择： " +
                                                "\r\nFast Script Reload -> Add Hot-Reload Exclusion " +
                                                "\r\nFast Script Reload -> Remove Hot-Reload Exclusion", MessageType.Info);
                        GUILayout.Space(10);
                
                        ProductPreferenceBase.RenderGuiAndPersistInput(FastScriptReloadPreference.FilesExcludedFromHotReload);
                    })),
                    new ChangeMainViewButton("Debugging", (screen) =>
                    {
                        GUILayout.Space(20);
                        using (LayoutHelper.LabelWidth(350))
                        {
                            EditorGUILayout.LabelField("日志记录", screen.BoldTextStyle);
                            GUILayout.Space(5);
                            ProductPreferenceBase.RenderGuiAndPersistInput(FastScriptReloadPreference.EnableDetailedDebugLogging);
                            ProductPreferenceBase.RenderGuiAndPersistInput(FastScriptReloadPreference.StopShowingAutoReloadEnabledDialogBox);
                        }
                    })
                }.Concat(additionalSections).ToList()),
                new GuiSection("Advanced", new List<ClickableElement>
                {
                    new ChangeMainViewButton("File Watchers", (screen) => 
                    {
                        EditorGUILayout.HelpBox(
                            $@"工具监控 .cs 文件的变更。不幸的是，Unity 的 FileWatcher 
实现存在一些性能问题。

默认情况下，可以监控所有项目目录，您可以在此处进行调整。

path - 要监控的目录
filter - 缩小文件范围以匹配过滤器，例如所有 *.cs 文件 (*.cs)
includeSubdirectories - 是否同时监控子目录

{FastScriptReloadManager.FileWatcherReplacementTokenForApplicationDataPath} - 您可以使用此令牌，它将被替换为您的 /Assets 文件夹"
                            , MessageType.Info);
                        
                        EditorGUILayout.HelpBox("更改文件监控器设置后需要重新编译才能重新加载。", MessageType.Warning);
                        GUILayout.Space(10);

                        using (LayoutHelper.LabelWidth(240))
                        {
                            ProductPreferenceBase.RenderGuiAndPersistInput(FastScriptReloadPreference.FileWatcherImplementationInUse);
                        }
                        EditorGUILayout.HelpBox(
@"DefaultUnity - 在某些编辑器版本中可能很慢或根本不触发
DirectWindowsApi - （实验性）直接使用 Windows API，更快（不支持符号链接）
CustomPolling - （实验性）通过手动轮询监控文件变更，最慢。确保将监控器范围缩小到脚本文件夹", MessageType.Info);

                        ProductPreferenceBase.RenderGuiAndPersistInput(FastScriptReloadPreference.FileWatcherSetupEntries);
                    })
                }),
                new GuiSection("Launch Demo", new List<ClickableElement>
                {
                    launchSceneButton
                })
            };
        }

        private static readonly string RedirectBaseUrl = "https://immersivevrtools.com/redirect/fast-script-reload"; 
        private static readonly ScrollViewGuiSection EmptyScrollViewSection = new ScrollViewGuiSection("", (screen) => { });
        private static readonly GuiSection TopSection = CreateTopSectionButtons(RedirectBaseUrl);

        protected static GuiSection CreateTopSectionButtons(string redirectBaseUrl)
        {
            return new GuiSection("Support", new List<ClickableElement>
                {
                    new OpenUrlButton("Documentation", $"{redirectBaseUrl}/documentation"),
                    new OpenUrlButton("Discord", $"{redirectBaseUrl}/discord"),
                    new OpenUrlButton("Github", $"{redirectBaseUrl}/github"),
                    new OpenUrlButton("Donate", $"{redirectBaseUrl}/donate", "sv_icon_name3")
                }
            );
        }

        private static readonly GuiSection BottomSection = new GuiSection(
            "I want to make this tool better. And I need your help!",
            $"Please spread the word and star github repo. Alternatively if you're in a position to make a donation I'd hugely appreciate that. It allows me to spend more time on the tool instead of paid client projects.",
            new List<ClickableElement>
            {
                new OpenUrlButton(" Star on Github", $"{RedirectBaseUrl}/github"),
                new OpenUrlButton(" Donate", $"{RedirectBaseUrl}/donate"),
            }
        );

        public override string WindowTitle { get; } = _WindowTitle;
        public override Vector2 WindowSizePx { get; } = _WindowSizePx;

#if !LiveScriptReload_Enabled
        [MenuItem("Window/Fast Script Reload/Start Screen", false, 1999)]
#endif
        public static FastScriptReloadWelcomeScreen Init()
        {
            return OpenWindow<FastScriptReloadWelcomeScreen>(_WindowTitle, _WindowSizePx);
        }

        public void OnEnable()
        {
            OnEnableCommon(ProjectIconName);
            // Set default view to Reload section
            if (ReloadSection != null)
            {
                ReloadSection.OnClick(this);
            }
        }

        public void OnGUI()
        {
            RenderGUI(LeftSections, TopSection, BottomSection, EmptyScrollViewSection);
        }
        
        protected static void CreateOpenFunctionLibraryOnRippleMethodButton()
        {
            if (GUILayout.Button("Open 'FunctionLibrary.cs'"))
            {
                var codeComponent = AssetDatabase.LoadAssetAtPath<MonoScript>(AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets($"t:Script FunctionLibrary")[0]));
                CodeEditorManager.GotoScript(codeComponent, "Ripple");
            }
        }
    }

#if !LiveScriptReload_Enabled
    [InitializeOnLoad]
#endif
    public class FastScriptReloadWelcomeScreenInitializer : WelcomeScreenInitializerBase
    {
#if !LiveScriptReload_Enabled
        static FastScriptReloadWelcomeScreenInitializer()
        {
            var userId = ProductPreferenceBase.CreateDefaultUserIdDefinition(FastScriptReloadWelcomeScreen.ProjectName).GetEditorPersistedValueOrDefault().ToString();

            HandleUnityStartup(
                () => FastScriptReloadWelcomeScreen.Init(),
                FastScriptReloadWelcomeScreen.GenerateGetUpdatesUrl(userId, FastScriptReloadWelcomeScreen.VersionId),
                new List<ProjectEditorPreferenceDefinitionBase>(),
                (isFirstRun) =>
                {
                    MigrateObsoleteEnableCustomFileWatcherPreference();
                }
            );
            
            InitCommon();
        }

        private static void MigrateObsoleteEnableCustomFileWatcherPreference()
        {
#pragma warning disable CS0618 // Type or member is obsolete
            if ((bool)FastScriptReloadPreference.EnableCustomFileWatcher.GetEditorPersistedValueOrDefault())
            {
                FastScriptReloadPreference.FileWatcherImplementationInUse.SetEditorPersistedValue(FileWatcherImplementation.CustomPolling);
                FastScriptReloadPreference.EnableCustomFileWatcher.SetEditorPersistedValue(false);
            }
#pragma warning restore CS0618 // Type or member is obsolete
        }
#endif
        
        protected static void InitCommon()
        {
            DisplayMessageIfLastDetourPotentiallyCrashedEditor();
            EnsureUserAwareOfAutoRefresh();

            BuildDefineSymbolManager.SetBuildDefineSymbolState(FastScriptReloadPreference.BuildSymbol_DetailedDebugLogging,
                (bool)FastScriptReloadPreference.EnableDetailedDebugLogging.GetEditorPersistedValueOrDefault()
            );
        }

        private static void EnsureUserAwareOfAutoRefresh()
        {
            var autoRefreshMode = (AssetPipelineAutoRefreshMode)EditorPrefs.GetInt("kAutoRefreshMode", EditorPrefs.GetBool("kAutoRefresh") ? 1 : 0);
            if (autoRefreshMode != AssetPipelineAutoRefreshMode.Enabled)
                return;
            
            if ((bool)FastScriptReloadPreference.IsForceLockAssembliesViaCode.GetEditorPersistedValueOrDefault())
                return;
            
            LoggerScoped.LogWarning("Fast Script Reload - asset auto refresh enabled - full reload will be triggered unless editor preference adjusted - see documentation for more details.");

            if ((bool)FastScriptReloadPreference.StopShowingAutoReloadEnabledDialogBox.GetEditorPersistedValueOrDefault())
                return;

            var chosenOption = EditorUtility.DisplayDialogComplex("Fast Script Reload - 警告",
                "已启用资源/脚本的自动重载。" +
                $"\n\n这意味着在 Play Mode 下进行的任何更改都可能触发完整重新编译。" +
                $"\r\n\r\n这是编辑器设置，可以随时通过 Edit -> Preferences -> Asset Pipeline -> Auto Refresh 进行调整" +
                $"\r\n\r\n我现在也可以为您调整 - 这意味着您需要手动加载更改（在 Play Mode 外）通过 Assets -> Refresh (CTRL + R)。" +
                $"\r\n\r\n在某些编辑器版本中，您还可以设置脚本编译在 Play Mode 外进行，无需手动刷新。 " +
                $"\r\n\r\n根据版本，您可以通过以下方式找到： " +
                $"\r\n1) Edit -> Preferences -> General -> Script Changes While Playing -> Recompile After Finished Playing." +
                $"\r\n2) Edit -> Preferences -> Asset Pipeline -> Auto Refresh -> Enabled Outside Playmode",
                "确定，禁用资源自动刷新（我需要在时手动刷新）",
                "不，不更改（停止显示此消息）",
                "不，不更改"
            );

            switch (chosenOption)
            {
                // change.
                case 0:
                    EditorPrefs.SetInt("kAutoRefreshMode", (int)AssetPipelineAutoRefreshMode.Disabled);
                    EditorPrefs.SetInt("kAutoRefresh", 0); //older unity versions
                    break;

                // don't change and stop showing message.
                case 1:
                    FastScriptReloadPreference.StopShowingAutoReloadEnabledDialogBox.SetEditorPersistedValue(true);

                    break;

                // don't change
                case 2:

                    break;

                default:
                    LoggerScoped.LogError("Unrecognized option.");
                    break;
            }
                
            
        }

        //copied from internal UnityEditor.AssetPipelineAutoRefreshMode
        internal enum AssetPipelineAutoRefreshMode
        {
            Disabled,
            Enabled,
            EnabledOutsidePlaymode,
        }

        private static void DisplayMessageIfLastDetourPotentiallyCrashedEditor()
        {
            const string firstInitSessionKey = "FastScriptReloadWelcomeScreenInitializer_FirstInitDone";
            if (!SessionState.GetBool(firstInitSessionKey, false))
            {
                SessionState.SetBool(firstInitSessionKey, true);

                var lastDetour = DetourCrashHandler.RetrieveLastDetour();
                if (!string.IsNullOrEmpty(lastDetour))
                {
                    EditorUtility.DisplayDialog("Fast Script Reload",
                        $@"这很尴尬！

看起来我让您的编辑器崩溃了，抱歉！

最后重定向的方法是：'{lastDetour}'

如果再次发生这种情况，请通过支持联系我们，我们会解决它。

In the meantime, you can exclude any file from Hot-Reload by 
1) right-clicking on .cs file in Project menu
2) Fast Script Reload 
3) Add Hot-Reload Exclusion
", "Ok");
                    DetourCrashHandler.ClearDetourLog();
                }
            }
        }
    }
}

