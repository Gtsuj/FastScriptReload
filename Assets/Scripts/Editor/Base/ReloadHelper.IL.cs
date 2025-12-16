using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastScriptReload.Runtime;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Pdb;
using Code = Mono.Cecil.Cil.Code;
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
        #region 核心方法

        public static string ModifyCompileAssembly(Dictionary<string, DiffResult> results)
        {
            // 修改程序集
            HandleAssemblyType(results);

            // 清理程序集
            ClearAssembly(results);

            // 生成文件名并保存
            var filePath = Path.Combine(AssemblyPath, $"{_assemblyDefinition.Name.Name}.dll");

            // 确保目录存在
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _assemblyDefinition.Write(filePath);

            // 设置每个类型的程序集路径
            foreach (var (_, typeDiff) in HookTypeInfoCache)
            {
                typeDiff.WrapperAssemblyPath = filePath;
            }

            return filePath;
        }

        private static void HandleAssemblyType(Dictionary<string, DiffResult> diffResults)
        {
            var mainModule = _assemblyDefinition.MainModule;
            
            // 用于存储源方法定义的映射（阶段2需要）
            var sourceMethodMap = new Dictionary<string, Dictionary<string, MethodDefinition>>();
            
            // ====== 阶段1：预注册所有方法签名（仅创建签名，不处理方法体） ======
            foreach (var typeDef in mainModule.Types)
            {
                if (!diffResults.TryGetValue(typeDef.FullName, out DiffResult result))
                {
                    continue;
                }

                if (!ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(typeDef.FullName, out var originalType))
                {
                    continue;
                }

                if (!HookTypeInfoCache.TryGetValue(originalType.FullName!, out var hookTypeInfo))
                {
                    hookTypeInfo = new HookTypeInfo
                    {
                        TypeFullName = originalType.FullName,
                        ExistingType = originalType,
                    };

                    HookTypeInfoCache.Add(originalType.FullName, hookTypeInfo);
                }

                // 处理新增字段
                foreach (var fieldDefinition in typeDef.Fields)
                {
                    if (result.AddedFields.ContainsKey(fieldDefinition.FullName))
                    {
                        hookTypeInfo.AddedFields.TryAdd(fieldDefinition.FullName, fieldDefinition);
                    }
                }

                var originalMethods = originalType.GetAllMethods();

                // 用于存储该类型的源方法映射
                var typeSourceMethods = new Dictionary<string, MethodDefinition>();
                sourceMethodMap[typeDef.FullName] = typeSourceMethods;

                // 预注册方法（仅创建方法签名，不处理方法体）
                void PreRegisterMethods(Dictionary<string, MethodDiffInfo> methodDiffInfos, HookMethodState hookMethodState)
                {
                    foreach (var methodDef in typeDef.Methods.ToArray())
                    {
                        if (!methodDiffInfos.TryGetValue(methodDef.FullName, out var methodDiffInfo))
                        {
                            continue;
                        }

                        var hookMethodName = methodDef.FullName;
                        
                        // 仅创建方法签名，不处理方法体
                        var newMethodDef = CreateMethodSignature(methodDef, mainModule.ImportReference(originalType));
                        
                        if (!hookTypeInfo.ModifiedMethods.TryGetValue(hookMethodName, out var hookMethodInfo))
                        {
                            
                            hookMethodInfo = new HookMethodInfo(newMethodDef, hookMethodState, originalMethods.GetValueOrDefault(hookMethodName));
                            hookTypeInfo.ModifiedMethods.Add(hookMethodName, hookMethodInfo);
                        }
                        else
                        {
                            hookMethodInfo.MethodDefinition = newMethodDef;
                        }
                        
                        typeDef.Methods.Add(newMethodDef);
                        
                        // 保存源方法定义
                        typeSourceMethods[hookMethodName] = methodDef;
                    }
                }

                // 预注册添加和修改的方法
                PreRegisterMethods(result.AddedMethods, HookMethodState.Added);
                PreRegisterMethods(result.ModifiedMethods, HookMethodState.Modified);
            }
            
            // ====== 阶段2：填充所有方法体（处理IL指令） ======
            foreach (var typeDef in mainModule.Types)
            {
                if (!diffResults.TryGetValue(typeDef.FullName, out DiffResult result))
                {
                    continue;
                }

                if (!HookTypeInfoCache.TryGetValue(typeDef.FullName, out var hookTypeInfo))
                {
                    continue;
                }

                if (!sourceMethodMap.TryGetValue(typeDef.FullName, out var typeSourceMethods))
                {
                    continue;
                }

                // 填充方法体
                void FillMethodBodies(Dictionary<string, MethodDiffInfo> methodDiffInfos)
                {
                    foreach (var (methodFullName, _) in methodDiffInfos)
                    {
                        if (!hookTypeInfo.ModifiedMethods.TryGetValue(methodFullName, out var hookMethodInfo))
                        {
                            continue;
                        }

                        if (!typeSourceMethods.TryGetValue(methodFullName, out var sourceMethodDef))
                        {
                            continue;
                        }

                        // 填充方法体（复制IL指令）
                        FillMethodBody(sourceMethodDef, hookMethodInfo.MethodDefinition);
                    }
                }

                FillMethodBodies(result.AddedMethods);
                FillMethodBodies(result.ModifiedMethods);
            }
        }

        /// <summary>
        /// 清理程序集：删除未使用的类型
        /// </summary>
        private static void ClearAssembly(Dictionary<string, DiffResult> results)
        {
            var mainModule = _assemblyDefinition.MainModule;

            // 清理编译器生成的特性
            var compilerNamespaces = new[] { "System.Runtime.CompilerServices", "Microsoft.CodeAnalysis" };
            
            void CleanupAttributesByNamespace(ICollection<CustomAttribute> attributes)
            {
                foreach (var attr in attributes.ToArray())
                {
                    var attrNamespace = attr.AttributeType.Namespace ?? string.Empty;
                    if (compilerNamespaces.Any(ns => attrNamespace.Equals(ns) || attrNamespace.StartsWith($"{ns}.")))
                    {
                        attributes.Remove(attr);
                    }
                }
            }
            
            CleanupAttributesByNamespace(mainModule.CustomAttributes);
            CleanupAttributesByNamespace(mainModule.Assembly.CustomAttributes);

            // 统一清理程序集
            foreach (var typeDef in mainModule.Types.ToArray())
            {
                // 删除没有Hook的类型
                if (!results.TryGetValue(typeDef.FullName, out var result))
                {
                    mainModule.Types.Remove(typeDef);
                    continue;
                }

                // 清理方法
                if (HookTypeInfoCache.TryGetValue(typeDef.FullName, out var hookTypeInfo))
                {
                    typeDef.Methods.Clear();
                    var methodNamesToAdd = result.ModifiedMethods.Concat(result.AddedMethods);
                    foreach (var (methodFullName, _) in methodNamesToAdd)
                    {
                        typeDef.Methods.Add(hookTypeInfo.ModifiedMethods[methodFullName].MethodDefinition);
                    }
                }

                // 清理字段
                foreach (var fieldDef in typeDef.Fields.ToArray())
                {
                    if (!result.AddedFields.ContainsKey(fieldDef.FullName))
                    {
                        typeDef.Fields.Remove(fieldDef);
                    }
                }

                typeDef.Properties.Clear();
            }
        }

        #endregion

        #region 方法、字段修改

        /// <summary>
        /// 创建方法签名（不包含方法体）
        /// </summary>
        private static MethodDefinition CreateMethodSignature(MethodDefinition methodDef, TypeReference originalTypeRef)
        {
            var returnType = GetOriginalType(methodDef.ReturnType);
            MethodDefinition newMethodDef = new MethodDefinition(methodDef.Name,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, returnType)
            {
                DeclaringType = methodDef.DeclaringType,
            };

            newMethodDef.ImplAttributes |= MethodImplAttributes.NoInlining;

            // 泛型参数处理
            if (methodDef.HasGenericParameters)
            {
                foreach (var sourceGenericParam in methodDef.GenericParameters)
                {
                    // 约束类型重定向
                    foreach (var constraint in sourceGenericParam.Constraints)
                    {
                        constraint.ConstraintType = GetOriginalType(constraint.ConstraintType);
                    }

                    newMethodDef.GenericParameters.Add(sourceGenericParam);
                }
            }

            newMethodDef.Parameters.Clear();
            // 将@this参数加入到方法参数列表的头部
            if (!methodDef.IsStatic)
            {
                newMethodDef.Parameters.Add(new ParameterDefinition("@this", ParameterAttributes.None, originalTypeRef));
            }

            // 添加原方法参数
            foreach (var param in methodDef.Parameters)
            {
                param.ParameterType = GetOriginalType(param.ParameterType);
                newMethodDef.Parameters.Add(param);
            }

            return newMethodDef;
        }

        /// <summary>
        /// 填充方法体（复制IL指令）- 用于阶段2填充方法体
        /// </summary>
        private static void FillMethodBody(MethodDefinition sourceMethodDef, MethodDefinition targetMethodDef)
        {
            var module = sourceMethodDef.Module;

            targetMethodDef.Body.MaxStackSize = sourceMethodDef.Body.MaxStackSize;

            // 添加局部变量定义
            foreach (var variable in sourceMethodDef.Body.Variables)
            {
                variable.VariableType = GetOriginalType(variable.VariableType);
                targetMethodDef.Body.Variables.Add(variable);
            }

            // 创建所有指令
            var sourceInstructions = sourceMethodDef.Body.Instructions.ToList();
            var processor = targetMethodDef.Body.GetILProcessor();

            // 建立指令映射，用于异常处理器
            var instructionMap = new Dictionary<Instruction, Instruction>();

            for (int i = 0; i < sourceInstructions.Count; i++)
            {
                var sourceInst = sourceInstructions[i];
                var newInst = CreateInstruction(module, processor, sourceInst);
                instructionMap[sourceInst] = newInst;
            }

            // 复制异常处理器
            foreach (var handler in sourceMethodDef.Body.ExceptionHandlers)
            {
                var newHandler = new ExceptionHandler(handler.HandlerType)
                {
                    TryStart = handler.TryStart != null ? instructionMap[handler.TryStart] : null,
                    TryEnd = handler.TryEnd != null ? instructionMap[handler.TryEnd] : null,
                    HandlerStart = handler.HandlerStart != null ? instructionMap[handler.HandlerStart] : null,
                    HandlerEnd = handler.HandlerEnd != null ? instructionMap[handler.HandlerEnd] : null,
                    CatchType = handler.CatchType != null ? GetOriginalType(handler.CatchType) : null,
                    FilterStart = handler.FilterStart != null ? instructionMap[handler.FilterStart] : null
                };
                targetMethodDef.Body.ExceptionHandlers.Add(newHandler);
            }
        }
        
        /// <summary>
        /// 创建指令
        /// </summary>
        private static Instruction CreateInstruction(ModuleDefinition module, ILProcessor processor, Instruction sourceInst)
        {
            Instruction newInst = null;

            if (sourceInst.Operand == null)
            {
                newInst = Instruction.Create(sourceInst.OpCode);
                processor.Append(newInst);
                return newInst;
            }

            switch (sourceInst.Operand)
            {
                case Instruction targetInst:
                    newInst = Instruction.Create(sourceInst.OpCode, targetInst);
                    break;
                case VariableDefinition variableDef:
                    newInst = Instruction.Create(sourceInst.OpCode, variableDef);
                    break;
                case ParameterDefinition paramDef:
                    newInst = Instruction.Create(sourceInst.OpCode, paramDef);
                    break;
                case TypeReference typeRef:
                    newInst = Instruction.Create(sourceInst.OpCode, GetOriginalType(typeRef));
                    break;
                case FieldReference fieldRef:
                    return HandleFieldReference(processor, sourceInst, fieldRef);
                case MethodReference methodRef:
                    return HandleMethodReference(processor, sourceInst, methodRef);
                default:
                    // 处理基本类型操作数
                    newInst = sourceInst.Operand switch
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
                    break;
            }

            processor.Append(newInst);
            return newInst;
        }
        
        /// <summary>
        /// 处理方法引用指令
        /// </summary>
        private static Instruction HandleMethodReference(ILProcessor processor, Instruction sourceInst, MethodReference methodRef)
        {
            var module = methodRef.Module;

            Instruction newInst = null;
            if (HookTypeInfoCache.TryGetValue(methodRef.DeclaringType.FullName, out var hookType))
            {
                // 处理新增方法调用
                if (hookType.TryGetAddedMethod(methodRef.FullName, out var addedMethodInfo))
                {
                    newInst = Instruction.Create(sourceInst.OpCode, module.ImportReference(addedMethodInfo.MethodDefinition));
                    processor.Append(newInst);

                    return newInst;
                }

                // 处理泛型方法调用
                if (methodRef is GenericInstanceMethod genericMethod 
                    && hookType.ModifiedMethods.TryGetValue(methodRef.GetElementMethod().FullName, out var modifiedMethodInfo))
                {
                    var genericInstanceMethod = CreateGenericInstanceMethod(genericMethod, modifiedMethodInfo.MethodDefinition);
                    newInst = Instruction.Create(sourceInst.OpCode, genericInstanceMethod);
                    processor.Append(newInst);

                    return newInst;
                }
            }

            var originalMethodRef = GetOriginalMethodReference(methodRef);
            if (originalMethodRef == null)
            {
                LoggerScoped.LogWarning($"从原程序集获取方法引用失败: {methodRef.FullName}");
                originalMethodRef = module.ImportReference(methodRef);
            }

            newInst = Instruction.Create(sourceInst.OpCode, originalMethodRef);
            processor.Append(newInst);
            return newInst;
        }

        private static GenericInstanceMethod CreateGenericInstanceMethod(GenericInstanceMethod originalMethod, MethodReference methodRef)
        {
            var genericInstanceMethod = new GenericInstanceMethod(methodRef);
            // 添加泛型参数
            foreach (var typeRef in originalMethod.GenericArguments)
            {
                genericInstanceMethod.GenericArguments.Add(GetOriginalType(typeRef));
            }

            return genericInstanceMethod;
        }

        /// <summary>
        /// 处理字段引用指令
        /// </summary>
        private static Instruction HandleFieldReference(ILProcessor processor, Instruction sourceInst, FieldReference fieldRef)
        {
            // 处理新增字段的访问
            if (HookTypeInfoCache.TryGetValue(fieldRef.DeclaringType.FullName, out var hookTypeInfo)
                && hookTypeInfo.AddedFields.ContainsKey(fieldRef.FullName))
            {
                var code = sourceInst.OpCode.Code;
                switch (code)
                {
                    case Code.Ldfld:
                    case Code.Ldsfld:
                        return ReplaceFieldLoadWithGetHolder(processor, fieldRef, code is Code.Ldsfld);
                    case Code.Stfld:
                    case Code.Stsfld:
                        return ReplaceFieldStoreWithGetHolder(processor, fieldRef, code is Code.Stsfld);
                    case Code.Ldflda:
                    case Code.Ldsflda:
                        return ReplaceFieldAddressWithGetRef(processor, fieldRef, code is Code.Ldsflda);
                }
            }

            // 普通字段引用处理
            var newFieldType = GetOriginalType(fieldRef.FieldType);
            var newDeclaringType = GetOriginalType(fieldRef.DeclaringType);
            var newFieldRef = fieldRef.DeclaringType == null
                ? new FieldReference(fieldRef.Name, newFieldType)
                : new FieldReference(fieldRef.Name, newFieldType, newDeclaringType);

            var newInst = Instruction.Create(sourceInst.OpCode, newFieldRef);
            processor.Append(newInst);
            return newInst;
        }

        /// <summary>
        /// 替换 ldfld/ldsfld 指令为 FieldResolver.GetHolder 调用
        /// 实例字段：instance.Field → GetHolder(instance, "Field").F
        /// 静态字段：Class.Field → GetHolder(null, "Field").F
        /// </summary>
        private static Instruction ReplaceFieldLoadWithGetHolder(ILProcessor processor, FieldReference fieldRef, bool isStatic)
        {
            var fieldType = GetOriginalType(fieldRef.FieldType);
            var ownerType = GetOriginalType(fieldRef.DeclaringType);

            var getHolderMethodRef = FieldResolverHelper.GetFieldResolverGetHolderMethodReference(ownerType, fieldType);
            var fFieldRef = FieldResolverHelper.GetFieldHolderFReference(fieldType);

            // 生成指令序列
            if (isStatic)
            {
                processor.Append(Instruction.Create(OpCodes.Ldnull));
            }
            processor.Append(Instruction.Create(OpCodes.Ldstr, fieldRef.Name));
            processor.Append(Instruction.Create(OpCodes.Call, getHolderMethodRef));
            var lastInst = Instruction.Create(OpCodes.Ldfld, fFieldRef);
            processor.Append(lastInst);

            return lastInst;
        }

        /// <summary>
        /// 替换 stfld/stsfld 指令为 FieldResolver.Store 调用
        /// 实例字段：instance.Field = value → Store(instance, value, "Field")
        /// 静态字段：Class.Field = value → Store(value, "Field")
        /// </summary>
        private static Instruction ReplaceFieldStoreWithGetHolder(ILProcessor processor, FieldReference fieldRef, bool isStatic)
        {
            var fieldType = GetOriginalType(fieldRef.FieldType);
            var ownerType = GetOriginalType(fieldRef.DeclaringType);

            var storeMethodRef = FieldResolverHelper.GetFieldResolverStoreMethodReference(ownerType, fieldType, isStatic);
            processor.Append(Instruction.Create(OpCodes.Ldstr, fieldRef.Name));
            var lastInst = Instruction.Create(OpCodes.Call, storeMethodRef);
            processor.Append(lastInst);

            return lastInst;
        }
        
        /// <summary>
        /// 替换 ldflda/ldsflda 指令为 FieldResolver.GetHolder + FieldHolder.GetRef 调用
        /// 实例字段：ref instance.Field → GetHolder(instance, "Field").GetRef()
        /// 静态字段：ref Class.Field → GetHolder(null, "Field").GetRef()
        /// </summary>
        private static Instruction ReplaceFieldAddressWithGetRef(ILProcessor processor, FieldReference fieldRef, bool isStatic)
        {
            var fieldType = GetOriginalType(fieldRef.FieldType);
            var ownerType = GetOriginalType(fieldRef.DeclaringType);

            var getHolderMethodRef = FieldResolverHelper.GetFieldResolverGetHolderMethodReference(ownerType, fieldType);
            var getRefMethodRef = FieldResolverHelper.GetFieldHolderGetRefMethodReference(fieldType);

            if (isStatic)
            {
                processor.Append(Instruction.Create(OpCodes.Ldnull));
            }

            processor.Append(Instruction.Create(OpCodes.Ldstr, fieldRef.Name));
            processor.Append(Instruction.Create(OpCodes.Call, getHolderMethodRef));
            var lastInst = Instruction.Create(OpCodes.Callvirt, getRefMethodRef);
            processor.Append(lastInst);

            return lastInst;
        }
        #endregion

        #region 类型和方法引用处理

        /// <summary>
        /// 获取原类型引用
        /// </summary>
        private static TypeReference GetOriginalType(TypeReference typeRef)
        {
            var module = typeRef.Module;

            // 只有定义在当前程序集才需要重定向到原类型
            if (!typeRef.Scope.Name.Equals(module.Name))
            {
                return typeRef;
            }

            if (typeRef.IsGenericParameter)
            {
                return typeRef;
            }

            if (typeRef is GenericInstanceType genericInstanceType)
            {
                foreach (var genericArgument in genericInstanceType.GenericArguments)
                {
                    genericArgument.DeclaringType = GetOriginalType(genericArgument.DeclaringType);
                }

                return typeRef;
            }

            if (ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(typeRef.FullName, out var originalType))
            {
                return module.ImportReference(originalType);
            }

            return typeRef;
        }

        /// <summary>
        /// 从原程序集中获取方法引用
        /// </summary>
        private static MethodReference GetOriginalMethodReference(MethodReference methodRef)
        {
            try
            {
                var module = methodRef.Module;

                // 只有定义在当前程序集才需要重定向到原方法
                if (!methodRef.DeclaringType.Scope.Name.Equals(module.Name))
                {
                    return methodRef;
                }

                // 从原类型中获取方法引用
                if (ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(methodRef.DeclaringType.FullName, out var originalType))
                {
                    var originalMethods = originalType.GetAllMethods();
                    var originalMethodName = methodRef.IsGenericInstance
                        ? methodRef.GetElementMethod().FullName
                        : methodRef.FullName;

                    if (!originalMethods.TryGetValue(originalMethodName, out var method))
                    {
                        return methodRef;
                    }

                    var originalMethodRef = module.ImportReference(method);
                    if (methodRef is GenericInstanceMethod genericMethod)
                    {
                        originalMethodRef = CreateGenericInstanceMethod(genericMethod, originalMethodRef);
                    }

                    return originalMethodRef;
                }

                return methodRef;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"从原程序集获取方法引用失败: {methodRef.FullName}, 错误: {ex.Message}");
                return null;
            }
        }

        #endregion
        
    }
}
