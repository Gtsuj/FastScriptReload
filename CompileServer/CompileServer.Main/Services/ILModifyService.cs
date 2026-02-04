using System.Reflection;
using CompileServer.Helper;
using CompileServer.Helpers;
using CompileServer.Models;
using HookInfo.Models;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Mono.Collections.Generic;
using Code = Mono.Cecil.Cil.Code;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using MethodImplAttributes = Mono.Cecil.MethodImplAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;

namespace CompileServer.Services
{
    /// <summary>
    /// IL 修改服务
    /// </summary>
    // ReSharper disable once InconsistentNaming
    public class ILModifyService
    {
        #region 私有字段

        private readonly ILogger<ILModifyService> _logger;
        
        private Dictionary<string, AssemblyDefinition> _wrapperAssemblyDict = new();

        #endregion

        #region 构造函数

        public ILModifyService(ILogger<ILModifyService> logger)
        {
            _logger = logger;
        }

        #endregion

        #region Public 方法

        /// <summary>
        /// 修改编译程序集
        /// </summary>
        public Dictionary<string, HookTypeInfo> ModifyCompileAssembly(Dictionary<string, DiffResult> diffResults)
        {
            try
            {
                PreHandleMember(diffResults);

                foreach (var (_, wrapperAssemblyDef) in _wrapperAssemblyDict)
                {
                    HandleMethodsInAssembly(wrapperAssemblyDef);
                }

                // 保存所有程序集，并建立 assemblyName -> filePath 的映射
                var assemblyPathMap = new Dictionary<string, string>();
                foreach (var (assemblyName, wrapperAssemblyDef) in _wrapperAssemblyDict)
                {
                    // 生成文件名并保存
                    var filePath = Path.Combine(ReloadHelper.AssemblyOutputPath, $"{wrapperAssemblyDef.Name.Name}.dll");
                    wrapperAssemblyDef.Write(filePath, ReloadHelper.WRITER_PARAMETERS);
                    wrapperAssemblyDef.Dispose();

                    assemblyPathMap[assemblyName] = filePath;
                }

                // 设置程序集路径并提取本次编译涉及的具体方法和字段信息
                return SetAssemblyPathAndExtractHookTypeInfos(assemblyPathMap, diffResults);
            }
            finally
            {
                _wrapperAssemblyDict.Clear();
            }
        }

        #endregion

        #region Private 方法 - 主流程

        /// <summary>
        /// 预所有处理成员
        /// </summary>
        private void PreHandleMember(Dictionary<string, DiffResult> diffResults)
        {
            foreach (var (typeName, diffResult) in diffResults)
            {
                var assemblyName = diffResult.AssemblyName;
                
                if (!ReloadHelper.HookTypeInfoCache.TryGetValue(typeName, out var hookTypeInfo))
                {
                    hookTypeInfo = new HookTypeInfo
                    {
                        TypeFullName = typeName,
                        AssemblyName = assemblyName,
                    };
                    ReloadHelper.HookTypeInfoCache[typeName] = hookTypeInfo;
                }

                var patchAssemblyDef = diffResult.AssemblyDef;
                var wrapperAssemblyDef = GetOrAddWrapperAssembly(assemblyName, patchAssemblyDef);
                
                var typeDef = patchAssemblyDef?.MainModule.GetType(typeName);

                if (patchAssemblyDef != null)
                {
                    var targetTypeDef = ExtractTypeToAssembly(wrapperAssemblyDef, typeDef);

                    // 处理修改字段
                    foreach (var fieldDef in typeDef.Fields)
                    {
                        if (diffResult.ModifiedFields.ContainsKey(fieldDef.FullName))
                        {
                            var hookFieldInfo = new HookFieldInfo(typeName, fieldDef);
                            hookTypeInfo.ModifiedFields.TryAdd(fieldDef.FullName, hookFieldInfo);
                            
                            // 添加字段到目标类型
                            targetTypeDef.Fields.Add(new FieldDefinition(fieldDef.Name, fieldDef.Attributes, GetOriginalType(fieldDef.FieldType, wrapperAssemblyDef.MainModule)));
                            
                            // 生成字段初始化方法
                            var initMethod = CreateFieldInitializerMethod(wrapperAssemblyDef, typeDef, fieldDef);
                            if (initMethod != null)
                            {
                                // 设置InitializerMethodName
                                hookFieldInfo.SetInitializerMethod(initMethod);
                            }
                        }
                    }

                    // 处理修改方法
                    foreach (var methodDef in typeDef.Methods)
                    {
                        if (!diffResult.ModifiedMethods.TryGetValue(methodDef.FullName, out var methodDiffInfo))
                        {
                            continue;
                        }

                        var wrapperMethodDef = ExtractMethodToAssembly(wrapperAssemblyDef, methodDef);
                        hookTypeInfo.AddOrModifyMethod(methodDef.FullName, wrapperMethodDef, methodDiffInfo.ModifyState);
                    }
                }
                
                // 处理泛型调用者
                foreach (var (methodName, methodDiffInfo) in diffResult.ModifiedMethods)
                {
                    if (!methodDiffInfo.IsMethodCaller)
                    {
                        continue;
                    }

                    if (typeDef != null && typeDef.Methods.Any(m => m.FullName == methodName))
                    {
                        // 已经处理过的方法
                        continue;
                    }

                    var callerMethodDef = ReloadHelper.GetLatestMethodDefinition(typeName, methodName);
                    var wrapperMethodDef = ExtractMethodToAssembly(wrapperAssemblyDef, callerMethodDef);
                    hookTypeInfo.AddOrModifyMethod(methodName, wrapperMethodDef, methodDiffInfo.ModifyState);
                }
            }
        }

