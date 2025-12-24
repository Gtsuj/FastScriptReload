using System;
using System.Linq;
using System.Reflection;
using FastScriptReload.Runtime;
using Mono.Cecil;
using UnityEngine;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;

namespace FastScriptReload.Editor
{
    public static class FieldResolverHelper
    {
        /// <summary>
        /// 获取 FieldResolver<TOwner>.GetHolder<TField>; 方法引用
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
        /// 获取 FieldResolver<TOwner>.Store<TField> 方法引用
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
        /// 获取 FieldHolder<TField>.F 字段引用
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
        /// 获取 FieldHolder<TField>.GetRef 方法引用
        /// </summary>
        public static MethodReference GetFieldHolderGetRefMethodReference(TypeReference fieldType)
        {
            var module = fieldType.Module;
            var fieldHolderTypeRef = module.ImportReference(typeof(FieldHolder<>));
            var fieldHolderGenericType = new GenericInstanceType(fieldHolderTypeRef)
            {
                GenericArguments = { fieldType }
            };

            var intPtrType = module.ImportReference(typeof(System.IntPtr));

            var methodRef = new MethodReference("GetRef", intPtrType, fieldHolderGenericType)
            {
                HasThis = true,
                ExplicitThis = false,
                CallingConvention = MethodCallingConvention.Default
            };

            return methodRef;
        }

        #region 运行时反射调用接口

        /// <summary>
        /// 通过反射注册字段初始化器 - 使用固定初始值
        /// </summary>
        /// <param name="ownerType">字段所属的类型</param>
        /// <param name="fieldName">字段名称</param>
        /// <param name="fieldType">字段类型</param>
        /// <param name="initialValue">初始值</param>
        public static void RegisterFieldInitializer(Type ownerType, string fieldName, Type fieldType, object initialValue)
        {
            if (ownerType == null)
            {
                throw new ArgumentNullException(nameof(ownerType));
            }

            if (string.IsNullOrEmpty(fieldName))
            {
                throw new ArgumentException("字段名称不能为空", nameof(fieldName));
            }

            if (fieldType == null)
            {
                throw new ArgumentNullException(nameof(fieldType));
            }

            // 构造 FieldResolver<TOwner> 类型
            var fieldResolverType = typeof(FieldResolver<>).MakeGenericType(ownerType);

            // 获取 RegisterFieldInitializer<TField> 方法（使用固定值的重载）
            var methodInfo = fieldResolverType.GetMethod("RegisterFieldInitializer");

            // 创建泛型方法实例
            var genericMethod = methodInfo.MakeGenericMethod(fieldType);

            // 调用方法
            genericMethod.Invoke(null, new object[] { fieldName, initialValue });
        }

        #endregion
    }
}