using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 泛型方法调用收集器
    /// 核心策略：
    /// 1. 一次性查找所有调用表达式，避免遍历整个树
    /// 2. 使用 Ancestors() 查找上下文，避免维护状态
    /// 3. 对所有调用进行语义分析，以捕获类型推断的泛型方法调用
    /// </summary>
    public class GenericMethodCallWalker
    {
        private readonly Dictionary<string, HashSet<string>> _genericMethodCalls = new Dictionary<string, HashSet<string>>();
        private SemanticModel _semanticModel;
        
        // 方法节点到方法名的缓存（延迟计算）
        private readonly Dictionary<SyntaxNode, string> _methodNameCache = new Dictionary<SyntaxNode, string>();

        public void SetSemanticModel(SemanticModel semanticModel)
        {
            _semanticModel = semanticModel ?? throw new ArgumentNullException(nameof(semanticModel));
        }

        public Dictionary<string, HashSet<string>> GetGenericMethodCalls()
        {
            return _genericMethodCalls;
        }

        /// <summary>
        /// 分析类型声明，收集泛型方法调用
        /// </summary>
        public void Analyze(TypeDeclarationSyntax typeDecl)
        {
            _methodNameCache.Clear();
            _genericMethodCalls.Clear();

            // 策略1：一次性查找所有调用表达式（只遍历一次）
            var allInvocations = typeDecl.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .ToList();

            // 策略2：对所有调用进行语义分析（包括类型推断的泛型方法调用）
            foreach (var invocation in allInvocations)
            {
                AnalyzeInvocation(invocation, typeDecl);
            }
        }

        /// <summary>
        /// 分析单个调用表达式
        /// </summary>
        private void AnalyzeInvocation(InvocationExpressionSyntax invocation, TypeDeclarationSyntax typeDecl)
        {
            // 策略4：使用 Ancestors() 查找调用所在的上下文，避免维护状态
            var containingMember = FindContainingMember(invocation);
            if (containingMember == null)
            {
                return;
            }

            // 获取调用者的完整方法名
            var callerMethodName = GetCallerMethodName(containingMember);
            if (string.IsNullOrEmpty(callerMethodName))
            {
                return;
            }

            // 策略3：对所有调用进行语义分析，以识别泛型方法调用（包括类型推断的情况）
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol && methodSymbol.IsGenericMethod)
            {
                var genericMethodFullName = GetMethodDefinitionNameFromSymbol(methodSymbol);
                if (!string.IsNullOrEmpty(genericMethodFullName))
                {
                    AddGenericMethodCall(genericMethodFullName, callerMethodName);
                }
            }
        }

        /// <summary>
        /// 查找调用所在的成员（方法、属性、字段等）
        /// 使用 Ancestors() 向上查找，避免维护状态
        /// </summary>
        private SyntaxNode FindContainingMember(InvocationExpressionSyntax invocation)
        {
            return invocation.Ancestors()
                .FirstOrDefault(node => 
                    node is MethodDeclarationSyntax ||
                    node is ConstructorDeclarationSyntax ||
                    node is PropertyDeclarationSyntax ||
                    node is AccessorDeclarationSyntax ||
                    node is OperatorDeclarationSyntax ||
                    node is ConversionOperatorDeclarationSyntax ||
                    (node is VariableDeclaratorSyntax && node.Parent?.Parent is FieldDeclarationSyntax));
        }

        /// <summary>
        /// 从包含成员获取调用者的完整方法名
        /// </summary>
        private string GetCallerMethodName(SyntaxNode containingMember)
        {
            switch (containingMember)
            {
                case MethodDeclarationSyntax methodDecl:
                    return methodDecl.FullName(_semanticModel);
                default:
                    return null;
            }
        }


        /// <summary>
        /// 添加泛型方法调用记录
        /// </summary>
        private void AddGenericMethodCall(string genericMethodFullName, string callerMethodName)
        {
            if (!_genericMethodCalls.TryGetValue(genericMethodFullName, out var callers))
            {
                callers = new HashSet<string>();
                _genericMethodCalls[genericMethodFullName] = callers;
            }
            callers.Add(callerMethodName);
        }

        /// <summary>
        /// 从方法符号获取方法的定义名称
        /// </summary>
        private static string GetMethodDefinitionNameFromSymbol(IMethodSymbol methodSymbol)
        {
            var originalMethod = methodSymbol.OriginalDefinition;

            string returnType;
            if (originalMethod.ReturnType is ITypeParameterSymbol returnTypeParam)
            {
                returnType = returnTypeParam.Name;
            }
            else
            {
                returnType = originalMethod.ReturnType.ToDisplayString(RoslynHelper.TYPE_FORMAT);
            }

            var declaringType = originalMethod.ContainingType?.ToDisplayString(RoslynHelper.TYPE_FORMAT) ?? "Unknown";
            var methodName = originalMethod.Name;

            var parameters = originalMethod.Parameters.Select(p =>
            {
                if (p.Type is ITypeParameterSymbol paramTypeParam)
                {
                    return paramTypeParam.Name;
                }
                return p.Type.ToDisplayString(RoslynHelper.TYPE_FORMAT);
            });

            var parameterList = string.Join(",", parameters);

            return $"{returnType} {declaringType}::{methodName}({parameterList})";
        }
    }
}

