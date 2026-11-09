@echo off
REM 12.2 单机部署：Platform.Host Windows 服务直装（无 Docker）。
REM 三条部署纪律：专用低权限账号 / sc failure 自愈重启 / 版本化发布目录（一分钟回滚）。
REM 用法（管理员）：03-install-services.bat [版本号，默认 1.0.0]

setlocal
set VERSION=%1
if "%VERSION%"=="" set VERSION=1.0.0

set BASE_DIR=%~dp0
set RELEASE_DIR=%BASE_DIR%04-platform-releases\%VERSION%
set DATA_DIR=D:\SmartDock

if not exist "%RELEASE_DIR%\Platform.Host.exe" (
    echo [ERROR] Platform.Host.exe not found in %RELEASE_DIR%
    exit /b 1
)

REM ① 目录结构：发布目录带版本号（回滚 = 换 binPath 指向的目录，12.2 纪律 ③）
if not exist "%DATA_DIR%\files" mkdir "%DATA_DIR%\files"
if not exist "%DATA_DIR%\logs" mkdir "%DATA_DIR%\logs"

REM ② 服务注册：专用低权限服务账号（12.2 纪律 ①，EDR 白名单按此账号配）
sc create Platform.Host binPath= "%RELEASE_DIR%\Platform.Host.exe" start= auto ^
    DisplayName= "SmartDock Platform" obj= NT SERVICE\NetworkService
sc description Platform.Host "SmartDock 上位机平台主服务"

REM ③ 自愈：5s/5s/30s 三段重启（12.2 纪律 ②，等价 Compose restart 策略）
sc failure Platform.Host reset= 86400 actions= restart/5000/restart/5000/restart/30000

REM ④ 文件目录授权（内嵌文件端点落盘位置，2.4/E8 默认档）
icacls "%DATA_DIR%\files" /grant "NETWORK SERVICE:(OI)(CI)M" /T
icacls "%DATA_DIR%\logs" /grant "NETWORK SERVICE:(OI)(CI)M" /T

REM ⑤ 数据库迁移：由 Platform.Host 首启前的 DbInit 完成（离线包内含 06-db）
echo [INFO] Before first start, run: DbInit --sqlfile 06-db\V001__m0_point_history.sql ... (V001~V009)
echo [INFO] Then: sc start Platform.Host
echo [DONE] Service registered. Rollback = sc stop + change binPath to older release dir + sc start.
