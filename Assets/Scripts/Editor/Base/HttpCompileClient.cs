using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HookInfo.Models;
using ImmersiveVrToolsCommon.Runtime.Logging;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FastScriptReload.Editor
{
    /// <summary>
    /// HTTP 编译客户端，连接到独立的 CompileServer 进程
    /// </summary>
    public class HttpCompileClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private Process _localProcess;
        private string _baseUrl;
        private readonly bool _isLocalMode;
        private int _port;

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };

        public HttpCompileClient(string baseUrl = null)
        {
            // 如果没有指定URL，自动查找可用端口
            if (string.IsNullOrEmpty(baseUrl))
            {
                _port = FindAvailablePort(5000);
                _baseUrl = $"http://localhost:{_port}";
            }
            else
            {
                _baseUrl = baseUrl;
                // 从URL中提取端口
                var uri = new Uri(baseUrl);
                _port = uri.Port;
            }

            _isLocalMode = _baseUrl.Contains("localhost") || _baseUrl.Contains("127.0.0.1");

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_baseUrl),
                Timeout = TimeSpan.FromSeconds(60) // 增加超时时间以支持复杂编译
            };
        }

        /// <summary>
        /// 查找可用端口（从指定端口开始）
        /// </summary>
        private static int FindAvailablePort(int startPort)
        {
            for (int port = startPort; port < startPort + 100; port++)
            {
                if (!IsPortInUse(port))
                {
                    return port;
                }
            }
            // 如果100个端口都被占用，返回最后一个尝试的端口（可能会失败，但至少不会无限循环）
            return startPort + 100;
        }

        /// <summary>
        /// 检查端口是否被占用
        /// </summary>
        private static bool IsPortInUse(int port)
        {
            try
            {
                using (var tcpClient = new System.Net.Sockets.TcpClient())
                {
                    tcpClient.Connect(System.Net.IPAddress.Loopback, port);
                    return true; // 端口被占用
                }
            }
            catch
            {
                return false; // 端口可用
            }
        }

        /// <summary>
        /// 连接到编译服务(优先连接已有服务,失败后再启动新进程)
        /// </summary>
        public async Task<bool> ConnectAsync()
        {
            try
            {
                // 步骤 1: 优先尝试连接已经运行的服务
                LoggerScoped.Log($"🔍 尝试连接到编译服务: {_baseUrl}");

                if (IsServiceHealthy())
                {
                    LoggerScoped.Log($"✅ 编译服务已连接 (使用现有服务): {_baseUrl}");
                    return true;
                }

                // 步骤 2: 如果连接失败且是本地模式,尝试启动新进程
                if (_isLocalMode)
                {
                    LoggerScoped.LogDebug("⚠️ 未检测到运行中的服务，尝试启动本地编译服务...");

                    StartLocalService();

                    // 等待服务启动（最多 10 秒）
                    bool serviceStarted = false;
                    for (int i = 0; i < 20; i++)
                    {
                        await Task.Delay(500);
                        if (IsServiceHealthy())
                        {
                            serviceStarted = true;
                            LoggerScoped.LogDebug($"✅ 编译服务已连接 (新启动进程): {_baseUrl}");
                            break;
                        }
                    }

                    if (!serviceStarted)
                    {
                        LoggerScoped.LogError("❌ 编译服务启动超时，请检查:\n" +
                                      "  1. CompileServer.exe 是否存在\n" +
                                      "  2. 端口 5000 是否被占用\n" +
                                      "  3. 查看 CompileServer 输出日志");
                        return false;
                    }

                    return true;
                }

                // 步骤 3: 非本地模式且连接失败
                LoggerScoped.LogError($"❌ 编译服务连接失败: {_baseUrl}\n" +
                              "请确保服务已启动并且可访问");
                return false;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"❌ 连接编译服务失败: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 健康检查（快速超时，用于检测服务是否可用）
        /// </summary>
        private bool IsServiceHealthy()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); // 2秒超时
                var response = _httpClient.GetAsync("/api/health", cts.Token).GetAwaiter().GetResult();
                return response.IsSuccessStatusCode;
            }
            catch (TaskCanceledException)
            {
                // 超时，服务未响应
                return false;
            }
            catch (HttpRequestException)
            {
                // 连接失败
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 检查是否已初始化（同步接口）
        /// </summary>
        public bool CheckInitialized(string projectPath = null)
        {
            try
            {
                var url = "/api/check-initialized";
                if (!string.IsNullOrEmpty(projectPath))
                {
                    url += $"?projectPath={Uri.EscapeDataString(projectPath)}";
                }

                var response = _httpClient.GetAsync(url).GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var result = JsonConvert.DeserializeAnonymousType(json, new
                {
                    IsInitialized = false,
                    ProjectPath = (string)null
                });

                return result?.IsInitialized ?? false;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"❌ 检查初始化状态失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 初始化 TypeInfoService
        /// </summary>
        public async Task<bool> InitializeAsync(Dictionary<string, AssemblyContext> assemblyContexts, string[] preprocessorDefines)
        {
            try
            {
                var request = new InitializeRequest
                {
                    AssemblyContexts = assemblyContexts,
                    PreprocessorDefines = preprocessorDefines,
                    ProjectPath = Path.GetDirectoryName(Application.dataPath) ?? string.Empty
                };

                var json = JsonConvert.SerializeObject(request, JsonSettings);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("/api/initialize", content);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    LoggerScoped.LogError($"❌ 初始化请求失败: {response.StatusCode}\n{error}");
                    return false;
                }

                var responseJson = await response.Content.ReadAsStringAsync();

                // 尝试解析响应（可能包含或不包含 HookTypeInfos）
                var resultWithHooks = JsonConvert.DeserializeAnonymousType(responseJson, new
                {
                    Success = false,
                    Message = "",
                    ElapsedMilliseconds = 0L,
                    HookTypeInfos = (Dictionary<string, HookTypeInfo>)null
                });

                if (resultWithHooks != null && resultWithHooks.Success)
                {
                    // 如果返回了缓存的HookTypeInfos，应用它们（用于Unity Reload Domain后重建Hook）
                    // 注意：首次初始化时不会返回 HookTypeInfos
                    if (resultWithHooks.HookTypeInfos != null && resultWithHooks.HookTypeInfos.Count > 0)
                    {
                        LoggerScoped.LogDebug($"收到缓存的HookTypeInfos: {resultWithHooks.HookTypeInfos.Count} 个类型，开始重建Hook");
                        ReloadHelper.ApplyHooks(resultWithHooks.HookTypeInfos);
                        LoggerScoped.LogDebug($"Hook重建完成");
                    }

                    return true;
                }
                else
                {
                    LoggerScoped.LogError($"❌ TypeInfoService 初始化失败");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"❌ 初始化请求异常: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }        
        
        /// <summary>
        /// 获取缓存的 HookTypeInfos（同步接口，用于 RebuildHook）
        /// </summary>
        public Dictionary<string, HookTypeInfo> GetHookTypeInfos(string projectPath = null)
        {
            try
            {
                var url = "/api/hook-type-infos";
                if (!string.IsNullOrEmpty(projectPath))
                {
                    url += $"?projectPath={Uri.EscapeDataString(projectPath)}";
                }

                var response = _httpClient.GetAsync(url).GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    var error = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    LoggerScoped.LogError($"❌ 获取 HookTypeInfos 失败: {response.StatusCode}\n{error}");
                    return null;
                }

                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var result = JsonConvert.DeserializeAnonymousType(json, new
                {
                    Success = false,
                    HookTypeInfos = (Dictionary<string, HookTypeInfo>)null
                });

                if (result?.Success == true && result.HookTypeInfos != null)
                {
                    return result.HookTypeInfos;
                }

                return null;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"❌ 获取 HookTypeInfos 异常: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// 启动本地编译服务进程（仅在服务未运行时调用）
        /// </summary>
        private void StartLocalService()
        {
            // 检查是否已有进程在运行
            if (_localProcess != null && !_localProcess.HasExited)
            {
                LoggerScoped.LogWarning("⚠️ 本地编译服务进程已在运行");
                return;
            }

            // 查找 CompileServer.exe（Package路径）
            var path = Path.GetDirectoryName(typeof(HookMethodInfo).Assembly.Location);
            string exePath = Path.Combine(path, "..", "CompileServer~", "CompileServer.exe");
            if (File.Exists(exePath))
            {
                LoggerScoped.LogDebug($"📂 找到编译服务: {exePath}");
            }
            else
            {
                LoggerScoped.LogError($"❌ 编译服务可执行文件不存在:{exePath}");
                return;
            }

            // 启动进程
            try
            {
                _localProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = $"--urls http://localhost:{_port}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = Path.GetDirectoryName(exePath)
                    }
                };

                _localProcess.Exited += (sender, e) =>
                {
                    // 清理进程引用
                    if (_localProcess != null)
                    {
                        try
                        {
                            _localProcess.Dispose();
                        }
                        catch
                        {
                            // 忽略清理时的异常
                        }
                        _localProcess = null;
                    }

                    // 进程退出时自动关闭热重载开关
                    EditorApplication.delayCall += () =>
                    {
                        FastScriptReloadPreference.EnableAutoReloadForChangedFiles = false;
                    };
                };

                _localProcess.EnableRaisingEvents = true;
                _localProcess.Start();
                _localProcess.BeginOutputReadLine();
                _localProcess.BeginErrorReadLine();

                LoggerScoped.Log($"🚀 编译服务进程已启动: PID={_localProcess.Id}, Path={exePath}");
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"❌ 启动编译服务失败: {ex.Message}\n{ex.StackTrace}");
                _localProcess = null;
            }
        }

        /// <summary>
        /// 清除所有 Output 和 HookTypeInfo 缓存
        /// </summary>
        public async Task<bool> ClearAsync()
        {
            try
            {
                var response = await _httpClient.PostAsync("/api/clear", null);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    LoggerScoped.LogWarning($"⚠️ 清除缓存失败: {response.StatusCode}\n{error}");
                    return false;
                }

                LoggerScoped.LogDebug("✅ 已清除所有缓存");
                return true;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogWarning($"⚠️ 清除缓存异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 发送编译请求
        /// </summary>
        /// <param name="changedFiles">改动的文件列表</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task<CompileResponse> CompileAsync(List<string> changedFiles, CancellationToken cancellationToken = default)
        {
            try
            {
                // 构建请求（只需要传递文件列表，其他信息在初始化时已经获取）
                var request = new CompileRequest
                {
                    ChangedFiles = changedFiles.ToDictionary(
                        f => f,
                        f => File.GetLastWriteTimeUtc(f).ToString("O")
                    )
                };

                var json = JsonConvert.SerializeObject(request, JsonSettings);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("/api/compile", content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    LoggerScoped.LogError($"❌ 编译请求失败: {response.StatusCode}\n{error}");
                    return null;
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<CompileResponse>(responseJson, JsonSettings);

                if (result.Success)
                {
                    LoggerScoped.LogDebug($"✅ 编译成功: {result.HookTypeInfos.Count} 个类型, 耗时 {result.ElapsedMilliseconds}ms");
                }
                else
                {
                    LoggerScoped.LogError($"❌ 编译失败: {result.ErrorMessage}");
                }

                return result;
            }
            catch (Exception ex)
            {
                LoggerScoped.LogError($"❌ 编译请求异常: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// 停止本地进程
        /// </summary>
        public void StopLocalProcess()
        {
            if (_localProcess != null && !_localProcess.HasExited)
            {
                try
                {
                    LoggerScoped.Log($"🛑 停止编译服务进程: PID={_localProcess.Id}");
                    _localProcess.Kill();
                    _localProcess.WaitForExit(3000); // 等待最多3秒
                    _localProcess?.Dispose();
                    _localProcess = null;
                    LoggerScoped.Log("✅ 编译服务进程已停止");
                }
                catch (Exception ex)
                {
                    LoggerScoped.LogWarning($"⚠️ 停止编译服务进程失败: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