        /// <summary>
        /// 处理程序集中的方法体
        /// </summary>
        private void HandleMethodsInAssembly(AssemblyDefinition wrapperAssemblyDef)
        {
            var module = wrapperAssemblyDef.MainModule;

            foreach (var typeDef in module.Types)
            {
                foreach (var methodDef in typeDef.Methods)
                {
                    HandleMethodInstruction(methodDef, module);
                }
            }
        }

        /// <summary>
        /// 设置程序集路径并提取本次编译涉及的具体方法和字段信息
        /// </summary>
        private Dictionary<string, HookTypeInfo> SetAssemblyPathAndExtractHookTypeInfos(
            Dictionary<string, string> assemblyPathMap, Dictionary<string, DiffResult> diffResults)
        {
            var allHookTypeInfos = new Dictionary<string, HookTypeInfo>();
            
            foreach (var (typeFullName, diffResult) in diffResults)
            {
                // 从全局缓存中获取完整的 HookTypeInfo
                if (!ReloadHelper.HookTypeInfoCache.TryGetValue(typeFullName, out var cachedHookTypeInfo))
                {
                    continue;
                }

                // 获取该类型对应的程序集路径
                if (!assemblyPathMap.TryGetValue(diffResult.AssemblyName, out var assemblyPath))
                {
                    continue;
                }

                // 创建新的 HookTypeInfo，只包含本次编译改动的方法和字段
                var hookTypeInfo = new HookTypeInfo
                {
                    TypeFullName = cachedHookTypeInfo.TypeFullName,
                    AssemblyName = cachedHookTypeInfo.AssemblyName
                };

                // 处理本次改动的方法：设置路径并添加到结果中
                foreach (var (methodFullName, _) in diffResult.ModifiedMethods)
                {
                    if (cachedHookTypeInfo.ModifiedMethods.TryGetValue(methodFullName, out var hookMethodInfo))
                    {
                        // 设置程序集路径
                        hookMethodInfo.HistoricalHookedAssemblyPaths.Add(assemblyPath);
                        
                        // 添加到返回结果
                        hookTypeInfo.ModifiedMethods[methodFullName] = hookMethodInfo;
                    }
                }

                // 处理本次改动的字段：设置路径并添加到结果中
                foreach (var (fieldFullName, _) in diffResult.ModifiedFields)
                {
                    if (cachedHookTypeInfo.ModifiedFields.TryGetValue(fieldFullName, out var hookFieldInfo))
                    {
                        // 设置程序集路径
                        hookFieldInfo.HistoricalHookedAssemblyPaths.Add(assemblyPath);
                        
                        // 添加到返回结果
                        hookTypeInfo.ModifiedFields.TryAdd(fieldFullName, hookFieldInfo);
                    }
                }

                // 只有当有实际改动时才添加到结果中
                if (hookTypeInfo.ModifiedMethods.Count > 0 || hookTypeInfo.ModifiedFields.Count > 0)
                {
                    allHookTypeInfos[typeFullName] = hookTypeInfo;
                }
            }

            return allHookTypeInfos;
        }

        #endregion

        #region Private 方法 - 程序集和类型提取

        /// <summary>
        /// 获取或添加包装程序集
        /// </summary>
        private AssemblyDefinition GetOrAddWrapperAssembly(string assemblyName, AssemblyDefinition patchAssembly)
        {
            if (!_wrapperAssemblyDict.TryGetValue(assemblyName, out var assemblyDef))
            {
                if (patchAssembly == null)
                {
                    var wrapperAssemblyName = ReloadHelper.GetWrapperAssemblyName(assemblyName);
                    assemblyDef = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(wrapperAssemblyName, new Version(0, 0, 0, 0)), wrapperAssemblyName, ModuleKind.Dll);
                }
                else
                {
                    assemblyDef = AssemblyDefinition.CreateAssembly(patchAssembly.Name, patchAssembly.MainModule.Name, ModuleKind.Dll);
                }
                
                _wrapperAssemblyDict[assemblyName] = assemblyDef;
            }

            return assemblyDef;
        }

