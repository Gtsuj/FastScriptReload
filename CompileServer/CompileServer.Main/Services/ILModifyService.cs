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
        private readonly ILogger<ILModifyService> _logger;

        public ILModifyService(ILogger<ILModifyService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// 修改编译后的程序集，生成 Wrapper 程序集
        /// </summary>
        public string ModifyCompileAssembly(string assemblyName, Dictionary<string, DiffResult> diffResults)
        {
            var assemblyDef = TypeInfoHelper.CloneAssemblyDefinition(assemblyName);
            if (assemblyDef == null)
            {
                throw new Exception($"无法克隆程序集: {assemblyName}");
            }

            NestedTypeInfo.Clear();

            // 从全局缓存中获取或创建 HookTypeInfo
            foreach (var (typeFullName, diffResult) in diffResults)
            {
                if (!ReloadHelper.HookTypeInfoCache.TryGetValue(typeFullName, out var cachedHookTypeInfo))
                {
                    cachedHookTypeInfo = new HookTypeInfo
                    {
                        TypeFullName = typeFullName,
                        AssemblyName = assemblyName,
                    };
                    ReloadHelper.HookTypeInfoCache[typeFullName] = cachedHookTypeInfo;
                }
            }

            // 修改程序集
            HandleAssemblyType(assemblyDef, diffResults);

            // 清理程序集
            ClearAssembly(assemblyDef, diffResults);

            // 生成文件名并保存
            var filePath = Path.Combine(ReloadHelper.AssemblyOutputPath, $"{assemblyDef.Name.Name}.dll");
            assemblyDef.Write(filePath, TypeInfoHelper.WRITER_PARAMETERS);
            assemblyDef.Dispose();

            // 设置每个类型的程序集路径（从全局缓存中获取）
            SetAssemblyPath(filePath, diffResults);

            return filePath;
        }

        private void HandleAssemblyType(AssemblyDefinition assemblyDef, Dictionary<string, DiffResult> diffResults)
        {
            var mainModule = assemblyDef.MainModule;

            // 先处理新增字段、注册方法到HookTypeInfo中
            foreach (var (typeName, diffResult) in diffResults)
            {
                var typeDef = mainModule.GetType(typeName);
                if (typeDef == null)
                {
                    continue;
                }

                if (!ReloadHelper.HookTypeInfoCache.TryGetValue(typeDef.FullName, out var hookTypeInfo))
                {
                    continue;
                }

                // 处理新增字段
                foreach (var fieldDef in typeDef.Fields)
                {
                    if (diffResult.AddedFields.ContainsKey(fieldDef.FullName))
                    {
                        hookTypeInfo.AddedFields.TryAdd(fieldDef.FullName, new HookFieldInfo(fieldDef));
                    }
                }

                // 处理原方法
                for (int i = typeDef.Methods.Count - 1; i >= 0; i--)
                {
                    var methodDef = typeDef.Methods[i];
                    if (!diffResult.ModifiedMethods.TryGetValue(methodDef.FullName, out var methodDiffInfo))
                    {
                        continue;
                    }

                    hookTypeInfo.AddOrModifyMethod(methodDef.FullName, methodDef, methodDiffInfo.ModifyState);
                }
            }

            // 处理方法引用
            foreach (var (typeName, result) in diffResults)
            {
                var typeDef = mainModule.GetType(typeName);
                if (typeDef == null)
                {
                    continue;
                }

                if (!ReloadHelper.HookTypeInfoCache.TryGetValue(typeDef.FullName, out var hookTypeInfo))
                {
                    continue;
                }

                foreach (var (methodFullName, _) in result.ModifiedMethods)
                {
                    if (!hookTypeInfo.ModifiedMethods.TryGetValue(methodFullName, out var hookMethodInfo))
                    {
                        continue;
                    }

                    HandleMethodDefinition(hookMethodInfo.WrapperMethodDef, mainModule);
                }
            }

            // 处理内部类
            foreach (var (nestedTypeName, nestedTypeInfo) in NestedTypeInfo.NestedTypeInfos)
            {
                var typeDef = mainModule.GetType(nestedTypeName);
                if (typeDef == null)
                {
                    continue;
                }

                // 内部类是Task生成出来的状态机，则保留整个内部类
                bool isTaskStateMachine = TypeInfoHelper.TypeIsTaskStateMachine(typeDef);
                foreach (var methodDef in typeDef.Methods.ToArray())
                {
                    bool isHandleMethodDef = isTaskStateMachine || methodDef.FullName.Contains("ctor");
                    if (isHandleMethodDef)
                    {
                        HandleMethodDefinition(methodDef, mainModule);
                    }

                    if (!isHandleMethodDef && !nestedTypeInfo.Methods.Contains(methodDef.FullName))
                    {
                        typeDef.Methods.Remove(methodDef);
                    }
                }

                foreach (var fieldDef in typeDef.Fields.ToArray())
                {
                    if (isTaskStateMachine || nestedTypeInfo.Fields.Contains(fieldDef.FullName))
                    {
                        fieldDef.DeclaringType = (TypeDefinition)GetOriginalType(fieldDef.DeclaringType, mainModule);
                        fieldDef.FieldType = GetOriginalType(fieldDef.FieldType, mainModule);
                    }
                    else
                    {
                        typeDef.Fields.Remove(fieldDef);
                    }
                }
            }

            // 给所有方法加上this参数
            foreach (var (typeName, result) in diffResults)
            {
                var typeDef = mainModule.GetType(typeName);
                if (typeDef == null)
                {
                    continue;
                }

                if (!ReloadHelper.HookTypeInfoCache.TryGetValue(typeDef.FullName, out var hookTypeInfo))
                {
                    continue;
                }

                typeDef.Interfaces.Clear();
                bool setBaseType = typeDef.BaseType != null && typeDef.BaseType.FullName != "System.Object";
                if (setBaseType)
                {
                    typeDef.BaseType = mainModule.TypeSystem.Object;
                }

                foreach (var (methodFullName, _) in result.ModifiedMethods)
                {
                    if (!hookTypeInfo.ModifiedMethods.TryGetValue(methodFullName, out var hookMethodInfo))
                    {
                        continue;
                    }

                    var methodDef = hookMethodInfo.WrapperMethodDef;

                    // 将@this参数加入到方法参数列表的头部
                    if (!methodDef.IsStatic)
                    {
                        methodDef.Parameters.Insert(0, new ParameterDefinition("this", ParameterAttributes.None, typeDef));
                        methodDef.HasThis = false;
                        methodDef.ExplicitThis = false;
                    }

                    methodDef.Attributes = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;
                    methodDef.ImplAttributes |= MethodImplAttributes.NoInlining;

                    if (setBaseType)
                    {
                        methodDef.Overrides.Clear();
                    }

                    hookMethodInfo.WrapperMethodName = methodDef.FullName;
                }
            }            
        }

        /// <summary>
        /// 清理程序集：删除未使用的类型
        /// </summary>
        private void ClearAssembly(AssemblyDefinition assemblyDef, Dictionary<string, DiffResult> diffResults)
        {
            var mainModule = assemblyDef.MainModule;

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
                if (!diffResults.TryGetValue(typeDef.FullName, out var result))
                {
                    mainModule.Types.Remove(typeDef);
                    continue;
                }

                // 清理方法（从全局缓存中获取）
                if (ReloadHelper.HookTypeInfoCache.TryGetValue(typeDef.FullName, out var hookTypeInfo))
                {
                    typeDef.Methods.Clear();
                    foreach (var (methodFullName, _) in result.ModifiedMethods)
                    {
                        if (hookTypeInfo.ModifiedMethods.TryGetValue(methodFullName, out var modifiedMethod))
                        {
                            // 直接使用 WrapperMethodDef，与原始逻辑一致
                            if (modifiedMethod.WrapperMethodDef != null)
                            {
                                typeDef.Methods.Add(modifiedMethod.WrapperMethodDef);
                            }
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

        /// <summary>
        /// 设置程序集路径
        /// </summary>
        private void SetAssemblyPath(string assemblyPath, Dictionary<string, DiffResult> diffResults)
        {
            foreach (var (typeFullName, diffResult) in diffResults)
            {
                if (!ReloadHelper.HookTypeInfoCache.TryGetValue(typeFullName, out var hookTypeInfo))
                {
                    continue;
                }

                foreach (var (_, methodInfo) in diffResult.ModifiedMethods)
                {
                    if (hookTypeInfo.ModifiedMethods.TryGetValue(methodInfo.FullName, out var modifiedMethodInfo))
                    {
                        modifiedMethodInfo.AssemblyPath = assemblyPath;
                        if (modifiedMethodInfo.MemberModifyState == MemberModifyState.Added)
                        {
                            modifiedMethodInfo.HistoricalHookedAssemblyPaths.Add(assemblyPath);
                        }
                    }
                }

                foreach (var (_, fieldInfo) in diffResult.AddedFields)
                {
                    if (hookTypeInfo.AddedFields.TryGetValue(fieldInfo.FullName, out var addedFieldInfo))
                    {
                        addedFieldInfo.AssemblyPath = assemblyPath;
                    }
                }
            }
        }

        #region 方法、字段修改

        /// <summary>
        /// 创建指令
        /// </summary>
        private void HandleInstruction(ILProcessor processor, Instruction sourceInst, ModuleDefinition module)
        {
            if (sourceInst.Operand == null)
            {
                processor.Append(sourceInst);
                return;
            }

            switch (sourceInst.Operand)
            {
                case TypeReference typeRef:
                    sourceInst.Operand = GetOriginalType(typeRef, module);
                    break;
                case FieldReference:
                    HandleFieldReference(processor, sourceInst, module);
                    return;
                case MethodReference methodRef:
                    sourceInst.Operand = HandleMethodReference(methodRef, module);
                    break;
            }

            processor.Append(sourceInst);
        }

        /// <summary>
        /// 处理方法定义和指令
        /// </summary>
        private void HandleMethodDefinition(MethodDefinition methodDef, ModuleDefinition moduleDef)
        {
            // 返回类型处理
            methodDef.ReturnType = GetOriginalType(methodDef.ReturnType, moduleDef);

            // 泛型参数引用处理
            if (methodDef.HasGenericParameters)
            {
                foreach (GenericParameter genericParameter in methodDef.GenericParameters)
                {
                    foreach (var constraint in genericParameter.Constraints)
                    {
                        constraint.ConstraintType = GetOriginalType(constraint.ConstraintType, moduleDef);
                    }
                }
            }

            // 参数引用处理
            foreach (var param in methodDef.Parameters)
            {
                param.ParameterType = GetOriginalType(param.ParameterType, moduleDef);
            }

            if (!methodDef.HasBody)
            {
                return;
            }

            // 局部变量引用处理
            foreach (var variable in methodDef.Body.Variables)
            {
                variable.VariableType = GetOriginalType(variable.VariableType, moduleDef);
            }

            // 创建所有指令
            var instructions = methodDef.Body.Instructions.ToArray();
            var processor = methodDef.Body.GetILProcessor();
            processor.Clear();

            for (int i = 0; i < instructions.Length; i++)
            {
                HandleInstruction(processor, instructions[i], moduleDef);
            }
        }

        /// <summary>
        /// 处理方法的引用
        /// </summary>
        private MethodReference HandleMethodReference(MethodReference methodRef, ModuleDefinition module)
        {
            // 编译时创建的内部类的成员后续统一处理
            if (TypeInfoHelper.IsCompilerGeneratedType(methodRef.DeclaringType as TypeDefinition))
            {
                if (NestedTypeInfo.AddMethod(methodRef))
                {
                    HandleMethodDefinition(methodRef as MethodDefinition, module);
                }
                return methodRef;
            }

            if (TypeInfoHelper.IsTaskCallStartMethod(methodRef))
            {
                var callMethodDef = TypeInfoHelper.FindTaskCallMethod(methodRef);
                if (NestedTypeInfo.AddMethod(callMethodDef))
                {
                    HandleMethodDefinition(callMethodDef, module);
                }
                return methodRef;
            }

            if (!CheckMethodIsScopeInModule(methodRef, module))
            {
                return methodRef;
            }

            // 处理新增/泛型方法调用（从全局缓存中获取）
            if (ReloadHelper.HookTypeInfoCache.TryGetValue(methodRef.DeclaringType.FullName, out var hookTypeInfo))
            {
                var methodName = methodRef.IsGenericInstance ? methodRef.GetElementMethod().FullName : methodRef.FullName;

                if (hookTypeInfo.ModifiedMethods.TryGetValue(methodName, out var methodInfo))
                {
                    if(methodRef is GenericInstanceMethod genericInstance)
                    {
                        return CreateGenericInstanceMethod(methodInfo.WrapperMethodDef, genericInstance.GenericArguments, module);
                    }
                    
                    if (methodInfo.MemberModifyState == MemberModifyState.Added)
                    {
                        return module.ImportReference(methodInfo.WrapperMethodDef);
                    }
                }
            }

            MethodReference CopyMethodReference(MethodReference targetMethodRef)
            {
                var newMethodRef = new MethodReference(targetMethodRef.Name, GetOriginalType(targetMethodRef.ReturnType, module), GetOriginalType(targetMethodRef.DeclaringType, module))
                {
                    HasThis = targetMethodRef.HasThis,
                    ExplicitThis = targetMethodRef.ExplicitThis,
                    CallingConvention = targetMethodRef.CallingConvention
                };

                // 添加参数
                foreach (var parameter in targetMethodRef.Parameters)
                {
                    newMethodRef.Parameters.Add(new ParameterDefinition(GetOriginalType(parameter.ParameterType, module)));
                }

                // 泛型参数
                foreach (var parameter in targetMethodRef.GenericParameters)
                {
                    newMethodRef.GenericParameters.Add(new GenericParameter(parameter.Name, parameter.Owner));
                }

                return newMethodRef;
            }

            // 处理泛型方法实例
            if (methodRef is GenericInstanceMethod genericInstanceMethod)
            {
                var elementMethod = CopyMethodReference(genericInstanceMethod.GetElementMethod());

                return CreateGenericInstanceMethod(elementMethod, genericInstanceMethod.GenericArguments, module);
            }

            return CopyMethodReference(methodRef);
        }

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
                && hookTypeInfo.AddedFields.ContainsKey(fieldRef.FullName))
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
                NestedTypeInfo.AddField(fieldRef);
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

        #region 类型和方法引用处理

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
                return false;
            }

            // 编译时生成的类型不需要处理
            if (TypeInfoHelper.IsCompilerGeneratedType(typeRef as TypeDefinition))
            {
                return false;
            }

            // 基础类型：直接检查 Scope
            if (typeRef.Scope.Name.Equals(module.Name))
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
        private TypeReference GetOriginalType(TypeReference typeRef, ModuleDefinition module)
        {
            if (!CheckTypeIsScopeInModule(typeRef, module))
            {
                return typeRef;
            }

            if (typeRef is TypeSpecification typeSpec)
            {
                // 泛型实例类型（需要处理多个泛型参数）
                if (typeSpec is GenericInstanceType genericInstanceType)
                {
                    return ProcessGenericInstanceType(genericInstanceType, module);
                }

                // 数组类型（需要保留维度信息）
                if (typeSpec is ArrayType arrayType)
                {
                    return ProcessArrayType(arrayType, module);
                }

                // 根据原类型重新构造相应的复合类型
                var elementType = GetOriginalType(typeSpec.ElementType, module);

                return typeSpec switch
                {
                    PointerType _ => new PointerType(elementType),
                    ByReferenceType _ => new ByReferenceType(elementType),
                    PinnedType _ => new PinnedType(elementType),
                    RequiredModifierType reqModType => new RequiredModifierType(GetOriginalType(reqModType.ModifierType, module), elementType),
                    OptionalModifierType optModType => new OptionalModifierType(GetOriginalType(optModType.ModifierType, module), elementType),
                    SentinelType _ => new SentinelType(elementType),
                    _ => typeSpec // 未知类型，保持原样
                };
            }

            var originalTypeDef = TypeInfoHelper.GetOriginalTypeDefinition(typeRef);
            if (originalTypeDef != null)
            {
                // 找到原类型，导入引用并返回
                return module.ImportReference(originalTypeDef);
            }

            // 未找到原类型，返回原类型引用（导入引用以确保类型正确）
            return module.ImportReference(typeRef);
        }

        /// <summary>
        /// 处理泛型实例类型（如 List&lt;Test&gt;、Dictionary&lt;string, Test[]&gt;）
        /// 特殊性：除了 ElementType，还有多个 GenericArguments 需要处理
        /// </summary>
        private TypeReference ProcessGenericInstanceType(GenericInstanceType genericInstanceType, ModuleDefinition module)
        {
            // 1. 递归处理泛型定义（如 List<> 本身可能也需要重定向）
            var elementType = genericInstanceType.GetElementType();
            var originalElementType = GetOriginalType(elementType, module);

            // 2. 递归处理每个泛型参数（如 Test、string、Test[] 等）
            var genericArguments = new TypeReference[genericInstanceType.GenericArguments.Count];
            for (int i = 0; i < genericInstanceType.GenericArguments.Count; i++)
            {
                genericArguments[i] = GetOriginalType(genericInstanceType.GenericArguments[i], module);
            }

            // 3. 用处理后的元素类型和泛型参数重新构造泛型实例
            return originalElementType.MakeGenericInstanceType(genericArguments);
        }

        /// <summary>
        /// 处理数组类型（如 Test[]、Test[,]、Test[,,]）
        /// </summary>
        private TypeReference ProcessArrayType(ArrayType arrayType, ModuleDefinition module)
        {
            // 递归处理数组元素类型
            var elementType = GetOriginalType(arrayType.ElementType, module);

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
    }

    /// 用户自定义的内部类：
    /// - 直接在 HandleAssemblyType 主流程中处理（与外部类相同）
    /// - 可能包含新增/修改的方法，需要正常的 Hook 流程
    public class NestedTypeInfo
    {
        public string NestedTypeName { get; }

        public HashSet<string> Methods { get; } = new();

        public HashSet<string> Fields { get; } = new();

        public NestedTypeInfo(string nestedTypeName)
        {
            NestedTypeName = nestedTypeName;
        }

        public static Dictionary<string, NestedTypeInfo> NestedTypeInfos = new();

        public static bool AddMethod(MethodReference methodRef)
        {
            if (!(methodRef.DeclaringType is TypeDefinition typeDef))
            {
                return false;
            }

            var fullName = methodRef.DeclaringType.FullName;
            if (!NestedTypeInfos.TryGetValue(fullName, out var nestedTypeInfo))
            {
                nestedTypeInfo = new NestedTypeInfo(typeDef.FullName);
                NestedTypeInfos[fullName] = nestedTypeInfo;
            }

            return nestedTypeInfo.Methods.Add(methodRef.FullName);
        }

        public static bool AddField(FieldReference fieldRef)
        {
            if (!(fieldRef.DeclaringType is TypeDefinition typeDef))
            {
                return false;
            }

            var fullName = fieldRef.DeclaringType.FullName;
            if (!NestedTypeInfos.TryGetValue(fullName, out var nestedTypeInfo))
            {
                nestedTypeInfo = new NestedTypeInfo(typeDef.FullName);
                NestedTypeInfos[fullName] = nestedTypeInfo;
            }

            return nestedTypeInfo.Fields.Add(fieldRef.FullName);
        }

        public static void Clear()
        {
            NestedTypeInfos.Clear();
        }
    }
}
