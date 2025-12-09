using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        
        // 当前正在访问的方法名（用于记录调用者，格式：ReturnType ClassName::MethodName(parameters)）
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
            return _genericMethodCalls;
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
            
            // 构建完整的方法名：ReturnType ClassName::MethodName(parameters)
            _currentMethodName = GetFullMethodName(node);
            
            base.VisitMethodDeclaration(node);
            
            _currentMethodName = previousMethodName;
        }
        
        /// <summary>
        /// 获取完整的方法名（包含返回类型、类名、方法名和参数）
        /// 格式：ReturnType ClassName::MethodName(parameters)
        /// 参考 CreateMethodDiffInfo 的实现方式
        /// </summary>
        private string GetFullMethodName(MethodDeclarationSyntax method)
        {
            if (method == null)
                throw new ArgumentNullException(nameof(method));
            
            if (string.IsNullOrEmpty(_currentClassName))
                throw new InvalidOperationException($"无法获取方法名：当前类名为空。方法：{method.Identifier.ValueText}");
            
            // 处理引用返回类型（ref T）
            string returnTypePrefix = "";
            TypeSyntax typeToAnalyze = method.ReturnType;
            
            if (method.ReturnType is RefTypeSyntax refType)
            {
                returnTypePrefix = "ref ";
                typeToAnalyze = refType.Type;
            }
            
            // 获取返回类型（对于 ref T，需要分析内部的类型）
            var returnTypeInfo = _semanticModel.GetTypeInfo(typeToAnalyze);
            if (returnTypeInfo.Type == null)
                throw new InvalidOperationException($"无法获取返回类型：方法 {_currentClassName}::{method.Identifier.ValueText}，返回类型节点：{method.ReturnType}");
            
            var returnType = returnTypeInfo.Type.ToDisplayString(RoslynHelper.TYPE_FORMAT);
            if (string.IsNullOrEmpty(returnType))
                throw new InvalidOperationException($"返回类型显示字符串为空：方法 {_currentClassName}::{method.Identifier.ValueText}，类型：{returnTypeInfo.Type}");
            
            // 组合返回类型（包含 ref 前缀）
            var fullReturnType = returnTypePrefix + returnType;
            
            // 获取方法名和参数
            var methodName = method.Identifier.ValueText;
            var parameters = method.ParameterList.Parameters.Count > 0
                ? string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""))
                : "";
            
            return $"{fullReturnType} {_currentClassName}::{methodName}({parameters})";
        }

        /// <summary>
        /// 访问构造函数
        /// </summary>
        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            var previousMethodName = _currentMethodName;
            
            // 构造函数返回类型是类本身
            var returnType = _currentClassName ?? "void";
            var methodName = node.Identifier.ValueText;
            var parameters = node.ParameterList.Parameters.Count > 0
                ? string.Join(",", node.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""))
                : "";
            
            _currentMethodName = $"{returnType} {_currentClassName}::{methodName}({parameters})";
            
            base.VisitConstructorDeclaration(node);
            
            _currentMethodName = previousMethodName;
        }

        /// <summary>
        /// 访问属性声明
        /// </summary>
        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var propertyName = node.Identifier.ValueText;
            
            // 获取属性类型作为返回类型
            var typeInfo = _semanticModel.GetTypeInfo(node.Type);
            if (typeInfo.Type == null)
                throw new InvalidOperationException($"无法获取属性类型：属性 {_currentClassName}::{propertyName}，类型节点：{node.Type}");
            
            var returnType = typeInfo.Type.ToDisplayString(RoslynHelper.TYPE_FORMAT);
            if (string.IsNullOrEmpty(returnType))
                throw new InvalidOperationException($"属性类型显示字符串为空：属性 {_currentClassName}::{propertyName}，类型：{typeInfo.Type}");
            
            if (node.AccessorList != null)
            {
                // 普通属性没有参数（索引器使用 IndexerDeclarationSyntax，不是 PropertyDeclarationSyntax）
                var parameters = "";
                
                foreach (var accessor in node.AccessorList.Accessors)
                {
                    var previousMethodName = _currentMethodName;
                    var accessorName = $"{propertyName}.{accessor.Keyword.ValueText}";
                    
                    _currentMethodName = $"{returnType} {_currentClassName}::{accessorName}({parameters})";
                    
                    base.VisitAccessorDeclaration(accessor);
                    
                    _currentMethodName = previousMethodName;
                }
            }
            else if (node.ExpressionBody != null)
            {
                var previousMethodName = _currentMethodName;
                _currentMethodName = $"{returnType} {_currentClassName}::{propertyName}.get()";
                
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
            
            // 获取返回类型
            var typeInfo = _semanticModel.GetTypeInfo(node.ReturnType);
            if (typeInfo.Type == null)
                throw new InvalidOperationException($"无法获取操作符返回类型：操作符 {node.OperatorToken.ValueText}，返回类型节点：{node.ReturnType}");
            
            var returnType = typeInfo.Type.ToDisplayString(RoslynHelper.TYPE_FORMAT);
            if (string.IsNullOrEmpty(returnType))
                throw new InvalidOperationException($"操作符返回类型显示字符串为空：操作符 {node.OperatorToken.ValueText}，类型：{typeInfo.Type}");
            
            var operatorName = $"operator {node.OperatorToken.ValueText}";
            var parameters = node.ParameterList.Parameters.Count > 0
                ? string.Join(",", node.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""))
                : "";
            
            _currentMethodName = $"{returnType} {_currentClassName}::{operatorName}({parameters})";
            
            base.VisitOperatorDeclaration(node);
            
            _currentMethodName = previousMethodName;
        }

        /// <summary>
        /// 访问转换操作符声明
        /// </summary>
        public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
        {
            var previousMethodName = _currentMethodName;
            
            // 转换操作符的返回类型就是转换的目标类型（使用 TEST_FORMAT，与其他方法一致）
            var typeInfo = _semanticModel.GetTypeInfo(node.Type);
            if (typeInfo.Type == null)
                throw new InvalidOperationException($"无法获取转换操作符目标类型：转换操作符，目标类型节点：{node.Type}");
            
            var returnType = typeInfo.Type.ToDisplayString(RoslynHelper.TYPE_FORMAT);
            if (string.IsNullOrEmpty(returnType))
                throw new InvalidOperationException($"转换操作符目标类型显示字符串为空：转换操作符，类型：{typeInfo.Type}");
            
            var operatorName = $"operator {returnType}";
            var parameters = node.ParameterList.Parameters.Count > 0
                ? string.Join(",", node.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""))
                : "";
            
            _currentMethodName = $"{returnType} {_currentClassName}::{operatorName}({parameters})";
            
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
                    
                    // 获取字段类型作为返回类型
                    var typeInfo = _semanticModel.GetTypeInfo(node.Declaration.Type);
                    if (typeInfo.Type == null)
                        throw new InvalidOperationException($"无法获取字段类型：字段 {variable.Identifier.ValueText}，类型节点：{node.Declaration.Type}");
                    
                    var returnType = typeInfo.Type.ToDisplayString(RoslynHelper.TYPE_FORMAT);
                    if (string.IsNullOrEmpty(returnType))
                        throw new InvalidOperationException($"字段类型显示字符串为空：字段 {variable.Identifier.ValueText}，类型：{typeInfo.Type}");
                    
                    var fieldName = $"field.{variable.Identifier.ValueText}";
                    _currentMethodName = $"{returnType} {_currentClassName}::{fieldName}()";
                    
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

            // 通过语义模型获取方法符号
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol && methodSymbol.IsGenericMethod)
            {
                var genericMethodFullName = GetMethodDefinitionNameFromSymbol(methodSymbol);
                if (string.IsNullOrEmpty(genericMethodFullName))
                    throw new InvalidOperationException($"无法获取泛型方法定义名称：调用处方法 {_currentMethodName}，方法符号 {methodSymbol}");
                
                AddGenericMethodCall(genericMethodFullName, _currentMethodName);
                return;
            }
            
            // 检查候选符号（可能由于重载解析失败）
            if (symbolInfo.CandidateSymbols.Any())
            {
                foreach (var candidate in symbolInfo.CandidateSymbols)
                {
                    if (candidate is IMethodSymbol candidateMethod && candidateMethod.IsGenericMethod)
                    {
                        var genericMethodFullName = GetMethodDefinitionNameFromSymbol(candidateMethod);
                        if (string.IsNullOrEmpty(genericMethodFullName))
                            throw new InvalidOperationException($"无法获取候选泛型方法定义名称：调用处方法 {_currentMethodName}，候选方法符号 {candidateMethod}");
                        
                        AddGenericMethodCall(genericMethodFullName, _currentMethodName);
                        return;
                    }
                }
            }
            
            // 如果不是泛型方法调用，直接返回（大多数调用都不是泛型方法）
            // 只有在语法上明确是泛型方法调用但无法解析时才抛出错误
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess && 
                memberAccess.Name is GenericNameSyntax)
            {
                // 语法上看起来是泛型方法调用，但语义模型无法解析
                throw new InvalidOperationException($"无法解析泛型方法调用：调用处方法 {_currentMethodName}，调用表达式位置 {invocation.GetLocation().GetLineSpan()}");
            }
            
            // 否则，这不是泛型方法调用，直接返回
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
                returnType = originalMethod.ReturnType.ToDisplayString(RoslynHelper.TYPE_FORMAT);
            }
            
            // 获取声明类型（使用 TEST_FORMAT 以包含完整的命名空间）
            var declaringType = originalMethod.ContainingType?.ToDisplayString(RoslynHelper.TYPE_FORMAT) ?? "Unknown";
            
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
                // 对于其他类型，使用 TEST_FORMAT 以保持一致性
                return p.Type.ToDisplayString(RoslynHelper.TYPE_FORMAT);
            });
            
            var parameterList = string.Join(",", parameters);
            
            // 构建定义格式：ReturnType DeclaringType::MethodName(ParameterTypes)
            return $"{returnType} {declaringType}::{methodName}({parameterList})";
        }
    }
}

