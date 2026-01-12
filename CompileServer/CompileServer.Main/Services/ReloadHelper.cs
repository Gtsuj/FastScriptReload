using System;
using System.Collections.Generic;
using System.IO;
using HookInfo.Models;

namespace CompileServer.Services
{
    /// <summary>
    /// 热重载辅助类 - 全局状态管理
    /// 与 Unity 端的 ReloadHelper 保持一致
    /// </summary>
    public static class ReloadHelper
    {
        private static string _assemblySavePath;
        private static string _projectPath;

        /// <summary>
        /// 设置项目路径（在初始化时调用）
        /// </summary>
        public static void SetProjectPath(string projectPath)
        {
            _projectPath = projectPath;
            _assemblySavePath = null; // 重置缓存，下次访问时重新计算
        }

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
                    string projectName;
                    if (!string.IsNullOrEmpty(_projectPath))
                    {
                        projectName = Path.GetFileName(_projectPath);
                        if (string.IsNullOrEmpty(projectName))
                        {
                            var dirInfo = new DirectoryInfo(_projectPath);
                            projectName = dirInfo.Name ?? "UnityProject";
                        }
                    }
                    else
                    {
                        // 如果没有项目路径，使用当前工作目录
                        projectName = Path.GetFileName(Environment.CurrentDirectory) ?? "CompileServer";
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
        }

        public static void ClearHookInfo()
        {
            // 清除类型缓存
            HookTypeInfoCache.Clear();   

            // 清除Output目录
            var outputPath = AssemblyOutputPath;
            if (Directory.Exists(outputPath))
            {
                Directory.Delete(outputPath, true);
            }
        }
    }
}