        /// <summary>
        /// 将指定类型提取到目标程序集
        /// </summary>
        private TypeDefinition ExtractTypeToAssembly(AssemblyDefinition wrapperAssemblyDef, TypeDefinition typeDef)
        {
            var module = wrapperAssemblyDef.MainModule;
            var type = module.GetType(typeDef.FullName);
            if (type == null)
            {
                if (typeDef.IsNested)
                {
                    type = new TypeDefinition(typeDef.Namespace, typeDef.Name, typeDef.Attributes, GetOriginalType(typeDef.BaseType, module));

                    if (TypeInfoHelper.IsCompilerGeneratedType(typeDef))
                    {
                        foreach (var implementation in typeDef.Interfaces)
                        {
                            type.Interfaces.Add(new InterfaceImplementation(GetOriginalType(implementation.InterfaceType, module)));
                        }

                        foreach (var attribute in typeDef.CustomAttributes)
                        {
                            type.CustomAttributes.Add(new CustomAttribute(module.ImportReference(attribute.Constructor)));
                        }
                    }
                    else
                    {
                        type.IsPublic = true;
                    }

                    module.GetType(typeDef.DeclaringType.FullName).NestedTypes.Add(type);
                }
                else
                {
                    type = new TypeDefinition(typeDef.Namespace, typeDef.Name, typeDef.Attributes, GetOriginalType(typeDef.Module.TypeSystem.Object, module))
                    {
                        IsPublic = true
                    };
                    module.Types.Add(type);
                }
            }

            return type;
        }

        private void HandleMethodParam(MethodReference sourceMethodRef, MethodReference targetMethodRef, ModuleDefinition module)
        {
            Dictionary<string, GenericParameter> genericParameters = new();

            // 复制泛型参数
            if (sourceMethodRef.HasGenericParameters)
            {
                foreach (GenericParameter originalGenericParam in sourceMethodRef.GenericParameters)
                {
                    var newGenericParam = new GenericParameter(originalGenericParam.Name, targetMethodRef)
                    {
                        Attributes = originalGenericParam.Attributes
                    };

                    // 复制约束
                    foreach (var constraint in originalGenericParam.Constraints)
                    {
                        newGenericParam.Constraints.Add(new GenericParameterConstraint(GetOriginalType(constraint.ConstraintType, module)));
                    }

                    targetMethodRef.GenericParameters.Add(newGenericParam);

                    genericParameters.Add(newGenericParam.Name, newGenericParam);
                }
            }
            
            targetMethodRef.ReturnType = GetOriginalType(targetMethodRef.ReturnType, module, genericParameters);

            // 复制参数
            foreach (var originalParam in sourceMethodRef.Parameters)
            {
                var newParam = new ParameterDefinition(
                    originalParam.Name,
                    originalParam.Attributes,
                    GetOriginalType(originalParam.ParameterType, module, genericParameters));

                // 复制默认值
                if (originalParam.HasConstant)
                {
                    newParam.Constant = originalParam.Constant;
                }

                targetMethodRef.Parameters.Add(newParam);
            }
            
            if (sourceMethodRef is MethodDefinition sourceMethodDef && targetMethodRef is MethodDefinition targetMethodDef
                && sourceMethodDef.HasBody)
            {
                // 局部变量引用处理
                foreach (var variable in sourceMethodDef.Body.Variables)
                {
                    targetMethodDef.Body.Variables.Add(new VariableDefinition(GetOriginalType(variable.VariableType, module, genericParameters)));
                }
            }
        }
        
