using System;
using HookInfo.Runtime;
using Mono.Cecil;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;

namespace CompileServer.Helpers
{
    /// <summary>
    /// FieldResolver 辅助方法
    /// 用于生成 FieldResolver 和 FieldHolder 的方法/字段引用
    /// </summary>
    public static class FieldResolverHelper
    {
        /// <summary>
        /// 获取 FieldResolver&lt;TOwner&gt;.GetHolder&lt;TField&gt; 方法引用
        /// </summary>
        public static MethodReference GetFieldResolverGetHolderMethodReference(TypeReference ownerType, TypeReference fieldType)
        {
            var module = ownerType.Module;
            var fieldResolverTypeRef = module.ImportReference(typeof(FieldResolver<>));
            var fieldResolverGenericType = new GenericInstanceType(fieldResolverTypeRef)
            {
                GenericArguments = { ownerType }
            };

            var fieldHolderTypeRef = module.ImportReference(typeof(FieldHolder<>));

            var methodRef = new MethodReference("GetHolder", fieldHolderTypeRef, fieldResolverGenericType)
            {
                HasThis = false,
                ExplicitThis = false,
                CallingConvention = MethodCallingConvention.Default
            };

            methodRef.Parameters.Add(new ParameterDefinition("instance", ParameterAttributes.None, module.TypeSystem.Object));
            methodRef.Parameters.Add(new ParameterDefinition("fieldName", ParameterAttributes.None, module.TypeSystem.String));

            var genericParam = new GenericParameter("TField", methodRef);
            methodRef.GenericParameters.Add(genericParam);

            var fieldHolderGenericType = new GenericInstanceType(fieldHolderTypeRef)
            {
                GenericArguments = { genericParam }
            };

            methodRef.ReturnType = fieldHolderGenericType;

            var genericInstanceMethod = new GenericInstanceMethod(methodRef)
            {
                GenericArguments = { fieldType }
            };

            return genericInstanceMethod;
        }

        /// <summary>
        /// 获取 FieldResolver&lt;TOwner&gt;.Store&lt;TField&gt; 方法引用
        /// 实例字段：Store(object instance, TField value, string fieldName)
        /// 静态字段：Store(TField value, string fieldName)
        /// </summary>
        public static MethodReference GetFieldResolverStoreMethodReference(TypeReference ownerType, TypeReference fieldType, bool isStatic)
        {
            var module = ownerType.Module;
            var fieldResolverTypeRef = module.ImportReference(typeof(FieldResolver<>));
            var fieldResolverGenericType = new GenericInstanceType(fieldResolverTypeRef)
            {
                GenericArguments = { ownerType }
            };

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

            var genericInstanceMethod = new GenericInstanceMethod(methodRef)
            {
                GenericArguments = { fieldType }
            };

            return genericInstanceMethod;
        }

        /// <summary>
        /// 获取 FieldHolder&lt;TField&gt;.F 字段引用
        /// </summary>
        public static FieldReference GetFieldHolderFReference(TypeReference fieldType)
        {
            var module = fieldType.Module;

            var fieldHolderTypeRef = module.ImportReference(typeof(FieldHolder<>));
            var fieldHolderGenericType = new GenericInstanceType(fieldHolderTypeRef)
            {
                GenericArguments = { fieldType }
            };

            var fFieldRef = new FieldReference("F", fieldHolderTypeRef.GenericParameters[0], fieldHolderGenericType);

            return fFieldRef;
        }

        /// <summary>
        /// 获取 FieldHolder&lt;TField&gt;.GetRef 方法引用
        /// </summary>
        public static MethodReference GetFieldHolderGetRefMethodReference(TypeReference fieldType)
        {
            var module = fieldType.Module;
            var fieldHolderTypeRef = module.ImportReference(typeof(FieldHolder<>));
            var fieldHolderGenericType = new GenericInstanceType(fieldHolderTypeRef)
            {
                GenericArguments = { fieldType }
            };

            var intPtrType = module.ImportReference(typeof(IntPtr));

            var methodRef = new MethodReference("GetRef", intPtrType, fieldHolderGenericType)
            {
                HasThis = true,
                ExplicitThis = false,
                CallingConvention = MethodCallingConvention.Default
            };

            return methodRef;
        }
    }
}
