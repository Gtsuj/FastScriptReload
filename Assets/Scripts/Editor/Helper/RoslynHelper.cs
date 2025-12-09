using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

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

            var syntaxTree = CSharpSyntaxTree.ParseText(content, PARSE_OPTIONS, path: filePath);

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
            var typeName = string.Join(".", parts);

            return string.IsNullOrEmpty(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
        }

        /// <summary>
        /// 获取方法全名（包含参数类型）
        /// </summary>
        public static string FullName(this MethodDeclarationSyntax method)
        {
            var methodName = method.Identifier.ValueText;
            var parameters = string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""));
            return $"{method.ReturnType.ToFullString()} {methodName}({parameters})";
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

            var typeName = methodSymbol.ContainingType.ToDisplayString(TYPE_FORMAT);
            var methodName = methodSymbol.ToDisplayString(METHOD_FORMAT);

            // 处理引用返回类型（ref T）
            string returnTypePrefix = "";
            TypeSyntax typeToAnalyze = method.ReturnType;
            
            if (method.ReturnType is RefTypeSyntax refType)
            {
                returnTypePrefix = "ref ";
                typeToAnalyze = refType.Type;
            }

            // 获取返回类型的完整名称
            var typeInfo = semanticModel.GetTypeInfo(typeToAnalyze);
            string returnType = typeInfo.Type?.ToDisplayString(TYPE_FORMAT);
            
            // 组合返回类型（包含 ref 前缀）
            var fullReturnType = returnTypePrefix + returnType;
            
            return $"{fullReturnType} {typeName}::{methodName}";
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
                    builder.Append(parameterDefinition.ParameterType.FullName);
                }
            }

            builder.Append(")");
            return builder.ToString();
        }        
    }
}