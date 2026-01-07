#if UNITY_EDITOR || LiveScriptReload_Enabled
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace FastScriptReload.Runtime
{
    public class ProjectTypeCache
    {
        private static bool _isInitialized;
        private static Dictionary<string, Type> _allTypesInNonDynamicGeneratedAssemblies;
        public static Dictionary<string, Type> AllTypesInNonDynamicGeneratedAssemblies
        {
            get
            {
                if (!_isInitialized)
                {
                    Init();
                }

                return _allTypesInNonDynamicGeneratedAssemblies;
            }
        }

        private static void Init()
        {
            if (_allTypesInNonDynamicGeneratedAssemblies == null)
            {
                var typeLookupSw = new Stopwatch();
                typeLookupSw.Start();

                _allTypesInNonDynamicGeneratedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic) // 过滤掉动态生成的程序集
                    .SelectMany(a => a.GetTypes())
                    .Where(t => !IsCompilerGeneratedType(t)) // 过滤掉编译器生成的类型
                    .GroupBy(t => t.FullName)
                    .Select(g => g.First()) //TODO: quite odd that same type full name can be defined multiple times? eg Microsoft.CodeAnalysis.EmbeddedAttribute throws 'An item with the same key has already been added' 
                    .ToDictionary(t => t.IsNested ? t.FullName.Replace('+', '/') : t.FullName, t => t);
                    
#if ImmersiveVrTools_DebugEnabled
                ImmersiveVrToolsCommon.Runtime.Logging.LoggerScoped.Log($"Initialized type-lookup dictionary, took: {typeLookupSw.ElapsedMilliseconds}ms - cached");
#endif
            }
        }

        /// <summary>
        /// 检查类型是否是编译器生成的类型
        /// 所有编译器生成的类型（状态机、闭包类、迭代器等）都会标记 CompilerGeneratedAttribute
        /// </summary>
        private static bool IsCompilerGeneratedType(Type type)
        {
            if (type == null)
            {
                return false;
            }

            // 检查是否有 CompilerGenerated 特性
            return type.GetCustomAttributes(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false).Length > 0;
        }

    }
}
#endif