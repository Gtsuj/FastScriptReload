using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 泛型方法调用收集器 - 收集文件中所有调用泛型方法的地方
    /// Key: 调用的泛型方法全名（格式：ReturnType DeclaringType::MethodName(ParameterTypes)）
    /// Value: 调用处的方法名集合
    /// </summary>
    public class GenericMethodCallWalker : CSharpSyntaxWalker
    {
        // Key: 调用的泛型方法全名, Value: 调用处的方法名集合
        private readonly Dictionary<string, HashSet<string>> _genericMethodCalls = new Dictionary<string, HashSet<string>>();
        
        // 语义模型，用于获取方法符号信息
        private readonly SemanticModel _semanticModel;
        
        // 当前正在访问的方法名（用于记录调用者）
        private string _currentMethodName = null;
        
        // 当前正在访问的类名（用于构建完整的方法名）
        private string _currentClassName = null;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="semanticModel">语义模型，用于获取方法符号信息</param>
        public GenericMethodCallWalker(SemanticModel semanticModel)
        {
            _semanticModel = semanticModel ?? throw new ArgumentNullException(nameof(semanticModel));
        }

        /// <summary>
        /// 获取收集到的泛型方法调用信息
        /// Key: 调用的泛型方法全名, Value: 调用处的方法名集合
        /// </summary>
        public Dictionary<string, HashSet<string>> GetGenericMethodCalls()
        {
            return new Dictionary<string, HashSet<string>>(_genericMethodCalls);
        }

        /// <summary>
        /// 访问类型声明（类、结构体、接口等）
        /// </summary>
        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var previousClassName = _currentClassName;
            _currentClassName = node.FullName();
            base.VisitClassDeclaration(node);
            _currentClassName = previousClassName;
        }

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            var previousClassName = _currentClassName;
            _currentClassName = node.FullName();
            base.VisitStructDeclaration(node);
            _currentClassName = previousClassName;
        }

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            var previousClassName = _currentClassName;
            _currentClassName = node.FullName();
            base.VisitInterfaceDeclaration(node);
            _currentClassName = previousClassName;
        }

        /// <summary>
        /// 访问方法声明
        /// </summary>
        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var previousMethodName = _currentMethodName;
            _currentMethodName = node.FullName();
            
            base.VisitMethodDeclaration(node);
            
            _currentMethodName = previousMethodName;
        }

        /// <summary>
        /// 访问构造函数
        /// </summary>
        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            var previousMethodName = _currentMethodName;
            _currentMethodName = node.Identifier.ValueText;
            
            base.VisitConstructorDeclaration(node);
            
            _currentMethodName = previousMethodName;
        }

        /// <summary>
        /// 访问属性声明
        /// </summary>
        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var propertyName = node.Identifier.ValueText;
            
            if (node.AccessorList != null)
            {
                foreach (var accessor in node.AccessorList.Accessors)
                {
                    var previousMethodName = _currentMethodName;
                    _currentMethodName = $"{propertyName}.{accessor.Keyword.ValueText}";
                    
                    base.VisitAccessorDeclaration(accessor);
                    
                    _currentMethodName = previousMethodName;
                }
            }
            else if (node.ExpressionBody != null)
            {
                var previousMethodName = _currentMethodName;
                _currentMethodName = $"{propertyName}.get";
                
                base.VisitPropertyDeclaration(node);
                
                _currentMethodName = previousMethodName;
            }
            else
            {
                base.VisitPropertyDeclaration(node);
            }
        }

        /// <summary>
        /// 访问操作符声明
        /// </summary>
        public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node)
        {
            var previousMethodName = _currentMethodName;
            _currentMethodName = $"operator {node.OperatorToken.ValueText}";
            
            base.VisitOperatorDeclaration(node);
            
            _currentMethodName = previousMethodName;
        }

        /// <summary>
        /// 访问转换操作符声明
        /// </summary>
        public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
        {
            var previousMethodName = _currentMethodName;
            _currentMethodName = $"operator {node.Type?.ToString() ?? ""}";
            
            base.VisitConversionOperatorDeclaration(node);
            
            _currentMethodName = previousMethodName;
        }

        /// <summary>
        /// 访问字段声明（检查初始化器中的泛型方法调用）
        /// </summary>
        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            foreach (var variable in node.Declaration.Variables)
            {
                if (variable.Initializer?.Value != null)
                {
                    var previousMethodName = _currentMethodName;
                    _currentMethodName = $"field.{variable.Identifier.ValueText}";
                    
                    Visit(variable.Initializer.Value);
                    
                    _currentMethodName = previousMethodName;
                }
            }
            
            base.VisitFieldDeclaration(node);
        }

        /// <summary>
        /// 访问方法调用表达式
        /// </summary>
        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            // 收集泛型方法调用
            CollectGenericMethodCall(node);
            
            base.VisitInvocationExpression(node);
        }

        /// <summary>
        /// 收集泛型方法调用
        /// </summary>
        private void CollectGenericMethodCall(InvocationExpressionSyntax invocation)
        {
            if (_currentMethodName == null)
                return;

            try
            {
                // 方法1: 通过语义模型获取方法符号
                var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
                
                if (symbolInfo.Symbol is IMethodSymbol methodSymbol && methodSymbol.IsGenericMethod)
                {
                    var genericMethodFullName = GetMethodDefinitionNameFromSymbol(methodSymbol);
                    if (!string.IsNullOrEmpty(genericMethodFullName))
                    {
                        AddGenericMethodCall(genericMethodFullName, _currentMethodName);
                    }
                }
                // 检查候选符号（可能由于重载解析失败）
                else if (symbolInfo.CandidateSymbols.Any())
                {
                    foreach (var candidate in symbolInfo.CandidateSymbols)
                    {
                        if (candidate is IMethodSymbol candidateMethod && candidateMethod.IsGenericMethod)
                        {
                            var genericMethodFullName = GetMethodDefinitionNameFromSymbol(candidateMethod);
                            if (!string.IsNullOrEmpty(genericMethodFullName))
                            {
                                AddGenericMethodCall(genericMethodFullName, _currentMethodName);
                                break; // 找到一个就够了
                            }
                        }
                    }
                }
                // 方法2: 如果语义模型无法解析，尝试通过语法检测泛型方法调用
                else if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    if (memberAccess.Name is GenericNameSyntax genericName)
                    {
                        // 尝试从接收者类型获取方法
                        var receiverTypeInfo = _semanticModel.GetTypeInfo(memberAccess.Expression);
                        var receiverType = receiverTypeInfo.ConvertedType as INamedTypeSymbol 
                                           ?? receiverTypeInfo.Type as INamedTypeSymbol;
                        
                        if (receiverType != null)
                        {
                            var methodName = genericName.Identifier.ValueText;
                            var methods = receiverType.GetMembers(methodName).OfType<IMethodSymbol>();
                            
                            // 查找泛型方法（匹配泛型参数数量）
                            var typeArgCount = genericName.TypeArgumentList.Arguments.Count;
                            var genericMethod = methods.FirstOrDefault(m => 
                                m.IsGenericMethod && 
                                m.TypeParameters.Length == typeArgCount);
                            
                            if (genericMethod != null)
                            {
                                var genericMethodFullName = GetMethodDefinitionNameFromSymbol(genericMethod);
                                if (!string.IsNullOrEmpty(genericMethodFullName))
                                {
                                    AddGenericMethodCall(genericMethodFullName, _currentMethodName);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 忽略错误，继续处理其他调用
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
        /// 从方法符号获取方法的定义名称（格式参考 MethodHelper.FullName）
        /// 格式：ReturnType DeclaringType::MethodName(ParameterTypes)
        /// </summary>
        private static string GetMethodDefinitionNameFromSymbol(IMethodSymbol methodSymbol)
        {
            // 使用原始定义来获取泛型参数（而不是实例化后的具体类型）
            var originalMethod = methodSymbol.OriginalDefinition;
            
            // 获取返回类型（对于泛型方法，返回类型可能是泛型参数）
            string returnType;
            if (originalMethod.ReturnType is ITypeParameterSymbol typeParam)
            {
                // 返回类型是泛型参数
                returnType = typeParam.Name;
            }
            else
            {
                // 返回类型是具体类型，使用简单格式（不包含 global:: 前缀）
                returnType = originalMethod.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            }
            
            // 获取声明类型（使用简单格式，不包含 global:: 前缀）
            var declaringType = originalMethod.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "Unknown";
            
            // 获取方法名
            var methodName = originalMethod.Name;
            
            // 获取参数类型（使用定义中的类型，而不是调用时的具体类型）
            var parameters = originalMethod.Parameters.Select(p => 
            {
                // 对于泛型参数，使用泛型参数名（如 T）
                if (p.Type is ITypeParameterSymbol typeParam)
                {
                    return typeParam.Name;
                }
                // 对于其他类型，使用简单格式
                return p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            });
            
            var parameterList = string.Join(",", parameters);
            
            // 构建定义格式：ReturnType DeclaringType::MethodName(ParameterTypes)
            return $"{returnType} {declaringType}::{methodName}({parameterList})";
        }

        /// <summary>
        /// 获取类型全名（支持嵌套类型）
        /// </summary>
    }
}

