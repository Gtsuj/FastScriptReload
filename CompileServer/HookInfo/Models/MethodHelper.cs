using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
        var methodNameWithoutReturn = splits[1].Replace("::", ".").Replace('<','[').Replace('>',']');

        var methods = type.GetMethods(ALL_DECLARED);
        foreach (var methodInfo in methods)
        {
            var name = methodInfo.ResolveFullName().Replace('+', '/');
            if (name.Contains(methodNameWithoutReturn))
            {
                return methodInfo;
            }
        }

        var constructors = type.GetConstructors(ALL_DECLARED);
        foreach (var constructorInfo in constructors)
        {
            var name = constructorInfo.ResolveFullName().Replace('+', '/');
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
            return string.Empty;
        return method.ReflectedType.FullName + "." + method.Name + "(" + string.Join(",", method.GetParameters().Select(o => string.Format("{0}", o.ParameterType)).ToArray()) + ")";
    }    
}