        /// <summary>
        /// 将指定方法提取到目标程序集，指令不处理
        /// </summary>
        private MethodDefinition ExtractMethodToAssembly(AssemblyDefinition wrapperAssemblyDef, MethodDefinition methodDef, bool addThisParam = true)
        {
            var type = ExtractTypeToAssembly(wrapperAssemblyDef, methodDef.DeclaringType);
            
            MethodDefinition wrapperMethodDef = null;
            wrapperMethodDef = type.Methods.FirstOrDefault((method => method.FullName.Equals(methodDef.FullName)));

            if (wrapperMethodDef != null)
            {
                return wrapperMethodDef;
            }

            var module = wrapperAssemblyDef.MainModule;
            
            // 创建新方法定义
            if (methodDef.DeclaringType.IsNested && TypeInfoHelper.IsCompilerGeneratedType(methodDef.DeclaringType))
            {
                wrapperMethodDef = new MethodDefinition(methodDef.Name, methodDef.Attributes, GetOriginalType(methodDef.ReturnType, module));

                foreach (var methodOverride in methodDef.Overrides)
                {
                    wrapperMethodDef.Overrides.Add(module.ImportReference(methodOverride));
                }
                
                foreach (var attribute in methodDef.CustomAttributes)
                {
                    wrapperMethodDef.CustomAttributes.Add(new CustomAttribute(module.ImportReference(attribute.Constructor)));
                }
            }
            else
            {
                wrapperMethodDef = new MethodDefinition(methodDef.Name, MethodAttributes.Public | MethodAttributes.Static, methodDef.ReturnType)
                {
                    ImplAttributes = MethodImplAttributes.NoInlining,
                };
            }

            if (!methodDef.IsStatic && addThisParam)
            {
                wrapperMethodDef.Parameters.Insert(0, new ParameterDefinition("this", ParameterAttributes.None, GetOriginalType(methodDef.DeclaringType, module)));
                wrapperMethodDef.HasThis = false;
                wrapperMethodDef.ExplicitThis = false;
            }

            HandleMethodParam(methodDef, wrapperMethodDef, module);

            if (methodDef.HasBody)
            {
                wrapperMethodDef.Body.MaxStackSize = methodDef.Body.MaxStackSize;
                wrapperMethodDef.Body.InitLocals = methodDef.Body.InitLocals;

                // 复制所有指令
                var newProcessor = wrapperMethodDef.Body.GetILProcessor();
                var instructions = methodDef.Body.Instructions.ToArray();
                for (int i = 0; i < instructions.Length; i++)
                {
                    newProcessor.Append(instructions[i]);
                }

                // 复制异常处理块
                foreach (var originalHandler in methodDef.Body.ExceptionHandlers)
                {
                    originalHandler.CatchType = originalHandler.CatchType != null ? GetOriginalType(originalHandler.CatchType, module) : null;
                    wrapperMethodDef.Body.ExceptionHandlers.Add(originalHandler);
                }
            }

            // 调试信息
            if (methodDef.DebugInformation.HasSequencePoints)
            {
                foreach (var sequencePoint in methodDef.DebugInformation.SequencePoints)
                {
                    wrapperMethodDef.DebugInformation.SequencePoints.Add(sequencePoint);
                }

                // 处理调试信息中的类型引用
                HandleImportScope(wrapperMethodDef.DebugInformation?.Scope?.Import, module);
            }

            // 将新方法添加到类型中
            type.Methods.Add(wrapperMethodDef);

            return wrapperMethodDef;
        }

        private TypeDefinition ExtractNestedTypeToAssembly(AssemblyDefinition wrapperAssemblyDef, TypeDefinition typeDef)
        {
            var module = wrapperAssemblyDef.MainModule;
            var nestedType = module.GetType(typeDef.FullName);
            if (nestedType != null)
            {
                return nestedType;
            }

            nestedType = ExtractTypeToAssembly(wrapperAssemblyDef, typeDef);

            var isTaskStateMachine = TypeInfoHelper.TypeIsTaskStateMachine(typeDef);
            var isCompilerGenerated = TypeInfoHelper.IsCompilerGeneratedType(typeDef);

            if (isTaskStateMachine)
            {
                foreach (var fieldDef in typeDef.Fields)
                {
                    nestedType.Fields.Add(new FieldDefinition(fieldDef.Name, fieldDef.Attributes, GetOriginalType(fieldDef.FieldType, module)));
                }

                foreach (var propertyDef in typeDef.Properties)
                {
                    nestedType.Properties.Add(new PropertyDefinition(propertyDef.Name, propertyDef.Attributes, GetOriginalType(propertyDef.PropertyType, module)));
                }
            }

            if (isTaskStateMachine || isCompilerGenerated)
            {
                foreach (var methodDef in typeDef.Methods)
                {
                    if (isTaskStateMachine || methodDef.IsConstructor)
                    {
                        var wrapperMethodDef = ExtractMethodToAssembly(wrapperAssemblyDef, methodDef, false);
                        HandleMethodInstruction(wrapperMethodDef, wrapperAssemblyDef.MainModule);
                    }
                }   
            }

            return nestedType;
        }
        #endregion

        #region Private 方法 - 指令处理

        private void HandleMethodInstruction(MethodDefinition methodDef, ModuleDefinition module)
        {
            var instructions = methodDef.Body.Instructions.ToArray();
            var processor = methodDef.Body.GetILProcessor();
            processor.Clear();

            for (int i = 0; i < instructions.Length; i++)
            {
                var sourceInst = instructions[i];
                if (sourceInst.Operand == null)
                {
                    processor.Append(sourceInst);
                    continue;
                }

                switch (sourceInst.Operand)
                {
                    case TypeReference typeRef:
                        sourceInst.Operand = GetOriginalType(typeRef, module, methodDef.HasGenericParameters ? methodDef.GenericParameters.ToDictionary(gp => gp.Name) : null);
                        break;
                    case FieldReference:
                        HandleFieldReference(processor, sourceInst, module);
                        continue;
                    case MethodReference methodRef:
                        sourceInst.Operand = HandleMethodReference(methodRef, module);
                        break;
                }

                processor.Append(sourceInst);
            }
        }

        /// <summary>
        /// 处理调试信息中的类型引用
        /// </summary>
        private void HandleImportScope(ImportDebugInformation import, ModuleDefinition moduleDef)
        {
            if (import == null)
            {
                return;
            }

            if (import.Parent != null)
            {
                HandleImportScope(import.Parent, moduleDef);
            }

            if (!import.HasTargets)
            {
                return;
            }

            foreach (var importTarget in import.Targets)
            {
                importTarget.Type = GetOriginalType(importTarget.Type, moduleDef);
            }
        }

