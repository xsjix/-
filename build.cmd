@echo off
rem ============================================
rem  重新编译脚本 - 仅使用 Windows 自带工具
rem  需要: src\GenshinServerSwitcher.cs
rem        src\resources\cn_config.ini (官服配置模板)
rem        src\resources\app.ico (图标)
rem        ChannelSDK\bili_sdk.zip (B服组件包, 若缺失会自动从现有exe中提取)
rem ============================================
cd /d "%~dp0"

if not exist "src\GenshinServerSwitcher.cs" ( echo [错误] 缺少源码 & exit /b 1 )
if not exist "src\resources\cn_config.ini" ( echo [错误] 缺少官服配置模板 & exit /b 1 )
if not exist "src\resources\app.ico" set ICOARG=

rem B服组件包: 优先用本地 ChannelSDK, 没有则从现有 exe 提取
if not exist "ChannelSDK\bili_sdk.zip" (
    if exist "原神转服器.exe" (
        echo 正在从现有 exe 中提取内置B服组件包...
        mkdir ChannelSDK 2>nul
        powershell -NoProfile -ExecutionPolicy Bypass -Command "$a=[Reflection.Assembly]::LoadFile((Resolve-Path '.\原神转服器.exe').Path); $s=$a.GetManifestResourceStream('bili_sdk.zip'); $f=[IO.File]::Create((Join-Path (Resolve-Path '.').Path 'ChannelSDK\bili_sdk.zip')); $s.CopyTo($f); $f.Close(); Write-Host extracted"
        if not exist "ChannelSDK\bili_sdk.zip" ( echo [错误] 提取失败 & exit /b 1 )
    ) else (
        echo [错误] 缺少 ChannelSDK\bili_sdk.zip 且没有现有exe可提取 & exit /b 1
    )
)

C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ ^
 /out:原神转服器.exe ^
 /win32icon:src\resources\app.ico ^
 /resource:ChannelSDK\bili_sdk.zip,bili_sdk.zip ^
 /resource:src\resources\cn_config.ini,cn_config.ini ^
 /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
 /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
 src\GenshinServerSwitcher.cs
if errorlevel 1 exit /b 1
echo.
echo 编译完成: 原神转服器.exe
