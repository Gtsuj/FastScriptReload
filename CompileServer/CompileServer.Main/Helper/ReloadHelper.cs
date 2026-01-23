using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CompileServer.Helper;
using HookInfo.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace CompileServer.Services
{
    /// <summary>
    /// 热重载辅助类 - 全局状态管理
    /// 与 Unity 端的 ReloadHelper 保持一致
    /// </summary>
    public static class ReloadHelper
    {
        public static readonly WriterParameters WRITER_PARAMETERS = new() { WriteSymbols = true, SymbolWriterProvider = new PortablePdbWriterProvider() };

        public static readonly ReaderParameters READER_PARAMETERS = new() { ReadSymbols = true };
        
        /// <summary>
        /// 程序集保存路径
        /// 基于项目路径或当前工作目录，创建临时目录用于保存编译后的程序集
        /// </summary>
        public static string AssemblyPath
        {
            get
            {
                if (_assemblySavePath == null)
                {
                    // 获取项目名
                    string projectName = Path.GetFileName(_projectPath);
                    if (string.IsNullOrEmpty(projectName))
                    {
                        var dirInfo = new DirectoryInfo(_projectPath);
                        projectName = dirInfo.Name ?? "UnityProject";
                    }

                    // 创建保存目录：%LOCALAPPDATA%\Temp\FastScriptReloadTemp\{工程名}
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _assemblySavePath = Path.Combine(localAppData, "Temp", "FastScriptReloadTemp", projectName);
                }

                if (!Directory.Exists(_assemblySavePath))
                {
                    Directory.CreateDirectory(_assemblySavePath);
                }

                return _assemblySavePath;
            }
        }

        /// <summary>
        /// 程序集输出路径（用于保存 Wrapper 程序集）
        /// </summary>
        public static string AssemblyOutputPath
        {
            get
            {
                var path = Path.Combine(AssemblyPath, "Output");
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                return path;
            }
        }

        /// <summary>
        /// 程序集输出路径（用于保存 Wrapper 程序集）
        /// </summary>
        public static string AssemblyOutputTempPath
        {
            get
            {
                var path = Path.Combine(AssemblyPath, "OutputTemp");
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                return path;
            }
        }

        /// <summary>
        /// BaseDLL 路径（用于保存原始程序集的拷贝，避免持有原始文件句柄）
        /// </summary>
        public static string BaseDllPath
        {
            get
            {
                var path = Path.Combine(AssemblyPath, "BaseDLL");
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                return path;
            }
        }

        /// <summary>
        /// 修改过的类型缓存
        /// 用于支持多次编译累积和新增方法的多次 Hook
        /// </summary>
        public static Dictionary<string, HookTypeInfo> HookTypeInfoCache = new();

        public static Dictionary<string, AssemblyContext> AssemblyContext { get; private set; }

        public static CSharpParseOptions ParseOptions { get; private set; }

        private static string _assemblySavePath;

        private static string _projectPath;

        /// <summary>
        /// 引用路径 -> MetadataReference 的缓存
        /// </summary>
        private static readonly Dictionary<string, MetadataReference> _metadataReferencesCache = new();

        /// <summary>
        /// 程序集名称 -> AssemblyDefinition 的缓存（懒加载）
        /// </summary>
        private static readonly Dictionary<string, AssemblyDefinition> _originalAssemblyCache = new();


        public static void Initialize(InitializeRequest request)
        {
            AssemblyContext = request.AssemblyContexts;
            _projectPath = request.ProjectPath;
            _assemblySavePath = null;

            ParseOptions = new CSharpParseOptions(
                preprocessorSymbols: request.PreprocessorDefines,
                languageVersion: LanguageVersion.Latest
            );
            
            Clear();

            if (AssemblyContext != null)
            {
                CopyAssembliesToBaseDll();
            }
        }

        /// <summary>
        /// 清除所有缓存和临时文件
        /// </summary>
        public static void Clear()
        {
            ClearHookInfo();

            // 清除BaseDLL目录
            var baseDllPath = BaseDllPath;
            if (Directory.Exists(baseDllPath))
            {
                Directory.Delete(baseDllPath, true);
            }

            // 清除 MetadataReference 缓存
            _metadataReferencesCache.Clear();

            // 释放并清除原始程序集缓存
            foreach (var assemblyDef in _originalAssemblyCache.Values)
            {
                assemblyDef?.Dispose();
            }
            _originalAssemblyCache.Clear();
        }

        public static void ClearHookInfo()
        {
            // 清除类型缓存
            HookTypeInfoCache.Clear();

            // 清除Output目录
            try
            {
                Directory.Delete(AssemblyOutputTempPath, true);
                Directory.Delete(AssemblyOutputPath, true);
            }
            catch (Exception)
            {
                // ignored
            }
        }

        /// <summary>
        /// 获取程序集的 MetadataReference 列表
        /// </summary>
        /// <param name="assemblyName">程序集名称</param>
        /// <returns>MetadataReference 列表，如果程序集不存在则返回 null</returns>
        public static List<MetadataReference> GetMetadataReferences(string assemblyName)
        {
            // 如果程序集上下文不存在，返回 null
            if (AssemblyContext == null || !AssemblyContext.TryGetValue(assemblyName, out var context))
            {
                return null;
            }

            // 构建引用列表（包含原程序集）
            var references = new List<MetadataReference>();

            // 添加程序集引用
            if (context.References == null)
            {
                return null;
            }

            foreach (var reference in context.References)
            {
                if (!string.IsNullOrEmpty(reference.Path) && File.Exists(reference.Path))
                {
                    // 从缓存中获取或创建 MetadataReference
                    if (!_metadataReferencesCache.TryGetValue(reference.Path, out var metadataRef))
                    {
                        metadataRef = MetadataReference.CreateFromFile(reference.Path);
                        _metadataReferencesCache[reference.Path] = metadataRef;
                    }

                    references.Add(metadataRef);
                }
            }

            // 原程序集作为引用
            if (!string.IsNullOrEmpty(context.OutputPath) && File.Exists(context.OutputPath))
            {
                // 从缓存中获取或创建 MetadataReference
                if (!_metadataReferencesCache.TryGetValue(context.OutputPath, out var outputMetadataRef))
                {
                    outputMetadataRef = MetadataReference.CreateFromFile(context.OutputPath);
                    _metadataReferencesCache[context.OutputPath] = outputMetadataRef;
                }

                references.Add(outputMetadataRef);
            }

            return references;
        }

        /// <summary>
        /// 获取原始程序集的 AssemblyDefinition（懒加载）
        /// </summary>
        /// <param name="assemblyName">程序集名称</param>
        /// <returns>AssemblyDefinition，如果程序集不存在或文件不存在则返回 null</returns>
        public static AssemblyDefinition GetOriginalAssembly(string assemblyName)
        {
            // // 如果缓存中存在，直接返回
            // if (_originalAssemblyCache.TryGetValue(assemblyName, out var cachedAssembly))
            // {
            //     return cachedAssembly;
            // }

            // 如果程序集上下文不存在，返回 null
            if (AssemblyContext == null || !AssemblyContext.TryGetValue(assemblyName, out var context))
            {
                return null;
            }

            // 如果输出路径不存在，返回 null
            if (string.IsNullOrEmpty(context.OutputPath) || !File.Exists(context.OutputPath))
            {
                return null;
            }

            // 读取程序集并缓存
            try
            {
                var assemblyDef = AssemblyDefinition.ReadAssembly(context.OutputPath, READER_PARAMETERS);
                // _originalAssemblyCache[assemblyName] = assemblyDef;
                return assemblyDef;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"读取原始程序集 {assemblyName} 时出错: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// 将所有程序集及其引用拷贝到BaseDLL目录，并更新 _assemblyContext 中的路径
        /// </summary>
        private static void CopyAssembliesToBaseDll()
        {
            foreach (var context in AssemblyContext.Values)
            {
                // 拷贝并更新所有引用程序集路径
                foreach (var reference in context.References)
                {
                    var targetPath = CopyAssemblyToBaseDll(reference.Path);
                    if (!string.IsNullOrEmpty(targetPath))
                    {
                        reference.Path = targetPath;
                    }
                }

                // 拷贝并更新主程序集自身路径
                var targetOutputPath = CopyAssemblyToBaseDll(context.OutputPath);
                if (!string.IsNullOrEmpty(targetOutputPath))
                {
                    context.OutputPath = targetOutputPath;
                }
            }
        }

        /// <summary>
        /// 将程序集DLL和PDB文件拷贝到BaseDLL目录
        /// </summary>
        /// <param name="originalAssemblyPath">原始程序集路径</param>
        /// <returns>拷贝后的程序集路径，如果失败则返回null</returns>
        private static string CopyAssemblyToBaseDll(string originalAssemblyPath)
        {
            var dllFileName = Path.GetFileName(originalAssemblyPath);
            var targetDllPath = Path.Combine(BaseDllPath, dllFileName);

            if (File.Exists(targetDllPath))
            {
                return targetDllPath;
            }

            // 拷贝DLL文件
            File.Copy(originalAssemblyPath, targetDllPath, overwrite: true);

            // 拷贝PDB文件（如果存在）
            var pdbPath = Path.ChangeExtension(originalAssemblyPath, ".pdb");
            if (File.Exists(pdbPath))
            {
                var targetPdbPath = Path.ChangeExtension(targetDllPath, ".pdb");
                File.Copy(pdbPath, targetPdbPath, overwrite: true);
            }

            Console.WriteLine($"已拷贝程序集 {dllFileName} 到 BaseDLL 目录: {targetDllPath}");

            return targetDllPath;
        }
        
        public static string GetWrapperAssemblyName(string assemblyName)
        {
            return $"{assemblyName}---{Guid.NewGuid():N}";
        }

        /// <summary>
        /// 获取最新的方法定义
        /// 优先从 hookTypeInfo 的 ModifiedMethods 中获取，如果找不到则从 originalTypeDef 的方法映射中获取
        /// </summary>
        /// <param name="methodName">方法全名</param>
        /// <param name="typeFullName">类型全名</param>
        /// <param name="originalTypeDef">原始类型定义，可能为 null。如果为 null，将从原程序集中自动加载</param>
        /// <returns>旧方法的定义，如果找不到则返回 null</returns>
        public static MethodDefinition GetLatestMethodDefinition(string typeFullName, string methodName, TypeDefinition originalTypeDef = null)
        {
            if (string.IsNullOrEmpty(typeFullName))
            {
                return null;
            }

            // 获取 HookTypeInfo
            var hookTypeInfo = HookTypeInfoCache.GetValueOrDefault(typeFullName);
            TypeDefinition finalTypeDef = null;
            AssemblyDefinition assembly = null;

            // 优先使用改动的
            if (hookTypeInfo != null && hookTypeInfo.ModifiedMethods.TryGetValue(methodName, out var modifyMethod))
            {
                string assemblyPath = string.Empty;
                for (int i = modifyMethod.HistoricalHookedAssemblyPaths.Count - 1; i >= 0; i--)
                {
                    var historicalPath = modifyMethod.HistoricalHookedAssemblyPaths[i];
                    historicalPath = historicalPath.Replace(AssemblyOutputPath, AssemblyOutputTempPath);
                    if (File.Exists(historicalPath))
                    {
                        assemblyPath = historicalPath;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(assemblyPath))
                {
                    assembly = AssemblyDefinition.ReadAssembly(assemblyPath, READER_PARAMETERS);
                    finalTypeDef = assembly.MainModule.GetType(typeFullName);
                }
            }
            
            // 如果 originalTypeDef 为 null，尝试从原程序集中加载
            
            if (originalTypeDef == null && finalTypeDef == null)
            {
                var assemblyName = TypeInfoHelper.GetAssemblyNameFromType(typeFullName);
                assembly = GetOriginalAssembly(assemblyName);
                originalTypeDef = assembly.MainModule.GetType(typeFullName);
            }
            
            finalTypeDef ??= originalTypeDef;

            var methodDef = finalTypeDef?.Methods.FirstOrDefault(m => m.FullName == methodName);

            return methodDef;
        }
    }
}
