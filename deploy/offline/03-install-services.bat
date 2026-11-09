@echo off
REM ============================================================
REM SmartDock offline install (05 12.2/12.3, acceptance #29) - one-shot
REM Usage (admin cmd): 03-install-services.bat [version, default 1.0.0]
REM NOTE: this file MUST stay pure ASCII - cmd mis-parses UTF-8 Chinese comments
REM ============================================================
setlocal
set VERSION=%1
if "%VERSION%"=="" set VERSION=1.0.0

set BASE_DIR=%~dp0
set RELEASE_DIR=%BASE_DIR%04-platform-releases\%VERSION%
set CLIENT_DIR=%BASE_DIR%05-client
set DB_DIR=%BASE_DIR%06-db
set DATA_DIR=D:\SmartDock
set STEP=init

echo [SmartDock offline install] version=%VERSION%
echo.

REM ---------- 0. artifact precheck (never half-install) ----------
if not exist "%RELEASE_DIR%\Platform.Host.exe" (
    echo [FATAL] %RELEASE_DIR%\Platform.Host.exe missing
    goto :fail
)
echo [OK] release artifacts found

REM ---------- 1. PostgreSQL (skip if present; silent install if bundled) ----------
set STEP=postgresql
if exist "%BASE_DIR%01-postgresql-17-installer.exe" (
    where psql >nul 2>nul
    if errorlevel 1 (
        echo [1/5] silent-installing PostgreSQL...
        "%BASE_DIR%01-postgresql-17-installer.exe" --mode unattended --unattendedmodeui none --superpassword Postgres123! --serverport 5432 --enable-components server,commandlinetools
        if errorlevel 1 goto :fail
    ) else (
        echo [1/5] PostgreSQL already present, skip installer
    )
) else (
    echo [1/5] no PG installer in bundle [SKIP] - env confirmed per 12.10.3 item 1
)

REM ---------- 2. data directories ----------
set STEP=datadir
echo [2/5] preparing data directories...
if not exist "%DATA_DIR%\files" mkdir "%DATA_DIR%\files"
if not exist "%DATA_DIR%\logs" mkdir "%DATA_DIR%\logs"
if not exist "%DATA_DIR%\backups" mkdir "%DATA_DIR%\backups"

REM ---------- 3. migrations V001..V010 (idempotent, filename order) ----------
set STEP=dbinit
echo [3/5] running migrations...
for %%F in ("%DB_DIR%\V*.sql") do (
    echo   - %%~nxF
    "%BASE_DIR%08-tools\DbInit\DbInit.exe" --sqlfile "%%F" >nul 2>nul
    if errorlevel 1 (
        echo [FATAL] migration failed: %%~nxF
        goto :fail
    )
)

REM ---------- 4. service registration (12.2: dedicated account / auto-restart / versioned dir) ----------
set STEP=service
echo [4/5] registering Windows service...
sc query Platform.Host >nul 2>nul
if not errorlevel 1 (
    echo   - service exists, stopping and reconfiguring
    sc stop Platform.Host >nul 2>nul
    timeout /t 3 /nobreak >nul
    sc config Platform.Host binPath= "%RELEASE_DIR%\Platform.Host.exe" start= auto >nul
) else (
    REM NT AUTHORITY\NetworkService = built-in low-priv service account (12.2 discipline 1)
    sc create Platform.Host binPath= "%RELEASE_DIR%\Platform.Host.exe" start= auto DisplayName= "SmartDock Platform" obj= "NT AUTHORITY\NetworkService"
    if errorlevel 1 (
        echo [FATAL] sc create failed - run from elevated cmd
        goto :fail
    )
    sc description Platform.Host "SmartDock Platform Host Service"
)
sc failure Platform.Host reset= 86400 actions= restart/5000/restart/5000/restart/30000
icacls "%DATA_DIR%\files" /grant "NETWORK SERVICE:(OI)(CI)M" /T >nul
icacls "%DATA_DIR%\logs" /grant "NETWORK SERVICE:(OI)(CI)M" /T >nul

REM ---------- 5. start and self-check (verify before declaring done) ----------
set STEP=start
echo [5/5] starting service and verifying...
sc start Platform.Host >nul
timeout /t 12 /nobreak >nul
curl -s -o nul http://localhost:5000/health/live
if errorlevel 1 (
    echo [WARN] health check not ready - check %DATA_DIR%\logs and event viewer
    echo [WARN] license.lic absent is expected: import license then re-check GET /api/license
) else (
    echo [OK] /health/live responding
)

echo.
if exist "%CLIENT_DIR%\Platform.Client.exe" (
    echo [INFO] client available at %CLIENT_DIR%\Platform.Client.exe
) else (
    echo [INFO] no client package in this bundle [SKIP]
)
echo [DONE] install finished. rollback = sc stop + sc config binPath to older release dir + sc start
exit /b 0

:fail
echo.
echo [FATAL] install failed at step: %STEP%
echo see docs/07 runbook P1 section and 99-docs clean-machine acceptance checklist
exit /b 1
