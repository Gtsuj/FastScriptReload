#if ENABLE_MONO && (DEVELOPMENT_BUILD || UNITY_EDITOR)
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;

namespace FastScriptReload.Editor
{
    public static class MethodHelper
    {
#if ENABLE_MONO
        public static unsafe void DisableVisibilityChecks(MethodBase method)
        {
            if (IntPtr.Size == sizeof(long))
            {
                var ptr = (MonoMethod64*)method.MethodHandle.Value.ToPointer();
                ptr->monoMethodFlags |= MonoMethodFlags.skip_visibility;
            }
            else
            {
                var ptr = (MonoMethod32*)method.MethodHandle.Value.ToPointer();
                ptr->monoMethodFlags |= MonoMethodFlags.skip_visibility;
            }
        }

        public static unsafe bool IsMethodInlined(MethodBase method)
        {
            if (IntPtr.Size == sizeof(long))
            {
                var ptr = (MonoMethod64*)method.MethodHandle.Value.ToPointer();
                return (ptr->monoMethodFlags & MonoMethodFlags.inline_info) == MonoMethodFlags.inline_info;
            }
            else
            {
                var ptr = (MonoMethod32*)method.MethodHandle.Value.ToPointer();
                return (ptr->monoMethodFlags & MonoMethodFlags.inline_info) == MonoMethodFlags.inline_info;
            }
        }
#else
        public static void DisableVisibilityChecks(MethodBase method) { }
        public static bool IsMethodInlined(MethodBase method) {
             return false; 
        }
#endif

        /// <summary>
        /// 获取方法的完整签名名称
        /// </summary>
        public static string FullName(this MethodBase member)
        {
            Type returnType = AccessTools.GetReturnedType(member);
            
            StringBuilder builder = new StringBuilder();
            builder.Append(returnType.FullName).Append(" ").
                Append(member.DeclaringType == null ? member.Name : member.DeclaringType.FullName + "::" + member.Name);
            builder.Append("(");
            if (member.GetParameters().Length > 0)
            {
                var parameters = member.GetParameters();
                for (int index = 0; index < parameters.Length; ++index)
                {
                    var parameterDefinition = parameters[index];
                    if (index > 0)
                        builder.Append(",");
                    builder.Append(parameterDefinition.ParameterType.FullName);
                }
            }
            builder.Append(")");
            return builder.ToString();
        }

        //see _MonoMethod struct in class-internals.h
        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Auto, Size = 8 + sizeof(long) * 3 + 4)]
        internal unsafe struct MonoMethod64
        {
            [FieldOffset(0)] public MethodAttributes flags;
            [FieldOffset(2)] public MethodImplAttributes iflags;
            [FieldOffset(4)] public uint token;
            [FieldOffset(8)] public void* klass;
            [FieldOffset(8 + sizeof(long))] public void* signature;

            [FieldOffset(8 + sizeof(long) * 2)] public char* name;

            /* this is used by the inlining algorithm */
            [FieldOffset(8 + sizeof(long) * 3)] public MonoMethodFlags monoMethodFlags;

            [FieldOffset(8 + sizeof(long) * 3 + 2)]
            public short slot;
        }

        [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Auto, Size = 8 + sizeof(int) * 3 + 4)]
        internal unsafe struct MonoMethod32
        {
            [FieldOffset(0)] public MethodAttributes flags;
            [FieldOffset(2)] public MethodImplAttributes iflags;
            [FieldOffset(4)] public uint token;
            [FieldOffset(8)] public void* klass;
            [FieldOffset(8 + sizeof(int))] public void* signature;

            [FieldOffset(8 + sizeof(int) * 2)] public char* name;

            /* this is used by the inlining algorithm */
            [FieldOffset(8 + sizeof(int) * 3)] public MonoMethodFlags monoMethodFlags;
            [FieldOffset(8 + sizeof(int) * 3 + 2)] public short slot;
        }

        //Corresponds to the bitflags of the _MonoMethod struct
        [Flags]
        internal enum MonoMethodFlags : ushort
        {
            inline_info = 1 << 0, //:1
            inline_failure = 1 << 1, //:1
            wrapper_type = 1 << 2, //:5
            string_ctor = 1 << 7, //:1
            save_lmf = 1 << 8, //:1
            dynamic = 1 << 9, //:1       /* created & destroyed during runtime */
            sre_method = 1 << 10, //:1       /* created at runtime using Reflection.Emit */
            is_generic = 1 << 11, //:1       /* whenever this is a generic method definition */
            is_inflated = 1 << 12, //:1       /* whether we're a MonoMethodInflated */
            skip_visibility = 1 << 13, //:1       /* whenever to skip JIT visibility checks */
            verification_success = 1 << 14, //:1       /* whether this method has been verified successfully.*/ 
        }
    }
}
#endif