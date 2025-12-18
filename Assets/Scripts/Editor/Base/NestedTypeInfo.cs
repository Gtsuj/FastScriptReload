using System.Collections.Generic;
using Mono.Cecil;

namespace FastScriptReload.Editor
{
    public class NestedTypeInfo
    {
        public TypeDefinition TypeDefinition { get; }
        
        public HashSet<MethodReference> Methods { get; } = new ();
        
        public HashSet<FieldReference> Fields { get; } = new ();

        public NestedTypeInfo(TypeDefinition typeDef)
        {
            TypeDefinition = typeDef;
        }

        public static Dictionary<string, NestedTypeInfo> NestedTypeInfos = new();

        public static void AddMethod(MethodReference methodRef)
        {
            if (!(methodRef.DeclaringType is TypeDefinition typeDef))
            {
                return;
            }

            var fullName = methodRef.DeclaringType.FullName;
            if (!NestedTypeInfos.TryGetValue(fullName, out var nestedTypeInfo))
            {
                nestedTypeInfo = new NestedTypeInfo(typeDef);
                NestedTypeInfos[fullName] = nestedTypeInfo;
            }

            nestedTypeInfo.Methods.Add(methodRef);
        }

        public static void AddField(FieldReference fieldRef)
        {
            if (!(fieldRef.DeclaringType is TypeDefinition typeDef))
            {
                return;
            }

            var fullName = fieldRef.DeclaringType.FullName;
            if (!NestedTypeInfos.TryGetValue(fullName, out var nestedTypeInfo))
            {
                nestedTypeInfo = new NestedTypeInfo(typeDef);
                NestedTypeInfos[fullName] = nestedTypeInfo;
            }

            nestedTypeInfo.Fields.Add(fieldRef);
        }

        public static void Clear()
        {
            NestedTypeInfos.Clear();
        }
    }
}