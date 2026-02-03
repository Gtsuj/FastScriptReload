@echo off
chcp 65001 >nul
:: CompileServer 发布脚本 - 发布单文件版本(包含 PDB)

echo.
echo ════════════════════════════════════════
echo   CompileServer 发布脚本
echo ════════════════════════════════════════
echo.
echo [1/2] 开始发布单文件版本...
echo.

cd /d "%~dp0CompileServer.Main"

:: 使用命令行参数指定所有发布配置,避免影响 IDE 调试
dotnet publish -c Release ^
    /p:PublishSingleFile=true ^
    /p:SelfContained=true ^
    /p:RuntimeIdentifier=win-x64 ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    /p:PublishReadyToRun=false ^
    /p:EnableCompressionInSingleFile=true ^
    /p:IncludeAllContentForSelfExtract=true ^
    /p:IncludePdbFiles=true

if %errorlevel% equ 0 (
    echo.
    echo ════════════════════════════════════════
    echo ✅ 发布成功！
    echo ════════════════════════════════════════
    echo.
    echo [2/2] 输出位置:
    echo   Assets\Plugins\CompileServer~\
    echo.
    echo 文件列表:
    for %%F in ("..\Assets\Plugins\CompileServer~\*.*") do (
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
