using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 类型依赖收集器 - 通过语义模型收集文件中所有依赖的类型
    /// </summary>
    public class TypeDependencyWalker : CSharpSyntaxWalker
    {
        // 存储完整的类型名（包含命名空间）
        private readonly HashSet<string> _fullTypeNames = new HashSet<string>();
        // 语义模型，用于获取完整的类型名
        private readonly SemanticModel _semanticModel;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="semanticModel">语义模型，用于获取完整的类型名</param>
        public TypeDependencyWalker(SemanticModel semanticModel)
        {
            _semanticModel = semanticModel ?? throw new System.ArgumentNullException(nameof(semanticModel));
        }

        /// <summary>
        /// 获取收集到的所有完整类型名称（包含命名空间）
        /// </summary>
        public HashSet<string> FullTypeNames => _fullTypeNames;

        /// <summary>
        /// 获取所有类型（包含命名空间的完整类型名）
        /// </summary>
        public HashSet<string> GetFullTypeNames()
        {
            return new HashSet<string>(_fullTypeNames);
        }

        public override void VisitVariableDeclaration(VariableDeclarationSyntax node)
        {
            CollectTypeFromSyntax(node.Type);
            base.VisitVariableDeclaration(node);
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            CollectTypeFromSyntax(node.Declaration.Type);
            base.VisitFieldDeclaration(node);
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            CollectTypeFromSyntax(node.Type);
            base.VisitPropertyDeclaration(node);
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            // 返回类型
            CollectTypeFromSyntax(node.ReturnType);

            // 参数类型
            foreach (var parameter in node.ParameterList.Parameters)
            {
                if (parameter.Type != null)
                {
                    CollectTypeFromSyntax(parameter.Type);
                }
            }

            // 泛型类型参数约束
            // 约束在方法声明的 ConstraintClauses 中，不在 TypeParameterSyntax 中
            foreach (var constraintClause in node.ConstraintClauses)
            {
                foreach (var constraint in constraintClause.Constraints.OfType<TypeConstraintSyntax>())
                {
                    CollectTypeFromSyntax(constraint.Type);
                }
            }

            base.VisitMethodDeclaration(node);
        }

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            // 参数类型
            foreach (var parameter in node.ParameterList.Parameters)
            {
                if (parameter.Type != null)
                {
                    CollectTypeFromSyntax(parameter.Type);
                }
            }
            base.VisitConstructorDeclaration(node);
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            // 基类
            if (node.BaseList != null)
            {
                foreach (var baseType in node.BaseList.Types)
                {
                    CollectTypeFromSyntax(baseType.Type);
                }
            }
            base.VisitClassDeclaration(node);
        }

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            // 接口实现
            if (node.BaseList != null)
            {
                foreach (var baseType in node.BaseList.Types)
                {
                    CollectTypeFromSyntax(baseType.Type);
                }
            }
            base.VisitStructDeclaration(node);
        }

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            // 接口继承
            if (node.BaseList != null)
            {
                foreach (var baseType in node.BaseList.Types)
                {
                    CollectTypeFromSyntax(baseType.Type);
                }
            }
            base.VisitInterfaceDeclaration(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            CollectTypeFromSyntax(node.Type);
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitCastExpression(CastExpressionSyntax node)
        {
            CollectTypeFromSyntax(node.Type);
            base.VisitCastExpression(node);
        }

        public override void VisitTypeOfExpression(TypeOfExpressionSyntax node)
        {
            CollectTypeFromSyntax(node.Type);
            base.VisitTypeOfExpression(node);
        }
        
        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            // 处理 'as' 表达式
            // 在 Roslyn 中，'as' 表达式是 BinaryExpressionSyntax，Kind 为 SyntaxKind.AsExpression
            if (node.Kind() == SyntaxKind.AsExpression)
            {
                // 'as' 表达式的类型在右侧（Right 属性）
                if (node.Right is TypeSyntax typeSyntax)
                {
                    CollectTypeFromSyntax(typeSyntax);
                }
            }
            base.VisitBinaryExpression(node);
        }

        public override void VisitArrayType(ArrayTypeSyntax node)
        {
            CollectTypeFromSyntax(node.ElementType);
            base.VisitArrayType(node);
        }

        public override void VisitNullableType(NullableTypeSyntax node)
        {
            CollectTypeFromSyntax(node.ElementType);
            base.VisitNullableType(node);
        }

        public override void VisitGenericName(GenericNameSyntax node)
        {
            // 泛型类型参数
            foreach (var typeArg in node.TypeArgumentList.Arguments)
            {
                CollectTypeFromSyntax(typeArg);
            }
            base.VisitGenericName(node);
        }

        /// <summary>
        /// 从类型语法节点中收集类型信息
        /// </summary>
        private void CollectTypeFromSyntax(TypeSyntax typeSyntax)
        {
            if (typeSyntax == null) return;

            var typeInfo = _semanticModel.GetTypeInfo(typeSyntax);
            if (typeInfo.Type != null)
            {
                var typeSymbol = typeInfo.Type;
                
                // 过滤掉预定义类型和关键字
                if (IsBuiltInType(typeSymbol))
                    return;
                
                // 构建完整的类型名：命名空间.类型名
                var namespaceName = typeSymbol.ContainingNamespace?.ToDisplayString();
                var typeName = typeSymbol.Name;
                
                // 对于嵌套类型，需要包含外层类型
                var containingType = typeSymbol.ContainingType;
                if (containingType != null)
                {
                    // 嵌套类型：外层类型.内层类型
                    var outerTypeName = containingType.Name;
                    var outerNamespace = containingType.ContainingNamespace?.ToDisplayString();
                    
                    string fullTypeName;
                    if (!string.IsNullOrWhiteSpace(outerNamespace) && outerNamespace != "<global namespace>")
                    {
                        fullTypeName = $"{outerNamespace}.{outerTypeName}.{typeName}";
                    }
                    else
                    {
                        fullTypeName = $"{outerTypeName}.{typeName}";
                    }
                    _fullTypeNames.Add(fullTypeName);
                    return;
                }
                
                // 非嵌套类型
                string fullName;
                if (!string.IsNullOrWhiteSpace(namespaceName) && namespaceName != "<global namespace>")
                {
                    fullName = $"{namespaceName}.{typeName}";
                }
                else
                {
                    fullName = typeName;
                }
                
                _fullTypeNames.Add(fullName);
            }
        }

        /// <summary>
        /// 判断是否为内置类型（不需要收集）
        /// </summary>
        private bool IsBuiltInType(ITypeSymbol typeSymbol)
        {
            if (typeSymbol.SpecialType != SpecialType.None)
                return true;
            
            var typeName = typeSymbol.Name.ToLower();
            var builtInTypes = new HashSet<string>
            {
                "object", "string", "int", "long", "short", "byte",
                "uint", "ulong", "ushort", "sbyte", "float", "double", "decimal",
                "bool", "char", "void"
            };
            
            return builtInTypes.Contains(typeName);
        }
    }
}

