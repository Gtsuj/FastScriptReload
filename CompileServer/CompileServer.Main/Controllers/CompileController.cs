using System.Collections.Generic;
using System.Linq;
using CompileServer.Helper;
using CompileServer.Models;
using HookInfo.Models;
using Microsoft.AspNetCore.Mvc;
using CompileServer.Services;
using Mono.Cecil;

namespace CompileServer.Controllers
{
    [ApiController]
    [Route("api")]
    public class CompileController : ControllerBase
    {
        private readonly ILogger<CompileController> _logger;
        private readonly CompileDiffService _compileDiffService;
        private readonly ILModifyService _ilModifyService;
        
        /// <summary>
        /// 缓存的初始化请求（用于判断是否需要重新初始化）
        /// </summary>
        private static InitializeRequest _cachedInitializeRequest;
        private static bool _isInitialized;

        public CompileController(
            ILogger<CompileController> logger,
            CompileDiffService compileDiffService,
            ILModifyService ilModifyService)
        {
            _logger = logger;
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
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                
                ReloadHelper.Initialize(request);

                await TypeInfoHelper.Initialize();
                
                stopwatch.Stop();
                
                // 缓存初始化请求
                _cachedInitializeRequest = request;
                _isInitialized = true;
                
                _logger.LogInformation($"TypeInfoHelper initialized successfully in {stopwatch.ElapsedMilliseconds}ms");
                
                return Ok(new
                {
                    Success = true,
                    Message = $"TypeInfoHelper initialized with {request.AssemblyContexts.Count} assemblies",
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize TypeInfoHelper");
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
        public async Task<CompileResponse> Compile([FromBody] CompileRequest request)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            try
            {
                // 根据文件路径自动确定程序集并分组
                var filesByAssembly = request.ChangedFiles.Keys
                    .Select(file => (file, TypeInfoHelper.GetFileToAssemblyName(file)))
                    .Where(x => !string.IsNullOrEmpty(x.Item2))
                    .GroupBy(x => x.Item2)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.Item1).ToList());

                if (filesByAssembly.Count == 0)
                {
                    return new CompileResponse
                    {
                        Success = false,
                        ErrorMessage = $"无法确定任何文件所属的程序集，请确保已调用 /api/initialize 初始化...",
                        ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                    };
                }

                var diffResults = _compileDiffService.CompileAndDiff(filesByAssembly);

                var allHookTypeInfos = _ilModifyService.ModifyCompileAssembly(diffResults);

                stopwatch.Stop();
                return await Task.FromResult(new CompileResponse
                {
                    Success = true,
                    HookTypeInfos = allHookTypeInfos,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                });
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, $"Compile failed: {ex.Message}");
                return await Task.FromResult(new CompileResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                });
            }
        }
    }
}
