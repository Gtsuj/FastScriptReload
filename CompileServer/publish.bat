@echo off
chcp 65001 >nul
:: CompileServer 发布脚本 - 自包含单文件版本（包含 .NET 8 运行时）

echo.
echo ════════════════════════════════════════
echo   CompileServer 发布脚本
echo   自包含模式（无需安装 .NET）
echo ════════════════════════════════════════
echo.
echo [1/2] 开始发布...
echo.

cd /d "%~dp0CompileServer.Main"
dotnet publish -c Release /p:IncludePdbFiles=true

if %errorlevel% equ 0 (
    echo.
    echo ════════════════════════════════════════
    echo ✅ 发布成功！（自包含单文件）
    echo ════════════════════════════════════════
    echo.
    echo [2/2] 输出位置:
    echo   Assets\Plugins\CompileServer~\
    echo.
    echo 📦 CompileServer~ 文件列表:
    for %%F in ("..\..\Assets\Plugins\CompileServer~\*.*") do (
        echo   %%~nxF ^(%%~zF bytes^)
    )
    echo.
    echo 📦 Unity Plugins 文件列表:
    echo   Assets\Plugins\CompileServer\
    for %%F in ("..\..\Assets\Plugins\CompileServer\HookInfo*.dll") do (
        echo   %%~nxF
    )
    echo.
) else (
    echo.
    echo ════════════════════════════════════════
    echo ❌ 发布失败！
    echo ════════════════════════════════════════
    echo.
    pause
    exit /b 1
)

echo 按任意键退出...
pause >nul
