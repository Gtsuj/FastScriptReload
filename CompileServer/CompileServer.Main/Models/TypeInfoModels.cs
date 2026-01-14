using Microsoft.CodeAnalysis;
using Mono.Cecil;

namespace CompileServer.Models
{
    /// <summary>
    /// 差异分析结果
    /// </summary>
    public class DiffResult
    {
        /// <summary>
        /// 修改的方法
        /// </summary>
        public Dictionary<string, MethodDiffInfo> ModifiedMethods { get; } = new();

        /// <summary>
        /// 新增的字段
        /// </summary>
        public Dictionary<string, FieldDiffInfo> AddedFields { get; } = new();
    }

    /// <summary>
    /// 方法差异信息
    /// </summary>
    public class MethodDiffInfo
    {
        /// <summary>
        /// 方法全名（包含返回类型、声明类型、方法名、参数类型）
        /// </summary>
        public string FullName { get; set; } = string.Empty;

        /// <summary>
        /// 修改状态
        /// </summary>
        public HookInfo.Models.MemberModifyState ModifyState { get; set; }

        /// <summary>
        /// 方法定义
        /// </summary>
        public MethodDefinition MethodDefinition { get; set; } = null!;
    }

    /// <summary>
    /// 字段差异信息
    /// </summary>
    public class FieldDiffInfo
    {
        /// <summary>
        /// 声明类型全名
        /// </summary>
        public string DeclaringTypeFullName { get; set; } = string.Empty;

        /// <summary>
        /// 字段全名
        /// </summary>
        public string FullName { get; set; } = string.Empty;
    }

    /// <summary>
    /// 文件快照
    /// </summary>
    public class FileSnapshot
    {
        public string FilePath { get; set; }
        public SyntaxTree SyntaxTree { get; set; }
        public DateTime SnapshotTime { get; set; }

        public FileSnapshot(string path)
        {
            FilePath = path;
            SnapshotTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 泛型方法调用信息
    /// </summary>
    public readonly struct GenericMethodCallInfo : IEquatable<GenericMethodCallInfo>
    {
        public readonly string TypeName;
        public readonly string MethodName;
        // public readonly MethodDefinition MethodDef;

        public GenericMethodCallInfo(MethodDefinition methodDef)
        {
            // MethodDef = methodDef;
            TypeName = methodDef.DeclaringType.IsGenericInstance 
                ? methodDef.DeclaringType.GetElementType().FullName 
                : methodDef.DeclaringType.FullName;
            MethodName = methodDef.Name;
        }

        public bool Equals(GenericMethodCallInfo other)
        {
            return TypeName == other.TypeName && MethodName == other.MethodName;
        }

        public override bool Equals(object obj)
        {
            return obj is GenericMethodCallInfo other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((TypeName?.GetHashCode() ?? 0) * 397) ^ (MethodName?.GetHashCode() ?? 0);
            }
        }
    }
}
