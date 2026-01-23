using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CompileServer.Rewriters
{
    /// <summary>
    /// 扩展方法类重命名器 - 添加 __Patch__ 后缀
    /// </summary>
    public class ExtensionClassRename : CSharpSyntaxRewriter
    {
        public Dictionary<string, string> RenamedClasses { get; } = new(); // patchName → originalName

        public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            if (node.Modifiers.Any(SyntaxKind.StaticKeyword) && ContainsExtensionMethods(node))
            {
                var originalName = node.Identifier.Text;
                var patchName = $"{originalName}__Patch__";
                RenamedClasses[patchName] = originalName;

                return node.WithIdentifier(SyntaxFactory.Identifier(patchName));
            }
            return base.VisitClassDeclaration(node);
        }

        private static bool ContainsExtensionMethods(ClassDeclarationSyntax classDecl)
        {
            return classDecl.Members.OfType<MethodDeclarationSyntax>()
                .Any(m => m.Modifiers.Any(SyntaxKind.StaticKeyword) &&
                          m.ParameterList.Parameters.Count > 0 &&
                          m.ParameterList.Parameters[0].Modifiers.Any(SyntaxKind.ThisKeyword));
        }
    }
}
