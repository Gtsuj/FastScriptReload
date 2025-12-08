using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UnityEditor;

namespace FastScriptReload.Editor
{
    public static class RoslynHelper
    {
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
            return $"{methodName}({parameters})";
        }
    }
}