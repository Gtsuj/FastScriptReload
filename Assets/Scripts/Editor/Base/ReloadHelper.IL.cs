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
            var assemblyDef = TypeInfoHelper.CloneAssemblyDefinition(assemblyName);

            NestedTypeInfo.Clear();

            // 修改程序集
            HandleAssemblyType(assemblyDef, diffResults);

            // 清理程序集
            ClearAssembly(assemblyDef, diffResults);

            // 生成文件名并保存
            var filePath = Path.Combine(ASSEMBLY_OUTPUT_PATH, $"{assemblyDef.Name.Name}.dll");

            // 确保目录存在
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            assemblyDef.Write(filePath, TypeInfoHelper.WRITER_PARAMETERS);

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

        private static void HandleAssemblyType(AssemblyDefinition assemblyDef, Dictionary<string, DiffResult> diffResults)
        {
            var mainModule = assemblyDef.MainModule;

            foreach (var (typeName, result) in diffResults)
            {
                var typeDef = mainModule.GetType(typeName);

                if (!ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(typeDef.FullName, out var originalType))
                {
                    continue;
                }

                if (!HookTypeInfoCache.TryGetValue(typeDef.FullName, out var hookTypeInfo))
                {
                    hookTypeInfo = new HookTypeInfo
                    {
                        TypeFullName = typeDef.FullName,
                    };

                    HookTypeInfoCache.Add(typeDef.FullName, hookTypeInfo);
                }

                typeDef.Interfaces.Clear();
                bool setBaseType = typeDef.BaseType != null && typeDef.BaseType.FullName != "System.Object";
                if (setBaseType)
                {
                    typeDef.BaseType = mainModule.ImportReference(typeof(object));
                }
                
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

                        // 将@this参数加入到方法参数列表的头部
                        var originalTypeRef = mainModule.ImportReference(originalType);
                        if (originalTypeRef != null && !methodDef.IsStatic)
                        {
                            methodDef.Parameters.Insert(0, new ParameterDefinition("this", ParameterAttributes.None, originalTypeRef));
                        }
                        
                        methodDef.Attributes = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;
                        methodDef.ImplAttributes |= MethodImplAttributes.NoInlining;
                        methodDef.HasThis = false;
                        methodDef.ExplicitThis = false;
                        if (setBaseType)
                        {
                            methodDef.Overrides.Clear();
                        }

                        hookTypeInfo.AddOrModifyMethod(hookMethodName, methodDef, hookMethodState);

                        // typeDef.Methods.Add(methodDef);
                    }
                }

                // 预注册添加和修改的方法
                PreRegisterMethods(result.AddedMethods, MemberModifyState.Added);
                PreRegisterMethods(result.ModifiedMethods, MemberModifyState.Modified);
            }

            // 处理方法引用
            foreach (var (typeName, result) in diffResults)
            {
                var typeDef = mainModule.GetType(typeName);

                if (!HookTypeInfoCache.TryGetValue(typeDef.FullName, out var hookTypeInfo))
                {
                    continue;
                }
                
                if (!ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(typeDef.FullName, out var originalType))
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
        private static void ClearAssembly(AssemblyDefinition assemblyDef, Dictionary<string, DiffResult> diffResults)
        {
            var mainModule = assemblyDef.MainModule;

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
        /// 处理方法定义和指令
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

            // 编译时创建的内部类的成员后续统一处理
            if (TypeInfoHelper.IsCompilerGeneratedType(methodRef.DeclaringType as TypeDefinition))
            {
                NestedTypeInfo.AddMethod(methodRef);
                return methodRef;
            }

            if (!CheckMethodIsScopeInModule(methodRef))
            {
                return methodRef;
            }

            // 处理新增/泛型方法调用
            if (HookTypeInfoCache.TryGetValue(methodRef.DeclaringType.FullName, out var hookTypeInfo))
            {
                var methodName = methodRef.IsGenericInstance ? methodRef.GetElementMethod().FullName : methodRef.FullName;
                if (hookTypeInfo.ModifiedMethods.TryGetValue(methodName, out var methodInfo) && methodInfo.MemberModifyState == MemberModifyState.Added)
                {
                    return module.ImportReference(methodInfo.WrapperMethodDef);
                }

                if (hookTypeInfo.TryGetMethod(methodName, out methodInfo)
                    && (methodInfo.MemberModifyState == MemberModifyState.Added || methodRef is GenericInstanceMethod))
                {
                    return methodRef;
                }
            }

            // 处理泛型方法实例
            if (methodRef is GenericInstanceMethod genericInstanceMethod)
            {
                var elementMethod = genericInstanceMethod.GetElementMethod();
                if (ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(elementMethod.DeclaringType.FullName, out var originalElementType))
                {
                    var originalElementMethod = originalElementType.GetMethodReference(module, elementMethod.FullName);
                    var newGenericInstanceMethod = new GenericInstanceMethod(originalElementMethod);
                    // 添加泛型参数
                    foreach (var typeRef in genericInstanceMethod.GenericArguments)
                    {
                        newGenericInstanceMethod.GenericArguments.Add(GetOriginalType(typeRef));
                    }

                    return module.ImportReference(newGenericInstanceMethod);
                }
            }

            if (ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(methodRef.DeclaringType.FullName, out var originalType))
            {
                var originalMethodRef = originalType.GetMethodReference(module, methodRef.FullName);
                return module.ImportReference(originalMethodRef);
            }
            
            if (methodRef.DeclaringType is GenericInstanceType)
            {
                methodRef.DeclaringType = GetOriginalType(methodRef.DeclaringType);
            }

            return methodRef;
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

            // 编译时创建的内部类的成员后续统一处理
            if (TypeInfoHelper.IsCompilerGeneratedType(fieldRef.DeclaringType as TypeDefinition))
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
        /// 检查类型是否定义在 Hook 程序集中（包括递归检查 TypeSpecification）
        /// </summary>
        private static bool CheckTypeIsScopeInModule(TypeReference typeRef)
        {
            if (typeRef == null)
            {
                return false;
            }
            
            var module = typeRef.Module;
            
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
                    if (CheckTypeIsScopeInModule(genericInstanceType.GetElementType()))
                    {
                        return true;
                    }
                    
                    // 检查所有泛型参数（如 Test 是否在 Hook 程序集）
                    foreach (var genericArgument in genericInstanceType.GenericArguments)
                    {
                        if (CheckTypeIsScopeInModule(genericArgument))
                        {
                            return true;
                        }
                    }
                    
                    return false;
                }
                
                // 其他 TypeSpecification：递归检查 ElementType
                return CheckTypeIsScopeInModule(typeSpec.ElementType);
            }
            
            return false;
        }
        
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

            if (!CheckTypeIsScopeInModule(typeRef))
            {
                return typeRef;
            }

            if (typeRef is TypeSpecification typeSpec)
            {
                // 泛型实例类型（需要处理多个泛型参数）
                if (typeSpec is GenericInstanceType genericInstanceType)
                {
                    return ProcessGenericInstanceType(genericInstanceType);
                }

                // 数组类型（需要保留维度信息）
                if (typeSpec is ArrayType arrayType)
                {
                    return ProcessArrayType(arrayType);
                }

                // 根据原类型重新构造相应的复合类型
                var elementType = GetOriginalType(typeSpec.ElementType);

                return typeSpec switch
                {
                    PointerType _ => new PointerType(elementType),
                    ByReferenceType _ => new ByReferenceType(elementType),
                    PinnedType _ => new PinnedType(elementType),
                    RequiredModifierType reqModType => new RequiredModifierType(GetOriginalType(reqModType.ModifierType), elementType),
                    OptionalModifierType optModType => new OptionalModifierType(GetOriginalType(optModType.ModifierType), elementType),
                    SentinelType _ => new SentinelType(elementType),
                    _ => typeSpec // 未知类型，保持原样
                };
            }
            
            // 尝试从原程序集中查找该类型
            if (ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(typeRef.FullName, out var originalType))
            {
                // 找到原类型，导入引用并返回
                return module.ImportReference(originalType);
            }

            return typeRef;
        }

        /// <summary>
        /// 处理泛型实例类型（如 List&lt;Test&gt;、Dictionary&lt;string, Test[]&gt;）
        /// 特殊性：除了 ElementType，还有多个 GenericArguments 需要处理
        /// </summary>
        private static TypeReference ProcessGenericInstanceType(GenericInstanceType genericInstanceType)
        {
            // 1. 递归处理泛型定义（如 List<> 本身可能也需要重定向）
            var elementType = genericInstanceType.GetElementType();
            var originalElementType = GetOriginalType(elementType);
            
            // 2. 递归处理每个泛型参数（如 Test、string、Test[] 等）
            var genericArguments = new TypeReference[genericInstanceType.GenericArguments.Count];
            for (int i = 0; i < genericInstanceType.GenericArguments.Count; i++)
            {
                genericArguments[i] = GetOriginalType(genericInstanceType.GenericArguments[i]);
            }

            // 3. 用处理后的元素类型和泛型参数重新构造泛型实例
            return originalElementType.MakeGenericInstanceType(genericArguments);
        }

        /// <summary>
        /// 处理数组类型（如 Test[]、Test[,]、Test[,,]）
        /// 特殊性：需要保留数组维度信息（一维、多维）
        /// </summary>
        private static TypeReference ProcessArrayType(ArrayType arrayType)
        {
            // 递归处理数组元素类型
            var elementType = GetOriginalType(arrayType.ElementType);

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
        private static bool CheckMethodIsScopeInModule(MethodReference methodRef)
        {
            if (methodRef == null)
            {
                return false;
            }

            // 1. 检查声明类型
            if (CheckTypeIsScopeInModule(methodRef.DeclaringType))
            {
                return true;
            }

            // 2. 检查返回类型
            if (CheckTypeIsScopeInModule(methodRef.ReturnType))
            {
                return true;
            }

            // 3. 检查参数类型
            if (methodRef.HasParameters)
            {
                foreach (var param in methodRef.Parameters)
                {
                    if (CheckTypeIsScopeInModule(param.ParameterType))
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
                    if (CheckTypeIsScopeInModule(arg))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion
    }
}
