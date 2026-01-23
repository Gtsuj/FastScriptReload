using Mono.Cecil;

namespace CompileServer.Models
{
    /// <summary>
    /// 泛型方法调用信息
    /// </summary>
    public readonly struct GenericMethodCallInfo : IEquatable<GenericMethodCallInfo>
    {
        public readonly string TypeName;

        public readonly string MethodName;

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