        /// <summary>
        /// 处理方法的引用
        /// </summary>
        private MethodReference HandleMethodReference(MethodReference methodRef, ModuleDefinition module)
        {
            if (methodRef.DeclaringType.IsNested && methodRef is MethodDefinition nestedMethodDef)
            {
                var wrapperMethodDef = GetNestedMethodDefinition(module.Assembly, nestedMethodDef);
                return wrapperMethodDef;
            }

            if (!CheckMethodIsScopeInModule(methodRef, module))
            {
                return module.ImportReference(methodRef);
            }

            // 处理新增/泛型方法调用（从全局缓存中获取）
            if (ReloadHelper.HookTypeInfoCache.TryGetValue(methodRef.DeclaringType.FullName, out var hookTypeInfo))
            {
                var methodName = methodRef.IsGenericInstance ? TypeInfoHelper.GetGenericMethodDefName(methodRef.GetElementMethod().FullName) : methodRef.FullName;

                if (hookTypeInfo.ModifiedMethods.TryGetValue(methodName, out var methodInfo))
                {
                    if (methodRef is GenericInstanceMethod genericInstance)
                    {
                        return CreateGenericInstanceMethod(methodInfo.WrapperMethodDef, genericInstance.GenericArguments, module);
                    }

                    if (methodInfo.MemberModifyState == MemberModifyState.Added)
                    {
                        return module.ImportReference(methodInfo.WrapperMethodDef);
                    }
                }
            }

            MethodReference CopyMethodReference(MethodReference sourceMethodRef)
            {
                var newMethodRef = new MethodReference(sourceMethodRef.Name, sourceMethodRef.ReturnType, GetOriginalType(sourceMethodRef.DeclaringType, module))
                {
                    HasThis = sourceMethodRef.HasThis,
                    ExplicitThis = sourceMethodRef.ExplicitThis,
                    CallingConvention = sourceMethodRef.CallingConvention
                };

                HandleMethodParam(sourceMethodRef, newMethodRef, module);

                return newMethodRef;
            }

            // 处理泛型方法实例
            if (methodRef is GenericInstanceMethod genericInstanceMethod)
            {
                var elementMethod = CopyMethodReference(genericInstanceMethod.GetElementMethod());

                return CreateGenericInstanceMethod(elementMethod, genericInstanceMethod.GenericArguments, module);
            }

            var originalMethodRef = TypeInfoHelper.GetOriginalMethodReference(methodRef, module);

            if (originalMethodRef != null)
            {
                return originalMethodRef;
            }

            return CopyMethodReference(methodRef);
        }

        /// <summary>
        /// 创建泛型实例方法
        /// </summary>
        private MethodReference CreateGenericInstanceMethod(MethodReference elementMethod, Collection<TypeReference> genericArguments, ModuleDefinition module)
        {
            var newGenericInstanceMethod = new GenericInstanceMethod(elementMethod);
            // 添加泛型参数
            foreach (var typeRef in genericArguments)
            {
                newGenericInstanceMethod.GenericArguments.Add(GetOriginalType(typeRef, module));
            }

            return module.ImportReference(newGenericInstanceMethod);
        }

        /// <summary>
        /// 处理字段引用指令
        /// </summary>
        private void HandleFieldReference(ILProcessor processor, Instruction inst, ModuleDefinition module)
        {
            var fieldRef = inst.Operand as FieldReference;
            if (fieldRef == null)
            {
                return;
            }

            // 处理新增字段的访问（从全局缓存中获取）
            if (ReloadHelper.HookTypeInfoCache.TryGetValue(fieldRef.DeclaringType.FullName, out var hookTypeInfo)
                && hookTypeInfo.ModifiedFields.ContainsKey(fieldRef.FullName))
            {
                var code = inst.OpCode.Code;
                switch (code)
                {
                    case Code.Ldfld:
                    case Code.Ldsfld:
                        ReplaceFieldLoadWithGetHolder(processor, fieldRef, code == Code.Ldsfld, module);
                        return;
                    case Code.Stfld:
                    case Code.Stsfld:
                        ReplaceFieldStoreWithGetHolder(processor, fieldRef, code == Code.Stsfld, module);
                        return;
                    case Code.Ldflda:
                    case Code.Ldsflda:
                        ReplaceFieldAddressWithGetRef(processor, fieldRef, code == Code.Ldsflda, module);
                        return;
                }
            }

            // 编译时创建的内部类的成员后续统一处理
            if (TypeInfoHelper.IsCompilerGeneratedType(fieldRef.DeclaringType as TypeDefinition))
            {
                // NestedTypeInfo.AddField(fieldRef);
                inst.Operand = GetNestedFieldDefinition(module.Assembly, fieldRef as FieldDefinition);
                processor.Append(inst);
                return;
            }

            // 普通字段引用处理
            var newField = module.ImportReference(new FieldReference(fieldRef.Name, GetOriginalType(fieldRef.FieldType, module), GetOriginalType(fieldRef.DeclaringType, module)));
            inst.Operand = newField;
            processor.Append(inst);
        }

