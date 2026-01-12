using System.Collections.Generic;
using System.Linq;
using HookInfo.Models;
using Microsoft.AspNetCore.Mvc;
using CompileServer.Services;

namespace CompileServer.Controllers
{
    [ApiController]
    [Route("api")]
    public class CompileController : ControllerBase
    {
        private readonly ILogger<CompileController> _logger;
        private readonly TypeInfoService _typeInfoService;
        private readonly CompileDiffService _compileDiffService;
        private readonly ILModifyService _ilModifyService;
        
        /// <summary>
        /// 缓存的初始化请求（用于判断是否需要重新初始化）
        /// </summary>
        private static InitializeRequest _cachedInitializeRequest;
        private static bool _isInitialized;

        public CompileController(
            ILogger<CompileController> logger, 
            TypeInfoService typeInfoService,
            CompileDiffService compileDiffService,
            ILModifyService ilModifyService)
        {
            _logger = logger;
            _typeInfoService = typeInfoService;
            _compileDiffService = compileDiffService;
            _ilModifyService = ilModifyService;
        }

        [HttpGet("health")]
        public IActionResult Health()
        {
            _logger.LogInformation("Health check requested.");
            return Ok("CompileServer is healthy!");
        }

        /// <summary>
        /// 检查是否已初始化（同步接口）
        /// </summary>
        [HttpGet("check-initialized")]
        public IActionResult CheckInitialized([FromQuery] string projectPath = null)
        {
            var isInitialized = _isInitialized && _cachedInitializeRequest != null;
            
            // 如果提供了项目路径，检查是否匹配
            if (isInitialized && !string.IsNullOrEmpty(projectPath))
            {
                isInitialized = projectPath.Equals(_cachedInitializeRequest.ProjectPath);
            }

            return Ok(new
            {
                IsInitialized = isInitialized,
                ProjectPath = _cachedInitializeRequest?.ProjectPath
            });
        }

        /// <summary>
        /// 获取缓存的 HookTypeInfos（同步接口，用于 RebuildHook）
        /// </summary>
        [HttpGet("hook-type-infos")]
        public IActionResult GetHookTypeInfos([FromQuery] string projectPath = null)
        {
            // 检查是否已初始化
            if (!_isInitialized || _cachedInitializeRequest == null)
            {
                return BadRequest(new
                {
                    Success = false,
                    ErrorMessage = "CompileServer 尚未初始化"
                });
            }

            // 如果提供了项目路径，检查是否匹配
            if (!string.IsNullOrEmpty(projectPath) && !projectPath.Equals(_cachedInitializeRequest.ProjectPath))
            {
                return BadRequest(new
                {
                    Success = false,
                    ErrorMessage = $"项目路径不匹配。当前: {_cachedInitializeRequest.ProjectPath}, 请求: {projectPath}"
                });
            }

            // 返回缓存的 HookTypeInfos
            return Ok(new
            {
                Success = true,
                HookTypeInfos = ReloadHelper.HookTypeInfoCache
            });
        }

