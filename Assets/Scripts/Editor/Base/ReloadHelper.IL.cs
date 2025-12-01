using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FastScriptReload.Runtime;
using HarmonyLib;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using MethodImplAttributes = Mono.Cecil.MethodImplAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// IL修改功能 - 使用 Mono.Cecil 修改编译后的程序集
    /// </summary>
    public static partial class ReloadHelper
    {
        /// <summary>
        /// 修改编译后的程序集
        /// </summary>
        /// <param name="hookTypeInfos">程序集差异结果</param>
        /// <returns>修改后的程序集文件路径</returns>
        public static string ModifyCompileAssembly(List<HookTypeInfo> hookTypeInfos)
        {
            var templateAssembly = _assemblyDefinition;
            var mainModule = templateAssembly.MainModule;

            // 清理程序集和模块级别的特性（删除编译器生成的特性）
            var compilerNamespaces = new[] { "System.Runtime.CompilerServices", "Microsoft.CodeAnalysis" };
            // 清理模块级别的特性
            CleanupAttributesByNamespace(mainModule.CustomAttributes, compilerNamespaces);
            // 清理程序集级别的特性
            CleanupAttributesByNamespace(mainModule.Assembly.CustomAttributes, compilerNamespaces);

            foreach (var typeDef in mainModule.Types.ToArray())
            {
                // 删除没有Hook的类型
                var info = hookTypeInfos.FirstOrDefault((info => info.TypeFullName == typeDef.FullName));
                if (info == null)
                {
                    mainModule.Types.Remove(typeDef);
                    continue;
                }

                var methodsToRemove = typeDef.Methods.ToArray();
                
                void HandleModifyMethod(string hookMethodName, HookMethodInfo hookMethodInfo)
                {
                    var methodDef = methodsToRemove.FirstOrDefault((definition => definition.FullName == hookMethodName));
                    if (methodDef == null)
                    {
                        return;
                    }

                    var newMethodDef = ModifyMethod(mainModule, methodDef, mainModule.ImportReference(info.ExistingType));
                    hookMethodInfo.ModifyMethodName = newMethodDef.FullName;
                    hookMethodInfo.MethodDefinition = newMethodDef;
                    
                    typeDef.Methods.Add(newMethodDef);
                }

                // 处理添加的方法
                foreach (var (name, addedMethodInfo) in info.AddedMethods)
                {
                    HandleModifyMethod(name, addedMethodInfo);
                }
                
                // 处理修改的方法
                foreach (var (name, modifiedMethodInfo) in info.ModifiedMethods)
                {
                    HandleModifyMethod(name, modifiedMethodInfo);
                }
                
                Array.ForEach(methodsToRemove, method => typeDef.Methods.Remove(method));

                // 清理属性
                typeDef.Properties.Clear();
                typeDef.Fields.Clear();
            }

            // 添加必要的引用
            AddRequiredReferences(templateAssembly, hookTypeInfos);

            // 生成文件名并保存
            var filePath = Path.Combine(AssemblyPath,
                $"{_assemblyDefinition.Name.Name}.dll");

            // 确保目录存在
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            templateAssembly.Write(filePath);

            // 设置每个类型的程序集路径
            foreach (var typeDiff in hookTypeInfos)
            {
                if (typeDiff.ModifiedMethods.Count > 0 || typeDiff.AddedMethods.Count > 0)
                {
                    typeDiff.WrapperAssemblyPath = filePath;
                }
            }

            return filePath;
        }

        private static MethodDefinition ModifyMethod(ModuleDefinition module, MethodDefinition methodDef, TypeReference originalTypeRef)
        {
            MethodDefinition newMethodDef = new MethodDefinition(methodDef.Name, 
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, methodDef.ReturnType)
                {
                    DeclaringType = methodDef.DeclaringType,
                };

            newMethodDef.ImplAttributes |= MethodImplAttributes.NoInlining;

            // 泛型参数处理
            var genericParamMap = new Dictionary<GenericParameter, GenericParameter>();
            if (methodDef.HasGenericParameters)
            {
                foreach (var sourceGenericParam in methodDef.GenericParameters)
                {
                    // 创建新的泛型参数，关联到新方法
                    var newGenericParam = new GenericParameter(sourceGenericParam.Name, newMethodDef);
                    newGenericParam.Attributes = sourceGenericParam.Attributes;

                    // 复制约束（需要导入约束类型）
                    foreach (var constraint in sourceGenericParam.Constraints)
                    {
                        var constraintType = GetOriginalType(module, constraint.ConstraintType);
                        newGenericParam.Constraints.Add(new GenericParameterConstraint(constraintType));
                    }

                    newMethodDef.GenericParameters.Add(newGenericParam);
                    genericParamMap[sourceGenericParam] = newGenericParam;
                }
            }

            // 设置返回值
            newMethodDef.ReturnType = GetOriginalType(module, methodDef.ReturnType, genericParamMap);

            // 将@this参数加入到方法参数列表的头部
            if (!methodDef.IsStatic)
            {
                newMethodDef.Parameters.Insert(0,
                    new ParameterDefinition("@this", ParameterAttributes.None, originalTypeRef));
            }

            // 添加原方法参数
            var parameterMap = new Dictionary<ParameterDefinition, ParameterDefinition>();
            foreach (var param in methodDef.Parameters)
            {
                var newParam = new ParameterDefinition(param.Name, param.Attributes,
                    GetOriginalType(module, param.ParameterType, genericParamMap));
                newMethodDef.Parameters.Add(newParam);
                
                parameterMap[param] = newParam;
            }

            // 复制局部变量定义
            foreach (var variable in methodDef.Body.Variables)
            {
                var variableType = GetOriginalType(module, variable.VariableType, genericParamMap);
                newMethodDef.Body.Variables.Add(new VariableDefinition(variableType));
            }
            
            newMethodDef.Body.MaxStackSize = methodDef.Body.MaxStackSize;

            // 创建所有指令
            var sourceInstructions = methodDef.Body.Instructions.ToList();
            var processor = newMethodDef.Body.GetILProcessor();
            for (int i = 0; i < sourceInstructions.Count; i++)
            {
                processor.Append(CreateInstruction(module, sourceInstructions[i], parameterMap, genericParamMap));
            }

            return newMethodDef;
        }

        /// <summary>
        /// 创建指令（处理template类型替换）
        /// </summary>
        private static Instruction CreateInstruction(ModuleDefinition module, Instruction sourceInst,
            Dictionary<ParameterDefinition, ParameterDefinition> parameterMap,
            Dictionary<GenericParameter, GenericParameter> genericParamMap)
        {
            if (sourceInst.Operand == null)
            {
                return Instruction.Create(sourceInst.OpCode);
            }

            if (sourceInst.Operand is Instruction targetInst)
            {
                return Instruction.Create(sourceInst.OpCode, targetInst);
            }

            if (sourceInst.Operand is VariableDefinition variableDef)
            {
                return Instruction.Create(sourceInst.OpCode, variableDef);
            }

            if (sourceInst.Operand is ParameterDefinition paramDef)
            {
                // paramDef.ParameterType = GetOriginalType(module, paramDef.ParameterType, genericParamMap);
                return Instruction.Create(sourceInst.OpCode, parameterMap[paramDef]);
            }

            if (sourceInst.Operand is TypeReference typeRef)
            {
                return Instruction.Create(sourceInst.OpCode, GetOriginalType(module, typeRef, genericParamMap));
            }

            if (sourceInst.Operand is FieldReference fieldRef)
            {
                var newFieldType = GetOriginalType(module, fieldRef.FieldType, genericParamMap);
                var newDeclaringType = GetOriginalType(module, fieldRef.DeclaringType, genericParamMap);
                if (fieldRef.DeclaringType == null)
                {
                    fieldRef = new FieldReference(fieldRef.Name, newFieldType);
                }
                else
                {
                    fieldRef = new FieldReference(fieldRef.Name, newFieldType, newDeclaringType);
                }

                return Instruction.Create(sourceInst.OpCode, fieldRef);
            }

            if (sourceInst.Operand is MethodReference methodRef)
            {
                // 处理新增方法调用
                if (HookTypeInfoCache.TryGetValue(methodRef.DeclaringType.FullName, out var hookType)
                    && hookType.AddedMethods.TryGetValue(methodRef.FullName, out var addedMethodInfo))
                {
                    methodRef = module.ImportReference(addedMethodInfo.MethodDefinition);
                    return Instruction.Create(sourceInst.OpCode, methodRef);
                }

                var originalMethodRef = GetOriginalMethodReference(module, methodRef);
                if (originalMethodRef == null)
                {
                    LoggerScoped.LogWarning($"从原程序集获取方法引用失败: {methodRef.FullName}");
                    originalMethodRef = methodRef;
                }

                return Instruction.Create(sourceInst.OpCode, originalMethodRef);
            }

            // 处理基本类型操作数
            return sourceInst.Operand switch
            {
                string strVal => Instruction.Create(sourceInst.OpCode, strVal),
                sbyte sbyteVal => Instruction.Create(sourceInst.OpCode, sbyteVal),
                byte byteVal => Instruction.Create(sourceInst.OpCode, byteVal),
                int intVal => Instruction.Create(sourceInst.OpCode, intVal),
                long longVal => Instruction.Create(sourceInst.OpCode, longVal),
                float floatVal => Instruction.Create(sourceInst.OpCode, floatVal),
                double doubleVal => Instruction.Create(sourceInst.OpCode, doubleVal),
                _ => sourceInst
            };
        }

        /// <summary>
        /// 根据命名空间清理自定义属性
        /// </summary>
        private static void CleanupAttributesByNamespace(ICollection<CustomAttribute> attributes, string[] compilerNamespaces)
        {
            if (attributes == null) return;

            var attributesToRemove = new List<CustomAttribute>();
            foreach (var attr in attributes)
            {
                if (attr?.AttributeType == null) continue;

                var attrNamespace = attr.AttributeType.Namespace ?? string.Empty;

                // 检查自定义属性的命名空间是否匹配要删除的命名空间
                foreach (var compilerNamespace in compilerNamespaces)
                {
                    if (attrNamespace == compilerNamespace || attrNamespace.StartsWith(compilerNamespace + "."))
                    {
                        attributesToRemove.Add(attr);
                        break;
                    }
                }
            }

            foreach (var attr in attributesToRemove)
            {
                attributes.Remove(attr);
            }
        }

        /// <summary>
        /// 添加必要的程序集引用
        /// </summary>
        private static void AddRequiredReferences(AssemblyDefinition wrapperAssembly, List<HookTypeInfo> hookTypeInfos)
        {
            var mainModule = wrapperAssembly.MainModule;

            // 收集所有原始程序集
            var originalAssemblies = new HashSet<Assembly>();
            foreach (var typeInfo in hookTypeInfos)
            {
                if (typeInfo.ExistingType?.Assembly != null)
                {
                    originalAssemblies.Add(typeInfo.ExistingType.Assembly);
                }
            }

            // 为每个原始程序集添加引用及其依赖
            foreach (var originalAssembly in originalAssemblies)
            {
                try
                {
                    if (string.IsNullOrEmpty(originalAssembly.Location))
                    {
                        LoggerScoped.LogDebug($"跳过动态程序集引用: {originalAssembly.FullName}");
                        continue;
                    }

                    var originalAssemblyDef = GetOrReadAssemblyDefinition(originalAssembly.Location);
                    if (originalAssemblyDef == null)
                    {
                        LoggerScoped.LogWarning($"无法读取原始程序集: {originalAssembly.Location}");
                        continue;
                    }

                    // 添加对源程序集的引用
                    var sourceAssemblyRef = new AssemblyNameReference(
                        originalAssemblyDef.Name.Name,
                        originalAssemblyDef.Name.Version);

                    if (mainModule.AssemblyReferences.All(r => r.FullName != sourceAssemblyRef.FullName))
                    {
                        mainModule.AssemblyReferences.Add(sourceAssemblyRef);
                        LoggerScoped.LogDebug($"添加原始程序集引用: {sourceAssemblyRef.FullName}");
                    }

                    // 添加源程序集的所有依赖引用（排除template程序集）
                    foreach (var reference in originalAssemblyDef.MainModule.AssemblyReferences)
                    {
                        if (mainModule.AssemblyReferences.All(r => r.FullName != reference.FullName))
                        {
                            mainModule.AssemblyReferences.Add(reference);
                            LoggerScoped.LogDebug($"添加依赖引用: {reference.FullName}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LoggerScoped.LogWarning($"无法添加原始程序集引用: {originalAssembly.FullName}, 错误: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 通过反射创建泛型实例方法（不直接访问 internal 类 GenericInstanceMethod）
        /// </summary>
        private static MethodReference CreateGenericInstanceMethodViaReflection(ModuleDefinition module,
            MethodReference originalMethodRef, MethodReference methodRef)
        {
            try
            {
                var genericInstanceMethodType = typeof(MethodReference).Assembly.GetType("Mono.Cecil.GenericInstanceMethod");
                if (genericInstanceMethodType == null)
                {
                    LoggerScoped.LogError($"{originalMethodRef.FullName} 无法找到 GenericInstanceMethod 类型");
                    return null;
                }

                var genericInstanceMethod = Activator.CreateInstance(genericInstanceMethodType, originalMethodRef);
                if (genericInstanceMethod == null)
                {
                    return null;
                }

                // 复制泛型参数
                var genericArgumentsProperty = genericInstanceMethodType.GetProperty("GenericArguments");

                var sourceGenericArguments = genericArgumentsProperty?.GetValue(methodRef) as Collection<TypeReference>;
                var targetGenericArguments = genericArgumentsProperty?.GetValue(genericInstanceMethod) as Collection<TypeReference>;

                if (sourceGenericArguments == null || targetGenericArguments == null)
                {
                    return null;
                }

                foreach (var typeRef in sourceGenericArguments)
                {
                    var originalGenericArg = GetOriginalType(module, typeRef);
                    targetGenericArguments.Add(originalGenericArg);
                }

                return genericInstanceMethod as MethodReference;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"通过反射创建泛型实例方法失败: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// 获取原类型引用（将template类型替换为原类型）
        /// </summary>
        private static TypeReference GetOriginalType(ModuleDefinition module, TypeReference operandTypeRef,
            Dictionary<GenericParameter, GenericParameter> genericParamMap = null)
        {
            if (operandTypeRef == null)
            {
                return null;
            }

            if (operandTypeRef is GenericParameter operandGenericParameter &&
                genericParamMap != null &&
                genericParamMap.TryGetValue(operandGenericParameter, out var genericParameter))
            {
                return genericParameter;
            }

            if (ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(operandTypeRef.FullName,
                    out var originalType))
            {
                return module.ImportReference(originalType);
            }

            return operandTypeRef;
        }

        /// <summary>
        /// 从原程序集中获取方法引用
        /// </summary>
        private static MethodReference GetOriginalMethodReference(ModuleDefinition module, MethodReference methodRef)
        {
            try
            {
                var declaringTypeFullName = methodRef.DeclaringType.FullName;

                if (!ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(declaringTypeFullName,
                        out var originalType) ||
                    string.IsNullOrEmpty(originalType.Assembly?.Location))
                {
                    return null;
                }

                // 作用域与原类型一致
                if (originalType.Assembly.GetName().Name.Equals(methodRef?.DeclaringType?.Scope?.Name))
                {
                    return module.ImportReference(methodRef);
                }

                var originalAssemblyDef = GetOrReadAssemblyDefinition(originalType.Assembly.Location);
                var originalTypeDef =
                    originalAssemblyDef?.MainModule.Types.FirstOrDefault(t => t.FullName == declaringTypeFullName);
                if (originalTypeDef == null)
                {
                    return null;
                }

                var templateMethodRefFullName = methodRef.IsGenericInstance
                    ? methodRef.GetElementMethod().FullName
                    : methodRef.FullName;

                var methods = originalTypeDef.Methods.Where(m => m.FullName == templateMethodRefFullName).ToArray();
                switch (methods.Length)
                {
                    case 0:
                        return null;
                    case > 1:
                        throw new Exception($"方法重载过多: {methodRef.FullName}");
                }

                if (!methodRef.IsGenericInstance)
                {
                    return module.ImportReference(methods[0]);
                }

                // 创建泛型实例
                var originalMethodRef = module.ImportReference(methods[0]);
                return CreateGenericInstanceMethodViaReflection(module, originalMethodRef, methodRef)
                       ?? module.ImportReference(methodRef);
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"从原程序集获取方法引用失败: {methodRef.FullName}, 错误: {ex.Message}");
                return null;
            }
        }
    }
}
