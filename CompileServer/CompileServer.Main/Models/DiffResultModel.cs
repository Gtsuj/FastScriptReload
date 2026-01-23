using CompileServer.Helper;
using HookInfo.Models;
using Mono.Cecil;

namespace CompileServer.Models
{
    /// <summary>
    /// 差异分析结果
    /// </summary>
    public class DiffResult
    {
        /// <summary>
        /// 类型所属程序集名称
        /// </summary>
        public string AssemblyName { get; init; }
        
        /// <summary>
        /// 生成的patch程序集
        /// </summary>
        public AssemblyDefinition AssemblyDef { get; init; }
        
        /// <summary>
        /// 修改的方法
        /// </summary>
        public Dictionary<string, MethodDiffInfo> ModifiedMethods { get; } = new();

        /// <summary>
        /// 新增的字段
        /// </summary>
        public Dictionary<string, FieldDiffInfo> ModifiedFields { get; } = new();
        
        public void AddModifiedMethod(MethodDefinition method, MemberModifyState state)
        {
            if (!ModifiedMethods.ContainsKey(method.FullName))
            {
                ModifiedMethods[method.FullName] = new MethodDiffInfo(method.DeclaringType.FullName, method.FullName, state)
                {
                    HasGenericParameters = method.HasGenericParameters
                };
                
                TypeInfoHelper.UpdateMethodCallGraph(method);
            }
        }

        public void AddModifiedMethod(string typeName, string methodName)
        {
            if (!ModifiedMethods.ContainsKey(methodName))
            {
                ModifiedMethods[methodName] = new MethodDiffInfo(typeName, methodName, MemberModifyState.Modified)
                {
                    IsMethodCaller = true
                };
            }
        }
        
        public void AddModifiedField(string typeName, string fieldName)
        {
            if (!ModifiedFields.ContainsKey(fieldName))
            {
                ModifiedFields[fieldName] = new FieldDiffInfo(typeName, fieldName, MemberModifyState.Added);
            }
        }
    }

    public abstract class MemberDiffInfo
    {
        /// <summary>
        /// 所属类型名
        /// </summary>
        public readonly string TypeName;
        
        /// <summary>
        /// 成员名
        /// </summary>
        public readonly string MemberName;
        
        /// <summary>
        /// 成员修改状态
        /// </summary>
        public readonly MemberModifyState ModifyState;

        protected MemberDiffInfo(string typeName, string memberName, MemberModifyState modifyState)
        {
            TypeName = typeName;
            MemberName = memberName;
            ModifyState = modifyState;
        }
    }
    
    /// <summary>
    /// 方法差异信息
    /// </summary>
    public class MethodDiffInfo : MemberDiffInfo
    {
        public bool HasGenericParameters { get; init; }
        
        public bool IsMethodCaller { get; init; }

        public MethodDiffInfo(string typeName, string memberName, MemberModifyState modifyState)
            : base(typeName, memberName, modifyState)
        {

        }
    }

    /// <summary>
    /// 字段差异信息
    /// </summary>
    public class FieldDiffInfo : MemberDiffInfo
    {
        public FieldDiffInfo(string typeName, string memberName, MemberModifyState modifyState) 
            : base(typeName, memberName, modifyState)
        {
            
        }
    }
}