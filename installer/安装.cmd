@echo off
chcp 65001 >nul
title DeepSeek Harness 桌面版 安装程序
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
