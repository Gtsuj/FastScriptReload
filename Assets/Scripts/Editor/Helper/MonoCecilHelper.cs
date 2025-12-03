using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FastScriptReload.Runtime;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// Mono.Cecil 辅助类 - 用于创建泛型类型和方法引用
    /// 使用反射来访问 Mono.Cecil 的内部类型（如 GenericInstanceType）
    /// </summary>
    public static class MonoCecilHelper
    {
        private static readonly Type GenericInstanceTypeType;
        private static readonly Type GenericInstanceMethodType;
        private static readonly Type ByReferenceTypeType;
        private static readonly PropertyInfo GenericArgumentsProperty;
        private static readonly Type OpCodesType;
        private static readonly Dictionary<string, OpCode> OpCodeCache;

        static MonoCecilHelper()
        {
            var cecilAssembly = typeof(TypeReference).Assembly;
            GenericInstanceTypeType = cecilAssembly.GetType("Mono.Cecil.GenericInstanceType");
            GenericInstanceMethodType = cecilAssembly.GetType("Mono.Cecil.GenericInstanceMethod");
            ByReferenceTypeType = cecilAssembly.GetType("Mono.Cecil.ByReferenceType");
            GenericArgumentsProperty = GenericInstanceTypeType?.GetProperty("GenericArguments");
            
            // 获取 OpCodes 类型
            OpCodesType = typeof(Instruction).Assembly.GetType("Mono.Cecil.Cil.OpCodes");
            
            // 缓存常用的 OpCode
            OpCodeCache = new Dictionary<string, OpCode>();
            if (OpCodesType != null)
            {
                var fields = OpCodesType.GetFields(BindingFlags.Public | BindingFlags.Static);
                foreach (var field in fields)
                {
                    if (field.FieldType == typeof(OpCode))
                    {
                        var opCode = (OpCode)field.GetValue(null);
                        OpCodeCache[field.Name] = opCode;
                    }
                }
            }
        }

        /// <summary>
        /// 通过反射获取 OpCode
        /// </summary>
        /// <param name="opCodeName">操作码名称（如 "Ldstr", "Call", "Ldfld"）</param>
        /// <returns>OpCode</returns>
        public static OpCode GetOpCode(string opCodeName)
        {
            if (string.IsNullOrEmpty(opCodeName))
            {
                throw new ArgumentNullException(nameof(opCodeName));
            }

            // 先从缓存查找
            if (OpCodeCache.TryGetValue(opCodeName, out var cachedOpCode))
            {
                return cachedOpCode;
            }

            // 如果缓存中没有，尝试从 OpCodes 类型获取
            if (OpCodesType != null)
            {
                var field = OpCodesType.GetField(opCodeName, BindingFlags.Public | BindingFlags.Static);
                if (field != null && field.FieldType == typeof(OpCode))
                {
                    var opCode = (OpCode)field.GetValue(null);
                    OpCodeCache[opCodeName] = opCode;
                    return opCode;
                }
            }

            throw new ArgumentException($"无法找到操作码: {opCodeName}");
        }

        /// <summary>
        /// 创建泛型实例类型（通过反射）
        /// </summary>
        /// <param name="elementType">泛型类型定义（如 List`1）</param>
        /// <returns>泛型实例类型（如 List&lt;string&gt;）</returns>
        public static TypeReference CreateGenericInstanceType(TypeReference elementType)
        {
            if (elementType == null)
            {
                throw new ArgumentNullException(nameof(elementType));
            }

            if (GenericInstanceTypeType == null)
            {
                LoggerScoped.LogError("无法找到 Mono.Cecil.GenericInstanceType 类型");
                return null;
            }

            try
            {
                // 使用反射创建 GenericInstanceType 实例
                var genericInstanceType = Activator.CreateInstance(GenericInstanceTypeType, elementType);
                if (genericInstanceType == null)
                {
                    LoggerScoped.LogError($"无法创建 GenericInstanceType: {elementType.FullName}");
                    return null;
                }

                return genericInstanceType as TypeReference;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"创建泛型实例类型失败: {elementType.FullName}, 错误: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// 创建泛型实例类型并添加泛型参数
        /// </summary>
        /// <param name="elementType">泛型类型定义</param>
        /// <param name="genericArguments">泛型参数列表</param>
        /// <returns>泛型实例类型</returns>
        public static TypeReference CreateGenericInstanceType(TypeReference elementType, params TypeReference[] genericArguments)
        {
            var genericInstanceType = CreateGenericInstanceType(elementType);
            if (genericInstanceType == null)
            {
                return null;
            }

            // 添加泛型参数
            if (GenericArgumentsProperty?.GetValue(genericInstanceType) is Collection<TypeReference> argumentsCollection)
            {
                foreach (var arg in genericArguments)
                {
                    argumentsCollection.Add(arg);
                }
            }

            return genericInstanceType;
        }

        /// <summary>
        /// 获取泛型实例类型的泛型参数集合
        /// </summary>
        /// <param name="genericInstanceType">泛型实例类型</param>
        /// <returns>泛型参数集合</returns>
        public static Collection<TypeReference> GetGenericArguments(TypeReference genericInstanceType)
        {
            if (genericInstanceType == null || !genericInstanceType.IsGenericInstance)
            {
                return null;
            }

            return GenericArgumentsProperty?.GetValue(genericInstanceType) as Collection<TypeReference>;
        }

        /// <summary>
        /// 创建泛型实例方法引用（通过反射）
        /// </summary>
        /// <param name="elementMethod">泛型方法定义</param>
        /// <param name="genericArguments">泛型参数列表</param>
        /// <returns>泛型实例方法引用</returns>
        public static MethodReference CreateGenericInstanceMethod(MethodReference elementMethod, params TypeReference[] genericArguments)
        {
            if (elementMethod == null)
            {
                throw new ArgumentNullException(nameof(elementMethod));
            }

            if (GenericInstanceMethodType == null)
            {
                LoggerScoped.LogError("无法找到 Mono.Cecil.GenericInstanceMethod 类型");
                return null;
            }

            try
            {
                // 使用反射创建 GenericInstanceMethod 实例
                var genericInstanceMethod = Activator.CreateInstance(GenericInstanceMethodType, elementMethod);
                if (genericInstanceMethod == null)
                {
                    LoggerScoped.LogError($"无法创建 GenericInstanceMethod: {elementMethod.FullName}");
                    return null;
                }

                // 添加泛型参数
                var genericArgumentsProperty = GenericInstanceMethodType.GetProperty("GenericArguments");
                if (genericArgumentsProperty?.GetValue(genericInstanceMethod) is Collection<TypeReference> argumentsCollection)
                {
                    foreach (var arg in genericArguments)
                    {
                        argumentsCollection.Add(arg);
                    }
                }

                return genericInstanceMethod as MethodReference;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"创建泛型实例方法失败: {elementMethod.FullName}, 错误: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// 从源方法引用创建泛型实例方法，并复制泛型参数（支持类型转换）
        /// </summary>
        /// <param name="elementMethod">泛型方法定义（用于创建实例）</param>
        /// <param name="sourceMethodRef">源方法引用（用于复制泛型参数）</param>
        /// <param name="typeConverter">类型转换函数（可选，用于转换泛型参数类型）</param>
        /// <returns>泛型实例方法引用</returns>
        public static MethodReference CreateGenericInstanceMethodFromSource(
            MethodReference elementMethod, MethodReference sourceMethodRef,
            Func<TypeReference, TypeReference> typeConverter = null)
        {
            if (elementMethod == null)
            {
                throw new ArgumentNullException(nameof(elementMethod));
            }

            if (sourceMethodRef == null)
            {
                throw new ArgumentNullException(nameof(sourceMethodRef));
            }

            if (GenericInstanceMethodType == null)
            {
                LoggerScoped.LogError("无法找到 Mono.Cecil.GenericInstanceMethod 类型");
                return null;
            }

            try
            {
                // 使用反射创建 GenericInstanceMethod 实例
                var genericInstanceMethod = Activator.CreateInstance(GenericInstanceMethodType, elementMethod);
                if (genericInstanceMethod == null)
                {
                    LoggerScoped.LogError($"无法创建 GenericInstanceMethod: {elementMethod.FullName}");
                    return null;
                }

                // 获取泛型参数属性
                var genericArgumentsProperty = GenericInstanceMethodType.GetProperty("GenericArguments");
                
                // 从源方法引用获取泛型参数
                var sourceGenericArguments = genericArgumentsProperty?.GetValue(sourceMethodRef) as Collection<TypeReference>;
                var targetGenericArguments = genericArgumentsProperty?.GetValue(genericInstanceMethod) as Collection<TypeReference>;

                if (sourceGenericArguments == null || targetGenericArguments == null)
                {
                    LoggerScoped.LogError("无法获取泛型参数集合");
                    return null;
                }

                // 复制泛型参数（应用类型转换）
                foreach (var typeRef in sourceGenericArguments)
                {
                    var convertedType = typeConverter != null ? typeConverter(typeRef) : typeRef;
                    if (convertedType != null)
                    {
                        targetGenericArguments.Add(convertedType);
                    }
                }

                return genericInstanceMethod as MethodReference;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"从源方法引用创建泛型实例方法失败: {elementMethod.FullName}, 错误: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// 获取泛型实例方法的泛型参数集合
        /// </summary>
        /// <param name="genericInstanceMethod">泛型实例方法</param>
        /// <returns>泛型参数集合</returns>
        public static Collection<TypeReference> GetGenericArguments(MethodReference genericInstanceMethod)
        {
            if (genericInstanceMethod == null || !genericInstanceMethod.IsGenericInstance)
            {
                return null;
            }

            var genericArgumentsProperty = GenericInstanceMethodType?.GetProperty("GenericArguments");
            return genericArgumentsProperty?.GetValue(genericInstanceMethod) as Collection<TypeReference>;
        }

        /// <summary>
        /// 设置泛型实例方法的泛型参数
        /// </summary>
        /// <param name="genericInstanceMethod">泛型实例方法</param>
        /// <param name="genericArguments">泛型参数列表</param>
        public static void SetGenericArguments(MethodReference genericInstanceMethod, params TypeReference[] genericArguments)
        {
            if (genericInstanceMethod == null || !genericInstanceMethod.IsGenericInstance)
            {
                return;
            }

            var genericArgumentsProperty = GenericInstanceMethodType?.GetProperty("GenericArguments");
            if (genericArgumentsProperty?.GetValue(genericInstanceMethod) is Collection<TypeReference> argumentsCollection)
            {
                argumentsCollection.Clear();
                foreach (var arg in genericArguments)
                {
                    argumentsCollection.Add(arg);
                }
            }
        }

        /// <summary>
        /// 创建 ByReferenceType（托管引用类型，如 int&）
        /// 使用反射访问 Mono.Cecil 的内部类型 ByReferenceType
        /// </summary>
        /// <param name="elementType">元素类型（如 int）</param>
        /// <returns>引用类型（如 int&）</returns>
        public static TypeReference CreateByReferenceType(TypeReference elementType)
        {
            if (elementType == null)
            {
                throw new ArgumentNullException(nameof(elementType));
            }

            if (ByReferenceTypeType == null)
            {
                LoggerScoped.LogError("无法找到 Mono.Cecil.ByReferenceType 类型");
                return null;
            }

            try
            {
                // 使用反射创建 ByReferenceType 实例
                // 构造函数签名：ByReferenceType(TypeReference elementType)
                var byReferenceType = Activator.CreateInstance(ByReferenceTypeType, elementType);
                if (byReferenceType == null)
                {
                    LoggerScoped.LogError($"无法创建 ByReferenceType: {elementType.FullName}");
                    return null;
                }

                LoggerScoped.LogDebug($"成功创建 ByReferenceType: {elementType.FullName}&");
                return byReferenceType as TypeReference;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"创建 ByReferenceType 失败: {elementType.FullName}, 错误: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }
    }
}

