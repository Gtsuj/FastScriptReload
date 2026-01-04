using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Mono.Cecil;
using Newtonsoft.Json;
using UnityEngine.Serialization;
using Assembly = UnityEditor.Compilation.Assembly;

namespace FastScriptReload.Editor
{
    public enum MemberModifyState
    {
        Added,
        Modified,
    }

    public interface IHookMemberInfo
    {
        public string AssemblyPath { get; set; }
        
        public string TypeFullName { get; set; }
        
        public string MemberFullName { get; set; }
    }

    [Serializable]
    public class HookMethodInfo : IHookMemberInfo
    {
        public string AssemblyPath { get; set; }
        
        public string TypeFullName { get; set; }

        public string MemberFullName { get; set; }

        public MemberModifyState MemberModifyState;

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
        public MethodDefinition WrapperMethodDef { get; set; }

        /// <summary>
        /// 历史Hook的方法列表（当新增方法被修改时，需要将这些方法重新Hook到新版本）
        /// </summary>
        [JsonIgnore]
        public List<MethodBase> HistoricalHookedMethods { get; } = new();
        
        public List<string> HistoricalHookedAssemblyPaths { get; } = new();

        public HookMethodInfo(string hookMethodName, MethodDefinition wrapperMethodDef, MemberModifyState memberModifyState)
        {
            MemberFullName = hookMethodName;
            WrapperMethodDef = wrapperMethodDef;
            TypeFullName = wrapperMethodDef?.DeclaringType.FullName;
            WrapperMethodName = wrapperMethodDef?.FullName;
            HasGenericParameters = wrapperMethodDef?.HasGenericParameters ?? false;
            MemberModifyState = memberModifyState;
        }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            AssemblyDefinition assemblyDefinition = AssemblyDefinition.ReadAssembly(AssemblyPath);
            var typeDef = assemblyDefinition.MainModule.GetType(TypeFullName);
            WrapperMethodDef = typeDef.Methods.FirstOrDefault(m => m.FullName == WrapperMethodName);

            if (HistoricalHookedAssemblyPaths.Count > 1)
            {
                HistoricalHookedAssemblyPaths.RemoveAt(HistoricalHookedAssemblyPaths.Count - 1);
                for (int i = 0; i < HistoricalHookedAssemblyPaths.Count; i++)
                {
                    var hookedAssemblyPath = HistoricalHookedAssemblyPaths[i];
                    var assembly = System.Reflection.Assembly.LoadFrom(hookedAssemblyPath);
                    var type = assembly.GetType(TypeFullName);
                    var method = type.GetMethodByMethodDefName(WrapperMethodName);
                    HistoricalHookedMethods.Add(method);
                }
            }
        }

        public void AddHistoricalHookedMethod(MethodBase methodBase)
        {
            HistoricalHookedMethods.Add(methodBase);
            HistoricalHookedAssemblyPaths.Add(AssemblyPath);
        }
    }
    
    public class HookFieldInfo : IHookMemberInfo
    {
        public string AssemblyPath { get; set; }
        
        public string TypeFullName { get; set; }
        
        public string MemberFullName { get; set; }

        public HookFieldInfo(FieldDefinition fieldDef)
        {
            TypeFullName = fieldDef?.DeclaringType.FullName;
            MemberFullName = fieldDef?.FullName;
        }
    }
}