using System.Collections.Generic;
using System.Linq;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 语法差异分析器 - 使用Roslyn进行源代码级别的差异分析
    /// </summary>
    public static class DiffAnalyzerHelper
    {
        /// <summary>
        /// 分析文件差异
        /// </summary>
        public static void AnalyzeDiff(CSharpCompilation compilation, SyntaxTree newSyntaxTree,
            Dictionary<string, DiffResult> results)
        {
            var filePath = newSyntaxTree.FilePath;
            var snapshot = TypeInfoHelper.GetFileSnapshot(filePath);
            if (snapshot == null)
            {
                LoggerScoped.LogDebug($"文件没有快照，跳过差异分析: {filePath}");
                return;
            }

            var oldSyntaxTree = snapshot.SyntaxTree;
            CompareTypes(compilation, oldSyntaxTree, newSyntaxTree, results);
        }
        
        /// <summary>
        /// 比较两个语法树中的类型差异
        /// </summary>
        private static void CompareTypes(CSharpCompilation compilation, SyntaxTree oldTree, SyntaxTree newTree,
            Dictionary<string, DiffResult> results)
        {
            var oldRoot = oldTree.GetRoot();
            var newRoot = newTree.GetRoot();

            var oldTypes = oldRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
            var newTypes = newRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();

            // 按类型全名匹配
            var oldTypeMap = oldTypes.ToDictionary(t => t.FullName(), t => t);
            var newTypeMap = newTypes.ToDictionary(t => t.FullName(), t => t);

            // 为新树获取SemanticModel
            SemanticModel newSemanticModel = compilation.GetSemanticModel(newTree);

            foreach (var kvp in newTypeMap)
            {
                var typeFullName = kvp.Key;
                var newType = kvp.Value;

                // 优先从results中获取
                if (!results.TryGetValue(typeFullName, out var typeResult))
                {
                    typeResult = new DiffResult();
                }

                if (oldTypeMap.TryGetValue(typeFullName, out var oldType))
                {
                    // 类型存在，比较差异
                    CompareTypeMembers(oldType, newType, typeFullName, typeResult, newSemanticModel);
                }

                // 只有当该类型有差异时才添加到结果中
                if (typeResult.AddedMethods.Count > 0 ||
                    typeResult.ModifiedMethods.Count > 0 ||
                    typeResult.AddedFields.Count > 0)
                {
                    results[typeFullName] = typeResult;
                }
            }
        }

        /// <summary>
        /// 比较类型成员的差异
        /// </summary>
        private static void CompareTypeMembers(TypeDeclarationSyntax oldType, TypeDeclarationSyntax newType,
            string typeFullName, DiffResult result, SemanticModel semanticModel)
        {
            var isInternalClass = IsInternalType(newType);

            // 比较方法
            var oldMethods = oldType.Members.OfType<MethodDeclarationSyntax>().ToList();
            var newMethods = newType.Members.OfType<MethodDeclarationSyntax>().ToList();

            var oldMethodMap = oldMethods.ToDictionary(m => m.FullName(), m => m);
            var newMethodMap = newMethods.ToDictionary(m => m.FullName(), m => m);

            foreach (var kvp in newMethodMap)
            {
                var methodSignature = kvp.Key;
                var newMethod = kvp.Value;

                if (!oldMethodMap.TryGetValue(methodSignature, out var oldMethod))
                {
                    // 新增方法
                    var methodInfo = CreateMethodDiffInfo(newMethod, semanticModel);
                    result.AddedMethods.Add(methodInfo.FullName, methodInfo);
                }
                else
                {
                    // 检查方法是否被修改（比较方法体）
                    if (IsMethodBodyChanged(oldMethod, newMethod))
                    {
                        var methodInfo = CreateMethodDiffInfo(newMethod, semanticModel);
                        result.ModifiedMethods.Add(methodInfo.FullName, methodInfo);
                    }
                }
            }

            // 比较字段
            var oldFields = oldType.Members.OfType<FieldDeclarationSyntax>().ToList();
            var newFields = newType.Members.OfType<FieldDeclarationSyntax>().ToList();

            var oldFieldMap = oldFields
                .SelectMany(f => f.Declaration.Variables.Select(v => new { Field = f, Variable = v }))
                .ToDictionary(x => x.Variable.Identifier.ValueText, x => x);

            var newFieldMap = newFields
                .SelectMany(f => f.Declaration.Variables.Select(v => new { Field = f, Variable = v }))
                .ToDictionary(x => x.Variable.Identifier.ValueText, x => x);

            foreach (var kvp in newFieldMap)
            {
                var fieldName = kvp.Key;
                var newFieldData = kvp.Value;

                if (!oldFieldMap.TryGetValue(fieldName, out var oldFieldData))
                {
                    // 新增字段
                    var fieldInfo = CreateFieldDiffInfo(newFieldData.Field, newFieldData.Variable, typeFullName, semanticModel);
                    result.AddedFields.Add(fieldInfo.FullName, fieldInfo);
                }
            }
        }

        /// <summary>
        /// 创建方法差异信息
        /// </summary>
        private static MethodDiffInfo CreateMethodDiffInfo(MethodDeclarationSyntax method, SemanticModel semanticModel)
        {
            var isGeneric = method.TypeParameterList != null && method.TypeParameterList.Parameters.Count > 0;

            return new MethodDiffInfo
            {
                FullName = method.FullName(semanticModel),
                IsGenericMethod = isGeneric,
            };
        }

        /// <summary>
        /// 创建字段差异信息
        /// </summary>
        private static FieldDiffInfo CreateFieldDiffInfo(FieldDeclarationSyntax field,
            VariableDeclaratorSyntax variable, string declaringTypeFullName, SemanticModel semanticModel)
        {
            var fieldName = variable.Identifier.ValueText;

            // 获取字段类型的完整名称
            var containingType = semanticModel.GetDeclaredSymbol(variable)?.ContainingType.ToDisplayString(RoslynHelper.TYPE_FORMAT);
            var typeInfo = semanticModel.GetTypeInfo(field.Declaration.Type);
            var fieldTypeFullName = typeInfo.Type?.ToDisplayString(RoslynHelper.TYPE_FORMAT) ?? field.Declaration.Type.ToString();

            var fullName = $"{fieldTypeFullName} {containingType}::{fieldName}";

            return new FieldDiffInfo
            {
                FullName = fullName,
                DeclaringTypeFullName = declaringTypeFullName,
            };
        }

        /// <summary>
        /// 检查方法体是否改变
        /// </summary>
        private static bool IsMethodBodyChanged(MethodDeclarationSyntax oldMethod, MethodDeclarationSyntax newMethod)
        {
            // 比较方法体的字符串表示（简化实现）
            var oldBody = oldMethod.Body?.ToString() ?? oldMethod.ExpressionBody?.ToString() ?? "";
            var newBody = newMethod.Body?.ToString() ?? newMethod.ExpressionBody?.ToString() ?? "";

            return oldBody != newBody;
        }

        /// <summary>
        /// 获取方法签名（用于匹配）
        /// </summary>
        /// <summary>
        /// 检查类型是否为internal
        /// </summary>
        private static bool IsInternalType(TypeDeclarationSyntax typeDecl)
        {
            return typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
        }
    }

    /// <summary>
    /// 差异结果
    /// </summary>
    public class DiffResult
    {
        /// <summary>
        /// 新增的方法
        /// </summary>
        public Dictionary<string, MethodDiffInfo> AddedMethods { get; } = new();

        /// <summary>
        /// 修改的方法
        /// </summary>
        public Dictionary<string, MethodDiffInfo> ModifiedMethods { get; } = new();

        /// <summary>
        /// 新增的字段
        /// </summary>
        public Dictionary<string, FieldDiffInfo> AddedFields { get; } = new();
    }

    /// <summary>
    /// 方法差异信息
    /// </summary>
    public class MethodDiffInfo
    {
        /// <summary>
        /// 方法全名（包含返回类型、声明类型、方法名、参数类型）
        /// </summary>
        public string FullName { get; set; }

        /// <summary>
        /// 是否为泛型方法
        /// </summary>
        public bool IsGenericMethod { get; set; }
    }

    /// <summary>
    /// 字段差异信息
    /// </summary>
    public class FieldDiffInfo
    {
        /// <summary>
        /// 声明类型全名
        /// </summary>
        public string DeclaringTypeFullName { get; set; }
        
        /// <summary>
        /// 字段全名
        /// </summary>
        public string FullName { get; set; }
    }
}