        /// <summary>
        /// 替换 ldfld/ldsfld 指令为 FieldResolver.GetHolder 调用
        /// 实例字段：instance.Field → GetHolder(instance, "Field").F
        /// 静态字段：Class.Field → GetHolder(null, "Field").F
        /// </summary>
        private void ReplaceFieldLoadWithGetHolder(ILProcessor processor, FieldReference fieldRef, bool isStatic, ModuleDefinition module)
        {
            var fieldType = GetOriginalType(fieldRef.FieldType, module);
            var ownerType = GetOriginalType(fieldRef.DeclaringType, module);

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
        private void ReplaceFieldStoreWithGetHolder(ILProcessor processor, FieldReference fieldRef, bool isStatic, ModuleDefinition module)
        {
            var fieldType = GetOriginalType(fieldRef.FieldType, module);
            var ownerType = GetOriginalType(fieldRef.DeclaringType, module);

            var storeMethodRef = FieldResolverHelper.GetFieldResolverStoreMethodReference(ownerType, fieldType, isStatic);
            processor.Append(Instruction.Create(OpCodes.Ldstr, fieldRef.Name));
            processor.Append(Instruction.Create(OpCodes.Call, storeMethodRef));
        }

        /// <summary>
        /// 替换 ldflda/ldsflda 指令为 FieldResolver.GetHolder + FieldHolder.GetRef 调用
        /// 实例字段：ref instance.Field → GetHolder(instance, "Field").GetRef()
        /// 静态字段：ref Class.Field → GetHolder(null, "Field").GetRef()
        /// </summary>
        private void ReplaceFieldAddressWithGetRef(ILProcessor processor, FieldReference fieldRef, bool isStatic, ModuleDefinition module)
        {
            var fieldType = GetOriginalType(fieldRef.FieldType, module);
            var ownerType = GetOriginalType(fieldRef.DeclaringType, module);

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

        #region Private 方法 - 类型和方法引用处理

        /// <summary>
        /// 检查类型是否定义在 Hook 程序集中（包括递归检查 TypeSpecification）
        /// </summary>
        private bool CheckTypeIsScopeInModule(TypeReference typeRef, ModuleDefinition module)
        {
            if (typeRef == null)
            {
                return false;
            }

            // 泛型参数不算 Hook 程序集类型
            if (typeRef is GenericParameter)
            {
                return true;
            }

            if (typeRef.IsNested)
            {
                return true;
            }

            // 编译时生成的类型不需要处理
            if (TypeInfoHelper.IsCompilerGeneratedType(typeRef as TypeDefinition))
            {
                return false;
            }

            // 基础类型：直接检查 Scope
            if (typeRef.Scope.Name.Equals(module.Name) || typeRef.Scope is ModuleDefinition)
            {
                return true;
            }

            // TypeSpecification（复合类型）：需要递归检查内部类型
            if (typeRef is TypeSpecification typeSpec)
            {
                // 特殊处理：泛型实例类型（需要检查泛型定义 + 所有泛型参数）
                if (typeSpec is GenericInstanceType genericInstanceType)
                {
                    // 检查泛型定义（如 List<> 是否在 Hook 程序集）
                    if (CheckTypeIsScopeInModule(genericInstanceType.GetElementType(), module))
                    {
                        return true;
                    }
                    
                    // 检查所有泛型参数（如 Test 是否在 Hook 程序集）
                    foreach (var genericArgument in genericInstanceType.GenericArguments)
                    {
                        if (CheckTypeIsScopeInModule(genericArgument, module))
                        {
                            return true;
                        }
                    }
                    
                    return false;
                }

                // 其他 TypeSpecification：递归检查 ElementType
                return CheckTypeIsScopeInModule(typeSpec.ElementType, module);
            }

            return false;
        }

        /// <summary>
        /// 获取原类型引用
        /// </summary>
        private TypeReference GetOriginalType(TypeReference typeRef, ModuleDefinition module, Dictionary<string, GenericParameter> genericParameters = null)
        {
            if (!CheckTypeIsScopeInModule(typeRef, module))
            {
                return module.ImportReference(typeRef);
            }

            if (typeRef is GenericParameter genericParameter)
            {
                if (genericParameters != null && genericParameters.TryGetValue(genericParameter.Name, out var gp))
                {
                    return gp;
                }

                GenericParameter newGenericParameter = null;
                switch (genericParameter.Owner)
                {
                    case MethodReference methodRef:
                        newGenericParameter = new GenericParameter(genericParameter.Name, HandleMethodReference(methodRef, module));
                        break;
                    case TypeReference typeRefOwner:
                        newGenericParameter = new GenericParameter(genericParameter.Name, GetOriginalType(typeRefOwner, module));
                        break;
                }

                var field = typeof(GenericParameter).GetField("position", BindingFlags.Instance | BindingFlags.NonPublic);
                field?.SetValue(newGenericParameter, genericParameter.Position);

                return newGenericParameter;
            }
            
            if (typeRef.IsNested && typeRef is TypeDefinition nestedTypeDef)
            {
                return ExtractNestedTypeToAssembly(module.Assembly, nestedTypeDef);
            }

            if (typeRef is TypeSpecification typeSpec)
            {
                // 泛型实例类型（需要处理多个泛型参数）
                if (typeSpec is GenericInstanceType genericInstanceType)
                {
                    return ProcessGenericInstanceType(genericInstanceType, module, genericParameters);
                }

                // 数组类型（需要保留维度信息）
                if (typeSpec is ArrayType arrayType)
                {
                    return ProcessArrayType(arrayType, module, genericParameters);
                }

                // 根据原类型重新构造相应的复合类型
                var elementType = GetOriginalType(typeSpec.ElementType, module, genericParameters);

                return typeSpec switch
                {
                    PointerType _ => new PointerType(elementType),
                    ByReferenceType _ => new ByReferenceType(elementType),
                    PinnedType _ => new PinnedType(elementType),
                    RequiredModifierType reqModType => new RequiredModifierType(GetOriginalType(reqModType.ModifierType, module), elementType),
                    OptionalModifierType optModType => new OptionalModifierType(GetOriginalType(optModType.ModifierType, module), elementType),
                    SentinelType _ => new SentinelType(elementType),
                    _ => module.ImportReference(typeSpec) // 未知类型，保持原样
                };
            }

            var originalTypeDef = TypeInfoHelper.GetOriginalTypeReference(typeRef);
            if (originalTypeDef != null)
            {
                // 找到原类型，导入引用并返回
                return module.ImportReference(originalTypeDef);
            }

            // 未找到原类型，返回原类型引用（导入引用以确保类型正确）
            return module.ImportReference(typeRef);
        }
        
        private FieldDefinition GetNestedFieldDefinition(AssemblyDefinition wrapperAssemblyDef, FieldDefinition fieldDef)
        {
            var typeDef = ExtractNestedTypeToAssembly(wrapperAssemblyDef, fieldDef.DeclaringType.Resolve());
            var wrapperFieldDef = typeDef.Fields.FirstOrDefault(f => f.FullName == fieldDef.FullName);
            if (wrapperFieldDef == null)
            {
                wrapperFieldDef = new FieldDefinition(fieldDef.Name, fieldDef.Attributes, GetOriginalType(fieldDef.FieldType, wrapperAssemblyDef.MainModule));
                typeDef.Fields.Add(wrapperFieldDef);
            }
            return wrapperFieldDef;
        }
        
        private MethodDefinition GetNestedMethodDefinition(AssemblyDefinition wrapperAssemblyDef, MethodDefinition methodDef)
        {
            var typeDef = ExtractNestedTypeToAssembly(wrapperAssemblyDef, methodDef.DeclaringType.Resolve());
            var wrapperMethodDef = typeDef.Methods.FirstOrDefault(m => m.Name == methodDef.Name && m.Parameters.Count == methodDef.Parameters.Count);
            if (wrapperMethodDef == null)
            {
                wrapperMethodDef = ExtractMethodToAssembly(wrapperAssemblyDef, methodDef, false);

                HandleMethodInstruction(wrapperMethodDef, wrapperAssemblyDef.MainModule);
            }
            return wrapperMethodDef;
        }        

        /// <summary>
        /// 处理泛型实例类型（如 List&lt;Test&gt;、Dictionary&lt;string, Test[]&gt;）
        /// 特殊性：除了 ElementType，还有多个 GenericArguments 需要处理
        /// </summary>
        private TypeReference ProcessGenericInstanceType(GenericInstanceType genericInstanceType, ModuleDefinition module, Dictionary<string, GenericParameter> genericParameters = null)
        {
            // 1. 递归处理泛型定义（如 List<> 本身可能也需要重定向）
            var elementType = genericInstanceType.GetElementType();
            var originalElementType = GetOriginalType(elementType, module);

            // 2. 递归处理每个泛型参数（如 Test、string、Test[] 等）
            var genericArguments = new TypeReference[genericInstanceType.GenericArguments.Count];
            for (int i = 0; i < genericInstanceType.GenericArguments.Count; i++)
            {
                genericArguments[i] = GetOriginalType(genericInstanceType.GenericArguments[i], module, genericParameters);
            }

            // 3. 用处理后的元素类型和泛型参数重新构造泛型实例
            return originalElementType.MakeGenericInstanceType(genericArguments);
        }

        /// <summary>
        /// 处理数组类型（如 Test[]、Test[,]、Test[,,]）
        /// </summary>
        private TypeReference ProcessArrayType(ArrayType arrayType, ModuleDefinition module,
            Dictionary<string, GenericParameter> genericParameters = null)
        {
            // 递归处理数组元素类型
            var elementType = GetOriginalType(arrayType.ElementType, module, genericParameters);

            var newArrayType = new ArrayType(elementType);
            foreach (var dimension in arrayType.Dimensions)
            {
                newArrayType.Dimensions.Add(dimension);
            }

            return newArrayType;
        }

        /// <summary>
        /// 检查方法引用是否涉及 Hook 程序集（需要处理）
        /// 检查范围：
        /// 1. 声明类型 (DeclaringType)
        /// 2. 返回类型 (ReturnType)
        /// 3. 参数类型 (Parameters)
        /// 4. 泛型参数 (GenericArguments for GenericInstanceMethod)
        /// </summary>
        private bool CheckMethodIsScopeInModule(MethodReference methodRef, ModuleDefinition module)
        {
            if (methodRef == null)
            {
                return false;
            }

            // 1. 检查声明类型
            if (CheckTypeIsScopeInModule(methodRef.DeclaringType, module))
            {
                return true;
            }

            // 2. 检查返回类型
            if (CheckTypeIsScopeInModule(methodRef.ReturnType, module))
            {
                return true;
            }

            // 3. 检查参数类型
            if (methodRef.HasParameters)
            {
                foreach (var param in methodRef.Parameters)
                {
                    if (CheckTypeIsScopeInModule(param.ParameterType, module))
                    {
                        return true;
                    }
                }
            }

            // 4. 检查泛型方法的泛型参数
            if (methodRef is GenericInstanceMethod genericMethod)
            {
                foreach (var arg in genericMethod.GenericArguments)
                {
                    if (CheckTypeIsScopeInModule(arg, module))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion

        #region Private 方法 - 字段初始化

        /// <summary>
        /// 为新增字段创建初始化方法
        /// 方法签名：public static TField __Init_{FieldName}__()
        /// 注意：不需要 instance 参数，只返回字段的默认初始值
        /// 返回字段的实际类型，运行时会转换为 Func<object>
        /// 如果字段没有初始化逻辑，返回 null
        /// </summary>
        private MethodDefinition CreateFieldInitializerMethod(AssemblyDefinition wrapperAssemblyDef, TypeDefinition typeDef, FieldDefinition fieldDef)
        {
            var module = wrapperAssemblyDef.MainModule;
            var targetTypeDef = ExtractTypeToAssembly(wrapperAssemblyDef, typeDef);
            
            // 方法名格式: <Init_{FieldName}>
            var methodName = $"<Init_{fieldDef.Name}>";
            var fieldType = GetOriginalType(fieldDef.FieldType, module);
            
            // 创建初始化方法（无参数，返回字段类型）
            var initMethod = new MethodDefinition(
                methodName,
                MethodAttributes.Public | MethodAttributes.Static,
                fieldType
            )
            {
                ImplAttributes = MethodImplAttributes.NoInlining
            };
            
            // 从原类型的构造函数或字段初始化器中提取初始化逻辑
            bool hasInitLogic = ExtractFieldInitializationLogic(initMethod, typeDef, fieldDef);
            
            if (!hasInitLogic)
            {
                // 没有初始化逻辑，不生成初始化方法
                return null;
            }
            
            // 添加方法到类型
            targetTypeDef.Methods.Add(initMethod);
            
            return initMethod;
        }

        /// <summary>
        /// 从原类型构造函数中提取字段的初始化逻辑
        /// 如果没有找到初始化逻辑，返回 null 表示不需要初始化方法
        /// </summary>
        private bool ExtractFieldInitializationLogic(MethodDefinition initMethod, TypeDefinition originalType, FieldDefinition fieldDef)
        {
            // 查找构造函数
            var ctor = fieldDef.IsStatic 
                ? originalType.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic)
                : originalType.Methods.FirstOrDefault(m => m.IsConstructor && !m.IsStatic);
            
            if (ctor == null || !ctor.HasBody)
            {
                // 没有构造函数，不生成初始化方法
                return false;
            }

            // 查找字段初始化指令序列
            var instructions = ctor.Body.Instructions;
            for (int i = 0; i < instructions.Count; i++)
            {
                var inst = instructions[i];
                if (inst.OpCode != OpCodes.Stfld && inst.OpCode != OpCodes.Stsfld)
                {
                    continue;
                }

                var fieldRef = inst.Operand as FieldReference;

                if (fieldRef == null || fieldRef.FullName != fieldDef.FullName)
                {
                    continue;
                }

                for (int j = i - 1; j >= 0; j--)
                {
                    var prevInst = instructions[j];

                    if (inst.OpCode == OpCodes.Stfld && prevInst.OpCode == OpCodes.Ldarg_0)
                    {
                        initMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        return true;
                    }

                    if (inst.OpCode == OpCodes.Stsfld && prevInst.OpCode == OpCodes.Stsfld)
                    {
                        initMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        return true;
                    }

                    initMethod.Body.Instructions.Insert(0, prevInst);

                    if (j == 0)
                    {
                        initMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        return true;
                    }
                }
            }

            return false;
        }
        #endregion
    }
}
