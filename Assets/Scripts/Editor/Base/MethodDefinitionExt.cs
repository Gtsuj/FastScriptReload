using System.Reflection;
using System.Text;
using HarmonyLib;
using Mono.Cecil;

namespace FastScriptReload.Editor
{
    public static class MethodDefinitionExt
    {
        /// <summary>
        /// 与MethodInfo.FullDescription一致
        /// </summary>
        /// <param name="member"></param>
        /// <returns></returns>
        public static string FullDescription(this MethodDefinition member)
        {
            if (member == null)
                return "null";
            var returnedType = member.ReturnType;
            StringBuilder stringBuilder = new StringBuilder();
            if (member.IsStatic)
                stringBuilder.Append("static ");
            if (member.IsAbstract)
                stringBuilder.Append("abstract ");
            if (member.IsVirtual)
                stringBuilder.Append("virtual ");
            stringBuilder.Append(returnedType.FullName + " ");
            if (member.DeclaringType != null)
                stringBuilder.Append(member.DeclaringType.FullName + "::");
            string str = member.Parameters.Join(p => p.ParameterType.FullName + " " + p.Name);
            stringBuilder.Append(member.Name + "(" + str + ")");
            return stringBuilder.ToString();
        }
    }
}