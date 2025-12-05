using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// 文件快照
    /// </summary>
    public class FileSnapshot
    {
        public SyntaxTree SyntaxTree { get; set; }
        public DateTime SnapshotTime { get; set; }
    }

    /// <summary>
    /// 差异结果
    /// </summary>
    public class DiffResult
    {
        /// <summary>
        /// 新增的方法
        /// </summary>
        public List<MethodDiffInfo> AddedMethods { get; } = new();

        /// <summary>
        /// 修改的方法
        /// </summary>
        public List<MethodDiffInfo> ModifiedMethods { get; } = new();

        /// <summary>
        /// 新增的字段
        /// </summary>
        public List<FieldDiffInfo> AddedFields { get; } = new();
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
        /// 方法名
        /// </summary>
        public string MethodName { get; set; }

        /// <summary>
        /// 声明类型全名
        /// </summary>
        public string DeclaringTypeFullName { get; set; }

        /// <summary>
        /// 是否为泛型方法
        /// </summary>
        public bool IsGenericMethod { get; set; }

        /// <summary>
        /// 是否有internal修饰符
        /// </summary>
        public bool HasInternalModifier { get; set; }

        /// <summary>
        /// 声明类型是否为internal类
        /// </summary>
        public bool IsDeclaringTypeInternal { get; set; }

        /// <summary>
        /// 方法定义节点
        /// </summary>
        public MethodDeclarationSyntax MethodDeclaration { get; set; }
    }

    /// <summary>
    /// 字段差异信息
    /// </summary>
    public class FieldDiffInfo
    {
        /// <summary>
        /// 字段全名
        /// </summary>
        public string FullName { get; set; }

        /// <summary>
        /// 字段名
        /// </summary>
        public string FieldName { get; set; }

        /// <summary>
        /// 声明类型全名
        /// </summary>
        public string DeclaringTypeFullName { get; set; }

        /// <summary>
        /// 是否有internal修饰符
        /// </summary>
        public bool HasInternalModifier { get; set; }

        /// <summary>
        /// 声明类型是否为internal类
        /// </summary>
        public bool IsDeclaringTypeInternal { get; set; }

        /// <summary>
        /// 字段定义节点
        /// </summary>
        public FieldDeclarationSyntax FieldDeclaration { get; set; }
    }

    /// <summary>
    /// 语法差异分析器 - 使用Roslyn进行源代码级别的差异分析
    /// 功能：
    /// 1. 比较原文件和改动文件中的类型差异（方法新增、方法修改、字段新增）
    /// 2. 分析差异部分是否有引用internal（通过语法分析）
    /// </summary>
    public static class DiffAnalyzerHelper
    {
        // 文件快照缓存：Key: 文件路径, Value: 文件内容快照
        private static readonly ConcurrentDictionary<string, FileSnapshot> _fileSnapshots = new();
        
        /// <summary>
        /// 批量保存文件快照
        /// </summary>
        public static void SaveFileSnapshots(IEnumerable<string> filePaths)
        {
            foreach (var filePath in filePaths)
            {
                SaveFileSnapshot(filePath);
            }
        }

        public static FileSnapshot GetFileSnapshots(string filePath)
        {
            if (!_fileSnapshots.ContainsKey(filePath))
            {
                SaveFileSnapshot(filePath);
            }

            return _fileSnapshots.GetValueOrDefault(filePath);
        }

        /// <summary>
        /// 分析文件差异
        /// </summary>
        /// <param name="filePath">变更后的文件路径</param>
        /// <param name="results"></param>
        /// <returns>差异结果字典，Key为类型全名，Value为该类型的差异结果。如果没有快照则返回null</returns>
        public static void AnalyzeDiff(string filePath, Dictionary<string, DiffResult> results)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

            var snapshot = GetFileSnapshots(filePath);
            if (snapshot == null)
            {
                LoggerScoped.LogDebug($"文件没有快照，跳过差异分析: {filePath}");
                return;
            }

            try
            {
                var newSyntaxTree = ReloadHelper.GetSyntaxTree(filePath);
                if (newSyntaxTree == null) return;

                var oldSyntaxTree = snapshot.SyntaxTree;

                // 比较类型差异（仅使用语法分析）
                CompareTypes(oldSyntaxTree, newSyntaxTree, results);

                return;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogWarning($"分析文件差异失败: {filePath}, {ex.Message}");
                return;
            }
        }

        /// <summary>
        /// 保存文件快照（在文件变更前调用）
        /// </summary>
        private static void SaveFileSnapshot(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            try
            {
                filePath = filePath.Replace("/", "\\");
                var syntaxTree = ReloadHelper.GetSyntaxTree(filePath);

                if (syntaxTree != null)
                {
                    _fileSnapshots[filePath] = new FileSnapshot
                    {
                        SyntaxTree = syntaxTree,
                        SnapshotTime = DateTime.UtcNow
                    };

                    LoggerScoped.LogDebug($"已保存文件快照: {filePath}");
                }
            }
            catch (Exception ex)
            {
                LoggerScoped.LogWarning($"保存文件快照失败: {filePath}, {ex.Message}");
            }
        }        
        
        /// <summary>
        /// 比较两个语法树中的类型差异
        /// </summary>
        private static void CompareTypes(
            SyntaxTree oldTree,
            SyntaxTree newTree,
            Dictionary<string, DiffResult> results)
        {
            var oldRoot = oldTree.GetRoot();
            var newRoot = newTree.GetRoot();

            var oldTypes = oldRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
            var newTypes = newRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();

            // 按类型全名匹配
            var oldTypeMap = oldTypes.ToDictionary(t => GetTypeFullName(t), t => t);
            var newTypeMap = newTypes.ToDictionary(t => GetTypeFullName(t), t => t);

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
                    CompareTypeMembers(oldType, newType, typeFullName, typeResult);
                }
                else
                {
                    // 新类型，所有成员都视为新增
                    AddAllMembersAsNew(newType, typeFullName, typeResult);
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
        private static void CompareTypeMembers(
            TypeDeclarationSyntax oldType,
            TypeDeclarationSyntax newType,
            string typeFullName,
            DiffResult result)
        {
            var isInternalClass = IsInternalType(newType);

            // 比较方法
            var oldMethods = oldType.Members.OfType<MethodDeclarationSyntax>().ToList();
            var newMethods = newType.Members.OfType<MethodDeclarationSyntax>().ToList();

            var oldMethodMap = oldMethods.ToDictionary(m => GetMethodSignature(m), m => m);
            var newMethodMap = newMethods.ToDictionary(m => GetMethodSignature(m), m => m);

            foreach (var kvp in newMethodMap)
            {
                var methodSignature = kvp.Key;
                var newMethod = kvp.Value;

                if (!oldMethodMap.TryGetValue(methodSignature, out var oldMethod))
                {
                    // 新增方法
                    var methodInfo = CreateMethodDiffInfo(newMethod, typeFullName, isInternalClass);
                    result.AddedMethods.Add(methodInfo);
                }
                else
                {
                    // 检查方法是否被修改（比较方法体）
                    if (IsMethodBodyChanged(oldMethod, newMethod))
                    {
                        var methodInfo = CreateMethodDiffInfo(newMethod, typeFullName, isInternalClass);
                        result.ModifiedMethods.Add(methodInfo);
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
                    var fieldInfo = CreateFieldDiffInfo(newFieldData.Field, newFieldData.Variable, typeFullName,
                        isInternalClass);
                    result.AddedFields.Add(fieldInfo);
                }
            }
        }

        /// <summary>
        /// 将类型的所有成员添加为新增
        /// </summary>
        private static void AddAllMembersAsNew(
            TypeDeclarationSyntax type,
            string typeFullName,
            DiffResult result)
        {
            var isInternalClass = IsInternalType(type);

            // 添加所有方法
            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
            {
                var methodInfo = CreateMethodDiffInfo(method, typeFullName, isInternalClass);
                result.AddedMethods.Add(methodInfo);
            }

            // 添加所有字段
            foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    var fieldInfo = CreateFieldDiffInfo(field, variable, typeFullName, isInternalClass);
                    result.AddedFields.Add(fieldInfo);
                }
            }
        }

        /// <summary>
        /// 创建方法差异信息
        /// </summary>
        private static MethodDiffInfo CreateMethodDiffInfo(
            MethodDeclarationSyntax method,
            string declaringTypeFullName,
            bool isDeclaringTypeInternal)
        {
            var methodName = method.Identifier.ValueText;
            var isGeneric = method.TypeParameterList != null && method.TypeParameterList.Parameters.Count > 0;
            var hasInternal = method.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));

            var returnType = method.ReturnType?.ToString() ?? "void";
            var parameters = method.ParameterList.Parameters.Count > 0
                ? string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""))
                : "";

            var fullName = $"{returnType} {declaringTypeFullName}::{methodName}({parameters})";

            return new MethodDiffInfo
            {
                FullName = fullName,
                MethodName = methodName,
                DeclaringTypeFullName = declaringTypeFullName,
                IsGenericMethod = isGeneric,
                HasInternalModifier = hasInternal,
                IsDeclaringTypeInternal = isDeclaringTypeInternal,
                MethodDeclaration = method
            };
        }

        /// <summary>
        /// 创建字段差异信息
        /// </summary>
        private static FieldDiffInfo CreateFieldDiffInfo(
            FieldDeclarationSyntax field,
            VariableDeclaratorSyntax variable,
            string declaringTypeFullName,
            bool isDeclaringTypeInternal)
        {
            var fieldName = variable.Identifier.ValueText;
            var hasInternal = field.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
            var fullName = $"{declaringTypeFullName}.{fieldName}";

            return new FieldDiffInfo
            {
                FullName = fullName,
                FieldName = fieldName,
                DeclaringTypeFullName = declaringTypeFullName,
                HasInternalModifier = hasInternal,
                IsDeclaringTypeInternal = isDeclaringTypeInternal,
                FieldDeclaration = field
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
        /// 检查字段是否改变
        /// </summary>
        private static bool IsFieldChanged(
            FieldDeclarationSyntax oldField,
            VariableDeclaratorSyntax oldVariable,
            FieldDeclarationSyntax newField,
            VariableDeclaratorSyntax newVariable)
        {
            // 比较字段类型
            var oldType = oldField.Declaration.Type?.ToString() ?? "";
            var newType = newField.Declaration.Type?.ToString() ?? "";
            if (oldType != newType)
                return true;

            // 比较字段修饰符（public, private, internal, static等）
            var oldModifiers = string.Join(" ", oldField.Modifiers.Select(m => m.ToString()).OrderBy(m => m));
            var newModifiers = string.Join(" ", newField.Modifiers.Select(m => m.ToString()).OrderBy(m => m));
            if (oldModifiers != newModifiers)
                return true;

            // 比较字段初始化值
            var oldInitializer = oldVariable.Initializer?.Value?.ToString() ?? "";
            var newInitializer = newVariable.Initializer?.Value?.ToString() ?? "";
            if (oldInitializer != newInitializer)
                return true;

            return false;
        }

        /// <summary>
        /// 获取方法签名（用于匹配）
        /// </summary>
        private static string GetMethodSignature(MethodDeclarationSyntax method)
        {
            var methodName = method.Identifier.ValueText;
            var parameters = string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? ""));
            return $"{methodName}({parameters})";
        }

        /// <summary>
        /// 获取类型全名（支持嵌套类型）
        /// </summary>
        private static string GetTypeFullName(TypeDeclarationSyntax typeDecl)
        {
            if (typeDecl == null)
                return "";

            var parts = new List<string>();
            var current = typeDecl;

            // 收集所有嵌套类型名
            while (current != null)
            {
                parts.Insert(0, current.Identifier.ValueText);
                current = current.Parent as TypeDeclarationSyntax;
            }

            // 获取命名空间
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
        /// 检查类型是否为internal
        /// </summary>
        private static bool IsInternalType(TypeDeclarationSyntax typeDecl)
        {
            return typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
        }

        /// <summary>
        /// 将泛型方法的调用者方法添加到差异结果的 ModifiedMethods 中
        /// </summary>
        /// <param name="callerMethodNames">调用者方法名集合，格式为 "ClassName::MethodName(parameters)"</param>
        /// <param name="result">差异结果</param>
        /// <param name="filePath">文件路径，用于查找方法声明</param>
        public static void AddCallerMethodsToModified(HashSet<string> callerMethodNames, DiffResult result,
            string filePath)
        {
            if (callerMethodNames == null || callerMethodNames.Count == 0 || result == null)
                return;

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            try
            {
                var syntaxTree = ReloadHelper.GetSyntaxTree(filePath);
                if (syntaxTree == null)
                    return;

                var root = syntaxTree.GetRoot();
                var typeDecls = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

                foreach (var callerMethodName in callerMethodNames)
                {
                    // 解析调用者方法名：格式 "ClassName::MethodName(parameters)"
                    if (!ParseCallerMethodName(callerMethodName, out var className, out var methodSignature))
                        continue;

                    // 查找对应的类型
                    foreach (var typeDecl in typeDecls)
                    {
                        var typeFullName = GetTypeFullName(typeDecl);
                        if (typeFullName != className)
                            continue;

                        // 查找对应的方法
                        var methods = typeDecl.Members.OfType<MethodDeclarationSyntax>();
                        foreach (var method in methods)
                        {
                            var methodSig = GetMethodSignature(method);
                            if (methodSig == methodSignature)
                            {
                                // 找到匹配的方法，创建 MethodDiffInfo 并添加到 ModifiedMethods
                                var isInternalClass = IsInternalType(typeDecl);
                                var methodInfo = CreateMethodDiffInfo(method, typeFullName, isInternalClass);

                                // 检查是否已存在（避免重复添加）
                                if (result.ModifiedMethods.All(m => m.FullName != methodInfo.FullName))
                                {
                                    result.ModifiedMethods.Add(methodInfo);
                                }

                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerScoped.LogWarning($"添加调用者方法到修改列表失败: {filePath}, {ex.Message}");
            }
        }

        /// <summary>
        /// 解析调用者方法名，格式：ClassName::MethodName(parameters)
        /// </summary>
        private static bool ParseCallerMethodName(
            string callerMethodName,
            out string className,
            out string methodSignature)
        {
            className = null;
            methodSignature = null;

            if (string.IsNullOrEmpty(callerMethodName))
                return false;

            var separatorIndex = callerMethodName.IndexOf("::", StringComparison.Ordinal);
            if (separatorIndex < 0)
                return false;

            className = callerMethodName.Substring(0, separatorIndex);
            methodSignature = callerMethodName.Substring(separatorIndex + 2);

            return !string.IsNullOrEmpty(className) && !string.IsNullOrEmpty(methodSignature);
        }
    }
}