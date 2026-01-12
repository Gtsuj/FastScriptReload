using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CompileServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddNewtonsoftJson();

// 注册 TypeInfoService（单例）
builder.Services.AddSingleton<TypeInfoService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<TypeInfoService>>();
    var projectPath = builder.Configuration["ProjectPath"] ?? string.Empty;
    return new TypeInfoService(logger, projectPath);
});

// 注册 CompileDiffService（单例）
builder.Services.AddSingleton<CompileDiffService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<CompileDiffService>>();
    var typeInfoService = sp.GetRequiredService<TypeInfoService>();
    return new CompileDiffService(logger, typeInfoService);
});

// 注册 ILModifyService（单例）
builder.Services.AddSingleton<ILModifyService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<ILModifyService>>();
    var typeInfoService = sp.GetRequiredService<TypeInfoService>();
    return new ILModifyService(logger, typeInfoService);
});

var app = builder.Build();

app.Logger.LogInformation("🚀 CompileServer starting...");

app.UseAuthorization();
app.MapControllers();

// 获取实际监听的URL（支持动态端口）
var urls = app.Urls;
var listenUrl = urls.Count > 0 ? urls.First() : "http://localhost:5000";
app.Logger.LogInformation($"📡 Listening on: {listenUrl}");
app.Logger.LogInformation($"🔍 Health check: {listenUrl}/api/health");
app.Logger.LogInformation($"🔧 Initialize: {listenUrl}/api/initialize");

app.Run();
