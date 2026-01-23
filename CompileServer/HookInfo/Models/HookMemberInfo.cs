using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Mono.Cecil;
using Newtonsoft.Json;

namespace HookInfo.Models;

public enum MemberModifyState
{
    Added,
    Modified,
}

public abstract class HookMemberInfo
{
    public string AssemblyPath => HistoricalHookedAssemblyPaths.Last();
    
    public List<string> HistoricalHookedAssemblyPaths { get; } = new();

    public string TypeName;

    public string MemberFullName;
    
    public MemberModifyState MemberModifyState;

    public HookMemberInfo() { }
    
    public HookMemberInfo(string typeName, string fieldName, MemberModifyState state)
    {
        TypeName = typeName;
        MemberFullName = fieldName;
        MemberModifyState = state;
    }
}

[Serializable]
public class HookMethodInfo : HookMemberInfo
{
    /// <summary>
    /// 封装过后的静态方法名称
    /// </summary>
    public string WrapperMethodName;

    /// <summary>
    /// 是否为泛型方法
    /// </summary>
    public bool HasGenericParameters;

    /// <summary>
    /// 当前方法信息
    /// </summary>
    [JsonIgnore]
    public MethodDefinition WrapperMethodDef;

    /// <summary>
    /// 历史Hook的方法列表（当新增方法被修改时，需要将这些方法重新Hook到新版本）
    /// </summary>
    [JsonIgnore]
    public List<MethodBase> HistoricalHookedMethods { get; } = new();

    public HookMethodInfo() { }
    
    public HookMethodInfo(string hookMethodName, MethodDefinition wrapperMethodDef, MemberModifyState state) : 
        base(wrapperMethodDef.DeclaringType.FullName, hookMethodName, state)
    {
        WrapperMethodDef = wrapperMethodDef;
        WrapperMethodName = wrapperMethodDef.FullName;
        HasGenericParameters = wrapperMethodDef.HasGenericParameters;
    }

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        if (string.IsNullOrEmpty(AssemblyPath))
        {
            return;
        }

        if (string.IsNullOrEmpty(TypeName) || string.IsNullOrEmpty(WrapperMethodName))
        {
            return;
        }

        for (int i = 0; i < HistoricalHookedAssemblyPaths.Count; i++)
        {
            var hookedAssemblyPath = HistoricalHookedAssemblyPaths[i];
            var assembly = Assembly.LoadFrom(hookedAssemblyPath);
            var type = assembly.GetType(TypeName);
            var method = type?.GetMethodByMethodDefName(WrapperMethodName);
            if (method != null)
            {
                HistoricalHookedMethods.Add(method);
            }
        }
    }
}

public class HookFieldInfo : HookMemberInfo
{
    public HookFieldInfo(string typeName, string fieldName, MemberModifyState state = MemberModifyState.Added) : base(typeName, fieldName, state)
    {
        
    }
}