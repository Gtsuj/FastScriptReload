using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FastScriptReload.Runtime;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
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
            HandleAssemblyType(results);

            // 清理程序集
            ClearAssembly(results);

            // 添加必要的引用
            AddRequiredReferences(_assemblyDefinition, HookTypeInfoCache.Values.ToList());

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

        private static void HandleAssemblyType(Dictionary<string, DiffResult> results)
        {
            var mainModule = _assemblyDefinition.MainModule;
            foreach (var typeDef in mainModule.Types)
            {
                if (!results.TryGetValue(typeDef.FullName, out DiffResult result))
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
                
                HashSet<MethodDefinition> hookMethods = new();
                
                var originalMethods = originalType.GetAllMethods();

                void HandleModifyMethod(List<MethodDiffInfo> methodDiffInfos, HookMethodState hookMethodState)
                {
                    foreach (var methodDiffInfo in methodDiffInfos)
                    {
                        var hookMethodName = methodDiffInfo.FullName;

                        var methodDef = typeDef.Methods.FirstOrDefault((definition => definition.FullName == hookMethodName));
                        if (methodDef == null)
                        {
                            continue;
                        }
                        
                        var newMethodDef = ModifyMethod(mainModule, methodDef, mainModule.ImportReference(originalType));
                        if (!hookTypeInfo.ModifiedMethods.TryGetValue(hookMethodName, out var hookMethodInfo))
                        {
                            hookMethodInfo = new HookMethodInfo(newMethodDef, hookMethodState, 
                                originalMethods.FirstOrDefault(info => info.FullName().Equals(hookMethodName)));
                            hookTypeInfo.ModifiedMethods.Add(hookMethodName, hookMethodInfo);
                        }
                        else
                        {
                            hookMethodInfo.MethodDefinition = newMethodDef;
                        }
                        
                        typeDef.Methods.Add(newMethodDef);
                        hookMethods.Add(newMethodDef);
                    }
                }
                
                // 处理添加的方法
                HandleModifyMethod(result.AddedMethods, HookMethodState.Added);

                // 处理修改的方法
                HandleModifyMethod(result.ModifiedMethods, HookMethodState.Modified);
                
                // 清理方法并提交修改过后的方法
                typeDef.Methods.Clear();
                foreach (var method in hookMethods)
                {
                    typeDef.Methods.Add(method);
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
                }
            }
        }

        #endregion

        #region 方法、字段修改

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
                CreateInstruction(processor, module, sourceInstructions[i], parameterMap, genericParamMap);
            }

            return newMethodDef;
        }
        
        /// <summary>
        /// 创建指令
        /// </summary>
        private static void CreateInstruction(ILProcessor processor, ModuleDefinition module, Instruction sourceInst,
            Dictionary<ParameterDefinition, ParameterDefinition> parameterMap, Dictionary<GenericParameter, GenericParameter> genericParamMap)
        {
            if (sourceInst.Operand == null)
            {
                processor.Append(Instruction.Create(sourceInst.OpCode));
                return;
            }

            switch (sourceInst.Operand)
            {
                case Instruction targetInst:
                    processor.Append(Instruction.Create(sourceInst.OpCode, targetInst));
                    return;

                case VariableDefinition variableDef:
                    processor.Append(Instruction.Create(sourceInst.OpCode, variableDef));
                    return;

                case ParameterDefinition paramDef:
                    processor.Append(Instruction.Create(sourceInst.OpCode, parameterMap[paramDef]));
                    return;

                case TypeReference typeRef:
                    processor.Append(Instruction.Create(sourceInst.OpCode, GetOriginalType(module, typeRef, genericParamMap)));
                    return;

                case FieldReference fieldRef:
                    HandleFieldReference(processor, module, sourceInst, fieldRef, genericParamMap);
                    return;

                case MethodReference methodRef:
                    HandleMethodReference(processor, module, sourceInst, methodRef);
                    return;

                default:
                    // 处理基本类型操作数
                    var inst = sourceInst.Operand switch
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
                    processor.Append(inst);
                    break;
            }
        }
        
        /// <summary>
        /// 处理方法引用指令
        /// </summary>
        private static void HandleMethodReference(ILProcessor processor, ModuleDefinition module, Instruction sourceInst, MethodReference methodRef)
        {
            // 处理新增方法调用
            if (HookTypeInfoCache.TryGetValue(methodRef.DeclaringType.FullName, out var hookType)
                && hookType.TryGetAddedMethod(methodRef.FullName, out var addedMethodInfo))
            {
                methodRef = module.ImportReference(addedMethodInfo.MethodDefinition);
                processor.Append(Instruction.Create(sourceInst.OpCode, methodRef));
                return;
            }

            var originalMethodRef = GetOriginalMethodReference(module, methodRef);
            if (originalMethodRef == null)
            {
                LoggerScoped.LogWarning($"从原程序集获取方法引用失败: {methodRef.FullName}");
                originalMethodRef = methodRef;
            }

            processor.Append(Instruction.Create(sourceInst.OpCode, originalMethodRef));
        }

        /// <summary>
        /// 处理字段引用指令
        /// </summary>
        private static void HandleFieldReference(ILProcessor processor, ModuleDefinition module, Instruction sourceInst,
            FieldReference fieldRef, Dictionary<GenericParameter, GenericParameter> genericParamMap)
        {
            // 处理新增字段的访问
            if (HookTypeInfoCache.TryGetValue(fieldRef.DeclaringType?.FullName ?? string.Empty, out var hookTypeInfo)
                && hookTypeInfo.AddedFields.ContainsKey(fieldRef.FullName))
            {
                var code = sourceInst.OpCode.Code;
                var isStatic = code == Code.Ldsfld || code == Code.Stsfld || code == Code.Ldsflda;

                switch (code)
                {
                    case Code.Ldfld:
                    case Code.Ldsfld:
                        ReplaceFieldLoadWithGetHolder(processor, sourceInst, fieldRef, module, genericParamMap, isStatic);
                        return;
                    case Code.Stfld:
                    case Code.Stsfld:
                        ReplaceFieldStoreWithGetHolder(processor, sourceInst, fieldRef, module, genericParamMap, isStatic);
                        return;
                    case Code.Ldflda:
                    case Code.Ldsflda:
                        ReplaceFieldAddressWithGetRef(processor, sourceInst, fieldRef, module, genericParamMap, isStatic);
                        return;
                }
            }

            // 普通字段引用处理
            var newFieldType = GetOriginalType(module, fieldRef.FieldType, genericParamMap);
            var newDeclaringType = GetOriginalType(module, fieldRef.DeclaringType, genericParamMap);
            var newFieldRef = fieldRef.DeclaringType == null
                ? new FieldReference(fieldRef.Name, newFieldType)
                : new FieldReference(fieldRef.Name, newFieldType, newDeclaringType);

            processor.Append(Instruction.Create(sourceInst.OpCode, newFieldRef));
        }

        /// <summary>
        /// 获取 FieldResolver&lt;TOwner&gt;.GetHolder&lt;TField&gt; 方法引用
        /// </summary>
        private static MethodReference GetFieldResolverGetHolderMethodReference(ModuleDefinition module, TypeReference ownerType, TypeReference fieldType)
        {
            try
            {
                var fieldResolverOpenType = typeof(FieldResolver<>);
                var fieldResolverTypeRef = module.ImportReference(fieldResolverOpenType);
                var fieldResolverGenericType = MonoCecilHelper.CreateGenericInstanceType(fieldResolverTypeRef, ownerType);
                if (fieldResolverGenericType == null)
                {
                    LoggerScoped.LogError("无法创建 FieldResolver 泛型实例类型");
                    return null;
                }

                var fieldHolderOpenType = typeof(FieldHolder<>);
                var fieldHolderTypeRef = module.ImportReference(fieldHolderOpenType);

                var methodRef = new MethodReference("GetHolder", fieldHolderTypeRef, fieldResolverGenericType)
                {
                    HasThis = false,
                    ExplicitThis = false,
                    CallingConvention = MethodCallingConvention.Default
                };

                var genericParam = new GenericParameter("TField", methodRef);
                methodRef.GenericParameters.Add(genericParam);

                var fieldHolderGenericType = MonoCecilHelper.CreateGenericInstanceType(fieldHolderTypeRef, genericParam);
                if (fieldHolderGenericType == null)
                {
                    LoggerScoped.LogError("无法创建 FieldHolder 泛型实例类型");
                    return null;
                }
                methodRef.ReturnType = fieldHolderGenericType;

                methodRef.Parameters.Add(new ParameterDefinition("instance", ParameterAttributes.None, module.TypeSystem.Object));
                methodRef.Parameters.Add(new ParameterDefinition("fieldName", ParameterAttributes.None, module.TypeSystem.String));

                var genericInstanceMethod = MonoCecilHelper.CreateGenericInstanceMethod(methodRef, fieldType);
                if (genericInstanceMethod == null)
                {
                    LoggerScoped.LogError("无法创建 GetHolder 泛型实例方法");
                    return null;
                }

                LoggerScoped.LogDebug($"成功创建 GetHolder 方法引用: FieldResolver<{ownerType.FullName}>.GetHolder<{fieldType.FullName}>");
                return genericInstanceMethod;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"构建 FieldResolver.GetHolder 方法引用失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取 FieldHolder&lt;TField&gt;.F 字段引用
        /// </summary>
        private static FieldReference GetFieldHolderFReference(ModuleDefinition module, TypeReference fieldType)
        {
            try
            {
                var fieldHolderOpenType = typeof(FieldHolder<>);
                var fieldHolderTypeRef = module.ImportReference(fieldHolderOpenType);

                var fieldHolderGenericType = MonoCecilHelper.CreateGenericInstanceType(fieldHolderTypeRef, fieldType);
                if (fieldHolderGenericType == null)
                {
                    LoggerScoped.LogError("无法创建 FieldHolder 泛型实例类型");
                    return null;
                }

                var fieldHolderClosedType = fieldHolderOpenType.MakeGenericType(typeof(object));
                var fFieldInfo = fieldHolderClosedType.GetField("F", BindingFlags.Public | BindingFlags.Instance);
                if (fFieldInfo == null)
                {
                    LoggerScoped.LogError("无法通过反射找到 FieldHolder.F 字段");
                    return null;
                }

                var fFieldRef = module.ImportReference(fFieldInfo);
                var declaringTypeProperty = typeof(FieldReference).GetProperty("DeclaringType");
                declaringTypeProperty?.SetValue(fFieldRef, fieldHolderGenericType);

                return fFieldRef;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"构建 FieldHolder.F 字段引用失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取 FieldHolder&lt;TField&gt;.GetRef 方法引用
        /// </summary>
        private static MethodReference GetFieldHolderGetRefMethodReference(ModuleDefinition module, TypeReference fieldType)
        {
            try
            {
                var fieldHolderOpenType = typeof(FieldHolder<>);
                var fieldHolderTypeRef = module.ImportReference(fieldHolderOpenType);

                var fieldHolderGenericType = MonoCecilHelper.CreateGenericInstanceType(fieldHolderTypeRef, fieldType);
                if (fieldHolderGenericType == null)
                {
                    LoggerScoped.LogError("无法创建 FieldHolder 泛型实例类型");
                    return null;
                }

                var fieldHolderClosedType = fieldHolderOpenType.MakeGenericType(typeof(object));
                var getRefMethodInfo = fieldHolderClosedType.GetMethod("GetRef", BindingFlags.Public | BindingFlags.Instance);
                if (getRefMethodInfo == null)
                {
                    LoggerScoped.LogError("无法通过反射找到 FieldHolder.GetRef 方法");
                    return null;
                }

                var methodRef = module.ImportReference(getRefMethodInfo);
                var declaringTypeProperty = typeof(MethodReference).GetProperty("DeclaringType");
                declaringTypeProperty?.SetValue(methodRef, fieldHolderGenericType);

                LoggerScoped.LogDebug($"成功创建 GetRef 方法引用: FieldHolder<{fieldType.FullName}>.GetRef()");
                return methodRef;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"构建 FieldHolder.GetRef 方法引用失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 替换 ldfld/ldsfld 指令为 FieldResolver.GetHolder 调用
        /// 实例字段：instance.Field → GetHolder(instance, "Field").F
        /// 静态字段：Class.Field → GetHolder(null, "Field").F
        /// </summary>
        private static void ReplaceFieldLoadWithGetHolder(ILProcessor processor, Instruction ldfldInst, FieldReference fieldRef,
            ModuleDefinition module, Dictionary<GenericParameter, GenericParameter> genericParamMap, bool isStatic)
        {
            try
            {
                var fieldType = GetOriginalType(module, fieldRef.FieldType, genericParamMap);
                var ownerType = GetOriginalType(module, fieldRef.DeclaringType, genericParamMap);

                var getHolderMethodRef = GetFieldResolverGetHolderMethodReference(module, ownerType, fieldType);
                var fFieldRef = GetFieldHolderFReference(module, fieldType);

                if (getHolderMethodRef == null || fFieldRef == null)
                {
                    LoggerScoped.LogError($"无法获取字段引用: {fieldRef.FullName}");
                    processor.Append(Instruction.Create(ldfldInst.OpCode, fieldRef));
                    return;
                }

                // 生成指令序列
                if (isStatic)
                {
                    processor.Append(Instruction.Create(MonoCecilHelper.GetOpCode("Ldnull")));
                }

                processor.Append(Instruction.Create(MonoCecilHelper.GetOpCode("Ldstr"), fieldRef.Name));
                processor.Append(Instruction.Create(MonoCecilHelper.GetOpCode("Call"), getHolderMethodRef));
                processor.Append(Instruction.Create(MonoCecilHelper.GetOpCode("Ldfld"), fFieldRef));
                
                LoggerScoped.LogDebug($"成功替换 {(isStatic ? "ldsfld" : "ldfld")} 指令: {fieldRef.FullName}");
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"替换 {(isStatic ? "ldsfld" : "ldfld")} 指令失败: {fieldRef.FullName}, 错误: {ex.Message}");
                processor.Append(Instruction.Create(ldfldInst.OpCode, fieldRef));
            }
        }

        /// <summary>
        /// 替换 stfld/stsfld 指令为 FieldResolver.Store 调用
        /// 实例字段：instance.Field = value → Store(instance, value, "Field")
        /// 静态字段：Class.Field = value → Store(value, "Field")
        /// </summary>
        private static void ReplaceFieldStoreWithGetHolder(ILProcessor processor, Instruction stfldInst, FieldReference fieldRef, 
            ModuleDefinition module, Dictionary<GenericParameter, GenericParameter> genericParamMap, bool isStatic)
        {
            try
            {
                var fieldType = GetOriginalType(module, fieldRef.FieldType, genericParamMap);
                var ownerType = GetOriginalType(module, fieldRef.DeclaringType, genericParamMap);

                var storeMethodRef = GetFieldResolverStoreMethodReference(module, ownerType, fieldType, isStatic);
                if (storeMethodRef == null)
                {
                    LoggerScoped.LogError($"无法获取 Store 方法引用: {fieldRef.FullName}");
                    processor.Append(Instruction.Create(stfldInst.OpCode, fieldRef));
                    return;
                }

                processor.Append(Instruction.Create(MonoCecilHelper.GetOpCode("Ldstr"), fieldRef.Name));
                processor.Append(Instruction.Create(MonoCecilHelper.GetOpCode("Call"), storeMethodRef));
                
                LoggerScoped.LogDebug($"成功替换 {(isStatic ? "stsfld" : "stfld")} 指令: {fieldRef.FullName}");
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"替换 {(isStatic ? "stsfld" : "stfld")} 指令失败: {fieldRef.FullName}, 错误: {ex.Message}");
                processor.Append(Instruction.Create(stfldInst.OpCode, fieldRef));
            }
        }
        
        /// <summary>
        /// 替换 ldflda/ldsflda 指令为 FieldResolver.GetHolder + FieldHolder.GetRef 调用
        /// 实例字段：ref instance.Field → GetHolder(instance, "Field").GetRef()
        /// 静态字段：ref Class.Field → GetHolder(null, "Field").GetRef()
        /// </summary>
        private static void ReplaceFieldAddressWithGetRef(ILProcessor processor, Instruction ldfldaInst, FieldReference fieldRef,
            ModuleDefinition module, Dictionary<GenericParameter, GenericParameter> genericParamMap, bool isStatic)
        {
            try
            {
                var fieldType = GetOriginalType(module, fieldRef.FieldType, genericParamMap);
                var ownerType = GetOriginalType(module, fieldRef.DeclaringType, genericParamMap);

                var getHolderMethodRef = GetFieldResolverGetHolderMethodReference(module, ownerType, fieldType);
                var getRefMethodRef = GetFieldHolderGetRefMethodReference(module, fieldType);

                if (getHolderMethodRef == null || getRefMethodRef == null)
                {
                    LoggerScoped.LogError($"无法获取字段引用: {fieldRef.FullName}");
                    processor.Append(Instruction.Create(ldfldaInst.OpCode, fieldRef));
                    return;
                }

                if (isStatic)
                {
                    processor.Append(Instruction.Create(MonoCecilHelper.GetOpCode("Ldnull")));
                }

                processor.Append(Instruction.Create(MonoCecilHelper.GetOpCode("Ldstr"), fieldRef.Name));
                processor.Append(Instruction.Create(MonoCecilHelper.GetOpCode("Call"), getHolderMethodRef));
                processor.Append(Instruction.Create(MonoCecilHelper.GetOpCode("Callvirt"), getRefMethodRef));
                
                LoggerScoped.LogDebug($"成功替换 {(isStatic ? "ldsflda" : "ldflda")} 指令: {fieldRef.FullName}");
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"替换 {(isStatic ? "ldsflda" : "ldflda")} 指令失败: {fieldRef.FullName}, 错误: {ex.Message}");
                processor.Append(Instruction.Create(ldfldaInst.OpCode, fieldRef));
            }
        }
        
        /// <summary>
        /// 获取 FieldResolver&lt;TOwner&gt;.Store&lt;TField&gt; 方法引用
        /// 实例字段：Store(object instance, TField value, string fieldName)
        /// 静态字段：Store(TField value, string fieldName)
        /// </summary>
        private static MethodReference GetFieldResolverStoreMethodReference(ModuleDefinition module, TypeReference ownerType, TypeReference fieldType, bool isStatic)
        {
            try
            {
                var fieldResolverOpenType = typeof(FieldResolver<>);
                var fieldResolverTypeRef = module.ImportReference(fieldResolverOpenType);
                var fieldResolverGenericType = MonoCecilHelper.CreateGenericInstanceType(fieldResolverTypeRef, ownerType);
                if (fieldResolverGenericType == null)
                {
                    LoggerScoped.LogError("无法创建 FieldResolver 泛型实例类型");
                    return null;
                }

                var methodRef = new MethodReference("Store", module.TypeSystem.Void, fieldResolverGenericType)
                {
                    HasThis = false,
                    ExplicitThis = false,
                    CallingConvention = MethodCallingConvention.Default
                };

                var genericParam = new GenericParameter("TField", methodRef);
                methodRef.GenericParameters.Add(genericParam);

                if (isStatic)
                {
                    methodRef.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, genericParam));
                    methodRef.Parameters.Add(new ParameterDefinition("fieldName", ParameterAttributes.None, module.TypeSystem.String));
                }
                else
                {
                    methodRef.Parameters.Add(new ParameterDefinition("instance", ParameterAttributes.None, module.TypeSystem.Object));
                    methodRef.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, genericParam));
                    methodRef.Parameters.Add(new ParameterDefinition("fieldName", ParameterAttributes.None, module.TypeSystem.String));
                }

                var genericInstanceMethod = MonoCecilHelper.CreateGenericInstanceMethod(methodRef, fieldType);
                if (genericInstanceMethod == null)
                {
                    LoggerScoped.LogError("无法创建 Store 泛型实例方法");
                    return null;
                }

                LoggerScoped.LogDebug($"成功创建 Store 方法引用: FieldResolver<{ownerType.FullName}>.Store<{fieldType.FullName}>({(isStatic ? "2参数" : "3参数")})");
                return genericInstanceMethod;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"构建 FieldResolver.Store 方法引用失败: {ex.Message}");
                return null;
            }
        }
        
        #endregion

        #region 程序集引用管理

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

        #endregion

        #region 类型和方法引用处理

        /// <summary>
        /// 获取原类型引用
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
                var originalTypeDef = originalAssemblyDef?.MainModule.Types.FirstOrDefault(t => t.FullName == declaringTypeFullName);
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

                var genericInstanceMethod = MonoCecilHelper.CreateGenericInstanceMethodFromSource(originalMethodRef, methodRef, typeRef => GetOriginalType(module, typeRef));

                return genericInstanceMethod ?? module.ImportReference(methodRef);
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
