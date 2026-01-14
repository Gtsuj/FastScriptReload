using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HookInfo.Models;
using ImmersiveVrToolsCommon.Runtime.Logging;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 热重载辅助类 - 统一的热重载管理器
    /// - ReloadHelper.cs: 公共状态和初始化，CompileServer 连接管理
    /// - ReloadHelper.Hook.cs: Hook应用和执行
    ///
    /// 注意：编译和IL修改逻辑已迁移到独立的 CompileServer 服务中
    /// </summary>
    public static partial class ReloadHelper
    {
        private static string _assemblySavePath;

        /// <summary>
        /// HTTP编译客户端实例（全局共享）
        /// </summary>
        private static HttpCompileClient _httpCompileClient;

        /// <summary>
        /// 获取HTTP编译客户端实例
        /// </summary>
        public static HttpCompileClient GetHttpCompileClient()
        {
            return _httpCompileClient;
        }

        /// <summary>
        /// 初始化
        /// </summary>
        [InitializeOnLoadMethod]
        public static async void Init()
        {
            if (!FastScriptReloadPreference.EnableAutoReloadForChangedFiles)
            {
                return;
            }

            // 注册编辑器退出事件，确保 CompileServer 进程被停止
            EditorApplication.quitting += OnEditorQuitting;

            // 注册 Unity 编译完成事件，在编译完成时清除缓存
            CompilationPipeline.compilationFinished += OnCompilationFinished;

            // 创建 HTTP 客户端实例
#if ImmersiveVrTools_DebugEnabled
            _httpCompileClient = new HttpCompileClient("http://localhost:5000");
#else
            _httpCompileClient = new HttpCompileClient();
#endif
            // 连接到 CompileServer（如果未运行会自动启动）
            if (!await _httpCompileClient.ConnectAsync())
            {
                LoggerScoped.LogError("无法连接到 CompileServer，热重载功能将不可用");
                return;
            }

            if (_httpCompileClient.CheckInitialized())
            {
                var hookTypeInfos = _httpCompileClient.GetHookTypeInfos();
                ApplyHooks(hookTypeInfos);
                return;
            }

            // 启动和初始化 CompileServer
            await InitializeCompileServerAsync();
        }

        /// <summary>
        /// Unity 编译完成时的回调
        /// </summary>
        private static void OnCompilationFinished(object obj)
        {
            // 在 Unity 编译完成后，清除所有旧的 Output 和 HookTypeInfo 缓存
            _httpCompileClient?.ClearAsync();
        }

        /// <summary>
        /// 初始化 CompileServer（启动服务并初始化 TypeInfoService）
        /// </summary>
        private static async Task InitializeCompileServerAsync()
        {
            FastScriptReloadSceneOverlay.NotifyInitializationStart();

            try
            {
                LoggerScoped.LogDebug("开始初始化 CompileServer...");

                // 收集所有 Unity 程序集上下文
                var allAssemblies = CompilationPipeline.GetAssemblies();

                // 过滤：只处理 Assets 目录下的程序集（所有源文件都在 Assets 下）
                var dataPathAssemblies = allAssemblies
                    .Where(assembly => assembly.sourceFiles.All(sourceFile => sourceFile.StartsWith("Assets"))).ToArray();

                LoggerScoped.LogDebug($"找到 {dataPathAssemblies.Length} 个 Assets 目录下的程序集（共 {allAssemblies.Length} 个）");

                var assemblyContexts = new Dictionary<string, AssemblyContext>();

                foreach (var assembly in dataPathAssemblies)
                {
                    // 收集引用
                    var references = new List<AssemblyReference>();

                    // 添加程序集引用
                    foreach (var reference in assembly.assemblyReferences)
                    {
                        references.Add(new AssemblyReference
                        {
                            Name = reference.name,
                            Path = Path.GetFullPath(reference.outputPath)
                        });
                    }

                    // 添加编译引用
                    foreach (var compiledRef in assembly.compiledAssemblyReferences)
                    {
                        references.Add(new AssemblyReference
                        {
                            Name = Path.GetFileNameWithoutExtension(compiledRef),
                            Path = Path.GetFullPath(compiledRef) // 编译引用通常已经是绝对路径
                        });
                    }

                    var context = new AssemblyContext
                    {
                        Name = assembly.name,
                        OutputPath = Path.GetFullPath(assembly.outputPath),
                        SourceFiles = assembly.sourceFiles.Select(Path.GetFullPath).ToArray(),
                        References = references.ToArray(),
                        PreprocessorDefines = assembly.defines, // 使用程序集特定的预处理器定义
                        AllowUnsafeCode = assembly.compilerOptions.AllowUnsafeCode
                    };

                    assemblyContexts[assembly.name] = context;
                }

                // 初始化 CompileServer 的 TypeInfoService
                var success = await _httpCompileClient.InitializeAsync(assemblyContexts, EditorUserBuildSettings.activeScriptCompilationDefines);

                if (success)
                {
                    LoggerScoped.LogDebug($"CompileServer 初始化成功: {assemblyContexts.Count} 个程序集");
                }
                else
                {
                    LoggerScoped.LogError("CompileServer 初始化失败，热重载功能将不可用");
                }
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"初始化 CompileServer 时发生异常: {ex.Message}\n{ex.StackTrace}");
            }

            FastScriptReloadSceneOverlay.NotifyInitializationComplete();
        }

        /// <summary>
        /// Unity 编辑器退出时的处理
        /// </summary>
        private static void OnEditorQuitting()
        {
            if (_httpCompileClient != null)
            {
                LoggerScoped.LogDebug("Unity 编辑器退出，停止 CompileServer...");
                _httpCompileClient.StopLocalProcess();
                _httpCompileClient.Dispose();
                _httpCompileClient = null;
            }
        }

        public static void Dispose()
        {
            // 停止 CompileServer 进程
            if (_httpCompileClient != null)
            {
                _httpCompileClient.StopLocalProcess();
                _httpCompileClient.Dispose();
                _httpCompileClient = null;
                LoggerScoped.LogDebug("CompileServer 已停止");
            }

            CompilationPipeline.RequestScriptCompilation();
        }
    }
}