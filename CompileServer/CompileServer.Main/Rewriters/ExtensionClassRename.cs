using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CompileServer.Rewriters
{
    /// <summary>
    /// 扩展方法重命名器 - 为扩展方法添加 __Patch__ 后缀以避免方法签名冲突
    /// </summary>
    public class ExtensionClassRename : CSharpSyntaxRewriter
    {
        public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            // 检查是否是扩展方法：static 方法且第一个参数带有 this 修饰符
            if (node.Modifiers.Any(SyntaxKind.StaticKeyword) &&
                node.ParameterList.Parameters.Count > 0 &&
                node.ParameterList.Parameters[0].Modifiers.Any(SyntaxKind.ThisKeyword))
            {
                var originalName = node.Identifier.Text;
                var patchName = $"{originalName}__Patch__";

                // 重命名方法
                return node.WithIdentifier(SyntaxFactory.Identifier(patchName));
            }

            return base.VisitMethodDeclaration(node);
        }
    }
}
