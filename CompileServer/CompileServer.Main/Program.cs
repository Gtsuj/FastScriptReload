using CompileServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddNewtonsoftJson();

// 注册 CompileDiffService（单例）
builder.Services.AddSingleton<CompileDiffService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<CompileDiffService>>();
    return new CompileDiffService(logger);
});

// 注册 ILModifyService（单例）
builder.Services.AddSingleton<ILModifyService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<ILModifyService>>();
    return new ILModifyService(logger);
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
