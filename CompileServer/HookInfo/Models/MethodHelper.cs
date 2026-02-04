using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace HookInfo.Models;

public static class MethodHelper
{
    public static readonly BindingFlags ALL = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.GetField | BindingFlags.SetField | BindingFlags.GetProperty | BindingFlags.SetProperty;
    
    public static readonly BindingFlags ALL_DECLARED = ALL | BindingFlags.DeclaredOnly;
    
    /// <summary>
    /// 根据MethodDefinition的全称获取方法
    /// </summary>
    /// <returns>方法</returns>
    public static MethodBase GetMethodByMethodDefName(this Type type, string methodName)
    {
        var splits = methodName.Split(" ");
        var returnTypeName = splits[0];
        var methodNameWithoutReturn = splits[1];

        var methods = type.GetMethods(ALL_DECLARED);
        foreach (var methodInfo in methods)
        {
            var name = methodInfo.ResolveFullName();
            if (name.Contains(methodNameWithoutReturn))
            {
                return methodInfo;
            }
        }

        var constructors = type.GetConstructors(ALL_DECLARED);
        foreach (var constructorInfo in constructors)
        {
            var name = constructorInfo.ResolveFullName();
            if (name.Contains(methodNameWithoutReturn))
            {
                return constructorInfo;
            }
        }

        return null;
    }
    
    public static string ResolveFullName(this MethodBase method)
    {
        if (method == null)
        {
            return string.Empty;
        }

        StringBuilder sb = new();
        sb.Append(method.ReflectedType.FullName);
        sb.Append("::");
        sb.Append(method.Name.Replace('+', '/'));
        sb.Append("(");
        var parameters = method.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i > 0) sb.Append(",");

            var paramType = parameters[i].ParameterType;
            if (paramType.IsGenericType)
            {
                sb.Append(paramType.ToString().Replace('[', '<').Replace(']', '>'));
            }
            else
            {
                sb.Append(parameters[i].ParameterType.ToString());
            }
        }
        sb.Append(")");

        return sb.ToString();
    }
}