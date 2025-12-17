using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnityEditor;

namespace FastScriptReload.Editor
{
    public static class RoslynHelper
    {
        public static readonly SymbolDisplayFormat TYPE_FORMAT =
            new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,parameterOptions: SymbolDisplayParameterOptions.IncludeType);

        public static readonly SymbolDisplayFormat METHOD_FORMAT =
            new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                memberOptions: SymbolDisplayMemberOptions.IncludeParameters,
                parameterOptions: SymbolDisplayParameterOptions.IncludeType);
        
        /// <summary>
        /// 解析选项
        /// </summary>
        public static readonly CSharpParseOptions PARSE_OPTIONS;

        static RoslynHelper()
        {
            PARSE_OPTIONS = new CSharpParseOptions(
                preprocessorSymbols: EditorUserBuildSettings.activeScriptCompilationDefines,
                languageVersion: LanguageVersion.Latest
            );
        }

        public static SyntaxTree GetSyntaxTree(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return null;
            }

            if (!File.Exists(filePath))
            {
                return null;
            }

            var content = File.ReadAllText(filePath);
            if (string.IsNullOrEmpty(content))
            {
                return null;
            }

            var syntaxTree = CSharpSyntaxTree.ParseText(content, PARSE_OPTIONS, path: filePath, encoding: Encoding.UTF8);

            return syntaxTree;
        }

        /// <summary>
        /// 获取类型全名（支持嵌套类型）
        /// </summary>
        public static string FullName(this TypeDeclarationSyntax typeDecl)
        {
            if (typeDecl == null)
                return "";

            var parts = new List<string>();
            var current = typeDecl;

            while (current != null)
            {
                parts.Insert(0, current.Identifier.ValueText);
                current = current.Parent as TypeDeclarationSyntax;
            }

            var namespaceDecl = typeDecl.Parent;
            while (namespaceDecl != null && !(namespaceDecl is BaseNamespaceDeclarationSyntax))
            {
                namespaceDecl = namespaceDecl.Parent;
            }

            var namespaceName = (namespaceDecl as BaseNamespaceDeclarationSyntax)?.Name.ToString() ?? string.Empty;
            
            if (string.IsNullOrEmpty(namespaceName))
            {
                return string.Join(".", parts);
            }
            
            var builder = new StringBuilder(namespaceName.Length + parts.Count * 20);
            builder.Append(namespaceName);
            builder.Append('.');
            builder.Append(string.Join(".", parts));
            return builder.ToString();
        }

        /// <summary>
        /// 获取方法全名（包含参数类型）
        /// </summary>
        public static string FullName(this MethodDeclarationSyntax method)
        {
            var methodName = method.Identifier.ValueText;
            var returnTypeStr = method.ReturnType.ToFullString();
            var paramList = method.ParameterList.Parameters;
            
            var builder = new StringBuilder(returnTypeStr.Length + methodName.Length + paramList.Count * 20 + 10);
            builder.Append(returnTypeStr);
            builder.Append(' ');
            builder.Append(methodName);
            builder.Append('(');
            
            for (int i = 0; i < paramList.Count; i++)
            {
                if (i > 0)
                    builder.Append(',');
                builder.Append(paramList[i].Type?.ToString() ?? "");
            }
            
            builder.Append(')');
            return builder.ToString();
        }
        
        /// <summary>
        /// 获取方法全名（包含参数类型）
        /// </summary>
        public static string FullName(this MethodDeclarationSyntax method, SemanticModel semanticModel)
        {
            var methodSymbol = semanticModel.GetDeclaredSymbol(method);
            if (methodSymbol == null)
            {
                return method.FullName();
            }

            return methodSymbol.FullName();
        }

        public static string FullName(this IMethodSymbol methodSymbol)
        {
            var typeName = methodSymbol.ContainingType.ToDisplayString(TYPE_FORMAT);
            var methodName = methodSymbol.Name;

            // 处理引用返回类型
            var typeToAnalyze = methodSymbol.ReturnType;
            var returnType = typeToAnalyze.ToDisplayString(TYPE_FORMAT);

            // 估算容量：返回类型 + 类型名 + 方法名 + 参数（每个约20字符）+ 固定字符
            var paramCount = methodSymbol.Parameters.Length;
            var estimatedCapacity = returnType.Length + typeName.Length + methodName.Length + paramCount * 20 + 20;
            var builder = new StringBuilder(estimatedCapacity);

            // 组合返回类型
            builder.Append(returnType);
            builder.Append(' ');
            builder.Append(typeName);
            builder.Append("::");
            builder.Append(methodName);
            builder.Append('(');

            for (int i = 0; i < paramCount; i++)
            {
                if (i > 0)
                    builder.Append(',');
                
                var param = methodSymbol.Parameters[i];
                if (param.Type is ITypeParameterSymbol paramTypeParam)
                {
                    builder.Append(paramTypeParam.Name);
                }
                else
                {
                    var paramTypeName = param.Type.ToDisplayString(TYPE_FORMAT);
                    paramTypeName = paramTypeName.Replace(param.Type.Name, param.Type.MetadataName);
                    builder.Append(paramTypeName);
                }
            }

            builder.Append(')');
            return builder.ToString();
        }
        
        /// <summary>
        /// 获取方法的完整签名名称
        /// </summary>
        public static string FullName(this MethodBase member)
        {
            Type returnType = AccessTools.GetReturnedType(member);

            StringBuilder builder = new StringBuilder();
            builder.Append(returnType.FullName).Append(" ").Append(member.DeclaringType == null
                ? member.Name
                : member.DeclaringType.FullName + "::" + member.Name);
            builder.Append("(");
            if (member.GetParameters().Length > 0)
            {
                var parameters = member.GetParameters();
                for (int index = 0; index < parameters.Length; ++index)
                {
                    var parameterDefinition = parameters[index];
                    if (index > 0)
                        builder.Append(",");
                    builder.Append(parameterDefinition.ParameterType.FullName ?? parameterDefinition.ParameterType.Name);
                }
            }

            builder.Append(")");
            return builder.ToString();
        }        
    }
}