        [HttpPost("initialize")]
        public async Task<IActionResult> Initialize([FromBody] InitializeRequest request)
        {
            _logger.LogInformation($"Initialize request received with {request.AssemblyContexts.Count} assemblies");
            
            try
            {
                // 设置项目路径到 ReloadHelper（用于路径管理）
                if (!string.IsNullOrEmpty(request.ProjectPath))
                {
                    ReloadHelper.SetProjectPath(request.ProjectPath);
                    _logger.LogInformation($"Project path set to: {request.ProjectPath}");
                }

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                
                await _typeInfoService.Initialize(
                    request.AssemblyContexts,
                    request.PreprocessorDefines
                );
                
                stopwatch.Stop();
                
                // 缓存初始化请求
                _cachedInitializeRequest = request;
                _isInitialized = true;
                
                _logger.LogInformation($"TypeInfoService initialized successfully in {stopwatch.ElapsedMilliseconds}ms");
                
                return Ok(new
                {
                    Success = true,
                    Message = $"TypeInfoService initialized with {request.AssemblyContexts.Count} assemblies",
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize TypeInfoService");
                return BadRequest(new
                {
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// 清除所有 Output 和 HookTypeInfo 缓存（在重新编译前调用）
        /// </summary>
        [HttpPost("clear")]
        public IActionResult Clear()
        {
            try
            {
                ReloadHelper.ClearHookInfo();
                
                _logger.LogInformation("已清除所有 Output 和 HookTypeInfo 缓存");
                
                return Ok(new
                {
                    Success = true,
                    Message = "已清除所有缓存"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"清除缓存失败: {ex.Message}");
                return BadRequest(new
                {
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        [HttpPost("compile")]
        public Task<CompileResponse> Compile([FromBody] CompileRequest request)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            try
            {
                // 1. 提取改动的文件路径列表
                var changedFiles = new List<string>(request.ChangedFiles.Keys);

                if (changedFiles.Count == 0)
                {
                    return Task.FromResult(new CompileResponse
                    {
                        Success = false,
                        ErrorMessage = "未提供改动的文件列表",
                        ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                        IsFromCache = false
                    });
                }

                // 2. 根据文件路径自动确定程序集（从初始化缓存中查找）
                // 根据第一个文件确定程序集（同一批文件应该属于同一程序集）
                string assemblyName = _typeInfoService.GetAssemblyName(changedFiles[0]);
                if (string.IsNullOrEmpty(assemblyName))
                {
                    _logger.LogWarning($"无法确定文件 '{changedFiles[0]}' 所属的程序集");
                    return Task.FromResult(new CompileResponse
                    {
                        Success = false,
                        ErrorMessage = $"无法确定文件所属的程序集，请确保已调用 /api/initialize 初始化",
                        ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                        IsFromCache = false
                    });
                }
                
                // 验证所有文件是否属于同一程序集
                foreach (var file in changedFiles.Skip(1))
                {
                    var fileAssemblyName = _typeInfoService.GetAssemblyName(file);
                    if (!string.IsNullOrEmpty(fileAssemblyName) && fileAssemblyName != assemblyName)
                    {
                        _logger.LogWarning($"文件 '{file}' 属于程序集 '{fileAssemblyName}'，与第一个文件 '{changedFiles[0]}' 的程序集 '{assemblyName}' 不一致");
                    }
                }

                _logger.LogInformation($"Compile request received for assembly: {assemblyName}, {changedFiles.Count} changed files");

                // 3. 调用 CompileAndDiff 进行编译和差异分析
                var diffResults = _compileDiffService.CompileAndDiff(assemblyName, changedFiles);
                
                if (diffResults == null || diffResults.Count == 0)
                {
                    _logger.LogWarning($"No differences found for assembly: {assemblyName}");
                    return Task.FromResult(new CompileResponse
                    {
                        Success = true,
                        HookTypeInfos = new Dictionary<string, HookTypeInfo>(),
                        ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                        IsFromCache = false
                    });
                }

                stopwatch.Stop();
                _logger.LogInformation($"Compile and diff completed in {stopwatch.ElapsedMilliseconds}ms, found {diffResults.Count} changed types");

                // 4. 调用 IL 修改服务生成 Wrapper 程序集
                string wrapperPath;
                try
                {
                    wrapperPath = _ilModifyService.ModifyCompileAssembly(assemblyName, diffResults);
                    _logger.LogInformation($"Wrapper assembly generated: {wrapperPath}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to generate wrapper assembly");
                    return Task.FromResult(new CompileResponse
                    {
                        Success = false,
                        ErrorMessage = $"IL modification failed: {ex.Message}",
                        ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                        IsFromCache = false
                    });
                }

                // 4. 从 DiffResult 中提取本次编译涉及的具体方法和字段信息
                var hookTypeInfos = new Dictionary<string, HookTypeInfo>();
                foreach (var (typeFullName, diffResult) in diffResults)
                {
                    // 从全局缓存中获取完整的 HookTypeInfo（ILModifyService 已更新）
                    if (!ReloadHelper.HookTypeInfoCache.TryGetValue(typeFullName, out var cachedHookTypeInfo))
                    {
                        continue;
                    }

                    // 创建新的 HookTypeInfo，只包含本次编译改动的方法和字段
                    var hookTypeInfo = new HookTypeInfo
                    {
                        TypeFullName = cachedHookTypeInfo.TypeFullName,
                        OriginalAssemblyName = cachedHookTypeInfo.OriginalAssemblyName
                    };

                    // 从 DiffResult 中提取本次改动的方法
                    foreach (var (methodFullName, methodDiffInfo) in diffResult.ModifiedMethods)
                    {
                        if (cachedHookTypeInfo.ModifiedMethods.TryGetValue(methodFullName, out var hookMethodInfo))
                        {
                            hookTypeInfo.ModifiedMethods[methodFullName] = hookMethodInfo;
                        }
                    }

                    // 从 DiffResult 中提取本次新增的字段
                    foreach (var (fieldFullName, _) in diffResult.AddedFields)
                    {
                        if (cachedHookTypeInfo.AddedFields.TryGetValue(fieldFullName, out var hookFieldInfo))
                        {
                            hookTypeInfo.AddedFields.TryAdd(fieldFullName, hookFieldInfo);
                        }
                    }

                    // 只有当有实际改动时才添加到结果中
                    if (hookTypeInfo.ModifiedMethods.Count > 0 || hookTypeInfo.AddedFields.Count > 0)
                    {
                        hookTypeInfos[typeFullName] = hookTypeInfo;
                    }
                }

                return Task.FromResult(new CompileResponse
                {
                    Success = true,
                    HookTypeInfos = hookTypeInfos,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                    IsFromCache = false
                });
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, $"Compile failed: {ex.Message}");
                return Task.FromResult(new CompileResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                    IsFromCache = false
                });
            }
        }
    }
}
