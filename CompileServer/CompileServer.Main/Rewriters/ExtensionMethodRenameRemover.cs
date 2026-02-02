using System.Linq;
using Mono.Cecil;

namespace CompileServer.Rewriters
{
    /// <summary>
    /// 扩展方法重命名移除器 - 在编译后移除扩展方法名中的 __Patch__ 后缀
    /// </summary>
    public static class ExtensionMethodRenameRemover
    {
        private const string PatchSuffix = "__Patch__";
        private const string ExtensionAttributeFullName = "System.Runtime.CompilerServices.ExtensionAttribute";

        /// <summary>
        /// 移除程序集中所有扩展方法名的 __Patch__ 后缀
        /// </summary>
        /// <param name="assembly">要处理的程序集</param>
        public static void RemovePatchSuffix(AssemblyDefinition assembly)
        {
            if (assembly == null)
                return;

            foreach (var type in assembly.MainModule.Types)
            {
                ProcessType(type);
            }
        }

        private static void ProcessType(TypeDefinition type)
        {
            // 处理当前类型的方法
            foreach (var method in type.Methods)
            {
                if (IsExtensionMethod(method) && method.Name.EndsWith(PatchSuffix))
                {
                    // 移除 __Patch__ 后缀
                    method.Name = method.Name.Substring(0, method.Name.Length - PatchSuffix.Length);
                }
            }

            // 递归处理嵌套类型
            foreach (var nestedType in type.NestedTypes)
            {
                ProcessType(nestedType);
            }
        }

        /// <summary>
        /// 判断方法是否为扩展方法
        /// </summary>
        private static bool IsExtensionMethod(MethodDefinition method)
        {
            // 扩展方法必须是静态方法
            if (!method.IsStatic)
                return false;

            // 检查是否有 ExtensionAttribute 特性
            return method.CustomAttributes.Any(attr => 
                attr.AttributeType.FullName == ExtensionAttributeFullName);
        }
    }
}
