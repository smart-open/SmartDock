SmartDock 1.0.0 离线安装包（12.3 结构）
==========================================
01-postgresql-17-installer.exe   [采购项] 官方安装器，静默参数见 docs/05 12.2（本包不含，从官网取得）
03-install-services.bat          服务直装（sc create + failure 自愈 + 目录授权）
04-platform-releases/1.0.0/   自包含发布产物（免装 .NET Runtime）
05-client-setup.exe              [待 M3-1 客户端补齐后纳入] WPF 安装包
06-db/                           V001~V009 迁移（DbInit 按文件名序执行）
08-tools/                        backup.ps1 / restore.ps1（每日 02:30 计划任务）
99-docs/                         SBOM（CycloneDX 1.5）+ 许可清单

签名状态：见 99-docs\signature-status.txt（OV/EV 证书为采购项，未就绪时标注 UNSIGNED）
