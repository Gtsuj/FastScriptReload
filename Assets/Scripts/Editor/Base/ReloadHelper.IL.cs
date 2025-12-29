using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FastScriptReload.Runtime;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
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

        public static string ModifyCompileAssembly(string assemblyName, Dictionary<string, DiffResult> diffResults)
        {
            // 编译一份新的用于修改
            _assemblyDefinition = TypeInfoHelper.Compile(assemblyName, false);

            // 修改程序集
            HandleAssemblyType(diffResults);

            // 清理程序集
            ClearAssembly(diffResults);

            NestedTypeInfo.Clear();

            // 生成文件名并保存
            var filePath = Path.Combine(ASSEMBLY_OUTPUT_PATH, $"{_assemblyDefinition.Name.Name}.dll");

            // 确保目录存在
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _assemblyDefinition.Write(filePath);

            // 设置每个类型的程序集路径
            foreach (var diffResult in diffResults)
            {
                if (!HookTypeInfoCache.TryGetValue(diffResult.Key, out var hookTypeInfo))
                {
                    continue;
                }

                hookTypeInfo.SetAssemblyPath(filePath, diffResult.Value);
            }

            return filePath;
        }

        private static void HandleAssemblyType(Dictionary<string, DiffResult> diffResults)
        {
            var mainModule = _assemblyDefinition.MainModule;

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

                typeDef.Interfaces.Clear();
                
                // 处理新增字段
                foreach (var fieldDef in typeDef.Fields)
                {
                    if (result.AddedFields.ContainsKey(fieldDef.FullName))
                    {
                        hookTypeInfo.AddedFields.TryAdd(fieldDef.FullName, new HookFieldInfo(fieldDef));
                    }
                }

                // 预注册方法（复制修改的方法内容）
                void PreRegisterMethods(Dictionary<string, MethodDiffInfo> methodDiffInfos, MemberModifyState hookMethodState)
                {
                    for (int i = typeDef.Methods.Count - 1; i >= 0; i--)
                    {
                        var methodDef = typeDef.Methods[i];
                        if (!methodDiffInfos.TryGetValue(methodDef.FullName, out var methodDiffInfo))
                        {
                            continue;
                        }

                        var hookMethodName = methodDef.FullName;

                        // 复制完整的方法定义，包括方法体
                        var newMethodDef = CopyMethod(methodDef, mainModule.ImportReference(originalType));

                        if (!hookTypeInfo.ModifiedMethods.TryGetValue(hookMethodName, out var hookMethodInfo))
                        {
                            hookMethodInfo = new HookMethodInfo(hookMethodName, newMethodDef, hookMethodState);
                            hookTypeInfo.ModifiedMethods.Add(hookMethodName, hookMethodInfo);
                        }
                        else
                        {
                            hookMethodInfo.WrapperMethodDef = newMethodDef;
                        }

                        typeDef.Methods.Add(newMethodDef);
                    }
                }

                // 预注册添加和修改的方法
                PreRegisterMethods(result.AddedMethods, MemberModifyState.Added);
                PreRegisterMethods(result.ModifiedMethods, MemberModifyState.Modified);
            }

            // 处理方法引用
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

                void HandleMethod(Dictionary<string, MethodDiffInfo> methodDiffInfos)
                {
                    foreach (var (methodFullName, _) in methodDiffInfos)
                    {
                        if (!hookTypeInfo.ModifiedMethods.TryGetValue(methodFullName, out var hookMethodInfo))
                        {
                            continue;
                        }

                        HandleMethodDefinition(hookMethodInfo.WrapperMethodDef);
                    }
                }

                HandleMethod(result.AddedMethods);
                HandleMethod(result.ModifiedMethods);
            }

            // 处理内部类
            foreach (var (nestedTypeName, nestedTypeInfo) in NestedTypeInfo.NestedTypeInfos)
            {
                var typeDef = mainModule.GetType(nestedTypeName);
                // 内部类是Task生成出来的状态机，则保留整个内部类
                bool isTaskStateMachine = TypeInfoHelper.TypeIsTaskStateMachine(typeDef);
                foreach (var methodDef in typeDef.Methods.ToArray())
                {
                    if (isTaskStateMachine || nestedTypeInfo.Methods.Contains(methodDef.FullName)
                                           || methodDef.FullName.Contains("ctor"))
                    {
                        HandleMethodDefinition(methodDef);
                    }
                    else
                    {
                        typeDef.Methods.Remove(methodDef);
                    }
                }

                foreach (var fieldDef in typeDef.Fields.ToArray())
                {
                    if (isTaskStateMachine || nestedTypeInfo.Fields.Contains(fieldDef.FullName))
                    {
                        fieldDef.DeclaringType = (TypeDefinition)GetOriginalType(fieldDef.DeclaringType);
                        fieldDef.FieldType = GetOriginalType(fieldDef.FieldType);
                    }
                    else
                    {
                        typeDef.Fields.Remove(fieldDef);
                    }
                }
            }
        }

        /// <summary>
        /// 清理程序集：删除未使用的类型
        /// </summary>
        private static void ClearAssembly(Dictionary<string, DiffResult> diffResults)
        {
            var mainModule = _assemblyDefinition.MainModule;

            // 清理编译器生成的特性
            var compilerNamespaces = new[] { "System.Runtime.CompilerServices", "Microsoft.CodeAnalysis"  };

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
                if (!diffResults.TryGetValue(typeDef.FullName, out var result))
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
                        if (hookTypeInfo.ModifiedMethods.TryGetValue(methodFullName, out var modifiedMethod))
                        {
                            typeDef.Methods.Add(modifiedMethod.WrapperMethodDef);
                        }
                    }
                }

                // 清理字段
                for (int i = typeDef.Fields.Count - 1; i >= 0; i--)
                {
                    var fieldDef = typeDef.Fields[i];
                    if (!result.AddedFields.ContainsKey(fieldDef.FullName))
                    {
                        typeDef.Fields.RemoveAt(i);
                    }
                }

                // 清理内部类
                for (int i = typeDef.NestedTypes.Count - 1; i >= 0; i--)
                {
                    var nestedType = typeDef.NestedTypes[i];
                    if (!NestedTypeInfo.NestedTypeInfos.ContainsKey(nestedType.FullName))
                    {
                        typeDef.NestedTypes.RemoveAt(i);
                    }
                }

                typeDef.Properties.Clear();
                typeDef.Events.Clear();
            }
        }

        #endregion

        #region 方法、字段修改

        /// <summary>
        /// 复制方法，并添加this参数
        /// </summary>
        private static MethodDefinition CopyMethod(MethodDefinition methodDef, TypeReference originalTypeRef)
        {
            MethodDefinition newMethodDef = new MethodDefinition(methodDef.Name,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, methodDef.ReturnType)
            {
                DeclaringType = methodDef.DeclaringType,
                IsSpecialName = methodDef.IsSpecialName,
                IsRuntimeSpecialName = methodDef.IsRuntimeSpecialName,
            };

            newMethodDef.ImplAttributes |= MethodImplAttributes.NoInlining;

            // 复制泛型参数
            if (methodDef.HasGenericParameters)
            {
                foreach (var sourceGenericParam in methodDef.GenericParameters)
                {
                    newMethodDef.GenericParameters.Add(sourceGenericParam);
                }
            }

            // 将@this参数加入到方法参数列表的头部
            if (originalTypeRef != null && !methodDef.IsStatic)
            {
                newMethodDef.Parameters.Add(new ParameterDefinition("@this", ParameterAttributes.None, originalTypeRef));
            }

            // 复制参数
            foreach (var param in methodDef.Parameters)
            {
                newMethodDef.Parameters.Add(param);
            }

            newMethodDef.Body.MaxStackSize = methodDef.Body.MaxStackSize;

            if (!methodDef.HasBody)
            {
                return newMethodDef;
            }

            // 复制局部变量定义
            foreach (var variable in methodDef.Body.Variables)
            {
                newMethodDef.Body.Variables.Add(variable);
            }

            // 复制指令
            var sourceInstructions = methodDef.Body.Instructions;
            var processor = newMethodDef.Body.GetILProcessor();

            foreach (var sourceInst in sourceInstructions)
            {
                processor.Append(sourceInst);
            }

            // 复制异常处理器
            foreach (var handler in methodDef.Body.ExceptionHandlers)
            {
                newMethodDef.Body.ExceptionHandlers.Add(handler);
            }

            return newMethodDef;
        }

        /// <summary>
        /// 创建指令
        /// </summary>
        private static void HandleInstruction(ILProcessor processor, Instruction sourceInst, bool methodDefIsConstructor)
        {
            if (sourceInst.Operand == null)
            {
                processor.Append(sourceInst);
                return;
            }

            switch (sourceInst.Operand)
            {
                case TypeReference typeRef:
                    sourceInst.Operand = GetOriginalType(typeRef);
                    break;
                case FieldReference:
                    HandleFieldReference(processor, sourceInst, methodDefIsConstructor);
                    return;
                case MethodReference methodRef:
                    sourceInst.Operand = HandleMethodReference(methodRef);
                    break;
            }

            processor.Append(sourceInst);
        }

        /// <summary>
        /// 处理方法引用
        /// </summary>
        private static void HandleMethodDefinition(MethodDefinition methodDef)
        {
            // 返回类型处理
            methodDef.ReturnType = GetOriginalType(methodDef.ReturnType);

            // 泛型参数引用处理
            if (methodDef.HasGenericParameters)
            {
                foreach (GenericParameter genericParameter in methodDef.GenericParameters)
                {
                    foreach (var constraint in genericParameter.Constraints)
                    {
                        constraint.ConstraintType = GetOriginalType(constraint.ConstraintType);
                    }
                }
            }

            // 参数引用处理
            foreach (var param in methodDef.Parameters)
            {
                param.ParameterType = GetOriginalType(param.ParameterType);
            }

            if (!methodDef.HasBody)
            {
                return;
            }

            // 局部变量引用处理
            foreach (var variable in methodDef.Body.Variables)
            {
                variable.VariableType = GetOriginalType(variable.VariableType);
            }

            // 创建所有指令
            var instructions = methodDef.Body.Instructions.ToArray();
            var processor = methodDef.Body.GetILProcessor();
            processor.Clear();

            for (int i = 0; i < instructions.Length; i++)
            {
                HandleInstruction(processor, instructions[i], methodDef.IsConstructor);
            }
        }

        /// <summary>
        /// 处理方法的引用
        /// </summary>
        private static MethodReference HandleMethodReference(MethodReference methodRef)
        {
            var module = methodRef.Module;

            if (HookTypeInfoCache.TryGetValue(methodRef.DeclaringType.FullName, out var hookType))
            {
                // 处理新增方法调用
                if (hookType.TryGetAddedMethod(methodRef.FullName, out var addedMethodInfo))
                {
                    return module.ImportReference(addedMethodInfo.WrapperMethodDef);
                }

                // 处理泛型方法调用
                if (methodRef is GenericInstanceMethod genericMethod
                    && hookType.ModifiedMethods.TryGetValue(methodRef.GetElementMethod().FullName, out var modifiedMethodInfo))
                {
                    var genericInstanceMethod = CreateGenericInstanceMethod(genericMethod, module.ImportReference(modifiedMethodInfo.WrapperMethodDef));

                    return genericInstanceMethod;
                }
            }

            // 内部类的方法处理
            if (methodRef.DeclaringType.IsNested)
            {
                NestedTypeInfo.AddMethod(methodRef);
            }

            var originalMethodRef = GetOriginalMethodReference(methodRef);
            if (originalMethodRef == null)
            {
                originalMethodRef = module.ImportReference(methodRef);
            }

            return originalMethodRef;
        }

        private static GenericInstanceMethod CreateGenericInstanceMethod(GenericInstanceMethod originalMethod, MethodReference elementMethodRef)
        {
            var genericInstanceMethod = new GenericInstanceMethod(elementMethodRef ?? originalMethod.GetElementMethod());
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
        private static void HandleFieldReference(ILProcessor processor, Instruction inst, bool methodDefIsConstructor)
        {
            var fieldRef = inst.Operand as FieldReference;
            if (fieldRef == null)
            {
                return;
            }

            // 处理新增字段的访问
            if (HookTypeInfoCache.TryGetValue(fieldRef.DeclaringType.FullName, out var hookTypeInfo)
                && hookTypeInfo.AddedFields.ContainsKey(fieldRef.FullName))
            {
                var code = inst.OpCode.Code;
                switch (code)
                {
                    case Code.Ldfld:
                    case Code.Ldsfld:
                        ReplaceFieldLoadWithGetHolder(processor, fieldRef, code is Code.Ldsfld);
                        return;
                    case Code.Stfld:
                    case Code.Stsfld:
                        if (methodDefIsConstructor 
                            && ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(fieldRef.DeclaringType.FullName, out var ownerType))
                        {
                            var fieldValue = inst.Previous.Operand;
                            var fieldType = fieldValue.GetType();
                            FieldResolverHelper.RegisterFieldInitializer(ownerType, fieldRef.Name, fieldType, fieldValue);
                        }
                        ReplaceFieldStoreWithGetHolder(processor, fieldRef, code is Code.Stsfld);
                        return;
                    case Code.Ldflda:
                    case Code.Ldsflda:
                        ReplaceFieldAddressWithGetRef(processor, fieldRef, code is Code.Ldsflda);
                        return;
                }
            }

            // 内部类的字段处理
            if (fieldRef.DeclaringType.IsNested)
            {
                NestedTypeInfo.AddField(fieldRef);
                processor.Append(inst);
                return;
            }

            // 普通字段引用处理
            var module = fieldRef.Module;
            var newField = module.ImportReference(new FieldReference(fieldRef.Name, GetOriginalType(fieldRef.FieldType), GetOriginalType(fieldRef.DeclaringType)));
            inst.Operand = newField;
            processor.Append(inst);
        }

        /// <summary>
        /// 替换 ldfld/ldsfld 指令为 FieldResolver.GetHolder 调用
        /// 实例字段：instance.Field → GetHolder(instance, "Field").F
        /// 静态字段：Class.Field → GetHolder(null, "Field").F
        /// </summary>
        private static void ReplaceFieldLoadWithGetHolder(ILProcessor processor, FieldReference fieldRef, bool isStatic)
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
            processor.Append(Instruction.Create(OpCodes.Ldfld, fFieldRef));
        }

        /// <summary>
        /// 替换 stfld/stsfld 指令为 FieldResolver.Store 调用
        /// 实例字段：instance.Field = value → Store(instance, value, "Field")
        /// 静态字段：Class.Field = value → Store(value, "Field")
        /// </summary>
        private static void ReplaceFieldStoreWithGetHolder(ILProcessor processor, FieldReference fieldRef, bool isStatic)
        {
            var fieldType = GetOriginalType(fieldRef.FieldType);
            var ownerType = GetOriginalType(fieldRef.DeclaringType);

            var storeMethodRef = FieldResolverHelper.GetFieldResolverStoreMethodReference(ownerType, fieldType, isStatic);
            processor.Append(Instruction.Create(OpCodes.Ldstr, fieldRef.Name));
            processor.Append(Instruction.Create(OpCodes.Call, storeMethodRef));
        }

        /// <summary>
        /// 替换 ldflda/ldsflda 指令为 FieldResolver.GetHolder + FieldHolder.GetRef 调用
        /// 实例字段：ref instance.Field → GetHolder(instance, "Field").GetRef()
        /// 静态字段：ref Class.Field → GetHolder(null, "Field").GetRef()
        /// </summary>
        private static void ReplaceFieldAddressWithGetRef(ILProcessor processor, FieldReference fieldRef, bool isStatic)
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
            processor.Append(Instruction.Create(OpCodes.Callvirt, getRefMethodRef));
        }
        #endregion

        #region 类型和方法引用处理

        /// <summary>
        /// 获取原类型引用
        /// </summary>
        private static TypeReference GetOriginalType(TypeReference typeRef)
        {
            if (typeRef == null)
            {
                return null;
            }

            var module = typeRef.Module;

            TypeReference originalTypeRef = null;

            // 定义不在当前程序集的之前返回
            if (typeRef.Scope.Name.Equals(module.Name)
                && ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(typeRef.FullName, out var originalType))
            {
                originalTypeRef = module.ImportReference(originalType);
            }

            if (typeRef is GenericInstanceType genericInstanceType)
            {
                TypeReference[] genericArguments = new TypeReference[genericInstanceType.GenericArguments.Count];

                for (int i = 0; i < genericInstanceType.GenericArguments.Count; i++)
                {
                    genericArguments[i] = GetOriginalType(genericInstanceType.GenericArguments[i]);
                }

                GenericInstanceType originalGenericInstanceType =
                    (originalTypeRef ?? typeRef.GetElementType()).MakeGenericInstanceType(genericArguments);


                return originalGenericInstanceType;
            }

            return originalTypeRef ?? typeRef;
        }

        /// <summary>
        /// 从原程序集中获取方法引用
        /// </summary>
        private static MethodReference GetOriginalMethodReference(MethodReference methodRef)
        {
            try
            {
                var module = methodRef.Module;

                var declaringTypeName = methodRef.DeclaringType.IsGenericInstance
                    ? methodRef.DeclaringType.GetElementType().FullName
                    : methodRef.DeclaringType.FullName;

                // 从原类型中获取方法引用
                if (methodRef.DeclaringType.Scope.Name.Equals(module.Name)
                    && ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(declaringTypeName, out var originalType))
                {
                    var originalMethodName = methodRef.IsGenericInstance
                        ? methodRef.GetElementMethod().FullName
                        : methodRef.FullName;

                    var originalMethodRef = originalType.GetMethodReference(module, originalMethodName);

                    if (methodRef is GenericInstanceMethod genericInstanceMethod)
                    {
                        return CreateGenericInstanceMethod(genericInstanceMethod, module.ImportReference(originalMethodRef));
                    }

                    return originalMethodRef;
                }

                if (methodRef is GenericInstanceMethod genericInstanceMethod1)
                {
                    return CreateGenericInstanceMethod(genericInstanceMethod1, null);
                }

                if (methodRef.DeclaringType is GenericInstanceType genericInstanceType)
                {
                    for (int i = 0; i < genericInstanceType.GenericArguments.Count; i++)
                    {
                        genericInstanceType.GenericArguments[i] = GetOriginalType(genericInstanceType.GenericArguments[i]);
                    }
                }

                foreach (var param in methodRef.Parameters)
                {
                    param.ParameterType = GetOriginalType(param.ParameterType);
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
